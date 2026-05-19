// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Provides efficient display text formatting for TCP flag combinations.
/// Precomputed 256-entry table eliminates per-packet string building.
/// </summary>
internal static class TcpFlagsFormatter
{
    // TCP flag bit positions (in the 8-bit Flags byte)
    private const byte FinBit = 0x01;
    private const byte SynBit = 0x02;
    private const byte RstBit = 0x04;
    private const byte PshBit = 0x08;
    private const byte AckBit = 0x10;
    private const byte UrgBit = 0x20;
    private const byte EceBit = 0x40;
    private const byte CwrBit = 0x80;

    private static readonly string[] FlagsTable = BuildFlagsTable();

    /// <summary>
    /// Returns a display string for the given TCP flags byte,
    /// listing active flag names separated by commas.
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
        if (flags == 0)
        {
            return "None";
        }

        // Build flag names in standard order using a stackalloc span,
        // then create the result string directly without intermediate ToArray()/string.Join.
        ReadOnlySpan<string> names =
        [
            "CWR", "ECE", "URG", "ACK", "PSH", "RST", "SYN", "FIN"
        ];
        byte[] masks = [CwrBit, EceBit, UrgBit, AckBit, PshBit, RstBit, SynBit, FinBit];

        // First pass: compute total length
        int totalLen = 0;
        int count = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((flags & masks[i]) != 0)
            {
                if (count > 0)
                {
                    totalLen += 2; // ", " separator
                }
                totalLen += names[i].Length;
                count++;
            }
        }

        // Second pass: fill the string
        return string.Create(totalLen, (flags, names.ToArray(), masks), static (chars, state) =>
        {
            int written = 0;
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                if ((state.flags & state.masks[i]) != 0)
                {
                    if (!first)
                    {
                        chars[written++] = ',';
                        chars[written++] = ' ';
                    }
                    state.Item2[i].AsSpan().CopyTo(chars[written..]);
                    written += state.Item2[i].Length;
                    first = false;
                }
            }
        });
    }
}
