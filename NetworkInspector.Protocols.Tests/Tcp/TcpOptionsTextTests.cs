// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// The container text of the TCP options is the text the dissector has always shown (the expected strings are the
/// literals of the former formatting code): decimal numbers, no thousands separators, same wording.
/// </summary>
internal sealed class TcpOptionsTextTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static byte[] _FrameWithOptions(byte[] optionBytes)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        TcpLayerWithOptions tcp = new(49152, 80, optionBytes, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static async Task _AssertText(byte[] optionBytes, string field, string expected)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = ProtocolTestHelper.ParseFrame(stack, _FrameWithOptions(optionBytes), 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertDisplayText(stack, packet, field, expected).ConfigureAwait(false);
    }

    [Test]
    public async Task WindowScaleShowsShiftAndMultiplier() =>
        await _AssertText([0x03, 0x03, 0x07, 0x01], "tcp.options.wscale", "Window Scale: 7 (multiply by 128)").ConfigureAwait(false);

    [Test]
    public async Task SackShowsTheBlockCount() =>
        await _AssertText(
            [0x05, 0x0A, 0x00, 0x00, 0x03, 0xE8, 0x00, 0x00, 0x07, 0xD0, 0x01, 0x01],
            "tcp.options.sack",
            "SACK: 1 block(s)").ConfigureAwait(false);

    [Test]
    public async Task TimestampsShowBothValuesWithoutSeparators() =>
        await _AssertText(
            [0x08, 0x0A, 0x00, 0x01, 0x86, 0xA0, 0x00, 0x0F, 0x42, 0x40, 0x01, 0x01],
            "tcp.options.timestamp",
            "Timestamps: TSval=100000, TSecr=1000000").ConfigureAwait(false);

    [Test]
    public async Task UserTimeoutShowsValueAndUnit() =>
        await _AssertText([0x1C, 0x04, 0x80, 0x3C], "tcp.options.user_timeout", "User Timeout: 60 seconds").ConfigureAwait(false);

    [Test]
    public async Task UserTimeoutInMinutes() =>
        await _AssertText([0x1C, 0x04, 0x00, 0x05], "tcp.options.user_timeout", "User Timeout: 5 minutes").ConfigureAwait(false);

    [Test]
    public async Task FastOpenCookieShowsItsLength() =>
        await _AssertText(
            [0x22, 0x0A, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x01, 0x01],
            "tcp.options.tfo",
            "TCP Fast Open: Cookie (8 bytes)").ConfigureAwait(false);

    [Test]
    public async Task MultipathTcpShowsTheSubtypeName() =>
        await _AssertText([0x1E, 0x04, 0x20, 0x00], "tcp.options.mptcp", "Multipath TCP: DSS").ConfigureAwait(false);

    [Test]
    public async Task TcpAuthenticationOptionShowsBothKeyIds() =>
        await _AssertText(
            [0x1D, 0x06, 0x07, 0x09, 0xAA, 0xBB, 0x01, 0x01],
            "tcp.options.ao",
            "TCP-AO: KeyID=7, RNextKeyID=9").ConfigureAwait(false);
}
