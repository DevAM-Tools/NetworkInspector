// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.IPv4;

/// <summary>
/// Provides efficient display text formatting for the IPv4 flags field.
/// The 8-entry precomputed table (3 flag bits: RB, DF, MF) eliminates
/// per-packet string building for the <c>ip.flags</c> container field.
/// <para>Output format: <c>[DF]</c> for set flags, <c>[None]</c> when no flags are set.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from a pre-built immutable array;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class IPv4FlagsFormatter
{
    // Key packing: bit0=RB (Reserved), bit1=DF (Don't Fragment), bit2=MF (More Fragments).
    // This is the table's own key, not the wire value: on the wire the reserved bit is the highest of the 3 flag
    // bits (0x4) and MF the lowest (0x1). Listing the names in ascending key order gives the wire order "RB, DF, MF".
    private static readonly string[] _FlagsTable = _BuildFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given IPv4 flag combination.
    /// Output example: <c>[DF]</c>, <c>[DF, MF]</c>, or <c>[None]</c>.
    /// </summary>
    /// <param name="rb">The reserved flag bit (must be zero in a valid packet).</param>
    /// <param name="df">The Don't Fragment bit.</param>
    /// <param name="mf">The More Fragments bit.</param>
    /// <returns>A preformatted string from the table; no allocation per call.</returns>
    internal static string Format(bool rb, bool df, bool mf)
    {
        int key = (rb ? 1 : 0) | (df ? 2 : 0) | (mf ? 4 : 0);
        return _FlagsTable[key];
    }

    private static string[] _BuildFlagsTable()
    {
        // Runs once in the type initializer: the temporary arrays below cost nothing at parse time, because Format
        // only reads the finished table.
        ReadOnlySpan<string> names = ["RB", "DF", "MF"];
        string[] table = new string[8];
        for (int i = 0; i < 8; i++)
        {
            table[i] = _BuildFlagString((byte)i, names);
        }
        return table;
    }

    private static string _BuildFlagString(byte flags, ReadOnlySpan<string> names)
    {
        if (flags == 0)
        {
            return "[None]";
        }

        // First pass: compute total length. Each name corresponds to the bit at its index position
        // (index 0 = bit 0 = RB, index 1 = bit 1 = DF, index 2 = bit 2 = MF).
        int totalLen = 2; // "[" and "]"
        int count = 0;
        for (int i = 0; i < names.Length; i++)
        {
            if ((flags >> i & 1) != 0)
            {
                if (count > 0)
                {
                    totalLen += 2; // ", "
                }
                totalLen += names[i].Length;
                count++;
            }
        }

        // Second pass: fill the string. A span cannot be captured in the state tuple, hence names.ToArray() (build-time
        // only). The lambda is static and writes exactly totalLen chars, so it mirrors the first pass.
        return string.Create(totalLen, (flags, names: names.ToArray()), static (chars, state) =>
        {
            chars[0] = '[';
            int written = 1;
            bool first = true;
            for (int i = 0; i < state.names.Length; i++)
            {
                if ((state.flags >> i & 1) != 0)
                {
                    if (!first)
                    {
                        chars[written++] = ',';
                        chars[written++] = ' ';
                    }
                    state.names[i].AsSpan().CopyTo(chars[written..]);
                    written += state.names[i].Length;
                    first = false;
                }
            }
            chars[written] = ']';
        });
    }
}
