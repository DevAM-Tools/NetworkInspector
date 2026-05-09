// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Values;

/// <summary>128-bit IPv6 address stored as two <see cref="ulong"/> fields.</summary>
/// <remarks>Creates an IPv6 address from two 64-bit halves.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct IPv6Address(ulong high, ulong low)
        : IEquatable<IPv6Address>, IComparable<IPv6Address>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Maximum formatted length: "XXXX:XXXX:XXXX:XXXX:XXXX:XXXX:XXXX:XXXX" = 39 characters.</summary>
    public const int MaxFormattedLength = 39;

    #endregion

    #region Fields

    private readonly ulong _High = high; // bits 127..64
    private readonly ulong _Low = low;  // bits 63..0

    #endregion

    #region Properties

    /// <summary>The upper 64 bits (bits 127..64).</summary>
    public ulong High
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _High;
    }
    /// <summary>The lower 64 bits (bits 63..0).</summary>
    public ulong Low
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Low;
    }

    #endregion

    #region Classification

    /// <summary>True if this is the loopback address (::1).</summary>
    public bool IsLoopback
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _High == 0 && _Low == 1;
    }
    /// <summary>True if this is a multicast address (ff00::/8).</summary>
    public bool IsMulticast
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_High >> 56) == 0xFF;
    }
    /// <summary>True if this is a link-local address (fe80::/10).</summary>
    public bool IsLinkLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_High >> 54) == (0xFE80UL >> 6);
    }
    /// <summary>True if this is a unique local address (fc00::/7).</summary>
    public bool IsUniqueLocal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_High >> 57) == (0xFC00UL >> 9);
    }
    /// <summary>True if this is the unspecified address (::).</summary>
    public bool IsUnspecified
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _High == 0 && _Low == 0;
    }
    /// <summary>True if this is an IPv4-mapped address (::ffff:x.x.x.x).</summary>
    public bool IsIPv4Mapped
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _High == 0 && (_Low >> 32) == 0xFFFF;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates an IPv6 address from a 16-byte big-endian span.</summary>
    public static IPv6Address FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            return default;
        }
        ulong high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return new IPv6Address(high, low);
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

    /// <summary>Returns a new 16-byte array with the big-endian representation.</summary>
    public byte[] ToBytesArray()
    {
        byte[] result = new byte[16];
        ToBytes(result);
        return result;
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

    /// <summary>
    /// Writes the 8 groups of 16-bit values into the destination span.
    /// </summary>
    /// <param name="groups">Destination span, must have length &gt;= 8.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="groups"/> is too small.</exception>
    public void GetGroups(Span<ushort> groups)
    {
        if (groups.Length < 8)
        {
            throw new ArgumentException("Destination span must have at least 8 elements.", nameof(groups));
        }
        groups[0] = (ushort)(_High >> 48);
        groups[1] = (ushort)(_High >> 32);
        groups[2] = (ushort)(_High >> 16);
        groups[3] = (ushort)_High;
        groups[4] = (ushort)(_Low >> 48);
        groups[5] = (ushort)(_Low >> 32);
        groups[6] = (ushort)(_Low >> 16);
        groups[7] = (ushort)_Low;
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
        Span<ushort> groups = stackalloc ushort[8];
        GetGroups(groups);

        // Find longest run of consecutive zero groups (RFC 5952 section 4.2.3)
        FindLongestZeroRun(groups, out int bestStart, out int bestLen);

        int pos = 0;
        for (int i = 0; i < 8;)
        {
            if (i == bestStart)
            {
                // Always write "::" for zero-group compression (RFC 5952 section 4.2.3)
                destination[pos++] = ':';
                destination[pos++] = ':';
                i += bestLen;
                continue;
            }
            // Separator colon between groups, but not before the first group
            // and not directly after "::" (which already ends with ':')
            if (i > 0 && !(bestStart >= 0 && i == bestStart + bestLen))
            {
                destination[pos++] = ':';
            }
            pos += WriteHexGroup(groups[i], destination[pos..]);
            i++;
        }
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
        Span<ushort> groups = stackalloc ushort[8];
        GetGroups(groups);
        FindLongestZeroRun(groups, out int bestStart, out int bestLen);

        size = 0;
        for (int i = 0; i < 8;)
        {
            if (i == bestStart)
            {
                // "::" is always 2 chars regardless of position
                size += 2;
                i += bestLen;
                continue;
            }
            if (i > 0 && !(bestStart >= 0 && i == bestStart + bestLen))
            {
                size++; // colon separator
            }
            size += HexGroupLength(groups[i]);
            i++;
        }
        return true;
    }

    #endregion

    #region Convenience Formatting

    /// <summary>Writes the compressed IPv6 address into the destination span.</summary>
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

    /// <summary>Returns the compressed IPv6 address as a new string.</summary>
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
    public bool Equals(IPv6Address other) => _High == other._High && _Low == other._Low;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IPv6Address other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => HashCode.Combine(_High, _Low);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(IPv6Address other)
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
    public static bool operator ==(IPv6Address left, IPv6Address right) =>
        left._High == right._High && left._Low == right._Low;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(IPv6Address left, IPv6Address right) => !(left == right);
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(IPv6Address left, IPv6Address right) => left.CompareTo(right) < 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(IPv6Address left, IPv6Address right) => left.CompareTo(right) > 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(IPv6Address left, IPv6Address right) => left.CompareTo(right) <= 0;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(IPv6Address left, IPv6Address right) => left.CompareTo(right) >= 0;

    #endregion

    #region Private Helpers

    /// <summary>Finds the longest run of consecutive zero groups for :: compression.</summary>
    private static void FindLongestZeroRun(
        ReadOnlySpan<ushort> groups, out int bestStart, out int bestLen)
    {
        bestStart = -1;
        bestLen = 0;
        int curStart = -1;
        int curLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if (groups[i] == 0)
            {
                if (curStart < 0)
                {
                    curStart = i;
                    curLen = 1;
                }
                else
                {
                    curLen++;
                }
                if (curLen > bestLen)
                {
                    bestStart = curStart;
                    bestLen = curLen;
                }
            }
            else
            {
                curStart = -1;
                curLen = 0;
            }
        }
        // RFC 5952 section 4.2.2: don't compress a single zero group
        if (bestLen < 2)
        {
            bestStart = -1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteHexGroup(ushort value, Span<char> dest)
    {
        ReadOnlySpan<char> hex = "0123456789ABCDEF";
        if (value >= 0x1000)
        {
            dest[0] = hex[value >> 12];
            dest[1] = hex[(value >> 8) & 0xF];
            dest[2] = hex[(value >> 4) & 0xF];
            dest[3] = hex[value & 0xF];
            return 4;
        }
        if (value >= 0x100)
        {
            dest[0] = hex[(value >> 8) & 0xF];
            dest[1] = hex[(value >> 4) & 0xF];
            dest[2] = hex[value & 0xF];
            return 3;
        }
        if (value >= 0x10)
        {
            dest[0] = hex[(value >> 4) & 0xF];
            dest[1] = hex[value & 0xF];
            return 2;
        }
        dest[0] = hex[value & 0xF];
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexGroupLength(ushort value) =>
        value >= 0x1000 ? 4 : value >= 0x100 ? 3 : value >= 0x10 ? 2 : 1;
    #endregion
}

