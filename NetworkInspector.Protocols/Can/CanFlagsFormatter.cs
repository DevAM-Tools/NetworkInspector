// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Can;

/// <summary>
/// Provides efficient display text formatting for CAN, CAN FD, and CAN XL flag combinations.
/// Each variant has a precomputed lookup table indexed by packed flag bits, eliminating
/// per-packet string building.
/// <list type="bullet">
///   <item><description>
///     Classic CAN: 8-entry table (3 flag bits: XTD, RTR, ERR).
///   </description></item>
///   <item><description>
///     CAN FD: 32-entry table (5 variable flag bits: XTD, RTR, ERR, BRS, ESI);
///     "FD" is always prepended since the FD frame indicator is structurally always set.
///   </description></item>
///   <item><description>
///     CAN XL: 4-entry table (2 variable flag bits: SEC, RRS);
///     "XLF" is always prepended since the XL frame indicator is structurally always set.
///   </description></item>
/// </list>
/// <para>Output format: <c>[FLAG1, FLAG2]</c> for set flags, <c>[None]</c> when no flags are set.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from pre-built immutable arrays;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class CanFlagsFormatter
{
    #region Classic CAN (3 flags)

    // Key packing: bit0=XTD, bit1=RTR, bit2=ERR
    private static readonly string[] ClassicFlagsTable = BuildClassicFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given classic CAN flag combination.
    /// Output example: <c>[XTD, RTR]</c> or <c>[None]</c>.
    /// </summary>
    internal static string FormatClassic(bool xtd, bool rtr, bool err)
    {
        int key = (xtd ? 1 : 0) | (rtr ? 2 : 0) | (err ? 4 : 0);
        return ClassicFlagsTable[key];
    }

    private static string[] BuildClassicFlagsTable()
    {
        string[] table = new string[8];
        for (int i = 0; i < 8; i++)
        {
            table[i] = BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0, (i & 4) != 0],
                ["XTD", "RTR", "ERR"],
                prefix: null);
        }
        return table;
    }

    #endregion

    #region CAN FD (FD always shown + 5 variable flags)

    // Key packing: bit0=XTD, bit1=RTR, bit2=ERR, bit3=BRS, bit4=ESI
    // "FD" is always prepended — the FDF flag is structurally true for all FD frames.
    private static readonly string[] FdFlagsTable = BuildFdFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given CAN FD flag combination.
    /// "FD" is always the first entry. Output example: <c>[FD, BRS]</c>.
    /// </summary>
    internal static string FormatFd(bool xtd, bool rtr, bool err, bool brs, bool esi)
    {
        int key = (xtd ? 1 : 0) | (rtr ? 2 : 0) | (err ? 4 : 0) | (brs ? 8 : 0) | (esi ? 16 : 0);
        return FdFlagsTable[key];
    }

    private static string[] BuildFdFlagsTable()
    {
        string[] table = new string[32];
        for (int i = 0; i < 32; i++)
        {
            table[i] = BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0, (i & 4) != 0, (i & 8) != 0, (i & 16) != 0],
                ["XTD", "RTR", "ERR", "BRS", "ESI"],
                prefix: "FD");
        }
        return table;
    }

    #endregion

    #region CAN XL (XLF always shown + 2 variable flags)

    // Key packing: bit0=SEC, bit1=RRS
    // "XLF" is always prepended — the XL frame indicator is structurally always set.
    private static readonly string[] XlFlagsTable = BuildXlFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given CAN XL flag combination.
    /// "XLF" is always the first entry. Output example: <c>[XLF, SEC]</c>.
    /// </summary>
    internal static string FormatXl(bool sec, bool rrs)
    {
        int key = (sec ? 1 : 0) | (rrs ? 2 : 0);
        return XlFlagsTable[key];
    }

    private static string[] BuildXlFlagsTable()
    {
        string[] table = new string[4];
        for (int i = 0; i < 4; i++)
        {
            table[i] = BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0],
                ["SEC", "RRS"],
                prefix: "XLF");
        }
        return table;
    }

    #endregion

    #region Shared string builder

    /// <summary>
    /// Builds a bracket-enclosed flag string for the given active-flag booleans and names.
    /// An optional <paramref name="prefix"/> is always inserted as the first entry.
    /// Example output: <c>[FD, BRS]</c>, <c>[None]</c> (when no variable flags set and no prefix).
    /// </summary>
    private static string BuildFlagString(bool[] active, string[] names, string? prefix)
    {
        // Single pass: accumulate total length and detect early-exit (all-false with no prefix).
        // Format: "[" + entries joined by ", " + "]"
        int totalLen = 2; // "[" and "]"
        bool first = true;

        if (prefix is not null)
        {
            totalLen += prefix.Length;
            first = false;
        }
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

        if (first) // No entries (no prefix, no active flags)
        {
            return "[None]";
        }

        // Second pass: fill the string.
        return string.Create(totalLen, (prefix, active, names), static (chars, state) =>
        {
            chars[0] = '[';
            int written = 1;
            bool firstChar = true;

            if (state.prefix is not null)
            {
                state.prefix.AsSpan().CopyTo(chars[written..]);
                written += state.prefix.Length;
                firstChar = false;
            }
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
