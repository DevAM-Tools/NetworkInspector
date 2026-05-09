// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols;

/// <summary>
/// Provides efficient display text formatting for FlexRay frame indicator and error flag combinations.
/// Two precomputed lookup tables eliminate per-packet string building:
/// <list type="bullet">
///   <item><description>
///     Frame indicators (4 bits: PPI, NFI, SFI, STFI): 16-entry table for the
///     <c>flexray.flags</c> container field.
///   </description></item>
///   <item><description>
///     Error flags (5 bits: FCRC_ERR, HCRC_ERR, FES_ERR, COD_ERR, TSS_VIOL): 32-entry table
///     for the <c>flexray.err_flags</c> container field.
///   </description></item>
/// </list>
/// <para>Output format: <c>[NFI, SFI]</c> for set flags, <c>[None]</c> when no flags are set.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from pre-built immutable arrays;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class FlexRayFlagsFormatter
{
    #region Frame indicators (4 flags)

    // Key packing: bit0=PPI, bit1=NFI, bit2=SFI, bit3=STFI
    private static readonly string[] IndicatorFlagsTable = BuildIndicatorFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given FlexRay frame indicator combination.
    /// Output example: <c>[NFI, SFI]</c> or <c>[None]</c>.
    /// </summary>
    internal static string FormatIndicators(bool ppi, bool nfi, bool sfi, bool stfi)
    {
        int key = (ppi ? 1 : 0) | (nfi ? 2 : 0) | (sfi ? 4 : 0) | (stfi ? 8 : 0);
        return IndicatorFlagsTable[key];
    }

    private static string[] BuildIndicatorFlagsTable()
    {
        string[] table = new string[16];
        for (int i = 0; i < 16; i++)
        {
            table[i] = BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0, (i & 4) != 0, (i & 8) != 0],
                ["PPI", "NFI", "SFI", "STFI"]);
        }
        return table;
    }

    #endregion

    #region Error flags (5 flags)

    // Key packing: bit0=FCRC_ERR, bit1=HCRC_ERR, bit2=FES_ERR, bit3=COD_ERR, bit4=TSS_VIOL
    private static readonly string[] ErrorFlagsTable = BuildErrorFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given FlexRay error flag combination.
    /// Output example: <c>[FCRC_ERR, TSS_VIOL]</c> or <c>[None]</c>.
    /// </summary>
    internal static string FormatErrors(bool fcrcErr, bool hcrcErr, bool fesErr, bool codErr, bool tssViol)
    {
        int key = (fcrcErr ? 1 : 0) | (hcrcErr ? 2 : 0) | (fesErr ? 4 : 0) | (codErr ? 8 : 0) | (tssViol ? 16 : 0);
        return ErrorFlagsTable[key];
    }

    private static string[] BuildErrorFlagsTable()
    {
        string[] table = new string[32];
        for (int i = 0; i < 32; i++)
        {
            table[i] = BuildFlagString(
                [(i & 1) != 0, (i & 2) != 0, (i & 4) != 0, (i & 8) != 0, (i & 16) != 0],
                ["FCRC_ERR", "HCRC_ERR", "FES_ERR", "COD_ERR", "TSS_VIOL"]);
        }
        return table;
    }

    #endregion

    #region Shared string builder

    private static string BuildFlagString(bool[] active, string[] names)
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
