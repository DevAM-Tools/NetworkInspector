// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscFlexRayParser.TryParse"/> covering FlexRay V9 message parsing,
/// channel/frame ID/cycle/header CRC extraction, and edge cases.
/// Verifies the DLT_FLEXRAY binary layout:
///   [channel(1) | type_flags(1) | frame_id(2BE) | cycle(1) | header_crc(2BE) | data(...)].
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

        // DLT_FLEXRAY header (7 bytes) + data
        await Assert.That(frame.Length).IsGreaterThanOrEqualTo(7);

        // Byte 0: channel
        await Assert.That(frame[0]).IsEqualTo((byte)1);

        // Bytes 2-3: frame ID (big-endian) — 0x0A = 10
        ushort frameId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        await Assert.That(frameId).IsEqualTo((ushort)0x0A);

        // Byte 4: cycle count
        await Assert.That(frame[4]).IsEqualTo((byte)0);

        // Bytes 5-6: header CRC (big-endian) — 0x1234
        ushort headerCrc = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(5));
        await Assert.That(headerCrc).IsEqualTo((ushort)0x1234);

        // Payload: 8 bytes starting at offset 7
        await Assert.That(frame.Length).IsEqualTo(15);
        await Assert.That(frame[7]).IsEqualTo((byte)0x01);
        await Assert.That(frame[14]).IsEqualTo((byte)0x08);
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
        await Assert.That(frame[0]).IsEqualTo((byte)2);

        ushort frameId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        await Assert.That(frameId).IsEqualTo((ushort)0x10);

        await Assert.That(frame[4]).IsEqualTo((byte)5);

        ushort headerCrc = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(5));
        await Assert.That(headerCrc).IsEqualTo((ushort)0xABCD);
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

        ushort frameId = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
        await Assert.That(frameId).IsEqualTo((ushort)10);

        ushort headerCrc = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(5));
        await Assert.That(headerCrc).IsEqualTo((ushort)100);
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
        // 7-byte header, no payload
        await Assert.That(frame.Length).IsEqualTo(7);
    }

    [Test]
    public async Task LargePayload_32Bytes()
    {
        bool ok = AscFlexRayParser.TryParse(
            "1.600000 Fr 1 V9 FF 16 0 0 FFFF x 32 0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2021"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        // 7 header + 32 data = 39 also check data count from the ASC payload word count
        await Assert.That(frame.Length - 7).IsGreaterThanOrEqualTo(32);
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
        // Build an ASC line where payloadLenWords=0 and dataLen=255 (decimal,
        // just one byte over the FlexRay maximum of 254). Provide 255 hex pairs as data.
        System.Text.StringBuilder sb = new("1.000000 Fr 1 V9 01 0 0 0 0000 x 255 ");
        for (int i = 0; i < 255; i++)
        {
            sb.Append("01");
        }

        byte[] lineBytes = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        bool ok = AscFlexRayParser.TryParse(lineBytes, 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        // Data must be clamped to 254 bytes (FlexRay spec max); 7-byte DLT header included.
        await Assert.That(frame.Length).IsEqualTo(7 + 254);
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
        await Assert.That(frame.Length).IsEqualTo(7 + 254);
    }
}
