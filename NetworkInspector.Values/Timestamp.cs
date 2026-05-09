// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Values;

/// <summary>Nanosecond-precision timestamp (nanoseconds since Unix epoch).</summary>
/// <remarks>Creates a timestamp from nanoseconds since Unix epoch.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct Timestamp(long nanos)
        : IEquatable<Timestamp>, IComparable<Timestamp>,
      ISpanFormattable, IUtf8SpanFormattable, IStringSize, IBinarySerializable
{
    #region Constants

    /// <summary>Maximum formatted length: yyyy-MM-ddTHH:mm:ss.nnnnnnnnnZ = 30.</summary>
    public const int MaxFormattedLength = 30;

    private const long NanosPerSecond = 1_000_000_000;
    private const long NanosPerMilli = 1_000_000;
    private const long NanosPerMicro = 1_000;

    #endregion

    #region Fields

    private readonly long _Nanos = nanos;

    #endregion

    #region Properties

    /// <summary>Current UTC time as a timestamp.</summary>
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
    public long AsNanos
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos;
    }
    /// <summary>The value in microseconds (truncated).</summary>
    public long AsMicros
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / NanosPerMicro;
    }
    /// <summary>The value in milliseconds (truncated).</summary>
    public long AsMillis
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / NanosPerMilli;
    }
    /// <summary>The whole seconds part.</summary>
    public long Secs
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Nanos / NanosPerSecond;
    }
    /// <summary>The sub-second nanosecond part.</summary>
    public int SubsecNanos
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (int)(_Nanos % NanosPerSecond);
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a timestamp from nanoseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromNanos(long nanos) => new(nanos);
    /// <summary>Creates a timestamp from microseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromMicros(long micros) => new(micros * NanosPerMicro);
    /// <summary>Creates a timestamp from milliseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromMillis(long millis) => new(millis * NanosPerMilli);
    /// <summary>Creates a timestamp from seconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromSecs(long secs) => new(secs * NanosPerSecond);
    /// <summary>Creates a timestamp from seconds and nanoseconds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Timestamp FromSecsAndNanos(long secs, int nanos) =>
        new(secs * NanosPerSecond + nanos);

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
    public static TimeSpan Subtract(Timestamp a, Timestamp b) =>
        TimeSpan.FromTicks((a._Nanos - b._Nanos) / 100);

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
            nanos += (int)NanosPerSecond;
        }

        // Convert to DateTimeOffset for calendar decomposition
        long ticks = DateTimeOffset.UnixEpoch.Ticks + secs * TimeSpan.TicksPerSecond;
        DateTimeOffset dto = new(ticks, TimeSpan.Zero);

        // Write UTC: yyyy-MM-dd HH:mm:ss.nnnnnnnnn (space separator, no trailing Z)
        int pos = 0;
        Write4Digits(dto.Year, destination[pos..]);
        pos += 4;
        destination[pos++] = '-';
        Write2Digits(dto.Month, destination[pos..]);
        pos += 2;
        destination[pos++] = '-';
        Write2Digits(dto.Day, destination[pos..]);
        pos += 2;
        // ISO 8601 'T' separator between date and time
        destination[pos++] = 'T';
        Write2Digits(dto.Hour, destination[pos..]);
        pos += 2;
        destination[pos++] = ':';
        Write2Digits(dto.Minute, destination[pos..]);
        pos += 2;
        destination[pos++] = ':';
        Write2Digits(dto.Second, destination[pos..]);
        pos += 2;
        destination[pos++] = '.';
        WriteNanos(nanos, destination[pos..]);
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

    /// <summary>Returns a <see cref="TempString"/> backed by a thread-static buffer.</summary>
    public TempString FormatTemp()
    {
        char[] buffer = ZeroAllocHelper.AcquireCharBuffer(MaxFormattedLength, out bool isThreadStatic);
        TryFormat(buffer, out int written, default, null);
        return new TempString(buffer, written, isThreadStatic);
    }

    /// <summary>Returns the formatted timestamp as a new string.</summary>
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
    public bool Equals(Timestamp other) => _Nanos == other._Nanos;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Timestamp other && Equals(other);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Nanos.GetHashCode();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Timestamp other) => _Nanos.CompareTo(other._Nanos);

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(Timestamp left, Timestamp right) => left._Nanos == right._Nanos;
    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> are not equal.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> if the condition holds; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(Timestamp left, Timestamp right) => left._Nanos != right._Nanos;
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
    private static void Write4Digits(int value, Span<char> dest)
    {
        dest[0] = (char)('0' + value / 1000);
        dest[1] = (char)('0' + (value / 100) % 10);
        dest[2] = (char)('0' + (value / 10) % 10);
        dest[3] = (char)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write2Digits(int value, Span<char> dest)
    {
        dest[0] = (char)('0' + value / 10);
        dest[1] = (char)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteNanos(int nanos, Span<char> dest)
    {
        // Always write exactly 9 digits, zero-padded
        for (int i = 8; i >= 0; i--)
        {
            dest[i] = (char)('0' + nanos % 10);
            nanos /= 10;
        }
    }
    #endregion
}

