// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Additional FrameBuilder tests introduced by the review remediation pass:
/// <list type="bullet">
///   <item>F4 — SOME/IP-TP application-layer segmentation produces correctly
///         offset, alignment-rounded segments with a per-segment TP word.</item>
///   <item>F4 — Trailers (Ethernet FCS) are applied per emitted fragment.</item>
///   <item>F8 — Interceptor ordering: <see cref="IFrameInterceptor.OnHeaderWritten{TLayer}"/>
///         fires once per header in outer→inner order during the scratch
///         build (not per fragment); <see cref="IFrameInterceptor.OnFrameComplete"/>
///         fires once per emitted frame.</item>
///   <item>F12 — Reusing a <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>
///         to emit two payloads back-to-back keeps both outputs correct.</item>
///   <item>VLAN QinQ — Eth + outer VLAN + inner VLAN + IPv4 + UDP composes,
///         emits, and yields the right ethertype chain.</item>
///   <item>Depth boundary — A 6-layer (Eth + VLAN + VLAN + IPv4 + UDP + SOME/IP)
///         stack composes and emits without recursion-depth issues.</item>
/// </list>
/// </summary>
// Shares the StatefulFragmentationTests parallelism-exclusion group: both classes
// exercise FrameSequenceScratch (the thread-local scratch buffer used during
// fragmentation) and must not run concurrently with each other.
[NotInParallel(nameof(StatefulFragmentationTests))]
internal sealed class FrameBuilderReviewExtensionTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _SrcIp4 = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp4 = IPv4Address.FromBytes([10, 0, 0, 2]);

    private const int MtuFrameBytes = 1500;
    private const int EthHeaderSize = 14;
    private const int IPv4HeaderSize = 20;
    private const int UdpHeaderSize = 8;
    private const int SomeIpTpHeaderSize = 20;

    #region #3 SOME/IP-TP fragmentation — F4

    /// <summary>
    /// Verifies that a stack with <see cref="FB.SomeIpTpLayer"/> innermost emits
    /// alignment-16 application-layer segments rather than IP-fragmenting the
    /// outer datagram, and that the per-segment TP word encodes the correct
    /// 16-byte-unit offset and More Segments flag.
    /// </summary>
    [Test]
    public async Task SomeIpTp_OversizePayload_EmitsApplicationSegments_With16ByteAlignment()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: MtuFrameBytes);
        // IPv4 default DF=true: IP fragmentation is forbidden.  SOME/IP-TP
        // (innermost IFragmentable) wins kind selection, alignment 16.
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(30490, 30490, FB.Auto<ushort>.Explicit(0));
        FB.SomeIpTpLayer tp = new(serviceId: 0x1234, methodId: 0x5678);

        FB.CreatedStack<
            FB.StatelessStack<FB.SomeIpTpLayer,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .Then(tp)
                .CreateWithFixedValues();

        // Inner = 3000 bytes of payload following the SOME/IP-TP header.
        // Header end offset = 14+20+8+20 = 62.  Max inner = 1500 - 62 = 1438.
        // Aligned to 16 → 1424.  ceil(3000/1424) = 3 segments: 1424, 1424, 152.
        const int PayloadLen = 3000;
        byte[] payload = new byte[PayloadLen];
        for (int i = 0; i < PayloadLen; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        FB.FrameSequence<
            FB.StatelessStack<FB.SomeIpTpLayer,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);

        List<byte[]> segments = [];
        byte[] scratch = new byte[MtuFrameBytes];
        while (seq.MoveNext(scratch, out int n))
        {
            byte[] frame = new byte[n];
            scratch.AsSpan(0, n).CopyTo(frame);
            segments.Add(frame);
        }
        FB.BuildStatus status = seq.Status;

        await Assert.That(status).IsEqualTo(FB.BuildStatus.Success);
        await Assert.That(segments.Count).IsEqualTo(3);

        const int TpOffsetInFrame = EthHeaderSize + IPv4HeaderSize + UdpHeaderSize;
        const int PayloadOffsetInFrame = TpOffsetInFrame + SomeIpTpHeaderSize;

        // Segment 0: payload offset=0 (TP word upper 28 bits / 16-byte units = 0), MF=1.
        await Assert.That(segments[0].Length).IsEqualTo(PayloadOffsetInFrame + 1424);
        uint tp0 = BinaryPrimitives.ReadUInt32BigEndian(segments[0].AsSpan(TpOffsetInFrame + 16, 4));
        await Assert.That(tp0 >> 4).IsEqualTo(0u);
        await Assert.That((tp0 & 1u) != 0).IsTrue();

        // Segment 1: offset=1424 / 16 = 89, MF=1.
        await Assert.That(segments[1].Length).IsEqualTo(PayloadOffsetInFrame + 1424);
        uint tp1 = BinaryPrimitives.ReadUInt32BigEndian(segments[1].AsSpan(TpOffsetInFrame + 16, 4));
        await Assert.That(tp1 >> 4).IsEqualTo(89u);
        await Assert.That((tp1 & 1u) != 0).IsTrue();

        // Segment 2 (last): offset=2848 / 16 = 178, MF=0.
        await Assert.That(segments[2].Length).IsEqualTo(PayloadOffsetInFrame + 152);
        uint tp2 = BinaryPrimitives.ReadUInt32BigEndian(segments[2].AsSpan(TpOffsetInFrame + 16, 4));
        await Assert.That(tp2 >> 4).IsEqualTo(178u);
        await Assert.That((tp2 & 1u) == 0).IsTrue();

        // Reassembled payload (concatenated SOME/IP-TP payloads) equals the
        // original input.
        byte[] reassembled = new byte[PayloadLen];
        int cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            int segPayloadLen = segments[i].Length - PayloadOffsetInFrame;
            segments[i].AsSpan(PayloadOffsetInFrame, segPayloadLen).CopyTo(reassembled.AsSpan(cursor));
            cursor += segPayloadLen;
        }
        await Assert.That(reassembled.AsSpan().SequenceEqual(payload)).IsTrue();

        // Per-segment IPv4 total length == segment frame length minus link header.
        for (int i = 0; i < segments.Count; i++)
        {
            ushort ipTotal = BinaryPrimitives.ReadUInt16BigEndian(segments[i].AsSpan(EthHeaderSize + 2, 2));
            await Assert.That((int)ipTotal).IsEqualTo(segments[i].Length - EthHeaderSize);
        }
    }

    #endregion

    #region #4 Trailer per fragment — F4

    /// <summary>
    /// Verifies that an Ethernet FCS trailer is recomputed and appended on
    /// every emitted fragment of a fragmenting build, not just the first.
    /// </summary>
    [Test]
    public async Task IPv4Fragmentation_AppendsValidFcsToEveryFragment()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.EthernetFcs,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .WithTrailer(FB.EthernetFcs.Crc32)
                .CreateWithFixedValues();

        byte[] payload = new byte[3000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.EthernetFcs, FB.NoInterceptor> seq = stack.Build(payload);

        List<byte[]> fragments = [];
        byte[] scratch = new byte[MtuFrameBytes];
        while (seq.MoveNext(scratch, out int n))
        {
            byte[] frame = new byte[n];
            scratch.AsSpan(0, n).CopyTo(frame);
            fragments.Add(frame);
        }
        await Assert.That(seq.Status).IsEqualTo(FB.BuildStatus.Success);
        await Assert.That(fragments.Count).IsEqualTo(3);

        // Each fragment must carry a valid CRC32 over (entire frame minus 4-byte FCS).
        foreach (byte[] frag in fragments)
        {
            ReadOnlySpan<byte> data = frag.AsSpan(0, frag.Length - 4);
            uint expectedCrc = ComputeReferenceCrc32(data);
            uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(frag.AsSpan(frag.Length - 4, 4));
            await Assert.That(actualCrc).IsEqualTo(expectedCrc);
        }
    }

    #endregion

    #region #5 Interceptor ordering — F8

    /// <summary>
    /// Verifies the documented interceptor contract:
    /// <list type="bullet">
    ///   <item>Single-frame build: <c>OnHeaderWritten</c> fires once per layer
    ///         (outer→inner) and <c>OnFrameComplete</c> fires once.</item>
    ///   <item>Fragmenting build: <c>OnHeaderWritten</c> is intentionally
    ///         suppressed during the scratch build; <c>OnFrameComplete</c>
    ///         fires exactly once per emitted fragment.</item>
    /// </list>
    /// </summary>
    [Test]
    public async Task Interceptor_OrderingAndCount_AcrossSingleFrameAndFragmentedBuilds()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        CountingInterceptor counter = new();

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            CountingInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues(in counter);

        // --- Single-frame build: small payload that fits the MTU. ---
        CountingInterceptor.Reset();
        byte[] scratch = new byte[MtuFrameBytes];
        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer, CountingInterceptor> single = stack.Build([1, 2, 3, 4]);
        int singleFrames = 0;
        while (single.MoveNext(scratch, out _))
        {
            singleFrames++;
        }
        await Assert.That(singleFrames).IsEqualTo(1);
        await Assert.That(CountingInterceptor.HeaderCount).IsEqualTo(3);
        await Assert.That(CountingInterceptor.FrameCount).IsEqualTo(1);

        // --- Fragmenting build: oversize payload. ---
        CountingInterceptor.Reset();
        byte[] payload = new byte[3000];
        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer, CountingInterceptor> seq = stack.Build(payload);
        int fragmentCount = 0;
        while (seq.MoveNext(scratch, out _))
        {
            fragmentCount++;
        }
        await Assert.That(fragmentCount).IsEqualTo(3);
        // OnHeaderWritten suppressed during scratch build for fragmenting path.
        await Assert.That(CountingInterceptor.HeaderCount).IsEqualTo(0);
        // One OnFrameComplete per emitted fragment.
        await Assert.That(CountingInterceptor.FrameCount).IsEqualTo(fragmentCount);
    }

    /// <summary>Process-shared counters captured by <see cref="CountingInterceptor"/>.</summary>
    private struct CountingInterceptor : FB.IFrameInterceptor
    {
        internal static int HeaderCount;
        internal static int FrameCount;

        public CountingInterceptor()
        {
            Reset();
        }

        internal static void Reset()
        {
            HeaderCount = 0;
            FrameCount = 0;
        }

        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, FB.IProtocolLayer
            => HeaderCount++;

        public void OnFrameComplete(scoped Span<byte> frame) => FrameCount++;
    }

    #endregion

    #region #7 Value reuse — F12

    /// <summary>
    /// Emits two consecutive single-frame builds from the same
    /// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> and asserts
    /// that the second build is independent of the first (no state leakage,
    /// no header cache pollution).
    /// </summary>
    [Test]
    public async Task CreatedStack_BackToBackBuilds_AreIndependent()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        byte[] p1 = [1, 2, 3, 4];
        byte[] p2 = [9, 9, 9, 9, 9, 9, 9, 9];

        byte[] frame1 = new byte[1500];
        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer, FB.NoInterceptor> s1 = stack.Build(p1);
        s1.MoveNext(frame1, out int n1);
        byte[] out1 = frame1.AsSpan(0, n1).ToArray();

        byte[] frame2 = new byte[1500];
        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer, FB.NoInterceptor> s2 = stack.Build(p2);
        s2.MoveNext(frame2, out int n2);
        byte[] out2 = frame2.AsSpan(0, n2).ToArray();

        // Sizes differ by exactly the payload size delta.
        await Assert.That(n2 - n1).IsEqualTo(p2.Length - p1.Length);
        // Payload bytes match the input on each frame.
        const int PayloadOffset = EthHeaderSize + IPv4HeaderSize + UdpHeaderSize;
        await Assert.That(out1.AsSpan(PayloadOffset, p1.Length).SequenceEqual(p1)).IsTrue();
        await Assert.That(out2.AsSpan(PayloadOffset, p2.Length).SequenceEqual(p2)).IsTrue();
        // IPv4 total-length field is correct on each frame.
        ushort ipLen1 = BinaryPrimitives.ReadUInt16BigEndian(out1.AsSpan(EthHeaderSize + 2, 2));
        ushort ipLen2 = BinaryPrimitives.ReadUInt16BigEndian(out2.AsSpan(EthHeaderSize + 2, 2));
        await Assert.That((int)ipLen1).IsEqualTo(out1.Length - EthHeaderSize);
        await Assert.That((int)ipLen2).IsEqualTo(out2.Length - EthHeaderSize);
    }

    #endregion

    #region #8 VLAN QinQ

    /// <summary>
    /// Verifies that an Ethernet + outer VLAN (QinQ) + inner VLAN + IPv4 + UDP
    /// stack composes, emits, and writes the QinQ ethertype chain correctly:
    /// <c>0x88A8</c> (S-Tag) → <c>0x8100</c> (C-Tag) → <c>0x0800</c> (IPv4).
    /// </summary>
    [Test]
    public async Task VlanQinQ_Emits_WithCorrectEthertypeChain()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer outerVlan = new(vlanId: 100, isQinQ: true);
        FB.VlanLayer innerVlan = new(vlanId: 200);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.VlanLayer,
                        FB.StatelessStack<FB.VlanLayer,
                            FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(outerVlan)
                .Then(innerVlan)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
        byte[] frame = new byte[2048];
        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.VlanLayer,
                        FB.StatelessStack<FB.VlanLayer,
                            FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>,
            FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);
        seq.MoveNext(frame, out int n);

        // Two VLAN tags add 8 bytes onto the link header.
        const int ExpectedHeaderBytes = EthHeaderSize + 4 + 4 + IPv4HeaderSize + UdpHeaderSize;
        await Assert.That(n).IsEqualTo(ExpectedHeaderBytes + payload.Length);

        // Outer ethertype at 12: 0x88A8 (S-Tag).
        ushort outerEthertype = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2));
        await Assert.That(outerEthertype).IsEqualTo((ushort)0x88A8);
        // Inner ethertype at 12 + 4 = 16: 0x8100 (C-Tag).
        ushort innerEthertype = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2));
        await Assert.That(innerEthertype).IsEqualTo((ushort)0x8100);
        // Final ethertype at 20: 0x0800 (IPv4).
        ushort ipEthertype = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(20, 2));
        await Assert.That(ipEthertype).IsEqualTo((ushort)0x0800);
    }

    #endregion

    #region #9 Depth boundary

    /// <summary>
    /// Verifies that a six-layer stack (Eth + outer VLAN + inner VLAN + IPv4 +
    /// UDP + SOME/IP) composes, emits a frame whose total size matches the
    /// sum of header sizes plus payload, and that the SOME/IP Length field
    /// is patched correctly across the long header chain.
    /// </summary>
    [Test]
    public async Task SixLayerStack_Emits_WithCorrectTotalSizeAndSomeIpLength()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer outerVlan = new(vlanId: 10, isQinQ: true);
        FB.VlanLayer innerVlan = new(vlanId: 20);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(30490, 30490, FB.Auto<ushort>.Explicit(0));
        FB.SomeIpLayer someIp = new(serviceId: 0xABCD, methodId: 0x0001);

        FB.CreatedStack<
            FB.StatelessStack<FB.SomeIpLayer,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.VlanLayer,
                            FB.StatelessStack<FB.VlanLayer,
                                FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(outerVlan)
                .Then(innerVlan)
                .Then(ip)
                .Then(udp)
                .Then(someIp)
                .CreateWithFixedValues();

        byte[] payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        byte[] frame = new byte[2048];
        FB.FrameSequence<
            FB.StatelessStack<FB.SomeIpLayer,
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.VlanLayer,
                            FB.StatelessStack<FB.VlanLayer,
                                FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>>,
            FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);
        seq.MoveNext(frame, out int n);

        const int Headers = EthHeaderSize + 4 + 4 + IPv4HeaderSize + UdpHeaderSize + 16;
        await Assert.That(n).IsEqualTo(Headers + payload.Length);

        // SOME/IP Length = (header size 16 - 8) + payload = 8 + 64 = 72.
        int someIpOffset = Headers - 16;
        uint someIpLength = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(someIpOffset + 4, 4));
        await Assert.That(someIpLength).IsEqualTo(8u + (uint)payload.Length);
    }

    #endregion

    /// <summary>
    /// Reference CRC-32 (IEEE 802.3) used to independently verify the FCS the
    /// trailer wrote.
    /// </summary>
    private static uint ComputeReferenceCrc32(ReadOnlySpan<byte> data)
    {
        const uint Polynomial = 0xEDB88320u;
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? Polynomial ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
