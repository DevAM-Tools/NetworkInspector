// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC FlexRay message line into a LINKTYPE_FLEXRAY binary frame.
///
/// ASC FlexRay line format:
///   &lt;time&gt; Fr &lt;channel&gt; V9 &lt;frame_id&gt; &lt;payload_len&gt; &lt;cycle&gt; &lt;nm&gt;
///   &lt;header_crc&gt; &lt;ident&gt; &lt;data_len&gt; &lt;data...&gt; &lt;flags&gt;
///
/// LINKTYPE_FLEXRAY frame layout (7-byte header + data):
///   Measurement header + error flags + ISO 17458-2 frame header + payload.
/// </summary>
internal static class AscFlexRayParser
{
    #region Public API

    /// <summary>
    /// Tries to parse an ASC FlexRay line and produce a DLT_FLEXRAY binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec.</param>
    /// <param name="timestamp">Parsed timestamp in seconds.</param>
    /// <param name="channel">Parsed FlexRay channel number.</param>
    /// <param name="frame">The resulting DLT_FLEXRAY binary frame.</param>
    /// <returns><c>true</c> if parsing succeeded.</returns>
    internal static bool TryParse(
        ReadOnlySpan<char> line, int numericBase,
        out double timestamp, out int channel, out byte[] frame)
    {
        timestamp = 0.0;
        channel = 0;
        frame = [];

        AscTokenizer tokenizer = new(line);

        // Token 0: timestamp
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> tsToken)
            || !AscCanParser.TryParseTimestamp(tsToken, out timestamp))
        {
            return false;
        }

        // Token 1: "Fr" keyword
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> frToken))
        {
            return false;
        }

        if (!frToken.Equals("Fr", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Token 2: channel number
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Token 3: version string (e.g., "V9") — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Token 4: frame ID (slot ID)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> frameIdToken))
        {
            return false;
        }

        NumberStyles numStyle = numericBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!ushort.TryParse(frameIdToken, numStyle, CultureInfo.InvariantCulture, out ushort frameId))
        {
            return false;
        }

        // Token 5: payload length (in words, i.e., 2-byte units)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> payloadLenToken))
        {
            return false;
        }

        if (!int.TryParse(payloadLenToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int payloadLenWords))
        {
            return false;
        }

        if (payloadLenWords < 0)
        {
            return false;
        }

        // Token 6: cycle count
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> cycleToken))
        {
            return false;
        }

        if (!byte.TryParse(cycleToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte cycle))
        {
            return false;
        }

        // Token 7: NM (Network Management) flag — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Token 8: header CRC
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> crcToken))
        {
            return false;
        }

        if (!ushort.TryParse(crcToken, numStyle, CultureInfo.InvariantCulture, out ushort headerCrc))
        {
            return false;
        }

        // Token 9: identifier/name — may be 'x' or a symbolic name, skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Token 10: data length (in bytes)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataLenToken))
        {
            return false;
        }

        if (!int.TryParse(dataLenToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataLen))
        {
            return false;
        }

        if (dataLen < 0)
        {
            return false;
        }

        if (payloadLenWords > 0 && dataLen > payloadLenWords * 2)
        {
            // Payload length in the header is in 16-bit words; declared byte length cannot exceed that.
            return false;
        }

        // Clamp to the FlexRay protocol maximum before allocating: when payloadLenWords is 0
        // (not specified) the guard above does not apply, and a malicious ASC line could
        // declare an arbitrarily large dataLen, triggering an unbounded heap allocation.
        dataLen = Math.Min(dataLen, FlexRayLinkTypeFrame.MaxPayloadBytes);

        byte[] dataBytes = ArrayPool<byte>.Shared.Rent(dataLen);
        try
        {
            int parsedCount = _ParseCharDataTokens(ref tokenizer, dataLen, dataBytes);

            // Build LINKTYPE_FLEXRAY frame (ASC channel 1 = A, 2 = B).
            ReadOnlySpan<byte> payloadSpan = parsedCount > 0
                ? dataBytes.AsSpan(0, parsedCount)
                : ReadOnlySpan<byte>.Empty;
            frame = FlexRayLinkTypeFrame.BuildFrame(
                FlexRayLinkTypeFrame.AscChannelToBusChannel(channel),
                frameId,
                cycle,
                headerCrc,
                payloadSpan);

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dataBytes);
        }
    }

    #endregion

    #region Byte-span overload (zero-allocation path)

    /// <summary>
    /// Byte-span overload of <see cref="TryParse(ReadOnlySpan{char}, int, out double, out int, out byte[])"/>.
    /// Works directly on raw ASCII bytes without converting to a <see cref="string"/>.
    /// </summary>
    internal static bool TryParse(
        ReadOnlySpan<byte> line, int numericBase,
        out double timestamp, out int channel, out byte[] frame)
    {
        timestamp = 0.0;
        channel = 0;
        frame = [];

        AscTokenizerBytes tokenizer = new(line);

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> tsToken)
            || !AscCanParser.TryParseTimestamp(tsToken, out timestamp))
        {
            return false;
        }

        // "Fr" keyword
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> frToken)
            || !AscLineClassifier.StartsWithAsciiIgnoreCase(frToken, "Fr"u8) || frToken.Length != 2)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Version string (e.g., "V9") — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Frame ID
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> frameIdToken))
        {
            return false;
        }

        bool isHex = numericBase == 16;
        ushort frameId;
        if (isHex)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(frameIdToken, out uint u, out _, 'X') || u > ushort.MaxValue)
            {
                return false;
            }

            frameId = (ushort)u;
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(frameIdToken, out int si, out _) || si < 0 || si > ushort.MaxValue)
            {
                return false;
            }

            frameId = (ushort)si;
        }

        // Payload length (in words)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> payloadLenToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(payloadLenToken, out int payloadLenWords, out _))
        {
            return false;
        }

        if (payloadLenWords < 0)
        {
            return false;
        }

        // Cycle count
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> cycleToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(cycleToken, out int cycleInt, out _))
        {
            return false;
        }

        byte cycle = (byte)(cycleInt & 0xFF);

        // NM flag — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Header CRC
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> crcToken))
        {
            return false;
        }

        ushort headerCrc;
        if (isHex)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(crcToken, out uint u, out _, 'X') || u > ushort.MaxValue)
            {
                return false;
            }

            headerCrc = (ushort)u;
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(crcToken, out int si, out _) || si < 0 || si > ushort.MaxValue)
            {
                return false;
            }

            headerCrc = (ushort)si;
        }

        // Identifier/name — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Data length (in bytes)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataLenToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(dataLenToken, out int dataLen, out _))
        {
            return false;
        }

        if (dataLen < 0)
        {
            return false;
        }

        if (payloadLenWords > 0 && dataLen > payloadLenWords * 2)
        {
            return false;
        }

        // Clamp to the FlexRay protocol maximum before allocating: when payloadLenWords is 0
        // (not specified) the guard above does not apply, and a malicious ASC line could
        // declare an arbitrarily large dataLen, triggering an unbounded heap allocation.
        dataLen = Math.Min(dataLen, FlexRayLinkTypeFrame.MaxPayloadBytes);

        byte[] dataBytes = ArrayPool<byte>.Shared.Rent(dataLen);
        try
        {
            int parsedCount = _ParseByteDataTokens(ref tokenizer, dataLen, dataBytes);

            ReadOnlySpan<byte> payloadSpan = parsedCount > 0
                ? dataBytes.AsSpan(0, parsedCount)
                : ReadOnlySpan<byte>.Empty;
            frame = FlexRayLinkTypeFrame.BuildFrame(
                FlexRayLinkTypeFrame.AscChannelToBusChannel(channel),
                frameId,
                cycle,
                headerCrc,
                payloadSpan);

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dataBytes);
        }
    }

    #endregion

    #region Private Helpers

    private static int _ParseCharDataTokens(ref AscTokenizer tokenizer, int dataLen, byte[] dataBytes)
    {
        int parsedCount = 0;

        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataToken))
            {
                break;
            }

            // The data might be in hex pairs (e.g., "01d0") — parse two bytes at a time
            if (dataToken.Length >= 4)
            {
                // Could be a hex word (2 bytes), parse byte by byte
                int bytesInToken = dataToken.Length / 2;
                for (int j = 0; j < bytesInToken && parsedCount < dataLen; j++)
                {
                    ReadOnlySpan<char> byteStr = dataToken.Slice(j * 2, 2);
                    if (byte.TryParse(byteStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                    {
                        dataBytes[parsedCount++] = b;
                    }
                    else
                    {
                        break;
                    }
                }

                // Adjust the loop counter to account for multi-byte tokens
                i = parsedCount - 1;
            }
            else if (dataToken.Length == 2)
            {
                if (byte.TryParse(dataToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    dataBytes[parsedCount++] = b;
                }
                else
                {
                    break;
                }
            }
            else
            {
                // Might be a flags token or other metadata — stop data parsing
                break;
            }
        }

        return parsedCount;
    }

    private static int _ParseByteDataTokens(ref AscTokenizerBytes tokenizer, int dataLen, byte[] dataBytes)
    {
        int parsedCount = 0;

        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken))
            {
                break;
            }

            if (dataToken.Length >= 4)
            {
                int bytesInToken = dataToken.Length / 2;
                for (int j = 0; j < bytesInToken && parsedCount < dataLen; j++)
                {
                    ReadOnlySpan<byte> byteStr = dataToken.Slice(j * 2, 2);
                    if (System.Buffers.Text.Utf8Parser.TryParse(byteStr, out uint u, out _, 'X') && u <= 255)
                    {
                        dataBytes[parsedCount++] = (byte)u;
                    }
                    else
                    {
                        break;
                    }
                }

                i = parsedCount - 1;
            }
            else if (dataToken.Length == 2)
            {
                if (System.Buffers.Text.Utf8Parser.TryParse(dataToken, out uint u, out _, 'X') && u <= 255)
                {
                    dataBytes[parsedCount++] = (byte)u;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return parsedCount;
    }

    #endregion
}
