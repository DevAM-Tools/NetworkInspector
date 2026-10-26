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

    /// <summary>Largest whole-second count whose product with 1e9 fits a <see cref="long"/>.</summary>
    private const long _MaxScalableSeconds = long.MaxValue / _NanosPerSecond;

    /// <summary>Smallest whole-second count whose product with 1e9 fits a <see cref="long"/>.</summary>
    private const long _MinScalableSeconds = long.MinValue / _NanosPerSecond;

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

        // Both fractions are below 1e9, so their sum (< 2e9) fits an int and needs at most one carry.
        int nanos = left.Nanoseconds + right.Nanoseconds;

        // A wrapped second count is a false result, so a corrupt or hostile timestamp is reported as a bad line instead of
        // silently producing a wrong time. The range is checked explicitly: a try/catch around checked arithmetic would cost
        // one exception per hostile line, and an expected failure must not use an exception.
        if (!_TryAddLongs(left.WholeSeconds, right.WholeSeconds, out long seconds))
        {
            return false;
        }

        if (nanos >= _NanosPerSecond)
        {
            nanos -= _NanosPerSecond;
            if (!_TryAddLongs(seconds, 1, out seconds))
            {
                return false;
            }
        }

        sum = new AscTimestamp(seconds, nanos);
        return true;
    }

    /// <summary>
    /// Converts a header Unix time plus an ASC offset into nanoseconds since the Unix epoch.
    /// Range-checks every step so a value that does not fit is rejected instead of wrapping (no exception is used).
    /// </summary>
    /// <param name="startUnixNanos">Header date in nanoseconds since the Unix epoch.</param>
    /// <param name="offset">Line clock (absolute offset or accumulated relative offset).</param>
    /// <param name="unixNanos">Sum. Zero when this method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the fraction is out of range or the sum overflows <see cref="long"/>.</returns>
    internal static bool TryToUnixNanos(long startUnixNanos, AscTimestamp offset, out long unixNanos)
    {
        unixNanos = 0;

        // The type promises a fraction in 0..999_999_999; a default or hand-built value that breaks it (negative or too
        // large) is rejected here, because the second scaling below assumes it. The unsigned compare checks both ends.
        if ((uint)offset.Nanoseconds >= (uint)_NanosPerSecond)
        {
            return false;
        }

        // Every step is range-checked without exceptions (a hostile timestamp must not cost one exception per line): seconds
        // x 1e9 overflows a long beyond about 292 years, and adding the fraction or the header date can overflow too. The
        // seconds are checked against the largest and smallest count whose product with 1e9 still fits, so the multiplication
        // below cannot wrap.
        if (offset.WholeSeconds > _MaxScalableSeconds || offset.WholeSeconds < _MinScalableSeconds)
        {
            return false;
        }

        long scaledSeconds = offset.WholeSeconds * _NanosPerSecond;
        if (!_TryAddLongs(scaledSeconds, offset.Nanoseconds, out long extra)
            || !_TryAddLongs(startUnixNanos, extra, out long total))
        {
            return false;
        }

        unixNanos = total;
        return true;
    }

    /// <summary>
    /// Reads the digits before the decimal point into <paramref name="whole"/> and advances <paramref name="index"/> past them.
    /// Hand-rolled instead of <c>long.Parse</c>/<c>double.Parse</c>: it works on the raw bytes without a string, and the
    /// integer parse keeps full precision (a <see cref="double"/> loses nanoseconds beyond about 2^53 ns, roughly 104 days).
    /// </summary>
    /// <returns><c>false</c> when there is no digit or the value would overflow <see cref="long"/>.</returns>
    private static bool _TryParseWholeSeconds(ReadOnlySpan<byte> token, ref int index, out long whole)
    {
        whole = 0;
        bool any = false;
        while (index < token.Length && _IsDigit(token[index]))
        {
            any = true;
            int digit = token[index] - (byte)'0';

            // Overflow guard before the multiply: whole * 10 + digit must stay ≤ long.MaxValue.
            if (whole > (long.MaxValue - digit) / 10)
            {
                return false;
            }

            whole = (whole * 10) + digit;
            index++;
        }

        return any;
    }

    /// <summary>
    /// Reads the fraction digits after the decimal point. The first nine digits become the nanoseconds; the tenth only
    /// decides rounding (half up); any further digits are consumed and ignored. A carry out of the fraction
    /// (<c>.9999999995</c>) increments <paramref name="whole"/>.
    /// </summary>
    /// <returns><c>false</c> when a non-digit follows or the carry would overflow <see cref="long"/>.</returns>
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

        // Scale a short fraction to nanoseconds: ".5" is 500 000 000 ns. Integer math only, so the result is exact.
        for (int pad = digits; pad < 9; pad++)
        {
            nanos *= 10;
        }

        if (roundDigit >= 5)
        {
            nanos++;
        }

        // Rounding up can produce exactly one second: carry it into the whole seconds so the fraction stays below 1e9.
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

    /// <summary>
    /// Negates the parsed magnitude while keeping the fraction non-negative: −(w + n/10^9) = −(w + 1) + (10^9 − n)/10^9.
    /// </summary>
    /// <returns><c>false</c> when borrowing one second would overflow <see cref="long"/>.</returns>
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

    /// <summary>
    /// Adds two longs and reports whether the sum fits. Signed addition overflows exactly when both operands have the same
    /// sign and the wrapped result has the other one, which the XOR test below detects without an exception.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryAddLongs(long left, long right, out long sum)
    {
        sum = unchecked(left + right);
        return ((left ^ sum) & (right ^ sum)) >= 0;
    }

    /// <summary>ASCII digit test with one unsigned compare (a value below '0' wraps to a large <see cref="uint"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsDigit(byte value) => (uint)(value - (byte)'0') <= 9;
}
