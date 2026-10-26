// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Provides efficient display text formatting for ICMPv6 Neighbor Discovery Protocol flag combinations.
/// Precomputed lookup tables eliminate per-packet string building for the
/// <c>icmpv6.nd.ra.flags</c> and <c>icmpv6.nd.na.flags</c> container fields.
/// <list type="bullet">
///   <item><description>
///     Router Advertisement flags (2 bits: M, O): 4-entry table.
///   </description></item>
///   <item><description>
///     Neighbor Advertisement flags (3 bits: R, S, O): 8-entry table.
///   </description></item>
/// </list>
/// <para>Output format: <c>[M, O]</c> for set flags, <c>[None]</c> when no flags are set.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from pre-built immutable arrays;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class Icmpv6NdpFlagsFormatter
{
    #region Router Advertisement flags (2 flags)

    // Key packing: bit0=M (Managed), bit1=O (Other). The key is only a table index; it is deliberately not the wire
    // bit order (M is 0x80 and O is 0x40 in the Router Advertisement flags byte), which the parser decodes itself.
    // The table is built once when the type is initialized, so formatting a packet is an array lookup and returns a
    // shared string instead of building a new one per packet.
    private static readonly string[] _RaFlagsTable = _BuildRaFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given Router Advertisement flag combination.
    /// Output example: <c>[M, O]</c>, <c>[M]</c>, or <c>[None]</c>.
    /// </summary>
    /// <param name="managed">Managed address configuration flag (M).</param>
    /// <param name="other">Other configuration flag (O).</param>
    internal static string FormatRa(bool managed, bool other)
    {
        int key = (managed ? 1 : 0) | (other ? 2 : 0);
        return _RaFlagsTable[key];
    }

    /// <summary>Builds the 4-entry Router Advertisement table; entry <c>i</c> is the text for key <c>i</c>.</summary>
    private static string[] _BuildRaFlagsTable()
    {
        string[] table = new string[4];
        for (int i = 0; i < 4; i++)
        {
            table[i] = _BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0],
                ["M", "O"]);
        }
        return table;
    }

    #endregion

    #region Neighbor Advertisement flags (3 flags)

    // Key packing: bit0=R (Router), bit1=S (Solicited), bit2=O (Override). Again a table index only; on the wire R, S
    // and O are the top three bits (0x80000000, 0x40000000, 0x20000000) of the 32-bit flags word.
    private static readonly string[] _NaFlagsTable = _BuildNaFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given Neighbor Advertisement flag combination.
    /// Output example: <c>[S, O]</c>, <c>[R, S]</c>, or <c>[None]</c>.
    /// </summary>
    /// <param name="router">Router flag (R).</param>
    /// <param name="solicited">Solicited flag (S).</param>
    /// <param name="overrideFlag">Override flag (O).</param>
    internal static string FormatNa(bool router, bool solicited, bool overrideFlag)
    {
        int key = (router ? 1 : 0) | (solicited ? 2 : 0) | (overrideFlag ? 4 : 0);
        return _NaFlagsTable[key];
    }

    /// <summary>Builds the 8-entry Neighbor Advertisement table; entry <c>i</c> is the text for key <c>i</c>.</summary>
    private static string[] _BuildNaFlagsTable()
    {
        string[] table = new string[8];
        for (int i = 0; i < 8; i++)
        {
            table[i] = _BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0, (i & 4) != 0],
                ["R", "S", "O"]);
        }
        return table;
    }

    #endregion

    #region Shared string builder

    /// <summary>
    /// Formats the names of the active flags as <c>[A, B]</c>, or <c>[None]</c>. Only used while the tables are
    /// built, so it is written for clarity (arrays, two passes) rather than for speed; the string is exactly sized
    /// via <c>string.Create</c> so no intermediate builder is needed.
    /// </summary>
    /// <param name="active">Which flags are set; same length and order as <paramref name="names"/>.</param>
    /// <param name="names">Display name of each flag.</param>
    private static string _BuildFlagString(bool[] active, string[] names)
    {
        int count = 0;
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i])
            {
                count++;
            }
        }

        if (count == 0)
        {
            return "[None]";
        }

        // First pass: compute total length.
        int totalLen = 2; // "[" and "]"
        bool first = true;
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i])
            {
                if (!first)
                {
                    totalLen += 2; // ", "
                }
                totalLen += names[i].Length;
                first = false;
            }
        }

        // Second pass: fill the string.
        return string.Create(totalLen, (active, names), static (chars, state) =>
        {
            chars[0] = '[';
            int written = 1;
            bool firstChar = true;
            for (int i = 0; i < state.active.Length; i++)
            {
                if (state.active[i])
                {
                    if (!firstChar)
                    {
                        chars[written++] = ',';
                        chars[written++] = ' ';
                    }
                    state.names[i].AsSpan().CopyTo(chars[written..]);
                    written += state.names[i].Length;
                    firstChar = false;
                }
            }
            chars[written] = ']';
        });
    }

    #endregion
}
