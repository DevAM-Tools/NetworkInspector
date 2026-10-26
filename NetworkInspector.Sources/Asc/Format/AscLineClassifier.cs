// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Fast, allocation-free classifier for ASC file lines.
/// Examines line prefixes to determine the bus type and event kind.
/// Provides overloads for both <c>ReadOnlySpan&lt;char&gt;</c> (string-based path) and
/// <c>ReadOnlySpan&lt;byte&gt;</c> (raw ASCII byte path, zero allocation during disk scan).
/// <para>
/// The checks run in a fixed order and the first match wins; the order is part of the behaviour (see the comments at the
/// individual checks). The classifier looks at prefixes and keywords only: it decides which parser a line goes to, and the
/// parsers validate the rest. It runs for every line of the open-time scan and again for every frame that is re-read, so
/// the byte overload, which the sources use, must stay allocation-free. The <c>char</c> overload is the reference
/// implementation kept in step with it by tests; the sources do not call it.
/// </para>
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscLineClassifier
{
    /// <summary>Digit set used to require at least one digit in a timestamp (char overload).</summary>
    private static readonly SearchValues<char> _AsciiTimestampDigits = SearchValues.Create("0123456789");

    /// <summary>Digit set used to require at least one digit in a timestamp (byte overload).</summary>
    private static readonly SearchValues<byte> _AsciiTimestampDigitBytes = SearchValues.Create("0123456789"u8);

    /// <summary>Characters a timestamp body may contain (digits and the decimal point).</summary>
    private static readonly SearchValues<char> _TimestampBodyChars = SearchValues.Create("0123456789.");

    /// <summary>Bytes a timestamp body may contain (digits and the decimal point).</summary>
    private static readonly SearchValues<byte> _TimestampBodyBytes = SearchValues.Create("0123456789."u8);

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

        // A first token that starts with a digit is the decimal channel of a classic CAN bus. These are by far the most
        // frequent lines, so they are decided first, from the second token alone (see _ClassifyChannelFirstLine). The
        // CANFD check above runs first on purpose: an FD error line is handled by AscCanFdParser, not as a classic error frame.
        if (char.IsDigit(rest[0]))
        {
            return _ClassifyChannelFirstLine(rest);
        }

        // CAN status: "CAN <ch> Status:..." with the keyword as the token after the channel (a symbolic name elsewhere in
        // the line must not turn a line into a status event).
        if (rest.StartsWith("CAN ", StringComparison.OrdinalIgnoreCase)
            && _TokenAtStartsWith(rest, 2, "Status"))
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

        // A line that starts with a digit (channel number) was decided above; nothing else is a known line type.
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
        if (!LooksLikeTimestamp(tsBytes))
        {
            return AscLineType.Unknown;
        }

        ReadOnlySpan<byte> rest = AscTokenizerBytes.TrimStartAscii(line[(firstSpace + 1)..]);

        if (rest.IsEmpty)
        {
            return AscLineType.Unknown;
        }

        // Same order and meaning as the char overload (the comments there apply): CANXL and CANFD must be the token right
        // after the timestamp, and are tested before the keyword searches so a symbolic name cannot steal the line.
        if (_NextTokenEquals(rest, "CANXL"u8))
        {
            return AscLineType.CanXlMessage;
        }

        if (_NextTokenEquals(rest, "CANFD"u8))
        {
            return AscLineType.CanFdMessage;
        }

        // A first token that starts with a digit is the decimal channel of a classic CAN bus: the most frequent lines, so
        // they are decided first and from their second token alone (no scan over the whole line, whatever its length). The
        // CANFD check above runs first on purpose: an FD error line is handled by AscCanFdParser, not as a classic error frame.
        if (_IsAsciiDigit(rest[0]))
        {
            return _ClassifyChannelFirstLine(rest);
        }

        // "CAN <ch> Status:..." with the keyword as the token after the channel; a symbolic name elsewhere in the line
        // must not turn it into a status event.
        if (StartsWithAsciiIgnoreCase(rest, "CAN "u8) && _TokenAtStartsWith(rest, 2, "Status"u8))
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

        // FlexRay: "Fr " or "Fr\t" (the separator is required so a longer word starting with "Fr" is not taken).
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

        // A line that starts with a digit (channel number) was decided above; nothing else is a known line type.
        return AscLineType.Unknown;
    }

    #endregion

    #region Char helpers

    /// <summary>True when the first token of <paramref name="rest"/> is exactly <paramref name="expected"/> (ignoring case).</summary>
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

    /// <summary>
    /// Decides a line after the timestamp whose first token is a digit, that is the decimal channel of a classic CAN bus:
    /// an <c>ErrorFrame</c> or <c>OverloadFrame</c> line, a <c>&lt;channel&gt; Statistic:</c> event, or a CAN message. The
    /// keyword must be the second token, where <c>AscCanErrorParser</c> reads it; a symbolic name that merely contains the
    /// word (<c>ErrorFrameCounter</c>) is a message, not an error frame.
    /// </summary>
    private static AscLineType _ClassifyChannelFirstLine(ReadOnlySpan<char> rest)
    {
        int end = rest.IndexOfAny(' ', '\t');
        if (end <= 0)
        {
            return AscLineType.CanMessage;
        }

        ReadOnlySpan<char> afterChannel = rest[(end + 1)..].TrimStart();
        if (_NextTokenEquals(afterChannel, "ErrorFrame"))
        {
            return AscLineType.CanErrorFrame;
        }

        if (_NextTokenEquals(afterChannel, "OverloadFrame"))
        {
            return AscLineType.CanOverloadFrame;
        }

        if (_IsChannelStatistic(rest))
        {
            return AscLineType.CanBusStatistics;
        }

        return AscLineType.CanMessage;
    }

    /// <summary>
    /// A LIN line is a sleep or wakeup event when the keyword starts the token right after the channel token or the one
    /// after that. A message has its id there (and the direction after it), so a symbolic name further along that happens to
    /// contain the word cannot turn a message into an event.
    /// </summary>
    private static AscLineType _ClassifyLinEvent(ReadOnlySpan<char> rest)
    {
        if (_TokenAtStartsWith(rest, 1, "sleep") || _TokenAtStartsWith(rest, 1, "wakeup")
            || _TokenAtStartsWith(rest, 2, "sleep") || _TokenAtStartsWith(rest, 2, "wakeup"))
        {
            return AscLineType.LinEvent;
        }

        return AscLineType.LinMessage;
    }

    /// <summary>
    /// True when the token at zero-based <paramref name="tokenIndex"/> (blank-separated) starts with <paramref name="prefix"/>,
    /// ignoring case.
    /// </summary>
    private static bool _TokenAtStartsWith(ReadOnlySpan<char> span, int tokenIndex, ReadOnlySpan<char> prefix)
    {
        ReadOnlySpan<char> remaining = span.TrimStart();
        for (int skipped = 0; skipped < tokenIndex; skipped++)
        {
            int end = remaining.IndexOfAny(' ', '\t');
            if (end < 0)
            {
                return false;
            }

            remaining = remaining[end..].TrimStart();
        }

        return remaining.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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

        // Vectorised: every character after an optional leading '-' is a digit or '.', and at least one is a digit.
        ReadOnlySpan<char> body = span[0] == '-' ? span[1..] : span;
        return body.IndexOfAnyExcept(_TimestampBodyChars) < 0
            && body.ContainsAny(_AsciiTimestampDigits);
    }

    #endregion

    #region Byte helpers

    /// <summary>True when the first token of <paramref name="rest"/> is exactly <paramref name="expected"/> (ASCII, ignoring case).</summary>
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

    /// <summary>
    /// True for <c>ETH</c> or <c>AFDX</c>, a channel token, and a packet direction. Status lines (<c>STAT</c>, <c>RxEr</c>,
    /// <c>BUS</c>) fail the direction test and stay <see cref="AscLineType.Unknown"/>.
    /// </summary>
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

    /// <summary>
    /// True when the first token is a LIN channel: <c>Li</c> (channel 1) or <c>L</c> plus one or more digits. The digits are
    /// not range-checked here; the LIN parser validates the channel.
    /// </summary>
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

    /// <summary>
    /// Decides a line after the timestamp whose first token is a digit, that is the decimal channel of a classic CAN bus:
    /// an <c>ErrorFrame</c> or <c>OverloadFrame</c> line, a <c>&lt;channel&gt; Statistic:</c> event, or a CAN message. The
    /// keyword must be the second token, where <c>AscCanErrorParser</c> reads it; a symbolic name that merely contains the
    /// word (<c>ErrorFrameCounter</c>) is a message, not an error frame. Reads at most the first two tokens, so the cost
    /// does not grow with the line length.
    /// </summary>
    private static AscLineType _ClassifyChannelFirstLine(ReadOnlySpan<byte> rest)
    {
        int end = rest.IndexOfAny(" \t"u8);
        if (end <= 0)
        {
            return AscLineType.CanMessage;
        }

        ReadOnlySpan<byte> afterChannel = AscTokenizerBytes.TrimStartAscii(rest[(end + 1)..]);
        if (_NextTokenEquals(afterChannel, "ErrorFrame"u8))
        {
            return AscLineType.CanErrorFrame;
        }

        if (_NextTokenEquals(afterChannel, "OverloadFrame"u8))
        {
            return AscLineType.CanOverloadFrame;
        }

        if (_IsChannelStatistic(rest))
        {
            return AscLineType.CanBusStatistics;
        }

        return AscLineType.CanMessage;
    }

    /// <summary>
    /// A LIN line is a sleep or wakeup event when the keyword starts the token right after the channel token or the one
    /// after that. A message has its id there (and the direction after it), so a symbolic name further along that happens to
    /// contain the word cannot turn a message into an event.
    /// </summary>
    private static AscLineType _ClassifyLinEvent(ReadOnlySpan<byte> rest)
    {
        if (_TokenAtStartsWith(rest, 1, "sleep"u8) || _TokenAtStartsWith(rest, 1, "wakeup"u8)
            || _TokenAtStartsWith(rest, 2, "sleep"u8) || _TokenAtStartsWith(rest, 2, "wakeup"u8))
        {
            return AscLineType.LinEvent;
        }

        return AscLineType.LinMessage;
    }

    /// <summary>
    /// True when the token at zero-based <paramref name="tokenIndex"/> (blank-separated) starts with <paramref name="prefix"/>,
    /// ignoring ASCII case.
    /// </summary>
    private static bool _TokenAtStartsWith(ReadOnlySpan<byte> span, int tokenIndex, ReadOnlySpan<byte> prefix)
    {
        ReadOnlySpan<byte> remaining = AscTokenizerBytes.TrimStartAscii(span);
        for (int skipped = 0; skipped < tokenIndex; skipped++)
        {
            int end = remaining.IndexOfAny(" \t"u8);
            if (end < 0)
            {
                return false;
            }

            remaining = AscTokenizerBytes.TrimStartAscii(remaining[end..]);
        }

        return StartsWithAsciiIgnoreCase(remaining, prefix);
    }

    /// <summary>
    /// Decides whether an <c>Fr</c> line is a frame: <c>RMSG</c> and <c>PDU</c> are; the cycle and symbol events
    /// (<c>SCE</c>, <c>SE</c>, <c>EE</c>, <c>StartCycleEvent</c>) are not; an all-digit token after <c>Fr</c> is the channel
    /// of the old format, which is a frame. Any other token is <see cref="AscLineType.Unknown"/>.
    /// </summary>
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

    /// <summary>
    /// True when <paramref name="span"/> has the shape of an ASC timestamp: an optional leading '-', then only digits
    /// and '.', with at least one digit. Shared by the line classifier and the Ethernet continuation check.
    /// </summary>
    internal static bool LooksLikeTimestamp(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        // Vectorised: every byte after an optional leading '-' is a digit or '.', and at least one is a digit.
        ReadOnlySpan<byte> body = span[0] == (byte)'-' ? span[1..] : span;
        return body.IndexOfAnyExcept(_TimestampBodyBytes) < 0
            && body.ContainsAny(_AsciiTimestampDigitBytes);
    }

    /// <summary>
    /// Case-insensitive ASCII prefix match on byte spans.
    /// Only works correctly for pure ASCII keywords. This is a prefix test, not equality: callers that need a whole token
    /// compare the length as well (see the <c>_TokenEquals</c> helpers of the parsers).
    /// Only ASCII letters are folded (<c>_ToAsciiLower</c>), so the result never depends on the host culture.
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
