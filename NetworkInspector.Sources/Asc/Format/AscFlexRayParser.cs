// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC FlexRay message line into a LINKTYPE_FLEXRAY binary frame.
///
/// Old ASC FlexRay line (CANoe 5.1 and earlier):
///   &lt;time&gt; Fr &lt;channel&gt; V9 &lt;id&gt; &lt;cycle&gt; &lt;nm&gt; &lt;sync&gt; &lt;header_crc&gt; &lt;name&gt; &lt;byte_count&gt; &lt;data...&gt;
/// New ASC FlexRay line (since CANoe 5.2):
///   &lt;time&gt; Fr RMSG|PDU &lt;cluster&gt; &lt;client&gt; &lt;channel&gt; &lt;mask&gt; &lt;slot&gt; &lt;cycle&gt; &lt;dir&gt; ... &lt;crc&gt; &lt;name&gt; &lt;payloadLen&gt; &lt;bufferLen&gt; &lt;data...&gt;
/// Numeric fields follow the file base. NM and Sync are skipped. Mask 3 (both channels) is stored as bus channel B
/// because LINKTYPE_FLEXRAY has one channel bit.
///
/// LINKTYPE_FLEXRAY frame layout (7-byte header + data):
///   Measurement header + error flags + ISO 17458-2 frame header + payload.
/// </summary>
internal static class AscFlexRayParser
{
    #region Public API

    /// <summary>
    /// Parses an ASC FlexRay line from raw ASCII bytes.
    /// </summary>
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
            || frameIdValue > ushort.MaxValue)
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
            || slot > ushort.MaxValue)
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
