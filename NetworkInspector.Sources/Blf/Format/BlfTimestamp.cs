// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Converts BLF timestamps to nanoseconds since Unix epoch.
/// BLF uses two timestamp representations, matched on the whole <c>flags</c> value:
/// <list type="bullet">
///   <item>10 µs resolution (<c>flags == 1</c>): multiply raw value by 10,000</item>
///   <item>1 ns resolution (<c>flags == 2</c>): raw value is nanoseconds directly</item>
///   <item>any other flags word, including a value whose low nibble is 1 or 2: timestamp 0</item>
/// </list>
/// Absolute timestamps are computed as: file_start_offset_ns + relative_timestamp_ns.
/// </summary>
/// <remarks>
/// Matching the whole flags word is deliberate and documented behaviour: only the exact words 1 and 2 define a unit, and
/// any other word is treated as an unknown resolution (timestamp 0) instead of guessing a unit. Overflow policy: a raw
/// value that does not fit a nanosecond <see cref="long"/> saturates at <see cref="long.MaxValue"/> rather than wrapping
/// to a negative (pre-1970) time. Stateless; thread-safe.
/// </remarks>
internal static class BlfTimestamp
{
    #region Public API

    /// <summary>
    /// Converts a raw BLF timestamp to nanoseconds based on resolution flags.
    /// </summary>
    /// <param name="rawTimestamp">Raw timestamp value from the log object header.</param>
    /// <param name="flags">
    /// Whole flags word. 1 = 10 µs units, 2 = nanoseconds.
    /// Any other word, including a value whose low nibble is 1 or 2, yields timestamp 0.
    /// </param>
    /// <returns>Timestamp in nanoseconds (relative or absolute depending on context).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ToNanoseconds(ulong rawTimestamp, uint flags)
    {
        if (flags == BlfConstants.TimestampResolution10Us)
        {
            // Largest raw value whose product with 10,000 still fits a signed 64-bit nanosecond count.
            const ulong maxRaw = (ulong)long.MaxValue / (ulong)BlfConstants.TimestampMultiplier10Us;
            if (rawTimestamp > maxRaw)
            {
                return long.MaxValue;
            }

            return (long)(rawTimestamp * (ulong)BlfConstants.TimestampMultiplier10Us);
        }

        if (flags == BlfConstants.TimestampResolution1Ns)
        {
            if (rawTimestamp > (ulong)long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)rawTimestamp;
        }

        return 0;
    }

    /// <summary>
    /// Adds the file start offset to a relative object time; saturates at <see cref="long.MaxValue"/> instead of
    /// wrapping. <see cref="ToNanoseconds"/> already saturates the relative part, so without this a saturated object
    /// time plus a positive start offset would wrap to a timestamp before 1970.
    /// </summary>
    /// <param name="startOffsetNanos">File start offset in nanoseconds since the Unix epoch (0 for an invalid date).</param>
    /// <param name="relativeNanos">Object time relative to the file start, from <see cref="ToNanoseconds"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ToAbsolute(long startOffsetNanos, long relativeNanos)
    {
        // Both operands are non-negative here (start offset >= 0, relative >= 0): a negative sum is an overflow.
        long sum = unchecked(startOffsetNanos + relativeNanos);
        return startOffsetNanos >= 0 && relativeNanos >= 0 && sum < 0
            ? long.MaxValue
            : sum;
    }

    /// <summary>
    /// Converts a <see cref="BlfDate"/> (Windows SYSTEMTIME) to nanoseconds since Unix epoch.
    /// <para>
    /// The BLF specification does not say whether the SYSTEMTIME fields are UTC or local
    /// civil time. Vector's reference tooling writes and reads them as local time; for
    /// cross-machine reproducibility the timezone must therefore be supplied explicitly
    /// by the caller.
    /// </para>
    /// <para>
    /// Sub-millisecond precision is lost — the SYSTEMTIME structure has 1 ms granularity.
    /// </para>
    /// </summary>
    /// <param name="date">The BLF date structure.</param>
    /// <param name="dateTimeZone">
    /// Time zone in which the SYSTEMTIME fields are interpreted. Pass
    /// <see cref="TimeZoneInfo.Utc"/> for cross-machine determinism, or
    /// <see cref="TimeZoneInfo.Local"/> for Vector tooling compatibility.
    /// </param>
    /// <returns>
    /// Nanoseconds since 1970-01-01T00:00:00Z, 0 if the date is invalid, or <see cref="long.MaxValue"/> when the instant
    /// does not fit a nanosecond count (dates from the year 2262 on, including those beyond <see cref="DateTime.MaxValue"/>
    /// once the zone offset is applied). Never throws for a header value.
    /// </returns>
    internal static long DateToUnixNanoseconds(in BlfDate date, TimeZoneInfo dateTimeZone)
    {
        ArgumentNullException.ThrowIfNull(dateTimeZone);

        int year = date.Year;
        int month = date.Month;
        int day = date.Day;
        int hour = date.Hour;
        int minute = date.Minute;
        int second = date.Second;
        int millisecond = date.Millisecond;

        // Validate ranges; an all-zero or out-of-range BlfDate yields epoch=0.
        if (year < 1970 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31)
        {
            return 0;
        }

        DateTime civil;
        try
        {
            civil = new DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }

        // Use the caller-supplied zone's offset (UTC default = no shift, fully reproducible). The offset is taken for the
        // civil time itself, so a DST transition inside the zone is resolved by the zone's own rules.
        //
        // The UTC instant is computed from ticks instead of through new DateTimeOffset(civil, offset): that constructor
        // throws when the UTC instant falls outside DateTime's range, and a header dated 9999-12-31 in a zone west of UTC
        // is exactly that. A hostile file header must never turn into an exception, so the range is checked explicitly.
        // civil.Ticks is within [0, DateTime.MaxValue.Ticks] and a zone offset is within +-14 hours, so the subtraction
        // cannot overflow a long.
        long utcTicks = civil.Ticks - dateTimeZone.GetUtcOffset(civil).Ticks;
        long millisecondsSinceEpoch = (utcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

        // A date whose nanosecond count does not fit a long (from the year 2262 on) saturates at long.MaxValue, the same
        // policy as ToNanoseconds, instead of wrapping to a negative (pre-1970) start time. Rejected alternative: returning 0
        // (that would make a far-future header indistinguishable from a missing date and shift every timestamp to 1970).
        const long maxMilliseconds = long.MaxValue / 1_000_000L;
        if (millisecondsSinceEpoch > maxMilliseconds)
        {
            return long.MaxValue;
        }

        return millisecondsSinceEpoch * 1_000_000L;
    }

    #endregion
}
