// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC LIN message line into a DLT_LIN binary frame.
///
/// ASC LIN line format:
///   &lt;time&gt; Li|&lt;L2..L255&gt; &lt;id&gt; [ &lt;dir&gt; ] &lt;dlc&gt; &lt;data...&gt; checksum = &lt;cs&gt; ... CSM = enhanced|classic
/// Channel 1 is the token <c>Li</c>. <c>L1</c> is not a channel.
/// The direction token (Tx/Rx/Slave/Master) is optional in some exports and is discarded.
///
/// DLT_LIN frame layout (Wireshark <c>packet-lin.h</c>):
/// 8-byte header, then data padded to 4 or 8 bytes.
/// </summary>
internal static class AscLinParser
{
    #region Constants

    /// <summary>DLT_LIN header size.</summary>
    private const int _DltLinHeaderSize = 8;

    /// <summary>Maximum LIN data length.</summary>
    private const int _MaxLinDataLength = 8;

    #endregion

    #region Public API

    /// <summary>
    /// Tries to parse an ASC LIN line and produce a DLT_LIN binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">Parsed LIN channel number.</param>
    /// <param name="frame">The resulting DLT_LIN binary frame. Empty when this method returns <c>false</c>.</param>
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

        // Channel token: "Li" is channel 1. "L2".."L255" are the other channels.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !_TryParseLinChannel(chToken, out channel))
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
            if (!System.Buffers.Text.Utf8Parser.TryParse(idToken, out uint parsedId, out _, 'X') || parsedId > 0x3F)
            {
                return false;
            }

            frameId = (byte)parsedId;
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(idToken, out int signedId, out _) || signedId > 0x3F)
            {
                return false;
            }

            frameId = (byte)signedId;
        }

        // After frame ID: optional direction token, then DLC. Direction is discarded.
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

            if (AscCanParser.TryParseByte(dataToken, numericBase, out byte parsed))
            {
                dataBytes[i] = parsed;
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
                if (System.Buffers.Text.Utf8Parser.TryParse(csValue, out uint checksumValue, out _, 'X') && checksumValue <= 255)
                {
                    checksum = (byte)checksumValue;
                }
            }
            else
            {
                // Ignore parse errors: checksum defaults to 0 when not parseable
                _ = System.Buffers.Text.Utf8Parser.TryParse(csValue, out checksum, out _);
            }
        }

        byte pid = ComputePid(frameId);
        frame = _BuildDltLinFrame(pid, parsedCount, dataBytes[..parsedCount], checksum);
        return true;
    }

    #endregion

    #region Helpers

    private static byte[] _BuildDltLinFrame(byte pid, int dlc, ReadOnlySpan<byte> data, byte checksum)
    {
        int clampedDlc = Math.Clamp(dlc, 0, _MaxLinDataLength);
        int dataPad = clampedDlc <= 4 ? 4 : 8;
        byte[] frame = new byte[_DltLinHeaderSize + dataPad];
        frame[0] = 1;
        frame[4] = (byte)(clampedDlc << 4);
        frame[5] = pid;
        frame[6] = checksum;
        int copyLen = Math.Min(clampedDlc, data.Length);
        if (copyLen > 0)
        {
            data[..copyLen].CopyTo(frame.AsSpan(_DltLinHeaderSize));
        }

        return frame;
    }

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

    /// <summary>
    /// <c>Li</c> is channel 1. <c>L2</c> through <c>L255</c> are the numbered channels.
    /// <c>L1</c> is not defined by the LIN ASC channel symbol.
    /// </summary>
    private static bool _TryParseLinChannel(ReadOnlySpan<byte> token, out int channel)
    {
        channel = 0;
        if (token.Length < 2 || (token[0] != (byte)'L' && token[0] != (byte)'l'))
        {
            return false;
        }

        if (token.Length == 2 && (token[1] == (byte)'i' || token[1] == (byte)'I'))
        {
            channel = 1;
            return true;
        }

        return System.Buffers.Text.Utf8Parser.TryParse(token[1..], out channel, out int consumed)
            && consumed == token.Length - 1
            && channel >= 2
            && channel <= 255;
    }

    private static bool _IsLikelyLinDirectionToken(ReadOnlySpan<byte> token) =>
        _AscLinDirectionBytesEqual(token, "Tx"u8)
        || _AscLinDirectionBytesEqual(token, "Rx"u8)
        || _AscLinDirectionBytesEqual(token, "Slave"u8)
        || _AscLinDirectionBytesEqual(token, "Master"u8);

    private static bool _AscLinDirectionBytesEqual(ReadOnlySpan<byte> token, ReadOnlySpan<byte> ascii) =>
        token.Length == ascii.Length && AscLineClassifier.StartsWithAsciiIgnoreCase(token, ascii);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(byte value) =>
        (value >= (byte)'A' && value <= (byte)'Z') || (value >= (byte)'a' && value <= (byte)'z');

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
