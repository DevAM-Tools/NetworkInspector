// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Helpers for emitting Wireshark UAT-backed tables (<c>epan/uat_load.l</c>): comma-separated
/// records where each field is either a <c>binstring</c> (even-length ASCII hex digit pairs) or a
/// <c>quoted_string</c>. Unquoted alphanumerics are parsed as hex pairs, so booleans, decimals,
/// and PDU names must be wrapped in double quotes.
/// </summary>
internal static class WiresharkCsvUat
{
    /// <summary>
    /// Wireshark resolves profile UAT paths via <c>get_persconffile_path(filename, from_profile)</c>
    /// using the basename from <c>packet-*.c</c> <c>DATAFILE_*</c> macros — no <c>.csv</c> suffix.
    /// </summary>
    internal const string Filesuffix = "";

    /// <summary>Returns an uppercase 8-nibble ASCII hex string without 0x prefix.</summary>
    /// <remarks>
    /// Wireshark UAT <c>UAT_HEX</c> columns must reach the lexer as <c>quoted_string</c> tokens (ASCII hex inside
    /// quotes). Combine with <see cref="UatQuoted"/> when emitting profile rows — bare 8-nibble strings are parsed as
    /// <c>binstring</c> and fail hex validation (<c>epan/uat_load.l</c>).
    /// </remarks>
    internal static string Hex32Upper(uint id) =>
        $"{id:X8}".ToUpperInvariant();

    internal static string Bool(bool value) =>
        value ? "TRUE" : "FALSE";

    internal static string CsvDouble(double value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Wraps <paramref name="value"/> in a Wireshark UAT <c>quoted_string</c> (outer double quotes,
    /// inner <c>\"</c> / <c>\\</c> escapes per <c>uat_load.l</c>).
    /// </summary>
    internal static string UatQuoted(int value) =>
        UatQuoted(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Same as <see cref="UatQuoted(int)"/> for unsigned ports and counters.</summary>
    internal static string UatQuoted(uint value) =>
        UatQuoted(value.ToString(CultureInfo.InvariantCulture));

    internal static string UatQuoted(ushort value) =>
        UatQuoted(value.ToString(CultureInfo.InvariantCulture));

    internal static string UatQuoted(string raw)
    {
        StringBuilder sb = new(raw.Length + 8);
        _ = sb.Append('"');
        foreach (char c in raw)
        {
            switch (c)
            {
                case '"':
                    _ = sb.Append("\\\"");
                    break;
                case '\\':
                    _ = sb.Append("\\\\");
                    break;
                case '\r':
                    _ = sb.Append("\\r");
                    break;
                case '\n':
                    _ = sb.Append("\\n");
                    break;
                default:
                    _ = sb.Append(c);
                    break;
            }
        }

        _ = sb.Append('"');
        return sb.ToString();
    }

    internal static string CsvEscaped(string raw)
    {
        if (!RequiresCsvEscaping(raw))
        {
            return raw;
        }

        StringBuilder sb = new(raw.Length + 8);
        _ = sb.Append('"');
        foreach (char c in raw)
        {
            switch (c)
            {
                case '"':
                    _ = sb.Append("\\\"");
                    break;
                case '\\':
                    _ = sb.Append("\\\\");
                    break;
                case '\r':
                    _ = sb.Append("\\r");
                    break;
                case '\n':
                    _ = sb.Append("\\n");
                    break;
                default:
                    _ = sb.Append(c);
                    break;
            }
        }

        _ = sb.Append('"');
        return sb.ToString();
    }

    private static bool RequiresCsvEscaping(string raw)
    {
        foreach (char c in raw)
        {
            switch (c)
            {
                case '"':
                case ',':
                case '\r':
                case '\n':
                    return true;
            }
        }

        return false;
    }
}
