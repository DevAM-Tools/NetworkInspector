// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>64-bit Extended Unique Identifier (EUI-64).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Eui64 :
    IEquatable<Eui64>, IComparable<Eui64>, IComparable,
    ISpanFormattable, IUtf8SpanFormattable,
    ISpanParsable<Eui64>, IParsable<Eui64>
{
    #region Constants

    /// <summary>Formatted length: "XX:XX:XX:XX:XX:XX:XX:XX" = 23 characters.</summary>
    public const int FormattedLength = 23;

    #endregion

    #region Constructor

    /// <summary>Creates an <see cref="Eui64"/> from a raw 64-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Eui64(ulong value) => RawValue = value;

    #endregion

    #region Properties

    /// <summary>The raw 64-bit value.</summary>
    public ulong RawValue { get; }

    #endregion

    #region Factory Methods

    /// <summary>Creates an EUI-64 from an 8-byte big-endian span.</summary>
    /// <returns><see langword="false"/> when fewer than 8 bytes are available.</returns>
    public static bool TryFromBytes(ReadOnlySpan<byte> bytes, out Eui64 value)
    {
        if (bytes.Length < 8)
        {
            value = default;
            return false;
        }

        value = new Eui64(BinaryPrimitives.ReadUInt64BigEndian(bytes));
        return true;
    }

    /// <summary>Creates an EUI-64 from an 8-byte big-endian span.</summary>
    /// <exception cref="ArgumentException">Fewer than 8 bytes are available.</exception>
    public static Eui64 FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (!TryFromBytes(bytes, out Eui64 value))
        {
            throw new ArgumentException("EUI-64 requires at least 8 bytes.", nameof(bytes));
        }

        return value;
    }

    /// <summary>
    /// Parses an EUI-64 from colon-separated hex notation "XX:XX:XX:XX:XX:XX:XX:XX" (case-insensitive).
    /// </summary>
    /// <param name="text">Input to parse.</param>
    /// <param name="result">The parsed EUI-64 when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if parsing succeeded; <see langword="false"/> for any malformed input.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out Eui64 result)
    {
        result = default;
        if (text.Length != FormattedLength) { return false; }
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            int offset = i * 3;
            if (i > 0 && text[offset - 1] != ':') { return false; }
            int h = _HexDigitValue(text[offset]);
            int l = _HexDigitValue(text[offset + 1]);
            if (h < 0 || l < 0) { return false; }
            value = (value << 8) | (uint)((h << 4) | l);
        }
        result = new Eui64(value);
        return true;
    }

    #endregion

    #region ISpanParsable / IParsable

    /// <inheritdoc/>
    static bool ISpanParsable<Eui64>.TryParse(
        ReadOnlySpan<char> s, IFormatProvider? provider, out Eui64 result)
        => TryParse(s, out result);

    /// <inheritdoc/>
    static Eui64 ISpanParsable<Eui64>.Parse(
        ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, out Eui64 result))
        {
            throw new FormatException(Invariant($"Input '{s}' is not a valid EUI-64."));
        }
        return result;
    }

    /// <inheritdoc/>
    static bool IParsable<Eui64>.TryParse(
        string? s, IFormatProvider? provider, out Eui64 result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s.AsSpan(), out result);
    }

    /// <inheritdoc/>
    static Eui64 IParsable<Eui64>.Parse(
        string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s.AsSpan(), out Eui64 result))
        {
            throw new FormatException(Invariant($"Input '{s}' is not a valid EUI-64."));
        }
        return result;
    }

    #endregion

    #region Binary Serialization

    /// <summary>Writes 8-byte big-endian representation into the destination span.</summary>
    public int ToBytes(Span<byte> destination)
    {
        if (destination.Length < 8)
        {
            return 0;
        }
        BinaryPrimitives.WriteUInt64BigEndian(destination, RawValue);
        return 8;
    }

    #endregion

    #region Binary Write

    /// <summary>
    /// Writes the 8-byte network-order form; <see langword="false"/> and 0 when the destination is shorter.
    /// A plain method (no interface) because every caller names the concrete type. The FrameBuilder headers call
    /// <see cref="ToBytes"/> on their exact-size slice instead, which needs no second length check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 8)
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = ToBytes(destination);
        return true;
    }

    #endregion

    #region ISpanFormattable

    /// <inheritdoc/>
    /// <remarks>
    /// Writes eight uppercase hex pairs separated by colons straight into the destination through
    /// <see cref="HexDigits.WritePair"/>; no builder, no intermediate buffer. The pair for byte i starts at
    /// <c>i * 3</c> and is followed by a colon for every byte but the last.
    /// </remarks>
    public bool TryFormat(
        Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        // The specifier is read before anything is written, so an invalid one fails even when the buffer is too small.
        ReadOnlySpan<char> hex = HexDigits.Table(HexDigits.ParseCase(format));

        if (destination.Length < FormattedLength)
        {
            charsWritten = 0;
            return false;
        }

        // The (int) cast keeps the low 32 bits of the shifted value on purpose: WritePair reads only the low byte, so
        // the dropped high bits cannot change the text (and no overflow check applies to a deliberate truncation).
        Span<char> d = destination[..FormattedLength];
        for (int i = 0; i < 8; i++)
        {
            HexDigits.WritePair(d, i * 3, (int)(RawValue >> (56 - (i * 8))), hex);
            if (i < 7)
            {
                d[(i * 3) + 2] = ':';
            }
        }

        charsWritten = FormattedLength;
        return true;
    }

    #endregion

    #region IUtf8SpanFormattable

    /// <inheritdoc/>
    public bool TryFormat(
        Span<byte> utf8Destination, out int bytesWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<char> chars = stackalloc char[FormattedLength];
        if (!TryFormat(chars, out int written, format, provider))
        {
            bytesWritten = 0;
            return false;
        }
        if (utf8Destination.Length < written)
        {
            bytesWritten = 0;
            return false;
        }
        // All output is ASCII — safe to narrow directly
        for (int i = 0; i < written; i++)
        {
            utf8Destination[i] = (byte)chars[i];
        }
        bytesWritten = written;
        return true;
    }

    #endregion

    #region String Size

    /// <summary>
    /// Character count of the default text (always 23, exact); lets a caller size a buffer once. Kept without an
    /// interface because every caller names the concrete type.
    /// </summary>
    public static bool TryGetStringSize(
        ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        size = FormattedLength;
        return true;
    }

    #endregion

    #region Convenience Formatting

    /// <summary>Writes the formatted EUI-64 into the destination span.</summary>
    /// <param name="destination">Target buffer; it must be large enough for the whole text.</param>
    /// <param name="hexCase">Letter case of the hex digits; <see cref="HexCase.Upper"/> by default.</param>
    public int FormatInto(Span<char> destination, HexCase hexCase = HexCase.Upper)
    {
        TryFormat(destination, out int written, _Specifier(hexCase), null);
        return written;
    }

    /// <summary>Returns the formatted EUI-64 as a new string.</summary>
    /// <remarks>
    /// Allocates exactly the result string (the text is built on the stack first). Use <see cref="FormatInto"/> or
    /// <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/> when the caller owns a buffer.
    /// <param name="hexCase">
    /// Letter case of the hex digits; <see cref="HexCase.Upper"/> by default. The format specifier <c>x</c> of <c>ToString</c> and of
    /// interpolation selects <see cref="HexCase.Lower"/>.
    /// </param>
    /// </remarks>
    public string Format(HexCase hexCase = HexCase.Upper)
    {
        Span<char> buf = stackalloc char[FormattedLength];
        TryFormat(buf, out int written, _Specifier(hexCase), null);
        return new string(buf[..written]);
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => Format(HexDigits.ParseCase(format));

    /// <inheritdoc/>
    public override string ToString() => Format();

    private static string _Specifier(HexCase hexCase) =>
        hexCase == HexCase.Lower
            ? "x"
            : "X";

    #endregion

    #region Equality & Comparison

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Eui64 other) => RawValue.CompareTo(other.RawValue);

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is Eui64 other) { return CompareTo(other); }
        throw new ArgumentException(Invariant($"Object must be of type {nameof(Eui64)}."), nameof(obj));
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(Eui64 left, Eui64 right) => left.RawValue < right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(Eui64 left, Eui64 right) => left.RawValue > right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(Eui64 left, Eui64 right) => left.RawValue <= right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(Eui64 left, Eui64 right) => left.RawValue >= right.RawValue;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _HexDigitValue(char c)
    {
        if (c >= '0' && c <= '9') { return c - '0'; }
        if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
        if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
        return -1;
    }

    #endregion
}

