// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests.Stacks;

/// <summary>
/// Tests that <see cref="FrameStack"/> produces valid checksums
/// for IPv4, TCP, UDP, ICMPv4, and ICMPv6 frames.
/// </summary>
internal sealed class ChecksumTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF];
    private static readonly IPv6Address _SrcIpv6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly IPv6Address _DstIpv6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);

    #region IPv4 header checksum

    [Test]
    public async Task EthIPv4Tcp_HasValidIPv4Checksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out _);

        // Verify IPv4 header checksum (verification should be 0)
        int ipOffset = EthernetHeader.Size;
        ushort verification = ChecksumUtils.IPv4Header(buf.AsSpan(ipOffset, IPv4Header.Size));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task EthIPv4Udp_HasValidIPv4Checksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(5353, 5353);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out _);

        int ipOffset = EthernetHeader.Size;
        ushort verification = ChecksumUtils.IPv4Header(buf.AsSpan(ipOffset, IPv4Header.Size));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region TCP checksum

    [Test]
    public async Task EthIPv4Tcp_HasValidTcpChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out int total);

        // Read source and destination IP for pseudo-header verification
        int ipOffset = EthernetHeader.Size;
        int tcpOffset = ipOffset + IPv4Header.Size;
        ReadOnlySpan<byte> srcIp = buf.AsSpan(ipOffset + 12, 4);
        ReadOnlySpan<byte> dstIp = buf.AsSpan(ipOffset + 16, 4);

        ushort verification = ChecksumUtils.PseudoHeaderIPv4(
            srcIp, dstIp, IpProtocols.Tcp, buf.AsSpan(tcpOffset, total - tcpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task EthIPv6Tcp_HasValidTcpChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip6 = new(_SrcIpv6, _DstIpv6);
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);
        byte[] buf = new byte[eth.HeaderSize + ip6.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip6).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out int total);

        int ipOffset = EthernetHeader.Size;
        int tcpOffset = ipOffset + IPv6Header.Size;
        ReadOnlySpan<byte> srcIp = buf.AsSpan(ipOffset + 8, 16);
        ReadOnlySpan<byte> dstIp = buf.AsSpan(ipOffset + 24, 16);

        ushort verification = ChecksumUtils.PseudoHeaderIPv6(
            srcIp, dstIp, IpProtocols.Tcp, buf.AsSpan(tcpOffset, total - tcpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region UDP checksum

    [Test]
    public async Task EthIPv4Udp_HasValidUdpChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(5353, 5353);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out int total);

        int ipOffset = EthernetHeader.Size;
        int udpOffset = ipOffset + IPv4Header.Size;
        ReadOnlySpan<byte> srcIp = buf.AsSpan(ipOffset + 12, 4);
        ReadOnlySpan<byte> dstIp = buf.AsSpan(ipOffset + 16, 4);

        ushort verification = ChecksumUtils.PseudoHeaderIPv4(
            srcIp, dstIp, IpProtocols.Udp, buf.AsSpan(udpOffset, total - udpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region ICMPv4 checksum

    [Test]
    public async Task EthIPv4IcmpV4Echo_HasValidChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        IcmpV4EchoLayer icmp = new(1, 1);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + icmp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out int total);

        int icmpOffset = EthernetHeader.Size + IPv4Header.Size;
        ushort verification = ChecksumUtils.IPv4Header(buf.AsSpan(icmpOffset, total - icmpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region ICMPv6 checksum

    [Test]
    public async Task EthIPv6IcmpV6Echo_HasValidChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip6 = new(_SrcIpv6, _DstIpv6);
        IcmpV6EchoLayer icmp = new(1, 1);
        byte[] buf = new byte[eth.HeaderSize + ip6.HeaderSize + icmp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip6).Then(icmp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out int total);

        int icmpOffset = EthernetHeader.Size + IPv6Header.Size;
        ReadOnlySpan<byte> srcIp = buf.AsSpan(EthernetHeader.Size + 8, 16);
        ReadOnlySpan<byte> dstIp = buf.AsSpan(EthernetHeader.Size + 24, 16);

        ushort verification = ChecksumUtils.PseudoHeaderIPv6(
            srcIp, dstIp, IpProtocols.IcmpV6, buf.AsSpan(icmpOffset, total - icmpOffset));
        await Assert.That(verification).IsEqualTo((ushort)0);
    }

    #endregion

    #region IPv4 TotalLength

    [Test]
    public async Task EthIPv4Tcp_HasCorrectTotalLength()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(buf, out _);

        int ipOffset = EthernetHeader.Size;
        ushort totalLength = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(ipOffset + 2, 2));
        // IP(20) + TCP(20) + payload(4)
        await Assert.That(totalLength).IsEqualTo((ushort)(IPv4Header.Size + TcpHeader.Size + _Payload.Length));
    }

    #endregion
}
