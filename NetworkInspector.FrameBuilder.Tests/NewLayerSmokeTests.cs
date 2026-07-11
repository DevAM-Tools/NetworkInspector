// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the M2 stateless layers added to the new
/// <see cref="NetworkInspector.FrameBuilder.Frames.FrameStack"/> API:
/// <c>VlanLayer</c>, <c>IPv6Layer</c>, <c>TcpLayer</c>, <c>ArpLayer</c>,
/// <c>IcmpV4EchoLayer</c>, <c>IcmpV6EchoLayer</c>, <c>IPv4LayerWithOptions</c>,
/// <c>TcpLayerWithOptions</c>.
/// </summary>
/// <remarks>
/// Each test exercises one layer end-to-end and verifies the structural
/// post-fix output (length fields, EtherType / Protocol patches, header
/// checksums) so a regression in the per-layer logic is detected without
/// depending on a reference implementation.
/// </remarks>
internal sealed class NewLayerSmokeTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    private static readonly IPv4Address _SrcIp4 = new(0x0A000001);
    private static readonly IPv4Address _DstIp4 = new(0x0A000002);

    // fe80::1 / fe80::2
    private static readonly IPv6Address _SrcIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    private static readonly IPv6Address _DstIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF];

    #region VlanLayer

    [Test]
    public async Task Vlan_PatchesEtherTypeAsTpid_AndInnerEtherType()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer vlan = new(vlanId: 100, pcp: 3);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.VlanLayer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(vlan)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        const int ExpectedTotal = 14 + 4 + 20 + 8 + 4; // payload = 4
        byte[] frame = new byte[ExpectedTotal];
        int written = _EmitOnce(in stack, _Payload, frame);

        await Assert.That(written).IsEqualTo(ExpectedTotal);

        // Outer EtherType = TPID (0x8100).
        ushort outerEtherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2));
        await Assert.That(outerEtherType).IsEqualTo((ushort)EtherTypes.VlanTagged);

        // VLAN TCI bit-layout: PCP=3 → 0b011_xxxxxxxxx_xxx, VID=100.
        ushort tci = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14, 2));
        await Assert.That((ushort)(tci & 0x0FFF)).IsEqualTo((ushort)100);
        await Assert.That((ushort)((tci >> 13) & 0x7)).IsEqualTo((ushort)3);

        // Inner EtherType = IPv4 (0x0800), patched into VLAN tag offset+2.
        ushort innerEtherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2));
        await Assert.That(innerEtherType).IsEqualTo((ushort)EtherTypes.IPv4);
    }

    [Test]
    public async Task Vlan_QinQ_StacksTwoTags()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.VlanLayer outerTag = new(vlanId: 200, isQinQ: true);
        FB.VlanLayer innerTag = new(vlanId: 100);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(53, 53, FB.Auto.Explicit((ushort)0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.VlanLayer,
                        FB.StatelessStack<FB.VlanLayer,
                            FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(outerTag)
                .Then(innerTag)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        byte[] frame = new byte[14 + 4 + 4 + 20 + 8 + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // Eth.EtherType = QinQ (outer tag's TPID).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)))
            .IsEqualTo((ushort)EtherTypes.QinQ);

        // outer-VLAN.InnerEtherType = inner-VLAN.TPID (0x8100).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16, 2)))
            .IsEqualTo((ushort)EtherTypes.VlanTagged);

        // inner-VLAN.InnerEtherType = IPv4 (0x0800).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(20, 2)))
            .IsEqualTo((ushort)EtherTypes.IPv4);
    }

    #endregion

    #region IPv6Layer

    [Test]
    public async Task IPv6_PatchesPayloadLength_NextHeader_AndUdpChecksum()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        FB.UdpLayer udp = new(1000, 53);

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv6Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip6)
                .Then(udp)
                .CreateWithFixedValues();

        const int ExpectedTotal = 14 + 40 + 8 + 4; // payload = 4
        byte[] frame = new byte[ExpectedTotal];
        int written = _EmitOnce(in stack, _Payload, frame);

        await Assert.That(written).IsEqualTo(ExpectedTotal);

        // Eth.EtherType = IPv6 (0x86DD).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)))
            .IsEqualTo((ushort)EtherTypes.IPv6);

        // IPv6.PayloadLength = UDP header + payload (= 8 + 4).
        ushort payloadLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 4, 2));
        await Assert.That(payloadLen).IsEqualTo((ushort)(8 + _Payload.Length));

        // IPv6.NextHeader = UDP (17).
        await Assert.That(frame[14 + 6]).IsEqualTo((byte)IpProtocols.Udp);

        // UDP checksum is non-zero (computed over IPv6 pseudo-header).
        ushort udpChecksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 40 + 6, 2));
        await Assert.That(udpChecksum).IsNotEqualTo((ushort)0);
    }

    #endregion

    #region TcpLayer

    [Test]
    public async Task Tcp_OverIPv4_PatchesProtocol_AndChecksum()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.TcpLayer tcp = new(srcPort: 12345, dstPort: 80, seqNum: 0x11223344, flags: TcpFlags.Syn);

        FB.CreatedStack<
            FB.StatelessStack<FB.TcpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(tcp)
                .CreateWithFixedValues();

        byte[] frame = new byte[14 + 20 + 20 + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // IP.Protocol = TCP (6).
        await Assert.That(frame[14 + 9]).IsEqualTo((byte)IpProtocols.Tcp);

        // TCP.SeqNum = supplied value.
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(14 + 20 + 4, 4));
        await Assert.That(seq).IsEqualTo(0x11223344u);

        // TCP.DataOffset = 5 words.
        byte dataOffsetWords = (byte)(frame[14 + 20 + 12] >> 4);
        await Assert.That(dataOffsetWords).IsEqualTo((byte)5);

        // TCP checksum is non-zero (covers IPv4 pseudo-header + TCP segment).
        ushort tcpChecksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 20 + 16, 2));
        await Assert.That(tcpChecksum).IsNotEqualTo((ushort)0);
    }

    #endregion

    #region ArpLayer

    [Test]
    public async Task Arp_PatchesEtherType_AndWritesOpcode()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.ArpLayer arp = new(
            opcode: FB.ArpLayer.OpcodeRequest,
            senderMac: _SrcMac,
            senderIp: _SrcIp4,
            targetMac: MacAddress.FromBytes([0, 0, 0, 0, 0, 0]),
            targetIp: _DstIp4);

        FB.CreatedStack<
            FB.StatelessStack<FB.ArpLayer,
                FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(arp)
                .CreateWithFixedValues();

        byte[] frame = new byte[14 + 28];
        int written = _EmitOnce(in stack, ReadOnlySpan<byte>.Empty, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // Eth.EtherType = ARP (0x0806).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2)))
            .IsEqualTo((ushort)EtherTypes.Arp);

        // ARP.Opcode = 1 (request).
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 6, 2)))
            .IsEqualTo((ushort)1);

        // ARP.HwLen = 6, ARP.ProtoLen = 4.
        await Assert.That(frame[14 + 4]).IsEqualTo((byte)6);
        await Assert.That(frame[14 + 5]).IsEqualTo((byte)4);
    }

    #endregion

    #region IcmpV4EchoLayer

    [Test]
    public async Task IcmpV4Echo_PatchesProtocol_AndComputesChecksum()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.IcmpV4EchoLayer icmp = new(identifier: 0x1234, sequenceNumber: 1);

        FB.CreatedStack<
            FB.StatelessStack<FB.IcmpV4EchoLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(icmp)
                .CreateWithFixedValues();

        byte[] frame = new byte[14 + 20 + 8 + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // IP.Protocol = ICMP (1).
        await Assert.That(frame[14 + 9]).IsEqualTo((byte)IpProtocols.Icmp);

        // ICMP.Type = 8 (echo request).
        await Assert.That(frame[14 + 20]).IsEqualTo(FB.IcmpV4EchoLayer.TypeEchoRequest);

        // ICMP checksum verifies to 0 over the message (one's complement self-check).
        ushort icmpVerify = ChecksumUtils.OnesComplement(frame.AsSpan(14 + 20, 8 + _Payload.Length));
        await Assert.That(icmpVerify).IsEqualTo((ushort)0);
    }

    #endregion

    #region IcmpV6EchoLayer

    [Test]
    public async Task IcmpV6Echo_PatchesNextHeader_AndComputesChecksumWithPseudoHeader()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        FB.IcmpV6EchoLayer icmp = new(identifier: 0x4321, sequenceNumber: 7);

        FB.CreatedStack<
            FB.StatelessStack<FB.IcmpV6EchoLayer,
                FB.StatelessStack<FB.IPv6Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip6)
                .Then(icmp)
                .CreateWithFixedValues();

        byte[] frame = new byte[14 + 40 + 8 + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // IPv6.NextHeader = ICMPv6 (58).
        await Assert.That(frame[14 + 6]).IsEqualTo((byte)IpProtocols.IcmpV6);

        // ICMPv6.Type = 128 (echo request).
        await Assert.That(frame[14 + 40]).IsEqualTo(FB.IcmpV6EchoLayer.TypeEchoRequest);

        // ICMPv6 checksum is non-zero (covers IPv6 pseudo-header + message).
        ushort icmpChecksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 40 + 2, 2));
        await Assert.That(icmpChecksum).IsNotEqualTo((ushort)0);
    }

    #endregion

    #region IPv4LayerWithOptions

    [Test]
    public async Task IPv4WithOptions_PadsToFourBytes_AndAdjustsIhl()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);

        // Router Alert option: type(148)+len(4)+value(0x0000) = 4 bytes (already aligned).
        byte[] options = [148, 4, 0x00, 0x00];
        FB.IPv4LayerWithOptions ip = new(_SrcIp4, _DstIp4, options);
        FB.UdpLayer udp = new(1000, 53, FB.Auto.Explicit((ushort)0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4LayerWithOptions,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        const int IpHeader = 24; // 20 + 4 options
        byte[] frame = new byte[14 + IpHeader + 8 + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // VersionIhl: low nibble = IHL in 32-bit words = 6 (24 bytes).
        await Assert.That((byte)(frame[14] & 0x0F)).IsEqualTo((byte)6);

        // IP.TotalLength covers full IP header + UDP + payload.
        ushort totalLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 2, 2));
        await Assert.That(totalLen).IsEqualTo((ushort)(IpHeader + 8 + _Payload.Length));

        // IPv4 header checksum verifies to 0.
        ushort hdrChecksum = ChecksumUtils.IPv4Header(frame.AsSpan(14, IpHeader));
        await Assert.That(hdrChecksum).IsEqualTo((ushort)0);

        // Option byte preserved.
        await Assert.That(frame[14 + 20]).IsEqualTo((byte)148);
    }

    #endregion

    #region TcpLayerWithOptions

    [Test]
    public async Task TcpWithOptions_AdjustsDataOffset_AndPreservesOptions()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);

        // TCP MSS option: kind(2)+len(4)+mss(2 BE) — 4 bytes (aligned).
        byte[] options = [2, 4, 0x05, 0xB4]; // MSS = 1460
        FB.TcpLayerWithOptions tcp = new(
            srcPort: 12345,
            dstPort: 443,
            opts: new FB.TcpOptions(options),
            seqNum: 0xCAFEBABEu,
            flags: TcpFlags.Syn);

        FB.CreatedStack<
            FB.StatelessStack<FB.TcpLayerWithOptions,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(tcp)
                .CreateWithFixedValues();

        const int TcpHeader = 24; // 20 + 4 options
        byte[] frame = new byte[14 + 20 + TcpHeader + _Payload.Length];
        int written = _EmitOnce(in stack, _Payload, frame);
        await Assert.That(written).IsEqualTo(frame.Length);

        // IP.Protocol = TCP (6).
        await Assert.That(frame[14 + 9]).IsEqualTo((byte)IpProtocols.Tcp);

        // TCP.DataOffset (high nibble of offset+12) = 6 words (24 bytes).
        byte dataOffsetWords = (byte)(frame[14 + 20 + 12] >> 4);
        await Assert.That(dataOffsetWords).IsEqualTo((byte)6);

        // Options preserved at offset+20 (= TCP header + 0).
        await Assert.That(frame[14 + 20 + 20]).IsEqualTo((byte)2); // kind
        await Assert.That(frame[14 + 20 + 21]).IsEqualTo((byte)4); // len
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 20 + 22, 2)))
            .IsEqualTo((ushort)1460);

        // TCP checksum non-zero.
        ushort tcpChecksum = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 20 + 16, 2));
        await Assert.That(tcpChecksum).IsNotEqualTo((ushort)0);
    }

    #endregion

    #region Helpers

    /// <summary>Sync helper that emits a single frame and returns its byte length.</summary>
    private static int _EmitOnce<TStack, TTrailer, TInterceptor>(
        in FB.CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload,
        Span<byte> dst)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
        where TTrailer : struct, FB.ITrailerLayer
        where TInterceptor : struct, FB.IFrameInterceptor
    {
        FB.FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        seq.MoveNext(dst, out int written);
        return written;
    }

    #endregion
}
