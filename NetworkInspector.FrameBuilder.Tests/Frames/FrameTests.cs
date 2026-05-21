// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Frames;

/// <summary>
/// Integration tests for the FrameBuilder composition API — verifying that frames built
/// via <see cref="FrameStack"/> and layer types produce valid output with correct
/// checksums and lengths.
/// </summary>
internal sealed class FrameTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF];
    private static readonly IPv6Address _SrcIpv6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly IPv6Address _DstIpv6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);

    #region Ethernet + IPv4 + UDP

    [Test]
    public async Task EthIPv4Udp_ProducesCorrectLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(5353, 5353, computeChecksum: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 20 + 8 + 4); // Eth + IP + UDP + payload
    }

    [Test]
    public async Task EthIPv4Udp_HasValidIPv4Checksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(1234, 80, computeChecksum: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out _);

        // Verify IPv4 header checksum
        int ipOffset = EthernetHeader.Size;
        ushort verification = ChecksumUtils.IPv4Header(buffer.AsSpan(ipOffset, 20));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task EthIPv4Udp_HasCorrectUdpLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(1234, 80, computeChecksum: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out _);

        int udpOffset = EthernetHeader.Size + IPv4Header.Size;
        ushort udpLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(udpOffset + 4, 2));
        await Assert.That(udpLength).IsEqualTo((ushort)(UdpHeader.Size + _Payload.Length));
    }

    #endregion

    #region Ethernet + IPv4 + TCP

    [Test]
    public async Task EthIPv4Tcp_ProducesCorrectLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(12345, 80, seqNum: 1000, flags: TcpFlags.PshAck);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 20 + 20 + 4);
    }

    [Test]
    public async Task EthIPv4Tcp_HasValidTcpChecksum()
    {
        IPv4Address srcIp = new(0xC0A80001);
        IPv4Address dstIp = new(0xC0A80002);

        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(srcIp, dstIp);
        TcpLayer tcp = new(12345, 80, seqNum: 1000);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out _);

        int tcpOffset = EthernetHeader.Size + IPv4Header.Size;
        int totalLen = 14 + 20 + 20 + _Payload.Length;

        Span<byte> srcBytes = stackalloc byte[4];
        Span<byte> dstBytes = stackalloc byte[4];
        srcIp.ToBytes(srcBytes);
        dstIp.ToBytes(dstBytes);

        ReadOnlySpan<byte> segment = buffer.AsSpan(tcpOffset, totalLen - tcpOffset);
        ushort verification = ChecksumUtils.PseudoHeaderIPv4(srcBytes, dstBytes, IpProtocols.Tcp, segment);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region Ethernet + IPv6 + UDP

    [Test]
    public async Task EthIPv6Udp_ProducesCorrectLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_SrcIpv6, _DstIpv6);
        UdpLayer udp = new(1234, 80);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 40 + 8 + 4);
    }

    [Test]
    public async Task EthIPv6Udp_HasValidUdpChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_SrcIpv6, _DstIpv6);
        UdpLayer udp = new(1234, 80);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out _);

        int udpOffset = EthernetHeader.Size + IPv6Header.Size;
        int totalLen = 14 + 40 + 8 + _Payload.Length;

        ReadOnlySpan<byte> segment = buffer.AsSpan(udpOffset, totalLen - udpOffset);
        Span<byte> srcBytes = stackalloc byte[16];
        Span<byte> dstBytes = stackalloc byte[16];
        _SrcIpv6.ToBytes(srcBytes);
        _DstIpv6.ToBytes(dstBytes);
        ushort verification = ChecksumUtils.PseudoHeaderIPv6(
            srcBytes, dstBytes, IpProtocols.Udp, segment);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region Ethernet + IPv6 + TCP

    [Test]
    public async Task EthIPv6Tcp_ProducesCorrectLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_SrcIpv6, _DstIpv6);
        TcpLayer tcp = new(443, 12345, seqNum: 5000, flags: TcpFlags.SynAck);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 40 + 20);
    }

    #endregion

    #region Ethernet + ARP

    [Test]
    public async Task EthArp_ProducesCorrectLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        ArpLayer arp = new(ArpWriter.OpcodeRequest,
            _SrcMac, new IPv4Address(0xC0A80001).RawValue,
            MacAddress.FromBytes([0, 0, 0, 0, 0, 0]), new IPv4Address(0xC0A80002).RawValue);

        byte[] buffer = new byte[eth.HeaderSize + arp.HeaderSize];
        FB.FrameStack.Start(eth).Then(arp).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 28);
    }

    [Test]
    public async Task EthArp_HasCorrectEtherType()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        ArpLayer arp = new(ArpWriter.OpcodeRequest,
            _SrcMac, new IPv4Address(0xC0A80001).RawValue,
            MacAddress.FromBytes([0, 0, 0, 0, 0, 0]), new IPv4Address(0xC0A80002).RawValue);

        byte[] buffer = new byte[eth.HeaderSize + arp.HeaderSize];
        FB.FrameStack.Start(eth).Then(arp).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo(EtherTypes.Arp);
    }

    #endregion

    #region ICMP

    [Test]
    public async Task EthIPv4IcmpEchoRequest_HasValidIcmpChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        IcmpV4EchoLayer icmp = new(identifier: 1, sequenceNumber: 1, isReply: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + icmp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().Build(_Payload).MoveNext(buffer, out _);

        int icmpOffset = EthernetHeader.Size + IPv4Header.Size;
        int totalLen = 14 + 20 + 8 + _Payload.Length;

        ReadOnlySpan<byte> icmpData = buffer.AsSpan(icmpOffset, totalLen - icmpOffset);
        ushort verification = ChecksumUtils.OnesComplement(icmpData);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task EthIPv6IcmpV6EchoRequest_HasValidChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(_SrcIpv6, _DstIpv6);
        IcmpV6EchoLayer icmp = new(identifier: 1, sequenceNumber: 1, isReply: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + icmp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        int icmpOffset = EthernetHeader.Size + IPv6Header.Size;
        int totalLen = 14 + 40 + 8;

        ReadOnlySpan<byte> segment = buffer.AsSpan(icmpOffset, totalLen - icmpOffset);
        Span<byte> srcBytes = stackalloc byte[16];
        Span<byte> dstBytes = stackalloc byte[16];
        _SrcIpv6.ToBytes(srcBytes);
        _DstIpv6.ToBytes(dstBytes);
        ushort verification = ChecksumUtils.PseudoHeaderIPv6(
            srcBytes, dstBytes, IpProtocols.IcmpV6, segment);
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region SocketCAN

    [Test]
    public async Task SocketCan_ProducesCorrectLength()
    {
        SocketCanLayer layer = new(0x123, new byte[] { 0x01, 0x02, 0x03 });
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(SocketCanHeader.HeaderSize + SocketCanHeader.MaxDataLength);
    }

    [Test]
    public async Task SocketCanFd_ProducesCorrectLength()
    {
        SocketCanFdLayer layer = new(0x456, new byte[] { 0x01, 0x02, 0x03, 0x04 }, brs: true);
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(SocketCanFdHeader.HeaderSize + SocketCanFdHeader.MaxDataLength);
    }

    [Test]
    public async Task SocketCanXl_ProducesCorrectLength()
    {
        SocketCanXlLayer layer = new(0x7AB, new byte[] { 0x01, 0x02, 0x03 });
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(SocketCanXlHeader.HeaderSize + SocketCanXlHeader.MaxDataLength);
    }

    [Test]
    public async Task SocketCanXl_DataIsWrittenCorrectly()
    {
        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
        SocketCanXlLayer layer = new(0x100, data);
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        // Data starts after 12-byte CAN XL header
        await Assert.That(buffer[SocketCanXlHeader.HeaderSize]).IsEqualTo((byte)0xDE);
        await Assert.That(buffer[SocketCanXlHeader.HeaderSize + 1]).IsEqualTo((byte)0xAD);
        await Assert.That(buffer[SocketCanXlHeader.HeaderSize + 2]).IsEqualTo((byte)0xBE);
        await Assert.That(buffer[SocketCanXlHeader.HeaderSize + 3]).IsEqualTo((byte)0xEF);

        // Remaining data area should be zero-padded
        await Assert.That(buffer[SocketCanXlHeader.HeaderSize + 4]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task SocketCanXl_XlfFlagAlwaysSet()
    {
        SocketCanXlLayer layer = new(0x100, Array.Empty<byte>());
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        // Flags byte is at offset 4 in the CAN XL header
        byte flags = buffer[4];
        await Assert.That((flags & SocketCanXlHeader.XlfFlag) != 0).IsTrue();
    }

    [Test]
    public async Task SocketCanXl_SecFlagConditional()
    {
        SocketCanXlLayer layerWithSec = new(0x100, Array.Empty<byte>(), sec: true);
        SocketCanXlLayer layerWithoutSec = new(0x100, Array.Empty<byte>(), sec: false);
        byte[] bufferWithSec = new byte[layerWithSec.HeaderSize];
        byte[] bufferWithoutSec = new byte[layerWithoutSec.HeaderSize];
        FB.FrameStack.Start(layerWithSec).CreateWithFixedValues().Build([]).MoveNext(bufferWithSec, out _);
        FB.FrameStack.Start(layerWithoutSec).CreateWithFixedValues().Build([]).MoveNext(bufferWithoutSec, out _);

        // Flags byte is at offset 4 in the CAN XL header
        await Assert.That((bufferWithSec[4] & SocketCanXlHeader.SecFlag) != 0).IsTrue();
        await Assert.That((bufferWithoutSec[4] & SocketCanXlHeader.SecFlag) == 0).IsTrue();
    }

    [Test]
    public async Task SocketCanXl_LenFieldMatchesDataLength()
    {
        byte[] data = new byte[500];
        SocketCanXlLayer layer = new(0x100, data);
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        // Len field is at offset 6-7 in the CAN XL header (little-endian)
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6, 2));
        await Assert.That((int)len).IsEqualTo(500);
    }

    [Test]
    public async Task SocketCanXl_AfFieldIsWrittenCorrectly()
    {
        SocketCanXlLayer layer = new(0x100, Array.Empty<byte>(), af: 0xDEADBEEF);
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        // AF field is at offset 8-11 in the CAN XL header (little-endian)
        uint af = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
        await Assert.That(af).IsEqualTo(0xDEADBEEFu);
    }

    [Test]
    public async Task SocketCan_DataIsWrittenCorrectly()
    {
        byte[] data = [0xAA, 0xBB, 0xCC];
        SocketCanLayer layer = new(0x100, data);
        byte[] buffer = new byte[layer.HeaderSize];
        FB.FrameStack.Start(layer).CreateWithFixedValues().Build([]).MoveNext(buffer, out _);

        // Data starts after 8-byte header
        await Assert.That(buffer[SocketCanHeader.HeaderSize]).IsEqualTo((byte)0xAA);
        await Assert.That(buffer[SocketCanHeader.HeaderSize + 1]).IsEqualTo((byte)0xBB);
        await Assert.That(buffer[SocketCanHeader.HeaderSize + 2]).IsEqualTo((byte)0xCC);

        // Remaining data area should be zero-padded
        await Assert.That(buffer[SocketCanHeader.HeaderSize + 3]).IsEqualTo((byte)0);
    }

    #endregion

    #region Size Helpers

    [Test]
    public async Task SizeHelpers_ProduceCorrectSizes()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip4 = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        IPv6Layer ip6 = new(_SrcIpv6, _DstIpv6);
        UdpLayer udp = new(1234, 80);
        TcpLayer tcp = new(1234, 80);
        ArpLayer arp = new(ArpWriter.OpcodeRequest,
            _SrcMac, 0xC0A80001u,
            MacAddress.FromBytes([0, 0, 0, 0, 0, 0]), 0xC0A80002u);
        IcmpV4EchoLayer icmpV4 = new();
        IcmpV6EchoLayer icmpV6 = new();
        SocketCanLayer can = new(0, Array.Empty<byte>());
        SocketCanFdLayer canFd = new(0, Array.Empty<byte>());
        SocketCanXlLayer canXl = new(0, Array.Empty<byte>());

        await Assert.That(eth.HeaderSize + ip4.HeaderSize + udp.HeaderSize).IsEqualTo(14 + 20 + 8);
        await Assert.That(eth.HeaderSize + ip4.HeaderSize + tcp.HeaderSize).IsEqualTo(14 + 20 + 20);
        await Assert.That(eth.HeaderSize + ip6.HeaderSize + udp.HeaderSize).IsEqualTo(14 + 40 + 8);
        await Assert.That(eth.HeaderSize + ip6.HeaderSize + tcp.HeaderSize).IsEqualTo(14 + 40 + 20);
        await Assert.That(eth.HeaderSize + arp.HeaderSize).IsEqualTo(14 + 28);
        await Assert.That(eth.HeaderSize + ip4.HeaderSize + icmpV4.HeaderSize).IsEqualTo(14 + 20 + 8);
        await Assert.That(eth.HeaderSize + ip6.HeaderSize + icmpV6.HeaderSize).IsEqualTo(14 + 40 + 8);
        await Assert.That(can.HeaderSize).IsEqualTo(8 + 8);
        await Assert.That(canFd.HeaderSize).IsEqualTo(8 + 64);
        await Assert.That(canXl.HeaderSize).IsEqualTo(12 + 2048);
    }

    #endregion

    #region NoPayload

    [Test]
    public async Task EthIPv4Udp_NoPayload_WorksCorrectly()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(1234, 80, computeChecksum: false);

        byte[] buffer = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buffer, out int len);

        await Assert.That(len).IsEqualTo(14 + 20 + 8);

        // UDP Length should be 8 (header only)
        int udpOffset = EthernetHeader.Size + IPv4Header.Size;
        ushort udpLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(udpOffset + 4, 2));
        await Assert.That(udpLength).IsEqualTo((ushort)8);
    }

    #endregion
}
