// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses a Vector ASC <c>CANXL</c> line (logging spec 1.4.17 / linux-can <c>eval_canxl</c>)
/// into the compact SocketCAN buffer used by the CAN XL dissector.
/// Channel and the decimal data-length column are decimal. Every other numeric column is hex,
/// even when the file header says <c>base dec</c>, so this parser ignores <see cref="AscHeader.NumericBase"/>.
/// <para>
/// A <c>CANXL</c> line carries one of three formats, selected by the format token. <c>XLFF</c> produces the 12-byte
/// CAN XL header buffer that <c>CanProtocol</c> reads: bytes 0-3 big-endian word (VCID in bits 16-23, 11-bit priority in
/// bits 0-10), byte 4 flags (XLF, SEC, RRS), byte 5 SDU type, bytes 6-7 payload length (little-endian), bytes 8-11
/// acceptance field (little-endian), then the payload. <c>CBFF</c>/<c>CEFF</c> (classic) and <c>FBFF</c>/<c>FEFF</c> (FD)
/// rows are written into the same SocketCAN classic/FD layouts as <see cref="AscCanParser"/> and
/// <see cref="AscCanFdParser"/>, so they are not CAN XL frames downstream.
/// </para>
/// </summary>
internal static class AscCanXlParser
{
    #region Constants

    /// <summary>Header of a classic or FD SocketCAN frame: id(4) + length(1) + flags(1) + reserved(2).</summary>
    private const int _ClassicHeaderSize = 8;

    /// <summary>Header of a CAN XL SocketCAN frame (priority/VCID word, flags, SDU type, length, acceptance field).</summary>
    private const int _XlHeaderSize = 12;

    /// <summary>Data bytes of a classic frame; the classic buffer is always header plus this many bytes.</summary>
    private const int _ClassicDataSize = 8;

    /// <summary>Largest CAN FD payload; bounds the stack scratch of the FD tail.</summary>
    private const int _MaxFdData = 64;

    /// <summary>Largest CAN XL payload (ISO 11898-1:2024); equals the maximum that <c>CanProtocol</c> accepts.</summary>
    private const int _MaxXlData = 2048;

    // SocketCAN id-word and flag-byte bits written into the output frame.
    private const uint _SocketCanEff = 0x80000000u;
    private const uint _SocketCanRtr = 0x40000000u;
    private const byte _SocketCanXlf = 0x80;
    private const byte _SocketCanSec = 0x01;
    private const byte _SocketCanRrs = 0x02;
    private const byte _SocketCanFdf = 0x04;
    private const byte _SocketCanBrs = 0x01;
    private const byte _SocketCanEsi = 0x02;

    // Bits of the hexadecimal flags column at the end of a CANXL line (Vector ASC logging spec); they differ from the
    // SocketCAN bits above and are translated one by one.
    private const uint _AscFXlf = 0x00400000u;
    private const uint _AscFFdf = 0x00001000u;
    private const uint _AscFBrs = 0x00002000u;
    private const uint _AscFEsi = 0x00004000u;
    private const uint _AscFSec = 0x01000000u;
    private const uint _AscFRrs = 0x00800000u;
    private const uint _AscFRemote = 0x10u;

    /// <summary>11-bit identifier (also the XL priority); larger values in a standard format are rejected, not truncated.</summary>
    private const uint _StandardIdMask = 0x7FFu;

    /// <summary>29-bit identifier (CAN_EFF_MASK).</summary>
    private const uint _ExtendedIdMask = 0x1FFFFFFFu;

    #endregion

    #region Public API

    /// <summary>
    /// Reads the format token and maps it to the bus type used for interface registration.
    /// <c>XLFF</c> is <see cref="AscBusType.CanXl"/> so export can tell it from CAN FD.
    /// </summary>
    /// <param name="line">Trimmed ASC line.</param>
    /// <param name="busType">The bus type for a recognised format token; <see cref="AscBusType.Unknown"/> otherwise.</param>
    /// <returns><c>false</c> when the line is not a <c>CANXL</c> line or the format token is not one of the four known formats.</returns>
    internal static bool TryPeekBusType(ReadOnlySpan<byte> line, out AscBusType busType)
    {
        // Used by the open-time scan (interface discovery) before any frame is built, so it reads only the format token
        // and none of the numeric columns.
        busType = AscBusType.Unknown;
        if (!_TryReadFormat(line, out ReadOnlySpan<byte> format))
        {
            return false;
        }

        if (_TokenEquals(format, "XLFF"u8))
        {
            busType = AscBusType.CanXl;
            return true;
        }

        if (_TokenEquals(format, "CBFF"u8) || _TokenEquals(format, "CEFF"u8))
        {
            busType = AscBusType.Can;
            return true;
        }

        if (_TokenEquals(format, "FBFF"u8) || _TokenEquals(format, "FEFF"u8))
        {
            busType = AscBusType.CanFd;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses one <c>CANXL</c> line. <c>TxRq</c> is not a frame.
    /// A single symbolic name between the identifier and the DLC/SDT column is skipped.
    /// </summary>
    /// <param name="line">Trimmed ASC line.</param>
    /// <param name="timestamp">Line timestamp. Not the file clock.</param>
    /// <param name="channel">Decimal channel.</param>
    /// <param name="busType">Bus family selected by the format token.</param>
    /// <param name="frame">SocketCAN bytes. Empty when this method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the line does not match the format tail.</returns>
    internal static bool TryParse(
        ReadOnlySpan<byte> line,
        out AscTimestamp timestamp,
        out int channel,
        out AscBusType busType,
        out byte[] frame)
    {
        timestamp = default;
        channel = 0;
        busType = AscBusType.Unknown;
        frame = [];

        AscTokenizerBytes tokenizer = new(line);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> timeToken)
            || !AscTimestamp.TryParse(timeToken, out timestamp))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> keyword)
            || !_TokenEquals(keyword, "CANXL"u8))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> channelToken)
            || !AscCanParser.TryParseChannel(channelToken, out channel)
            || channel < 0)
        {
            return false;
        }

        // Direction is accepted and discarded. TxRq is a request, not a frame.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction)
            || !_IsFrameDirection(direction))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> format))
        {
            return false;
        }

        // Two columns between the format token and the identifier carry nothing that is stored in the frame.
        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // The identifier is hex in every format. For XLFF it is the 11-bit priority. The range check per format rejects
        // a line whose id does not fit instead of masking it into a different identifier.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idToken)
            || !_TryParseHexUInt(idToken, out uint identifier))
        {
            return false;
        }

        // Dispatch on the format token; busType is set only on the branch that parsed, so a failure leaves Unknown.
        if (_TokenEquals(format, "XLFF"u8))
        {
            if (identifier > _StandardIdMask)
            {
                return false;
            }

            busType = AscBusType.CanXl;
            return _TryParseXlff(ref tokenizer, identifier, out frame);
        }

        if (_TokenEquals(format, "CBFF"u8) || _TokenEquals(format, "CEFF"u8))
        {
            bool extended = _TokenEquals(format, "CEFF"u8);
            if (!_IdFits(identifier, extended))
            {
                return false;
            }

            busType = AscBusType.Can;
            return _TryParseClassic(ref tokenizer, identifier, extended, out frame);
        }

        if (_TokenEquals(format, "FBFF"u8) || _TokenEquals(format, "FEFF"u8))
        {
            bool extended = _TokenEquals(format, "FEFF"u8);
            if (!_IdFits(identifier, extended))
            {
                return false;
            }

            busType = AscBusType.CanFd;
            return _TryParseFd(ref tokenizer, identifier, extended, out frame);
        }

        return false;
    }

    #endregion

    #region Format tails

    /// <summary>
    /// Parses the XLFF tail (SDT, SEC, DLC, data length, ..., data, flags). Tries the tail as written; if the DLC/length
    /// pair is unreadable, retries once with one token skipped, because an optional symbolic name can sit in front of SDT.
    /// </summary>
    private static bool _TryParseXlff(ref AscTokenizerBytes tokenizer, uint priority, out byte[] frame)
    {
        frame = [];
        ReadOnlySpan<byte> saved = tokenizer.Capture();
        if (_TryXlffTail(ref tokenizer, priority, out frame, out bool retryName))
        {
            return true;
        }

        if (!retryName)
        {
            return false;
        }

        // can-utils retries once when a symbolic name sits in front of SDT
        // and the DLC/length pair therefore does not parse.
        tokenizer.Restore(saved);
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        return _TryXlffTail(ref tokenizer, priority, out frame, out _);
    }

    /// <summary>
    /// Parses one attempt at the XLFF tail: SDT, SEC, DLC, data length, two unstored columns, VCID, acceptance field, the
    /// data bytes, one unstored column and the flags column (which must have the XL format bit set).
    /// </summary>
    /// <param name="tokenizer">Tokenizer positioned after the identifier column.</param>
    /// <param name="priority">The 11-bit priority parsed from the identifier column.</param>
    /// <param name="frame">The CAN XL frame; empty when this method returns <c>false</c>.</param>
    /// <param name="retryName">
    /// True while the failure is still in the leading columns (SDT, SEC, DLC, length), where a symbolic name could be the
    /// cause. Cleared once the DLC/length pair is valid, so a later failure is reported as a bad line, not retried.
    /// </param>
    private static bool _TryXlffTail(
        ref AscTokenizerBytes tokenizer,
        uint priority,
        out byte[] frame,
        out bool retryName)
    {
        frame = [];
        retryName = true;
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> sdtToken)
            || !_TryParseHexByte(sdtToken, out byte sdt))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> secToken)
            || !_IsZeroOrOne(secToken))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dlcToken)
            || !_TryParseHexUInt(dlcToken, out uint dlc)
            || dlc > 2047)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> lengthToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(lengthToken, out int dataLen, out int consumed)
            || consumed != lengthToken.Length)
        {
            return false;
        }

        // Past this point the DLC/length pair was readable. A later failure is the line, not a name.
        // CAN XL encodes the length as DLC = length − 1 (1..2048 bytes), so the two columns must agree.
        if (dataLen < 1 || dataLen > _MaxXlData || dataLen != (int)dlc + 1)
        {
            return false;
        }

        retryName = false;

        // Two columns between the length and the VCID are not stored.
        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> vcidToken)
            || !_TryParseHexByte(vcidToken, out byte vcid))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> afToken)
            || !_TryParseHexUInt(afToken, out uint acceptanceField))
        {
            return false;
        }

        // Build the XL header in the layout CanProtocol parses. The word is big-endian while the length and the acceptance
        // field are little-endian: that mix is the layout of the consumer, not an oversight. dataLen (≤ 2048) is bounded
        // above, so the allocation and the ushort cast are safe. Byte 4 (flags) is written after the flags column is read.
        frame = new byte[_XlHeaderSize + dataLen];
        uint word = ((uint)vcid << 16) | (priority & _StandardIdMask);
        BinaryPrimitives.WriteUInt32BigEndian(frame, word);
        frame[5] = sdt;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)dataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), acceptanceField);

        // Every payload byte must be present: a short line is rejected whole (the frame is dropped, not zero-padded).
        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken)
                || !_TryParseHexByte(dataToken, out frame[_XlHeaderSize + i]))
            {
                frame = [];
                return false;
            }
        }

        if (!tokenizer.TryNextToken(out _))
        {
            frame = [];
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> flagsToken)
            || !_TryParseHexUInt(flagsToken, out uint flags)
            || (flags & _AscFXlf) == 0)
        {
            frame = [];
            return false;
        }

        byte socketFlags = _SocketCanXlf;
        if ((flags & _AscFSec) != 0)
        {
            socketFlags |= _SocketCanSec;
        }

        if ((flags & _AscFRrs) != 0)
        {
            socketFlags |= _SocketCanRrs;
        }

        frame[4] = socketFlags;
        return true;
    }

    /// <summary>
    /// Parses the CBFF/CEFF tail into a 16-byte classic SocketCAN frame. Unlike <see cref="AscCanParser"/> the bus DLC
    /// above 8 is not copied to <c>len8_dlc</c> (byte 7); only the stored length is capped at 8.
    /// </summary>
    private static bool _TryParseClassic(
        ref AscTokenizerBytes tokenizer,
        uint identifier,
        bool extended,
        out byte[] frame)
    {
        frame = [];
        if (!_TryReadLengthPair(ref tokenizer, 15, 8, out int dlc, out int dataLen))
        {
            return false;
        }

        // The scratch is 8 bytes on the stack; dataLen ≤ 8 was checked by _TryReadLengthPair, so data[i] stays in range.
        Span<byte> data = stackalloc byte[_ClassicDataSize];
        data.Clear();
        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken)
                || !_TryParseHexByte(dataToken, out data[i]))
            {
                return false;
            }
        }

        // One column (not stored) precedes the flags column; its remote-request bit selects RTR.
        if (!tokenizer.TryNextToken(out _)
            || !tokenizer.TryNextToken(out ReadOnlySpan<byte> flagsToken)
            || !_TryParseHexUInt(flagsToken, out uint flags))
        {
            return false;
        }

        bool remote = (flags & _AscFRemote) != 0;
        int storedLen = Math.Min(dlc, _ClassicDataSize);
        uint socketId = identifier & _ExtendedIdMask;
        if (extended)
        {
            socketId |= _SocketCanEff;
        }

        if (remote)
        {
            socketId |= _SocketCanRtr;
            data.Clear();
        }

        frame = new byte[_ClassicHeaderSize + _ClassicDataSize];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketId);
        frame[4] = (byte)storedLen;
        data.CopyTo(frame.AsSpan(_ClassicHeaderSize));
        return true;
    }

    /// <summary>
    /// Parses the FBFF/FEFF tail into a SocketCAN FD frame whose length byte is the data-length column; the flags column
    /// must have the FD-format bit set, else the line is not an FD frame.
    /// </summary>
    private static bool _TryParseFd(
        ref AscTokenizerBytes tokenizer,
        uint identifier,
        bool extended,
        out byte[] frame)
    {
        frame = [];
        if (!_TryReadLengthPair(ref tokenizer, 15, _MaxFdData, out _, out int dataLen))
        {
            return false;
        }

        // dataLen ≤ 64 was checked by _TryReadLengthPair, so the 64-byte stack scratch cannot overflow. Not cleared:
        // only the first dataLen bytes are read, and each is written before.
        Span<byte> data = stackalloc byte[_MaxFdData];
        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken)
                || !_TryParseHexByte(dataToken, out data[i]))
            {
                return false;
            }
        }

        // Stuff-bit count, then FCRC. Both are ignored.
        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> flagsToken)
            || !_TryParseHexUInt(flagsToken, out uint flags)
            || (flags & _AscFFdf) == 0)
        {
            return false;
        }

        byte fdFlags = _SocketCanFdf;
        if ((flags & _AscFBrs) != 0)
        {
            fdFlags |= _SocketCanBrs;
        }

        if ((flags & _AscFEsi) != 0)
        {
            fdFlags |= _SocketCanEsi;
        }

        uint socketId = identifier & _ExtendedIdMask;
        if (extended)
        {
            socketId |= _SocketCanEff;
        }

        frame = new byte[_ClassicHeaderSize + dataLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketId);
        frame[4] = (byte)dataLen;
        frame[5] = fdFlags;
        data[..dataLen].CopyTo(frame.AsSpan(_ClassicHeaderSize));
        return true;
    }

    /// <summary>
    /// Reads DLC (hex) and data length (decimal). Skips one symbolic name when the first token is not a DLC.
    /// </summary>
    private static bool _TryReadLengthPair(
        ref AscTokenizerBytes tokenizer,
        int maxDlc,
        int maxData,
        out int dlc,
        out int dataLen)
    {
        dlc = 0;
        dataLen = 0;
        ReadOnlySpan<byte> saved = tokenizer.Capture();
        if (_TryLengthPair(ref tokenizer, maxDlc, maxData, out dlc, out dataLen))
        {
            return true;
        }

        tokenizer.Restore(saved);
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        return _TryLengthPair(ref tokenizer, maxDlc, maxData, out dlc, out dataLen);
    }

    /// <summary>
    /// Reads the DLC (hex, at most <paramref name="maxDlc"/>) and the data length (decimal, at most
    /// <paramref name="maxData"/>); the whole token must be consumed. The bounds protect the callers' fixed-size scratch.
    /// </summary>
    private static bool _TryLengthPair(
        ref AscTokenizerBytes tokenizer,
        int maxDlc,
        int maxData,
        out int dlc,
        out int dataLen)
    {
        dlc = 0;
        dataLen = 0;
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dlcToken)
            || !_TryParseHexUInt(dlcToken, out uint dlcValue)
            || dlcValue > (uint)maxDlc)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> lengthToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(lengthToken, out dataLen, out int consumed)
            || consumed != lengthToken.Length
            || dataLen < 0
            || dataLen > maxData)
        {
            return false;
        }

        dlc = (int)dlcValue;
        return true;
    }

    #endregion

    #region Helpers

    /// <summary>Reads the format token (the fifth token) of a <c>CANXL</c> line; it must be four characters long.</summary>
    private static bool _TryReadFormat(ReadOnlySpan<byte> line, out ReadOnlySpan<byte> format)
    {
        format = default;
        AscTokenizerBytes tokenizer = new(line);
        if (!tokenizer.TryNextToken(out _)
            || !tokenizer.TryNextToken(out ReadOnlySpan<byte> keyword)
            || !_TokenEquals(keyword, "CANXL"u8))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        return tokenizer.TryNextToken(out format) && format.Length == 4;
    }

    /// <summary>True for <c>Rx</c> and <c>Tx</c>; <c>TxRq</c> (a transmit request, not a frame) is deliberately excluded.</summary>
    private static bool _IsFrameDirection(ReadOnlySpan<byte> token) =>
        _TokenEquals(token, "Rx"u8) || _TokenEquals(token, "Tx"u8);

    private static bool _IsZeroOrOne(ReadOnlySpan<byte> token) =>
        token.Length == 1 && (token[0] == (byte)'0' || token[0] == (byte)'1');

    private static bool _IdFits(uint identifier, bool extended)
    {
        if (extended)
        {
            return identifier <= _ExtendedIdMask;
        }

        return identifier <= _StandardIdMask;
    }

    /// <summary>Parses a hexadecimal token without prefix; the whole token must be consumed.</summary>
    private static bool _TryParseHexUInt(ReadOnlySpan<byte> token, out uint value)
    {
        value = 0;
        return !token.IsEmpty
            && System.Buffers.Text.Utf8Parser.TryParse(token, out value, out int consumed, 'X')
            && consumed == token.Length;
    }

    private static bool _TryParseHexByte(ReadOnlySpan<byte> token, out byte value)
    {
        value = 0;
        if (!_TryParseHexUInt(token, out uint parsed) || parsed > 255)
        {
            return false;
        }

        value = (byte)parsed;
        return true;
    }

    private static bool _TokenEquals(ReadOnlySpan<byte> token, ReadOnlySpan<byte> ascii) =>
        token.Length == ascii.Length
        && AscLineClassifier.StartsWithAsciiIgnoreCase(token, ascii);

    #endregion
}
