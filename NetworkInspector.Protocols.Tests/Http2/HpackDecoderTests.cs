// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the stateless HPACK decoder: integers, strings, Huffman decoding and the per-block dynamic table.
/// </summary>
internal sealed class HpackDecoderTests
{
    // The few Huffman codes of RFC 7541 Appendix B that the tests need: (code, bit length).
    private static readonly Dictionary<char, (uint Code, int Length)> _Codes = new()
    {
        ['a'] = (0x3, 5),
        ['b'] = (0x23, 6),
        ['\n'] = (0x3ffffffc, 30),
        ['\r'] = (0x3ffffffd, 30),
    };

    /// <summary>Encodes <paramref name="text"/> with the Huffman codes above; pads the last octet with one bits (EOS prefix).</summary>
    private static byte[] _HuffmanEncode(string text)
    {
        List<byte> output = [];
        ulong buffer = 0;
        int bits = 0;
        foreach (char c in text)
        {
            (uint code, int length) = _Codes[c];
            buffer = (buffer << length) | code;
            bits += length;
            while (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1UL << bits) - 1;
            }
        }

        if (bits > 0)
        {
            output.Add((byte)((buffer << (8 - bits)) | ((1UL << (8 - bits)) - 1)));
        }

        return [.. output];
    }

    /// <summary>A "literal without indexing, new name" field with the plain name "x" and the given encoded value.</summary>
    private static byte[] _LiteralWithValue(byte[] encodedValue, bool huffman)
    {
        byte lengthOctet = (byte)(encodedValue.Length | (huffman ? 0x80 : 0));
        return [0x00, 0x01, (byte)'x', lengthOctet, .. encodedValue];
    }

    #region Huffman

    [Test]
    public async Task HuffmanRfcExampleWwwExampleCom()
    {
        // RFC 7541 appendix C.4.1: :method GET, :scheme http, :path /, :authority www.example.com (Huffman).
        byte[] block =
        [
            0x82, 0x86, 0x84, 0x41, 0x8c, 0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff
        ];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(4);
        await Assert.That(headers[3]).IsEqualTo(new HpackDecoder.Header(":authority", "www.example.com"));
    }

    [Test]
    public async Task HuffmanRfcExampleResponseDate()
    {
        // RFC 7541 appendix C.6.1: the date header uses 29 Huffman octets.
        byte[] block =
        [
            0x61, 0x96, 0xd0, 0x7a, 0xbe, 0x94, 0x10, 0x54, 0xd4, 0x44, 0xa8, 0x20, 0x05, 0x95, 0x04, 0x0b,
            0x81, 0x66, 0xe0, 0x82, 0xa6, 0x2d, 0x1b, 0xff
        ];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers[0]).IsEqualTo(new HpackDecoder.Header("date", "Mon, 21 Oct 2013 20:13:21 GMT"));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task HuffmanThirtyBitCodeDecodesAtEveryBitAlignment(int leadingSymbols)
    {
        // A 30-bit code that starts at a 5-bit-multiple offset: the pending bits of the unfinished code plus the next octet
        // must not overflow the bit buffer. Every alignment of the code against the octet boundary is covered by 0..7.
        string text = new string('a', leadingSymbols) + "\n" + "ab" + "\r" + "a";
        byte[] block = _LiteralWithValue(_HuffmanEncode(text), huffman: true);

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers[0].Value).IsEqualTo(text);
    }

    #endregion

    #region Integers and strings

    [Test]
    public async Task StringLengthNearIntMaxIsRejectedWithoutException()
    {
        // Length 0x7FFFFFFF: offset + length wraps to a negative number; the bounds check must not be fooled by it.
        byte[] block = [0x00, 0x01, (byte)'x', 0x7F, 0x80, 0xFF, 0xFF, 0xFF, 0x07, 0x41, 0x42];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IndexWithFifthGroupOverflowingAnIntIsRejected()
    {
        // 127 + 0xF0000000: the fifth group (shift 28) would make an int negative or wrap it around.
        byte[] block = [0xFF, 0x80, 0x80, 0x80, 0x80, 0x0F];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TableSizeUpdateWithFiveByteIntegerIsSkippedAndDecodingContinues()
    {
        // A dynamic table size update with the largest legal value (2^32 - 1) is consumed; the indexed header after it
        // (:method GET) is still decoded.
        byte[] block = [0x3F, 0xE0, 0xFF, 0xFF, 0xFF, 0x0F, 0x82];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers[0]).IsEqualTo(new HpackDecoder.Header(":method", "GET"));
    }

    [Test]
    public async Task PlainAndHuffmanStringsMapOctetsTheSameWay()
    {
        // Octets above 0x7F must keep their value (Latin-1) in both paths; a plain string must not turn them into '?'.
        byte[] block = _LiteralWithValue([(byte)'c', (byte)'a', (byte)'f', 0xE9], huffman: false);

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers[0].Value).IsEqualTo("café");
    }

    [Test]
    public async Task TruncatedStringEndsDecoding()
    {
        byte[] block = [0x00, 0x01, (byte)'x', 0x05, (byte)'a', (byte)'b'];

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode(block);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    #endregion

    #region Dynamic table

    [Test]
    public async Task DynamicTableIndexesNewestEntryFirst()
    {
        // Three literals with incremental indexing (name "n1".."n3"), then indexed references 62, 63 and 64.
        List<byte> block = [];
        foreach (char digit in "123")
        {
            block.AddRange([0x40, 0x02, (byte)'n', (byte)digit, 0x01, (byte)digit]);
        }

        block.AddRange([0xBE, 0xBF, 0xC0]);

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode([.. block]);

        await Assert.That(headers.Count).IsEqualTo(6);
        await Assert.That(headers[3]).IsEqualTo(new HpackDecoder.Header("n3", "3"));
        await Assert.That(headers[4]).IsEqualTo(new HpackDecoder.Header("n2", "2"));
        await Assert.That(headers[5]).IsEqualTo(new HpackDecoder.Header("n1", "1"));
    }

    [Test]
    public async Task ManyIncrementallyIndexedLiteralsDecodeQuickly()
    {
        // 30000 literals with incremental indexing and then a reference to the newest entry (index 62). The decode must be
        // linear in the block size even though nearly every insert evicts an old entry (the table holds 4096 octets, so
        // about 120 of these 34-octet entries); the check is the result, which is only reachable after the whole block was
        // processed.
        const int count = 30_000;
        List<byte> block = new(count * 6);
        for (int index = 0; index < count; index++)
        {
            block.AddRange([0x40, 0x01, (byte)'k', 0x01, (byte)'v']);
        }

        block.Add(0x80 | 62);

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode([.. block]);

        await Assert.That(headers.Count).IsEqualTo(count + 1);
        await Assert.That(headers[^1]).IsEqualTo(new HpackDecoder.Header("k", "v"));
    }

    #endregion

    #region Dynamic table across blocks

    [Test]
    public async Task EntryAddedInEarlierBlockResolvesInLaterBlock()
    {
        // The reason for the stateful decoder: the first block adds "x-key: x-val" to the dynamic table, the second block
        // refers to it by index 62. A stateless decoder would skip the reference.
        HpackDecoder decoder = new();
        byte[] first = [0x40, 0x05, (byte)'x', (byte)'-', (byte)'k', (byte)'e', (byte)'y', 0x05, (byte)'x', (byte)'-', (byte)'v', (byte)'a', (byte)'l'];
        byte[] second = [0x80 | 62];

        List<HpackDecoder.Header> firstHeaders = decoder.Decode(first);
        List<HpackDecoder.Header> secondHeaders = decoder.Decode(second);

        await Assert.That(firstHeaders.Count).IsEqualTo(1);
        await Assert.That(secondHeaders.Count).IsEqualTo(1);
        await Assert.That(secondHeaders[0]).IsEqualTo(new HpackDecoder.Header("x-key", "x-val"));
    }

    [Test]
    public async Task FreshDecoderDoesNotResolveEntryOfAnotherConnection()
    {
        // State is per decoder instance: another connection's table must not leak in.
        HpackDecoder first = new();
        _ = first.Decode([0x40, 0x01, (byte)'a', 0x01, (byte)'b']);

        List<HpackDecoder.Header> headers = new HpackDecoder().Decode([0x80 | 62]);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TableSizeUpdateToZeroEvictsEveryEntry()
    {
        // RFC 7541 section 6.3: a size update to 0 empties the table; a later reference to a former entry does not resolve.
        HpackDecoder decoder = new();
        _ = decoder.Decode([0x40, 0x01, (byte)'a', 0x01, (byte)'b']);

        List<HpackDecoder.Header> headers = decoder.Decode([0x20, 0x80 | 62]);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OldestEntryIsEvictedWhenTheTableOverflows()
    {
        // A table of 100 octets holds two 34-octet entries ("k": "v" is 1 + 1 + 32 octets) but not three: the third insert
        // evicts the oldest. Size update first (7 bits prefix 5: 0x20 | 31 then 100 - 31 = 69).
        HpackDecoder decoder = new();
        _ = decoder.Decode([0x3F, 69]);
        _ = decoder.Decode([0x40, 0x01, (byte)'a', 0x01, (byte)'1']);
        _ = decoder.Decode([0x40, 0x01, (byte)'b', 0x01, (byte)'2']);
        _ = decoder.Decode([0x40, 0x01, (byte)'c', 0x01, (byte)'3']);

        // Index 62 is the newest (c), 63 the one before (b); a (the oldest) was evicted, so index 64 does not resolve.
        List<HpackDecoder.Header> headers = decoder.Decode([0x80 | 62, 0x80 | 63, 0x80 | 64]);

        await Assert.That(headers.Count).IsEqualTo(2);
        await Assert.That(headers[0]).IsEqualTo(new HpackDecoder.Header("c", "3"));
        await Assert.That(headers[1]).IsEqualTo(new HpackDecoder.Header("b", "2"));
    }

    [Test]
    public async Task EntryLargerThanTheTableEmptiesItAndIsNotAdded()
    {
        // RFC 7541 section 4.4: an entry that does not fit the whole table clears the table and is not added.
        HpackDecoder decoder = new();
        _ = decoder.Decode([0x3F, 69]);
        _ = decoder.Decode([0x40, 0x01, (byte)'a', 0x01, (byte)'1']);

        byte[] longValue = new byte[200];
        Array.Fill(longValue, (byte)'x');
        List<byte> oversized = [0x40, 0x01, (byte)'k', (byte)longValue.Length];
        oversized.AddRange(longValue);
        _ = decoder.Decode([.. oversized]);

        List<HpackDecoder.Header> headers = decoder.Decode([0x80 | 62]);

        await Assert.That(headers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OversizedTableSizeIsClippedToTheDecoderLimit()
    {
        // The encoder announces a table larger than the decoder's own limit: the limit applies, so the decoder never holds
        // more than MaxTableBytes. With 34-octet entries more than MaxTableBytes / 34 inserts must have evicted the first.
        HpackDecoder decoder = new();

        // Size update to 2^24 - 1 (the largest representable value): 0x3F then the continuation of 5-bit prefix 31.
        _ = decoder.Decode([0x3F, 0xE0 | 0x1F, 0xFF, 0xFF, 0x07]);
        int entries = (HpackDecoder.MaxTableBytes / 34) + 10;
        for (int index = 0; index < entries; index++)
        {
            _ = decoder.Decode([0x40, 0x01, (byte)'k', 0x01, (byte)'v']);
        }

        int oldestAlive = 62 + (HpackDecoder.MaxTableBytes / 34) - 1;
        List<HpackDecoder.Header> beyond = decoder.Decode([0xFF, (byte)((oldestAlive + 16 - 127) & 0x7F | 0x80), (byte)((oldestAlive + 16 - 127) >> 7)]);

        await Assert.That(beyond.Count).IsEqualTo(0);
    }

    #endregion
}
