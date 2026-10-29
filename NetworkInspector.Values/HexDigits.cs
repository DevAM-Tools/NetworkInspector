// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>
/// Hexadecimal digit writer and format-specifier reader shared by <see cref="MacAddress"/>, <see cref="Eui64"/>,
/// <see cref="Uuid"/>, and <see cref="IPv6Address"/>. One table lookup per digit, no allocation. Thread safety: stateless.
/// </summary>
internal static class HexDigits
{
    // The digit tables as spans over the assembly's read-only data: the lookup replaces a branch per digit. Upper case is
    // the default and the text the value types have always produced (the oracle exports lock it in); lower case is the
    // opt-in. The table is chosen once per formatted value, not once per digit, so the choice costs one comparison.
    private static ReadOnlySpan<char> _Upper => "0123456789ABCDEF";

    private static ReadOnlySpan<char> _Lower => "0123456789abcdef";

    /// <summary>Returns the digit table of <paramref name="hexCase"/>.</summary>
    internal static ReadOnlySpan<char> Table(HexCase hexCase) =>
        hexCase == HexCase.Lower
            ? _Lower
            : _Upper;

    /// <summary>
    /// Reads the letter case from a format specifier: empty and <c>X</c> mean <see cref="HexCase.Upper"/>, <c>x</c> means
    /// <see cref="HexCase.Lower"/>.
    /// </summary>
    /// <param name="format">The format specifier of <c>TryFormat</c> or <c>ToString</c>; empty selects the default.</param>
    /// <exception cref="FormatException">The specifier is neither empty, <c>X</c> nor <c>x</c>. Failing loudly beats
    /// silently ignoring a specifier the caller believes has an effect (for example <c>"D"</c> or <c>"N"</c> of a GUID).</exception>
    internal static HexCase ParseCase(ReadOnlySpan<char> format)
    {
        if (format.IsEmpty || format is "X")
        {
            return HexCase.Upper;
        }

        if (format is "x")
        {
            return HexCase.Lower;
        }

        throw new FormatException("The format specifier must be empty, \"X\" (upper case) or \"x\" (lower case).");
    }

    /// <summary>Writes the two hex digits of the low byte of <paramref name="value"/> at <paramref name="offset"/>.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="offset">Index of the first digit.</param>
    /// <param name="value">Value whose low byte is written; the higher bits are ignored on purpose.</param>
    /// <param name="table">Digit table from <see cref="Table"/>.</param>
    /// <remarks>The caller guarantees <c>offset + 2 &lt;= destination.Length</c>; the indexers throw otherwise.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WritePair(Span<char> destination, int offset, int value, ReadOnlySpan<char> table)
    {
        destination[offset] = table[(value >> 4) & 0x0F];
        destination[offset + 1] = table[value & 0x0F];
    }
}
