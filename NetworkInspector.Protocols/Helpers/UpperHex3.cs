// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Formats the low 12 bits of a value as exactly three uppercase hexadecimal digits (no prefix); used for the 11-bit
/// standard CAN identifier text.
/// </summary>
/// <remarks>
/// Same reason as <see cref="UpperHex8"/>: the fragment overloads of <c>MutField</c> take the value itself, so the format
/// must travel with the value. Bits above the twelfth are dropped, so the text is always three characters long.
/// Thread safety: immutable value type.
/// </remarks>
internal readonly record struct UpperHex3(ushort Value) : ISpanFormattable
{
    /// <summary>Formatted length (3 characters).</summary>
    public const int FormattedLength = 3;

    /// <inheritdoc/>
    /// <remarks>The format string and provider are ignored on purpose: the type always prints three uppercase digits.</remarks>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => ((ushort)(Value & 0xFFF)).TryFormat(destination, out charsWritten, "X3", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Returns the three uppercase hexadecimal digits.</summary>
    public override string ToString() => ((ushort)(Value & 0xFFF)).ToString("X3", CultureInfo.InvariantCulture);
}
