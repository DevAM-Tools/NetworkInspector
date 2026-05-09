// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Values;

/// <summary>128-bit UUID stored as two <see cref="ulong"/> fields.</summary>
/// <remarks>Creates a UUID from two 64-bit halves.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct Uuid(ulong high, ulong low)
        : IEquatable<Uuid>, IComparable<Uuid>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Formatted length: "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" = 36 characters.</summary>
    public const int FormattedLength = 36;

    #endregion

    #region Fields

    private readonly ulong _High = high;
    private readonly ulong _Low = low;

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
    public bool TryGetSerializedSize(out int size)
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
        WriteHexByte(destination, 0, (int)(_High >> 56));
        WriteHexByte(destination, 2, (int)(_High >> 48));
        WriteHexByte(destination, 4, (int)(_High >> 40));
        WriteHexByte(destination, 6, (int)(_High >> 32));
        destination[8] = '-';
        WriteHexByte(destination, 9, (int)(_High >> 24));
        WriteHexByte(destination, 11, (int)(_High >> 16));
        destination[13] = '-';
        WriteHexByte(destination, 14, (int)(_High >> 8));
        WriteHexByte(destination, 16, (int)_High);
        destination[18] = '-';
        WriteHexByte(destination, 19, (int)(_Low >> 56));
        WriteHexByte(destination, 21, (int)(_Low >> 48));
        destination[23] = '-';
        WriteHexByte(destination, 24, (int)(_Low >> 40));
        WriteHexByte(destination, 26, (int)(_Low >> 32));
        WriteHexByte(destination, 28, (int)(_Low >> 24));
        WriteHexByte(destination, 30, (int)(_Low >> 16));
        WriteHexByte(destination, 32, (int)(_Low >> 8));
        WriteHexByte(destination, 34, (int)_Low);

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

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static buffer.</summary>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(FormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted UUID as a new string.</summary>
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
    public bool Equals(Uuid other) => _High == other._High && _Low == other._Low;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Uuid other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => HashCode.Combine(_High, _Low);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Uuid other)
    {
        int c = _High.CompareTo(other._High);
        return c != 0 ? c : _Low.CompareTo(other._Low);
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(Uuid left, Uuid right) =>
        left._High == right._High && left._Low == right._Low;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(Uuid left, Uuid right) => !(left == right);
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

    private const string HexChars = "0123456789ABCDEF";

    /// <summary>Writes a single byte as two uppercase hex characters at the given offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHexByte(Span<char> dest, int offset, int value)
    {
        dest[offset] = HexChars[(value >> 4) & 0xF];
        dest[offset + 1] = HexChars[value & 0xF];
    }
    #endregion
}

