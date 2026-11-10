// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC FlexRay message line into a LINKTYPE_FLEXRAY binary frame.
///
/// Old ASC FlexRay line (CANoe 5.1 and earlier):
///   &lt;time&gt; Fr &lt;channel&gt; V9 &lt;id&gt; &lt;cycle&gt; &lt;nm&gt; &lt;sync&gt; &lt;header_crc&gt; &lt;name&gt; &lt;byte_count&gt; &lt;data...&gt;
/// New ASC FlexRay line (since CANoe 5.2):
///   &lt;time&gt; Fr RMSG|PDU &lt;cluster&gt; &lt;client&gt; &lt;channel&gt; &lt;mask&gt; &lt;slot&gt; &lt;cycle&gt; &lt;dir&gt; ... &lt;crc&gt;
///   &lt;name&gt; &lt;payloadLen&gt; &lt;bufferLen&gt; &lt;data...&gt;
/// Numeric fields follow the file base. NM and Sync are skipped. Mask 3 (both channels) is stored as bus channel B
/// because LINKTYPE_FLEXRAY has one channel bit.
///
/// LINKTYPE_FLEXRAY frame layout (7-byte header + data):
///   Measurement header + error flags + ISO 17458-2 frame header + payload.
/// The frame itself is built by <c>FlexRayLinkTypeFrame.BuildFrame</c>, which pads the payload with a zero byte to an even
/// length (FlexRay payloads are whole 16-bit words) and throws for a slot above 2047 or a cycle above 63; the parser checks
/// both limits first, so such a line is rejected (the method returns <c>false</c>) and never throws.
/// Unlike the CAN, LIN and Ethernet parsers there is no slab overload: every FlexRay frame is its own array.
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscFlexRayParser
{
    #region Constants

    /// <summary>
    /// Largest slot the frame builder accepts (the ISO frame ID is 11 bits). A fact of the format, so a constant. A line
    /// with a larger slot is rejected here: <c>FlexRayLinkTypeFrame.BuildFrame</c> throws for it, and a hostile ASC line
    /// must end as a counted parse failure, never as an exception out of a <c>Try</c> parser.
    /// </summary>
    private const int _MaxSlot = (int)FlexRayLinkTypeFrame.FrameIdKeyMask;

    #endregion

    #region Public API

    /// <summary>
    /// Parses an ASC FlexRay line from raw ASCII bytes.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec — from the file header; applies to every numeric column.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">
    /// Old format: the channel after <c>Fr</c> (1 = A, 2 = B). New format: the application channel (interface id), which
    /// is not the same as the bus channel stored in the frame.
    /// </param>
    /// <param name="frame">The LINKTYPE_FLEXRAY frame. Empty when this method returns <c>false</c>.</param>
    /// <returns><c>true</c> if parsing succeeded.</returns>
    internal static bool TryParse(
        ReadOnlySpan<byte> line, int numericBase,
        out AscTimestamp timestamp, out int channel, out byte[] frame)
    {
        timestamp = default;
        channel = 0;
        frame = [];

        AscTokenizerBytes tokenizer = new(line);

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> tsToken)
            || !AscTimestamp.TryParse(tsToken, out timestamp))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> frToken)
            || !AscLineClassifier.StartsWithAsciiIgnoreCase(frToken, "Fr"u8) || frToken.Length != 2)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> kindOrChannel))
        {
            return false;
        }

        // The token after "Fr" tells the generation of the format: RMSG or PDU is the new layout, a channel number the old one.
        bool isHex = numericBase == 16;
        if (_TokenIs(kindOrChannel, "RMSG"u8) || _TokenIs(kindOrChannel, "PDU"u8))
        {
            return _TryParseNewFlexRay(ref tokenizer, isHex, out channel, out frame);
        }

        if (!AscCanParser.TryParseChannel(kindOrChannel, out channel))
        {
            return false;
        }

        return _TryParseOldFlexRay(ref tokenizer, isHex, channel, out frame);
    }

    /// <summary>
    /// Old format: version, id, cycle, NM, sync, header CRC, name, byte count, data.
    /// A byte count above 254 is rejected so the line cannot force a large allocation.
    /// </summary>
    private static bool _TryParseOldFlexRay(
        ref AscTokenizerBytes tokenizer,
        bool isHex,
        int channel,
        out byte[] frame)
    {
        frame = [];

        // Version string (for example "V9").
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int frameIdValue)
            || frameIdValue < 0
            || frameIdValue > _MaxSlot)
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int cycleInt) || cycleInt < 0 || cycleInt > 63)
        {
            return false;
        }

        // NM and Sync are not carried by LINKTYPE_FLEXRAY.
        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int crcValue)
            || crcValue < 0
            || crcValue > ushort.MaxValue)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int dataLen)
            || dataLen < 0
            || dataLen > FlexRayLinkTypeFrame.MaxPayloadBytes)
        {
            return false;
        }

        // dataLen is at most MaxPayloadBytes (254), so the rental is small and bounded. The pooled scratch avoids a
        // throwaway array per line; BuildFrame copies the payload into the frame, so the rental is returned right after.
        // Fewer data tokens than the declared count give a shorter payload (parsedCount), not zero-filled bytes.
        byte[] dataBytes = ArrayPool<byte>.Shared.Rent(dataLen);
        try
        {
            int parsedCount = _ParseByteDataTokens(ref tokenizer, dataLen, isHex, dataBytes);
            ReadOnlySpan<byte> payloadSpan = parsedCount > 0
                ? dataBytes.AsSpan(0, parsedCount)
                : ReadOnlySpan<byte>.Empty;
            frame = FlexRayLinkTypeFrame.BuildFrame(
                FlexRayLinkTypeFrame.AscChannelToBusChannel(channel),
                (ushort)frameIdValue,
                (byte)cycleInt,
                (ushort)crcValue,
                payloadSpan);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dataBytes);
        }
    }

    /// <summary>
    /// New format since CANoe 5.2. The application channel is the interface id.
    /// Mask 1 is bus A. Mask 2 and mask 3 are bus B.
    /// </summary>
    private static bool _TryParseNewFlexRay(
        ref AscTokenizerBytes tokenizer,
        bool isHex,
        out int channel,
        out byte[] frame)
    {
        channel = 0;
        frame = [];

        // Cluster and client are not part of the reconstructed frame.
        if (!_TryParseFlexInt(ref tokenizer, isHex, out _)
            || !_TryParseFlexInt(ref tokenizer, isHex, out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out channel))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int mask) || (mask != 1 && mask != 2 && mask != 3))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int slot)
            || slot < 0
            || slot > _MaxSlot)
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int cycleInt) || cycleInt < 0 || cycleInt > 63)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction) || !_IsFlexRayDirection(direction))
        {
            return false;
        }

        // App parameter, flags, CC type, and CC data are controller metadata.
        if (!_TryParseFlexInt(ref tokenizer, isHex, out _)
            || !_TryParseFlexInt(ref tokenizer, isHex, out _)
            || !_TryParseFlexInt(ref tokenizer, isHex, out _)
            || !_TryParseFlexInt(ref tokenizer, isHex, out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int crcValue)
            || crcValue < 0
            || crcValue > ushort.MaxValue)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int payloadLength)
            || payloadLength < 0
            || payloadLength > FlexRayLinkTypeFrame.MaxPayloadBytes)
        {
            return false;
        }

        if (!_TryParseFlexInt(ref tokenizer, isHex, out int bufferLength)
            || bufferLength < 0
            || bufferLength > payloadLength)
        {
            return false;
        }

        // payloadLength is the frame's payload size and bufferLength the bytes the line actually lists (the rest of a longer
        // payload is not logged): the payload array has the full length with the unlisted tail left zero. Both are
        // bounded by MaxPayloadBytes above, so the allocation is at most 254 bytes. A missing or invalid listed byte fails the line.
        byte[] data = new byte[payloadLength];
        for (int i = 0; i < bufferLength; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken)
                || !AscCanParser.TryParseByte(dataToken, isHex ? 16 : 10, out byte parsed))
            {
                return false;
            }

            data[i] = parsed;
        }

        // Mask 3 means both channels. The link type stores one bit, so both is recorded as B.
        bool channelB = mask != 1;
        frame = FlexRayLinkTypeFrame.BuildFrame(
            channelB,
            (ushort)slot,
            (byte)cycleInt,
            (ushort)crcValue,
            data);
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Parses up to <paramref name="dataLen"/> data byte tokens into <paramref name="dataBytes"/> and returns how many were
    /// read; stops at the first missing or invalid token. The caller guarantees <paramref name="dataBytes"/> holds at least
    /// <paramref name="dataLen"/> bytes.
    /// </summary>
    private static int _ParseByteDataTokens(
        ref AscTokenizerBytes tokenizer, int dataLen, bool isHex, byte[] dataBytes)
    {
        int numericBase = isHex ? 16 : 10;
        int parsedCount = 0;
        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken))
            {
                break;
            }

            if (!AscCanParser.TryParseByte(dataToken, numericBase, out byte parsed))
            {
                break;
            }

            dataBytes[parsedCount++] = parsed;
        }

        return parsedCount;
    }

    /// <summary>
    /// Reads the next token as a number in the file base (hexadecimal or decimal); the whole token must be consumed. The
    /// result is a non-negative <see cref="int"/> for hex; a decimal token may be negative, so callers range-check it.
    /// </summary>
    private static bool _TryParseFlexInt(ref AscTokenizerBytes tokenizer, bool isHex, out int value)
    {
        value = 0;
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> token) || token.IsEmpty)
        {
            return false;
        }

        if (isHex)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(token, out uint parsed, out int consumed, 'X')
                || consumed != token.Length
                || parsed > int.MaxValue)
            {
                return false;
            }

            value = (int)parsed;
            return true;
        }

        return System.Buffers.Text.Utf8Parser.TryParse(token, out value, out int consumedDec)
            && consumedDec == token.Length;
    }

    private static bool _TokenIs(ReadOnlySpan<byte> token, ReadOnlySpan<byte> expected) =>
        token.Length == expected.Length && AscLineClassifier.StartsWithAsciiIgnoreCase(token, expected);

    private static bool _IsFlexRayDirection(ReadOnlySpan<byte> token) =>
        _TokenIs(token, "Rx"u8) || _TokenIs(token, "Tx"u8) || _TokenIs(token, "TxRq"u8);

    #endregion
}
