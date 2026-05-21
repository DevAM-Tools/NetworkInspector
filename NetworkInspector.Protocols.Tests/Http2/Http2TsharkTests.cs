// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// HTTP/2 cross-validation tests against tshark. tshark only auto-dissects HTTP/2
/// when the connection preface is observed, which our raw single-frame payloads
/// deliberately omit. We therefore force the dissector via <c>-d tcp.port==8443,http2</c>
/// (passed through <see cref="TsharkVerifier"/>) so tshark decodes the actual HTTP/2
/// frame bytes. tshark must be available on PATH or the developer must opt into the
/// <c>NETWORKINSPECTOR_ALLOW_MISSING_TSHARK</c> escape hatch.
/// </summary>
internal sealed class Http2TsharkTests
{
    private const string Http2DecodeAs = "tcp.port==8443,http2";

    /// <summary>
    /// Sanity check: tshark sees the same TCP destination port we wrote.
    /// </summary>
    [Test]
    public async Task Tshark_TcpDestPort_Is_8443()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] body = Http2Layer.BuildSettingsBody((id: 3, value: 100));
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Settings, 0, 0, body);
        EthernetLayer ethLayer = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ipLayer = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcpLayer = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] eth = FrameStack.Start(ethLayer).Then(ipLayer).Then(tcpLayer).Then(http2).CreateWithFixedValues().EmitFrame([]);
        string? value = TsharkVerifier.GetFieldValue(eth, "tcp.dstport");
        await Assert.That(TsharkEquivalence.AreEquivalent(value, "8443")).IsTrue()
            .Because(TsharkEquivalence.Describe("tcp.dstport", value, "8443"));
    }

    /// <summary>
    /// Forces HTTP/2 dissection on port 8443 and verifies that tshark agrees on
    /// the SETTINGS frame's type, length, flags and (zero) stream id.
    /// </summary>
    [Test]
    public async Task Tshark_Http2Settings_Frame_Fields_Match()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] body = Http2Layer.BuildSettingsBody((id: 3, value: 100));
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Settings, 0, 0, body);
        EthernetLayer ethLayer = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ipLayer = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcpLayer = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] eth = FrameStack.Start(ethLayer).Then(ipLayer).Then(tcpLayer).Then(http2).CreateWithFixedValues().EmitFrame([]);

        string?[] values = TsharkVerifier.GetFieldValues(
            eth,
            ["http2.type", "http2.length", "http2.flags", "http2.streamid"],
            decodeAs: Http2DecodeAs);

        await Assert.That(TsharkEquivalence.AreEquivalent(values[0], "4")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.type", values[0], "4"));
        await Assert.That(TsharkEquivalence.AreEquivalent(values[1], body.Length.ToString())).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.length", values[1], body.Length.ToString()));
        await Assert.That(TsharkEquivalence.AreEquivalent(values[2], "0x00")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.flags", values[2], "0x00"));
        await Assert.That(TsharkEquivalence.AreEquivalent(values[3], "0")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.streamid", values[3], "0"));
    }

    /// <summary>
    /// Forces HTTP/2 dissection and verifies a non-zero stream id and the PING type.
    /// </summary>
    [Test]
    public async Task Tshark_Http2Ping_StreamId_And_Type_Match()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] body = Http2Layer.BuildPingBody(0x0102030405060708UL);
        Http2Layer http2 = Http2Layer.BuildFrame(Http2FrameType.Ping, 0, 0, body);
        EthernetLayer ethLayer = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ipLayer = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcpLayer = new(12345, 8443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] eth = FrameStack.Start(ethLayer).Then(ipLayer).Then(tcpLayer).Then(http2).CreateWithFixedValues().EmitFrame([]);

        string?[] values = TsharkVerifier.GetFieldValues(
            eth,
            ["http2.type", "http2.streamid", "http2.length"],
            decodeAs: Http2DecodeAs);

        await Assert.That(TsharkEquivalence.AreEquivalent(values[0], "6")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.type", values[0], "6"));
        await Assert.That(TsharkEquivalence.AreEquivalent(values[1], "0")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.streamid", values[1], "0"));
        await Assert.That(TsharkEquivalence.AreEquivalent(values[2], "8")).IsTrue()
            .Because(TsharkEquivalence.Describe("http2.length", values[2], "8"));
    }
}
