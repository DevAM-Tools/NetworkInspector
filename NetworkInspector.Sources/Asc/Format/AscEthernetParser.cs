// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses a Vector Ethernet ASC 1.3.1 section 4.2 packet.
/// The line is <c>&lt;time&gt; ETH &lt;channel&gt; &lt;dir&gt; &lt;DataLen&gt;:&lt;hex&gt;</c>.
/// <c>TxFwd</c> is a forwarded packet and uses the same payload.
/// An AFDX data line is <c>&lt;time&gt; AFDX &lt;channel&gt; &lt;dir&gt; &lt;afdxChannel&gt; &lt;flags&gt; &lt;bag&gt; &lt;DataLen&gt;:&lt;hex&gt;</c>.
/// The three fields after the direction are not part of the Ethernet bytes.
/// <c>DataLen</c> uses the file numeric base. Data bytes are always hex, start at the
/// destination MAC, and do not include the Ethernet CRC. A short hex tail continues
/// on the following physical lines until the byte count matches.
/// <c>ETH STAT</c>, <c>ETH RxEr</c>, <c>AFDX STAT</c>, and <c>AFDX BUS</c> are not packets.
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscEthernetParser
{
    #region Constants

    /// <summary>Largest <c>DataLen</c> the spec allows. Allocation never exceeds this.</summary>
    internal const int MaxDataLength = 1518;

    /// <summary>
    /// Bytes a caller should present from the start of the packet line so a wrapped
    /// 1518-byte payload still fits, including one-byte-per-line wrapping.
    /// </summary>
    internal const int MaxRecordBytes = 16384;

    #endregion

    #region Public API

    /// <summary>
    /// Parses one Ethernet packet. <paramref name="record"/> may contain newline-joined
    /// continuation lines. Bytes after the packet, up to the next logging event, are not copied.
    /// </summary>
    /// <param name="record">Packet line plus any continuation text already joined with newlines.</param>
    /// <param name="numericBase">16 when the header says <c>base hex</c>, 10 when it says <c>base dec</c>.</param>
    /// <param name="timestamp">Line timestamp. Not the file clock.</param>
    /// <param name="channel">Decimal channel in <c>1..255</c>.</param>
    /// <param name="frame">Exactly <c>DataLen</c> bytes. Empty when this method returns <c>false</c>, or when <c>DataLen</c> is 0.</param>
    /// <returns><c>false</c> when the line is not a section 4.2 packet or the hex count does not match.</returns>
    internal static bool TryParse(
        ReadOnlySpan<byte> record,
        int numericBase,
        out AscTimestamp timestamp,
        out int channel,
        out byte[] frame)
    {
        timestamp = default;
        channel = 0;
        frame = [];

        AscTokenizerBytes tokenizer = new(record);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> timeToken)
            || !AscTimestamp.TryParse(timeToken, out timestamp))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> busToken))
        {
            return false;
        }

        bool afdx = busToken.Length == 4
            && AscLineClassifier.StartsWithAsciiIgnoreCase(busToken, "AFDX"u8);
        bool eth = busToken.Length == 3
            && AscLineClassifier.StartsWithAsciiIgnoreCase(busToken, "ETH"u8);
        if (!afdx && !eth)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> channelToken)
            || !AscCanParser.TryParseChannel(channelToken, out channel)
            || channel < 1
            || channel > 255)
        {
            return false;
        }

        // Rx, Tx, TxRq, and TxFwd are accepted and discarded. STAT / RxEr / BUS are not packets.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction)
            || !_IsPacketDirection(direction))
        {
            return false;
        }

        // AFDX inserts virtual-link channel, flags, and BAG before the Ethernet length.
        if (afdx)
        {
            for (int skipped = 0; skipped < 3; skipped++)
            {
                if (!tokenizer.TryNextToken(out _))
                {
                    return false;
                }
            }
        }

        ReadOnlySpan<byte> rest = tokenizer.Remaining;
        if (!_TryParseDataLength(rest, numericBase, out int dataLen, out int hexStart))
        {
            return false;
        }

        if (dataLen == 0)
        {
            return !_HasExtraHex(rest[hexStart..]);
        }

        frame = new byte[dataLen];
        if (!_TryCopyHex(rest[hexStart..], frame))
        {
            frame = [];
            return false;
        }

        return true;
    }

    /// <summary>
    /// A continuation line has no timestamp-plus-token prefix. Logging events stop the hex join.
    /// </summary>
    internal static bool IsNewLoggingEvent(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(line);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        int space = _IndexOfSpaceOrTab(trimmed);
        if (space <= 0)
        {
            return false;
        }

        if (!_LooksLikeTimestamp(trimmed[..space]))
        {
            return false;
        }

        return !AscTokenizerBytes.TrimStartAscii(trimmed[(space + 1)..]).IsEmpty;
    }

    #endregion

    #region Hex scan

    private static bool _TryParseDataLength(
        ReadOnlySpan<byte> rest,
        int numericBase,
        out int dataLen,
        out int hexStart)
    {
        dataLen = 0;
        hexStart = 0;
        int colon = rest.IndexOf((byte)':');
        if (colon < 0)
        {
            return false;
        }

        int lineBreak = rest.IndexOf((byte)'\n');
        if (lineBreak >= 0 && colon > lineBreak)
        {
            return false;
        }

        int digitStart = colon;
        while (digitStart > 0 && _IsLengthDigit(rest[digitStart - 1], numericBase))
        {
            digitStart--;
        }

        int digitCount = colon - digitStart;
        if (digitCount < 1 || digitCount > 4)
        {
            return false;
        }

        if (digitStart > 0)
        {
            byte before = rest[digitStart - 1];
            if (before != (byte)' ' && before != (byte)'\t')
            {
                return false;
            }
        }

        ReadOnlySpan<byte> digits = rest.Slice(digitStart, digitCount);
        if (numericBase == 16)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(digits, out uint hex, out int consumed, 'X')
                || consumed != digits.Length
                || hex > (uint)MaxDataLength)
            {
                return false;
            }

            dataLen = (int)hex;
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(digits, out int dec, out int consumed)
                || consumed != digits.Length
                || dec < 0
                || dec > MaxDataLength)
            {
                return false;
            }

            dataLen = dec;
        }

        hexStart = colon + 1;
        return true;
    }

    private static bool _TryCopyHex(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        int index = 0;
        bool atLineStart = false;
        for (int written = 0; written < destination.Length; written++)
        {
            if (!_MoveToHexDigit(text, ref index, ref atLineStart, out bool atEvent))
            {
                return false;
            }

            if (atEvent)
            {
                return false;
            }

            int high = _HexValue(text[index]);
            index++;
            if (index >= text.Length)
            {
                return false;
            }

            int low = _HexValue(text[index]);
            if (low < 0)
            {
                return false;
            }

            index++;
            destination[written] = (byte)((high << 4) | low);
        }

        return !_HasExtraHex(text[index..]);
    }

    private static bool _HasExtraHex(ReadOnlySpan<byte> text)
    {
        int index = 0;
        bool atLineStart = false;
        if (!_MoveToHexDigit(text, ref index, ref atLineStart, out bool atEvent))
        {
            return false;
        }

        return !atEvent;
    }

    /// <summary>
    /// Advances to the next hex digit. Whitespace and continuation lines are skipped.
    /// A new logging event stops the scan without consuming it.
    /// </summary>
    private static bool _MoveToHexDigit(
        ReadOnlySpan<byte> text,
        ref int index,
        ref bool atLineStart,
        out bool atEvent)
    {
        atEvent = false;
        while (index < text.Length)
        {
            byte current = text[index];
            if (current == (byte)' ' || current == (byte)'\t' || current == (byte)'\r')
            {
                index++;
                continue;
            }

            if (current == (byte)'\n')
            {
                atLineStart = true;
                index++;
                continue;
            }

            if (atLineStart)
            {
                int lineEnd = text[index..].IndexOf((byte)'\n');
                ReadOnlySpan<byte> line = lineEnd < 0 ? text[index..] : text.Slice(index, lineEnd);
                // A logging event, or any line that is not a hex continuation (End TriggerBlock,
                // comments), ends the packet. Only hex lines keep supplying payload bytes.
                if (IsNewLoggingEvent(line) || !IsHexContinuation(line))
                {
                    atEvent = true;
                    return true;
                }

                atLineStart = false;
            }

            if (_HexValue(current) >= 0)
            {
                return true;
            }

            return false;
        }

        return false;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A wrapped payload line is only hexadecimal digits and ASCII whitespace.
    /// </summary>
    internal static bool IsHexContinuation(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(line);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        bool anyHex = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            byte current = trimmed[i];
            if (current == (byte)' ' || current == (byte)'\t')
            {
                continue;
            }

            if (_HexValue(current) < 0)
            {
                return false;
            }

            anyHex = true;
        }

        return anyHex;
    }

    private static bool _IsPacketDirection(ReadOnlySpan<byte> token) =>
        _TokenEquals(token, "Rx"u8)
        || _TokenEquals(token, "Tx"u8)
        || _TokenEquals(token, "TxRq"u8)
        || _TokenEquals(token, "TxFwd"u8);

    private static bool _TokenEquals(ReadOnlySpan<byte> token, ReadOnlySpan<byte> ascii) =>
        token.Length == ascii.Length
        && AscLineClassifier.StartsWithAsciiIgnoreCase(token, ascii);

    private static bool _IsLengthDigit(byte value, int numericBase)
    {
        if (_IsDigit(value))
        {
            return true;
        }

        if (numericBase != 16)
        {
            return false;
        }

        return (value >= (byte)'A' && value <= (byte)'F')
            || (value >= (byte)'a' && value <= (byte)'f');
    }

    private static int _HexValue(byte value)
    {
        if (_IsDigit(value))
        {
            return value - (byte)'0';
        }

        if (value >= (byte)'A' && value <= (byte)'F')
        {
            return value - (byte)'A' + 10;
        }

        if (value >= (byte)'a' && value <= (byte)'f')
        {
            return value - (byte)'a' + 10;
        }

        return -1;
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
            byte current = span[i];
            if (_IsDigit(current))
            {
                hasDigit = true;
            }
            else if (current != (byte)'.')
            {
                return false;
            }
        }

        return hasDigit;
    }

    private static int _IndexOfSpaceOrTab(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)' ' || span[i] == (byte)'\t')
            {
                return i;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsDigit(byte value) => (uint)(value - (byte)'0') <= 9;

    #endregion
}
