// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>64-bit Extended Unique Identifier (EUI-64).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Eui64
        : IEquatable<Eui64>, IComparable<Eui64>, IComparable,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable,
      ISpanParsable<Eui64>, IParsable<Eui64>
{
    #region Constants

    /// <summary>Formatted length: "XX:XX:XX:XX:XX:XX:XX:XX" = 23 characters.</summary>
    public const int FormattedLength = 23;

    #endregion

    #region Constructor

    /// <summary>Creates an <see cref="Eui64"/> from a raw 64-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Eui64(ulong value)
    {
        _Value = value;
    }

    #endregion

    #region Fields

    private readonly ulong _Value;

    #endregion

    #region Properties

    /// <summary>The raw 64-bit value.</summary>
    public ulong RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates an EUI-64 from an 8-byte big-endian span.</summary>
    public static Eui64 FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return default;
        }
        return new Eui64(BinaryPrimitives.ReadUInt64BigEndian(bytes));
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
            int h = HexDigitValue(text[offset]);
            int l = HexDigitValue(text[offset + 1]);
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
            throw new FormatException($"Input '{s}' is not a valid EUI-64.");
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
            throw new FormatException($"Input '{s}' is not a valid EUI-64.");
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
        BinaryPrimitives.WriteUInt64BigEndian(destination, _Value);
        return 8;
    }

    #endregion

    #region IBinarySerializable

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSerializedSize(out int size)
    {
        size = 8;
        return true;
    }

    /// <inheritdoc/>
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
    /// <remarks>Uses <see cref="ZeroAlloc.SpanStringBuilder"/> for zero-allocation span-based formatting.</remarks>
    public bool TryFormat(
        Span<char> destination, out int charsWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < FormattedLength)
        {
            charsWritten = 0;
            return false;
        }
        SpanStringBuilder sb = new(destination);
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
            {
                sb.Append(':');
            }
            sb.AppendHex2((byte)(_Value >> (56 - i * 8)));
        }
        charsWritten = sb.Length;
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

    /// <summary>Writes the formatted EUI-64 into the destination span.</summary>
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

    /// <summary>Returns the formatted EUI-64 as a new string.</summary>
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
    public int CompareTo(Eui64 other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is Eui64 other) { return CompareTo(other); }
        throw new ArgumentException($"Object must be of type {nameof(Eui64)}.", nameof(obj));
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(Eui64 left, Eui64 right) => left._Value < right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(Eui64 left, Eui64 right) => left._Value > right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(Eui64 left, Eui64 right) => left._Value <= right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(Eui64 left, Eui64 right) => left._Value >= right._Value;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexDigitValue(char c)
    {
        if (c >= '0' && c <= '9') { return c - '0'; }
        if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
        if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
        return -1;
    }

    #endregion
}

