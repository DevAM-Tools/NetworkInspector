// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parsed representation of an ASC file header.
/// Contains metadata extracted from the header lines before the first data event.
/// </summary>
internal sealed class AscHeader
{
    #region Properties

    /// <summary>
    /// Base for numeric values in the file: 16 for hex, 10 for dec.
    /// Default is hex (16) if not specified in the file.
    /// </summary>
    internal int NumericBase { get; set; } = 16;

    /// <summary>
    /// Timestamp format: <c>"absolute"</c> (offsets from trigger block start)
    /// or <c>"relative"</c> (deltas between events).
    /// Default is <c>"absolute"</c>.
    /// </summary>
    internal string TimestampFormat { get; set; } = "absolute";

    /// <summary>
    /// Whether internal events are logged in this file.
    /// </summary>
    internal bool InternalEventsLogged
    {
        get; set;
    }

    /// <summary>
    /// The date/time string from the <c>date</c> header line.
    /// Null if no date line was present.
    /// </summary>
    internal string? DateString
    {
        get; set;
    }

    /// <summary>
    /// Parsed start timestamp in seconds since Unix epoch, derived from the date header.
    /// Zero if the date could not be parsed or was not present.
    /// </summary>
    internal double StartTimeEpoch
    {
        get; set;
    }

    /// <summary>
    /// Timezone used to interpret the SYSTEMTIME-style fields parsed from the
    /// <c>date</c> header line. Defaults to <see cref="TimeZoneInfo.Utc"/> for
    /// machine-independent reproducibility; callers (AscSource / AscStreamSource)
    /// override this from <c>AscSourceOptions.TimestampTimeZone</c> before feeding lines.
    /// </summary>
    internal TimeZoneInfo TimestampTimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>
    /// File format version string (from <c>// version X.Y.Z</c> comment).
    /// Null if not present.
    /// </summary>
    internal string? Version
    {
        get; set;
    }

    #endregion

    #region Parsing

    /// <summary>
    /// Attempts to parse a single header line from raw ASCII bytes and update this header instance.
    /// Returns <c>true</c> if the line was consumed as a header line,
    /// <c>false</c> if it is a data line (the header section is over).
    /// </summary>
    /// <param name="line">A trimmed ASC line as raw ASCII bytes.</param>
    /// <returns><c>true</c> if the line was a header line; <c>false</c> if the header section ended.</returns>
    internal bool TryParseLine(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
        {
            return true; // blank lines in the header are allowed
        }

        // Comment lines (// or ;) are part of the header
        if (line[0] == (byte)';')
        {
            return true;
        }

        if (line.Length >= 2 && line[0] == (byte)'/' && line[1] == (byte)'/')
        {
            ParseComment(line);
            return true;
        }

        // "date <datetime_string>"
        if (AscLineClassifier.StartsWithAsciiIgnoreCase(line, "date "u8))
        {
            ReadOnlySpan<byte> datePart = AscTokenizerBytes.TrimAscii(line[5..]);
            DateString = ByteSpanToString(datePart);
            StartTimeEpoch = AscDateParser.TryParseToEpoch(DateString, TimestampTimeZone);
            return true;
        }

        // "base hex|dec [timestamps absolute|relative]"
        if (AscLineClassifier.StartsWithAsciiIgnoreCase(line, "base "u8))
        {
            ParseBaseLine(line);
            return true;
        }

        // "[no] internal events logged"
        if (EndsWithAsciiIgnoreCase(line, "internal events logged"u8))
        {
            InternalEventsLogged = !AscLineClassifier.StartsWithAsciiIgnoreCase(line, "no "u8);
            return true;
        }

        // "Begin Triggerblock" ends the header; optional date on the same line updates StartTimeEpoch
        if (AscLineClassifier.StartsWithAsciiIgnoreCase(line, "Begin Triggerblock"u8))
        {
            ReadOnlySpan<byte> rest = AscTokenizerBytes.TrimStartAscii(line[18..]);
            if (!rest.IsEmpty)
            {
                string triggerDate = ByteSpanToString(rest);
                double triggerEpoch = AscDateParser.TryParseToEpoch(triggerDate, TimestampTimeZone);
                if (triggerEpoch > 0)
                {
                    StartTimeEpoch = triggerEpoch;
                }
            }

            return false; // Header section ends here
        }

        // A line starting with a digit or '-' is a data line → header is over
        if ((line[0] >= (byte)'0' && line[0] <= (byte)'9') || line[0] == (byte)'-')
        {
            return false;
        }

        // Unknown header line — consume and ignore
        return true;
    }

    /// <summary>
    /// Parses the <c>base</c> line: <c>base hex|dec [timestamps absolute|relative]</c>.
    /// </summary>
    private void ParseBaseLine(ReadOnlySpan<byte> line)
    {
        // Skip "base " and any additional leading whitespace
        ReadOnlySpan<byte> rest = AscTokenizerBytes.TrimStartAscii(line[5..]);

        // First token: "hex" or "dec"
        int spaceIdx = rest.IndexOf((byte)' ');
        ReadOnlySpan<byte> baseToken = spaceIdx >= 0 ? rest[..spaceIdx] : rest;

        if (baseToken.Length == 3 && AscLineClassifier.StartsWithAsciiIgnoreCase(baseToken, "hex"u8))
        {
            NumericBase = 16;
        }
        else if (baseToken.Length == 3 && AscLineClassifier.StartsWithAsciiIgnoreCase(baseToken, "dec"u8))
        {
            NumericBase = 10;
        }

        // Optional: "timestamps absolute|relative"
        if (spaceIdx >= 0)
        {
            ReadOnlySpan<byte> remaining = AscTokenizerBytes.TrimStartAscii(rest[(spaceIdx + 1)..]);
            if (AscLineClassifier.StartsWithAsciiIgnoreCase(remaining, "timestamps"u8))
            {
                ReadOnlySpan<byte> tsRest = AscTokenizerBytes.TrimStartAscii(remaining[10..]);
                if (AscLineClassifier.StartsWithAsciiIgnoreCase(tsRest, "absolute"u8))
                {
                    TimestampFormat = "absolute";
                }
                else if (AscLineClassifier.StartsWithAsciiIgnoreCase(tsRest, "relative"u8))
                {
                    TimestampFormat = "relative";
                }
            }
        }
    }

    /// <summary>
    /// Parses a <c>//</c> comment line for version information.
    /// </summary>
    private void ParseComment(ReadOnlySpan<byte> line)
    {
        // Skip "//" and leading whitespace
        ReadOnlySpan<byte> content = AscTokenizerBytes.TrimStartAscii(line[2..]);
        if (AscLineClassifier.StartsWithAsciiIgnoreCase(content, "version "u8))
        {
            Version = ByteSpanToString(AscTokenizerBytes.TrimAscii(content[8..]));
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Case-insensitive ASCII suffix check on byte spans.
    /// </summary>
    private static bool EndsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> suffix)
    {
        if (span.Length < suffix.Length)
        {
            return false;
        }

        return AscLineClassifier.StartsWithAsciiIgnoreCase(span[^suffix.Length..], suffix);
    }

    /// <summary>
    /// Converts a pure-ASCII byte span to a managed string.
    /// Called only for header-level strings (date, version) that are allocated once per file open
    /// — the one-time allocation is acceptable here.
    /// </summary>
    private static string ByteSpanToString(ReadOnlySpan<byte> span) =>
        System.Text.Encoding.ASCII.GetString(span);

    #endregion
}
