// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The container text of IPv4 options and IPv6 extension headers is the text the dissectors have always shown (the expected
/// strings follow the argument lists of the former formatting code): decimal numbers, same wording and punctuation.
/// </summary>
internal sealed class IPv4OptionsTextTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static async Task _AssertText(byte[] frame, string field, string expected)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertDisplayText(stack, packet, field, expected).ConfigureAwait(false);
    }

    private static byte[] _Ipv4WithOptions(byte[] options) =>
        FrameStack
            .Start(new EthernetLayer(_DstMac, _SrcMac))
            .Then(new IPv4LayerWithOptions(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002), options, protocol: 17))
            .Then(new UdpLayer(srcPort: 1, dstPort: 2))
            .CreateWithFixedValues()
            .EmitFrame([1, 2, 3, 4]);

    [Test]
    public async Task OptionsContainerShowsTheOptionByteCount() =>
        await _AssertText(_Ipv4WithOptions([0x01, 0x01, 0x01, 0x00]), "ip.options", "Options (4 bytes)").ConfigureAwait(false);

    [Test]
    public async Task RecordRouteShowsLengthAndEntryCount()
    {
        byte[] options = [0x07, 0x0B, 0x04, 10, 0, 0, 1, 10, 0, 0, 2, 0x00];

        string name = Protocols.Helpers.DisplayTables.GetIpOptionTypeName(0x07);

        await _AssertText(_Ipv4WithOptions(options), "ip.opt.record_route", $"{name} (11 bytes, 2 entries)").ConfigureAwait(false);
    }

    [Test]
    public async Task RouterAlertWithAnUnknownValueShowsItDecimal()
    {
        byte[] options = [0x94, 0x04, 0x12, 0x34];

        string name = Protocols.Helpers.DisplayTables.GetIpOptionTypeName(0x94);

        await _AssertText(_Ipv4WithOptions(options), "ip.opt.router_alert", $"{name}: Unknown (4660)").ConfigureAwait(false);
    }

    [Test]
    public async Task HopByHopHeaderShowsItsLength()
    {
        IPv6HopByHopLayer hopByHop = new();
        byte[] frame = FrameStack
            .Start(new EthernetLayer(_DstMac, _SrcMac))
            .Then(new IPv6Layer(
                IPv6Address.FromBytes(Convert.FromHexString("20010DB8000000000000000000000001")),
                IPv6Address.FromBytes(Convert.FromHexString("20010DB8000000000000000000000002"))))
            .Then(hopByHop)
            .Then(new UdpLayer(srcPort: 1, dstPort: 2))
            .CreateWithFixedValues()
            .EmitFrame([1, 2, 3, 4]);

        await _AssertText(frame, "ipv6.hopopts", "Hop-by-Hop Options Header (8 bytes)").ConfigureAwait(false);
    }

    [Test]
    public async Task SecurityOptionShowsItsLength()
    {
        byte[] options = [0x82, 0x04, 0x00, 0x00];

        string name = Protocols.Helpers.DisplayTables.GetIpOptionTypeName(0x82);

        await _AssertText(_Ipv4WithOptions(options), "ip.opt.security", $"{name} (4 bytes)").ConfigureAwait(false);
    }

    [Test]
    public async Task StreamIdentifierShowsTheDecimalId()
    {
        byte[] options = [0x88, 0x04, 0x12, 0x34];

        string name = Protocols.Helpers.DisplayTables.GetIpOptionTypeName(0x88);

        await _AssertText(_Ipv4WithOptions(options), "ip.opt.stream_id", $"{name}: 4660").ConfigureAwait(false);
    }

    // The FrameBuilder has no AH layer, so the frame is written out: Ethernet II (IPv6), a fixed IPv6 header whose
    // next-header byte announces the security header, and the header bytes themselves.
    private static byte[] _Ipv6SecurityFrame(string nextHeaderHex, string securityHeaderHex)
    {
        string payloadLength = (securityHeaderHex.Length / 2).ToString("X4", CultureInfo.InvariantCulture);
        return Convert.FromHexString(
            "001122334455" + "66778899AABB" + "86DD"
            + "60000000" + payloadLength + nextHeaderHex + "40"
            + "20010DB8000000000000000000000001" + "20010DB8000000000000000000000002"
            + securityHeaderHex);
    }

    [Test]
    public async Task AuthenticationHeaderShowsPaddedSpiAndDecimalSequence()
    {
        // next 59 (none) | length 4 (24 bytes in all) | reserved | SPI 0x0000ABCD | seq 65536 | 12 bytes ICV
        byte[] frame = _Ipv6SecurityFrame("33", "3B04" + "0000" + "0000ABCD" + "00010000" + "000102030405060708090A0B");

        await _AssertText(frame, "ipv6.ah", "Authentication Header (SPI: 0x0000ABCD, Seq: 65536)").ConfigureAwait(false);
        await _AssertText(frame, "ipv6.ah.spi", "0x0000ABCD").ConfigureAwait(false);
    }

    [Test]
    public async Task EspHeaderShowsSpiAndDecimalSequence()
    {
        // SPI 0x0A0B0C0D | seq 100000 (the ciphertext after the first 8 bytes is not split)
        byte[] frame = _Ipv6SecurityFrame("32", "0A0B0C0D" + "000186A0");

        await _AssertText(frame, "ipv6.esp", "Encapsulating Security Payload (SPI: 0x0A0B0C0D, Seq: 100000)").ConfigureAwait(false);
        await _AssertText(frame, "ipv6.esp.spi", "0x0A0B0C0D").ConfigureAwait(false);
    }
}
