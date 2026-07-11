// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>128-bit UUID stored as two <see cref="ulong"/> fields.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Uuid
        : IEquatable<Uuid>, IComparable<Uuid>, IComparable,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable,
      ISpanParsable<Uuid>, IParsable<Uuid>
{
    #region Constants

    /// <summary>Formatted length: "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" = 36 characters.</summary>
    public const int FormattedLength = 36;

    #endregion

    #region Constructor

    /// <summary>Creates a <see cref="Uuid"/> from two 64-bit halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Uuid(ulong high, ulong low)
    {
        _High = high;
        _Low = low;
    }

    #endregion

    #region Fields

    private readonly ulong _High;
    private readonly ulong _Low;

    #endregion

    #region Properties

    /// <summary>The upper 64 bits.</summary>
    public ulong High
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _High;
    }
    /// <summary>The lower 64 bits.</summary>
    public ulong Low
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Low;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a UUID from a 16-byte big-endian span.</summary>
    public static Uuid FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            return default;
        }
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return new Uuid(high, low);
    }

    /// <summary>
    /// Parses a UUID from canonical form "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" (case-insensitive).
    /// </summary>
    /// <param name="text">Input to parse; must be exactly 36 characters.</param>
    /// <param name="result">The parsed UUID when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if parsing succeeded; <see langword="false"/> for any malformed input.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out Uuid result)
    {
        result = default;
        if (text.Length != FormattedLength) { return false; }
        // Validate dash positions: 8, 13, 18, 23
        if (text[8] != '-' || text[13] != '-' || text[18] != '-' || text[23] != '-')
        {
            return false;
        }
        ulong high = 0;
        ulong low = 0;
        // Parse 8 hex chars (bytes 0-3 of high)
        for (int i = 0; i < 8; i++)
        {
            int d = _HexDigitValue(text[i]);
            if (d < 0) { return false; }
            high = (high << 4) | (uint)d;
        }
        // Parse 4 hex chars (bytes 4-5 of high), skip dash at 8
        for (int i = 9; i < 13; i++)
        {
            int d = _HexDigitValue(text[i]);
            if (d < 0) { return false; }
            high = (high << 4) | (uint)d;
        }
        // Parse 4 hex chars (bytes 6-7 of high), skip dash at 13
        for (int i = 14; i < 18; i++)
        {
            int d = _HexDigitValue(text[i]);
            if (d < 0) { return false; }
            high = (high << 4) | (uint)d;
        }
        // Parse 4 hex chars (bytes 0-1 of low), skip dash at 18
        for (int i = 19; i < 23; i++)
        {
            int d = _HexDigitValue(text[i]);
            if (d < 0) { return false; }
            low = (low << 4) | (uint)d;
        }
        // Parse 12 hex chars (bytes 2-7 of low), skip dash at 23
        for (int i = 24; i < 36; i++)
        {
            int d = _HexDigitValue(text[i]);
            if (d < 0) { return false; }
            low = (low << 4) | (uint)d;
        }
        result = new Uuid(high, low);
        return true;
    }

    #endregion

    #region ISpanParsable / IParsable

    /// <inheritdoc/>
    static bool ISpanParsable<Uuid>.TryParse(
        ReadOnlySpan<char> s, IFormatProvider? provider, out Uuid result)
        => TryParse(s, out result);

    /// <inheritdoc/>
    static Uuid ISpanParsable<Uuid>.Parse(
        ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, out Uuid result))
        {
            throw new FormatException($"Input '{s}' is not a valid UUID.");
        }
        return result;
    }

    /// <inheritdoc/>
    static bool IParsable<Uuid>.TryParse(
        string? s, IFormatProvider? provider, out Uuid result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s.AsSpan(), out result);
    }

    /// <inheritdoc/>
    static Uuid IParsable<Uuid>.Parse(
        string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s.AsSpan(), out Uuid result))
        {
            throw new FormatException($"Input '{s}' is not a valid UUID.");
        }
        return result;
    }

    #endregion

    #region Binary Serialization

    /// <summary>Writes 16-byte big-endian representation into the destination span.</summary>
    public int ToBytes(Span<byte> destination)
    {
        if (destination.Length < 16)
        {
            return 0;
        }
        BinaryPrimitives.WriteUInt64BigEndian(destination, _High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _Low);
        return 16;
    }

    #endregion

    #region IBinarySerializable

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWrittenSize(out int size)
    {
        size = 16;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 16)
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
    public bool TryFormat(
        Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < FormattedLength)
        {
            charsWritten = 0;
            return false;
        }

        // Extract hex nibbles directly from ulong fields using shifts.
        // Avoids intermediate byte array and SpanStringBuilder overhead.
        // Format: XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX
        _WriteHexByte(destination, 0, (int)(_High >> 56));
        _WriteHexByte(destination, 2, (int)(_High >> 48));
        _WriteHexByte(destination, 4, (int)(_High >> 40));
        _WriteHexByte(destination, 6, (int)(_High >> 32));
        destination[8] = '-';
        _WriteHexByte(destination, 9, (int)(_High >> 24));
        _WriteHexByte(destination, 11, (int)(_High >> 16));
        destination[13] = '-';
        _WriteHexByte(destination, 14, (int)(_High >> 8));
        _WriteHexByte(destination, 16, (int)_High);
        destination[18] = '-';
        _WriteHexByte(destination, 19, (int)(_Low >> 56));
        _WriteHexByte(destination, 21, (int)(_Low >> 48));
        destination[23] = '-';
        _WriteHexByte(destination, 24, (int)(_Low >> 40));
        _WriteHexByte(destination, 26, (int)(_Low >> 32));
        _WriteHexByte(destination, 28, (int)(_Low >> 24));
        _WriteHexByte(destination, 30, (int)(_Low >> 16));
        _WriteHexByte(destination, 32, (int)(_Low >> 8));
        _WriteHexByte(destination, 34, (int)_Low);

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

    #region IStringSize

    /// <inheritdoc/>
    public bool TryGetStringSize(
        ReadOnlySpan<char> format, IFormatProvider? provider, out int size)
    {
        size = FormattedLength;
        return true;
    }

    #endregion

    #region Convenience Formatting

    /// <summary>Writes the formatted UUID into the destination span.</summary>
    public int FormatInto(Span<char> destination)
    {
        TryFormat(destination, out int written, default, null);
        return written;
    }

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static or pooled buffer.</summary>
    /// <remarks>
    /// The underlying buffer is thread-local or pooled. The caller must dispose the returned <see cref="TempString"/>
    /// before making another <see cref="FormatTemp"/> call on the same thread to avoid overwriting the buffer.
    /// Do not retain references to the underlying span after disposal.
    /// </remarks>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(FormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted UUID as a new string.</summary>
    /// <remarks>Allocates a new string on every call. Use <see cref="FormatInto"/> or <see cref="FormatTemp"/> for allocation-free hot paths.</remarks>
    public string Format()
    {
        Span<char> buf = stackalloc char[FormattedLength];
        TryFormat(buf, out int written, default, null);
        return new string(buf[..written]);
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => Format();

    /// <inheritdoc/>
    public override string ToString() => Format();

    #endregion

    #region Equality & Comparison

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Uuid other)
    {
        int c = _High.CompareTo(other._High);
        if (c != 0)
        {
            return c;
        }

        return _Low.CompareTo(other._Low);
    }

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is Uuid other) { return CompareTo(other); }
        throw new ArgumentException($"Object must be of type {nameof(Uuid)}.", nameof(obj));
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(Uuid left, Uuid right) => left.CompareTo(right) < 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(Uuid left, Uuid right) => left.CompareTo(right) > 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(Uuid left, Uuid right) => left.CompareTo(right) <= 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(Uuid left, Uuid right) => left.CompareTo(right) >= 0;

    #endregion

    #region Private Helpers

    private const string _HexChars = "0123456789ABCDEF";

    /// <summary>Writes a single byte as two uppercase hex characters at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteHexByte(Span<char> dest, int offset, int value)
    {
        dest[offset] = _HexChars[(value >> 4) & 0xF];
        dest[offset + 1] = _HexChars[value & 0xF];
    }

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

