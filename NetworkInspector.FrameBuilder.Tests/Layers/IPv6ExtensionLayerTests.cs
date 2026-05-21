// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for IPv6 extension header layers:
/// <see cref="IPv6HopByHopLayer"/>, <see cref="IPv6RoutingLayer"/>,
/// <see cref="IPv6DestinationOptionsLayer"/>, and <see cref="IPv6FragmentExtensionLayer"/>.
/// </summary>
internal sealed class IPv6ExtensionLayerTests
{
    private static readonly MacAddress _Dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _Src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly EthernetLayer _Eth = new(_Dst, _Src);

    private static readonly IPv6Address _SrcIp6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly IPv6Address _DstIp6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);

    #region HopByHop

    [Test]
    public async Task HopByHop_Empty_HeaderSize_Is8()
    {
        IPv6HopByHopLayer hbh = new();
        await Assert.That(hbh.HeaderSize).IsEqualTo(8);
    }

    [Test]
    public async Task HopByHop_Empty_ProtocolType_Is0()
    {
        IPv6HopByHopLayer hbh = new();
        await Assert.That(hbh.ProtocolType).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task HopByHop_Empty_IPv6NextHeader_Is0()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6HopByHopLayer hbh = new();
        UdpLayer udp = new(1234, 80);
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + hbh.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(hbh).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // IPv6 NextHeader at Ethernet(14) + IPv6_NextHeader_offset(6) = byte 20
        byte nextHeader = buf[14 + 6];
        await Assert.That(nextHeader).IsEqualTo((byte)0); // 0 = HopByHop
    }

    [Test]
    public async Task HopByHop_NextHeader_PatchedToUdp_InExtensionHeader()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6HopByHopLayer hbh = new();
        UdpLayer udp = new(1234, 80);
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + hbh.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(hbh).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // HopByHop NextHeader is at byte 0 of the extension header
        int hbhOffset = 14 + IPv6Header.Size;
        byte hbhNextHeader = buf[hbhOffset];
        await Assert.That(hbhNextHeader).IsEqualTo(IpProtocols.Udp); // UDP = 17
    }

    [Test]
    public async Task HopByHop_WithOptions_HeaderSizePaddedTo8()
    {
        // 3-byte option data → raw = 2 + 3 = 5 → padded to 8
        byte[] options = [1, 2, 3]; // NOP NOP NOP
        IPv6HopByHopLayer hbh = new(options);
        await Assert.That(hbh.HeaderSize).IsEqualTo(8);
    }

    [Test]
    public async Task HopByHop_WithOptions_LargeData_HeaderSizeIs16()
    {
        // 10-byte option data → raw = 2 + 10 = 12 → padded to 16
        byte[] options = new byte[10];
        IPv6HopByHopLayer hbh = new(options);
        await Assert.That(hbh.HeaderSize).IsEqualTo(16);
    }

    #endregion

    #region Routing

    [Test]
    public async Task Routing_Type2_HeaderSize_Is24()
    {
        byte[] homeAddr = new byte[16];
        IPv6RoutingLayer rt = new(homeAddress: homeAddr);
        // 8 bytes fixed + 16 bytes home address = 24, already 8-byte aligned
        await Assert.That(rt.HeaderSize).IsEqualTo(24);
    }

    [Test]
    public async Task Routing_Type2_ProtocolType_Is43()
    {
        byte[] homeAddr = new byte[16];
        IPv6RoutingLayer rt = new(homeAddress: homeAddr);
        await Assert.That(rt.ProtocolType).IsEqualTo((ushort)43);
    }

    [Test]
    public async Task Routing_Type2_IPv6NextHeader_Is43()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        byte[] homeAddr = new byte[16];
        IPv6RoutingLayer rt = new(homeAddress: homeAddr);
        UdpLayer udp = new(1234, 80);
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + rt.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(rt).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        byte nextHeader = buf[14 + 6];
        await Assert.That(nextHeader).IsEqualTo((byte)43); // Routing
    }

    [Test]
    public async Task Routing_Generic_HeaderSizePaddedTo8()
    {
        // 5-byte type-specific data → 8 + 5 = 13 → padded to 16
        byte[] data = new byte[5];
        IPv6RoutingLayer rt = new(routingType: 0, segmentsLeft: 0, typeSpecificData: data);
        await Assert.That(rt.HeaderSize).IsEqualTo(16);
    }

    #endregion

    #region DestinationOptions

    [Test]
    public async Task DestinationOptions_Empty_HeaderSize_Is8()
    {
        IPv6DestinationOptionsLayer dst = new();
        await Assert.That(dst.HeaderSize).IsEqualTo(8);
    }

    [Test]
    public async Task DestinationOptions_Empty_ProtocolType_Is60()
    {
        IPv6DestinationOptionsLayer dst = new();
        await Assert.That(dst.ProtocolType).IsEqualTo((ushort)60);
    }

    [Test]
    public async Task DestinationOptions_IPv6NextHeader_Is60()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6DestinationOptionsLayer dst = new();
        UdpLayer udp = new(1234, 80);
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + dst.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(dst).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        byte nextHeader = buf[14 + 6];
        await Assert.That(nextHeader).IsEqualTo((byte)60); // Destination Options
    }

    [Test]
    public async Task DestinationOptions_NextHeader_PatchedToTcp_InExtensionHeader()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6DestinationOptionsLayer dst = new();
        TcpLayer tcp = new(1234, 80);
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + dst.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(dst).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // DestinationOptions NextHeader at byte 0 of ext header
        int dstOffset = 14 + IPv6Header.Size;
        byte dstNextHeader = buf[dstOffset];
        await Assert.That(dstNextHeader).IsEqualTo(IpProtocols.Tcp); // TCP = 6
    }

    #endregion

    #region Chained extension headers

    [Test]
    public async Task HopByHopThenRouting_NextHeaderChain_IsCorrect()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6HopByHopLayer hbh = new();
        byte[] homeAddr = new byte[16];
        IPv6RoutingLayer rt = new(homeAddress: homeAddr);
        UdpLayer udp = new(1234, 80);

        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + hbh.HeaderSize + rt.HeaderSize + udp.HeaderSize];
        bool _ok = FB.FrameStack.Start(_Eth).Then(ip6).Then(hbh).Then(rt).Then(udp)
            .CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        await Assert.That(_ok).IsTrue();

        // IPv6 NextHeader should be 0 (HopByHop)
        byte ip6NextHeader = buf[14 + 6];
        await Assert.That(ip6NextHeader).IsEqualTo((byte)0);

        // HopByHop NextHeader should be 43 (Routing)
        int hbhOffset = 14 + IPv6Header.Size;
        byte hbhNextHeader = buf[hbhOffset];
        await Assert.That(hbhNextHeader).IsEqualTo((byte)43);

        // Routing NextHeader should be 17 (UDP)
        int rtOffset = hbhOffset + hbh.HeaderSize;
        byte rtNextHeader = buf[rtOffset];
        await Assert.That(rtNextHeader).IsEqualTo(IpProtocols.Udp);
    }

    [Test]
    public async Task DestinationOptionsThenTcp_TcpChecksumIsValid()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6DestinationOptionsLayer dst = new();
        TcpLayer tcp = new(srcPort: 12345, dstPort: 443);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + dst.HeaderSize + tcp.HeaderSize + payload.Length];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(dst).Then(tcp).CreateWithFixedValues().Build(payload).MoveNext(buf, out _);

        // Verify TCP checksum (should be 0 after verification = valid)
        int tcpOffset = 14 + IPv6Header.Size + dst.HeaderSize;
        ushort verification = ChecksumUtils.PseudoHeaderIPv6(
            _SrcIp6.ToBytesArray(), _DstIp6.ToBytesArray(), IpProtocols.Tcp,
            buf.AsSpan(tcpOffset, buf.Length - tcpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region FragmentExt

    [Test]
    public async Task FragmentExt_HeaderSize_Is8()
    {
        IPv6FragmentExtensionLayer frag = new(identification: 42);
        await Assert.That(frag.HeaderSize).IsEqualTo(8);
    }

    [Test]
    public async Task FragmentExt_ProtocolType_Is44()
    {
        IPv6FragmentExtensionLayer frag = new(identification: 1);
        await Assert.That(frag.ProtocolType).IsEqualTo((ushort)44);
    }

    [Test]
    public async Task FragmentExt_MFlag_Set_InWireFormat()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        IPv6FragmentExtensionLayer frag = new(identification: 1);
        byte[] payload = [1, 2, 3, 4];
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + frag.HeaderSize + payload.Length];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(frag).CreateWithFixedValues().Build(payload).MoveNext(buf, out _);

        // Simulate what the fragmentation iterator does: patch the More Fragments
        // flag via PatchFragmentHeader, which is the method the FrameSequence calls
        // on each emitted fragment.
        int fragOffset = 14 + IPv6Header.Size;
        frag.PatchFragmentHeader(buf, fragOffset, frag.HeaderSize, fragmentPayloadOffset: 0, moreFragments: true);

        // Fragment extension header: bytes 0-7; M flag is bit 0 of byte 3.
        byte mBit = (byte)(buf[fragOffset + 3] & 0x01);
        await Assert.That(mBit).IsEqualTo((byte)1);
    }

    [Test]
    public async Task FragmentExt_Identification_WrittenBigEndian()
    {
        IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        uint id = 0xDEADBEEF;
        IPv6FragmentExtensionLayer frag = new(identification: id);
        byte[] payload = [1, 2, 3, 4];
        byte[] buf = new byte[_Eth.HeaderSize + ip6.HeaderSize + frag.HeaderSize + payload.Length];
        FB.FrameStack.Start(_Eth).Then(ip6).Then(frag).CreateWithFixedValues().Build(payload).MoveNext(buf, out _);

        int fragOffset = 14 + IPv6Header.Size;
        uint writtenId = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(fragOffset + 4, 4));
        await Assert.That(writtenId).IsEqualTo(id);
    }

    #endregion
}
