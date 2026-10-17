// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Wire-format tests for <see cref="ProtobufEncoder.WriteString"/>. The expected bytes are built independently
/// (tag, LEB128 length, <see cref="Encoding.UTF8"/> bytes) so the direct-to-buffer encoding is pinned byte for byte.
/// </summary>
internal sealed class ProtobufEncoderTests
{
    #region Data sources

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

    #region Tests

    /// <summary>
    /// <see cref="ProtobufEncoder.WriteString"/> must emit tag (wire type 2), the UTF-8 byte length as varint and
    /// the UTF-8 bytes, identical to an independent reference encoding.
    /// </summary>
    /// <param name="value">The string to encode.</param>
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

    #endregion

    #region Helpers

    /// <summary>Builds the expected protobuf bytes for a string field without using the encoder under test.</summary>
    /// <param name="fieldNumber">Protobuf field number.</param>
    /// <param name="value">String value.</param>
    /// <returns>Tag, length varint and UTF-8 payload.</returns>
    private static byte[] _ReferenceEncoding(int fieldNumber, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        List<byte> bytes = [];
        _AppendVarint(bytes, (ulong)((fieldNumber << 3) | 2));
        _AppendVarint(bytes, (ulong)utf8.Length);
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
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
    }

    #endregion
}
