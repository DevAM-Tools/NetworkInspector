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

        // CANXL and CANFD are the token after the timestamp. A later symbolic name must not steal the line.
        if (_NextTokenEquals(rest, "CANXL"))
        {
            return AscLineType.CanXlMessage;
        }

        if (_NextTokenEquals(rest, "CANFD"))
        {
            return AscLineType.CanFdMessage;
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

        // LIN channel 1 is "Li". Channels 2..255 are "L" plus digits. "L*" is not a channel.
        if (_IsLinChannel(rest))
        {
            return _ClassifyLinEvent(rest);
        }

        // FlexRay — "Fr" prefix
        if (rest.StartsWith("Fr ", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("Fr\t", StringComparison.OrdinalIgnoreCase))
        {
            return _ClassifyFlexRayEvent(rest);
        }

        // Ethernet or AFDX packet: bus, channel, then Rx / Tx / TxRq / TxFwd.
        // STAT, RxEr, and AFDX BUS stay Unknown.
        if (_IsEthernetPacket(rest))
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

        // "<channel> Statistic:" is a bus-statistics event, not a CAN frame.
        if (_IsChannelStatistic(rest))
        {
            return AscLineType.CanBusStatistics;
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

        if (_NextTokenEquals(rest, "CANXL"u8))
        {
            return AscLineType.CanXlMessage;
        }

        if (_NextTokenEquals(rest, "CANFD"u8))
        {
            return AscLineType.CanFdMessage;
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

        // LIN channel 1 is "Li". Channels 2..255 are "L" plus digits. "L*" is not a channel.
        if (_IsLinChannel(rest))
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

        if (_IsEthernetPacket(rest))
        {
            return AscLineType.EthernetPacket;
        }

        if (_IsChannelStatistic(rest))
        {
            return AscLineType.CanBusStatistics;
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

    private static bool _NextTokenEquals(ReadOnlySpan<char> rest, string expected)
    {
        ReadOnlySpan<char> trimmed = rest.TrimStart();
        if (!trimmed.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length == expected.Length)
        {
            return true;
        }

        char next = trimmed[expected.Length];
        return next == ' ' || next == '\t';
    }

    private static bool _IsEthernetPacket(ReadOnlySpan<char> rest)
    {
        AscTokenizer tokenizer = new(rest);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> bus)
            || !_IsEthernetBus(bus))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> direction))
        {
            return false;
        }

        return _IsEthernetDirection(direction);
    }

    private static bool _IsEthernetBus(ReadOnlySpan<char> bus) =>
        (bus.Length == 3 && bus.Equals("ETH", StringComparison.OrdinalIgnoreCase))
        || (bus.Length == 4 && bus.Equals("AFDX", StringComparison.OrdinalIgnoreCase));

    private static bool _IsEthernetDirection(ReadOnlySpan<char> direction) =>
        direction.Equals("Rx", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("Tx", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("TxRq", StringComparison.OrdinalIgnoreCase)
        || direction.Equals("TxFwd", StringComparison.OrdinalIgnoreCase);

    private static bool _IsLinChannel(ReadOnlySpan<char> rest)
    {
        if (rest.Length < 2 || (rest[0] != 'L' && rest[0] != 'l'))
        {
            return false;
        }

        int end = rest.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> token = end < 0 ? rest : rest[..end];
        if (token.Length == 2 && (token[1] == 'i' || token[1] == 'I'))
        {
            return true;
        }

        if (token.Length < 2)
        {
            return false;
        }

        for (int i = 1; i < token.Length; i++)
        {
            if (!char.IsDigit(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Statistics are <c>&lt;channel&gt; Statistic:</c>. The channel is decimal and comes first.
    /// </summary>
    private static bool _IsChannelStatistic(ReadOnlySpan<char> rest)
    {
        int end = rest.IndexOfAny(' ', '\t');
        if (end <= 0)
        {
            return false;
        }

        for (int i = 0; i < end; i++)
        {
            if (!char.IsDigit(rest[i]))
            {
                return false;
            }
        }

        ReadOnlySpan<char> after = rest[(end + 1)..].TrimStart();
        return after.StartsWith("Statistic:", StringComparison.OrdinalIgnoreCase);
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
        AscTokenizer tokenizer = new(rest);
        if (!tokenizer.TryNextToken(out _)
            || !tokenizer.TryNextToken(out ReadOnlySpan<char> kind))
        {
            return AscLineType.Unknown;
        }

        // RMSG and PDU are frames. A numeric token is the old V9 channel.
        // SCE, SE, EE, and StartCycleEvent are not frames.
        if (kind.Equals("RMSG", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("PDU", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.FlexRayMessage;
        }

        if (kind.Equals("SCE", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("SE", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("EE", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("StartCycleEvent", StringComparison.OrdinalIgnoreCase))
        {
            return AscLineType.Unknown;
        }

        for (int i = 0; i < kind.Length; i++)
        {
            if (!char.IsDigit(kind[i]))
            {
                return AscLineType.Unknown;
            }
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

    private static bool _NextTokenEquals(ReadOnlySpan<byte> rest, ReadOnlySpan<byte> expected)
    {
        ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimStartAscii(rest);
        if (trimmed.Length < expected.Length
            || !StartsWithAsciiIgnoreCase(trimmed, expected))
        {
            return false;
        }

        if (trimmed.Length == expected.Length)
        {
            return true;
        }

        byte next = trimmed[expected.Length];
        return next == (byte)' ' || next == (byte)'\t';
    }

    private static bool _IsEthernetPacket(ReadOnlySpan<byte> rest)
    {
        AscTokenizerBytes tokenizer = new(rest);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> bus)
            || !_IsEthernetBus(bus))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction))
        {
            return false;
        }

        return _IsEthernetDirection(direction);
    }

    private static bool _IsEthernetBus(ReadOnlySpan<byte> bus) =>
        (bus.Length == 3 && StartsWithAsciiIgnoreCase(bus, "ETH"u8))
        || (bus.Length == 4 && StartsWithAsciiIgnoreCase(bus, "AFDX"u8));

    private static bool _IsEthernetDirection(ReadOnlySpan<byte> direction) =>
        (direction.Length == 2 && StartsWithAsciiIgnoreCase(direction, "Rx"u8))
        || (direction.Length == 2 && StartsWithAsciiIgnoreCase(direction, "Tx"u8))
        || (direction.Length == 4 && StartsWithAsciiIgnoreCase(direction, "TxRq"u8))
        || (direction.Length == 5 && StartsWithAsciiIgnoreCase(direction, "TxFwd"u8));

    private static bool _IsLinChannel(ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 2 || (rest[0] != (byte)'L' && rest[0] != (byte)'l'))
        {
            return false;
        }

        int end = rest.IndexOfAny(" \t"u8);
        ReadOnlySpan<byte> token = end < 0 ? rest : rest[..end];
        if (token.Length == 2 && (token[1] == (byte)'i' || token[1] == (byte)'I'))
        {
            return true;
        }

        if (token.Length < 2)
        {
            return false;
        }

        for (int i = 1; i < token.Length; i++)
        {
            if (!_IsAsciiDigit(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool _IsChannelStatistic(ReadOnlySpan<byte> rest)
    {
        int end = rest.IndexOfAny(" \t"u8);
        if (end <= 0)
        {
            return false;
        }

        for (int i = 0; i < end; i++)
        {
            if (!_IsAsciiDigit(rest[i]))
            {
                return false;
            }
        }

        ReadOnlySpan<byte> after = AscTokenizerBytes.TrimStartAscii(rest[(end + 1)..]);
        return StartsWithAsciiIgnoreCase(after, "Statistic:"u8);
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
        AscTokenizerBytes tokenizer = new(rest);
        if (!tokenizer.TryNextToken(out _)
            || !tokenizer.TryNextToken(out ReadOnlySpan<byte> kind))
        {
            return AscLineType.Unknown;
        }

        if (_TokenEquals(kind, "RMSG"u8) || _TokenEquals(kind, "PDU"u8))
        {
            return AscLineType.FlexRayMessage;
        }

        if (_TokenEquals(kind, "SCE"u8)
            || _TokenEquals(kind, "SE"u8)
            || _TokenEquals(kind, "EE"u8)
            || _TokenEquals(kind, "StartCycleEvent"u8))
        {
            return AscLineType.Unknown;
        }

        for (int i = 0; i < kind.Length; i++)
        {
            if (!_IsAsciiDigit(kind[i]))
            {
                return AscLineType.Unknown;
            }
        }

        return AscLineType.FlexRayMessage;
    }

    private static bool _TokenEquals(ReadOnlySpan<byte> token, ReadOnlySpan<byte> expected) =>
        token.Length == expected.Length && StartsWithAsciiIgnoreCase(token, expected);

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
