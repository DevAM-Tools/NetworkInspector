// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Parser;

/// <summary>
/// Converts literal tokens into <see cref="FieldValueData"/> values and <c>within:</c> tokens
/// into <see cref="FlankWindow"/> values.
/// <para>
/// Literals reuse <see cref="FieldValueData"/> so comparisons against packet field values go
/// through the same cross-type ordering the core already implements, instead of a parallel
/// value union.
/// </para>
/// </summary>
internal static class LiteralParser
{
    #region Literals

    /// <summary>Parses a literal token into a field value.</summary>
    public static FilterResult<FieldValueData> Parse(in Token token)
    {
        switch (token.Kind)
        {
            case TokenKind.Integer:
                return _ParseInteger(token);

            case TokenKind.Float:
                if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    return FieldValueData.NewF64(d);
                }
                return FilterError.InvalidValue($"Invalid number '{token.Text}'", token.Position, token.Length);

            case TokenKind.StringLiteral:
                return FieldValueData.NewString(token.Text);

            case TokenKind.True:
                return FieldValueData.NewBool(true);

            case TokenKind.False:
                return FieldValueData.NewBool(false);

            case TokenKind.MacAddress:
                if (MacAddress.TryParse(token.Text, out MacAddress mac))
                {
                    return FieldValueData.NewMacAddress(mac);
                }
                return FilterError.InvalidValue($"Invalid MAC address '{token.Text}'", token.Position, token.Length);

            case TokenKind.Ipv4Address:
                if (IPv4Address.TryParse(token.Text, out IPv4Address ipv4))
                {
                    return FieldValueData.NewIPv4(ipv4);
                }
                return FilterError.InvalidValue($"Invalid IPv4 address '{token.Text}'", token.Position, token.Length);

            case TokenKind.Ipv6Address:
                if (IPv6Address.TryParse(token.Text, out IPv6Address ipv6))
                {
                    return FieldValueData.NewIPv6(ipv6);
                }
                return FilterError.InvalidValue($"Invalid IPv6 address '{token.Text}'", token.Position, token.Length);

            case TokenKind.HexBytes:
                return _ParseHexBytes(token);

            default:
                return FilterError.Syntax(
                    $"Expected a literal value but found '{token.Text}'",
                    token.Position,
                    Math.Max(token.Length, 1));
        }
    }

    /// <summary>Whether <paramref name="kind"/> can start a literal value.</summary>
    public static bool IsLiteralStart(TokenKind kind) => kind
        is TokenKind.Integer
        or TokenKind.Float
        or TokenKind.StringLiteral
        or TokenKind.True
        or TokenKind.False
        or TokenKind.MacAddress
        or TokenKind.Ipv4Address
        or TokenKind.Ipv6Address
        or TokenKind.HexBytes;

    #endregion

    #region Flank window

    /// <summary>Parses the <c>within:</c> argument of a flank expression.</summary>
    public static FilterResult<FlankWindow> ParseWindow(in Token token)
    {
        if (token.Kind != TokenKind.Duration)
        {
            return FilterError.Syntax(
                $"'within:' expects a duration such as 5s, 100ms or 10packets, but found '{token.Text}'",
                token.Position,
                Math.Max(token.Length, 1));
        }

        string text = token.Text;
        int suffixStart = text.Length;
        while (suffixStart > 0 && !char.IsAsciiDigit(text[suffixStart - 1]))
        {
            suffixStart--;
        }

        string numberPart = text[..suffixStart];
        string suffix = text[suffixStart..];

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
            || amount < 0)
        {
            return FilterError.InvalidValue($"Invalid window amount '{text}'", token.Position, token.Length);
        }

        if (suffix is "packet" or "packets")
        {
            if (amount != Math.Floor(amount))
            {
                return FilterError.InvalidValue($"Invalid window amount '{text}'", token.Position, token.Length);
            }

            if (amount > ArrayIndexIdRange.MaxCount)
            {
                return FilterError.InvalidValue(
                    $"Packet window must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)}",
                    token.Position,
                    token.Length);
            }

            return FlankWindow.FromPackets((int)amount);
        }

        double nanosPerUnit = suffix switch
        {
            "ns" => 1d,
            "us" => 1_000d,
            "ms" => 1_000_000d,
            "s" => 1_000_000_000d,
            "m" => 60d * 1_000_000_000d,
            "h" => 3_600d * 1_000_000_000d,
            _ => -1d,
        };

        if (nanosPerUnit < 0)
        {
            return FilterError.InvalidValue($"Unknown duration unit '{suffix}'", token.Position, token.Length);
        }

        return FlankWindow.FromNanoseconds((long)(amount * nanosPerUnit));
    }

    #endregion

    #region Helpers

    private static FilterResult<FieldValueData> _ParseInteger(in Token token)
    {
        string text = token.Text.Replace("_", string.Empty, StringComparison.Ordinal);

        if (text.StartsWith('-'))
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
            {
                return FieldValueData.NewI64(signed);
            }

            return FilterError.InvalidValue($"Invalid integer '{token.Text}'", token.Position, token.Length);
        }

        if (text.Length > 2 && text[0] == '0')
        {
            char prefix = char.ToLowerInvariant(text[1]);
            string digits = text[2..];
            if (prefix == 'x')
            {
                return _ParseRadix(token, digits, 16);
            }
            if (prefix == 'b')
            {
                return _ParseRadix(token, digits, 2);
            }
            if (prefix == 'o')
            {
                return _ParseRadix(token, digits, 8);
            }
        }

        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
        {
            return FieldValueData.NewU64(value);
        }
        return FilterError.InvalidValue($"Invalid integer '{token.Text}'", token.Position, token.Length);
    }

    /// <summary>
    /// Converts the digits after a <c>0x</c>/<c>0b</c>/<c>0o</c> prefix. The caller guarantees at
    /// least one digit because it only takes this path when the token is longer than the prefix.
    /// </summary>
    private static FilterResult<FieldValueData> _ParseRadix(in Token token, string digits, int radix)
    {
        ulong result = 0;
        foreach (char c in digits)
        {
            int digit = _HexValue(c);
            if (digit < 0 || digit >= radix)
            {
                return FilterError.InvalidValue($"Invalid integer '{token.Text}'", token.Position, token.Length);
            }
            if (result > (ulong.MaxValue - (ulong)digit) / (ulong)radix)
            {
                return FilterError.InvalidValue($"Integer '{token.Text}' does not fit in 64 bits", token.Position, token.Length);
            }
            result = (result * (ulong)radix) + (ulong)digit;
        }

        return FieldValueData.NewU64(result);
    }

    private static FilterResult<FieldValueData> _ParseHexBytes(in Token token)
    {
        string text = token.Text;
        int groups = 1;
        foreach (char c in text)
        {
            if (c == ':')
            {
                groups++;
            }
        }

        byte[] bytes = new byte[groups];
        int written = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            if (pos + 1 >= text.Length || written >= bytes.Length)
            {
                return FilterError.InvalidValue($"Invalid byte sequence '{text}'", token.Position, token.Length);
            }
            int high = _HexValue(text[pos]);
            int low = _HexValue(text[pos + 1]);
            if (high < 0 || low < 0)
            {
                return FilterError.InvalidValue($"Invalid byte sequence '{text}'", token.Position, token.Length);
            }
            bytes[written++] = (byte)((high << 4) | low);
            pos += 2;
            if (pos < text.Length && text[pos] == ':')
            {
                pos++;
            }
        }

        return FieldValueData.NewBytes(bytes);
    }

    private static int _HexValue(char c)
    {
        if (char.IsAsciiDigit(c))
        {
            return c - '0';
        }
        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }

    #endregion
}
