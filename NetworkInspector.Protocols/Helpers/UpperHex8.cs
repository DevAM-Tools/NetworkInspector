// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Formats a 32-bit value as exactly eight uppercase hexadecimal digits (no prefix). Used as an interpolation
/// argument and as a custom-text fragment where the display text has always shown the uppercase form.
/// </summary>
/// <remarks>
/// A dedicated type instead of <c>{value:X8}</c> at every site: the fragment overloads of <c>MutField</c> take the value
/// itself, not a format string, so the format must travel with the value. <see cref="DisplayTables.FormatHexU32"/>
/// is not a substitute: it is lowercase and carries a <c>0x</c> prefix. Thread safety: immutable value type.
/// </remarks>
internal readonly record struct UpperHex8(uint Value) : ISpanFormattable
{
    /// <summary>Formatted length (8 characters).</summary>
    public const int FormattedLength = 8;

    /// <inheritdoc/>
    /// <remarks>The format string and provider are ignored on purpose: the type always prints eight uppercase digits.</remarks>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => Value.TryFormat(destination, out charsWritten, "X8", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>Returns the eight uppercase hexadecimal digits.</summary>
    public override string ToString() => Value.ToString("X8", CultureInfo.InvariantCulture);
}
