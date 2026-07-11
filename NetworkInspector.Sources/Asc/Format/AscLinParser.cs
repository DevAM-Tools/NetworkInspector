// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC LIN message line into a DLT_LIN binary frame.
///
/// ASC LIN line format:
///   &lt;time&gt; L&lt;n&gt; &lt;id&gt; [ &lt;dir&gt; ] &lt;dlc&gt; &lt;data...&gt; checksum = &lt;cs&gt; ... CSM = enhanced|classic
/// The direction token (Tx/Rx/Slave/Master) is optional in some exports.
///
/// DLT_LIN frame layout:
///   [pid(1) | length(1) | data(0–8) | checksum(1) | errors(1)]
/// </summary>
internal static class AscLinParser
{
    #region Constants

    /// <summary>DLT_LIN header: [pid(1) | length(1)].</summary>
    private const int _DltLinHeaderSize = 2;

    /// <summary>DLT_LIN trailer: [checksum(1) | errors(1)].</summary>
    private const int _DltLinTrailerSize = 2;

    /// <summary>Maximum LIN data length.</summary>
    private const int _MaxLinDataLength = 8;

    #endregion

    #region Public API

    /// <summary>
    /// Tries to parse an ASC LIN line and produce a DLT_LIN binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec.</param>
    /// <param name="timestamp">Parsed timestamp in seconds.</param>
    /// <param name="channel">Parsed LIN channel number.</param>
    /// <param name="frame">The resulting DLT_LIN binary frame.</param>
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

        // Token 1: LIN channel "L<n>" (e.g., "L1", "L2")
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> chToken))
        {
            return false;
        }

        if (chToken.Length < 2 || chToken[0] != 'L')
        {
            return false;
        }

        if (!int.TryParse(chToken[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out channel))
        {
            return false;
        }

        // Token 2: LIN frame ID
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> idToken))
        {
            return false;
        }

        NumberStyles idStyle = numericBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!byte.TryParse(idToken, idStyle, CultureInfo.InvariantCulture, out byte frameId))
        {
            return false;
        }

        // After frame ID: optional direction (Tx/Rx/Slave/Master), then DLC.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dirOrDlcToken))
        {
            return false;
        }

        ReadOnlySpan<char> dlcToken;
        if (_IsLikelyLinDirectionToken(dirOrDlcToken))
        {
            if (!tokenizer.TryNextToken(out dlcToken))
            {
                return false;
            }
        }
        else
        {
            dlcToken = dirOrDlcToken;
        }

        if (!int.TryParse(dlcToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dlc))
        {
            return false;
        }

        int dataLength = Math.Min(dlc, _MaxLinDataLength);

        // Parse data bytes
        Span<byte> dataBytes = stackalloc byte[_MaxLinDataLength];
        dataBytes.Clear();
        int parsedCount = 0;

        for (int i = 0; i < dataLength; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataToken))
            {
                break;
            }

            // Stop at keyword tokens like "checksum", "HeaderTime", etc.
            // In hex mode, valid bytes like AB, CD start with letters too,
            // so only treat longer tokens starting with a letter as metadata.
            if (dataToken.Length > 2 && char.IsLetter(dataToken[0]))
            {
                break;
            }

            if (AscCanParser.TryParseByte(dataToken, numericBase, out byte b))
            {
                dataBytes[i] = b;
                parsedCount++;
            }
            else
            {
                break;
            }
        }

        // Parse trailing metadata for checksum value
        byte checksum = 0;
        ReadOnlySpan<char> remaining = tokenizer.Remaining;
        int csIdx = remaining.IndexOf("checksum", StringComparison.OrdinalIgnoreCase);
        if (csIdx >= 0)
        {
            ReadOnlySpan<char> afterCs = remaining[(csIdx + 8)..].TrimStart();
            // Skip "=" or ":" if present
            if (afterCs.Length > 0 && (afterCs[0] == '=' || afterCs[0] == ':'))
            {
                afterCs = afterCs[1..].TrimStart();
            }

            // Read the checksum value
            int endIdx = afterCs.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> csValue = endIdx >= 0 ? afterCs[..endIdx] : afterCs;
            _ = byte.TryParse(csValue, idStyle, CultureInfo.InvariantCulture, out checksum);
        }

        // Compute PID from frame ID
        byte pid = ComputePid(frameId);

        // Build DLT_LIN frame: [pid(1) | length(1) | data(0-8) | checksum(1) | errors(1)]
        frame = new byte[_DltLinHeaderSize + parsedCount + _DltLinTrailerSize];
        frame[0] = pid;
        frame[1] = (byte)parsedCount;

        if (parsedCount > 0)
        {
            dataBytes[..parsedCount].CopyTo(frame.AsSpan(_DltLinHeaderSize));
        }

        frame[_DltLinHeaderSize + parsedCount] = checksum;
        frame[_DltLinHeaderSize + parsedCount + 1] = 0; // no errors

        return true;
    }

    #endregion

    #region Helpers

    private static bool _IsLikelyLinDirectionToken(ReadOnlySpan<char> token) =>
        token.Equals("Tx", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Rx", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Slave", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Master", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the LIN PID (Protected Identifier) from a 6-bit frame ID.
    /// P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4 (even parity over bits 0,1,2,4)
    /// P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5) (odd parity over bits 1,3,4,5)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ComputePid(byte id)
    {
        int frameId = id & 0x3F;
        int p0 = ((frameId >> 0) ^ (frameId >> 1) ^ (frameId >> 2) ^ (frameId >> 4)) & 1;
        int p1 = (~((frameId >> 1) ^ (frameId >> 3) ^ (frameId >> 4) ^ (frameId >> 5))) & 1;
        return (byte)(frameId | (p0 << 6) | (p1 << 7));
    }

    private static bool _IsLikelyLinDirectionToken(ReadOnlySpan<byte> token) =>
        _AscLinDirectionBytesEqual(token, "Tx"u8)
        || _AscLinDirectionBytesEqual(token, "Rx"u8)
        || _AscLinDirectionBytesEqual(token, "Slave"u8)
        || _AscLinDirectionBytesEqual(token, "Master"u8);

    private static bool _AscLinDirectionBytesEqual(ReadOnlySpan<byte> token, ReadOnlySpan<byte> ascii) =>
        token.Length == ascii.Length && AscLineClassifier.StartsWithAsciiIgnoreCase(token, ascii);

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

        // Channel token: "L<n>"
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken))
        {
            return false;
        }

        if (chToken.Length < 2 || chToken[0] != (byte)'L')
        {
            return false;
        }

        if (!System.Buffers.Text.Utf8Parser.TryParse(chToken[1..], out channel, out _))
        {
            return false;
        }

        // Frame ID
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idToken))
        {
            return false;
        }

        bool isHex = numericBase == 16;
        byte frameId;
        if (isHex)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(idToken, out uint u, out _, 'X') || u > 0x3F)
            {
                return false;
            }

            frameId = (byte)u;
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(idToken, out int si, out _) || si > 0x3F)
            {
                return false;
            }

            frameId = (byte)si;
        }

        // After frame ID: optional direction token, then DLC.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dirOrDlcToken))
        {
            return false;
        }

        ReadOnlySpan<byte> dlcTokenBytes;
        if (_IsLikelyLinDirectionToken(dirOrDlcToken))
        {
            if (!tokenizer.TryNextToken(out dlcTokenBytes))
            {
                return false;
            }
        }
        else
        {
            dlcTokenBytes = dirOrDlcToken;
        }

        if (!System.Buffers.Text.Utf8Parser.TryParse(dlcTokenBytes, out int dlc, out _))
        {
            return false;
        }

        int dataLength = Math.Min(dlc, _MaxLinDataLength);

        Span<byte> dataBytes = stackalloc byte[_MaxLinDataLength];
        dataBytes.Clear();
        int parsedCount = 0;

        for (int i = 0; i < dataLength; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken))
            {
                break;
            }

            if (dataToken.Length > 2 && _IsAsciiLetter(dataToken[0]))
            {
                break;
            }

            if (AscCanParser.TryParseByte(dataToken, numericBase, out byte b))
            {
                dataBytes[i] = b;
                parsedCount++;
            }
            else
            {
                break;
            }
        }

        // Extract checksum from remaining bytes
        byte checksum = 0;
        ReadOnlySpan<byte> remaining = tokenizer.Remaining;
        int csIdx = _IndexOfAsciiIgnoreCase(remaining, "checksum"u8);
        if (csIdx >= 0)
        {
            ReadOnlySpan<byte> afterCs = AscTokenizerBytes.TrimStartAscii(remaining[(csIdx + 8)..]);
            if (afterCs.Length > 0 && (afterCs[0] == (byte)'=' || afterCs[0] == (byte)':'))
            {
                afterCs = AscTokenizerBytes.TrimStartAscii(afterCs[1..]);
            }

            int endIdx = -1;
            for (int k = 0; k < afterCs.Length; k++)
            {
                if (afterCs[k] == (byte)' ' || afterCs[k] == (byte)'\t')
                {
                    endIdx = k;
                    break;
                }
            }

            ReadOnlySpan<byte> csValue = endIdx >= 0 ? afterCs[..endIdx] : afterCs;
            if (isHex)
            {
                if (System.Buffers.Text.Utf8Parser.TryParse(csValue, out uint u, out _, 'X') && u <= 255)
                {
                    checksum = (byte)u;
                }
            }
            else
            {
                // Ignore parse errors: checksum defaults to 0 when not parseable
                _ = System.Buffers.Text.Utf8Parser.TryParse(csValue, out checksum, out _);
            }
        }

        byte pid = ComputePid(frameId);

        frame = new byte[_DltLinHeaderSize + parsedCount + _DltLinTrailerSize];
        frame[0] = pid;
        frame[1] = (byte)parsedCount;

        if (parsedCount > 0)
        {
            dataBytes[..parsedCount].CopyTo(frame.AsSpan(_DltLinHeaderSize));
        }

        frame[_DltLinHeaderSize + parsedCount] = checksum;
        frame[_DltLinHeaderSize + parsedCount + 1] = 0;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');

    private static int _IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return -1;
        }

        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            if (AscLineClassifier.StartsWithAsciiIgnoreCase(haystack[i..], needle))
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
