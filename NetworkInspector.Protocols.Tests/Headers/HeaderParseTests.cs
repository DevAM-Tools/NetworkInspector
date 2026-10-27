// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The handwritten fixed-header parsers read every field from its wire offset: each test feeds bytes with a distinct
/// value in every field (bit fields at non-zero values), so a swapped offset or a shift that is off by one fails.
/// A buffer one byte short must fail with a default value; a longer buffer must read only the fixed header.
/// </summary>
internal sealed class HeaderParseTests
{
    #region Wire samples

    // 4 | IHL 5 | DSCP 0x2E | ECN 1 | total 0x1234 | id 0x5678 | flags R=1 DF=0 MF=1 + offset 0x0ABC | ttl 0x40 |
    // protocol 0x11 | checksum 0x9ABC | src 192.168.0.1 | dst 10.0.0.2
    private const string _Ipv4Hex = "45B9" + "1234" + "5678" + "AABC" + "4011" + "9ABC" + "C0A80001" + "0A000002";

    // version 6 | traffic class 0xAB | flow label 0x5CDEF | payload 0x0102 | next 0x3A | hop 0x40 | src | dst
    private const string _Ipv6Hex = "6AB5CDEF" + "0102" + "3A40"
        + "2001" + "0DB8" + "0001" + "0002" + "0003" + "0004" + "0005" + "0006"
        + "FE80" + "0000" + "0000" + "0000" + "1112" + "1314" + "1516" + "1718";

    // ports 8080 and 443 | seq | ack | data offset 5, reserved 5, NS 1 | flags 0x12 | window | checksum | urgent
    private const string _TcpHex = "1F90" + "01BB" + "DEADBEEF" + "CAFEBABE" + "5B" + "12" + "7210" + "ABCD" + "0102";

    private const string _UdpHex = "0035" + "C001" + "0020" + "BEEF";

    // PCP 5 | DEI 1 | VID 0xABC | EtherType IPv4
    private const string _VlanHex = "BABC" + "0800";

    #endregion

    #region IPv4

    [Test]
    public async Task Ipv4ParsesEveryField()
    {
        byte[] bytes = Convert.FromHexString(_Ipv4Hex);

        bool ok = IPv4Header.TryParse(bytes, out IPv4Header header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.Version).IsEqualTo((byte)4);
        await Assert.That(header.Ihl).IsEqualTo((byte)5);
        await Assert.That(header.HeaderLength).IsEqualTo(20);
        await Assert.That(header.Dscp).IsEqualTo((byte)0x2E);
        await Assert.That(header.Ecn).IsEqualTo((byte)1);
        await Assert.That(header.TotalLength).IsEqualTo((ushort)0x1234);
        await Assert.That(header.Identification).IsEqualTo((ushort)0x5678);
        await Assert.That(header.ReservedFlag).IsEqualTo((byte)1);
        await Assert.That(header.DontFragment).IsEqualTo((byte)0);
        await Assert.That(header.MoreFragments).IsEqualTo((byte)1);
        await Assert.That(header.FragmentOffset).IsEqualTo((ushort)0x0ABC);
        await Assert.That(header.Ttl).IsEqualTo((byte)0x40);
        await Assert.That(header.Protocol).IsEqualTo((byte)0x11);
        await Assert.That(header.Checksum).IsEqualTo((ushort)0x9ABC);
        await Assert.That(header.SrcAddr).IsEqualTo(0xC0A80001u);
        await Assert.That(header.DstAddr).IsEqualTo(0x0A000002u);
    }

    [Test]
    public async Task Ipv4OneByteShortFailsWithDefault()
    {
        byte[] bytes = Convert.FromHexString(_Ipv4Hex)[..(IPv4Header.MinHeaderSize - 1)];

        bool ok = IPv4Header.TryParse(bytes, out IPv4Header header);

        await Assert.That(ok).IsFalse();
        await Assert.That(header).IsEqualTo(default(IPv4Header));
    }

    [Test]
    public async Task Ipv4LongerInputReadsOnlyTheFixedHeader()
    {
        byte[] bytes = [.. Convert.FromHexString(_Ipv4Hex), 0xFF, 0xFF, 0xFF, 0xFF];

        bool ok = IPv4Header.TryParse(bytes, out IPv4Header header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.DstAddr).IsEqualTo(0x0A000002u);
    }

    #endregion

    #region IPv6

    [Test]
    public async Task Ipv6ParsesEveryField()
    {
        byte[] bytes = Convert.FromHexString(_Ipv6Hex);

        bool ok = IPv6Header.TryParse(bytes, out IPv6Header header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.Version).IsEqualTo((byte)6);
        await Assert.That(header.TrafficClass).IsEqualTo((byte)0xAB);
        await Assert.That(header.FlowLabel).IsEqualTo(0x5CDEFu);
        await Assert.That(header.PayloadLength).IsEqualTo((ushort)0x0102);
        await Assert.That(header.NextHeader).IsEqualTo((byte)0x3A);
        await Assert.That(header.HopLimit).IsEqualTo((byte)0x40);
        await Assert.That(header.SrcHi).IsEqualTo(0x2001_0DB8_0001_0002UL);
        await Assert.That(header.SrcLo).IsEqualTo(0x0003_0004_0005_0006UL);
        await Assert.That(header.DstHi).IsEqualTo(0xFE80_0000_0000_0000UL);
        await Assert.That(header.DstLo).IsEqualTo(0x1112_1314_1516_1718UL);
    }

    [Test]
    public async Task Ipv6OneByteShortFailsWithDefault()
    {
        byte[] bytes = Convert.FromHexString(_Ipv6Hex)[..(IPv6Header.HeaderSize - 1)];

        bool ok = IPv6Header.TryParse(bytes, out IPv6Header header);

        await Assert.That(ok).IsFalse();
        await Assert.That(header).IsEqualTo(default(IPv6Header));
    }

    [Test]
    public async Task Ipv6LongerInputReadsOnlyTheFixedHeader()
    {
        byte[] bytes = [.. Convert.FromHexString(_Ipv6Hex), 0xFF, 0xFF];

        bool ok = IPv6Header.TryParse(bytes, out IPv6Header header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.DstLo).IsEqualTo(0x1112_1314_1516_1718UL);
    }

    #endregion

    #region TCP

    [Test]
    public async Task TcpParsesEveryField()
    {
        byte[] bytes = Convert.FromHexString(_TcpHex);

        bool ok = TcpHeader.TryParse(bytes, out TcpHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.SrcPort).IsEqualTo((ushort)8080);
        await Assert.That(header.DstPort).IsEqualTo((ushort)443);
        await Assert.That(header.SeqNumber).IsEqualTo(0xDEADBEEFu);
        await Assert.That(header.AckNumber).IsEqualTo(0xCAFEBABEu);
        await Assert.That(header.DataOffset).IsEqualTo((byte)5);
        await Assert.That(header.HeaderLength).IsEqualTo(20);
        await Assert.That(header.Reserved).IsEqualTo((byte)5);
        await Assert.That(header.NsFlag).IsEqualTo((byte)1);
        await Assert.That(header.Flags).IsEqualTo((byte)0x12);
        await Assert.That(header.WindowSize).IsEqualTo((ushort)0x7210);
        await Assert.That(header.Checksum).IsEqualTo((ushort)0xABCD);
        await Assert.That(header.UrgentPointer).IsEqualTo((ushort)0x0102);
    }

    [Test]
    public async Task TcpOneByteShortFailsWithDefault()
    {
        byte[] bytes = Convert.FromHexString(_TcpHex)[..(TcpHeader.MinSize - 1)];

        bool ok = TcpHeader.TryParse(bytes, out TcpHeader header);

        await Assert.That(ok).IsFalse();
        await Assert.That(header).IsEqualTo(default(TcpHeader));
    }

    [Test]
    public async Task TcpLongerInputReadsOnlyTheFixedHeader()
    {
        byte[] bytes = [.. Convert.FromHexString(_TcpHex), 0x02, 0x04, 0x05, 0xB4];

        bool ok = TcpHeader.TryParse(bytes, out TcpHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.UrgentPointer).IsEqualTo((ushort)0x0102);
    }

    #endregion

    #region UDP

    [Test]
    public async Task UdpParsesEveryField()
    {
        byte[] bytes = Convert.FromHexString(_UdpHex);

        bool ok = UdpHeader.TryParse(bytes, out UdpHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.SrcPort).IsEqualTo((ushort)0x0035);
        await Assert.That(header.DstPort).IsEqualTo((ushort)0xC001);
        await Assert.That(header.Length).IsEqualTo((ushort)0x0020);
        await Assert.That(header.Checksum).IsEqualTo((ushort)0xBEEF);
    }

    [Test]
    public async Task UdpOneByteShortFailsWithDefault()
    {
        byte[] bytes = Convert.FromHexString(_UdpHex)[..(UdpHeader.HeaderSize - 1)];

        bool ok = UdpHeader.TryParse(bytes, out UdpHeader header);

        await Assert.That(ok).IsFalse();
        await Assert.That(header).IsEqualTo(default(UdpHeader));
    }

    [Test]
    public async Task UdpLongerInputReadsOnlyTheFixedHeader()
    {
        byte[] bytes = [.. Convert.FromHexString(_UdpHex), 0x01, 0x02, 0x03];

        bool ok = UdpHeader.TryParse(bytes, out UdpHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.Checksum).IsEqualTo((ushort)0xBEEF);
    }

    #endregion

    #region VLAN

    [Test]
    public async Task VlanParsesEveryField()
    {
        byte[] bytes = Convert.FromHexString(_VlanHex);

        bool ok = VlanHeader.TryParse(bytes, out VlanHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.Priority).IsEqualTo((byte)5);
        await Assert.That(header.Dei).IsEqualTo((byte)1);
        await Assert.That(header.VlanId).IsEqualTo((ushort)0x0ABC);
        await Assert.That(header.EtherType).IsEqualTo((ushort)0x0800);
    }

    [Test]
    public async Task VlanOneByteShortFailsWithDefault()
    {
        byte[] bytes = Convert.FromHexString(_VlanHex)[..(VlanHeader._HeaderSize - 1)];

        bool ok = VlanHeader.TryParse(bytes, out VlanHeader header);

        await Assert.That(ok).IsFalse();
        await Assert.That(header).IsEqualTo(default(VlanHeader));
    }

    [Test]
    public async Task VlanLongerInputReadsOnlyTheFixedHeader()
    {
        byte[] bytes = [.. Convert.FromHexString(_VlanHex), 0x45, 0x00];

        bool ok = VlanHeader.TryParse(bytes, out VlanHeader header);

        await Assert.That(ok).IsTrue();
        await Assert.That(header.EtherType).IsEqualTo((ushort)0x0800);
    }

    #endregion
}
