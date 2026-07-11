// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the IPv4/IPv6 fragmentation pipeline:
/// <list type="bullet">
///   <item>IPv4 layer-driven fragmentation (Eth/IPv4/UDP).</item>
///   <item>IPv4 DontFragment behaviour (FragmentationRequired status).</item>
///   <item>IPv6 fragmentation via the new IPv6FragmentExtensionLayer.</item>
///   <item>Single-frame backwards compatibility for sub-MTU payloads.</item>
/// </list>
/// </summary>
internal sealed class FragmentationSmokeTests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x10, 0x20, 0x30, 0x40, 0x50, 0x60]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

    private static readonly IPv4Address _SrcIp4 = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp4 = IPv4Address.FromBytes([10, 0, 0, 2]);

    private static readonly IPv6Address _SrcIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _DstIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    /// <summary>Frame-MTU used in the tests (full Ethernet frame size).</summary>
    private const int _MtuFrameBytes = 1500;

    /// <summary>IPv4 header without options.</summary>
    private const int _IPv4HeaderSize = 20;

    /// <summary>IPv6 fixed header.</summary>
    private const int _IPv6HeaderSize = 40;

    /// <summary>Ethernet header (no FCS).</summary>
    private const int _EthHeaderSize = 14;

    /// <summary>UDP header.</summary>
    private const int _UdpHeaderSize = 8;

    /// <summary>IPv6 fragment extension header.</summary>
    private const int _IPv6FragHeaderSize = 8;

    #endregion

    #region IPv4 fragmentation

    [Test]
    public async Task IPv4_LargePayload_EmitsMultipleFragments_WithMfAndOffsetChain()
    {
        // 3000 bytes UDP payload requires three IPv4 fragments at MTU 1500
        // (per-fragment IP body = 1500 - 14 - 20 = 1466; rounded down to
        // multiple of 8 = 1464).  Inner-of-fragmentable payload (UDP header
        // + payload) = 8 + 3000 = 3008.  ceil(3008 / 1464) = 3 fragments.
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        byte[] payload = new byte[3000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

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

        List<byte[]> fragments = _CollectFragments(in stack, payload, _MtuFrameBytes);

        await Assert.That(fragments.Count).IsEqualTo(3);

        // Per-fragment slice geometry: 1464, 1464, 80 (= 3008 - 2*1464).
        await Assert.That(fragments[0].Length).IsEqualTo(_EthHeaderSize + _IPv4HeaderSize + 1464);
        await Assert.That(fragments[1].Length).IsEqualTo(_EthHeaderSize + _IPv4HeaderSize + 1464);
        await Assert.That(fragments[2].Length).IsEqualTo(_EthHeaderSize + _IPv4HeaderSize + 80);

        // Fragment 0: MF=1, FragOffset=0.
        ushort flags0 = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(_EthHeaderSize + 6, 2));
        await Assert.That((flags0 & 0x2000) != 0).IsTrue();          // MF=1
        await Assert.That(flags0 & 0x1FFF).IsEqualTo(0);             // offset=0
        await Assert.That((flags0 & 0x4000) == 0).IsTrue();          // DF cleared

        // Fragment 1: MF=1, FragOffset=183 (1464 / 8).
        ushort flags1 = BinaryPrimitives.ReadUInt16BigEndian(fragments[1].AsSpan(_EthHeaderSize + 6, 2));
        await Assert.That((flags1 & 0x2000) != 0).IsTrue();
        await Assert.That(flags1 & 0x1FFF).IsEqualTo(183);

        // Fragment 2: MF=0, FragOffset=366 (2*1464 / 8).
        ushort flags2 = BinaryPrimitives.ReadUInt16BigEndian(fragments[2].AsSpan(_EthHeaderSize + 6, 2));
        await Assert.That((flags2 & 0x2000) == 0).IsTrue();
        await Assert.That(flags2 & 0x1FFF).IsEqualTo(366);

        // Same Identification across all fragments (left at 0 because we did
        // not set it explicitly — still must be identical).
        ushort id0 = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(_EthHeaderSize + 4, 2));
        ushort id1 = BinaryPrimitives.ReadUInt16BigEndian(fragments[1].AsSpan(_EthHeaderSize + 4, 2));
        ushort id2 = BinaryPrimitives.ReadUInt16BigEndian(fragments[2].AsSpan(_EthHeaderSize + 4, 2));
        await Assert.That(id0).IsEqualTo(id1);
        await Assert.That(id1).IsEqualTo(id2);

        // Per-fragment TotalLength matches actual frame bytes minus eth header.
        ushort total0 = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(_EthHeaderSize + 2, 2));
        ushort total2 = BinaryPrimitives.ReadUInt16BigEndian(fragments[2].AsSpan(_EthHeaderSize + 2, 2));
        await Assert.That(total0).IsEqualTo((ushort)(_IPv4HeaderSize + 1464));
        await Assert.That(total2).IsEqualTo((ushort)(_IPv4HeaderSize + 80));

        // IPv4 header checksum recomputed on each fragment — verify by
        // checking the one's-complement sum over the 20-byte header is zero.
        for (int i = 0; i < fragments.Count; i++)
        {
            await Assert.That(_VerifyOnesComplementZero(fragments[i].AsSpan(_EthHeaderSize, _IPv4HeaderSize))).IsTrue();
        }
    }

    [Test]
    public async Task IPv4_DontFragment_True_ProducesFragmentationRequiredStatus()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: true);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        byte[] payload = new byte[3000];

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

        byte[] dst = new byte[_MtuFrameBytes];
        bool wrote;
        int written;
        FB.BuildStatus status;
        {
            FB.FrameSequence<
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
                FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);
            wrote = seq.MoveNext(dst, out written);
            status = seq.Status;
        }
        await Assert.That(wrote).IsFalse();
        await Assert.That(written).IsEqualTo(0);
        await Assert.That(status).IsEqualTo(FB.BuildStatus.FragmentationRequired);
    }

    [Test]
    public async Task IPv4_SubMtuPayload_StillEmitsExactlyOneFrame()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        byte[] payload = new byte[200];

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

        List<byte[]> fragments = _CollectFragments(in stack, payload, _MtuFrameBytes);

        await Assert.That(fragments.Count).IsEqualTo(1);
        await Assert.That(fragments[0].Length).IsEqualTo(_EthHeaderSize + _IPv4HeaderSize + _UdpHeaderSize + payload.Length);

        // Single-frame path: Flags+FragmentOffset must remain at "DF=true" from
        // the un-touched IPv4 header, MF=0, offset=0.  (DF bit reflects the
        // IPv4Layer instance — here dontFragment:false → DF=0.)
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(_EthHeaderSize + 6, 2));
        await Assert.That(flags & 0x3FFF).IsEqualTo(0);
    }

    #endregion

    #region IPv6 fragmentation

    [Test]
    public async Task IPv6Fragment_LargePayload_EmitsMultipleFragments_WithMfAndOffsetChain()
    {
        // Per-fragment IP body capacity = 1500 - 14 - 40 - 8 = 1438; rounded
        // down to multiple of 8 = 1432.  Inner-of-fragmentable = UDP + payload
        // = 8 + 3000 = 3008.  ceil(3008 / 1432) = 3 fragments.
        FB.EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        FB.IPv6Layer ip = new(_SrcIp6, _DstIp6);
        FB.IPv6FragmentExtensionLayer frag = new(identification: 0xCAFEBABE);
        FB.UdpLayer udp = new(53, 53);

        byte[] payload = new byte[3000];

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv6FragmentExtensionLayer,
                    FB.StatelessStack<FB.IPv6Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(frag)
                .Then(udp)
                .CreateWithFixedValues();

        List<byte[]> fragments = _CollectFragments(in stack, payload, _MtuFrameBytes);
        await Assert.That(fragments.Count).IsEqualTo(3);

        // Outer: IPv6.NextHeader points to Fragment (44).
        await Assert.That(fragments[0][_EthHeaderSize + 6]).IsEqualTo(IpProtocols.IPv6Fragment);
        // Frag header: NextHeader = UDP (17).
        const int FragOffsetInFrame = _EthHeaderSize + _IPv6HeaderSize;
        await Assert.That(fragments[0][FragOffsetInFrame + 0]).IsEqualTo(IpProtocols.Udp);

        // Identification must be 0xCAFEBABE in every fragment.
        for (int i = 0; i < fragments.Count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(fragments[i].AsSpan(FragOffsetInFrame + 4, 4));
            await Assert.That(id).IsEqualTo(0xCAFEBABEu);
        }

        // FragmentOffset+Flags: 13 high bits = offset/8, low bit = M.
        ushort word0 = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(FragOffsetInFrame + 2, 2));
        ushort word1 = BinaryPrimitives.ReadUInt16BigEndian(fragments[1].AsSpan(FragOffsetInFrame + 2, 2));
        ushort word2 = BinaryPrimitives.ReadUInt16BigEndian(fragments[2].AsSpan(FragOffsetInFrame + 2, 2));

        await Assert.That(word0 & 0x0001).IsEqualTo(1);              // MF=1
        await Assert.That(word0 >> 3).IsEqualTo(0);                  // offset=0

        await Assert.That(word1 & 0x0001).IsEqualTo(1);              // MF=1
        await Assert.That(word1 >> 3).IsEqualTo(1432 / 8);           // offset=179

        await Assert.That(word2 & 0x0001).IsEqualTo(0);              // MF=0
        await Assert.That(word2 >> 3).IsEqualTo((1432 * 2) / 8);     // offset=358

        // IPv6 PayloadLength per fragment = (FragHdr + slice).
        ushort plen0 = BinaryPrimitives.ReadUInt16BigEndian(fragments[0].AsSpan(_EthHeaderSize + 4, 2));
        await Assert.That(plen0).IsEqualTo((ushort)(_IPv6FragHeaderSize + 1432));
    }

    #endregion

    #region Helpers

    /// <summary>Drains a <see cref="FB.FrameSequence{TStack,TTrailer,TInterceptor}"/> into a list of frame copies.</summary>
    private static List<byte[]> _CollectFragments<TStack, TTrailer, TInterceptor>(
        in FB.CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload,
        int frameBufferBytes)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
        where TTrailer : struct, FB.ITrailerLayer
        where TInterceptor : struct, FB.IFrameInterceptor
    {
        FB.FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        List<byte[]> result = [];
        byte[] scratch = new byte[frameBufferBytes];
        while (seq.MoveNext(scratch, out int written))
        {
            byte[] frame = new byte[written];
            scratch.AsSpan(0, written).CopyTo(frame);
            result.Add(frame);
        }
        return result;
    }

    /// <summary>
    /// One's-complement sum check: a valid IPv4 header has a 16-bit one's-
    /// complement sum of zero across the entire header.
    /// </summary>
    private static bool _VerifyOnesComplementZero(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (int i = 0; i + 1 < header.Length; i += 2)
        {
            sum += (uint)((header[i] << 8) | header[i + 1]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return sum == 0xFFFF;
    }

    #endregion
}
