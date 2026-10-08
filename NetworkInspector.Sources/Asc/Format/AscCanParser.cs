// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC CAN classic message line into a SocketCAN binary frame.
///
/// ASC CAN line format:
///   &lt;time&gt; &lt;channel&gt; &lt;id&gt;[x] &lt;dir&gt; d|r &lt;dlc&gt; [&lt;data...&gt;]
///
/// SocketCAN frame layout (16 bytes):
///   [id(4BE) | payload length(1) | flags(1) | reserved(2) | data(8)]
/// Byte 4 is the payload byte count (0–8), not a DLC code above 8.
/// </summary>
internal static class AscCanParser
{
    #region Constants

    /// <summary>SocketCAN header: id(4) + length(1) + flags(1) + reserved(2).</summary>
    private const int _SocketCanHeaderSize = 8;

    /// <summary>Classic CAN maximum data length.</summary>
    private const int _MaxDataLength = 8;

    /// <summary>SocketCAN Extended Frame Format flag (bit 31).</summary>
    private const uint _SocketCanEff = 0x80000000;

    /// <summary>SocketCAN Remote Transmission Request flag (bit 30).</summary>
    private const uint _SocketCanRtr = 0x40000000;

    #endregion

    #region Public API

    /// <summary>
    /// Tries to parse an ASC CAN classic line and produce a SocketCAN binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec — from the file header.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">Parsed channel number.</param>
    /// <param name="frame">The resulting SocketCAN binary frame. Empty when this method returns <c>false</c>.</param>
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

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // The id column may be a number or a database name. A name is resolved from "ID = <decimal>".
        if (!_TryResolveCanId(ref tokenizer, numericBase, out uint canId, out bool isExtended, out ReadOnlySpan<byte> afterDirection))
        {
            return false;
        }

        AscTokenizerBytes body = new(afterDirection);
        if (!body.TryNextToken(out ReadOnlySpan<byte> typeToken))
        {
            return false;
        }

        bool isRemote = typeToken.Length == 1 && (typeToken[0] == (byte)'r' || typeToken[0] == (byte)'R');
        int storedLength;
        if (isRemote)
        {
            // Versions before 8.5 omit the DLC. "Length" / "ID" are not a DLC.
            storedLength = 0;
            if (body.TryNextToken(out ReadOnlySpan<byte> remoteDlc)
                && TryParseDlc(remoteDlc, numericBase, out int parsedRemoteDlc))
            {
                storedLength = Math.Min(parsedRemoteDlc, _MaxDataLength);
            }
        }
        else if (typeToken.Length != 1 || (typeToken[0] != (byte)'d' && typeToken[0] != (byte)'D'))
        {
            return false;
        }
        else if (!body.TryNextToken(out ReadOnlySpan<byte> dlcToken)
            || !TryParseDlc(dlcToken, numericBase, out int dlc))
        {
            return false;
        }
        else
        {
            // Classic SocketCAN stores the payload count. DLC above 8 still carries only 8 bytes.
            storedLength = Math.Min(dlc, _MaxDataLength);
        }

        uint socketCanId = canId & 0x1FFFFFFF;
        if (isExtended)
        {
            socketCanId |= _SocketCanEff;
        }

        if (isRemote)
        {
            socketCanId |= _SocketCanRtr;
        }

        Span<byte> dataBytes = stackalloc byte[_MaxDataLength];
        dataBytes.Clear();

        if (!isRemote)
        {
            for (int i = 0; i < storedLength; i++)
            {
                if (!body.TryNextToken(out ReadOnlySpan<byte> dataToken))
                {
                    break;
                }

                if (dataToken.Length > 2 && _IsAsciiLetter(dataToken[0]))
                {
                    break;
                }

                if (TryParseByte(dataToken, numericBase, out byte parsed))
                {
                    dataBytes[i] = parsed;
                }
                else
                {
                    break;
                }
            }
        }

        frame = new byte[_SocketCanHeaderSize + _MaxDataLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)storedLength;
        dataBytes.CopyTo(frame.AsSpan(_SocketCanHeaderSize));
        return true;
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseChannel(ReadOnlySpan<byte> token, out int channel)
        => System.Buffers.Text.Utf8Parser.TryParse(token, out channel, out _);

    internal static bool TryParseCanId(ReadOnlySpan<byte> token, int numericBase, out uint canId, out bool isExtended)
    {
        canId = 0;
        isExtended = false;

        if (token.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> idPart = token;
        if (token[^1] == (byte)'x' || token[^1] == (byte)'X')
        {
            isExtended = true;
            idPart = token[..^1];
        }

        if (numericBase == 16)
        {
            return System.Buffers.Text.Utf8Parser.TryParse(idPart, out canId, out int consumed, 'X')
                && consumed == idPart.Length;
        }

        if (System.Buffers.Text.Utf8Parser.TryParse(idPart, out int signed, out int consumedDec)
            && consumedDec == idPart.Length)
        {
            canId = (uint)signed;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseByte(ReadOnlySpan<byte> token, int numericBase, out byte value)
    {
        if (numericBase == 16)
        {
            if (System.Buffers.Text.Utf8Parser.TryParse(token, out uint parsed, out _, 'X') && parsed <= 255)
            {
                value = (byte)parsed;
                return true;
            }

            value = 0;
            return false;
        }

        return System.Buffers.Text.Utf8Parser.TryParse(token, out value, out _);
    }

    /// <summary>
    /// DLC is hex when the file base is hex and decimal when the file base is decimal.
    /// The value is a nibble, 0..15. The whole token must be consumed.
    /// </summary>
    internal static bool TryParseDlc(ReadOnlySpan<byte> token, int numericBase, out int dlc)
    {
        dlc = 0;
        if (token.IsEmpty)
        {
            return false;
        }

        if (numericBase == 16)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(token, out uint parsed, out int consumed, 'X')
                || consumed != token.Length
                || parsed > 15)
            {
                return false;
            }

            dlc = (int)parsed;
            return true;
        }

        if (!System.Buffers.Text.Utf8Parser.TryParse(token, out int parsedDec, out int consumedDec)
            || consumedDec != token.Length
            || parsedDec < 0
            || parsedDec > 15)
        {
            return false;
        }

        dlc = parsedDec;
        return true;
    }

    /// <summary>
    /// Walks to Rx, Tx, or TxRq. A numeric id column wins. Otherwise the decimal <c>ID =</c> field is used.
    /// </summary>
    private static bool _TryResolveCanId(
        ref AscTokenizerBytes tokenizer,
        int numericBase,
        out uint canId,
        out bool isExtended,
        out ReadOnlySpan<byte> afterDirection)
    {
        canId = 0;
        isExtended = false;
        afterDirection = [];
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> first))
        {
            return false;
        }

        if (_IsDirection(first))
        {
            return false;
        }

        if (TryParseCanId(first, numericBase, out canId, out isExtended))
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> direction) || !_IsDirection(direction))
            {
                return false;
            }

            afterDirection = tokenizer.Capture();
            return true;
        }

        ReadOnlySpan<byte> token = first;
        while (!_IsDirection(token))
        {
            if (!tokenizer.TryNextToken(out token))
            {
                return false;
            }
        }

        afterDirection = tokenizer.Capture();
        return _TryParseIdEquals(afterDirection, out canId, out isExtended);
    }

    private static bool _TryParseIdEquals(ReadOnlySpan<byte> span, out uint canId, out bool isExtended)
    {
        canId = 0;
        isExtended = false;
        AscTokenizerBytes scan = new(span);
        while (scan.TryNextToken(out ReadOnlySpan<byte> token))
        {
            if (token.Length != 2 || !AscLineClassifier.StartsWithAsciiIgnoreCase(token, "ID"u8))
            {
                continue;
            }

            if (!scan.TryNextToken(out ReadOnlySpan<byte> equals) || equals.Length != 1 || equals[0] != (byte)'=')
            {
                continue;
            }

            if (!scan.TryNextToken(out ReadOnlySpan<byte> number))
            {
                return false;
            }

            return _TryParseDecimalId(number, out canId, out isExtended);
        }

        return false;
    }

    /// <summary>
    /// The <c>ID =</c> field is decimal even when the file base is hex. A trailing <c>x</c> marks an extended id.
    /// </summary>
    private static bool _TryParseDecimalId(ReadOnlySpan<byte> token, out uint canId, out bool isExtended)
    {
        canId = 0;
        isExtended = false;
        if (token.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> digits = token;
        if (token[^1] == (byte)'x' || token[^1] == (byte)'X')
        {
            isExtended = true;
            digits = token[..^1];
        }

        return System.Buffers.Text.Utf8Parser.TryParse(digits, out canId, out int consumed)
            && consumed == digits.Length;
    }

    private static bool _IsDirection(ReadOnlySpan<byte> token) =>
        (token.Length == 2 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "Rx"u8))
        || (token.Length == 2 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "Tx"u8))
        || (token.Length == 4 && AscLineClassifier.StartsWithAsciiIgnoreCase(token, "TxRq"u8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(byte value) =>
        (value >= (byte)'A' && value <= (byte)'Z') || (value >= (byte)'a' && value <= (byte)'z');

    #endregion
}
