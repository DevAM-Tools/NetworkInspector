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
    private static readonly string[] FlagsTable = BuildFlagsTable();

    /// <summary>
    /// Returns the precomputed display string for the given IPv4 flag combination.
    /// Output example: <c>[DF]</c>, <c>[DF, MF]</c>, or <c>[None]</c>.
    /// </summary>
    internal static string Format(bool rb, bool df, bool mf)
    {
        int key = (rb ? 1 : 0) | (df ? 2 : 0) | (mf ? 4 : 0);
        return FlagsTable[key];
    }

    private static string[] BuildFlagsTable()
    {
        ReadOnlySpan<string> names = ["RB", "DF", "MF"];
        string[] table = new string[8];
        for (int i = 0; i < 8; i++)
        {
            table[i] = BuildFlagString((byte)i, names);
        }
        return table;
    }

    private static string BuildFlagString(byte flags, ReadOnlySpan<string> names)
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

        // Second pass: fill the string.
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
