// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Provides display text for the flag bytes of ICMPv6 Neighbor Discovery messages without building a string per
/// packet. Every text is computed once when the type is initialised and handed out as a shared string.
/// <list type="bullet">
///   <item><description>
///     Router Advertisement flags (one byte: M, O, H, Prf, P, S): a 256-entry table indexed by the raw flags byte.
///   </description></item>
///   <item><description>
///     Neighbor Advertisement flags (3 bits: R, S, O): an 8-entry table for the container and one for the packet
///     info text.
///   </description></item>
/// </list>
/// <para>Output format: <c>[M, O]</c> for set flags, <c>[None]</c> when no flag is set, <c>Prf=High</c> for a default
/// router preference other than Medium.</para>
/// </summary>
/// <remarks>
/// <para><b>Why tables:</b> a Router Advertisement is sent every few seconds by every router and a Neighbor
/// Advertisement answers every neighbour lookup, so a capture holds thousands of them. The flag byte has only 256
/// values (the Neighbor Advertisement flags 8), so one shared string per value is cheaper than any per-packet
/// formatting: the call site boxes nothing, and the lazy-text machinery shares the string reference.</para>
/// <para><b>Why the table index is the raw byte:</b> the parser then needs no bit shuffling to select a text. The
/// Router Advertisement table is indexed by the wire byte directly; the Neighbor Advertisement table is indexed by a
/// compact 3-bit key because its flags live in the top bits of a 32-bit word.</para>
/// <para><b>Thread safety:</b> All members are static and read from arrays that are built once and never changed;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class Icmpv6NdpFlagsFormatter
{
    #region Router Advertisement flags (one byte)

    // Wire layout of the RA flags byte (RFC 4861 section 4.2, RFC 6275 H, RFC 4191 Prf, RFC 4389 P, RFC 6275 S):
    //   bit 7 M   managed address configuration
    //   bit 6 O   other configuration
    //   bit 5 H   home agent
    //   bits 4-3  Prf  default router preference (00 Medium, 01 High, 10 Reserved, 11 Low)
    //   bit 2 P   ND proxy
    //   bit 1 S   SNAC router
    //   bit 0     reserved (shown as its own field, not in the text)
    // The table is indexed by the raw byte so the parser does a single array read.
    private static readonly string[] _RaFlagsTable = _BuildRaFlagsTable();

    private static readonly string[] _RouterPreferenceTable =
    [
        "Medium (0)",
        "High (1)",
        "Reserved (2)",
        "Low (3)",
    ];

    /// <summary>
    /// Returns the precomputed display string for a Router Advertisement flags byte.
    /// Output example: <c>[M, O]</c>, <c>[M, H, Prf=High]</c> or <c>[None]</c>.
    /// </summary>
    /// <param name="flags">The raw flags byte of the Router Advertisement.</param>
    /// <remarks>The reserved bit (0x01) does not change the text; the "Medium" preference is the default and is
    /// omitted so that a plain advertisement keeps its short <c>[None]</c> / <c>[M, O]</c> form.</remarks>
    internal static string FormatRa(byte flags) => _RaFlagsTable[flags];

    /// <summary>
    /// Returns the display text of a 2-bit default router preference value (RFC 4191 section 2.1).
    /// </summary>
    /// <param name="preference">The preference bits already shifted down to 0..3; higher bits are ignored.</param>
    internal static string RouterPreferenceText(byte preference) => _RouterPreferenceTable[preference & 0x03];

    /// <summary>Builds the 256-entry Router Advertisement table; entry <c>i</c> is the text for flags byte <c>i</c>.</summary>
    private static string[] _BuildRaFlagsTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < table.Length; i++)
        {
            int preference = (i & 0x18) >> 3;
            string preferenceName = preference switch
            {
                1 => "Prf=High",
                2 => "Prf=Reserved",
                3 => "Prf=Low",
                _ => string.Empty, // Medium is the default and stays out of the text
            };
            table[i] = _BuildFlagString(
                [(i & 0x80) != 0, (i & 0x40) != 0, (i & 0x20) != 0, preferenceName.Length > 0, (i & 0x04) != 0, (i & 0x02) != 0],
                ["M", "O", "H", preferenceName, "P", "S"]);
        }
        return table;
    }

    #endregion

    #region Neighbor Advertisement flags (3 flags)

    // Key packing: bit0=R (Router), bit1=S (Solicited), bit2=O (Override). The key is only a table index; on the wire
    // R, S and O are the top three bits (0x80000000, 0x40000000, 0x20000000) of the 32-bit flags word.
    private static readonly string[] _NaFlagsTable = _BuildNaFlagsTable();

    // Packet-info form ("rtr, sol, ovr"): the short words the neighbour table of an operator expects. No brackets,
    // and "none" instead of "[None]" because it is appended to a sentence, not shown as a flag container.
    private static readonly string[] _NaInfoTable = _BuildNaInfoTable();

    /// <summary>
    /// Returns the precomputed display string for the given Neighbor Advertisement flag combination.
    /// Output example: <c>[S, O]</c>, <c>[R, S]</c>, or <c>[None]</c>.
    /// </summary>
    /// <param name="router">Router flag (R).</param>
    /// <param name="solicited">Solicited flag (S).</param>
    /// <param name="overrideFlag">Override flag (O).</param>
    internal static string FormatNa(bool router, bool solicited, bool overrideFlag) => _NaFlagsTable[NaKey(router, solicited, overrideFlag)];

    /// <summary>
    /// Packs the three Neighbor Advertisement flags into the table key: bit 0 is R, bit 1 is S, bit 2 is O. The key is only a
    /// table index and not the wire layout.
    /// </summary>
    /// <param name="router">Router flag (R).</param>
    /// <param name="solicited">Solicited flag (S).</param>
    /// <param name="overrideFlag">Override flag (O).</param>
    /// <returns>A value from 0 to 7.</returns>
    internal static int NaKey(bool router, bool solicited, bool overrideFlag)
    {
        int key = 0;
        if (router)
        {
            key |= 1;
        }

        if (solicited)
        {
            key |= 2;
        }

        if (overrideFlag)
        {
            key |= 4;
        }

        return key;
    }

    /// <summary>
    /// Returns the packet-info form of the Neighbor Advertisement flags: <c>rtr, sol, ovr</c> or <c>none</c>.
    /// </summary>
    /// <param name="router">Router flag (R).</param>
    /// <param name="solicited">Solicited flag (S).</param>
    /// <param name="overrideFlag">Override flag (O).</param>
    internal static string FormatNaInfo(bool router, bool solicited, bool overrideFlag) => _NaInfoTable[NaKey(router, solicited, overrideFlag)];

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

    /// <summary>Builds the 8-entry packet-info table for the Neighbor Advertisement flags.</summary>
    private static string[] _BuildNaInfoTable()
    {
        string[] table = new string[8];
        string[] names = ["rtr", "sol", "ovr"];
        for (int i = 0; i < 8; i++)
        {
            List<string> active = new(3);
            for (int bit = 0; bit < 3; bit++)
            {
                if ((i & (1 << bit)) != 0)
                {
                    active.Add(names[bit]);
                }
            }

            table[i] = active.Count == 0
                ? "none"
                : string.Join(", ", active);
        }
        return table;
    }

    #endregion

    #region Shared string builder

    /// <summary>
    /// Formats the names of the active flags as <c>[A, B]</c>, or <c>[None]</c>. Only used while the tables are
    /// built, so it is written for clarity rather than for speed.
    /// </summary>
    /// <param name="active">Which flags are set; same length and order as <paramref name="names"/>.</param>
    /// <param name="names">Display name of each flag.</param>
    private static string _BuildFlagString(bool[] active, string[] names)
    {
        List<string> items = new(active.Length);
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i])
            {
                items.Add(names[i]);
            }
        }

        if (items.Count == 0)
        {
            return "[None]";
        }

        return string.Concat("[", string.Join(", ", items), "]");
    }

    #endregion
}
