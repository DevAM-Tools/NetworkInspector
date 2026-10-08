// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC CAN FD message line into a SocketCAN FD binary frame.
///
/// ASC CAN FD line format:
///   &lt;time&gt; CANFD &lt;channel&gt; &lt;dir&gt; &lt;id&gt;[x] [&lt;sym_name&gt;] &lt;brs&gt; &lt;esi&gt; &lt;dlc&gt; &lt;data_len&gt; &lt;data...&gt;
///
/// SocketCAN FD frame layout (8 header + up to 64 data):
///   [id(4BE) | payload length(1) | fd_flags(1) | reserved(2) | data(0–64)]
/// Byte 4 is the payload byte count (<c>canfd_frame.len</c>), not the ASC DLC code.
/// </summary>
internal static class AscCanFdParser
{
    #region Constants

    /// <summary>SocketCAN header: id(4) + length(1) + flags(1) + reserved(2).</summary>
    private const int _SocketCanHeaderSize = 8;

    /// <summary>Maximum CAN FD data length.</summary>
    private const int _MaxDataLength = 64;

    /// <summary>SocketCAN Extended Frame Format flag (bit 31).</summary>
    private const uint _SocketCanEff = 0x80000000;

    /// <summary>SocketCAN FD: FDF (FD Format indicator).</summary>
    private const byte _SocketCanFdFdf = 0x04;

    /// <summary>SocketCAN FD: BRS (Bit Rate Switch).</summary>
    private const byte _SocketCanFdBrs = 0x01;

    /// <summary>SocketCAN FD: ESI (Error State Indicator).</summary>
    private const byte _SocketCanFdEsi = 0x02;

    /// <summary>CAN FD DLC to data length mapping.</summary>
    private static ReadOnlySpan<byte> _DlcToLength =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    #endregion

    #region Public API

    /// <summary>
    /// Tries to parse an ASC CAN FD line and produce a SocketCAN FD binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">Parsed channel number.</param>
    /// <param name="frame">The resulting SocketCAN FD binary frame. Empty when this method returns <c>false</c>.</param>
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

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> canfdToken)
            || canfdToken.Length != 5
            || !AscLineClassifier.StartsWithAsciiIgnoreCase(canfdToken, "CANFD"u8))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Since v8.1 the token after the channel is the direction. Older lines put the id there.
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> afterChannel))
        {
            return false;
        }

        uint canId;
        bool isExtended;
        bool oldColumnOrder;
        if (_IsDirection(afterChannel))
        {
            oldColumnOrder = false;
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idOrError))
            {
                return false;
            }

            if (_TokenIs(idOrError, "ErrorFrame"u8))
            {
                frame = new byte[_SocketCanHeaderSize + 8];
                BinaryPrimitives.WriteUInt32BigEndian(frame, 0x20000000);
                return true;
            }

            if (!AscCanParser.TryParseCanId(idOrError, numericBase, out canId, out isExtended))
            {
                return false;
            }
        }
        else
        {
            oldColumnOrder = true;
            if (!AscCanParser.TryParseCanId(afterChannel, numericBase, out canId, out isExtended))
            {
                return false;
            }

            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction) || !_IsDirection(direction))
            {
                return false;
            }
        }

        ReadOnlySpan<byte> brsToken;
        if (oldColumnOrder)
        {
            if (!tokenizer.TryNextToken(out brsToken) || !_IsBoolToken(brsToken))
            {
                return false;
            }
        }
        else
        {
            // Optional symbolic name: skip tokens until BRS (0 or 1).
            while (true)
            {
                if (!tokenizer.TryNextToken(out brsToken))
                {
                    return false;
                }

                if (_IsBoolToken(brsToken))
                {
                    break;
                }
            }
        }

        bool hasBrs = brsToken[0] == (byte)'1';

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> esiToken) || !_IsBoolToken(esiToken))
        {
            return false;
        }

        bool hasEsi = esiToken[0] == (byte)'1';

        bool oldRemote = false;
        if (oldColumnOrder)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> marker)
                || marker.Length != 1
                || (marker[0] != (byte)'d' && marker[0] != (byte)'D' && marker[0] != (byte)'r' && marker[0] != (byte)'R'))
            {
                return false;
            }

            oldRemote = marker[0] == (byte)'r' || marker[0] == (byte)'R';
        }

        int dlc = 0;
        if (!oldRemote)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dlcToken)
                || !AscCanParser.TryParseDlc(dlcToken, numericBase, out dlc))
            {
                return false;
            }
        }

        int dataLen = 0;
        if (!oldRemote)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataLenToken)
                || !System.Buffers.Text.Utf8Parser.TryParse(dataLenToken, out dataLen, out int consumed)
                || consumed != dataLenToken.Length
                || dataLen < 0)
            {
                return false;
            }
        }

        dataLen = Math.Min(dataLen, _MaxDataLength);

        uint socketCanId = canId & 0x1FFFFFFF;
        if (isExtended)
        {
            socketCanId |= _SocketCanEff;
        }

        byte fdFlags = _SocketCanFdFdf;
        if (hasBrs)
        {
            fdFlags |= _SocketCanFdBrs;
        }

        if (hasEsi)
        {
            fdFlags |= _SocketCanFdEsi;
        }

        Span<byte> dataBytes = stackalloc byte[_MaxDataLength];
        dataBytes.Clear();
        int parsedCount = 0;

        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataToken))
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

        // Byte 4 is the payload count. The DLC table is an upper bound; the data-length column
        // and the bytes actually present are what SocketCAN stores.
        int frameDlc = Math.Min(dlc, 15);
        int fromTable = frameDlc < _DlcToLength.Length ? _DlcToLength[frameDlc] : dataLen;
        int socketDataLen = Math.Min(fromTable, dataLen);
        socketDataLen = Math.Min(socketDataLen, parsedCount);
        if (socketDataLen < 0)
        {
            socketDataLen = 0;
        }

        frame = new byte[_SocketCanHeaderSize + socketDataLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)socketDataLen;
        frame[5] = fdFlags;

        int copyLen = Math.Min(parsedCount, socketDataLen);
        dataBytes[..copyLen].CopyTo(frame.AsSpan(_SocketCanHeaderSize));
        return true;
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsBoolToken(ReadOnlySpan<byte> token) =>
        token.Length == 1 && (token[0] == (byte)'0' || token[0] == (byte)'1');

    private static bool _IsDirection(ReadOnlySpan<byte> token) =>
        (token.Length == 2 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "Rx"u8))
        || (token.Length == 2 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "Tx"u8))
        || (token.Length == 4 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "TxRq"u8));

    private static bool _TokenIs(ReadOnlySpan<byte> token, ReadOnlySpan<byte> expected) =>
        token.Length == expected.Length && AscLineClassifier.StartsWithAsciiIgnoreCase(token, expected);

    #endregion
}
