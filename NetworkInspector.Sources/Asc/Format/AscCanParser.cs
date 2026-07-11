// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC CAN classic message line into a SocketCAN binary frame.
///
/// ASC CAN line format:
///   &lt;time&gt; &lt;channel&gt; &lt;id&gt;[x] &lt;dir&gt; d|r &lt;dlc&gt; [&lt;data...&gt;]
///
/// SocketCAN frame layout (16 bytes):
///   [id(4BE) | dlc(1) | fd_flags(1) | reserved(2) | data(8)]
/// </summary>
internal static class AscCanParser
{
    #region Constants

    /// <summary>SocketCAN header: id(4) + dlc(1) + flags(1) + reserved(2).</summary>
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
    /// <param name="timestamp">Parsed timestamp in seconds.</param>
    /// <param name="channel">Parsed channel number.</param>
    /// <param name="frame">The resulting SocketCAN binary frame.</param>
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
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> tsToken))
        {
            return false;
        }

        if (!TryParseTimestamp(tsToken, out timestamp))
        {
            return false;
        }

        // Token 1: channel
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> chToken))
        {
            return false;
        }

        if (!TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Token 2: CAN ID (with optional 'x' suffix for extended)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> idToken))
        {
            return false;
        }

        if (!TryParseCanId(idToken, numericBase, out uint canId, out bool isExtended))
        {
            return false;
        }

        // Token 3: direction (Rx/Tx) — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Token 4: frame type (d = data, r = remote)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> typeToken))
        {
            return false;
        }

        bool isRemote = typeToken.Length > 0 && (typeToken[0] == 'r' || typeToken[0] == 'R');

        // Token 5: DLC
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dlcToken))
        {
            return false;
        }

        if (!int.TryParse(dlcToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dlc))
        {
            return false;
        }

        // Clamp DLC for classic CAN
        int dataLength = Math.Min(dlc, _MaxDataLength);

        // Build the SocketCAN ID with flags
        uint socketCanId = canId & 0x1FFFFFFF;
        if (isExtended)
        {
            socketCanId |= _SocketCanEff;
        }
        if (isRemote)
        {
            socketCanId |= _SocketCanRtr;
        }

        // Parse data bytes
        Span<byte> dataBytes = stackalloc byte[_MaxDataLength];
        dataBytes.Clear();

        int parsedDataCount = 0;
        for (int i = 0; i < dataLength; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataToken))
            {
                break; // RTR frames may have no data bytes
            }

            // Stop at metadata tokens like "Length", "BitCount", "ID"
            // In hex mode, valid data bytes (AA, BB, etc.) start with letters too,
            // so only treat longer tokens starting with a letter as metadata.
            if (dataToken.Length > 2 && char.IsLetter(dataToken[0]))
            {
                break;
            }

            if (TryParseByte(dataToken, numericBase, out byte b))
            {
                dataBytes[i] = b;
                parsedDataCount++;
            }
            else
            {
                break;
            }
        }

        // For remote frames, data length is from DLC but no actual data
        if (isRemote)
        {
            parsedDataCount = 0;
        }

        // Build frame
        frame = new byte[_SocketCanHeaderSize + _MaxDataLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)dlc;
        frame[5] = 0; // no FD flags for classic CAN
        frame[6] = 0; // reserved
        frame[7] = 0; // reserved

        // Copy data (already padded to 8 by stackalloc clear)
        dataBytes[.._MaxDataLength].CopyTo(frame.AsSpan(_SocketCanHeaderSize));

        return true;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Parses a timestamp from ASC format (decimal seconds, e.g., "1.000000").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseTimestamp(ReadOnlySpan<char> token, out double timestamp) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp);

    /// <summary>
    /// Parses a channel number from the channel token.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseChannel(ReadOnlySpan<char> token, out int channel) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out channel);

    /// <summary>
    /// Parses a CAN ID with optional 'x' suffix for extended frames.
    /// </summary>
    internal static bool TryParseCanId(ReadOnlySpan<char> token, int numericBase, out uint canId, out bool isExtended)
    {
        canId = 0;
        isExtended = false;

        if (token.IsEmpty)
        {
            return false;
        }

        // Check for 'x' or 'X' suffix → extended frame
        ReadOnlySpan<char> idPart = token;
        if (token[^1] == 'x' || token[^1] == 'X')
        {
            isExtended = true;
            idPart = token[..^1];
        }

        NumberStyles style = numericBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
        return uint.TryParse(idPart, style, CultureInfo.InvariantCulture, out canId);
    }

    /// <summary>
    /// Parses a single data byte in the configured numeric base.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseByte(ReadOnlySpan<char> token, int numericBase, out byte value)
    {
        if (token.Length <= 256)
        {
            Span<byte> utf8 = stackalloc byte[token.Length];
            for (int i = 0; i < token.Length; i++)
            {
                char ch = token[i];
                if (ch > 127)
                {
                    NumberStyles style = numericBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
                    return byte.TryParse(token, style, CultureInfo.InvariantCulture, out value);
                }

                utf8[i] = (byte)ch;
            }

            return TryParseByte(utf8, numericBase, out value);
        }

        NumberStyles style2 = numericBase == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
        return byte.TryParse(token, style2, CultureInfo.InvariantCulture, out value);
    }

    #endregion

    #region Byte-span overloads (zero-allocation path)

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
            || !TryParseTimestamp(tsToken, out timestamp))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !TryParseChannel(chToken, out channel))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idToken)
            || !TryParseCanId(idToken, numericBase, out uint canId, out bool isExtended))
        {
            return false;
        }

        // direction — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // frame type: d = data, r = remote
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> typeToken))
        {
            return false;
        }

        bool isRemote = typeToken.Length > 0 && (typeToken[0] == (byte)'r' || typeToken[0] == (byte)'R');

        // DLC
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dlcToken)
            || !_TryParseInt(dlcToken, out int dlc))
        {
            return false;
        }

        int dataLength = Math.Min(dlc, _MaxDataLength);
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

                if (TryParseByte(dataToken, numericBase, out byte b))
                {
                    dataBytes[i] = b;
                }
                else
                {
                    break;
                }
            }
        }

        frame = new byte[_SocketCanHeaderSize + _MaxDataLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)dlc;
        frame[5] = 0;
        frame[6] = 0;
        frame[7] = 0;
        dataBytes[.._MaxDataLength].CopyTo(frame.AsSpan(_SocketCanHeaderSize));

        return true;
    }

    // ── Byte-span helpers ────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseTimestamp(ReadOnlySpan<byte> token, out double timestamp)
        // Utf8Parser.TryParse works on ASCII-encoded floats (same encoding as UTF-8 for digits)
        => System.Buffers.Text.Utf8Parser.TryParse(token, out timestamp, out _);

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
            return System.Buffers.Text.Utf8Parser.TryParse(idPart, out canId, out _, 'X');
        }

        // Decimal base: Utf8Parser returns int, cast to uint
        if (System.Buffers.Text.Utf8Parser.TryParse(idPart, out int signed, out _))
        {
            canId = (uint)signed;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParseByte(ReadOnlySpan<byte> token, int numericBase, out byte value)
    {
        // Parse as uint first (Utf8Parser.TryParse for byte uses 'G' format = decimal only)
        if (numericBase == 16)
        {
            if (System.Buffers.Text.Utf8Parser.TryParse(token, out uint u, out _, 'X') && u <= 255)
            {
                value = (byte)u;
                return true;
            }

            value = 0;
            return false;
        }

        return System.Buffers.Text.Utf8Parser.TryParse(token, out value, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _TryParseInt(ReadOnlySpan<byte> token, out int value)
        => System.Buffers.Text.Utf8Parser.TryParse(token, out value, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsAsciiLetter(byte b) => (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');

    #endregion
}
