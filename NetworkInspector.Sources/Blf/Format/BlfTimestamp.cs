// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Converts BLF timestamps to nanoseconds since Unix epoch.
/// BLF uses two timestamp representations:
/// <list type="bullet">
///   <item>10 µs resolution (flags &amp; 0x0F == 1): multiply raw value by 10,000</item>
///   <item>1 ns resolution (flags &amp; 0x0F == 2): raw value is nanoseconds directly</item>
/// </list>
/// Absolute timestamps are computed as: file_start_offset_ns + relative_timestamp_ns.
/// </summary>
internal static class BlfTimestamp
{
    #region Public API

    /// <summary>
    /// Converts a raw BLF timestamp to nanoseconds based on resolution flags.
    /// </summary>
    /// <param name="rawTimestamp">Raw timestamp value from the log object header.</param>
    /// <param name="flags">Flags from the log object header (lower nibble = resolution).</param>
    /// <returns>Timestamp in nanoseconds (relative or absolute depending on context).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ToNanoseconds(ulong rawTimestamp, uint flags)
    {
        byte resolution = (byte)(flags & 0x0F);
        return resolution switch
        {
            BlfConstants.TimestampResolution10Us => (long)(rawTimestamp * (ulong)BlfConstants.TimestampMultiplier10Us),
            BlfConstants.TimestampResolution1Ns => (long)rawTimestamp,
            // Unknown resolution — treat as nanoseconds (best-effort)
            _ => (long)rawTimestamp,
        };
    }

    /// <summary>
    /// Converts a <see cref="BlfDate"/> (Windows SYSTEMTIME) to nanoseconds since Unix epoch.
    /// <para>
    /// The BLF specification does not say whether the SYSTEMTIME fields are UTC or local
    /// civil time. Vector's reference tooling and Wireshark's <c>blf.c</c> write/read them
    /// as local time; for cross-machine reproducibility the timezone must therefore be
    /// supplied explicitly by the caller.
    /// </para>
    /// <para>
    /// Sub-millisecond precision is lost — the SYSTEMTIME structure has 1 ms granularity.
    /// </para>
    /// </summary>
    /// <param name="date">The BLF date structure.</param>
    /// <param name="dateTimeZone">
    /// Time zone in which the SYSTEMTIME fields are interpreted. Pass
    /// <see cref="TimeZoneInfo.Utc"/> for cross-machine determinism, or
    /// <see cref="TimeZoneInfo.Local"/> for Vector / Wireshark compatibility.
    /// </param>
    /// <returns>Nanoseconds since 1970-01-01T00:00:00Z, or 0 if the date is invalid.</returns>
    internal static long DateToUnixNanoseconds(in BlfDate date, TimeZoneInfo dateTimeZone)
    {
        ArgumentNullException.ThrowIfNull(dateTimeZone);

        int year = date.Year.Value;
        int month = date.Month.Value;
        int day = date.Day.Value;
        int hour = date.Hour.Value;
        int minute = date.Minute.Value;
        int second = date.Second.Value;
        int millisecond = date.Millisecond.Value;

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

        // Use the caller-supplied zone's offset (UTC default = no shift, fully reproducible).
        DateTimeOffset dto = new(civil, dateTimeZone.GetUtcOffset(civil));
        return dto.ToUnixTimeMilliseconds() * 1_000_000L;
    }

    #endregion
}
