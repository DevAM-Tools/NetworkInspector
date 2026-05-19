// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Precomputed display text tables for SOME/IP protocol fields.
/// </summary>
internal static class SomeIpDisplayTables
{
    #region Message Type (byte field)

    private static readonly string[] MsgTypeTable = BuildMsgTypeTable();

    /// <summary>Returns display text for a SOME/IP message type byte.</summary>
    internal static string GetMsgTypeDisplayText(byte msgType) => MsgTypeTable[msgType];

    private static string[] BuildMsgTypeTable()
    {
        string[] table = new string[256];
        table[0x00] = "REQUEST (0x00)";
        table[0x01] = "REQUEST_NO_RETURN (0x01)";
        table[0x02] = "NOTIFICATION (0x02)";
        table[0x20] = "TP_REQUEST (0x20)";
        table[0x21] = "TP_REQUEST_NO_RETURN (0x21)";
        table[0x22] = "TP_NOTIFICATION (0x22)";
        table[0x80] = "RESPONSE (0x80)";
        table[0x81] = "ERROR (0x81)";
        table[0xA0] = "TP_RESPONSE (0xA0)";
        table[0xA1] = "TP_ERROR (0xA1)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }

    #endregion

    #region Return Code (byte field)

    private static readonly string[] ReturnCodeTable = BuildReturnCodeTable();

    /// <summary>Returns display text for a SOME/IP return code byte.</summary>
    internal static string GetReturnCodeDisplayText(byte code) => ReturnCodeTable[code];

    private static string[] BuildReturnCodeTable()
    {
        string[] table = new string[256];
        table[0x00] = "E_OK (0x00)";
        table[0x01] = "E_NOT_OK (0x01)";
        table[0x02] = "E_UNKNOWN_SERVICE (0x02)";
        table[0x03] = "E_UNKNOWN_METHOD (0x03)";
        table[0x04] = "E_NOT_READY (0x04)";
        table[0x05] = "E_NOT_REACHABLE (0x05)";
        table[0x06] = "E_TIMEOUT (0x06)";
        table[0x07] = "E_WRONG_PROTOCOL_VERSION (0x07)";
        table[0x08] = "E_WRONG_INTERFACE_VERSION (0x08)";
        table[0x09] = "E_MALFORMED_MESSAGE (0x09)";
        table[0x0A] = "E_WRONG_MESSAGE_TYPE (0x0A)";
        table[0x0B] = "E_E2E_REPEATED (0x0B)";
        table[0x0C] = "E_E2E_WRONG_SEQUENCE (0x0C)";
        table[0x0D] = "E_E2E (0x0D)";
        table[0x0E] = "E_E2E_NOT_AVAILABLE (0x0E)";
        table[0x0F] = "E_E2E_NO_NEW_DATA (0x0F)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }
    #endregion
}
