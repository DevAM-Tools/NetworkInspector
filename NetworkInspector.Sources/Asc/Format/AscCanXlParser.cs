// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses a Vector ASC <c>CANXL</c> line (logging spec 1.4.17 / linux-can <c>eval_canxl</c>)
/// into the compact SocketCAN buffer used by the CAN XL dissector.
/// Channel and the decimal data-length column are decimal. Every other numeric column is hex,
/// even when the file header says <c>base dec</c>.
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscCanXlParser
{
    #region Constants

    private const int _ClassicHeaderSize = 8;
    private const int _XlHeaderSize = 12;
    private const int _ClassicDataSize = 8;
    private const int _MaxFdData = 64;
    private const int _MaxXlData = 2048;
    private const uint _SocketCanEff = 0x80000000u;
    private const uint _SocketCanRtr = 0x40000000u;
    private const byte _SocketCanXlf = 0x80;
    private const byte _SocketCanSec = 0x01;
    private const byte _SocketCanRrs = 0x02;
    private const byte _SocketCanFdf = 0x04;
    private const byte _SocketCanBrs = 0x01;
    private const byte _SocketCanEsi = 0x02;
    private const uint _AscFXlf = 0x00400000u;
    private const uint _AscFFdf = 0x00001000u;
    private const uint _AscFBrs = 0x00002000u;
    private const uint _AscFEsi = 0x00004000u;
    private const uint _AscFSec = 0x01000000u;
    private const uint _AscFRrs = 0x00800000u;
    private const uint _AscFRemote = 0x10u;
    private const uint _StandardIdMask = 0x7FFu;
    private const uint _ExtendedIdMask = 0x1FFFFFFFu;

    #endregion

    #region Public API

    /// <summary>
    /// Reads the format token and maps it to the bus type used for interface registration.
    /// <c>XLFF</c> is <see cref="AscBusType.CanXl"/> so export can tell it from CAN FD.
    /// </summary>
    internal static bool TryPeekBusType(ReadOnlySpan<byte> line, out AscBusType busType)
    {
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

        if (!tokenizer.TryNextToken(out _) || !tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idToken)
            || !_TryParseHexUInt(idToken, out uint identifier))
        {
            return false;
        }

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
        if (dataLen < 1 || dataLen > _MaxXlData || dataLen != (int)dlc + 1)
        {
            return false;
        }

        retryName = false;

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

        frame = new byte[_XlHeaderSize + dataLen];
        uint word = ((uint)vcid << 16) | (priority & _StandardIdMask);
        BinaryPrimitives.WriteUInt32BigEndian(frame, word);
        frame[5] = sdt;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)dataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), acceptanceField);

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
