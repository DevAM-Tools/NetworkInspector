// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Protocols.Http2;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="Http2FlagsFormatter"/> covering known flag bits,
/// zero flags, unknown bits, and combinations.
/// </summary>
internal sealed class Http2FlagsFormatterTests
{
    [Test]
    public async Task Format_NoFlags() =>
        await Assert.That(Http2FlagsFormatter.Format(0x00)).IsEqualTo("0x00 [None]");

    [Test]
    public async Task Format_EndStream() =>
        // 0x01 = END_STREAM / ACK
        await Assert.That(Http2FlagsFormatter.Format(0x01)).IsEqualTo("0x01 [ES/ACK]");

    [Test]
    public async Task Format_EndHeaders() =>
        // 0x04 = END_HEADERS
        await Assert.That(Http2FlagsFormatter.Format(0x04)).IsEqualTo("0x04 [END_HDRS]");

    [Test]
    public async Task Format_Padded() =>
        // 0x08 = PADDED
        await Assert.That(Http2FlagsFormatter.Format(0x08)).IsEqualTo("0x08 [PADDED]");

    [Test]
    public async Task Format_Priority() =>
        // 0x20 = PRIORITY
        await Assert.That(Http2FlagsFormatter.Format(0x20)).IsEqualTo("0x20 [PRIORITY]");

    [Test]
    public async Task Format_EndStreamAndEndHeaders() =>
        // DATA/HEADERS last frame: END_STREAM | END_HEADERS
        await Assert.That(Http2FlagsFormatter.Format(0x05)).IsEqualTo("0x05 [ES/ACK, END_HDRS]");

    [Test]
    public async Task Format_AllKnownFlags() =>
        // END_STREAM(0x01) | END_HEADERS(0x04) | PADDED(0x08) | PRIORITY(0x20)
        await Assert.That(Http2FlagsFormatter.Format(0x2D)).IsEqualTo("0x2d [ES/ACK, END_HDRS, PADDED, PRIORITY]");

    [Test]
    public async Task Format_UnknownBit() =>
        // 0x02 is not a defined flag; should appear as "0x02"
        await Assert.That(Http2FlagsFormatter.Format(0x02)).IsEqualTo("0x02 [0x02]");

    [Test]
    public async Task Format_KnownAndUnknown() =>
        // 0x01 (ES/ACK) | 0x02 (unknown)
        await Assert.That(Http2FlagsFormatter.Format(0x03)).IsEqualTo("0x03 [ES/ACK, 0x02]");
}
