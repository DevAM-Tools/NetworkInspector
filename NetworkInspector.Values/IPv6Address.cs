// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>128-bit IPv6 address stored as two <see cref="ulong"/> fields.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct IPv6Address
        : IEquatable<IPv6Address>, IComparable<IPv6Address>, IComparable,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable,
      ISpanParsable<IPv6Address>, IParsable<IPv6Address>
{
    #region Constants

    /// <summary>Maximum formatted length: "XXXX:XXXX:XXXX:XXXX:XXXX:XXXX:XXXX:XXXX" = 39 characters.</summary>
    public const int MaxFormattedLength = 39;

    #endregion

    #region Constructor

    /// <summary>Creates an <see cref="IPv6Address"/> from two 64-bit halves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IPv6Address(ulong high, ulong low)
    {
        _High = high;
        _Low = low;
    }

    #endregion

    #region Fields

    private readonly ulong _High; // bits 127..64
    private readonly ulong _Low;  // bits 63..0

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

    /// <summary>
    /// Parses an IPv6 address from colon-hex notation with optional <c>::</c> compression (RFC 5952).
    /// </summary>
    /// <param name="text">Input to parse.</param>
    /// <param name="result">The parsed address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if parsing succeeded; <see langword="false"/> for any malformed input.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out IPv6Address result)
    {
        result = default;

        // Locate a '::' expansion, if present.
        int colonColon = -1;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == ':' && text[i + 1] == ':')
            {
                if (colonColon >= 0) { return false; } // only one '::' allowed
                colonColon = i;
            }
        }

        Span<ushort> groups = stackalloc ushort[8];
        int groupsFilled;

        if (colonColon < 0)
        {
            // No '::' — must have exactly 8 colon-separated groups
            groupsFilled = ParseGroups(text, groups);
            if (groupsFilled != 8) { return false; }
        }
        else
        {
            // Split on '::'
            ReadOnlySpan<char> left = text[..colonColon];
            ReadOnlySpan<char> right = colonColon + 2 < text.Length
                ? text[(colonColon + 2)..]
                : ReadOnlySpan<char>.Empty;

            Span<ushort> leftGroups = stackalloc ushort[8];
            Span<ushort> rightGroups = stackalloc ushort[8];

            int leftCount = left.IsEmpty ? 0 : ParseGroups(left, leftGroups);
            int rightCount = right.IsEmpty ? 0 : ParseGroups(right, rightGroups);

            if (leftCount < 0 || rightCount < 0) { return false; }
            if (leftCount + rightCount > 7) { return false; } // at least one zero group needed

            for (int i = 0; i < leftCount; i++) { groups[i] = leftGroups[i]; }
            // Zero groups are already zeroed by stackalloc
            for (int i = 0; i < rightCount; i++) { groups[8 - rightCount + i] = rightGroups[i]; }
            groupsFilled = 8;
        }

        ulong high = ((ulong)groups[0] << 48) | ((ulong)groups[1] << 32) |
                     ((ulong)groups[2] << 16) | groups[3];
        ulong low = ((ulong)groups[4] << 48) | ((ulong)groups[5] << 32) |
                    ((ulong)groups[6] << 16) | groups[7];
        result = new IPv6Address(high, low);
        return true;
    }

    #endregion

    #region ISpanParsable / IParsable

    /// <inheritdoc/>
    static bool ISpanParsable<IPv6Address>.TryParse(
        ReadOnlySpan<char> s, IFormatProvider? provider, out IPv6Address result)
        => TryParse(s, out result);

    /// <inheritdoc/>
    static IPv6Address ISpanParsable<IPv6Address>.Parse(
        ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, out IPv6Address result))
        {
            throw new FormatException($"Input '{s}' is not a valid IPv6 address.");
        }
        return result;
    }

    /// <inheritdoc/>
    static bool IParsable<IPv6Address>.TryParse(
        string? s, IFormatProvider? provider, out IPv6Address result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s.AsSpan(), out result);
    }

    /// <inheritdoc/>
    static IPv6Address IParsable<IPv6Address>.Parse(
        string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s.AsSpan(), out IPv6Address result))
        {
            throw new FormatException($"Input '{s}' is not a valid IPv6 address.");
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
        if (!TryGetGroups(groups))
        {
            throw new ArgumentException("Destination span must have at least 8 elements.", nameof(groups));
        }
    }

    /// <summary>
    /// Writes the 8 groups of 16-bit values into the destination span.
    /// Returns <see langword="false"/> without throwing when the span is too small.
    /// </summary>
    /// <param name="groups">Destination span; must have length &gt;= 8.</param>
    /// <returns><see langword="true"/> if the groups were written; <see langword="false"/> when the span is too small.</returns>
    public bool TryGetGroups(Span<ushort> groups)
    {
        if (groups.Length < 8) { return false; }
        groups[0] = (ushort)(_High >> 48);
        groups[1] = (ushort)(_High >> 32);
        groups[2] = (ushort)(_High >> 16);
        groups[3] = (ushort)_High;
        groups[4] = (ushort)(_Low >> 48);
        groups[5] = (ushort)(_Low >> 32);
        groups[6] = (ushort)(_Low >> 16);
        groups[7] = (ushort)_Low;
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

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static or pooled buffer.</summary>
    /// <remarks>
    /// The underlying buffer is thread-local or pooled. The caller must dispose the returned <see cref="TempString"/>
    /// before making another <see cref="FormatTemp"/> call on the same thread to avoid overwriting the buffer.
    /// Do not retain references to the underlying span after disposal.
    /// </remarks>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(MaxFormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the compressed IPv6 address as a new string.</summary>
    /// <remarks>Allocates a new string on every call. Use <see cref="FormatInto"/> or <see cref="FormatTemp"/> for allocation-free hot paths.</remarks>
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
    public int CompareTo(IPv6Address other)
    {
        int c = _High.CompareTo(other._High);
        return c != 0 ? c : _Low.CompareTo(other._Low);
    }

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is IPv6Address other) { return CompareTo(other); }
        throw new ArgumentException($"Object must be of type {nameof(IPv6Address)}.", nameof(obj));
    }

    #endregion

    #region Operators

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

    // Parses colon-separated hex groups from a span (no '::' handling).
    // Returns the number of groups parsed, or -1 on malformed input.
    private static int ParseGroups(ReadOnlySpan<char> text, Span<ushort> groups)
    {
        int count = 0;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == ':')
            {
                int len = i - start;
                if (len == 0 || len > 4) { return -1; }
                ushort v = 0;
                for (int j = start; j < i; j++)
                {
                    int d = HexDigitValue(text[j]);
                    if (d < 0) { return -1; }
                    v = (ushort)((v << 4) | d);
                }
                if (count >= groups.Length) { return -1; }
                groups[count++] = v;
                start = i + 1;
            }
        }
        return count;
    }

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

