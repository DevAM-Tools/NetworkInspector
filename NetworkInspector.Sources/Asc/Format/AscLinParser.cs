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
/// DLT_LIN frame layout (LINKTYPE_LIN, 212): 8-byte header, then data padded to 4 or 8 bytes. Byte 5 is the
/// protected identifier (id plus parity, as the BLF reader writes it); the <c>CSM</c> token goes into bits 1-0 of byte 4
/// (1 = classic, 2 = enhanced, 0 = not stated).
/// Header bytes: 0 = format revision (1), 1-3 reserved (zero), 4 = payload length in bits 7-4 and checksum type in bits 1-0
/// (message type 0 = frame), 5 = protected identifier, 6 = checksum, 7 = error flags (zero: the ASC line carries none).
/// The buffer is always header plus 4 or 8 data bytes (unused data bytes are zero), the same shape the BLF reader produces.
/// Stateless; safe to call from multiple threads.
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
        => FrameSlabAllocator.ToArray(
            TryParse(line, numericBase, null, out timestamp, out channel, out ReadOnlyMemory<byte> memory), memory, out frame);

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, int, out AscTimestamp, out int, out byte[])"/>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">Parsed LIN channel number.</param>
    /// <param name="frame">The resulting DLT_LIN binary frame. Empty when this method returns <c>false</c>.</param>
    internal static bool TryParse(
        ReadOnlySpan<byte> line, int numericBase, FrameSlabAllocator? slabs,
        out AscTimestamp timestamp, out int channel, out ReadOnlyMemory<byte> frame)
    {
        timestamp = default;
        channel = 0;
        frame = default;

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

        // The frame id is a 6-bit LIN identifier (0..0x3F), written in the file's numeric base. Values above 0x3F are not a
        // LIN id and reject the line instead of being truncated to a different id.
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
            // A negative decimal id is not a LIN id either: the (byte) cast below would wrap it into a valid-looking 6-bit id.
            if (!System.Buffers.Text.Utf8Parser.TryParse(idToken, out int signedId, out _) || signedId < 0 || signedId > 0x3F)
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

        // The DLC token is parsed as decimal whatever the file base. It comes from the file, so it is clamped to the 8-byte LIN maximum before
        // it bounds the loop and the stack scratch below.
        if (!System.Buffers.Text.Utf8Parser.TryParse(dlcTokenBytes, out int dlc, out _))
        {
            return false;
        }

        int dataLength = Math.Min(dlc, _MaxLinDataLength);

        // Payload bytes go to an 8-byte stack scratch (no allocation); the frame is rented after the line is parsed. A line
        // with fewer data tokens than its DLC (or a trailing word such as "checksum") stops the loop early; parsedCount
        // then becomes the stored length, so the frame never contains invented bytes.
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

        // Extract the checksum from the text after the data: "checksum = <value>" (also ':' as separator). The keyword is
        // searched anywhere in the rest of the line, case-insensitively; an absent or unparseable value leaves 0.
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

        // Checksum model: "CSM = classic|enhanced" maps to DLT_LIN byte 4 bits 1-0, so the ASC writer can restore it.
        byte checksumTypeBits = _ParseChecksumModel(remaining);

        // Byte 5 carries the protected identifier (id plus parity), computed with the rule shared with the BLF reader so
        // both formats produce identical frames for the same LIN traffic.
        byte pid = LinProtectedId.Compute(frameId);
        frame = _BuildDltLinFrame(slabs, pid, parsedCount, dataBytes[..parsedCount], checksum, checksumTypeBits);
        return true;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds the DLT_LIN record: 8-byte header plus the data area padded to 4 or 8 bytes. The zeroed rental provides the
    /// reserved header bytes (1-3), the error flags (7) and the data padding, none of which is written here.
    /// </summary>
    private static ReadOnlyMemory<byte> _BuildDltLinFrame(
        FrameSlabAllocator? slabs, byte pid, int dlc, ReadOnlySpan<byte> data, byte checksum, byte checksumTypeBits)
    {
        int clampedDlc = Math.Clamp(dlc, 0, _MaxLinDataLength);
        int dataPad = clampedDlc <= 4 ? 4 : 8;
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, _DltLinHeaderSize + dataPad);
        Span<byte> frame = buffer.Span;
        frame[0] = 1;   // message format revision

        // Byte 4: payload length in the high nibble, checksum type in bits 1-0 (message type bits 3-2 stay 0 = frame).
        frame[4] = (byte)((clampedDlc << 4) | (checksumTypeBits & 0x03));
        frame[5] = pid;
        frame[6] = checksum;
        int copyLen = Math.Min(clampedDlc, data.Length);
        if (copyLen > 0)
        {
            data[..copyLen].CopyTo(frame[_DltLinHeaderSize..]);
        }

        return buffer;
    }

    /// <summary>
    /// Reads the <c>CSM = classic|enhanced</c> token after the data bytes. Returns the DLT_LIN checksum-type bits:
    /// 1 = classic, 2 = enhanced, 0 when the token is missing or unknown.
    /// </summary>
    private static byte _ParseChecksumModel(ReadOnlySpan<byte> remaining)
    {
        int csmIdx = _IndexOfAsciiIgnoreCase(remaining, "CSM"u8);
        if (csmIdx < 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> afterCsm = AscTokenizerBytes.TrimStartAscii(remaining[(csmIdx + 3)..]);
        if (afterCsm.IsEmpty || (afterCsm[0] != (byte)'=' && afterCsm[0] != (byte)':'))
        {
            return 0;
        }

        ReadOnlySpan<byte> value = AscTokenizerBytes.TrimStartAscii(afterCsm[1..]);
        if (AscLineClassifier.StartsWithAsciiIgnoreCase(value, "enhanced"u8))
        {
            return 2;
        }

        return AscLineClassifier.StartsWithAsciiIgnoreCase(value, "classic"u8)
            ? (byte)1
            : (byte)0;
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

    /// <summary>True for the optional direction word between id and DLC: <c>Tx</c>, <c>Rx</c>, <c>Slave</c>, <c>Master</c>.</summary>
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

    /// <summary>
    /// Index of the first case-insensitive ASCII occurrence of <paramref name="needle"/>, or -1. A plain scan: the text
    /// searched is the short tail of one line (after the data bytes) and the needles are at most 8 bytes.
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
            if (AscLineClassifier.StartsWithAsciiIgnoreCase(haystack[i..], needle))
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
