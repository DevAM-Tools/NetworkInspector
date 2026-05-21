// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Precomputed display text tables for SOME/IP-SD fields.
/// Covers SD entry types, option types, and L4 protocol names.
/// </summary>
internal static class SomeIpSdDisplayTables
{
    #region Entry Types

    /// <summary>256-entry lookup table for SD entry type display text ("Name (0xNN)").</summary>
    private static readonly string[] EntryTypeTable = BuildEntryTypeTable();

    /// <summary>256-entry lookup table for SD entry type short names (no hex suffix).</summary>
    private static readonly string[] EntryTypeShortTable = BuildEntryTypeShortTable();

    /// <summary>Returns the full display text for an SD entry type byte.</summary>
    internal static string GetEntryTypeDisplayText(byte entryType) => EntryTypeTable[entryType];

    /// <summary>Returns the short name for an SD entry type byte (no hex suffix).</summary>
    internal static string GetEntryTypeShortName(byte entryType) => EntryTypeShortTable[entryType];

    private static string[] BuildEntryTypeTable()
    {
        string[] table = new string[256];
        table[0x00] = "FindService (0x00)";
        table[0x01] = "OfferService (0x01)";
        table[0x06] = "SubscribeEventgroup (0x06)";
        table[0x07] = "SubscribeEventgroupAck (0x07)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }

    private static string[] BuildEntryTypeShortTable()
    {
        string[] table = new string[256];
        table[0x00] = "FindService";
        table[0x01] = "OfferService";
        table[0x06] = "SubscribeEventgroup";
        table[0x07] = "SubscribeEventgroupAck";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"Entry (0x{i:X2})";
        }
        return table;
    }

    #endregion

    #region Option Types

    /// <summary>256-entry lookup table for SD option type display text ("Name (0xNN)").</summary>
    private static readonly string[] OptionTypeTable = BuildOptionTypeTable();

    /// <summary>256-entry lookup table for SD option type short names.</summary>
    private static readonly string[] OptionTypeShortTable = BuildOptionTypeShortTable();

    /// <summary>Returns the full display text for an SD option type byte.</summary>
    internal static string GetOptionTypeDisplayText(byte optType) => OptionTypeTable[optType];

    /// <summary>Returns the short name for an SD option type byte.</summary>
    internal static string GetOptionTypeShortName(byte optType) => OptionTypeShortTable[optType];

    private static string[] BuildOptionTypeTable()
    {
        string[] table = new string[256];
        table[0x01] = "Configuration (0x01)";
        table[0x02] = "Load Balancing (0x02)";
        table[0x04] = "IPv4 Endpoint (0x04)";
        table[0x06] = "IPv6 Endpoint (0x06)";
        table[0x14] = "IPv4 Multicast (0x14)";
        table[0x16] = "IPv6 Multicast (0x16)";
        table[0x24] = "IPv4 SD Endpoint (0x24)";
        table[0x26] = "IPv6 SD Endpoint (0x26)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }

    private static string[] BuildOptionTypeShortTable()
    {
        string[] table = new string[256];
        table[0x01] = "Configuration";
        table[0x02] = "Load Balancing";
        table[0x04] = "IPv4 Endpoint";
        table[0x06] = "IPv6 Endpoint";
        table[0x14] = "IPv4 Multicast";
        table[0x16] = "IPv6 Multicast";
        table[0x24] = "IPv4 SD Endpoint";
        table[0x26] = "IPv6 SD Endpoint";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"Option (0x{i:X2})";
        }
        return table;
    }

    #endregion

    #region L4 Protocol

    /// <summary>256-entry lookup table for L4 protocol display text.</summary>
    private static readonly string[] L4ProtoTable = BuildL4ProtoTable();

    /// <summary>Returns display text for an L4 protocol byte.</summary>
    internal static string GetL4ProtoDisplayText(byte proto) => L4ProtoTable[proto];

    private static string[] BuildL4ProtoTable()
    {
        string[] table = new string[256];
        table[6] = "TCP (6)";
        table[17] = "UDP (17)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion
}
