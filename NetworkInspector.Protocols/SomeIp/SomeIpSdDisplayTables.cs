// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Precomputed display text tables for SOME/IP-SD fields.
/// Covers SD entry types, option types, and L4 protocol names.
/// </summary>
/// <remarks>
/// Every table has exactly 256 entries, one per possible byte value, and every slot is filled (unknown values get
/// a generic text). A lookup is therefore a single array index that cannot go out of range for any
/// <see cref="byte"/> argument and needs no hashing, no per-packet string formatting and no allocation. The strings
/// are built once in the static initializer. Thread safety: tables are never modified after type initialization,
/// so they are safe to read from any thread. The full and short variants exist because the full text
/// (<c>Name (0xNN)</c>) is shown on the type field while the short name is used for container summaries.
/// </remarks>
internal static class SomeIpSdDisplayTables
{
    #region Entry Types

    /// <summary>256-entry lookup table for SD entry type display text ("Name (0xNN)").</summary>
    private static readonly string[] _EntryTypeTable = _BuildEntryTypeTable();

    /// <summary>256-entry lookup table for SD entry type short names (no hex suffix).</summary>
    private static readonly string[] _EntryTypeShortTable = _BuildEntryTypeShortTable();

    /// <summary>Returns the full display text for an SD entry type byte.</summary>
    internal static string GetEntryTypeDisplayText(byte entryType) => _EntryTypeTable[entryType];

    /// <summary>Returns the short name for an SD entry type byte (no hex suffix).</summary>
    internal static string GetEntryTypeShortName(byte entryType) => _EntryTypeShortTable[entryType];

    /// <summary>
    /// Builds the full entry type texts: the four entry types defined by SOME/IP-SD
    /// (service entries 0x00 and 0x01, eventgroup entries 0x06 and 0x07).
    /// </summary>
    private static string[] _BuildEntryTypeTable()
    {
        string[] table = new string[256];
        table[0x00] = "FindService (0x00)";
        table[0x01] = "OfferService (0x01)";
        table[0x06] = "SubscribeEventgroup (0x06)";
        table[0x07] = "SubscribeEventgroupAck (0x07)";

        // Fill every unnamed slot so the lookup never returns null.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }

    /// <summary>Builds the short entry type names (no hex suffix); unknown types read <c>Entry (0xNN)</c>.</summary>
    private static string[] _BuildEntryTypeShortTable()
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
    private static readonly string[] _OptionTypeTable = _BuildOptionTypeTable();

    /// <summary>256-entry lookup table for SD option type short names.</summary>
    private static readonly string[] _OptionTypeShortTable = _BuildOptionTypeShortTable();

    /// <summary>Returns the full display text for an SD option type byte.</summary>
    internal static string GetOptionTypeDisplayText(byte optType) => _OptionTypeTable[optType];

    /// <summary>Returns the short name for an SD option type byte.</summary>
    internal static string GetOptionTypeShortName(byte optType) => _OptionTypeShortTable[optType];

    /// <summary>
    /// Builds the full option type texts. In the option type values the upper nibble 0x1 marks the multicast variant
    /// and 0x2 the SD endpoint variant of the IPv4 (0x04) and IPv6 (0x06) endpoint options.
    /// </summary>
    private static string[] _BuildOptionTypeTable()
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

        // Fill every unnamed slot so the lookup never returns null.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:X2}";
        }
        return table;
    }

    /// <summary>Builds the short option type names; unknown types read <c>Option (0xNN)</c>.</summary>
    private static string[] _BuildOptionTypeShortTable()
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
    private static readonly string[] _L4ProtoTable = _BuildL4ProtoTable();

    /// <summary>Returns display text for an L4 protocol byte.</summary>
    internal static string GetL4ProtoDisplayText(byte proto) => _L4ProtoTable[proto];

    /// <summary>
    /// Builds the L4 protocol texts. The byte is the IANA IP protocol number (TCP = 6, UDP = 17), which are the only
    /// transports SOME/IP-SD endpoint options carry; any other number is shown as its decimal value.
    /// </summary>
    private static string[] _BuildL4ProtoTable()
    {
        string[] table = new string[256];
        table[6] = "TCP (6)";
        table[17] = "UDP (17)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion
}
