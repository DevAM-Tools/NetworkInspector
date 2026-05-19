// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// Provides efficient display text formatting for HTTP/2 frame flag combinations.
/// The 256-entry precomputed table (full 8-bit domain) eliminates per-packet string
/// building for the <c>http2.frame.flags</c> field.
/// <para>
/// HTTP/2 uses four distinct flag bit positions across all frame types (RFC 7540 §6):
/// <list type="bullet">
///   <item><description>0x01 — END_STREAM (DATA, HEADERS) / ACK (SETTINGS, PING)</description></item>
///   <item><description>0x04 — END_HEADERS (HEADERS, PUSH_PROMISE, CONTINUATION)</description></item>
///   <item><description>0x08 — PADDED (DATA, HEADERS, PUSH_PROMISE)</description></item>
///   <item><description>0x20 — PRIORITY (HEADERS)</description></item>
/// </list>
/// Bits 0x02, 0x10, 0x40, 0x80 are not defined for any standard frame type and are
/// listed as "0x{value}" when set.
/// </para>
/// <para>Output format: <c>0x05 [ES/ACK, END_HDRS]</c>.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from a pre-built immutable array;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class Http2FlagsFormatter
{
    // Full 256-entry table for the 8-bit flags byte.
    private static readonly string[] FlagsTable = BuildFlagsTable();

    /// <summary>
    /// Returns the display string for the given HTTP/2 frame flags byte.
    /// Output example: <c>0x05 [ES/ACK, END_HDRS]</c>.
    /// </summary>
    internal static string Format(byte flags) => FlagsTable[flags];

    private static string[] BuildFlagsTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = BuildFlagString((byte)i);
        }
        return table;
    }

    private static string BuildFlagString(byte flags)
    {
        // Known flag bit definitions
        ReadOnlySpan<(byte mask, string name)> knownFlags =
        [
            (0x01, "ES/ACK"),
            (0x04, "END_HDRS"),
            (0x08, "PADDED"),
            (0x20, "PRIORITY"),
        ];

        // Hex prefix is always included: "0x00"
        string hexPart = Helpers.DisplayTables.FormatHexU8(flags);

        if (flags == 0)
        {
            // Concatenate directly — both are pre-allocated literals.
            return hexPart + " [None]";
        }

        // Build the bracket-enclosed flag list.
        // First pass: compute total length.
        int totalLen = hexPart.Length + 3; // " [" and "]"
        int count = 0;
        for (int i = 0; i < knownFlags.Length; i++)
        {
            if ((flags & knownFlags[i].mask) != 0)
            {
                if (count > 0)
                {
                    totalLen += 2; // ", "
                }
                totalLen += knownFlags[i].name.Length;
                count++;
            }
        }
        // Reserve space for unknown bits rendered as "0xNN" (max 4 chars each, 4 unknown bits = at most 4 entries)
        // We use a growable approach: compute actual unknown names first.
        string[] unknownNames = BuildUnknownNames(flags);
        foreach (string name in unknownNames)
        {
            if (count > 0)
            {
                totalLen += 2; // ", "
            }
            totalLen += name.Length;
            count++;
        }

        // Second pass: fill the string.
        return string.Create(totalLen, (hexPart, flags, knownFlags: knownFlags.ToArray(), unknownNames), static (chars, state) =>
        {
            state.hexPart.AsSpan().CopyTo(chars);
            int written = state.hexPart.Length;
            chars[written++] = ' ';
            chars[written++] = '[';
            bool first = true;

            for (int i = 0; i < state.knownFlags.Length; i++)
            {
                if ((state.flags & state.knownFlags[i].mask) != 0)
                {
                    if (!first)
                    {
                        chars[written++] = ',';
                        chars[written++] = ' ';
                    }
                    state.knownFlags[i].name.AsSpan().CopyTo(chars[written..]);
                    written += state.knownFlags[i].name.Length;
                    first = false;
                }
            }
            foreach (string name in state.unknownNames)
            {
                if (!first)
                {
                    chars[written++] = ',';
                    chars[written++] = ' ';
                }
                name.AsSpan().CopyTo(chars[written..]);
                written += name.Length;
                first = false;
            }

            chars[written] = ']';
        });
    }

    /// <summary>
    /// Builds display names for any set bits that are not covered by the four known flag positions.
    /// Returns an empty array when all set bits are known.
    /// </summary>
    private static string[] BuildUnknownNames(byte flags)
    {
        // Unknown bit positions: 0x02, 0x10, 0x40, 0x80
        ReadOnlySpan<byte> unknownMasks = [0x02, 0x10, 0x40, 0x80];
        int count = 0;
        for (int i = 0; i < unknownMasks.Length; i++)
        {
            if ((flags & unknownMasks[i]) != 0)
            {
                count++;
            }
        }
        if (count == 0)
        {
            return [];
        }
        string[] result = new string[count];
        int idx = 0;
        for (int i = 0; i < unknownMasks.Length; i++)
        {
            if ((flags & unknownMasks[i]) != 0)
            {
                result[idx++] = Helpers.DisplayTables.FormatHexU8(unknownMasks[i]);
            }
        }
        return result;
    }
}
