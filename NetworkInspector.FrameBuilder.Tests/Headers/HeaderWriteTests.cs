// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Headers;

/// <summary>
/// The handwritten header writers emit the exact big-endian wire bytes: every header is filled with a distinct value
/// per field (bit-packed fields at non-zero values) and compared with a hand-written byte string, so a swapped field or
/// a wrong offset fails. A destination one byte short must report failure, write 0 bytes, and leave the destination untouched.
/// </summary>
internal sealed class HeaderWriteTests
{
    #region Helpers

    private delegate bool WriteHeader(Span<byte> destination, out int bytesWritten);

    private static readonly MacAddress _MacA = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _MacB = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly IPv4Address _Ip4A = IPv4Address.FromBytes([192, 168, 0, 1]);
    private static readonly IPv4Address _Ip4B = IPv4Address.FromBytes([10, 0, 0, 2]);
    private static readonly IPv6Address _Ip6A = IPv6Address.FromBytes(Convert.FromHexString("20010DB8000000000000000000000001"));
    private static readonly IPv6Address _Ip6B = IPv6Address.FromBytes(Convert.FromHexString("FE800000000000000000000000000002"));

    /// <summary>Checks the exact-size write against <paramref name="expectedHex"/> and the one-byte-short failure.</summary>
    private static async Task _AssertWire(string expectedHex, int size, WriteHeader write)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        byte[] exact = new byte[size];
        byte[] tooShort = new byte[size - 1];
        Array.Fill(tooShort, (byte)0xEE);

        bool ok = write(exact, out int written);
        bool shortOk = write(tooShort, out int shortWritten);

        await Assert.That(expected.Length).IsEqualTo(size);
        await Assert.That(ok).IsTrue();
        await Assert.That(written).IsEqualTo(size);
        await Assert.That(exact.AsSpan().SequenceEqual(expected)).IsTrue();
        await Assert.That(shortOk).IsFalse();
        await Assert.That(shortWritten).IsEqualTo(0);
        await Assert.That(tooShort.AsSpan().IndexOfAnyExcept((byte)0xEE) < 0).IsTrue();
    }

    #endregion

    #region Tests

    [Test]
    public async Task ArpWritesWireBytes()
    {
        ArpHeader header = ArpHeader.Create(2, _MacA, _Ip4A, _MacB, _Ip4B);

        await _AssertWire(
            "0001" + "0800" + "06" + "04" + "0002" + "AABBCCDDEEFF" + "C0A80001" + "001122334455" + "0A000002",
            ArpHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task EthernetWritesWireBytes()
    {
        EthernetHeader header = new() { DstMac = _MacA, SrcMac = _MacB, EtherType = 0x86DD };

        await _AssertWire(
            "AABBCCDDEEFF" + "001122334455" + "86DD",
            EthernetHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task Ipv4WritesWireBytes()
    {
        // Reserved, DF and MF all set with fragment offset 0x0ABC: flags/fragment word 0xEABC.
        IPv4Header header = IPv4Header.Create(
            _Ip4A, _Ip4B, protocol: 17, ttl: 0x40, identification: 0x1234, dontFragment: true, moreFragments: true,
            fragmentOffset: 0x0ABC, ihl: 5, typeOfService: 0xB8, reservedFlag: true);

        await _AssertWire(
            "45B8" + "0000" + "1234" + "EABC" + "4011" + "0000" + "C0A80001" + "0A000002",
            IPv4Header.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task Ipv6FragmentExtensionWritesWireBytes()
    {
        IPv6FragmentExtensionHeader header = new()
        {
            NextHeader = 0x11,
            Reserved = 0x00,
            FragmentOffsetAndFlags = 0x1239,
            Identification = 0xDEADBEEF,
        };

        await _AssertWire(
            "1100" + "1239" + "DEADBEEF",
            IPv6FragmentExtensionHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task Ipv6WritesWireBytes()
    {
        IPv6Header header = IPv6Header.Create(_Ip6A, _Ip6B, nextHeader: 0x11, hopLimit: 0x40);

        await _AssertWire(
            "60000000" + "0000" + "11" + "40" + "20010DB8000000000000000000000001" + "FE800000000000000000000000000002",
            IPv6Header.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task Ipv6OptionsExtensionWritesWireBytes()
    {
        IPv6OptionsExtensionHeader header = new()
        {
            NextHeader = 0x3A,
            HdrExtLen = 0x00,
            Data0 = 1,
            Data1 = 2,
            Data2 = 3,
            Data3 = 4,
            Data4 = 5,
            Data5 = 6,
        };

        await _AssertWire(
            "3A00" + "010203040506",
            IPv6OptionsExtensionHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task IcmpV4WritesWireBytes()
    {
        IcmpV4Header header = IcmpV4Header.EchoRequest(identifier: 0x1234, sequenceNumber: 0x5678);

        await _AssertWire(
            "0800" + "0000" + "1234" + "5678",
            IcmpV4Header.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task IcmpV6WritesWireBytes()
    {
        IcmpV6Header header = IcmpV6Header.EchoRequest(identifier: 0x1234, sequenceNumber: 0x5678);

        await _AssertWire(
            "8000" + "0000" + "1234" + "5678",
            IcmpV6Header.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task SocketCanWritesWireBytes()
    {
        SocketCanHeader header = new()
        {
            CanId = SocketCanHeader.EffFlag | 0x123u,
            Dlc = 3,
            Pad = 0,
            Res0 = 0,
            Res1 = 0,
            Data0 = 0x11,
            Data1 = 0x22,
            Data2 = 0x33,
            Data3 = 0x44,
            Data4 = 0x55,
            Data5 = 0x66,
            Data6 = 0x77,
            Data7 = 0x88,
        };

        await _AssertWire(
            "80000123" + "03" + "00" + "00" + "00" + "1122334455667788",
            SocketCanHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task SomeIpWritesWireBytes()
    {
        SomeIpHeader header = new()
        {
            ServiceId = 0x1234,
            MethodId = 0x8001,
            Length = 0x00000010,
            ClientId = 0x0001,
            SessionId = 0x0002,
            ProtocolVersion = 0x01,
            InterfaceVersion = 0x02,
            MessageType = 0x80,
            ReturnCode = 0x0E,
        };

        await _AssertWire(
            "1234" + "8001" + "00000010" + "0001" + "0002" + "01" + "02" + "80" + "0E",
            SomeIpHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task TcpWritesWireBytes()
    {
        // Data offset 5, reserved 5, NS set, flags 0x12: offset/flags word 0x5B12.
        TcpHeader header = TcpHeader.Create(
            srcPort: 8080, dstPort: 443, seqNum: 0xDEADBEEF, ackNum: 0xCAFEBABE, flags: 0x12, windowSize: 0x7210,
            dataOffsetWords: 5, urgentPointer: 0x0102, reservedNibble: 5, nsFlag: true);

        await _AssertWire(
            "1F90" + "01BB" + "DEADBEEF" + "CAFEBABE" + "5B12" + "7210" + "0000" + "0102",
            TcpHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task UdpWritesWireBytes()
    {
        UdpHeader header = new() { SrcPort = 0x0035, DstPort = 0xC001, Length = 0x0020, Checksum = 0xBEEF };

        await _AssertWire(
            "0035" + "C001" + "0020" + "BEEF",
            UdpHeader.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    [Test]
    public async Task VlanTagWritesWireBytes()
    {
        // PCP 5, DEI 1, VID 0xABC: TCI 0xBABC.
        VlanTag header = new() { Tci = VlanTag.MakeTci(0xABC, pcp: 5, dei: 1), InnerEtherType = 0x0800 };

        await _AssertWire(
            "BABC" + "0800",
            VlanTag.Size,
            (Span<byte> d, out int n) => header.TryWrite(d, out n));
    }

    #endregion
}
