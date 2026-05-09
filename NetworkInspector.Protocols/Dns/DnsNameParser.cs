// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System.Text;

namespace NetworkInspector.Protocols.Dns;

/// <summary>
/// Zero-allocation DNS label decompression (RFC 1035 Section 4.1.4).
/// Supports label pointers (0xC0xx) with cycle detection via max depth.
/// </summary>
internal static class DnsNameParser
{
    /// <summary>Maximum pointer chain depth to prevent infinite loops.</summary>
    private const int MaxPointerDepth = 64;

    /// <summary>Maximum label length (63 bytes per RFC 1035).</summary>
    private const int MaxLabelLength = 63;

    /// <summary>Pointer indicator mask (top 2 bits = 11).</summary>
    private const byte PointerMask = 0xC0;

    /// <summary>
    /// Reads a DNS domain name from the data at the given offset.
    /// Returns the decoded name as a string and advances <paramref name="offset"/>
    /// past the name (only at the first level — pointer offsets don't advance the caller's offset).
    /// </summary>
    /// <param name="fullPacket">The entire DNS packet data (needed for pointer resolution).</param>
    /// <param name="offset">Current read position; updated to point past the name on success.</param>
    /// <returns>The decoded domain name, or an empty string on error.</returns>
    internal static string ReadName(ReadOnlySpan<byte> fullPacket, ref int offset)
    {
        // Use stackalloc for small names (< 256 chars), fall back to StringBuilder otherwise
        Span<char> buffer = stackalloc char[256];
        int written = 0;
        bool success = ReadNameCore(fullPacket, ref offset, buffer, ref written, depth: 0, firstLevel: true);

        if (!success || written == 0)
        {
            return string.Empty;
        }

        // Remove trailing dot if present
        if (written > 0 && buffer[written - 1] == '.')
        {
            written--;
        }

        return new string(buffer[..written]);
    }

    /// <summary>
    /// Core recursive name reader. Follows pointer chains with depth limiting.
    /// </summary>
    /// <param name="data">Full DNS packet.</param>
    /// <param name="offset">Current offset — only advanced at the first (non-pointer) level.</param>
    /// <param name="buffer">Output buffer for characters.</param>
    /// <param name="written">Number of chars written to buffer so far.</param>
    /// <param name="depth">Current pointer recursion depth.</param>
    /// <param name="firstLevel">True if this is the top-level call (advances offset).</param>
    /// <returns>True on success, false on malformed data.</returns>
    private static bool ReadNameCore(
        ReadOnlySpan<byte> data, ref int offset,
        Span<char> buffer, ref int written,
        int depth, bool firstLevel)
    {
        if (depth > MaxPointerDepth)
        {
            return false; // Probable infinite pointer loop
        }

        int pos = offset;
        bool pointerSeen = false;

        while (pos < data.Length)
        {
            byte labelLen = data[pos];

            // End of name (null terminator)
            if (labelLen == 0)
            {
                if (firstLevel && !pointerSeen)
                {
                    offset = pos + 1; // Skip the null byte
                }
                return true;
            }

            // Pointer (top 2 bits = 11)
            if ((labelLen & PointerMask) == PointerMask)
            {
                if (pos + 1 >= data.Length)
                {
                    return false;
                }

                // Record the offset past the pointer (only on first encounter at this level)
                if (firstLevel && !pointerSeen)
                {
                    offset = pos + 2;
                    pointerSeen = true;
                }

                // Follow the pointer
                int pointerTarget = ((labelLen & 0x3F) << 8) | data[pos + 1];
                if (pointerTarget >= data.Length)
                {
                    return false;
                }

                int ptrOffset = pointerTarget;
                return ReadNameCore(data, ref ptrOffset, buffer, ref written, depth + 1, firstLevel: false);
            }

            // Regular label
            if (labelLen > MaxLabelLength)
            {
                return false;
            }

            pos++; // Skip past the length byte

            if (pos + labelLen > data.Length)
            {
                return false;
            }

            // Copy label characters (ASCII) to buffer
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
        if (firstLevel && !pointerSeen)
        {
            offset = pos;
        }

        return false;
    }

    /// <summary>
    /// Parses a DNS resource record's RDATA section for an A record (4 bytes IPv4).
    /// Returns the IPv4 address as a string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv4Address ParseARecord(ReadOnlySpan<byte> rdata) =>
        rdata.Length >= 4
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(rdata))
            : default;

    /// <summary>
    /// Parses a DNS resource record's RDATA section for an AAAA record (16 bytes IPv6).
    /// Returns the IPv6 address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address ParseAAAARecord(ReadOnlySpan<byte> rdata) =>
        rdata.Length >= 16
            ? new IPv6Address(
                BinaryPrimitives.ReadUInt64BigEndian(rdata),
                BinaryPrimitives.ReadUInt64BigEndian(rdata[8..]))
            : default;
}
