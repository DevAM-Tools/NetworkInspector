// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.WebSocket;

/// <summary>
/// Precomputed display text table for WebSocket opcodes (RFC 6455 Section 5.2).
/// </summary>
internal static class WebSocketDisplayTables
{
    #region Opcodes — 16-entry table

    private static readonly string[] _OpcodeDisplayText =
    [
        "Continuation (0x0)",  // 0
        "Text (0x1)",          // 1
        "Binary (0x2)",        // 2
        "0x3",                 // 3 reserved
        "0x4",                 // 4 reserved
        "0x5",                 // 5 reserved
        "0x6",                 // 6 reserved
        "0x7",                 // 7 reserved
        "Close (0x8)",         // 8
        "Ping (0x9)",          // 9
        "Pong (0xA)",          // 10
        "0xB",                 // 11 reserved
        "0xC",                 // 12 reserved
        "0xD",                 // 13 reserved
        "0xE",                 // 14 reserved
        "0xF"                  // 15 reserved
    ];

    /// <summary>Returns display text for a WebSocket opcode (4-bit value, 0-15).</summary>
    public static string GetOpcodeDisplayText(byte opcode) =>
        _OpcodeDisplayText[opcode & 0x0F];

    /// <summary>
    /// Returns display text for a WebSocket close status code (RFC 6455 Section 7.4.1).
    /// </summary>
    public static string GetCloseCodeDisplayText(ushort code) =>
        code switch
        {
            1000 => "Normal Closure (1000)",
            1001 => "Going Away (1001)",
            1002 => "Protocol Error (1002)",
            1003 => "Unsupported Data (1003)",
            1005 => "No Status Rcvd (1005)",
            1006 => "Abnormal Closure (1006)",
            1007 => "Invalid Payload Data (1007)",
            1008 => "Policy Violation (1008)",
            1009 => "Message Too Big (1009)",
            1010 => "Mandatory Extension (1010)",
            1011 => "Internal Server Error (1011)",
            1012 => "Service Restart (1012)",
            1013 => "Try Again Later (1013)",
            1014 => "Bad Gateway (1014)",
            1015 => "TLS Handshake (1015)",
            _ => $"{code}",
        };
    #endregion
}
