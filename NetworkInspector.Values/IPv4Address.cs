// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Values;

/// <summary>32-bit IPv4 address stored in network byte order.</summary>
/// <remarks>Creates an IPv4 address from a raw 32-bit value in network byte order.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct IPv4Address(uint value)
        : IEquatable<IPv4Address>, IComparable<IPv4Address>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Maximum formatted length: "255.255.255.255" = 15 characters.</summary>
    public const int MaxFormattedLength = 15;

    #endregion

    #region Fields

    private readonly uint _Value = value;

    #endregion

    #region Properties

    /// <summary>The raw 32-bit value in network byte order.</summary>
    public uint RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value;
    }

    #endregion

    #region Classification

    /// <summary>True if the address is in a private range (10/8, 172.16/12, 192.168/16).</summary>
    public bool IsPrivate
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get =>
        ((_Value & 0xFF000000) == 0x0A000000) || // 10.0.0.0/8
        ((_Value & 0xFFF00000) == 0xAC100000) || // 172.16.0.0/12
        ((_Value & 0xFFFF0000) == 0xC0A80000);   // 192.168.0.0/16
    }
    /// <summary>True if the address is in the loopback range (127.0.0.0/8).</summary>
    public bool IsLoopback
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_Value & 0xFF000000) == 0x7F000000;
    }
    /// <summary>True if the address is in the multicast range (224.0.0.0/4).</summary>
    public bool IsMulticast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_Value & 0xF0000000) == 0xE0000000;
    }
    /// <summary>True if the address is in the link-local range (169.254.0.0/16).</summary>
    public bool IsLinkLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_Value & 0xFFFF0000) == 0xA9FE0000;
    }
    /// <summary>True if the address is the broadcast address (255.255.255.255).</summary>
    public bool IsBroadcast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value == 0xFFFFFFFF;
    }
    /// <summary>True if the address is all zeros.</summary>
    public bool IsZero
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value == 0;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates an IPv4 address from a 4-byte big-endian span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IPv4Address FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return default;
        }
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                     ((uint)bytes[2] << 8) | bytes[3];
        return new IPv4Address(value);
    }

    /// <summary>Parses an IPv4 address from dotted-decimal notation.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IPv4Address result)
    {
        result = default;
        uint value = 0;
        int octet = 0;
        int octetValue = 0;
        bool hasDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= '0' && c <= '9')
            {
                octetValue = octetValue * 10 + (c - '0');
                if (octetValue > 255)
                {
                    return false;
                }
                hasDigit = true;
            }
            else if (c == '.')
            {
                if (!hasDigit || octet >= 3)
                {
                    return false;
                }
                value = (value << 8) | (uint)octetValue;
                octetValue = 0;
                hasDigit = false;
                octet++;
            }
            else
            {
                return false;
            }
        }

        if (!hasDigit || octet != 3)
        {
            return false;
        }
        value = (value << 8) | (uint)octetValue;
        result = new IPv4Address(value);
        return true;
    }

    #endregion

    #region Binary Serialization

    /// <summary>Writes 4-byte big-endian representation into the destination span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToBytes(Span<byte> destination)
    {
        if (destination.Length < 4)
        {
            return 0;
        }
        destination[0] = (byte)(_Value >> 24);
        destination[1] = (byte)(_Value >> 16);
        destination[2] = (byte)(_Value >> 8);
        destination[3] = (byte)_Value;
        return 4;
    }

    /// <summary>Returns a new 4-byte array with the big-endian representation.</summary>
    public byte[] ToBytesArray()
    {
        byte[] r = new byte[4];
        ToBytes(r);
        return r;
    }

    #endregion

    #region IBinarySerializable

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSerializedSize(out int size)
    {
        size = 4;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 4)
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
        if (destination.Length < MaxFormattedLength)
        {
            charsWritten = 0;
            return false;
        }
        int pos = 0;
        pos += WriteOctet((byte)(_Value >> 24), destination[pos..]);
        destination[pos++] = '.';
        pos += WriteOctet((byte)(_Value >> 16), destination[pos..]);
        destination[pos++] = '.';
        pos += WriteOctet((byte)(_Value >> 8), destination[pos..]);
        destination[pos++] = '.';
        pos += WriteOctet((byte)_Value, destination[pos..]);
        charsWritten = pos;
        return true;
    }

    #endregion

    #region IUtf8SpanFormattable

    /// <inheritdoc/>
    public bool TryFormat(
        Span<byte> utf8Destination, out int bytesWritten,
        ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        Span<char> chars = stackalloc char[MaxFormattedLength];
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
        // Exact calculation: digit count of each octet + 3 dots
        size = OctetLength((byte)(_Value >> 24)) + 1 +
               OctetLength((byte)(_Value >> 16)) + 1 +
               OctetLength((byte)(_Value >> 8)) + 1 +
               OctetLength((byte)_Value);
        return true;
    }

    #endregion

    #region Convenience Formatting

    /// <summary>Writes the dotted-decimal address into the destination span.</summary>
    public int FormatInto(Span<char> destination)
    {
        TryFormat(destination, out int written, default, null);
        return written;
    }

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static buffer.</summary>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(MaxFormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted IPv4 address as a new string.</summary>
    public string Format()
    {
        Span<char> buf = stackalloc char[MaxFormattedLength];
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
    public bool Equals(IPv4Address other) => _Value == other._Value;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IPv4Address other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => (int)_Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(IPv4Address other) => _Value.CompareTo(other._Value);

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(IPv4Address left, IPv4Address right) => left._Value == right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(IPv4Address left, IPv4Address right) => left._Value != right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(IPv4Address left, IPv4Address right) => left._Value < right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(IPv4Address left, IPv4Address right) => left._Value > right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(IPv4Address left, IPv4Address right) => left._Value <= right._Value;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(IPv4Address left, IPv4Address right) => left._Value >= right._Value;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteOctet(byte value, Span<char> dest)
    {
        if (value >= 100)
        {
            dest[0] = (char)('0' + value / 100);
            dest[1] = (char)('0' + (value / 10) % 10);
            dest[2] = (char)('0' + value % 10);
            return 3;
        }
        if (value >= 10)
        {
            dest[0] = (char)('0' + value / 10);
            dest[1] = (char)('0' + value % 10);
            return 2;
        }
        dest[0] = (char)('0' + value);
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OctetLength(byte value) => value >= 100 ? 3 : value >= 10 ? 2 : 1;
    #endregion
}

