// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Dns;

/// <summary>
/// Provides efficient display text formatting for DNS header flag combinations.
/// <para>
/// The DNS flags word (RFC 1035 §4.1.1) is 16 bits wide and contains a mix of
/// boolean flags, a 4-bit opcode, and a 4-bit RCODE.  Only the eight boolean bits
/// are encoded in the 256-entry lookup table; opcode and RCODE are rendered
/// separately by the protocol parser.
/// </para>
/// <para>
/// Synthetic 8-bit key encoding (bit 7 = highest):
/// <list type="table">
///   <listheader><term>Key bit</term><term>Wire bit</term><term>Name</term></listheader>
///   <item><term>7</term><term>15</term><term>Response (QR)</term></item>
///   <item><term>6</term><term>10</term><term>AA</term></item>
///   <item><term>5</term><term>9</term><term>TC</term></item>
///   <item><term>4</term><term>8</term><term>RD</term></item>
///   <item><term>3</term><term>7</term><term>RA</term></item>
///   <item><term>2</term><term>6</term><term>Z</term></item>
///   <item><term>1</term><term>5</term><term>AD</term></item>
///   <item><term>0</term><term>4</term><term>CD</term></item>
/// </list>
/// </para>
/// <para>
/// <see cref="Format"/> returns the bracket portion only, e.g. <c>[Response, RD, RA]</c>
/// or <c>[None]</c>.  The caller prepends the hex value:
/// <c>ZA.Lazy(DisplayTables.FormatHexU16(flags), " ", DnsFlagsFormatter.Format(flags))</c>.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from a pre-built immutable array;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class DnsFlagsFormatter
{
    // 256-entry table indexed by the synthetic 8-bit key; stores bracket strings only.
    // Bits 14–11 (Opcode) and 3–0 (RCODE) are excluded; they are multi-bit
    // numeric values rendered separately by the protocol parser.
    private static readonly string[] _FlagsTable = _BuildFlagsTable();

    /// <summary>
    /// Returns the bracket portion of the display string for the given DNS flags word.
    /// Output examples: <c>[Response, RD, RA]</c>, <c>[None]</c>.
    /// The caller is responsible for prepending the hex value.
    /// </summary>
    internal static string Format(ushort flags) => _FlagsTable[_BuildKey(flags)];

    /// <summary>
    /// Extracts the eight boolean flag bits from a 16-bit DNS flags word
    /// and packs them into a compact 8-bit lookup key.
    /// Bits 14–11 (Opcode) and 3–0 (RCODE) are excluded; they are multi-bit
    /// numeric values rendered separately by the protocol parser.
    /// </summary>
    private static byte _BuildKey(ushort flags) =>
        (byte)(
            ((flags >> 15) & 1) << 7 |  // QR  → key bit 7
            ((flags >> 10) & 1) << 6 |  // AA  → key bit 6
            ((flags >> 9) & 1) << 5 |   // TC  → key bit 5
            ((flags >> 8) & 1) << 4 |   // RD  → key bit 4
            ((flags >> 7) & 1) << 3 |   // RA  → key bit 3
            ((flags >> 6) & 1) << 2 |   // Z   → key bit 2
            ((flags >> 5) & 1) << 1 |   // AD  → key bit 1
            ((flags >> 4) & 1) << 0);   // CD  → key bit 0

    private static string[] _BuildFlagsTable()
    {
        string[] table = new string[256];
        // Names are ordered by ascending key bit position (bit 0 first).
        // The array is allocated once here and shared across all 256 _BuildBracketString calls
        // to avoid 256 identical ToArray() allocations inside the loop.
        string[] names = ["CD", "AD", "Z", "RA", "RD", "TC", "AA", "Response"];
        for (int i = 0; i < 256; i++)
        {
            table[i] = _BuildBracketString((byte)i, names);
        }
        return table;
    }

    private static string _BuildBracketString(byte key, string[] names)
    {
        if (key == 0)
        {
            return "[None]";
        }

        // First pass: compute total length. Iterate MSB first (7→0) for natural display order.
        int totalLen = 2; // "[" and "]"
        bool first = true;
        for (int i = 7; i >= 0; i--)
        {
            if ((key & (1 << i)) != 0)
            {
                if (!first)
                {
                    totalLen += 2; // ", "
                }
                totalLen += names[i].Length;
                first = false;
            }
        }

        // Second pass: fill the string via string.Create for zero-allocation construction.
        // Names are emitted MSB first (7→0) so Response, AA, TC, RD, RA, Z, AD, CD is the natural order.
        return string.Create(totalLen, (key, names), static (chars, state) =>
        {
            chars[0] = '[';
            int written = 1;
            bool firstChar = true;
            for (int i = 7; i >= 0; i--)
            {
                if ((state.key & (1 << i)) != 0)
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
}
