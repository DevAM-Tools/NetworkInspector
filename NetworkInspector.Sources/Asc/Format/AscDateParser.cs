// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses the date string from an ASC file header into a Unix epoch timestamp.
/// Handles the Vector CANoe/CANalyzer date format variants.
/// </summary>
internal static class AscDateParser
{
    /// <summary>
    /// Known date formats used by Vector tools in ASC headers.
    /// Covers the US/English locale default from CANoe and CANalyzer, compact ISO
    /// variants, and European numeric formats such as those produced on a German-locale
    /// Windows host where the tool writes "d.M.yyyy HH:mm:ss".
    /// </summary>
    private static readonly string[] _DateFormats =
    [
        // US formats (CANalyzer/CANoe default)
        "ddd MMM dd hh:mm:ss tt yyyy",
        "ddd MMM dd h:mm:ss tt yyyy",
        "ddd MMM  d hh:mm:ss tt yyyy",
        "ddd MMM  d h:mm:ss tt yyyy",
        // European formats
        "ddd MMM dd HH:mm:ss yyyy",
        "ddd MMM  d HH:mm:ss yyyy",
        // Compact formats
        "ddd MMM dd HH:mm:ss.fff yyyy",
        "ddd MMM  d HH:mm:ss.fff yyyy",
        // ISO-ish formats
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        // German/numeric locale formats (Vector tools on German-locale Windows)
        // e.g. "1.6.2023 10:30:00" or "01.06.2023 10:30:00"
        "d.M.yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "d.M.yyyy H:mm:ss",
        "dd.MM.yyyy H:mm:ss",
        // French/other European numeric formats
        "dd/MM/yyyy HH:mm:ss",
        "d/M/yyyy HH:mm:ss",
    ];

    /// <summary>
    /// Tries to parse a date string from an ASC header into seconds since Unix epoch.
    /// Returns 0.0 if the string could not be parsed.
    /// </summary>
    /// <param name="dateString">The date string from the ASC header (e.g., "Sun Nov 24 11:44:00 AM 2019").</param>
    /// <param name="dateTimeZone">
    /// Timezone in which the date string is interpreted. Pass
    /// <see cref="TimeZoneInfo.Utc"/> for cross-machine reproducibility, or
    /// <see cref="TimeZoneInfo.Local"/> for Vector-compatible behaviour.
    /// </param>
    /// <returns>Seconds since Unix epoch, or 0.0 if parsing failed.</returns>
    internal static double TryParseToEpoch(string dateString, TimeZoneInfo dateTimeZone)
    {
        ArgumentNullException.ThrowIfNull(dateTimeZone);

        if (string.IsNullOrWhiteSpace(dateString))
        {
            return 0.0;
        }

        // Try the known formats with invariant culture first (covers all English/US variants
        // and the explicit numeric formats that are locale-independent).
        if (DateTime.TryParseExact(
                dateString,
                _DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            return CivilToEpochSeconds(parsed, dateTimeZone);
        }

        // Fallback 1: try general parsing with invariant culture.
        if (DateTime.TryParse(dateString, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime fallback))
        {
            return CivilToEpochSeconds(fallback, dateTimeZone);
        }

        // Fallback 2: try the German locale specifically. Vector tools on a
        // German-locale Windows host emit month/day names in German (e.g., "Mo",
        // "Mrz"). The allowed set of formats is the same explicit list, so format
        // injection is not possible, and parsing remains reproducible regardless
        // of the host's CurrentCulture.
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
        if (DateTime.TryParseExact(
                dateString,
                _DateFormats,
                german,
                DateTimeStyles.None,
                out DateTime localParsed))
        {
            return CivilToEpochSeconds(localParsed, dateTimeZone);
        }

        return 0.0;
    }

    /// <summary>
    /// Converts a parsed civil time to Unix epoch seconds using the supplied timezone.
    /// Strips any <see cref="DateTimeKind"/> set by the parser and reinterprets the
    /// fields in <paramref name="dateTimeZone"/> so the result is independent of the
    /// host's local timezone.
    /// </summary>
    private static double CivilToEpochSeconds(DateTime civil, TimeZoneInfo dateTimeZone)
    {
        DateTime unspecified = DateTime.SpecifyKind(civil, DateTimeKind.Unspecified);
        DateTimeOffset dto = new(unspecified, dateTimeZone.GetUtcOffset(unspecified));
        return (dto.UtcDateTime - DateTime.UnixEpoch).TotalSeconds;
    }
}
