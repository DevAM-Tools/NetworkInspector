// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC CAN classic message line into a SocketCAN binary frame.
///
/// ASC CAN line format:
///   &lt;time&gt; &lt;channel&gt; &lt;id&gt;[x] &lt;dir&gt; d|r &lt;dlc&gt; [&lt;data...&gt;]
///
/// SocketCAN frame layout (16 bytes):
///   [id(4BE) | payload length(1) | flags(1) | reserved(1) | len8_dlc(1) | data(8)]
/// Byte 4 is the payload byte count (0–8); a DLC of 9–15 is kept in byte 7 (<c>len8_dlc</c>), as the BLF reader does.
/// The id word is big-endian (the layout of the SocketCAN link type used in capture files), with EFF/RTR/ERR in bits 31/30/29.
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscCanParser
{
    #region Constants

    /// <summary>SocketCAN header: id(4) + length(1) + flags(1) + reserved(1) + len8_dlc(1).</summary>
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
        => FrameSlabAllocator.ToArray(
            TryParse(line, numericBase, null, out timestamp, out channel, out ReadOnlyMemory<byte> memory), memory, out frame);

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, int, out AscTimestamp, out int, out byte[])"/>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="numericBase">16 for hex, 10 for dec — from the file header.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="timestamp">Parsed line timestamp. Not scaled to Unix time.</param>
    /// <param name="channel">Parsed channel number.</param>
    /// <param name="frame">The resulting SocketCAN binary frame. Empty when this method returns <c>false</c>.</param>
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

        // Frame type token: "d" = data frame, "r" = remote request. storedLength is the byte count written to the SocketCAN
        // length byte; busDlc is the DLC as the bus carried it (it can be 9-15, which classic CAN still allows).
        bool isRemote = typeToken.Length == 1 && (typeToken[0] == (byte)'r' || typeToken[0] == (byte)'R');
        int storedLength;
        int busDlc;
        if (isRemote)
        {
            // Versions before 8.5 omit the DLC. "Length" / "ID" are not a DLC.
            storedLength = 0;
            busDlc = 0;
            if (body.TryNextToken(out ReadOnlySpan<byte> remoteDlc)
                && TryParseDlc(remoteDlc, numericBase, out int parsedRemoteDlc))
            {
                storedLength = Math.Min(parsedRemoteDlc, _MaxDataLength);
                busDlc = parsedRemoteDlc;
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
            busDlc = dlc;
        }

        // Compose the SocketCAN id word: the identifier is masked to 29 bits (CAN_EFF_MASK) so that a malformed or negative
        // id column cannot set the EFF/RTR/ERR flag bits; the flags come only from the line's x suffix and frame type.
        uint socketCanId = canId & 0x1FFFFFFF;
        if (isExtended)
        {
            socketCanId |= _SocketCanEff;
        }

        if (isRemote)
        {
            socketCanId |= _SocketCanRtr;
        }

        // Payload bytes go to a stack scratch first (8 bytes, no allocation); the frame is rented only after the line is
        // fully validated, so a rejected line leaves the slab untouched. A remote frame has a length but no data tokens.
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

                // A word longer than a byte token that starts with a letter is the trailing attribute list
                // (Length = ..., BitCount = ...) of a line with fewer data bytes than its DLC says: stop, keep zeros.
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

        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, _SocketCanHeaderSize + _MaxDataLength);
        Span<byte> socketCan = buffer.Span;
        BinaryPrimitives.WriteUInt32BigEndian(socketCan, socketCanId);
        socketCan[BlfConstants.SocketCanLenOffset] = (byte)storedLength;

        // A bus DLC of 9-15 goes into len8_dlc (byte 7), the convention all readers and writers of this product share.
        socketCan[BlfConstants.SocketCanLen8DlcOffset] = busDlc is > _MaxDataLength and <= 15 ? (byte)busDlc : (byte)0;
        dataBytes.CopyTo(socketCan[_SocketCanHeaderSize..]);
        frame = buffer;
        return true;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Parses a decimal channel token. Shared by the CAN, CAN FD and error parsers. Trailing characters after the number are
    /// not rejected (the consumed count is ignored).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseChannel(ReadOnlySpan<byte> token, out int channel)
        => System.Buffers.Text.Utf8Parser.TryParse(token, out channel, out _);

    /// <summary>
    /// Parses the numeric id column. A trailing <c>x</c> marks an extended (29-bit) id. The number is hexadecimal when the
    /// file header says <c>base hex</c> (<paramref name="numericBase"/> 16), else decimal; the whole token must be consumed.
    /// </summary>
    /// <param name="token">The id column.</param>
    /// <param name="numericBase">16 for hex, 10 for dec — from the file header.</param>
    /// <param name="canId">The identifier without flags. Zero when the method returns false; a negative decimal value is rejected.</param>
    /// <param name="isExtended">True when the token ended in <c>x</c>.</param>
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

        // A negative decimal id is rejected: the cast to uint used to wrap it into a huge unsigned value that the later
        // masking turned into a valid-looking id, so a corrupt column became a frame with the wrong identifier.
        if (System.Buffers.Text.Utf8Parser.TryParse(idPart, out int signed, out int consumedDec)
            && consumedDec == idPart.Length
            && signed >= 0)
        {
            canId = (uint)signed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses one data byte token: hexadecimal (<paramref name="numericBase"/> 16, at most 255) or decimal (a <see cref="byte"/>).
    /// Per-byte call inside the payload loop, hence inlined; <c>Utf8Parser</c> works on the bytes without a string.
    /// </summary>
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
