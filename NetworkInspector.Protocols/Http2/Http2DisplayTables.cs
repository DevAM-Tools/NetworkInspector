// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Http2;

/// <summary>
/// Precomputed display text tables for HTTP/2 frame types, error codes, and settings.
/// All tables use zero-allocation static lookups.
/// </summary>
internal static class Http2DisplayTables
{
    #region Frame Types (RFC 7540 Section 6) — 256-entry table

    private static readonly string[] _FrameTypeDisplayText = GenerateFrameTypeTable();

    private static string[] GenerateFrameTypeTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i switch
            {
                0 => "DATA (0)",
                1 => "HEADERS (1)",
                2 => "PRIORITY (2)",
                3 => "RST_STREAM (3)",
                4 => "SETTINGS (4)",
                5 => "PUSH_PROMISE (5)",
                6 => "PING (6)",
                7 => "GOAWAY (7)",
                8 => "WINDOW_UPDATE (8)",
                9 => "CONTINUATION (9)",
                _ => i.ToString()
            };
        }

        return table;
    }

    /// <summary>Returns precomputed display text for an HTTP/2 frame type.</summary>
    public static string GetFrameTypeDisplayText(byte frameType) =>
        _FrameTypeDisplayText[frameType];

    #endregion

    #region Error Codes (RFC 7540 Section 7) — used in RST_STREAM and GOAWAY

    private static readonly string[] _ErrorCodeDisplayText = GenerateErrorCodeTable();

    private static string[] GenerateErrorCodeTable()
    {
        // Error codes are 32-bit, but defined values are 0-13. Use 256-entry table for fast lookup.
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = i switch
            {
                0 => "NO_ERROR (0x0)",
                1 => "PROTOCOL_ERROR (0x1)",
                2 => "INTERNAL_ERROR (0x2)",
                3 => "FLOW_CONTROL_ERROR (0x3)",
                4 => "SETTINGS_TIMEOUT (0x4)",
                5 => "STREAM_CLOSED (0x5)",
                6 => "FRAME_SIZE_ERROR (0x6)",
                7 => "REFUSED_STREAM (0x7)",
                8 => "CANCEL (0x8)",
                9 => "COMPRESSION_ERROR (0x9)",
                10 => "CONNECT_ERROR (0xa)",
                11 => "ENHANCE_YOUR_CALM (0xb)",
                12 => "INADEQUATE_SECURITY (0xc)",
                13 => "HTTP_1_1_REQUIRED (0xd)",
                _ => $"0x{i:x}"
            };
        }

        return table;
    }

    /// <summary>
    /// Returns display text for an HTTP/2 error code.
    /// Only values 0-255 use the fast table; larger values fall back to formatting.
    /// </summary>
    public static string GetErrorCodeDisplayText(uint errorCode) =>
        errorCode < 256 ? _ErrorCodeDisplayText[errorCode] : $"0x{errorCode:x}";

    #endregion

    #region Settings Identifiers (RFC 7540 Section 6.5.2)

    private static readonly string[] _SettingsDisplayText = GenerateSettingsTable();

    private static string[] GenerateSettingsTable()
    {
        string[] table = new string[16];
        for (int i = 0; i < 16; i++)
        {
            table[i] = i switch
            {
                1 => "HEADER_TABLE_SIZE (0x1)",
                2 => "ENABLE_PUSH (0x2)",
                3 => "MAX_CONCURRENT_STREAMS (0x3)",
                4 => "INITIAL_WINDOW_SIZE (0x4)",
                5 => "MAX_FRAME_SIZE (0x5)",
                6 => "MAX_HEADER_LIST_SIZE (0x6)",
                _ => $"Unknown (0x{i:x})"
            };
        }

        return table;
    }

    /// <summary>Returns display text for an HTTP/2 settings identifier.</summary>
    public static string GetSettingsDisplayText(ushort settingsId) =>
        settingsId < 16 ? _SettingsDisplayText[settingsId] : $"Unknown (0x{settingsId:x})";
    #endregion
}
