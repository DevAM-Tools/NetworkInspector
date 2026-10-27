// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>
/// Uppercase hexadecimal digit writer shared by <see cref="MacAddress"/>, <see cref="Eui64"/>, and <see cref="Uuid"/>.
/// One table lookup per digit, no allocation. Thread safety: stateless.
/// </summary>
internal static class HexDigits
{
    // "0123456789ABCDEF" as a span over the assembly's read-only data: the lookup replaces a branch per digit and keeps
    // the output uppercase, which is the text the value types have always produced (and which the oracle exports lock in).
    private static ReadOnlySpan<char> _Upper => "0123456789ABCDEF";

    /// <summary>Writes the two hex digits of the low byte of <paramref name="value"/> at <paramref name="offset"/>.</summary>
    /// <remarks>The caller guarantees <c>offset + 2 &lt;= destination.Length</c>; the indexers throw otherwise.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteUpperPair(Span<char> destination, int offset, int value)
    {
        destination[offset] = _Upper[(value >> 4) & 0x0F];
        destination[offset + 1] = _Upper[value & 0x0F];
    }
}
