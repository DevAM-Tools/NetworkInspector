// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Provides efficient display text formatting for TCP flag combinations.
/// Precomputed 256-entry table eliminates per-packet string building.
/// </summary>
/// <remarks>
/// Every possible flags byte maps to a fixed string built once in the static initializer, so formatting a packet's flags
/// is one array read with no allocation. Thread safety: the table is never modified after type initialization and is safe
/// to read from any thread.
/// </remarks>
internal static class TcpFlagsFormatter
{
    // TCP flag bit positions (in the 8-bit Flags byte)
    private const byte _FinBit = 0x01;
    private const byte _SynBit = 0x02;
    private const byte _RstBit = 0x04;
    private const byte _PshBit = 0x08;
    private const byte _AckBit = 0x10;
    private const byte _UrgBit = 0x20;
    private const byte _EceBit = 0x40;
    private const byte _CwrBit = 0x80;

    private static readonly string[] _FlagsTable = _BuildFlagsTable();

    /// <summary>
    /// Returns a display string for the given TCP flags byte,
    /// listing active flag names separated by commas (for example <c>SYN, ACK</c>), or <c>None</c> when no bit is set.
    /// </summary>
    /// <param name="flags">The TCP flags byte (header byte 13). The NS flag lives in byte 12 and is not part of it.</param>
    /// <returns>The shared, preformatted display string (do not mutate or rely on a new instance per call).</returns>
    internal static string Format(byte flags) => _FlagsTable[flags];

    /// <summary>Builds the full 256-entry table once; entry <c>i</c> is the text for flags byte <c>i</c>.</summary>
    private static string[] _BuildFlagsTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = _BuildFlagString((byte)i);
        }
        return table;
    }

    /// <summary>
    /// Builds the text for one flags byte. Setup-only code (runs 256 times when the table is built), so the small
    /// helper arrays below are acceptable here; the packet path never calls it.
    /// </summary>
    /// <param name="flags">Flags byte.</param>
    /// <returns>Comma separated flag names in the order FIN, SYN, RST, PSH, ACK, URG, ECE, CWR, or <c>None</c>.</returns>
    private static string _BuildFlagString(byte flags)
    {
        if (flags == 0)
        {
            return "None";
        }

        // Build flag names in the order of the bits from LEAST to most significant, so a handshake answer reads
        // "SYN, ACK" and a data segment "PSH, ACK" - the order users know from other packet analyzers, and the order the
        // documented field tree shows. The final string is created directly with string.Create (exact length known up
        // front) instead of string.Join over an intermediate list. names[i] and masks[i] must stay index-aligned.
        ReadOnlySpan<string> names =
        [
            "FIN", "SYN", "RST", "PSH", "ACK", "URG", "ECE", "CWR"
        ];
        byte[] masks = [_FinBit, _SynBit, _RstBit, _PshBit, _AckBit, _UrgBit, _EceBit, _CwrBit];

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
