// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>Nanosecond-precision timestamp (nanoseconds since Unix epoch).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Timestamp
        : IEquatable<Timestamp>, IComparable<Timestamp>, IComparable,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable,
      ISpanParsable<Timestamp>, IParsable<Timestamp>
{
    #region Constants

    /// <summary>Maximum formatted length: yyyy-MM-ddTHH:mm:ss.nnnnnnnnnZ = 30.</summary>
    public const int MaxFormattedLength = 30;

    private const long _NanosPerSecond = 1_000_000_000;
    private const long _NanosPerMilli = 1_000_000;
    private const long _NanosPerMicro = 1_000;

    #endregion

    #region Constructor

    /// <summary>Creates a <see cref="Timestamp"/> from nanoseconds since Unix epoch.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Timestamp(long nanos)
    {
        _Nanos = nanos;
    }

    #endregion

    #region Fields

    private readonly long _Nanos;

    #endregion

    #region Properties

    /// <summary>Current UTC time as a timestamp.</summary>
    /// <remarks>
    /// Resolution is constrained by the underlying OS clock. On Windows, <see cref="DateTimeOffset.UtcNow"/>
    /// typically has ~15 ms resolution; the nanosecond sub-millisecond digits are therefore zero-padded.
    /// On Linux and macOS, resolution is typically 1 µs or better.
    /// </remarks>
    public static Timestamp Now
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long ticks = DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks;
            return new Timestamp(ticks * 100);
        }
    }

    /// <summary>The raw nanosecond value.</summary>
    /// <remarks>
    /// Negative values represent timestamps before the Unix epoch (January 1, 1970 UTC).
    /// Hashing and comparison treat negative values correctly (consistent with <see cref="long"/> semantics).
    /// </remarks>
    public long AsNanos
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos;
    }
    /// <summary>The value in microseconds (truncated).</summary>
    public long AsMicros
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / _NanosPerMicro;
    }
    /// <summary>The value in milliseconds (truncated).</summary>
    public long AsMillis
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / _NanosPerMilli;
    }
    /// <summary>The whole seconds part.</summary>
    public long Secs
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / _NanosPerSecond;
    }
    /// <summary>The sub-second nanosecond part.</summary>
    public int SubsecNanos
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_Nanos % _NanosPerSecond);
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a timestamp from nanoseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromNanos(long nanos) => new(nanos);
    /// <summary>Creates a timestamp from microseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromMicros(long micros) => new(micros * _NanosPerMicro);
    /// <summary>Creates a timestamp from milliseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromMillis(long millis) => new(millis * _NanosPerMilli);
    /// <summary>Creates a timestamp from seconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromSecs(long secs) => new(secs * _NanosPerSecond);
    /// <summary>Creates a timestamp from seconds and nanoseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromSecsAndNanos(long secs, int nanos) =>
        new(secs * _NanosPerSecond + nanos);

    /// <summary>
    /// Parses a timestamp from ISO 8601 UTC nanosecond format <c>yyyy-MM-ddTHH:mm:ss.nnnnnnnnnZ</c>.
    /// </summary>
    /// <param name="text">Input to parse; must be exactly 30 characters.</param>
    /// <param name="result">The parsed timestamp when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if parsing succeeded; <see langword="false"/> for any malformed input.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out Timestamp result)
    {
        result = default;
        if (text.Length != MaxFormattedLength) { return false; }
        // Expected format: yyyy-MM-ddTHH:mm:ss.nnnnnnnnnZ
        // Positions:       0123456789012345678901234567890
        if (text[4] != '-' || text[7] != '-' || text[10] != 'T' ||
            text[13] != ':' || text[16] != ':' || text[19] != '.' ||
            text[29] != 'Z')
        {
            return false;
        }
        if (!_TryParse4Digits(text[0..4], out int year)) { return false; }
        if (!_TryParse2Digits(text[5..7], out int month)) { return false; }
        if (!_TryParse2Digits(text[8..10], out int day)) { return false; }
        if (!_TryParse2Digits(text[11..13], out int hour)) { return false; }
        if (!_TryParse2Digits(text[14..16], out int minute)) { return false; }
        if (!_TryParse2Digits(text[17..19], out int second)) { return false; }
        if (!_TryParse9Digits(text[20..29], out int nanos)) { return false; }

        // Validate calendar bounds
        if (month < 1 || month > 12 || day < 1 || day > 31) { return false; }
        if (hour > 23 || minute > 59 || second > 59) { return false; }

        DateTimeOffset dto;
        try
        {
            dto = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        long epochSecs = (dto.Ticks - DateTimeOffset.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;
        result = new Timestamp(epochSecs * _NanosPerSecond + nanos);
        return true;
    }

    #endregion

    #region ISpanParsable / IParsable

    /// <inheritdoc/>
    static bool ISpanParsable<Timestamp>.TryParse(
        ReadOnlySpan<char> s, IFormatProvider? provider, out Timestamp result)
        => TryParse(s, out result);

    /// <inheritdoc/>
    static Timestamp ISpanParsable<Timestamp>.Parse(
        ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, out Timestamp result))
        {
            throw new FormatException($"Input '{s}' is not a valid Timestamp.");
        }
        return result;
    }

    /// <inheritdoc/>
    static bool IParsable<Timestamp>.TryParse(
        string? s, IFormatProvider? provider, out Timestamp result)
    {
        if (s is null) { result = default; return false; }
        return TryParse(s.AsSpan(), out result);
    }

    /// <inheritdoc/>
    static Timestamp IParsable<Timestamp>.Parse(
        string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!TryParse(s.AsSpan(), out Timestamp result))
        {
            throw new FormatException($"Input '{s}' is not a valid Timestamp.");
        }
        return result;
    }

    #endregion

    #region Arithmetic

    /// <summary>Adds a <see cref="TimeSpan"/> to the timestamp.</summary>
    public static Timestamp operator +(Timestamp ts, TimeSpan duration) => Add(ts, duration);
    /// <summary>Subtracts a <see cref="TimeSpan"/> from the timestamp.</summary>
    public static Timestamp operator -(Timestamp ts, TimeSpan duration) => Subtract(ts, duration);
    /// <summary>Returns the duration between two timestamps.</summary>
    public static TimeSpan operator -(Timestamp a, Timestamp b) => Subtract(a, b);

    /// <summary>Adds a <see cref="TimeSpan"/> to a timestamp.</summary>
    /// <remarks>Uses <c>Ticks * 100</c> for exact nanosecond conversion (1 tick = 100 ns).</remarks>
    public static Timestamp Add(Timestamp ts, TimeSpan duration) =>
        new(ts._Nanos + duration.Ticks * 100);
    /// <summary>Subtracts a <see cref="TimeSpan"/> from a timestamp.</summary>
    /// <remarks>Uses <c>Ticks * 100</c> for exact nanosecond conversion (1 tick = 100 ns).</remarks>
    public static Timestamp Subtract(Timestamp ts, TimeSpan duration) =>
        new(ts._Nanos - duration.Ticks * 100);
    /// <summary>Returns the duration between two timestamps.</summary>
    /// <remarks>Uses <c>Ticks * 100</c> for exact nanosecond conversion (1 tick = 100 ns).
    /// Throws <see cref="OverflowException"/> when the nanosecond delta exceeds <see cref="long"/> range.</remarks>
    /// <exception cref="OverflowException">Thrown when the nanosecond delta overflows a signed 64-bit integer.</exception>
    public static TimeSpan Subtract(Timestamp a, Timestamp b)
    {
        checked
        {
            long delta = a._Nanos - b._Nanos;
            return TimeSpan.FromTicks(delta / 100);
        }
    }

    #endregion

    #region IBinarySerializable

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetWrittenSize(out int size)
    {
        size = 8;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Writes the raw nanosecond value as 8-byte big-endian.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 8)
        {
            bytesWritten = 0;
            return false;
        }
        BinaryPrimitives.WriteInt64BigEndian(destination, _Nanos);
        bytesWritten = 8;
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

        long secs = Secs;
        int nanos = SubsecNanos;

        // Normalize so nanos is always in [0, 999_999_999]
        if (nanos < 0)
        {
            secs -= 1;
            nanos += (int)_NanosPerSecond;
        }

        // Convert to DateTimeOffset for calendar decomposition
        long ticks = DateTimeOffset.UnixEpoch.Ticks + secs * TimeSpan.TicksPerSecond;
        DateTimeOffset dto = new(ticks, TimeSpan.Zero);

        // Write UTC: yyyy-MM-dd HH:mm:ss.nnnnnnnnn (space separator, no trailing Z)
        int pos = 0;
        _Write4Digits(dto.Year, destination[pos..]);
        pos += 4;
        destination[pos++] = '-';
        _Write2Digits(dto.Month, destination[pos..]);
        pos += 2;
        destination[pos++] = '-';
        _Write2Digits(dto.Day, destination[pos..]);
        pos += 2;
        // ISO 8601 'T' separator between date and time
        destination[pos++] = 'T';
        _Write2Digits(dto.Hour, destination[pos..]);
        pos += 2;
        destination[pos++] = ':';
        _Write2Digits(dto.Minute, destination[pos..]);
        pos += 2;
        destination[pos++] = ':';
        _Write2Digits(dto.Second, destination[pos..]);
        pos += 2;
        destination[pos++] = '.';
        _WriteNanos(nanos, destination[pos..]);
        pos += 9;
        // Trailing 'Z' indicates UTC (ISO 8601)
        destination[pos++] = 'Z';

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
        // UTC ISO 8601 format is always exactly 30 chars: yyyy-MM-ddTHH:mm:ss.nnnnnnnnnZ
        size = MaxFormattedLength;
        return true;
    }

    #endregion

    #region Convenience Formatting

    /// <summary>Writes the ISO 8601 UTC timestamp into the destination span.</summary>
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

    /// <summary>Returns the formatted timestamp as a new string.</summary>
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
    public int CompareTo(Timestamp other) => _Nanos.CompareTo(other._Nanos);

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null) { return 1; }
        if (obj is Timestamp other) { return CompareTo(other); }
        throw new ArgumentException($"Object must be of type {nameof(Timestamp)}.", nameof(obj));
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <(Timestamp left, Timestamp right) => left._Nanos < right._Nanos;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >(Timestamp left, Timestamp right) => left._Nanos > right._Nanos;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(Timestamp left, Timestamp right) => left._Nanos <= right._Nanos;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(Timestamp left, Timestamp right) => left._Nanos >= right._Nanos;

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _Write4Digits(int value, Span<char> dest)
    {
        dest[0] = (char)('0' + value / 1000);
        dest[1] = (char)('0' + (value / 100) % 10);
        dest[2] = (char)('0' + (value / 10) % 10);
        dest[3] = (char)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _Write2Digits(int value, Span<char> dest)
    {
        dest[0] = (char)('0' + value / 10);
        dest[1] = (char)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _WriteNanos(int nanos, Span<char> dest)
    {
        // Always write exactly 9 digits, zero-padded
        for (int i = 8; i >= 0; i--)
        {
            dest[i] = (char)('0' + nanos % 10);
            nanos /= 10;
        }
    }

    // Parse exactly 4 decimal digits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryParse4Digits(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') { return false; }
            value = value * 10 + (c - '0');
        }
        return true;
    }

    // Parse exactly 2 decimal digits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryParse2Digits(ReadOnlySpan<char> s, out int value)
    {
        char c0 = s[0]; char c1 = s[1];
        if (c0 < '0' || c0 > '9' || c1 < '0' || c1 > '9') { value = 0; return false; }
        value = (c0 - '0') * 10 + (c1 - '0');
        return true;
    }

    // Parse exactly 9 decimal digits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryParse9Digits(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        for (int i = 0; i < 9; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9') { return false; }
            value = value * 10 + (c - '0');
        }
        return true;
    }
    #endregion
}

