// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests.Stacks;

/// <summary>
/// Tests for <see cref="FrameStack"/> auto-inference of EtherType and IP Protocol fields.
/// </summary>
internal sealed class FrameStackAutoInferenceTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    #region EtherType inference

    [Test]
    public async Task EthIPv4_SetsEtherType0x0800()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // EtherType at bytes 12-13
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo(EtherTypes.IPv4);
    }

    [Test]
    public async Task EthIPv6_SetsEtherType0x86DD()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Address srcIpv6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        IPv6Address dstIpv6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);
        IPv6Layer ip6 = new(srcIpv6, dstIpv6);
        byte[] buf = new byte[eth.HeaderSize + ip6.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip6).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo(EtherTypes.IPv6);
    }

    [Test]
    public async Task EthArp_SetsEtherType0x0806()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        ArpLayer arp = new(1, _SrcMac, new IPv4Address(0xC0A80001), _DstMac, new IPv4Address(0xC0A80002));
        byte[] buf = new byte[eth.HeaderSize + arp.HeaderSize];
        FB.FrameStack.Start(eth).Then(arp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo(EtherTypes.Arp);
    }

    #endregion

    #region IP Protocol inference

    [Test]
    public async Task EthIPv4Tcp_SetsProtocol6()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // IP Protocol field at Eth(14) + IP(9) = byte 23
        byte protocol = buf[14 + 9];
        await Assert.That(protocol).IsEqualTo(IpProtocols.Tcp);
    }

    [Test]
    public async Task EthIPv4Udp_SetsProtocol17()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        UdpLayer udp = new(5353, 5353);
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + udp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        byte protocol = buf[14 + 9];
        await Assert.That(protocol).IsEqualTo(IpProtocols.Udp);
    }

    [Test]
    public async Task EthIPv4Icmp_SetsProtocol1()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        IcmpV4EchoLayer icmp = new();
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + icmp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        byte protocol = buf[14 + 9];
        await Assert.That(protocol).IsEqualTo(IpProtocols.Icmp);
    }

    [Test]
    public async Task EthIPv6Tcp_SetsNextHeader6()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Address srcIpv6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        IPv6Address dstIpv6 = IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);
        IPv6Layer ip6 = new(srcIpv6, dstIpv6);
        TcpLayer tcp = new(1234, 80);
        byte[] buf = new byte[eth.HeaderSize + ip6.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(eth).Then(ip6).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // IPv6 NextHeader at Eth(14) + 6 = byte 20
        byte nextHeader = buf[14 + 6];
        await Assert.That(nextHeader).IsEqualTo(IpProtocols.Tcp);
    }

    #endregion

    #region VLAN EtherType chaining

    [Test]
    public async Task EthVlanIPv4_SetsVlanEtherTypeAndInnerEtherType()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        VlanLayer vlan = new(100);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        byte[] buf = new byte[eth.HeaderSize + vlan.HeaderSize + ip.HeaderSize];
        FB.FrameStack.Start(eth).Then(vlan).Then(ip).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // Outer EtherType at bytes 12-13 should be 0x8100 (VLAN Tagged)
        ushort outerType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(12, 2));
        await Assert.That(outerType).IsEqualTo(EtherTypes.VlanTagged);

        // Inner EtherType at VLAN offset + 2 = 14 + 2 = 16
        ushort innerType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(16, 2));
        await Assert.That(innerType).IsEqualTo(EtherTypes.IPv4);
    }

    #endregion

    #region GetSize correctness

    [Test]
    public async Task GetSize_MatchesActualBuildLength_3Layer()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        int predicted = eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + payload.Length;
        byte[] buf = new byte[predicted];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(payload).MoveNext(buf, out int actual);

        await Assert.That(actual).IsEqualTo(predicted);
    }

    [Test]
    public async Task GetSize_MatchesActualBuildLength_2Layer()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        ArpLayer arp = new(1, _SrcMac, new IPv4Address(0xC0A80001), _DstMac, new IPv4Address(0xC0A80002));

        int predicted = eth.HeaderSize + arp.HeaderSize;
        byte[] buf = new byte[predicted];
        FB.FrameStack.Start(eth).Then(arp).CreateWithFixedValues().Build([]).MoveNext(buf, out int actual);

        await Assert.That(actual).IsEqualTo(predicted);
    }

    #endregion
}
