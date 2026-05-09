// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Values;

/// <summary>
/// 48-bit MAC (EUI-48) address stored in the lower 6 bytes of a <see cref="ulong"/>.
/// </summary>
/// <remarks>Creates a MAC address from a raw 64-bit value (only lower 48 bits are used).</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct MacAddress(ulong value)
        : IEquatable<MacAddress>, IComparable<MacAddress>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Formatted length: "XX:XX:XX:XX:XX:XX" = 17 characters.</summary>
    public const int FormattedLength = 17;

    private const ulong Mask48 = 0xFFFF_FFFF_FFFF;

    #endregion

    #region Fields

    private readonly ulong _Value = value & Mask48;

    #endregion

    #region Properties

    /// <summary>The raw 64-bit value (lower 48 bits contain the MAC address).</summary>
    public ulong RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value;
    }

    #endregion

    #region Classification

    /// <summary>True if this is the broadcast address (FF:FF:FF:FF:FF:FF).</summary>
    public bool IsBroadcast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value == Mask48;
    }
    /// <summary>True if the multicast bit (bit 0 of octet 0) is set.</summary>
    public bool IsMulticast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_Value & (1UL << 40)) != 0;
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
        get => (_Value & (1UL << 41)) != 0;
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
        get => _Value == 0;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a MAC address from a 6-byte big-endian span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6)
        {
            return default;
        }
        ulong value = ((ulong)bytes[0] << 40) | ((ulong)bytes[1] << 32) |
                      ((ulong)bytes[2] << 24) | ((ulong)bytes[3] << 16) |
                      ((ulong)bytes[4] << 8) | bytes[5];
        return new MacAddress(value);
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
            int h = HexDigitValue(text[offset]);
            int l = HexDigitValue(text[offset + 1]);
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

    #region Binary Serialization

    /// <summary>Writes 6-byte big-endian representation into the destination span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToBytes(Span<byte> destination)
    {
        if (destination.Length < 6)
        {
            return 0;
        }
        destination[0] = (byte)(_Value >> 40);
        destination[1] = (byte)(_Value >> 32);
        destination[2] = (byte)(_Value >> 24);
        destination[3] = (byte)(_Value >> 16);
        destination[4] = (byte)(_Value >> 8);
        destination[5] = (byte)_Value;
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

    #region IBinarySerializable

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSerializedSize(out int size)
    {
        size = 6;
        return true;
    }

    /// <inheritdoc/>
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
        sb.AppendHex2((byte)(_Value >> 40));
        sb.Append(':');
        sb.AppendHex2((byte)(_Value >> 32));
        sb.Append(':');
        sb.AppendHex2((byte)(_Value >> 24));
        sb.Append(':');
        sb.AppendHex2((byte)(_Value >> 16));
        sb.Append(':');
        sb.AppendHex2((byte)(_Value >> 8));
        sb.Append(':');
        sb.AppendHex2((byte)_Value);
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

    /// <summary>Writes the formatted MAC address into the destination span.</summary>
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

    /// <summary>Returns the formatted MAC address as a new string.</summary>
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
    public bool Equals(MacAddress other) => _Value == other._Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MacAddress other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value.GetHashCode();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(MacAddress other) => _Value.CompareTo(other._Value);

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(MacAddress left, MacAddress right) => left._Value == right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(MacAddress left, MacAddress right) => left._Value != right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(MacAddress left, MacAddress right) => left._Value < right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(MacAddress left, MacAddress right) => left._Value > right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(MacAddress left, MacAddress right) => left._Value <= right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(MacAddress left, MacAddress right) => left._Value >= right._Value;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexDigitValue(char c)
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

