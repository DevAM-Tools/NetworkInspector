// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses an ASC Ethernet packet line into a raw Ethernet frame.
///
/// ASC Ethernet line format:
///   &lt;time&gt; ETH|AFDX &lt;channel&gt; &lt;dir&gt; [&lt;flags...&gt;] &lt;data_len&gt;:&lt;hex_data_continuous&gt;
///
/// The hex data is the raw Ethernet frame starting from destination MAC.
/// LinkType: Ethernet (1).
/// </summary>
internal static class AscEthernetParser
{
    #region Public API

    /// <summary>
    /// Tries to parse an ASC Ethernet line and produce a raw Ethernet binary frame.
    /// </summary>
    /// <param name="line">The full trimmed ASC line (including timestamp).</param>
    /// <param name="timestamp">Parsed timestamp in seconds.</param>
    /// <param name="channel">Parsed Ethernet channel number.</param>
    /// <param name="frame">The resulting raw Ethernet frame bytes.</param>
    /// <returns><c>true</c> if parsing succeeded.</returns>
    internal static bool TryParse(
        ReadOnlySpan<char> line,
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

        // Token 1: "ETH" or "AFDX" keyword
        if (!tokenizer.TryNextToken(out ReadOnlySpan<char> busToken))
        {
            return false;
        }

        if (!busToken.Equals("ETH", StringComparison.OrdinalIgnoreCase)
            && !busToken.Equals("AFDX", StringComparison.OrdinalIgnoreCase))
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

        // Remaining tokens: skip optional numeric fields until we find "<len>:<hex_data>"
        // The data is identified by the colon separating length from hex data
        ReadOnlySpan<char> remaining = tokenizer.Remaining;

        // Find the "<len>:<data>" pattern — scan tokens for one containing ':'
        while (true)
        {
            // Find colon in remaining text
            int colonIdx = remaining.IndexOf(':');
            if (colonIdx < 0)
            {
                return false;
            }

            // Everything before the colon should be a numeric length
            // Everything after the colon is continuous hex data
            ReadOnlySpan<char> beforeColon = remaining[..colonIdx].TrimEnd();
            ReadOnlySpan<char> afterColon = remaining[(colonIdx + 1)..].TrimStart();

            // Find the end of the data length token (last whitespace before colon)
            int lastSpaceBefore = beforeColon.LastIndexOfAny(' ', '\t');
            ReadOnlySpan<char> lenToken = lastSpaceBefore >= 0
                ? beforeColon[(lastSpaceBefore + 1)..]
                : beforeColon;

            if (!int.TryParse(lenToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataLen))
            {
                remaining = remaining[(colonIdx + 1)..];
                continue;
            }

            // The hex data is a continuous string (no spaces) starting after the colon
            // Find the end of hex data (next whitespace or end of line)
            int endIdx = afterColon.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> hexData = endIdx >= 0 ? afterColon[..endIdx] : afterColon;

            if (hexData.IsEmpty)
            {
                return false;
            }

            // Decode hex string into byte array
            // Each byte = 2 hex chars
            int hexByteCount = hexData.Length / 2;
            int actualLen = Math.Min(dataLen, hexByteCount);

            if (actualLen <= 0)
            {
                return false;
            }

            frame = new byte[actualLen];
            for (int i = 0; i < actualLen; i++)
            {
                ReadOnlySpan<char> hexByte = hexData.Slice(i * 2, 2);
                if (!byte.TryParse(hexByte, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out frame[i]))
                {
                    frame = [];
                    return false;
                }
            }

            return true;
        }
    }

    #endregion

    #region Byte-span overload (zero-allocation path)

    /// <summary>
    /// Byte-span overload of <see cref="TryParse(ReadOnlySpan{char}, out double, out int, out byte[])"/>.
    /// Works directly on raw ASCII bytes without converting to a <see cref="string"/>.
    /// </summary>
    internal static bool TryParse(
        ReadOnlySpan<byte> line,
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

        // "ETH" or "AFDX" keyword
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> busToken))
        {
            return false;
        }

        if (!AscLineClassifier.StartsWithAsciiIgnoreCase(busToken, "ETH"u8)
            && !AscLineClassifier.StartsWithAsciiIgnoreCase(busToken, "AFDX"u8))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> chToken)
            || !AscCanParser.TryParseChannel(chToken, out channel))
        {
            return false;
        }

        // Direction — skip
        if (!tokenizer.TryNextToken(out _))
        {
            return false;
        }

        // Scan for "<len>:<hex_data>" pattern
        ReadOnlySpan<byte> remaining = tokenizer.Remaining;

        while (true)
        {
            int colonIdx = remaining.IndexOf((byte)':');
            if (colonIdx < 0)
            {
                return false;
            }

            ReadOnlySpan<byte> beforeColon = AscTokenizerBytes.TrimEndAscii(remaining[..colonIdx]);
            ReadOnlySpan<byte> afterColon = AscTokenizerBytes.TrimStartAscii(remaining[(colonIdx + 1)..]);

            // Find last whitespace before colon to isolate the length token
            int lastSpaceBefore = -1;
            for (int k = beforeColon.Length - 1; k >= 0; k--)
            {
                if (beforeColon[k] == (byte)' ' || beforeColon[k] == (byte)'\t')
                {
                    lastSpaceBefore = k;
                    break;
                }
            }

            ReadOnlySpan<byte> lenToken = lastSpaceBefore >= 0
                ? beforeColon[(lastSpaceBefore + 1)..]
                : beforeColon;

            if (!System.Buffers.Text.Utf8Parser.TryParse(lenToken, out int dataLen, out _))
            {
                // Not a valid length token — skip past this colon
                remaining = remaining[(colonIdx + 1)..];
                continue;
            }

            // Hex data: continuous string after the colon until whitespace
            int endIdx = -1;
            for (int k = 0; k < afterColon.Length; k++)
            {
                if (afterColon[k] == (byte)' ' || afterColon[k] == (byte)'\t')
                {
                    endIdx = k;
                    break;
                }
            }

            ReadOnlySpan<byte> hexData = endIdx >= 0 ? afterColon[..endIdx] : afterColon;

            if (hexData.IsEmpty)
            {
                return false;
            }

            int hexByteCount = hexData.Length / 2;
            int actualLen = Math.Min(dataLen, hexByteCount);

            if (actualLen <= 0)
            {
                return false;
            }

            frame = new byte[actualLen];
            for (int i = 0; i < actualLen; i++)
            {
                ReadOnlySpan<byte> hexByte = hexData.Slice(i * 2, 2);
                if (!System.Buffers.Text.Utf8Parser.TryParse(hexByte, out uint u, out _, 'X') || u > 255)
                {
                    frame = [];
                    return false;
                }

                frame[i] = (byte)u;
            }

            return true;
        }
    }

    #endregion
}
