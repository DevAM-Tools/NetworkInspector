// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// HPACK header compression decoder (RFC 7541) with a dynamic table: one instance decodes the header blocks of ONE
/// direction of ONE HTTP/2 connection, in order.
/// <para>
/// <b>Why an instance with state:</b> HPACK compresses headers against a table that both endpoints build up over the whole
/// connection. After the first request, a browser or server refers to <c>:authority</c>, <c>user-agent</c>, cookies and most
/// custom headers by index; a decoder that starts every block with an empty table can show only the static-table headers of
/// the second and every later request. The decoder therefore keeps the dynamic table between blocks, applies the size updates
/// the encoder signals (RFC 7541 section 4.3) and evicts the oldest entries when the table is over its size
/// (section 4.4), exactly as the endpoints do.
/// </para>
/// <para>
/// <b>A passive observer can lack history:</b> a capture that starts mid-connection misses the blocks that built the table.
/// An index that refers to an entry the decoder never saw is skipped (indexed header) or ends the block (literal with a name
/// index), and the decoder keeps going with the next block; later blocks that only use entries added after the capture
/// started resolve normally. The decoder is a display aid, not a protocol enforcer: it does not check the size updates
/// against the SETTINGS the endpoints exchanged (the SETTINGS frame may precede the capture) and does not report a
/// compression error.
/// </para>
/// <para>
/// <b>Limit - dynamic table size:</b> <see cref="MaxTableBytes"/>, 64 KiB per decoder. Threat: every tracked connection owns
/// two decoders, and an endpoint may announce a table of up to 4 GiB; honoring that would let a peer make the dissector hold
/// gigabytes of header strings. Constant (a memory bound of the dissector, not configurable), secure by default (typical
/// tables are 4 KiB; the largest common value is 64 KiB). Exceeding it is not an error: a larger announced size is clipped to
/// the limit, so entries are evicted earlier than the endpoints do, and a later index that refers to an evicted entry is
/// skipped like an unseen one.
/// </para>
/// <para>
/// <b>Thread-safety:</b> an instance is mutated by every <see cref="Decode"/> call and must be used by one thread at a
/// time; the HTTP/2 protocol decodes on the ordered first parse only and replays recorded headers. The static and Huffman
/// tables are immutable. The cost is a handful of allocations per header (strings, a list per block, a
/// <see cref="StringBuilder"/> per Huffman string). The output size is bounded by the input size (every representation
/// consumes at least one byte and a Huffman string expands at most 8/5 times).
/// </para>
/// </summary>
internal sealed class HpackDecoder
{
    /// <summary>
    /// The largest HPACK integer the decoder accepts: the maximum HTTP/2 frame payload, 2^24 - 1 octets (RFC 9113
    /// section 4.2). Constant (protocol fact); see <c>_DecodeInteger</c> for the threat and posture.
    /// </summary>
    private const int _MaxInteger = 0xFFFFFF;

    /// <summary>
    /// The size of the dynamic table before any size update: 4096 octets, the initial value of
    /// SETTINGS_HEADER_TABLE_SIZE (RFC 9113 section 6.5.2). A protocol fact.
    /// </summary>
    private const int _InitialTableBytes = 4096;

    /// <summary>
    /// Per-entry overhead of the dynamic table size accounting: an entry occupies the length of its name plus the length of
    /// its value plus 32 octets (RFC 7541 section 4.1). A protocol fact.
    /// </summary>
    private const int _EntryOverheadBytes = 32;

    /// <summary>
    /// Upper bound of the dynamic table size the decoder honors (see the class remarks for the threat, posture and what
    /// happens when it is exceeded).
    /// </summary>
    internal const int MaxTableBytes = 64 * 1024;

    /// <summary>Represents a decoded HPACK header (name + value).</summary>
    /// <param name="Name">The header name (lower case on the wire for HTTP/2).</param>
    /// <param name="Value">The header value.</param>
    internal readonly record struct Header(string Name, string Value);

    /// <summary>
    /// The dynamic table in insertion order, oldest first; the live entries are <c>[_Head, Count)</c>. Index 62 of the
    /// HPACK address space is the NEWEST entry (RFC 7541 section 2.3.3), so lookups count from the end. Appending at the end
    /// keeps an insert O(1); the evicted prefix is released by advancing <see cref="_Head"/>, and the list is compacted only
    /// when more than half of it is dead, so an eviction is O(1) amortized instead of an O(n) shift per entry.
    /// </summary>
    private readonly List<Header> _Entries = [];

    private int _Head;

    /// <summary>Sum of the sizes (name + value + 32) of the live entries, in octets.</summary>
    private int _TableBytes;

    /// <summary>Current maximum size of the dynamic table, set by size updates (starts at <see cref="_InitialTableBytes"/>).</summary>
    private int _MaxTableBytes = _InitialTableBytes;

    /// <summary>
    /// Decodes one HPACK header block (the concatenated header block fragments of a HEADERS frame and its CONTINUATION
    /// frames) into name/value pairs and updates the dynamic table.
    /// </summary>
    /// <param name="data">The HPACK-encoded header block.</param>
    /// <returns>
    /// The headers decoded before the first malformed representation, in wire order. Malformed or truncated input
    /// ends decoding early and returns the headers found so far (possibly an empty list); it never throws for a
    /// truncated block. An indexed header whose index refers to an entry the decoder never saw is skipped.
    /// </returns>
    /// <remarks>
    /// The "never indexed" flag of a literal is not reported (it only matters to re-encoders). A malformed block can leave
    /// the table with fewer entries than the encoder added; later indexes may then point at a different entry than the
    /// endpoints' tables would, which a display aid accepts.
    /// </remarks>
    internal List<Header> Decode(ReadOnlySpan<byte> data)
    {
        List<Header> headers = [];

        // One representation per iteration; the first bits of the first byte select the representation (RFC 7541
        // section 6). Any malformed representation ends the loop and keeps what was decoded before it.
        int offset = 0;
        while (offset < data.Length)
        {
            byte b = data[offset];

            if ((b & 0x80) != 0)
            {
                // §6.1 Indexed Header Field Representation
                // Bit pattern: 1xxxxxxx
                int index = _DecodeInteger(data, ref offset, 7);
                if (index <= 0)
                {
                    break; // Invalid index
                }

                // An index into an entry the decoder never saw (the capture started mid-connection, or the entry was
                // evicted by the size limit) does not resolve: skip the header and continue, unlike a literal with such a
                // name index, which ends the block.
                Header? header = _LookupIndex(index);
                if (header is not null)
                {
                    headers.Add(header.Value);
                }
            }
            else if ((b & 0xC0) == 0x40)
            {
                // §6.2.1 Literal Header Field with Incremental Indexing
                // Bit pattern: 01xxxxxx
                Header? header = _DecodeLiteralHeader(data, ref offset, 6);
                if (header is null)
                {
                    break;
                }

                _AddEntry(header.Value);
                headers.Add(header.Value);
            }
            else if ((b & 0xF0) == 0x00)
            {
                // §6.2.2 Literal Header Field without Indexing
                // Bit pattern: 0000xxxx
                Header? header = _DecodeLiteralHeader(data, ref offset, 4);
                if (header is null)
                {
                    break;
                }
                headers.Add(header.Value);
            }
            else if ((b & 0xF0) == 0x10)
            {
                // §6.2.3 Literal Header Field Never Indexed
                // Bit pattern: 0001xxxx. Decoded like "without indexing"; the never-indexed marker only matters to
                // re-encoders and is not kept.
                Header? header = _DecodeLiteralHeader(data, ref offset, 4);
                if (header is null)
                {
                    break;
                }
                headers.Add(header.Value);
            }
            else if ((b & 0xE0) == 0x20)
            {
                // §6.3 Dynamic Table Size Update
                // Bit pattern: 001xxxxx. The new maximum size takes effect immediately and evicts entries until the table
                // fits (RFC 7541 section 4.3). A value the decoder cannot represent ends the block as malformed.
                int newSize = _DecodeInteger(data, ref offset, 5);
                if (newSize < 0)
                {
                    // Truncated: the block ends here. A well-formed value too large to represent (above 2^24 - 1) has been
                    // consumed completely; it is clipped to the decoder's limit like any other oversized request.
                    if (offset >= data.Length)
                    {
                        break;
                    }

                    newSize = MaxTableBytes;
                }

                // Clipped to the decoder's own limit, see MaxTableBytes.
                _MaxTableBytes = Math.Min(newSize, MaxTableBytes);
                _EvictToFit(0);
            }
            else
            {
                break; // Unknown representation
            }
        }

        return headers;
    }

    /// <summary>
    /// Adds an entry at the newest position and evicts the oldest entries until the table fits (RFC 7541 section 4.4). An
    /// entry larger than the whole table empties the table and is not added, as the RFC prescribes.
    /// </summary>
    private void _AddEntry(Header header)
    {
        // The sizes are octets; the header strings are Latin-1, so one char is one octet and Length is the octet count.
        int size = header.Name.Length + header.Value.Length + _EntryOverheadBytes;
        _EvictToFit(size);
        if (size > _MaxTableBytes)
        {
            return;
        }

        _Entries.Add(header);
        _TableBytes += size;
    }

    /// <summary>Evicts the oldest entries until <paramref name="incomingBytes"/> more octets would fit.</summary>
    private void _EvictToFit(int incomingBytes)
    {
        while (_Head < _Entries.Count && _TableBytes + incomingBytes > _MaxTableBytes)
        {
            Header oldest = _Entries[_Head];
            _TableBytes -= oldest.Name.Length + oldest.Value.Length + _EntryOverheadBytes;

            // Release the strings now; the slot stays until the list is compacted.
            _Entries[_Head] = default;
            _Head++;
        }

        // Compact when more than half of the list is dead (and it is worth the copy): an eviction stays O(1) amortized.
        if (_Head > 32 && _Head * 2 > _Entries.Count)
        {
            _Entries.RemoveRange(0, _Head);
            _Head = 0;
        }
    }

    /// <summary>
    /// Decodes a literal header field from the HPACK data.
    /// </summary>
    /// <param name="data">The header block.</param>
    /// <param name="offset">Read position; advanced past the field on success, unspecified on failure.</param>
    /// <param name="prefixBits">Width of the name index prefix: 6 with incremental indexing, 4 otherwise.</param>
    /// <returns>The header, or <see langword="null"/> when the field is truncated or its name index is unknown.</returns>
    private Header? _DecodeLiteralHeader(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        int nameIndex = _DecodeInteger(data, ref offset, prefixBits);
        if (nameIndex < 0)
        {
            return null;
        }

        string name;
        if (nameIndex > 0)
        {
            // Name is indexed in the static/dynamic table
            Header? indexed = _LookupIndex(nameIndex);
            if (indexed is null)
            {
                return null;
            }
            name = indexed.Value.Name;
        }
        else
        {
            // Name is a literal string
            string? decoded = _DecodeString(data, ref offset);
            if (decoded is null)
            {
                return null;
            }
            name = decoded;
        }

        // Value is always a literal string
        string? value = _DecodeString(data, ref offset);
        if (value is null)
        {
            return null;
        }

        return new Header(name, value);
    }

    /// <summary>
    /// Decodes an HPACK integer using the prefix-coded encoding (RFC 7541 §5.1).
    /// </summary>
    /// <param name="data">The header block.</param>
    /// <param name="offset">Read position; advanced past the integer (also on failure, as far as it was read).</param>
    /// <param name="prefixBits">Number of low bits of the first byte that hold the integer prefix (1 to 7).</param>
    /// <returns>The value, or -1 when the integer is truncated, longer than five bytes, or larger than 2^24 - 1.</returns>
    private static int _DecodeInteger(ReadOnlySpan<byte> data, ref int offset, int prefixBits)
    {
        if (offset >= data.Length)
        {
            return -1;
        }

        int mask = (1 << prefixBits) - 1;
        int value = data[offset] & mask;
        offset++;

        if (value < mask)
        {
            return value;
        }

        // Multi-byte encoding: the prefix was saturated, so the value continues in 7-bit groups, least significant
        // first, each with a continuation flag in bit 7. The fifth group (shift 28) is the last one accepted. The sum is
        // kept in 64 bits: five groups plus the prefix need at most 35 bits, so it cannot overflow, whereas an int
        // would wrap for a large fifth group and the callers would see a negative or a too small number.
        long wide = value;
        int shift = 0;
        while (offset < data.Length)
        {
            byte b = data[offset];
            offset++;
            wide += (long)(b & 0x7F) << shift;
            shift += 7;

            if ((b & 0x80) == 0)
            {
                // Limit: a value above _MaxInteger is refused. Threat: a crafted length or index that overflows the
                // bounds arithmetic of the callers. Constant (protocol fact): no string length, header index or
                // table size that the decoder acts on can exceed one HTTP/2 frame, 2^24 - 1 octets (RFC 9113 section
                // 4.2); a table size update is consumed and not applied, so a larger value is simply reported as invalid.
                // Secure by default (fixed). Residual risk: none, the decoder is a display aid.
                return wide <= _MaxInteger
                    ? (int)wide
                    : -1;
            }

            // Guard against an endless continuation chain: five groups are the longest accepted encoding.
            if (shift > 28)
            {
                return -1;
            }
        }

        return -1; // Incomplete integer
    }


    /// <summary>
    /// Decodes an HPACK string (RFC 7541 §5.2).
    /// Supports both plain and Huffman-encoded strings.
    /// </summary>
    /// <param name="data">The header block.</param>
    /// <param name="offset">Read position; advanced past the string on success.</param>
    /// <returns>The decoded string, or <see langword="null"/> when the string is truncated.</returns>
    /// <remarks>
    /// Both the plain and the Huffman path map each octet to the Latin-1 char of the same value, so no octet is lost or
    /// replaced and the two paths agree for non-ASCII octets.
    /// </remarks>
    private static string? _DecodeString(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return null;
        }

        // Bit 7 of the length octet is the Huffman flag; the remaining 7 bits start the length integer.
        bool huffmanEncoded = (data[offset] & 0x80) != 0;
        int length = _DecodeInteger(data, ref offset, 7);

        // The declared length must lie inside the block: it comes from the wire and is never trusted for allocation. The
        // comparison is written as a subtraction (offset <= data.Length here), so a huge length cannot wrap the sum.
        if (length < 0 || length > data.Length - offset)
        {
            return null;
        }

        ReadOnlySpan<byte> stringData = data.Slice(offset, length);
        offset += length;

        if (huffmanEncoded)
        {
            return _HuffmanDecode(stringData);
        }

        // Latin-1, like the Huffman path: ASCII would turn every octet above 0x7F into '?' and lose it.
        return Encoding.Latin1.GetString(stringData);
    }

    /// <summary>
    /// Looks up a header by index in the combined static + dynamic table.
    /// Static table indices: 1-61, Dynamic table indices: 62+.
    /// </summary>
    /// <returns>The entry, or <see langword="null"/> for index 0, a negative index, or an unknown dynamic index.</returns>
    private Header? _LookupIndex(int index)
    {
        if (index >= 1 && index <= _StaticTable.Length)
        {
            return _StaticTable[index - 1];
        }

        // Dynamic table: index 62 = most recent entry. The list is in insertion order (oldest first), so count from the end;
        // the dead prefix before _Head holds evicted entries and is never addressed.
        int dynamicIndex = index - _StaticTable.Length - 1;
        int live = _Entries.Count - _Head;
        if (dynamicIndex >= 0 && dynamicIndex < live)
        {
            return _Entries[_Entries.Count - 1 - dynamicIndex];
        }

        return null;
    }

    /// <summary>
    /// Decodes a Huffman-encoded string using the HPACK Huffman table (RFC 7541, Appendix B).
    /// A simple table-scan decoder: for every symbol it tries each possible code length against the whole code table.
    /// </summary>
    /// <param name="data">The Huffman-coded octets of one string (without the length prefix).</param>
    /// <returns>The decoded string. Never <see langword="null"/>; trailing bits that do not form a code are dropped.</returns>
    /// <remarks>
    /// Chosen for simplicity over speed: each symbol costs up to (code lengths tried) x 256 table comparisons. A
    /// 256-entry decoding tree or byte-wise state table would be faster; header blocks are small and this decoder is
    /// a display aid, not a throughput path. The trailing padding (the most significant bits of the EOS code, all
    /// ones, RFC 7541 section 5.2) and truncated codes are not validated or reported.
    /// </remarks>
    private static string? _HuffmanDecode(ReadOnlySpan<byte> data)
    {
        // Use a simple bit-by-bit approach with the Huffman code table.
        // Capacity: the shortest code is 5 bits, so n input octets yield at most 8n/5 (< 2n) symbols and the
        // builder never has to grow for valid input.
        StringBuilder sb = new(data.Length * 2);

        // MSB-first bit buffer: the oldest unconsumed bit is the highest of the low `bits` bits. It is a ulong because
        // codes may be up to 30 bits long and a byte is added at a time: a long code that is still incomplete (up to 29
        // pending bits) plus the next octet is 37 bits, which a uint would silently cut at the top and decode wrongly.
        ulong accumulator = 0;
        int bits = 0;

        foreach (byte b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;

            // Try to extract symbols while we have enough bits
            while (bits >= 5) // Minimum Huffman code length is 5 bits
            {
                bool found = false;

                // The code is prefix-free, so at most one length matches the head of the buffer and the order of the
                // tries does not change the result; longest-first is just the order used here.
                for (int len = Math.Min(bits, 30); len >= 5; len--)
                {
                    uint code = (uint)((accumulator >> (bits - len)) & ((1UL << len) - 1));

                    if (_TryLookupHuffmanSymbol(code, len, out byte symbol))
                    {
                        sb.Append((char)symbol);
                        bits -= len;
                        accumulator &= (1UL << bits) - 1;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    break; // Need more bits
                }
            }
        }

        // Remaining bits should be padding (all 1s); they are not checked.
        return sb.ToString();
    }

    /// <summary>
    /// Attempts to find a symbol matching the given Huffman code and code length.
    /// </summary>
    /// <param name="code">The candidate code bits, right-aligned.</param>
    /// <param name="codeLen">The number of bits in <paramref name="code"/>.</param>
    /// <param name="symbol">The decoded octet when found.</param>
    private static bool _TryLookupHuffmanSymbol(uint code, int codeLen, out byte symbol)
    {
        // Linear search of the 256 symbols; the symbol value is the table index. The EOS symbol (256) is not in the
        // table, so it never matches.
        for (int i = 0; i < _HuffmanTable.Length; i++)
        {
            (uint hCode, int hLen) = _HuffmanTable[i];
            if (hLen == codeLen && hCode == code)
            {
                symbol = (byte)i;
                return true;
            }
        }
        symbol = 0;
        return false;
    }

    #region HPACK Static Table (RFC 7541, Appendix A)

    /// <summary>
    /// The 61-entry HPACK static table. Index 1 = entry[0], index 61 = entry[60].
    /// </summary>
    private static readonly Header[] _StaticTable =
    [
        new(":authority", ""),          // 1
        new(":method", "GET"),          // 2
        new(":method", "POST"),         // 3
        new(":path", "/"),              // 4
        new(":path", "/index.html"),    // 5
        new(":scheme", "http"),         // 6
        new(":scheme", "https"),        // 7
        new(":status", "200"),          // 8
        new(":status", "204"),          // 9
        new(":status", "206"),          // 10
        new(":status", "304"),          // 11
        new(":status", "400"),          // 12
        new(":status", "404"),          // 13
        new(":status", "500"),          // 14
        new("accept-charset", ""),      // 15
        new("accept-encoding", "gzip, deflate"), // 16
        new("accept-language", ""),     // 17
        new("accept-ranges", ""),       // 18
        new("accept", ""),              // 19
        new("access-control-allow-origin", ""), // 20
        new("age", ""),                 // 21
        new("allow", ""),               // 22
        new("authorization", ""),       // 23
        new("cache-control", ""),       // 24
        new("content-disposition", ""), // 25
        new("content-encoding", ""),    // 26
        new("content-language", ""),    // 27
        new("content-length", ""),      // 28
        new("content-location", ""),    // 29
        new("content-range", ""),       // 30
        new("content-type", ""),        // 31
        new("cookie", ""),              // 32
        new("date", ""),                // 33
        new("etag", ""),                // 34
        new("expect", ""),              // 35
        new("expires", ""),             // 36
        new("from", ""),                // 37
        new("host", ""),                // 38
        new("if-match", ""),            // 39
        new("if-modified-since", ""),   // 40
        new("if-none-match", ""),       // 41
        new("if-range", ""),            // 42
        new("if-unmodified-since", ""), // 43
        new("last-modified", ""),       // 44
        new("link", ""),                // 45
        new("location", ""),            // 46
        new("max-forwards", ""),        // 47
        new("proxy-authenticate", ""),  // 48
        new("proxy-authorization", ""), // 49
        new("range", ""),               // 50
        new("referer", ""),             // 51
        new("refresh", ""),             // 52
        new("retry-after", ""),         // 53
        new("server", ""),              // 54
        new("set-cookie", ""),          // 55
        new("strict-transport-security", ""), // 56
        new("transfer-encoding", ""),   // 57
        new("user-agent", ""),          // 58
        new("vary", ""),                // 59
        new("via", ""),                 // 60
        new("www-authenticate", ""),    // 61
    ];

    #endregion

    #region HPACK Huffman Code Table (RFC 7541, Appendix B)

    /// <summary>
    /// HPACK Huffman encoding table. Index = symbol (0-255), value = (code, code_length_in_bits).
    /// Used for decoding Huffman-encoded header strings.
    /// </summary>
    private static readonly (uint Code, int Length)[] _HuffmanTable =
    [
        (0x1ff8, 13),     // 0
        (0x7fffd8, 23),   // 1
        (0xfffffe2, 28),  // 2
        (0xfffffe3, 28),  // 3
        (0xfffffe4, 28),  // 4
        (0xfffffe5, 28),  // 5
        (0xfffffe6, 28),  // 6
        (0xfffffe7, 28),  // 7
        (0xfffffe8, 28),  // 8
        (0xffffea, 24),   // 9
        (0x3ffffffc, 30), // 10
        (0xfffffe9, 28),  // 11
        (0xfffffea, 28),  // 12
        (0x3ffffffd, 30), // 13
        (0xfffffeb, 28),  // 14
        (0xfffffec, 28),  // 15
        (0xfffffed, 28),  // 16
        (0xfffffee, 28),  // 17
        (0xfffffef, 28),  // 18
        (0xffffff0, 28),  // 19
        (0xffffff1, 28),  // 20
        (0xffffff2, 28),  // 21
        (0x3ffffffe, 30), // 22
        (0xffffff3, 28),  // 23
        (0xffffff4, 28),  // 24
        (0xffffff5, 28),  // 25
        (0xffffff6, 28),  // 26
        (0xffffff7, 28),  // 27
        (0xffffff8, 28),  // 28
        (0xffffff9, 28),  // 29
        (0xffffffa, 28),  // 30
        (0xffffffb, 28),  // 31
        (0x14, 6),        // 32 ' '
        (0x3f8, 10),      // 33 '!'
        (0x3f9, 10),      // 34 '"'
        (0xffa, 12),      // 35 '#'
        (0x1ff9, 13),     // 36 '$'
        (0x15, 6),        // 37 '%'
        (0xf8, 8),        // 38 '&'
        (0x7fa, 11),      // 39 '\''
        (0x3fa, 10),      // 40 '('
        (0x3fb, 10),      // 41 ')'
        (0xf9, 8),        // 42 '*'
        (0x7fb, 11),      // 43 '+'
        (0xfa, 8),        // 44 ','
        (0x16, 6),        // 45 '-'
        (0x17, 6),        // 46 '.'
        (0x18, 6),        // 47 '/'
        (0x0, 5),         // 48 '0'
        (0x1, 5),         // 49 '1'
        (0x2, 5),         // 50 '2'
        (0x19, 6),        // 51 '3'
        (0x1a, 6),        // 52 '4'
        (0x1b, 6),        // 53 '5'
        (0x1c, 6),        // 54 '6'
        (0x1d, 6),        // 55 '7'
        (0x1e, 6),        // 56 '8'
        (0x1f, 6),        // 57 '9'
        (0x5c, 7),        // 58 ':'
        (0xfb, 8),        // 59 ';'
        (0x7ffc, 15),     // 60 '<'
        (0x20, 6),        // 61 '='
        (0xffb, 12),      // 62 '>'
        (0x3fc, 10),      // 63 '?'
        (0x1ffa, 13),     // 64 '@'
        (0x21, 6),        // 65 'A'
        (0x5d, 7),        // 66 'B'
        (0x5e, 7),        // 67 'C'
        (0x5f, 7),        // 68 'D'
        (0x60, 7),        // 69 'E'
        (0x61, 7),        // 70 'F'
        (0x62, 7),        // 71 'G'
        (0x63, 7),        // 72 'H'
        (0x64, 7),        // 73 'I'
        (0x65, 7),        // 74 'J'
        (0x66, 7),        // 75 'K'
        (0x67, 7),        // 76 'L'
        (0x68, 7),        // 77 'M'
        (0x69, 7),        // 78 'N'
        (0x6a, 7),        // 79 'O'
        (0x6b, 7),        // 80 'P'
        (0x6c, 7),        // 81 'Q'
        (0x6d, 7),        // 82 'R'
        (0x6e, 7),        // 83 'S'
        (0x6f, 7),        // 84 'T'
        (0x70, 7),        // 85 'U'
        (0x71, 7),        // 86 'V'
        (0x72, 7),        // 87 'W'
        (0xfc, 8),        // 88 'X'
        (0x73, 7),        // 89 'Y'
        (0xfd, 8),        // 90 'Z'
        (0x1ffb, 13),     // 91 '['
        (0x7fff0, 19),    // 92 '\\'
        (0x1ffc, 13),     // 93 ']'
        (0x3ffc, 14),     // 94 '^'
        (0x22, 6),        // 95 '_'
        (0x7ffd, 15),     // 96 '`'
        (0x3, 5),         // 97 'a'
        (0x23, 6),        // 98 'b'
        (0x4, 5),         // 99 'c'
        (0x24, 6),        // 100 'd'
        (0x5, 5),         // 101 'e'
        (0x25, 6),        // 102 'f'
        (0x26, 6),        // 103 'g'
        (0x27, 6),        // 104 'h'
        (0x6, 5),         // 105 'i'
        (0x74, 7),        // 106 'j'
        (0x75, 7),        // 107 'k'
        (0x28, 6),        // 108 'l'
        (0x29, 6),        // 109 'm'
        (0x2a, 6),        // 110 'n'
        (0x7, 5),         // 111 'o'
        (0x2b, 6),        // 112 'p'
        (0x76, 7),        // 113 'q'
        (0x2c, 6),        // 114 'r'
        (0x8, 5),         // 115 's'
        (0x9, 5),         // 116 't'
        (0x2d, 6),        // 117 'u'
        (0x77, 7),        // 118 'v'
        (0x78, 7),        // 119 'w'
        (0x79, 7),        // 120 'x'
        (0x7a, 7),        // 121 'y'
        (0x7b, 7),        // 122 'z'
        (0x7fffe, 19),    // 123 '{'
        (0x7fc, 11),      // 124 '|'
        (0x3ffd, 14),     // 125 '}'
        (0x1ffd, 13),     // 126 '~'
        (0xffffffc, 28),  // 127
        (0xfffe6, 20),    // 128
        (0x3fffd2, 22),   // 129
        (0xfffe7, 20),    // 130
        (0xfffe8, 20),    // 131
        (0x3fffd3, 22),   // 132
        (0x3fffd4, 22),   // 133
        (0x3fffd5, 22),   // 134
        (0x7fffd9, 23),   // 135
        (0x3fffd6, 22),   // 136
        (0x7fffda, 23),   // 137
        (0x7fffdb, 23),   // 138
        (0x7fffdc, 23),   // 139
        (0x7fffdd, 23),   // 140
        (0x7fffde, 23),   // 141
        (0xffffeb, 24),   // 142
        (0x7fffdf, 23),   // 143
        (0xffffec, 24),   // 144
        (0xffffed, 24),   // 145
        (0x3fffd7, 22),   // 146
        (0x7fffe0, 23),   // 147
        (0xffffee, 24),   // 148
        (0x7fffe1, 23),   // 149
        (0x7fffe2, 23),   // 150
        (0x7fffe3, 23),   // 151
        (0x7fffe4, 23),   // 152
        (0x1fffdc, 21),   // 153
        (0x3fffd8, 22),   // 154
        (0x7fffe5, 23),   // 155
        (0x3fffd9, 22),   // 156
        (0x7fffe6, 23),   // 157
        (0x7fffe7, 23),   // 158
        (0xffffef, 24),   // 159
        (0x3fffda, 22),   // 160
        (0x1fffdd, 21),   // 161
        (0xfffe9, 20),    // 162
        (0x3fffdb, 22),   // 163
        (0x3fffdc, 22),   // 164
        (0x7fffe8, 23),   // 165
        (0x7fffe9, 23),   // 166
        (0x1fffde, 21),   // 167
        (0x7fffea, 23),   // 168
        (0x3fffdd, 22),   // 169
        (0x3fffde, 22),   // 170
        (0xfffff0, 24),   // 171
        (0x1fffdf, 21),   // 172
        (0x3fffdf, 22),   // 173
        (0x7fffeb, 23),   // 174
        (0x7fffec, 23),   // 175
        (0x1fffe0, 21),   // 176
        (0x1fffe1, 21),   // 177
        (0x3fffe0, 22),   // 178
        (0x1fffe2, 21),   // 179
        (0x7fffed, 23),   // 180
        (0x3fffe1, 22),   // 181
        (0x7fffee, 23),   // 182
        (0x7fffef, 23),   // 183
        (0xfffea, 20),    // 184
        (0x3fffe2, 22),   // 185
        (0x3fffe3, 22),   // 186
        (0x3fffe4, 22),   // 187
        (0x7ffff0, 23),   // 188
        (0x3fffe5, 22),   // 189
        (0x3fffe6, 22),   // 190
        (0x7ffff1, 23),   // 191
        (0x3ffffe0, 26),  // 192
        (0x3ffffe1, 26),  // 193
        (0xfffeb, 20),    // 194
        (0x7fff1, 19),    // 195
        (0x3fffe7, 22),   // 196
        (0x7ffff2, 23),   // 197
        (0x3fffe8, 22),   // 198
        (0x1ffffec, 25),  // 199
        (0x3ffffe2, 26),  // 200
        (0x3ffffe3, 26),  // 201
        (0x3ffffe4, 26),  // 202
        (0x7ffffde, 27),  // 203
        (0x7ffffdf, 27),  // 204
        (0x3ffffe5, 26),  // 205
        (0xfffff1, 24),   // 206
        (0x1ffffed, 25),  // 207
        (0x7fff2, 19),    // 208
        (0x1fffe3, 21),   // 209
        (0x3ffffe6, 26),  // 210
        (0x7ffffe0, 27),  // 211
        (0x7ffffe1, 27),  // 212
        (0x3ffffe7, 26),  // 213
        (0x7ffffe2, 27),  // 214
        (0xfffff2, 24),   // 215
        (0x1fffe4, 21),   // 216
        (0x1fffe5, 21),   // 217
        (0x3ffffe8, 26),  // 218
        (0x3ffffe9, 26),  // 219
        (0xffffffd, 28),  // 220
        (0x7ffffe3, 27),  // 221
        (0x7ffffe4, 27),  // 222
        (0x7ffffe5, 27),  // 223
        (0xfffec, 20),    // 224
        (0xfffff3, 24),   // 225
        (0xfffed, 20),    // 226
        (0x1fffe6, 21),   // 227
        (0x3fffe9, 22),   // 228
        (0x1fffe7, 21),   // 229
        (0x1fffe8, 21),   // 230
        (0x7ffff3, 23),   // 231
        (0x3fffea, 22),   // 232
        (0x3fffeb, 22),   // 233
        (0x1ffffee, 25),  // 234
        (0x1ffffef, 25),  // 235
        (0xfffff4, 24),   // 236
        (0xfffff5, 24),   // 237
        (0x3ffffea, 26),  // 238
        (0x7ffff4, 23),   // 239
        (0x3ffffeb, 26),  // 240
        (0x7ffffe6, 27),  // 241
        (0x3ffffec, 26),  // 242
        (0x3ffffed, 26),  // 243
        (0x7ffffe7, 27),  // 244
        (0x7ffffe8, 27),  // 245
        (0x7ffffe9, 27),  // 246
        (0x7ffffea, 27),  // 247
        (0x7ffffeb, 27),  // 248
        (0xffffffe, 28),  // 249
        (0x7ffffec, 27),  // 250
        (0x7ffffed, 27),  // 251
        (0x7ffffee, 27),  // 252
        (0x7ffffef, 27),  // 253
        (0x7fffff0, 27),  // 254
        (0x3ffffee, 26),  // 255
    ];
    #endregion
}
