// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>
/// 48-bit MAC (EUI-48) address stored in the lower 6 bytes of a <see cref="ulong"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MacAddress :
    IEquatable<MacAddress>, IComparable<MacAddress>, IComparable,
    ISpanFormattable, IUtf8SpanFormattable,
    ISpanParsable<MacAddress>, IParsable<MacAddress>
{
    #region Constants

    /// <summary>Formatted length: "XX:XX:XX:XX:XX:XX" = 17 characters.</summary>
    public const int FormattedLength = 17;

    private const ulong _Mask48 = 0xFFFF_FFFF_FFFF;

    #endregion

    #region Constructor

    /// <summary>Creates a <see cref="MacAddress"/> from a raw 64-bit value (only lower 48 bits are used).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MacAddress(ulong value) => RawValue = value & _Mask48;

    #endregion

    #region Properties

    /// <summary>The raw 64-bit value (lower 48 bits contain the MAC address).</summary>
    public ulong RawValue { get; }

    #endregion

    #region Classification

    /// <summary>True if this is the broadcast address (FF:FF:FF:FF:FF:FF).</summary>
    public bool IsBroadcast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => RawValue == _Mask48;
    }
    /// <summary>True if the multicast bit (bit 0 of octet 0) is set.</summary>
    public bool IsMulticast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (RawValue & (1UL << 40)) != 0;
    }
    /// <summary>True if the address is unicast (multicast bit not set).</summary>
    public bool IsUnicast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsMulticast;
    }
    /// <summary>True if the locally administered bit (bit 1 of octet 0) is set.</summary>
    public bool IsLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (RawValue & (1UL << 41)) != 0;
    }
    /// <summary>True if the address is globally unique.</summary>
    public bool IsGlobal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsLocal;
    }
    /// <summary>True if all 48 bits are zero.</summary>
    public bool IsZero
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => RawValue == 0;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a MAC address from a 6-byte big-endian span.</summary>
    /// <returns><see langword="false"/> when fewer than 6 bytes are available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFromBytes(ReadOnlySpan<byte> bytes, out MacAddress address)
    {
        if (bytes.Length < 6)
        {
            address = default;
            return false;
        }

        // BinaryPrimitives has no ReadUInt48 — combine a 2-byte and 4-byte read for portability.
        ulong high = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        ulong low = BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]);
        address = new MacAddress((high << 32) | low);
        return true;
    }

    /// <summary>Creates a MAC address from a 6-byte big-endian span.</summary>
    /// <exception cref="ArgumentException">Fewer than 6 bytes are available.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (!TryFromBytes(bytes, out MacAddress address))
        {
            throw new ArgumentException("MAC address requires at least 6 bytes.", nameof(bytes));
        }

        return address;
    }

    /// <summary>Parses a MAC address from "XX:XX:XX:XX:XX:XX" notation (case-insensitive).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out MacAddress result)
    {
        result = default;
        if (text.Length != FormattedLength)
        {
            return false;
        }
        ulong value = 0;
        for (int i = 0; i < 6; i++)
        {
            int offset = i * 3;
            if (i > 0 && text[offset - 1] != ':')
            {
                return false;
            }
            int h = _HexDigitValue(text[offset]);
            int l = _HexDigitValue(text[offset + 1]);
            if (h < 0 || l < 0)
            {
                return false;
            }
            value = (value << 8) | (uint)((h << 4) | l);
        }
        result = new MacAddress(value);
        return true;
    }

    #endregion

    #region ISpanParsable / IParsable

    /// <inheritdoc/>
    static bool ISpanParsable<MacAddress>.TryParse(
        ReadOnlySpan<char> s, IFormatProvider? provider, out MacAddress result)
        => TryParse(s, out result);

    /// <inheritdoc/>
    static MacAddress ISpanParsable<MacAddress>.Parse(
        ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, out MacAddress result))
        {
            throw new FormatException($"Input '{s}' is not a valid MAC address.");
        }
        return result;
    }

    /// <inheritdoc/>
    static bool IParsable<MacAddress>.TryParse(
        string? s, IFormatProvider? provider, out MacAddress result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s.AsSpan(), out result);
    }

    /// <inheritdoc/>
    static MacAddress IParsable<MacAddress>.Parse(
        string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s.AsSpan(), out MacAddress result))
        {
            throw new FormatException($"Input '{s}' is not a valid MAC address.");
        }
        return result;
    }

    #endregion

    #region Binary Serialization

    /// <summary>Writes 6-byte big-endian representation into the destination span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToBytes(Span<byte> destination)
    {
        if (destination.Length < 6)
        {
            return 0;
        }
        destination[0] = (byte)(RawValue >> 40);
        destination[1] = (byte)(RawValue >> 32);
        destination[2] = (byte)(RawValue >> 24);
        destination[3] = (byte)(RawValue >> 16);
        destination[4] = (byte)(RawValue >> 8);
        destination[5] = (byte)RawValue;
        return 6;
    }

    /// <summary>Returns a new 6-byte array with the big-endian representation.</summary>
    public byte[] ToBytesArray()
    {
        byte[] result = new byte[6];
        ToBytes(result);
        return result;
    }

    #endregion

    #region Binary Write

    /// <summary>
    /// Writes the 6-byte network-order form; <see langword="false"/> and 0 when the destination is shorter.
    /// A plain method (no interface) because every caller names the concrete type. The FrameBuilder headers call
    /// <see cref="ToBytes"/> on their exact-size slice instead, which needs no second length check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 6)
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
    /// Writes six uppercase hex pairs separated by colons straight into the destination through
    /// <see cref="HexDigits.WritePair"/>; no builder, no intermediate buffer. The one length check up front plus
    /// the exact-size slice keeps the seventeen stores free of per-store bounds checks after JIT.
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

        // The (int) casts keep the low 32 bits of the shifted value on purpose: WritePair reads only the low byte, so
        // the dropped high bits cannot change the text (and no overflow check applies to a deliberate truncation).
        Span<char> d = destination[..FormattedLength];
        HexDigits.WritePair(d, 0, (int)(RawValue >> 40), hex);
        d[2] = ':';
        HexDigits.WritePair(d, 3, (int)(RawValue >> 32), hex);
        d[5] = ':';
        HexDigits.WritePair(d, 6, (int)(RawValue >> 24), hex);
        d[8] = ':';
        HexDigits.WritePair(d, 9, (int)(RawValue >> 16), hex);
        d[11] = ':';
        HexDigits.WritePair(d, 12, (int)(RawValue >> 8), hex);
        d[14] = ':';
        HexDigits.WritePair(d, 15, (int)RawValue, hex);
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
    /// Character count of the default text (always 17, exact); lets a caller size a buffer once. Kept without an
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

    /// <summary>Writes the formatted MAC address into the destination span.</summary>
    /// <param name="destination">Target buffer; it must be large enough for the whole text.</param>
    /// <param name="hexCase">Letter case of the hex digits; <see cref="HexCase.Upper"/> by default.</param>
    public int FormatInto(Span<char> destination, HexCase hexCase = HexCase.Upper)
    {
        TryFormat(destination, out int written, _Specifier(hexCase), null);
        return written;
    }

    /// <summary>Returns the formatted MAC address as a new string.</summary>
    /// <remarks>
    /// Allocates exactly the result string (the text is built on the stack first). Use <see cref="FormatInto"/> or
    /// <see cref="TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/> when the caller owns a buffer.
    /// <param name="hexCase">Letter case of the hex digits; <see cref="HexCase.Upper"/> by default. The format specifier <c>x</c> of <c>ToString</c> and of interpolation selects <see cref="HexCase.Lower"/>.</param>
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
    public int CompareTo(MacAddress other) => RawValue.CompareTo(other.RawValue);

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is MacAddress other) { return CompareTo(other); }
        throw new ArgumentException($"Object must be of type {nameof(MacAddress)}.", nameof(obj));
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(MacAddress left, MacAddress right) => left.RawValue < right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(MacAddress left, MacAddress right) => left.RawValue > right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(MacAddress left, MacAddress right) => left.RawValue <= right.RawValue;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(MacAddress left, MacAddress right) => left.RawValue >= right.RawValue;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _HexDigitValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }
    #endregion
}

