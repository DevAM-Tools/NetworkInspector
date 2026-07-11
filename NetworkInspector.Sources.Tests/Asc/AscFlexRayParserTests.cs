// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscFlexRayParser.TryParse"/> covering FlexRay V9 message parsing,
/// channel/frame ID/cycle/header CRC extraction, and edge cases.
/// Verifies the LINKTYPE_FLEXRAY binary layout (measurement header + ISO 17458-2 header + data).
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscFlexRayParserTests
{
    // ========================================================================
    // Basic FlexRay parsing (hex base)
    // ========================================================================

    [Test]
    public async Task BasicHex_ParsedCorrectly()
    {
        bool ok = AscFlexRayParser.TryParse(
            "0.800000 Fr 1 V9 0A 4 0 0 1234 x 8 0102030405060708"u8,
            16, out double ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts).IsEqualTo(0.8).Within(0.0001);
        await Assert.That(ch).IsEqualTo(1);

        await Assert.That(frame.Length).IsGreaterThanOrEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> payloadSpan);
        byte[] payload = payloadSpan.ToArray();
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.ChannelB).IsFalse();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x0A);
        await Assert.That(fields.Cycle).IsEqualTo((byte)0);
        await Assert.That(fields.HeaderCrc).IsEqualTo((ushort)(0x1234 & 0x7FF));
        await Assert.That(payload.Length).IsEqualTo(8);
        await Assert.That(payload[0]).IsEqualTo((byte)0x01);
        await Assert.That(payload[7]).IsEqualTo((byte)0x08);
    }

    // ========================================================================
    // Channel variations
    // ========================================================================

    [Test]
    public async Task Channel2_ParsedCorrectly()
    {
        bool ok = AscFlexRayParser.TryParse(
            "1.000000 Fr 2 V9 10 2 5 0 ABCD x 4 DEADBEEF"u8,
            16, out _, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ch).IsEqualTo(2);

        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out _);
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.ChannelB).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)0x10);
        await Assert.That(fields.Cycle).IsEqualTo((byte)5);
        await Assert.That(fields.HeaderCrc).IsEqualTo((ushort)(0xABCD & 0x7FF));
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_FrameIdParsedAsDecimal()
    {
        // In decimal mode, frame_id "10" = decimal 10 (not hex 0x10)
        bool ok = AscFlexRayParser.TryParse(
            "1.200000 Fr 1 V9 10 2 0 0 100 x 4 01020304"u8,
            10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();

        bool parsed = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out _);
        await Assert.That(parsed).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)10);
        await Assert.That(fields.HeaderCrc).IsEqualTo((ushort)100);
    }

    // ========================================================================
    // Payload variations
    // ========================================================================

    [Test]
    public async Task EmptyPayload_HeaderOnly()
    {
        bool ok = AscFlexRayParser.TryParse(
            "1.400000 Fr 1 V9 01 0 0 0 0000 x 0"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
    }

    [Test]
    public async Task LargePayload_32Bytes()
    {
        bool ok = AscFlexRayParser.TryParse(
            "1.600000 Fr 1 V9 FF 16 0 0 FFFF x 32 0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2021"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length - FlexRayLinkTypeFrame.MinHeaderSize).IsGreaterThanOrEqualTo(32);
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public async Task EmptyLine_ReturnsFalse()
    {
        bool ok = AscFlexRayParser.TryParse(""u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLine_ReturnsFalse()
    {
        bool ok = AscFlexRayParser.TryParse("0.100000 Fr"u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    // ========================================================================
    // OOM guard — F3: dataLen clamp when payloadLenWords == 0
    // ========================================================================

    /// <summary>
    /// F3 regression (byte-span variant): When <c>payloadLenWords</c> is 0 (unspecified),
    /// the guard <c>dataLen &gt; payloadLenWords * 2</c> is always false and does not
    /// constrain <c>dataLen</c>. Without the clamp a malicious ASC line could declare an
    /// arbitrarily large data length, causing an unbounded heap allocation.
    /// The clamp must cap the resulting frame data to at most MaxFlexRayDataBytes = 254.
    /// </summary>
    [Test]
    public async Task PayloadLenWordsZero_DataLen255_ByteVariant_ClampedTo254()
    {
        System.Text.StringBuilder sb = new("1.000000 Fr 1 V9 01 0 0 0 0000 x 255 ");
        for (int i = 0; i < 255; i++)
        {
            sb.Append("01");
        }

        byte[] lineBytes = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        bool ok = AscFlexRayParser.TryParse(lineBytes, 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize + 254);
    }

    /// <summary>
    /// F3 regression (char-span variant): Same clamp verification as
    /// <see cref="PayloadLenWordsZero_DataLen255_ByteVariant_ClampedTo254"/>
    /// but exercising the <see cref="AscFlexRayParser.TryParse(ReadOnlySpan{char},int,out double,out int,out byte[])"/>
    /// overload to confirm both variants share the fix.
    /// </summary>
    [Test]
    public async Task PayloadLenWordsZero_DataLen255_CharVariant_ClampedTo254()
    {
        System.Text.StringBuilder sb = new("1.000000 Fr 1 V9 01 0 0 0 0000 x 255 ");
        for (int i = 0; i < 255; i++)
        {
            sb.Append("01");
        }

        string line = sb.ToString();
        bool ok = AscFlexRayParser.TryParse(line.AsSpan(), 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize + 254);
    }
}
