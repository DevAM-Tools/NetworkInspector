// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC CAN FD message line into a SocketCAN FD binary frame.
///
/// ASC CAN FD line format:
///   &lt;time&gt; CANFD &lt;channel&gt; &lt;dir&gt; &lt;id&gt;[x] [&lt;sym_name&gt;] &lt;brs&gt; &lt;esi&gt; &lt;dlc&gt; &lt;data_len&gt; &lt;data...&gt;
///
/// SocketCAN FD frame layout (8 header + up to 64 data):
///   [id(4BE) | dlc(1) | fd_flags(1) | reserved(2) | data(0–64)]
/// </summary>
internal static class AscCanFdParser
{
    #region Constants

    /// <summary>SocketCAN header: id(4) + dlc(1) + flags(1) + reserved(2).</summary>
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
    /// <param name="timestamp">Parsed timestamp in seconds.</param>
    /// <param name="channel">Parsed channel number.</param>
    /// <param name="frame">The resulting SocketCAN FD binary frame.</param>
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

        // Token 1: "CANFD" keyword — skip
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> canfdToken))
        {
            return false;
        }

        if (!canfdToken.Equals("CANFD", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Token 2: channel
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Token 3: direction (Rx/Tx) — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Token 4: CAN ID (with optional 'x' suffix)
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> idToken)
            || !AscCanParser.TryParseCanId(idToken, numericBase, out uint canId, out bool isExtended))
        {
            return false;
        }

        // Next tokens may be: [sym_name] <brs> <esi> <dlc> <data_len> <data...>
        // sym_name is optional and can be identified because it's not a simple "0"/"1" digit
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> nextToken))
        {
            return false;
        }

        // If the token is not "0" or "1", it might be a symbolic name — skip and get next
        ReadOnlySpan<char> brsToken;
        if (!_IsBoolToken(nextToken))
        {
            // This was a symbolic name, skip it
            if (!tokenizer.TryNextToken(out brsToken))
            {
                return false;
            }
        }
        else
        {
            brsToken = nextToken;
        }

        bool brs = brsToken.Length > 0 && brsToken[0] == '1';

        // ESI token
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> esiToken))
        {
            return false;
        }

        bool esi = esiToken.Length > 0 && esiToken[0] == '1';

        // DLC token
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dlcToken))
        {
            return false;
        }

        if (!int.TryParse(dlcToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dlc))
        {
            return false;
        }

        // Data length token
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataLenToken))
        {
            return false;
        }

        if (!int.TryParse(dataLenToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataLen))
        {
            return false;
        }

        dataLen = Math.Min(dataLen, _MaxDataLength);

        // Build SocketCAN ID
        uint socketCanId = canId & 0x1FFFFFFF;
        if (isExtended)
        {
            socketCanId |= _SocketCanEff;
        }

        // Build FD flags
        byte fdFlags = _SocketCanFdFdf; // Always set for FD frames
        if (brs)
        {
            fdFlags |= _SocketCanFdBrs;
        }
        if (esi)
        {
            fdFlags |= _SocketCanFdEsi;
        }

        // Parse data bytes — use stackalloc to avoid a heap allocation for temporary storage
        Span<byte> dataBytes = stackalloc byte[_MaxDataLength];
        dataBytes.Clear();
        int parsedCount = 0;
        for (int i = 0; i < dataLen; i++)
        {
            if (!tokenizer.TryNextToken(out ReadOnlySpan<char> dataToken))
            {
                break;
            }

            // Stop at metadata tokens (e.g., "MessageDuration", "MessageLength")
            // In hex mode, valid bytes like AA, BB start with letters too,
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

        // Build frame — DLC-based sizing for SocketCAN compatibility
        int frameDlc = Math.Min(dlc, 15);
        int socketDataLen = frameDlc < _DlcToLength.Length ? _DlcToLength[frameDlc] : dataLen;
        socketDataLen = Math.Max(socketDataLen, dataLen);
        socketDataLen = Math.Min(socketDataLen, _MaxDataLength);

        frame = new byte[_SocketCanHeaderSize + socketDataLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)dlc;
        frame[5] = fdFlags;
        frame[6] = 0; // reserved
        frame[7] = 0; // reserved

        // Copy parsed data
        int copyLen = Math.Min(parsedCount, socketDataLen);
        dataBytes.Slice(0, copyLen).CopyTo(frame.AsSpan(_SocketCanHeaderSize));

        return true;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Checks if a token is a boolean value (single digit "0" or "1").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsBoolToken(ReadOnlySpan<char> token) =>
        token.Length == 1 && (token[0] == '0' || token[0] == '1');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsBoolToken(ReadOnlySpan<byte> token) =>
        token.Length == 1 && (token[0] == (byte)'0' || token[0] == (byte)'1');

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

        // "CANFD" keyword
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> canfdToken))
        {
            return false;
        }

        if (!AscLineClassifier.StartsWithAsciiIgnoreCase(canfdToken, "CANFD"u8) || canfdToken.Length != 5)
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // direction — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> idToken)
            || !AscCanParser.TryParseCanId(idToken, numericBase, out uint canId, out bool isExtended))
        {
            return false;
        }

        // Optional symbolic name: skip tokens until we get BRS (bool)
        ReadOnlySpan<byte> brsToken;
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

        bool hasBrs = brsToken[0] == (byte)'1';

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> esiToken) || !_IsBoolToken(esiToken))
        {
            return false;
        }

        bool hasEsi = esiToken[0] == (byte)'1';

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dlcToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(dlcToken, out int dlc, out _))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> dataLenToken)
            || !System.Buffers.Text.Utf8Parser.TryParse(dataLenToken, out int dataLen, out _))
        {
            return false;
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

        int frameDlc = Math.Min(dlc, 15);
        int socketDataLen = frameDlc < _DlcToLength.Length ? _DlcToLength[frameDlc] : dataLen;
        socketDataLen = Math.Max(socketDataLen, dataLen);
        socketDataLen = Math.Min(socketDataLen, _MaxDataLength);

        frame = new byte[_SocketCanHeaderSize + socketDataLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)dlc;
        frame[5] = fdFlags;
        frame[6] = 0;
        frame[7] = 0;

        int copyLen = Math.Min(parsedCount, socketDataLen);
        dataBytes.Slice(0, copyLen).CopyTo(frame.AsSpan(_SocketCanHeaderSize));

        return true;
    }

    #endregion
}
