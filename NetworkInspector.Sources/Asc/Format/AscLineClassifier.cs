// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Fast, allocation-free classifier for ASC file lines.
/// Examines line prefixes to determine the bus type and event kind.
/// Provides overloads for both <c>ReadOnlySpan&lt;char&gt;</c> (string-based path) and
/// <c>ReadOnlySpan&lt;byte&gt;</c> (raw ASCII byte path, zero allocation during disk scan).
/// </summary>
internal static class AscLineClassifier
{
    private static readonly SearchValues<char> _AsciiTimestampDigits = SearchValues.Create("0123456789");
    private static readonly SearchValues<byte> _AsciiTimestampDigitBytes = SearchValues.Create("0123456789"u8);

    #region Char overload

    /// <summary>
    /// Classifies a single ASC line by examining its prefix tokens.
    /// </summary>
    /// <param name="line">A trimmed line from an ASC file.</param>
    /// <returns>The classified <see cref="AscLineType"/>.</returns>
    internal static AscLineType Classify(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return AscLineType.Comment;
        }

        // Comment lines
        if (line[0] == ';' || (line.Length > 1 && line[0] == '/' && line[1] == '/'))
        {
            return AscLineType.Comment;
        }

        // Header keywords (must come before timestamp check)
        if (line.StartsWith("date ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("base ", StringComparison.OrdinalIgnoreCase)
            || line.EndsWith("internal events logged", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.Header;
        }

        // Trigger block markers
        if (line.StartsWith("Begin Triggerblock", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.TriggerBlockBegin;
        }

        if (line.StartsWith("End TriggerBlock", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("End Triggerblock", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.TriggerBlockEnd;
        }

        // Start of measurement
        if (line.StartsWith("Start of measurement", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.StartOfMeasurement;
        }

        // CAN FD — lines starting with "CANFD" (no timestamp prefix, CANFD is the line start in some variants)
        // Or lines with timestamp where the second/third token is "CANFD"
        if (_ContainsCanFdToken(line))
        {
            return AscLineType.CanFdMessage;
        }

        // From here, data lines typically start with a timestamp: <digits>.<digits> <...>
        // Find the first space after the timestamp
        int firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return AscLineType.Unknown;
        }

        // Ensure the prefix looks like a timestamp (digits and dots)
        ReadOnlySpan<char> timestamp = line[..firstSpace];
        if (!_LooksLikeTimestamp(timestamp))
        {
            return AscLineType.Unknown;
        }

        // Get remaining after timestamp, trimmed
        ReadOnlySpan<char> rest = line[(firstSpace + 1)..].TrimStart();

        if (rest.IsEmpty)
        {
            return AscLineType.Unknown;
        }

        // ErrorFrame — can appear as "<time> <ch> ErrorFrame" or "<time> <ch>  ErrorFrame"
        if (_ContainsToken(rest, "ErrorFrame"))
        {
            return AscLineType.CanErrorFrame;
        }

        // OverloadFrame
        if (_ContainsToken(rest, "OverloadFrame"))
        {
            return AscLineType.CanOverloadFrame;
        }

        // CAN status: "CAN <ch> Status:..."
        if (rest.StartsWith("CAN ", StringComparison.OrdinalIgnoreCase)
            && _ContainsToken(rest, "Status"))
        {
            return AscLineType.CanStatus;
        }

        // Statistic — bus statistics event
        if (rest.StartsWith("Statistic:", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.CanBusStatistics;
        }

        // LIN — channel like "L1", "L2", etc. or "Lin"
        if (rest[0] == 'L' && rest.Length > 1 && char.IsDigit(rest[1]))
        {
            return _ClassifyLinEvent(rest);
        }

        // FlexRay — "Fr" prefix
        if (rest.StartsWith("Fr ", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("Fr\t", StringComparison.OrdinalIgnoreCase))
        {
            return _ClassifyFlexRayEvent(rest);
        }

        // Ethernet — "ETH" or "AFDX" prefix
        if (rest.StartsWith("ETH ", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("AFDX ", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.EthernetPacket;
        }

        // Environment variable
        if (rest.StartsWith("EnvVar:", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.EnvironmentVariable;
        }

        // System variable
        if (rest.StartsWith("SV:", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.SystemVariable;
        }

        // Log trigger
        if (rest.StartsWith("log trigger", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.LogTrigger;
        }

        // GPS event
        if (rest.StartsWith("GPS", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.GpsEvent;
        }

        // Default: if the first char of rest is a digit (channel number), it's a CAN message
        if (char.IsDigit(rest[0]))
        {
            return AscLineType.CanMessage;
        }

        return AscLineType.Unknown;
    }

    #endregion

    #region Byte overload

    /// <summary>
    /// Classifies a single raw-ASCII ASC line without any string allocation.
    /// Semantically identical to <see cref="Classify(ReadOnlySpan{char})"/>.
    /// </summary>
    /// <param name="line">A trimmed line from an ASC file as raw ASCII bytes.</param>
    /// <returns>The classified <see cref="AscLineType"/>.</returns>
    internal static AscLineType Classify(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
        {
            return AscLineType.Comment;
        }

        // Comment lines
        if (line[0] == (byte)';' || (line.Length > 1 && line[0] == (byte)'/' && line[1] == (byte)'/'))
        {
            return AscLineType.Comment;
        }

        // Header keywords
        if (StartsWithAsciiIgnoreCase(line, "date "u8)
            || StartsWithAsciiIgnoreCase(line, "base "u8)
            || _EndsWithAsciiIgnoreCase(line, "internal events logged"u8))
        {
            return AscLineType.Header;
        }

        if (StartsWithAsciiIgnoreCase(line, "Begin Triggerblock"u8))
        {
            return AscLineType.TriggerBlockBegin;
        }

        if (StartsWithAsciiIgnoreCase(line, "End TriggerBlock"u8)
            || StartsWithAsciiIgnoreCase(line, "End Triggerblock"u8))
        {
            return AscLineType.TriggerBlockEnd;
        }

        if (StartsWithAsciiIgnoreCase(line, "Start of measurement"u8))
        {
            return AscLineType.StartOfMeasurement;
        }

        // CAN FD
        if (_ContainsCanFdToken(line))
        {
            return AscLineType.CanFdMessage;
        }

        // Timestamp-prefixed lines: find the first space
        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return AscLineType.Unknown;
        }

        ReadOnlySpan<byte> tsBytes = line[..firstSpace];
        if (!_LooksLikeTimestamp(tsBytes))
        {
            return AscLineType.Unknown;
        }

        ReadOnlySpan<byte> rest = AscTokenizerBytes.TrimStartAscii(line[(firstSpace + 1)..]);

        if (rest.IsEmpty)
        {
            return AscLineType.Unknown;
        }

        if (_ContainsByteTokenIgnoreCase(rest, "ErrorFrame"u8))
        {
            return AscLineType.CanErrorFrame;
        }

        if (_ContainsByteTokenIgnoreCase(rest, "OverloadFrame"u8))
        {
            return AscLineType.CanOverloadFrame;
        }

        if (StartsWithAsciiIgnoreCase(rest, "CAN "u8) && _ContainsByteTokenIgnoreCase(rest, "Status"u8))
        {
            return AscLineType.CanStatus;
        }

        if (StartsWithAsciiIgnoreCase(rest, "Statistic:"u8))
        {
            return AscLineType.CanBusStatistics;
        }

        // LIN: L<digit>
        if (rest[0] == (byte)'L' && rest.Length > 1 && _IsAsciiDigit(rest[1]))
        {
            return _ClassifyLinEvent(rest);
        }

        // FlexRay: "Fr " or "Fr\t"
        if ((StartsWithAsciiIgnoreCase(rest, "Fr"u8))
            && rest.Length > 2
            && (rest[2] == (byte)' ' || rest[2] == (byte)'\t'))
        {
            return _ClassifyFlexRayEvent(rest);
        }

        // Ethernet
        if (StartsWithAsciiIgnoreCase(rest, "ETH "u8) || StartsWithAsciiIgnoreCase(rest, "AFDX "u8))
        {
            return AscLineType.EthernetPacket;
        }

        if (StartsWithAsciiIgnoreCase(rest, "EnvVar:"u8))
        {
            return AscLineType.EnvironmentVariable;
        }

        if (StartsWithAsciiIgnoreCase(rest, "SV:"u8))
        {
            return AscLineType.SystemVariable;
        }

        if (StartsWithAsciiIgnoreCase(rest, "log trigger"u8))
        {
            return AscLineType.LogTrigger;
        }

        if (StartsWithAsciiIgnoreCase(rest, "GPS"u8))
        {
            return AscLineType.GpsEvent;
        }

        if (_IsAsciiDigit(rest[0]))
        {
            return AscLineType.CanMessage;
        }

        return AscLineType.Unknown;
    }

    #endregion

    #region Char helpers

    private static bool _ContainsCanFdToken(ReadOnlySpan<char> line)
    {
        int idx = line.IndexOf("CANFD", StringComparison.OrdinalIgnoreCase);
        return idx >= 0;
    }

    private static AscLineType _ClassifyLinEvent(ReadOnlySpan<char> rest)
    {
        if (_ContainsToken(rest, "sleep") || _ContainsToken(rest, "wakeup"))
        {
            return AscLineType.LinEvent;
        }

        return AscLineType.LinMessage;
    }

    private static AscLineType _ClassifyFlexRayEvent(ReadOnlySpan<char> rest)
    {
        if (_ContainsToken(rest, "Cycle"))
        {
            return AscLineType.FlexRayStartCycle;
        }

        return AscLineType.FlexRayMessage;
    }

    private static bool _LooksLikeTimestamp(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        int start = span[0] == '-' ? 1 : 0;
        bool hasDigit = false;
        for (int i = start; i < span.Length; i++)
        {
            char c = span[i];
            if (_AsciiTimestampDigits.Contains(c))
            {
                hasDigit = true;
            }
            else if (c != '.')
            {
                return false;
            }
        }

        return hasDigit;
    }

    private static bool _ContainsToken(ReadOnlySpan<char> span, ReadOnlySpan<char> token)
    {
        int idx = span.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        return idx >= 0;
    }

    #endregion

    #region Byte helpers

    private static bool _ContainsCanFdToken(ReadOnlySpan<byte> line)
    {
        // Search for 'C'/'c' followed by 'A'/'a' 'N'/'n' 'F'/'f' 'D'/'d'
        ReadOnlySpan<byte> canfd = "CANFD"u8;
        return _IndexOfAsciiIgnoreCase(line, canfd) >= 0;
    }

    private static AscLineType _ClassifyLinEvent(ReadOnlySpan<byte> rest)
    {
        if (_ContainsByteTokenIgnoreCase(rest, "sleep"u8) || _ContainsByteTokenIgnoreCase(rest, "wakeup"u8))
        {
            return AscLineType.LinEvent;
        }

        return AscLineType.LinMessage;
    }

    private static AscLineType _ClassifyFlexRayEvent(ReadOnlySpan<byte> rest)
    {
        if (_ContainsByteTokenIgnoreCase(rest, "Cycle"u8))
        {
            return AscLineType.FlexRayStartCycle;
        }

        return AscLineType.FlexRayMessage;
    }

    private static bool _LooksLikeTimestamp(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        int start = span[0] == (byte)'-' ? 1 : 0;
        bool hasDigit = false;
        for (int i = start; i < span.Length; i++)
        {
            byte b = span[i];
            if (_AsciiTimestampDigitBytes.Contains(b))
            {
                hasDigit = true;
            }
            else if (b != (byte)'.')
            {
                return false;
            }
        }

        return hasDigit;
    }

    /// <summary>
    /// Case-insensitive ASCII prefix match on byte spans.
    /// Only works correctly for pure ASCII keywords.
    /// </summary>
    internal static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> prefix)
    {
        if (span.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (_ToAsciiLower(span[i]) != _ToAsciiLower(prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Case-insensitive ASCII suffix match on byte spans.
    /// </summary>
    private static bool _EndsWithAsciiIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> suffix)
    {
        if (span.Length < suffix.Length)
        {
            return false;
        }

        return StartsWithAsciiIgnoreCase(span[^suffix.Length..], suffix);
    }

    /// <summary>
    /// Returns the index of the first occurrence of <paramref name="needle"/> in
    /// <paramref name="haystack"/> using case-insensitive ASCII comparison, or -1.
    /// </summary>
    private static int _IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return -1;
        }

        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            if (StartsWithAsciiIgnoreCase(haystack[i..], needle))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks whether the byte span contains the given ASCII token (case-insensitive).
    /// This is a simple substring search — token boundary checking is not performed
    /// because ASC keyword checks are always followed by structure-level validation in parsers.
    /// </summary>
    private static bool _ContainsByteTokenIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> token)
        => _IndexOfAsciiIgnoreCase(span, token) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte _ToAsciiLower(byte b)
    {
        // 'A'=0x41 … 'Z'=0x5A → add 0x20 to lower-case
        if (b >= 0x41 && b <= 0x5A)
        {
            return (byte)(b | 0x20);
        }

        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiDigit(byte b) => (uint)(b - (byte)'0') <= 9;

    #endregion
}
