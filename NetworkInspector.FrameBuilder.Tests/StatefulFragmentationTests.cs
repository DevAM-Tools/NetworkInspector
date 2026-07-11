// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Tests for stateful-path fragmentation and ACK-mutation introduced by the
/// review remediation pass.
/// </summary>
/// <remarks>
/// <para>
/// Covers:
/// <list type="bullet">
///   <item>F1 — Stateful fragmentation loop: oversize payloads on a stateful
///         IPv4 stack must produce multiple correct fragments instead of a
///         single oversize frame.</item>
///   <item>F2 — <see cref="IPv4LayerWithAutoIpId"/> implements
///         <see cref="IFragmentable"/>: <c>dontFragment=false</c> permits
///         splitting; <c>dontFragment=true</c> raises
///         <see cref="BuildStatus.FragmentationRequired"/>.</item>
///   <item>F1 (counter discipline) — IPv4 Identification advances by exactly
///         one per <em>logical packet</em> (not per fragment); TCP sequence
///         number advances by the full payload size (not per fragment).</item>
///   <item>F3 — <see cref="Session{TStack,TTrailer,TInterceptor}.UpdateAck"/>
///         updates the ACK number written into subsequent frames.</item>
///   <item>F8 — Scratch reentrancy guard: nested fragmenting builds on the
///         same thread (e.g. via an interceptor) do not corrupt the outer
///         iterator's cached headers.</item>
/// </list>
/// </para>
/// </remarks>
[NotInParallel(nameof(StatefulFragmentationTests))]
internal sealed class StatefulFragmentationTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _SrcIp4 = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp4 = IPv4Address.FromBytes([10, 0, 0, 2]);

    private const int _MtuFrameBytes = 1500;
    private const int _EthHeaderSize = 14;
    private const int _IPv4HeaderSize = 20;
    private const int _UdpHeaderSize = 8;
    private const int _TcpHeaderSize = 20;

    private static ushort _ReadIPv4Id(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(_EthHeaderSize + 4, 2));

    private static ushort _ReadIPv4FlagsField(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(_EthHeaderSize + 6, 2));

    private static uint _ReadTcpAck(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(_EthHeaderSize + _IPv4HeaderSize + 8, 4));

    private static uint _ReadTcpSeq(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(_EthHeaderSize + _IPv4HeaderSize + 4, 4));

    #region F1 + F2 — stateful IPv4 fragmentation

    [Test]
    public async Task StatefulIPv4_OversizePayload_EmitsMultipleFragments_WithSameIPID()
    {
        // Per-fragment IP body = 1500 - 14 - 20 = 1466 → rounded down to
        // multiple of 8 = 1464.  Inner-of-fragmentable payload (UDP header
        // + payload) = 8 + 3000 = 3008 → ceil(3008/1464) = 3 fragments.
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 4242, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] payload = new byte[3000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();
        StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> seq = session.NextPacket(payload);

        List<byte[]> fragments = [];
        byte[] scratch = new byte[_MtuFrameBytes];
        while (seq.MoveNext(scratch, out int n))
        {
            byte[] frame = new byte[n];
            scratch.AsSpan(0, n).CopyTo(frame);
            fragments.Add(frame);
        }
        BuildStatus status = seq.Status;

        await Assert.That(status).IsEqualTo(BuildStatus.Success);
        await Assert.That(fragments.Count).IsEqualTo(3);

        // All fragments must share the IPID seeded for this packet.
        await Assert.That(_ReadIPv4Id(fragments[0])).IsEqualTo<ushort>(4242);
        await Assert.That(_ReadIPv4Id(fragments[1])).IsEqualTo<ushort>(4242);
        await Assert.That(_ReadIPv4Id(fragments[2])).IsEqualTo<ushort>(4242);

        // Fragment 0: MF=1, offset=0; Fragment 1: MF=1, offset=183; Fragment 2: MF=0, offset=366.
        ushort f0 = _ReadIPv4FlagsField(fragments[0]);
        ushort f1 = _ReadIPv4FlagsField(fragments[1]);
        ushort f2 = _ReadIPv4FlagsField(fragments[2]);
        await Assert.That((f0 & 0x2000) != 0).IsTrue();
        await Assert.That(f0 & 0x1FFF).IsEqualTo(0);
        await Assert.That((f1 & 0x2000) != 0).IsTrue();
        await Assert.That(f1 & 0x1FFF).IsEqualTo(183);
        await Assert.That((f2 & 0x2000) == 0).IsTrue();
        await Assert.That(f2 & 0x1FFF).IsEqualTo(366);
    }

    [Test]
    public async Task StatefulIPv4_OversizePayload_DontFragment_True_Returns_FragmentationRequired()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 1, dontFragment: true);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] payload = new byte[3000];

        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();
        StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> seq = session.NextPacket(payload);

        byte[] dst = new byte[_MtuFrameBytes];
        bool wrote = seq.MoveNext(dst, out int written);
        BuildStatus status = seq.Status;

        await Assert.That(wrote).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(status).IsEqualTo(BuildStatus.FragmentationRequired);
    }

    [Test]
    public async Task StatefulIPv4_OversizeAcrossTwoPackets_AdvancesIPIdByOnePerPacket()
    {
        // Two oversize packets, each yielding multiple fragments.  IPID must
        // advance by exactly one between the packets, not by the per-packet
        // fragment count.
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 1000, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] payload = new byte[3000];
        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        ushort firstId = 0;
        ushort secondId = 0;
        int firstFragCount = 0;
        int secondFragCount = 0;
        for (int packet = 0; packet < 2; packet++)
        {
            (ushort id, int count) = _DrainPacket(session, payload);
            if (packet == 0)
            {
                firstId = id;
                firstFragCount = count;
            }
            else
            {
                secondId = id;
                secondFragCount = count;
            }
        }

        await Assert.That(firstFragCount).IsEqualTo(3);
        await Assert.That(secondFragCount).IsEqualTo(3);
        await Assert.That(firstId).IsEqualTo<ushort>(1000);
        await Assert.That(secondId).IsEqualTo<ushort>(1001);
    }

    /// <summary>Drains one packet from the session and returns the IPID of fragment 0 plus the fragment count.</summary>
    private static (ushort firstFragmentId, int fragmentCount) _DrainPacket(
        Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> session,
        ReadOnlySpan<byte> payload)
    {
        StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> seq = session.NextPacket(payload);
        byte[] scratch = new byte[_MtuFrameBytes];
        int fragIdx = 0;
        ushort firstFragmentId = 0;
        while (seq.MoveNext(scratch, out int n))
        {
            if (fragIdx == 0)
            {
                firstFragmentId = _ReadIPv4Id(scratch);
            }
            fragIdx++;
        }
        return (firstFragmentId, fragIdx);
    }

    #endregion

    #region F1 — TCP sequence advances by full payload length (once per logical packet)

    [Test]
    public async Task StatefulTcp_OversizePayload_TcpSeqAdvancesByFullPayloadLength()
    {
        // 4000-byte TCP payload across IPv4/Eth at MTU 1500.  Inner-of-IPv4
        // payload = 20 (TCP header) + 4000 = 4020.  Per-fragment slot = 1464.
        // Fragments: ceil(4020/1464) = 3.  TCP seq must advance by exactly 4000
        // between two such packets (not by 3*1464).
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 1, dontFragment: false);
        FB.TcpLayerWithAutoSequence tcp = new(srcPort: 1, dstPort: 2, initialSequence: 5_000_000, initialAck: 0);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        byte[] payload = new byte[4000];
        using Session<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        // Packet 1: drain in one synchronous block, capture seq from first fragment.
        uint seqP1 = _DrainTcpPacketFirstSeq(session, payload);

        // Packet 2.
        uint seqP2 = _DrainTcpPacketFirstSeq(session, payload);

        await Assert.That(seqP1).IsEqualTo<uint>(5_000_000);
        // Advance by full payload length, NOT by sum of fragment sizes.
        await Assert.That(seqP2).IsEqualTo<uint>(5_000_000u + 4000u);
    }

    /// <summary>Drains one stateful TCP packet from the session and returns the TCP sequence number of fragment 0.</summary>
    private static uint _DrainTcpPacketFirstSeq(
        Session<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> session,
        ReadOnlySpan<byte> payload)
    {
        StatefulFrameSequence<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> seq = session.NextPacket(payload);
        byte[] scratch = new byte[_MtuFrameBytes];
        uint firstSeq = 0;
        int idx = 0;
        while (seq.MoveNext(scratch, out _))
        {
            if (idx == 0)
            {
                firstSeq = _ReadTcpSeq(scratch);
            }
            idx++;
        }
        return firstSeq;
    }

    #endregion

    #region F3 — Session.UpdateAck

    [Test]
    public async Task Session_UpdateAck_UpdatesAckOnSubsequentFrames()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 1);
        FB.TcpLayerWithAutoSequence tcp = new(srcPort: 100, dstPort: 200, initialSequence: 0, initialAck: 0);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        byte[] payload = [0x42];
        byte[] dst = new byte[stack.HeaderSize + payload.Length];
        using Session<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        // Frame 1: ACK = 0 (initial).
        StatefulFrameSequence<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> s1 = session.NextPacket(payload);
        s1.MoveNext(dst, out _);
        uint ack1 = _ReadTcpAck(dst);

        // Application sees an inbound ACK; mutate the session.
        session.UpdateAck(0xCAFEBABE);

        // Frame 2: must carry the updated ACK.
        StatefulFrameSequence<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> s2 = session.NextPacket(payload);
        s2.MoveNext(dst, out _);
        uint ack2 = _ReadTcpAck(dst);

        // Update again; frame 3 reflects the new value.
        session.UpdateAck(0x12345678);
        StatefulFrameSequence<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> s3 = session.NextPacket(payload);
        s3.MoveNext(dst, out _);
        uint ack3 = _ReadTcpAck(dst);

        await Assert.That(ack1).IsEqualTo<uint>(0);
        await Assert.That(ack2).IsEqualTo<uint>(0xCAFEBABE);
        await Assert.That(ack3).IsEqualTo<uint>(0x12345678);
    }

    [Test]
    public async Task Session_UpdateAck_AfterDispose_Throws_ObjectDisposedException()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 1);
        FB.TcpLayerWithAutoSequence tcp = new(1, 2, 0, 0);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        Session<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();
        session.Dispose();

        await Assert.That(() => session.UpdateAck(123)).Throws<ObjectDisposedException>();
    }

    #endregion

    #region F8 — scratch reentrancy guard

    /// <summary>
    /// Interceptor that, on the FIRST OnFrameComplete, runs a complete
    /// fragmenting build on the same thread.  The outer build is itself
    /// fragmenting; without the reentrancy guard, the inner build would
    /// overwrite the outer iterator's cached headers and produce corrupted
    /// fragments.
    /// </summary>
    private struct ReentrantFragmentInterceptor : FB.IFrameInterceptor
    {
        private bool _Triggered;
        public List<byte[]> InnerFragments;

        public ReentrantFragmentInterceptor(List<byte[]> innerFragments)
        {
            _Triggered = false;
            InnerFragments = innerFragments;
        }

        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, IProtocolLayer
        {
        }

        public void OnFrameComplete(scoped Span<byte> frame)
        {
            if (_Triggered)
            {
                return;
            }
            _Triggered = true;

            // Run a nested fragmenting build that DELIBERATELY uses different
            // header values so any header-cache cross-contamination would be
            // visible to the outer-loop verification below.
            FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
            FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
            FB.UdpLayer udp = new(7777, 8888, FB.Auto.Explicit((ushort)0));

            FB.CreatedStack<
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
                FB.NoTrailer,
                FB.NoInterceptor> nested = FB.FrameStack
                    .Start(eth)
                    .Then(ip)
                    .Then(udp)
                    .CreateWithFixedValues();

            byte[] payload = new byte[3000];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(0x80 ^ (i & 0xFF));
            }

            FB.FrameSequence<
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
                FB.NoTrailer, FB.NoInterceptor> seq = nested.Build(payload);

            byte[] scratch = new byte[_MtuFrameBytes];
            while (seq.MoveNext(scratch, out int n))
            {
                byte[] frameCopy = new byte[n];
                scratch.AsSpan(0, n).CopyTo(frameCopy);
                InnerFragments.Add(frameCopy);
            }
        }
    }

    [Test]
    public async Task Scratch_ReentrantBuildFromInterceptor_DoesNotCorruptOuterFragments()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        List<byte[]> innerFragments = [];
        ReentrantFragmentInterceptor interceptor = new(innerFragments);

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            ReentrantFragmentInterceptor> stack = FB.FrameStack.CreateWithFixedValues(
                FB.FrameStack.Start(eth).Then(ip).Then(udp),
                interceptor);

        byte[] payload = new byte[3000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        FB.FrameSequence<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer, ReentrantFragmentInterceptor> seq = stack.Build(payload);

        List<byte[]> outerFragments = [];
        byte[] scratch = new byte[_MtuFrameBytes];
        while (seq.MoveNext(scratch, out int n))
        {
            byte[] frame = new byte[n];
            scratch.AsSpan(0, n).CopyTo(frame);
            outerFragments.Add(frame);
        }

        // Outer build: 3 fragments.  All must keep the OUTER IP src/dst bytes
        // (10.0.0.1 / 10.0.0.2) — if the inner build had clobbered the cached
        // headers, fragments 1+ would carry stale or zero IP addresses.
        await Assert.That(outerFragments.Count).IsEqualTo(3);
        for (int i = 0; i < outerFragments.Count; i++)
        {
            byte[] srcIpBytes = outerFragments[i].AsSpan(_EthHeaderSize + 12, 4).ToArray();
            byte[] dstIpBytes = outerFragments[i].AsSpan(_EthHeaderSize + 16, 4).ToArray();
            await Assert.That(srcIpBytes.AsSpan().SequenceEqual([(byte)10, (byte)0, (byte)0, (byte)1])).IsTrue();
            await Assert.That(dstIpBytes.AsSpan().SequenceEqual([(byte)10, (byte)0, (byte)0, (byte)2])).IsTrue();
        }

        // Inner build also produced 3 fragments and was untouched by the outer.
        await Assert.That(innerFragments.Count).IsEqualTo(3);
    }

    #endregion
}
