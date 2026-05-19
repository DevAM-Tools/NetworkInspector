// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Globalization;

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC FlexRay message line into a DLT_FLEXRAY binary frame.
///
/// ASC FlexRay line format:
///   &lt;time&gt; Fr &lt;channel&gt; V9 &lt;frame_id&gt; &lt;payload_len&gt; &lt;cycle&gt; &lt;nm&gt;
///   &lt;header_crc&gt; &lt;ident&gt; &lt;data_len&gt; &lt;data...&gt; &lt;flags&gt;
///
/// DLT_FLEXRAY frame layout (7-byte header + data):
///   [channel(1) | type_flags(1) | frame_id_hi(1) | frame_id_lo(1) | cycle(1) | header_crc_hi(1) | header_crc_lo(1) | data...]
/// </summary>
internal static class AscFlexRayParser
{
    #region Constants

    /// <summary>DLT_FLEXRAY header size: 7 bytes.</summary>
    private const int DltFlexRayHeaderSize = 7;

    /// <summary>
    /// Maximum FlexRay payload in bytes per the FlexRay specification:
    /// 127 payload words × 2 bytes per word = 254 bytes.
    /// Used to clamp the parsed data length and prevent unbounded allocation
    /// when the ASC line omits an explicit <c>payload_len_words</c> value (i.e. 0).
    /// </summary>
    private const int MaxFlexRayDataBytes = 254;

    #endregion

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

        if (payloadLenWords > 0 && dataLen > payloadLenWords * 2)
        {
            // Payload length in the header is in 16-bit words; declared byte length cannot exceed that.
            return false;
        }

        // Clamp to the FlexRay protocol maximum before allocating: when payloadLenWords is 0
        // (not specified) the guard above does not apply, and a malicious ASC line could
        // declare an arbitrarily large dataLen, triggering an unbounded heap allocation.
        dataLen = Math.Min(dataLen, MaxFlexRayDataBytes);

        // Parse data bytes (hex pairs, may have spaces)
        byte[] dataBytes = new byte[dataLen];
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

        // Build DLT_FLEXRAY frame
        frame = new byte[DltFlexRayHeaderSize + parsedCount];

        // Byte 0: channel (A=0/1, B=1/2)
        frame[0] = (byte)(channel & 0xFF);

        // Byte 1: type_flags (no flag info from typical ASC lines, set to 0)
        frame[1] = 0;

        // Bytes 2-3: frame ID (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), frameId);

        // Byte 4: cycle count
        frame[4] = cycle;

        // Bytes 5-6: header CRC (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), headerCrc);

        // Payload
        if (parsedCount > 0)
        {
            dataBytes.AsSpan(0, parsedCount).CopyTo(frame.AsSpan(DltFlexRayHeaderSize));
        }

        return true;
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

        if (payloadLenWords > 0 && dataLen > payloadLenWords * 2)
        {
            return false;
        }

        // Clamp to the FlexRay protocol maximum before allocating: when payloadLenWords is 0
        // (not specified) the guard above does not apply, and a malicious ASC line could
        // declare an arbitrarily large dataLen, triggering an unbounded heap allocation.
        dataLen = Math.Min(dataLen, MaxFlexRayDataBytes);

        byte[] dataBytes = new byte[dataLen];
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

        frame = new byte[DltFlexRayHeaderSize + parsedCount];
        frame[0] = (byte)(channel & 0xFF);
        frame[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), frameId);
        frame[4] = cycle;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), headerCrc);

        if (parsedCount > 0)
        {
            dataBytes.AsSpan(0, parsedCount).CopyTo(frame.AsSpan(DltFlexRayHeaderSize));
        }

        return true;
    }

    #endregion
}
