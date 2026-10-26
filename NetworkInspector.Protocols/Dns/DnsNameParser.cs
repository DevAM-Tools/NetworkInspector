// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Dns;

/// <summary>
/// Zero-allocation DNS label decompression (RFC 1035 Section 4.1.4): the name is assembled in a stack buffer,
/// the only heap allocation is the final string returned by <see cref="ReadName"/>.
/// Supports label pointers (0xC0xx) with cycle detection via max depth.
/// <para>
/// Security: names come straight from the wire, so every read is bounds checked and every loop is bounded. A
/// compression pointer may point forward or into itself (a loop); the parser does not track visited offsets or require
/// backward pointers, it only limits the pointer chain depth, which also bounds the recursion depth. The amount of
/// text copied is bounded by the fixed 256-character output buffer. Residual risk: label bytes are copied as-is
/// (Latin-1) without escaping, so a label may contain dots, spaces or control characters that make the displayed name
/// ambiguous; callers that use the name for anything but display must not trust its structure.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> stateless; all buffers are per-call stack memory, safe for concurrent use.</para>
/// </remarks>
internal static class DnsNameParser
{
    /// <summary>
    /// Maximum pointer chain depth to prevent infinite loops. Limit: 64 hops; threat: a pointer loop or a very long
    /// chain (CPU and stack exhaustion from one hostile packet); a fixed constant in code, not configurable.
    /// The depth check runs on entry, so up to 65 nested calls are possible.
    /// </summary>
    private const int _MaxPointerDepth = 64;

    /// <summary>Maximum label length (63 bytes per RFC 1035: the top two bits of the length byte are the pointer flag).</summary>
    private const int _MaxLabelLength = 63;

    /// <summary>Pointer indicator mask (top 2 bits = 11).</summary>
    private const byte _PointerMask = 0xC0;

    /// <summary>
    /// Reads a DNS domain name from the data at the given offset.
    /// Returns the decoded name as a string and advances <paramref name="offset"/>
    /// past the name (only at the first level — pointer offsets don't advance the caller's offset).
    /// A name that ends in a pointer advances the offset past the two pointer bytes.
    /// </summary>
    /// <param name="fullPacket">The entire DNS packet data (needed for pointer resolution).</param>
    /// <param name="offset">
    /// Current read position; updated to point past the name on success. On failure the value is unspecified
    /// within the packet: it is left unchanged when a label is malformed, and otherwise points past the last
    /// bytes that were consumed at the first level.
    /// </param>
    /// <returns>
    /// The decoded domain name without trailing dot, or an empty string on error and for the root name (a single
    /// zero byte). The two cases cannot be told apart from the result.
    /// </returns>
    internal static string ReadName(ReadOnlySpan<byte> fullPacket, ref int offset)
    {
        // A valid name is at most 255 bytes on the wire, which is at most 254 characters with the trailing dot, so a
        // 512-byte stack buffer (256 chars) always fits it: no heap buffer or StringBuilder is needed, and a longer
        // (malformed) name is rejected by the length check in _ReadNameCore rather than growing the buffer.
        Span<char> buffer = stackalloc char[256];
        int written = 0;
        bool success = _ReadNameCore(fullPacket, ref offset, buffer, ref written, depth: 0, firstLevel: true);

        if (!success || written == 0)
        {
            return string.Empty;
        }

        // Every label appends "label."; drop the final dot so the result reads "www.example.com". The only
        // allocation of the whole read happens in the next statement.
        if (written > 0 && buffer[written - 1] == '.')
        {
            written--;
        }

        return new string(buffer[..written]);
    }

    /// <summary>
    /// Advances <paramref name="offset"/> past a DNS domain name without materializing the decoded
    /// string. Used by the eager index-group detector, which only needs the post-name read position
    /// to walk resource records and must not allocate a throwaway name string per record on the parse
    /// hot path. Reuses the identical <see cref="_ReadNameCore"/> traversal as <see cref="ReadName"/>,
    /// so the resulting offset is byte-for-byte the same — the detector cannot drift from the populator.
    /// </summary>
    /// <param name="fullPacket">The entire DNS packet data (needed for pointer resolution).</param>
    /// <param name="offset">Current read position; updated to point past the name.</param>
    internal static void SkipName(ReadOnlySpan<byte> fullPacket, ref int offset)
    {
        // Same traversal and offset progression as ReadName; the stackalloc buffer is required by
        // _ReadNameCore but its contents are intentionally discarded (no final string allocation).
        Span<char> buffer = stackalloc char[256];
        int written = 0;
        _ = _ReadNameCore(fullPacket, ref offset, buffer, ref written, depth: 0, firstLevel: true);
    }

    /// <summary>
    /// Core recursive name reader. Follows pointer chains with depth limiting. Recursion happens only at a
    /// pointer (at most <c>_MaxPointerDepth + 1</c> nested calls), never per label, so stack use stays small.
    /// </summary>
    /// <param name="data">Full DNS packet.</param>
    /// <param name="offset">Current offset — only advanced at the first (non-pointer) level.</param>
    /// <param name="buffer">Output buffer for characters.</param>
    /// <param name="written">Number of chars written to buffer so far.</param>
    /// <param name="depth">Current pointer recursion depth.</param>
    /// <param name="firstLevel">True if this is the top-level call (advances offset).</param>
    /// <returns>True on success, false on malformed data.</returns>
    private static bool _ReadNameCore(
        ReadOnlySpan<byte> data, ref int offset,
        Span<char> buffer, ref int written,
        int depth, bool firstLevel)
    {
        if (depth > _MaxPointerDepth)
        {
            return false; // Probable infinite pointer loop
        }

        // pos walks the labels of this level. The caller's offset is only assigned at the first level (see the
        // comments below) because a pointer target lies elsewhere in the message and must not move the caller.
        // A pointer returns from this method right after it is handled, so no label of this level is read after
        // a pointer and the terminator and end-of-data branches below never need to know whether one was seen.
        int pos = offset;

        while (pos < data.Length)
        {
            byte labelLen = data[pos];

            // End of name (null terminator)
            if (labelLen == 0)
            {
                if (firstLevel)
                {
                    offset = pos + 1; // Skip the null byte
                }
                return true;
            }

            // Pointer (top 2 bits = 11): a 14-bit offset from the start of the message (RFC 1035 §4.1.4). A pointer
            // always ends the name at this level: the recursive call below is in return position and the loop never
            // continues after it. The bit patterns 01 and 10 (extended label types) are rejected as invalid labels further down.
            if ((labelLen & _PointerMask) == _PointerMask)
            {
                if (pos + 1 >= data.Length)
                {
                    return false;
                }

                // Record the offset past the pointer (only at the first level: a followed pointer must not move the caller)
                if (firstLevel)
                {
                    offset = pos + 2;
                }

                // Follow the pointer
                int pointerTarget = ((labelLen & 0x3F) << 8) | data[pos + 1];
                if (pointerTarget >= data.Length)
                {
                    return false;
                }

                int ptrOffset = pointerTarget;
                return _ReadNameCore(data, ref ptrOffset, buffer, ref written, depth + 1, firstLevel: false);
            }

            // Regular label. Lengths above 63 can only be the reserved 01 / 10 prefixes here (11 was handled above).
            if (labelLen > _MaxLabelLength)
            {
                return false;
            }

            pos++; // Skip past the length byte

            if (pos + labelLen > data.Length)
            {
                return false;
            }

            // Copy label characters to the buffer (each byte becomes the char with the same code, i.e. Latin-1; no
            // escaping). The check reserves room for the label plus its trailing dot, so the writes below can never
            // run past the buffer. It also caps the name length, since each label adds at least two characters.
            if (written + labelLen + 1 > buffer.Length)
            {
                return false; // Name too long for buffer
            }

            for (int i = 0; i < labelLen; i++)
            {
                buffer[written++] = (char)data[pos + i];
            }

            // Add dot separator
            buffer[written++] = '.';
            pos += labelLen;
        }

        // Reached end of data without null terminator — update offset if at first level
        if (firstLevel)
        {
            offset = pos;
        }

        return false;
    }

    /// <summary>
    /// Parses a DNS resource record's RDATA section for an A record (4 bytes IPv4).
    /// Returns the IPv4 address, or the default (all-zero) address when <paramref name="rdata"/> is shorter than 4 bytes
    /// (callers check the length first). Extra trailing bytes are ignored.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv4Address ParseARecord(ReadOnlySpan<byte> rdata) =>
        rdata.Length >= 4
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(rdata))
            : default;

    /// <summary>
    /// Parses a DNS resource record's RDATA section for an AAAA record (16 bytes IPv6).
    /// Returns the IPv6 address, or the default (all-zero) address when <paramref name="rdata"/> is shorter than
    /// 16 bytes (callers check the length first). Extra trailing bytes are ignored.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address ParseAAAARecord(ReadOnlySpan<byte> rdata) =>
        rdata.Length >= 16
            ? new IPv6Address(
                BinaryPrimitives.ReadUInt64BigEndian(rdata),
                BinaryPrimitives.ReadUInt64BigEndian(rdata[8..]))
            : default;
}
