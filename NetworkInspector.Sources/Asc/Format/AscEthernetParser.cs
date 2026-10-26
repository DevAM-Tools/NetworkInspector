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

    /// <summary>
    /// Bytes a hex continuation line may contain: hex digits, space, tab. A <see cref="SearchValues{T}"/> set, built once,
    /// so <c>IndexOfAnyExcept</c> checks a whole line with vectorized code instead of a byte loop (run for each line
    /// that follows a packet line).
    /// </summary>
    private static readonly SearchValues<byte> _HexOrBlankBytes = SearchValues.Create("0123456789abcdefABCDEF \t"u8);

    /// <summary>Hex digits; a continuation line needs at least one.</summary>
    private static readonly SearchValues<byte> _HexDigitBytes = SearchValues.Create("0123456789abcdefABCDEF"u8);

    /// <summary>
    /// Largest <c>DataLen</c> the Vector Ethernet ASC specification allows. Allocation never exceeds this. Constant of the
    /// format (not host-dependent); a larger declared length is a parse failure, not truncated.
    /// </summary>
    internal const int MaxDataLength = 1518;

    /// <summary>
    /// Bytes a caller should present from the start of the packet line so a wrapped
    /// 1518-byte payload still fits, including one-byte-per-line wrapping (two hex digits, CR, and LF per line are about
    /// 6 KB for 1518 bytes; blanks between digits add more, and 16 KiB leaves room for that). This also caps the work and
    /// memory one packet can cost: a continuation text that does not end the packet within this many bytes is cut off, so a
    /// file cannot make one record grow without bound. Constant, not configurable: derived from the format limit above.
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
        => FrameSlabAllocator.ToArray(
            TryParse(record, numericBase, null, out timestamp, out channel, out ReadOnlyMemory<byte> memory), memory, out frame);

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, int, out AscTimestamp, out int, out byte[])"/>
    /// <param name="record">Packet line plus any continuation text already joined with newlines.</param>
    /// <param name="numericBase">16 when the header says <c>base hex</c>, 10 when it says <c>base dec</c>.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="timestamp">Line timestamp. Not the file clock.</param>
    /// <param name="channel">Decimal channel in <c>1..255</c>.</param>
    /// <param name="frame">Exactly <c>DataLen</c> bytes. Empty on failure or when <c>DataLen</c> is 0.</param>
    internal static bool TryParse(
        ReadOnlySpan<byte> record,
        int numericBase,
        FrameSlabAllocator? slabs,
        out AscTimestamp timestamp,
        out int channel,
        out ReadOnlyMemory<byte> frame)
    {
        frame = default;
        if (!_TryParseHeader(record, numericBase, out timestamp, out channel, out ReadOnlySpan<byte> hexText, out int dataLen))
        {
            return false;
        }

        // DataLen 0 is an empty frame: legal only when no hex follows (before the next logging event).
        if (dataLen == 0)
        {
            return !_HasExtraHex(hexText);
        }

        // The hex is decoded into a stack scratch (at most MaxDataLength = 1518 bytes, so the stack cost is bounded) and
        // verified first; only a payload that passed is copied into slab or array space. Renting before the check left an
        // unused region behind in the slab for every malformed packet, so a file of broken packets could burn slab memory
        // with no frame to show for it (the CAN, CAN FD and LIN parsers rent after validation as well).
        Span<byte> scratch = stackalloc byte[MaxDataLength];
        Span<byte> decoded = scratch[..dataLen];
        if (!_TryCopyHex(hexText, decoded))
        {
            return false;
        }

        // The decoded bytes overwrite every byte of the rented region, so the zeroing in RentZeroed is not needed here.
        Memory<byte> buffer = slabs is null
            ? new byte[dataLen]
            : slabs.Allocate(dataLen);
        decoded.CopyTo(buffer.Span);
        frame = buffer;
        return true;
    }

    /// <summary>
    /// True when the packet header — time, bus, channel, direction (AFDX: three more tokens), and <c>DataLen:</c> —
    /// parses from <paramref name="line"/> alone. Then the header tokens of the full record are the same tokens and
    /// the hex scan starts on this line, so the record ends at the first line for which <see cref="EndsPacket"/> is
    /// true. When it is false, a malformed header might take a token across a line break, and a reader must present
    /// the full <see cref="MaxRecordBytes"/> window to see what the parser sees.
    /// </summary>
    /// <param name="line">The packet's first physical line without its line terminator.</param>
    /// <param name="numericBase">16 when the header says <c>base hex</c>, 10 when it says <c>base dec</c>.</param>
    internal static bool HeaderFitsLine(ReadOnlySpan<byte> line, int numericBase) =>
        _TryParseHeader(line, numericBase, out _, out _, out _, out _);

    /// <summary>
    /// Whether the hex scan of a packet stops at <paramref name="physicalLine"/> (a following line, without its LF):
    /// null for a blank line (spaces, tabs, CR only), which the scan skips; true for a logging event or any line that
    /// is not a hex continuation; false for a hex continuation line. The same rule <c>_MoveToHexDigit</c> applies.
    /// </summary>
    internal static bool? EndsPacket(ReadOnlySpan<byte> physicalLine)
    {
        // The scan skips spaces, tabs, and CR before it looks at a line, and then checks the rest up to the LF.
        int start = physicalLine.IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r');
        if (start < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> line = physicalLine[start..];
        return IsNewLoggingEvent(line) || !IsHexContinuation(line);
    }

    /// <summary>
    /// A continuation line has no timestamp-plus-token prefix. Logging events stop the hex join. A line is a logging
    /// event when its first blank-delimited token is a timestamp and at least one more token follows. A timestamp has a
    /// decimal point (<c>0.039255</c>): a hex payload group such as <c>12</c> or <c>0042</c> is all decimal digits too, so
    /// digits alone must not count, or a wrapped payload line written with blanks (<c>12 34 56</c>) would end the packet
    /// early and cut the frame. A genuine event with an integer first token always carries a word that is not hex
    /// (the bus or event name), so <see cref="IsHexContinuation"/> still ends the packet on it.
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

        // The decimal point separates a timestamp from a payload group (see the summary): digits and dots alone are not enough.
        ReadOnlySpan<byte> firstToken = trimmed[..space];
        if (!firstToken.Contains((byte)'.') || !AscLineClassifier.LooksLikeTimestamp(firstToken))
        {
            return false;
        }

        return !AscTokenizerBytes.TrimStartAscii(trimmed[(space + 1)..]).IsEmpty;
    }

    #endregion

    #region Hex scan

    /// <summary>
    /// Parses the packet header of <paramref name="record"/>: time, bus (<c>ETH</c> or <c>AFDX</c>), channel 1..255, a
    /// packet direction, the three AFDX fields, and <c>DataLen:</c>. <paramref name="hexText"/> is the text after the colon.
    /// </summary>
    private static bool _TryParseHeader(
        ReadOnlySpan<byte> record,
        int numericBase,
        out AscTimestamp timestamp,
        out int channel,
        out ReadOnlySpan<byte> hexText,
        out int dataLen)
    {
        timestamp = default;
        channel = 0;
        hexText = default;
        dataLen = 0;

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
        if (!_TryParseDataLength(rest, numericBase, out dataLen, out int hexStart))
        {
            return false;
        }

        hexText = rest[hexStart..];
        return true;
    }

    /// <summary>
    /// Finds <c>DataLen:</c> in the text after the header tokens. The colon must lie on the first line, the digits before
    /// it (at most four: 1518 is four decimal digits and three hex digits) must start after a blank or at the start, and
    /// the value must not exceed <see cref="MaxDataLength"/>. <paramref name="hexStart"/> is the index after the colon.
    /// </summary>
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

    /// <summary>
    /// Fills <paramref name="destination"/> from the hex text, two digits per byte. The two digits of one byte must be
    /// adjacent (a line break between them fails the parse); blanks and wrapped lines are allowed between bytes. The
    /// hex count must match exactly: too few bytes fail, and further hex digits after the last byte fail too (unless a new
    /// logging event follows).
    /// </summary>
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

    /// <summary>
    /// True when another hex digit follows in <paramref name="text"/> before its end or the next logging event, i.e. the
    /// packet carries more data than its DataLen says. A non-hex character stops the scan and does not count.
    /// </summary>
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
    /// <param name="text">The hex text of the record (the part after <c>DataLen:</c>), lines separated by LF.</param>
    /// <param name="index">Scan position; on <c>true</c> without <paramref name="atEvent"/> it points at the hex digit.</param>
    /// <param name="atLineStart">True after an LF until the first non-blank character of that line has been classified.</param>
    /// <param name="atEvent">True when the scan stopped at a line that ends the packet.</param>
    /// <returns>
    /// <c>true</c> when a hex digit was found or the packet ended (<paramref name="atEvent"/>); <c>false</c> at the end of
    /// the text or at a character that is not a hex digit.
    /// </returns>
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
        // Vectorised: only hex digits and blanks, and at least one hex digit. Trimming leaves a non-blank first byte.
        ReadOnlySpan<byte> trimmed = AscTokenizerBytes.TrimAscii(line);
        return !trimmed.IsEmpty
            && trimmed.IndexOfAnyExcept(_HexOrBlankBytes) < 0
            && trimmed.ContainsAny(_HexDigitBytes);
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

    private static int _IndexOfSpaceOrTab(ReadOnlySpan<byte> span) =>
        span.IndexOfAny((byte)' ', (byte)'\t');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsDigit(byte value) => (uint)(value - (byte)'0') <= 9;

    #endregion
}
