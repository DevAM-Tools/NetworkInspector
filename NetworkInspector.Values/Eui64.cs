// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>64-bit Extended Unique Identifier (EUI-64).</summary>
/// <remarks>Creates an EUI-64 from a raw 64-bit value.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct Eui64(ulong value)
        : IEquatable<Eui64>, IComparable<Eui64>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Formatted length: "XX:XX:XX:XX:XX:XX:XX:XX" = 23 characters.</summary>
    public const int FormattedLength = 23;

    #endregion

    #region Fields

    private readonly ulong _Value = value;

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

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static buffer.</summary>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(FormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted EUI-64 as a new string.</summary>
    public string Format()
    {
        Span<char> buf = stackalloc char[FormattedLength];
        TryFormat(buf, out _, default, null);
        return new string(buf);
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => Format();

    /// <inheritdoc/>
    public override string ToString() => Format();

    #endregion

    #region Equality & Comparison

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Eui64 other) => _Value == other._Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Eui64 other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value.GetHashCode();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Eui64 other) => _Value.CompareTo(other._Value);

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(Eui64 left, Eui64 right) => left._Value == right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(Eui64 left, Eui64 right) => left._Value != right._Value;
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
}

