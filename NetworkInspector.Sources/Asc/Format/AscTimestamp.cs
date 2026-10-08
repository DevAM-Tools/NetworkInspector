// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// ASC line time as whole seconds plus a nanosecond fraction in <c>0..999_999_999</c>.
/// Negative durations borrow from <see cref="WholeSeconds"/> so the fraction stays non-negative.
/// Scaling to Unix nanoseconds happens in <see cref="long"/>, not in <see cref="double"/>.
/// </summary>
/// <param name="WholeSeconds">Whole seconds. May be negative.</param>
/// <param name="Nanoseconds">Fraction of a second, always <c>0..999_999_999</c>.</param>
internal readonly record struct AscTimestamp(long WholeSeconds, int Nanoseconds)
{
    private const int _NanosPerSecond = 1_000_000_000;

    /// <summary>
    /// Parses an ASC timestamp token. An optional leading <c>-</c> is allowed.
    /// Up to nine fractional digits are kept; a tenth digit <c>&gt;= 5</c> rounds up by one nanosecond.
    /// </summary>
    /// <param name="token">ASCII token, without surrounding whitespace.</param>
    /// <param name="value">Parsed time. <c>default</c> when this method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the token is empty, non-numeric, or the magnitude does not fit in <see cref="long"/>.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> token, out AscTimestamp value)
    {
        value = default;
        if (token.IsEmpty)
        {
            return false;
        }

        int index = 0;
        bool negative = false;
        if (token[0] == (byte)'-')
        {
            negative = true;
            index = 1;
            if (index >= token.Length)
            {
                return false;
            }
        }

        if (!_TryParseWholeSeconds(token, ref index, out long whole))
        {
            return false;
        }

        int nanos = 0;
        if (index < token.Length)
        {
            if (token[index] != (byte)'.')
            {
                return false;
            }

            index++;
            if (!_TryParseFraction(token, ref index, ref whole, out nanos))
            {
                return false;
            }
        }

        if (index != token.Length)
        {
            return false;
        }

        if (negative && !_ApplyNegative(ref whole, ref nanos))
        {
            return false;
        }

        value = new AscTimestamp(whole, nanos);
        return true;
    }

    /// <summary>
    /// Folds one timestamped line into the running file clock.
    /// Relative mode adds the line time. Absolute mode replaces the running value.
    /// Call this for every line whose first token is a timestamp, including events that are not frames.
    /// A line that does not start with a timestamp leaves <paramref name="running"/> unchanged.
    /// </summary>
    /// <param name="running">Clock so far. Updated only when this method returns <c>true</c>.</param>
    /// <param name="relative"><c>true</c> when the header says <c>timestamps relative</c>.</param>
    /// <param name="line">Trimmed ASC line. The first token is the line time.</param>
    /// <param name="applied">Clock value to store on the frame after a successful fold.</param>
    /// <returns><c>false</c> when the first token is not a timestamp or the sum overflows.</returns>
    internal static bool TryFold(
        ref AscTimestamp running,
        bool relative,
        ReadOnlySpan<byte> line,
        out AscTimestamp applied)
    {
        applied = default;
        AscTokenizerBytes tokenizer = new(line);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> token)
            || !TryParse(token, out AscTimestamp lineTime))
        {
            return false;
        }

        if (!relative)
        {
            running = lineTime;
            applied = lineTime;
            return true;
        }

        if (!TryAdd(running, lineTime, out AscTimestamp sum))
        {
            return false;
        }

        running = sum;
        applied = sum;
        return true;
    }

    /// <summary>
    /// Adds two normalized timestamps. The fraction of the result stays in <c>0..999_999_999</c>.
    /// </summary>
    internal static bool TryAdd(AscTimestamp left, AscTimestamp right, out AscTimestamp sum)
    {
        sum = default;
        int nanos = left.Nanoseconds + right.Nanoseconds;
        long seconds;
        try
        {
            seconds = checked(left.WholeSeconds + right.WholeSeconds);
            if (nanos >= _NanosPerSecond)
            {
                nanos -= _NanosPerSecond;
                seconds = checked(seconds + 1);
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        sum = new AscTimestamp(seconds, nanos);
        return true;
    }

    /// <summary>
    /// Converts a header Unix time plus an ASC offset into nanoseconds since the Unix epoch.
    /// Uses <c>checked</c> arithmetic so a value that does not fit is rejected instead of wrapping.
    /// </summary>
    /// <param name="startUnixNanos">Header date in nanoseconds since the Unix epoch.</param>
    /// <param name="offset">Line clock (absolute offset or accumulated relative offset).</param>
    /// <param name="unixNanos">Sum. Zero when this method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the fraction is out of range or the sum overflows <see cref="long"/>.</returns>
    internal static bool TryToUnixNanos(long startUnixNanos, AscTimestamp offset, out long unixNanos)
    {
        unixNanos = 0;
        if ((uint)offset.Nanoseconds >= (uint)_NanosPerSecond)
        {
            return false;
        }

        try
        {
            long extra = checked((offset.WholeSeconds * _NanosPerSecond) + offset.Nanoseconds);
            unixNanos = checked(startUnixNanos + extra);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool _TryParseWholeSeconds(ReadOnlySpan<byte> token, ref int index, out long whole)
    {
        whole = 0;
        bool any = false;
        while (index < token.Length && _IsDigit(token[index]))
        {
            any = true;
            int digit = token[index] - (byte)'0';
            if (whole > (long.MaxValue - digit) / 10)
            {
                return false;
            }

            whole = (whole * 10) + digit;
            index++;
        }

        return any;
    }

    private static bool _TryParseFraction(ReadOnlySpan<byte> token, ref int index, ref long whole, out int nanos)
    {
        nanos = 0;
        int digits = 0;
        int roundDigit = -1;
        while (index < token.Length && _IsDigit(token[index]))
        {
            int digit = token[index] - (byte)'0';
            if (digits < 9)
            {
                nanos = (nanos * 10) + digit;
                digits++;
            }
            else if (digits == 9)
            {
                roundDigit = digit;
                digits++;
            }

            index++;
        }

        if (index != token.Length)
        {
            return false;
        }

        for (int pad = digits; pad < 9; pad++)
        {
            nanos *= 10;
        }

        if (roundDigit >= 5)
        {
            nanos++;
        }

        if (nanos < _NanosPerSecond)
        {
            return true;
        }

        nanos = 0;
        if (whole == long.MaxValue)
        {
            return false;
        }

        whole++;
        return true;
    }

    private static bool _ApplyNegative(ref long whole, ref int nanos)
    {
        if (nanos == 0)
        {
            whole = -whole;
            return true;
        }

        if (whole == long.MaxValue)
        {
            return false;
        }

        whole = -whole - 1;
        nanos = _NanosPerSecond - nanos;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsDigit(byte value) => (uint)(value - (byte)'0') <= 9;
}
