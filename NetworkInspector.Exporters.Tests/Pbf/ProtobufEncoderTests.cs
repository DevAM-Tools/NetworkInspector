// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Wire-format tests for <see cref="ProtobufEncoder.WriteString"/>. The expected bytes are built independently
/// (tag, LEB128 length, <see cref="Encoding.UTF8"/> bytes) so the direct-to-buffer encoding is pinned byte for byte.
/// </summary>
internal sealed class ProtobufEncoderTests
{
    #region Strings to encode

    /// <summary>
    /// Strings across the encoder's interesting classes: empty, ASCII, multi-byte, a lone surrogate (replacement
    /// fallback), and lengths whose UTF-8 form needs a one-, two- and three-byte length varint.
    /// </summary>
    /// <returns>One string per test case.</returns>
    public static IEnumerable<Func<string>> WriteStringCases()
    {
        yield return static () => string.Empty;
        yield return static () => "eth.src";
        yield return static () => "Grüße, 世界 🌍";
        yield return static () => "lone\uD800surrogate";
        yield return static () => new string('a', 127);
        yield return static () => new string('a', 128);
        yield return static () => new string('ä', 300);
        yield return static () => new string('€', 6000);
    }

    #endregion

    #region Strings and fixed-width fields

    /// <summary>
    /// <see cref="ProtobufEncoder.WriteString"/> must emit tag (wire type 2), the UTF-8 byte length as varint and
    /// the UTF-8 bytes, identical to an independent reference encoding.
    /// </summary>
    /// <param name="value">The string to encode.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [MethodDataSource(nameof(WriteStringCases))]
    public async Task WriteStringMatchesReferenceEncoding(string value)
    {
        PooledBuffer buffer = new(16);
        byte[] expected = _ReferenceEncoding(fieldNumber: 5, value);

        ProtobufEncoder.WriteString(ref buffer, 5, value);
        byte[] actual = buffer.WrittenSpan.ToArray();
        buffer.Return();

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    /// <summary>
    /// Two consecutive strings must be laid out back to back: the second write appends after the first and does
    /// not overwrite it, even when the buffer has to grow in between.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task WriteStringAppendsAfterExistingContent()
    {
        PooledBuffer buffer = new(4);
        string first = "first";
        string second = new('x', 1000);
        byte[] expected = [.. _ReferenceEncoding(1, first), .. _ReferenceEncoding(2, second)];

        ProtobufEncoder.WriteString(ref buffer, 1, first);
        ProtobufEncoder.WriteString(ref buffer, 2, second);
        byte[] actual = buffer.WrittenSpan.ToArray();
        buffer.Return();

        await Assert.That(actual).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A fixed32 field is the tag with wire type 5 and the four little-endian bytes of the value, whatever the host byte order.
    /// The expected bytes are written out, not computed with the encoder.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task WriteFixed32FieldWritesTagWireTypeFiveAndFourLittleEndianBytes()
    {
        PooledBuffer buffer = new(16);

        ProtobufEncoder.WriteFixed32Field(ref buffer, 5, 0xC0A80101u);
        byte[] actual = buffer.WrittenSpan.ToArray();
        buffer.Return();

        // Tag (5 << 3) | 5 = 0x2D, then 0xC0A80101 least significant byte first.
        await Assert.That(Convert.ToHexString(actual)).IsEqualTo("2D" + "0101A8C0");
    }

    /// <summary>A fixed64 field is the tag with wire type 1 and the eight little-endian bytes of the value; field 11 needs a one-byte tag.</summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task WriteFixed64FieldWritesTagWireTypeOneAndEightLittleEndianBytes()
    {
        PooledBuffer buffer = new(16);

        ProtobufEncoder.WriteFixed64Field(ref buffer, 11, 0x0102_0304_0506_0708UL);
        byte[] actual = buffer.WrittenSpan.ToArray();
        buffer.Return();

        // Tag (11 << 3) | 1 = 0x59, then the value least significant byte first.
        await Assert.That(Convert.ToHexString(actual)).IsEqualTo("59" + "0807060504030201");
    }

    /// <summary>
    /// <see cref="ProtobufEncoder.VarintSize"/> equals the number of bytes <see cref="ProtobufEncoder.WriteVarint"/> writes, at every
    /// boundary of the seven-bit groups (the standard block builder writes the length of a buffer record from it before the content),
    /// and for zero and the largest value.
    /// </summary>
    /// <param name="value">The value whose size is checked.</param>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    [Arguments(0UL)]
    [Arguments(1UL)]
    [Arguments(127UL)]
    [Arguments(128UL)]
    [Arguments(255UL)]
    [Arguments(16_383UL)]
    [Arguments(16_384UL)]
    [Arguments(2_097_151UL)]
    [Arguments(2_097_152UL)]
    [Arguments(268_435_455UL)]
    [Arguments(268_435_456UL)]
    [Arguments(long.MaxValue)]
    [Arguments(ulong.MaxValue)]
    public async Task VarintSizeIsTheNumberOfBytesWriteVarintWrites(ulong value)
    {
        PooledBuffer buffer = new(16);

        ProtobufEncoder.WriteVarint(ref buffer, value);
        int written = buffer.WrittenSpan.Length;
        buffer.Return();

        await Assert.That(ProtobufEncoder.VarintSize(value)).IsEqualTo(written);
    }

    /// <summary>
    /// A packed fixed32 column is tag (wire type 2), the byte length (count times 4), then the values the caller writes into the
    /// reserved payload; the reservation follows existing content and survives a buffer that has to grow.
    /// </summary>
    /// <returns>The task that completes when the assertion is done.</returns>
    [Test]
    public async Task ReservePackedFixed32WritesTagLengthAndAFillablePayload()
    {
        PooledBuffer buffer = new(2);
        ProtobufEncoder.WriteVarintField(ref buffer, 1, 7);

        Span<byte> payload = ProtobufEncoder.ReservePackedFixed32(ref buffer, 19, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x0A000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0xFFFFFFFFu);
        string actual = Convert.ToHexString(buffer.WrittenSpan);
        buffer.Return();

        // Field 1 varint 7; tag (19 << 3) | 2 = 0x9A 0x01 (two-byte varint); length 8; two little-endian values.
        await Assert.That(actual).IsEqualTo("0807" + "9A01" + "08" + "0100000A" + "FFFFFFFF");
    }

    /// <summary>A packed fixed64 column is tag, byte length (count times 8), and the reserved payload; an empty column has length 0.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ReservePackedFixed64WritesTagLengthAndAFillablePayload()
    {
        PooledBuffer buffer = new(2);

        Span<byte> payload = ProtobufEncoder.ReservePackedFixed64(ref buffer, 20, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 0x8000_0000_0000_0001UL);
        string one = Convert.ToHexString(buffer.WrittenSpan);
        buffer.Reset();
        Span<byte> empty = ProtobufEncoder.ReservePackedFixed64(ref buffer, 21, 0);
        int emptyLength = empty.Length;
        string none = Convert.ToHexString(buffer.WrittenSpan);
        buffer.Return();

        // Tag (20 << 3) | 2 = 0xA2 0x01, length 8, the value; tag (21 << 3) | 2 = 0xAA 0x01, length 0.
        await Assert.That(one).IsEqualTo("A201" + "08" + "0100000000000080");
        await Assert.That(emptyLength).IsEqualTo(0);
        await Assert.That(none).IsEqualTo("AA01" + "00");
    }

    /// <summary>A negative count is a caller bug and is refused before anything is written.</summary>
    /// <returns>The task that completes when the assertions are done.</returns>
    [Test]
    public async Task ReservePackedFixedRefusesANegativeCount()
    {
        PooledBuffer buffer = new(16);

        bool refused32 = _Refuses32(ref buffer);
        bool refused64 = _Refuses64(ref buffer);
        int written = buffer.Length;
        buffer.Return();

        await Assert.That(refused32).IsTrue();
        await Assert.That(refused64).IsTrue();
        await Assert.That(written).IsEqualTo(0);
    }

    /// <summary>Calls <see cref="ProtobufEncoder.ReservePackedFixed32"/> with a negative count and reports whether it was refused.</summary>
    /// <param name="buffer">The buffer; must stay empty.</param>
    /// <returns><see langword="true"/> when an <see cref="ArgumentOutOfRangeException"/> was thrown.</returns>
    private static bool _Refuses32(ref PooledBuffer buffer)
    {
        try
        {
            _ = ProtobufEncoder.ReservePackedFixed32(ref buffer, 19, -1);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    /// <summary>Calls <see cref="ProtobufEncoder.ReservePackedFixed64"/> with a negative count and reports whether it was refused.</summary>
    /// <param name="buffer">The buffer; must stay empty.</param>
    /// <returns><see langword="true"/> when an <see cref="ArgumentOutOfRangeException"/> was thrown.</returns>
    private static bool _Refuses64(ref PooledBuffer buffer)
    {
        try
        {
            _ = ProtobufEncoder.ReservePackedFixed64(ref buffer, 20, -1);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    #endregion

    #region Reference encoding

    /// <summary>Builds the expected protobuf bytes for a string field without using the encoder under test.</summary>
    /// <param name="fieldNumber">Protobuf field number.</param>
    /// <param name="value">String value.</param>
    /// <returns>Tag, length varint and UTF-8 payload.</returns>
    private static byte[] _ReferenceEncoding(int fieldNumber, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        List<byte> bytes = [];
        _AppendVarint(bytes, checked((ulong)((fieldNumber << 3) | 2)));
        _AppendVarint(bytes, checked((ulong)utf8.Length));
        bytes.AddRange(utf8);
        return [.. bytes];
    }

    /// <summary>Appends an unsigned LEB128 varint.</summary>
    /// <param name="bytes">Destination list.</param>
    /// <param name="value">Value to encode.</param>
    private static void _AppendVarint(List<byte> bytes, ulong value)
    {
        while (value > 0x7F)
        {
            // The low seven bits with the continuation bit set: masked first so the narrowing to a byte cannot lose a set bit.
            bytes.Add(checked((byte)((value & 0x7F) | 0x80)));
            value >>= 7;
        }

        bytes.Add(checked((byte)value));
    }

    #endregion
}
