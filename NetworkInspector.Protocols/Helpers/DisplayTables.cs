// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Precomputed static display text tables for protocol fields.
/// Zero-allocation lookups via array indexing.
/// <para>
/// All tables are allocated once when the type initializer runs.
/// Call <see cref="EnsureInitialized"/> during stack build to trigger
/// initialization eagerly, so no first-access cost occurs during
/// the timed parsing phase.
/// </para>
/// </summary>
internal static class DisplayTables
{
    #region Eager Initialization

    /// <summary>
    /// Forces execution of the type initializer, pre-allocating all
    /// lookup tables. Call once during stack build so the first packet
    /// parse does not pay the initialization cost.
    /// </summary>
    internal static void EnsureInitialized() =>
        RuntimeHelpers.RunClassConstructor(typeof(DisplayTables).TypeHandle);

    #endregion

    #region DSCP Display Text (64 entries for 6-bit field)

    private static readonly string[] DscpTable = BuildDscpTable();

    internal static string GetDscpDisplayText(byte dscp) => DscpTable[dscp & 0x3F];

    private static string[] BuildDscpTable()
    {
        string[] table = new string[64];
        // Well-known DSCP values
        table[0] = "Default (BE) (0)";
        table[8] = "CS1 (8)";
        table[10] = "AF11 (10)";
        table[12] = "AF12 (12)";
        table[14] = "AF13 (14)";
        table[16] = "CS2 (16)";
        table[18] = "AF21 (18)";
        table[20] = "AF22 (20)";
        table[22] = "AF23 (22)";
        table[24] = "CS3 (24)";
        table[26] = "AF31 (26)";
        table[28] = "AF32 (28)";
        table[30] = "AF33 (30)";
        table[32] = "CS4 (32)";
        table[34] = "AF41 (34)";
        table[36] = "AF42 (36)";
        table[38] = "AF43 (38)";
        table[40] = "CS5 (40)";
        table[44] = "Voice-Admit (44)";
        table[46] = "EF (46)";
        table[48] = "CS6 (48)";
        table[56] = "CS7 (56)";

        // Fill unnamed entries
        for (int i = 0; i < 64; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region ECN Display Text (4 entries for 2-bit field)

    private static readonly string[] EcnTable =
    [
        "Not-ECT (0)",
        "ECT(1) (1)",
        "ECT(0) (2)",
        "CE (3)"
    ];

    internal static string GetEcnDisplayText(byte ecn) => EcnTable[ecn & 0x03];

    #endregion

    #region IP Protocol Display Text (256 entries for 8-bit field)

    private static readonly string[] IpProtocolTable = BuildIpProtocolTable();

    internal static string GetIpProtocolDisplayText(byte protocol) => IpProtocolTable[protocol];

    private static string[] BuildIpProtocolTable()
    {
        string[] table = new string[256];
        table[0] = "HOPOPT (0)";
        table[1] = "ICMP (1)";
        table[2] = "IGMP (2)";
        table[4] = "IPv4 (4)";
        table[6] = "TCP (6)";
        table[8] = "EGP (8)";
        table[17] = "UDP (17)";
        table[41] = "IPv6 (41)";
        table[43] = "IPv6-Route (43)";
        table[44] = "IPv6-Frag (44)";
        table[47] = "GRE (47)";
        table[50] = "ESP (50)";
        table[51] = "AH (51)";
        table[58] = "ICMPv6 (58)";
        table[59] = "IPv6-NoNxt (59)";
        table[60] = "IPv6-Opts (60)";
        table[89] = "OSPF (89)";
        table[103] = "PIM (103)";
        table[112] = "VRRP (112)";
        table[132] = "SCTP (132)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region EtherType Display Text (65,536 entries for zero-alloc u16 lookup)

    private static readonly string[] EtherTypeTable = BuildEtherTypeTable();

    internal static string GetEtherTypeDisplayText(ushort etherType) => EtherTypeTable[etherType];

    private static string[] BuildEtherTypeTable()
    {
        string[] table = new string[65536];

        // Well-known EtherType values
        table[0x0800] = "IPv4 (0x0800)";
        table[0x0806] = "ARP (0x0806)";
        table[0x0842] = "Wake-on-LAN (0x0842)";
        table[0x22F0] = "Audio Video Transport Protocol (0x22f0)";
        table[0x22F3] = "IETF TRILL (0x22f3)";
        table[0x6002] = "DEC MOP RC (0x6002)";
        table[0x6003] = "DECnet Phase IV (0x6003)";
        table[0x6004] = "DEC LAT (0x6004)";
        table[0x8035] = "RARP (0x8035)";
        table[0x809B] = "AppleTalk (0x809b)";
        table[0x80F3] = "AARP (0x80f3)";
        table[0x8100] = "802.1Q (0x8100)";
        table[0x8137] = "IPX (0x8137)";
        table[0x8204] = "QNX Qnet (0x8204)";
        table[0x86DD] = "IPv6 (0x86dd)";
        table[0x8808] = "Ethernet flow control (0x8808)";
        table[0x8809] = "Slow Protocols (LACP) (0x8809)";
        table[0x8847] = "MPLS unicast (0x8847)";
        table[0x8848] = "MPLS multicast (0x8848)";
        table[0x8863] = "PPPoE Discovery (0x8863)";
        table[0x8864] = "PPPoE Session (0x8864)";
        table[0x887B] = "HomePlug (0x887b)";
        table[0x888E] = "EAP over LAN (0x888e)";
        table[0x8892] = "PROFINET (0x8892)";
        table[0x889A] = "HyperSCSI (0x889a)";
        table[0x88A2] = "ATA over Ethernet (0x88a2)";
        table[0x88A4] = "EtherCAT (0x88a4)";
        table[0x88A8] = "802.1ad (0x88a8)";
        table[0x88AB] = "Ethernet Powerlink (0x88ab)";
        table[0x88B8] = "GOOSE (0x88b8)";
        table[0x88B9] = "GSE (0x88b9)";
        table[0x88BA] = "SV (0x88ba)";
        table[0x88CC] = "LLDP (0x88cc)";
        table[0x88CD] = "SERCOS III (0x88cd)";
        table[0x88E1] = "HomePlug Green PHY (0x88e1)";
        table[0x88E3] = "MRP (0x88e3)";
        table[0x88E5] = "802.1AE (0x88e5)";
        table[0x88F7] = "PTP (0x88f7)";
        table[0x88F8] = "NC-SI (0x88f8)";
        table[0x88FB] = "PRP (0x88fb)";
        table[0x8902] = "802.1ag CFM (0x8902)";
        table[0x8906] = "FCoE (0x8906)";
        table[0x8914] = "FCoE Init (0x8914)";
        table[0x8915] = "RoCE (0x8915)";
        table[0x891D] = "TTE (0x891d)";
        table[0x892F] = "HSR (0x892f)";
        table[0x9000] = "Ethernet Configuration Testing Protocol (0x9000)";
        table[0x9100] = "802.1Q-in-Q (0x9100)";
        table[0xF1C1] = "Redundancy Tag (0xf1c1)";

        // Fill unnamed entries with hex representation
        for (int i = 0; i < 65536; i++)
        {
            table[i] ??= $"0x{i:x4}";
        }
        return table;
    }

    #endregion

    #region Hex u16 Display Text (65,536 entries for zero-alloc u16 hex formatting)

    private static readonly string[] HexU16Table = BuildHexU16Table();

    internal static string FormatHexU16(ushort value) => HexU16Table[value];

    private static string[] BuildHexU16Table()
    {
        string[] table = new string[65536];
        for (int i = 0; i < 65536; i++)
        {
            table[i] = $"0x{i:x4}";
        }
        return table;
    }

    #endregion

    #region Hex u32 Display Text (composed from two u16 table lookups)

    /// <summary>
    /// Formats a 32-bit unsigned integer as "0xNNNNNNNN" using the precomputed u16 table.
    /// Single allocation (the result string), no intermediate allocations.
    /// </summary>
    internal static string FormatHexU32(uint value) =>
        string.Create(10, value, static (span, v) =>
        {
            span[0] = '0';
            span[1] = 'x';
            HexU16Table[v >> 16].AsSpan(2).CopyTo(span[2..]);
            HexU16Table[v & 0xFFFF].AsSpan(2).CopyTo(span[6..]);
        });

    /// <summary>
    /// Formats a 32-bit unsigned integer as "0xNNN" (3-digit hex) for standard CAN IDs.
    /// Single allocation (the result string), no intermediate allocations.
    /// </summary>
    internal static string FormatHexU12(uint value) =>
        string.Create(5, value, static (span, v) =>
        {
            span[0] = '0';
            span[1] = 'x';
            // Use lower 3 hex digits from the U16 table (skip "0x" prefix + first digit)
            HexU16Table[v & 0x0FFF].AsSpan(3).CopyTo(span[2..]);
        });

    #endregion

    #region Hex u8 Display Text (256 entries for zero-alloc u8 hex formatting)

    private static readonly string[] HexU8Table = BuildHexU8Table();

    internal static string FormatHexU8(byte value) => HexU8Table[value];

    private static string[] BuildHexU8Table()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = $"0x{i:x2}";
        }
        return table;
    }

    #endregion

    #region IPv4 Header Length Display Text (common IHL*4 values: 20..60)

    private static readonly string[] HeaderLengthTable = BuildHeaderLengthTable();

    /// <summary>
    /// Returns precomputed "N bytes" display text for header lengths 0-60.
    /// Falls back to dynamic formatting for out-of-range values.
    /// </summary>
    internal static string GetHeaderLengthDisplayText(int headerLength)
    {
        if ((uint)headerLength < (uint)HeaderLengthTable.Length)
        {
            return HeaderLengthTable[headerLength];
        }
        return $"{headerLength} bytes";
    }

    private static string[] BuildHeaderLengthTable()
    {
        // Covers IPv4 IHL range: 5*4=20 to 15*4=60
        string[] table = new string[61];
        for (int i = 0; i <= 60; i++)
        {
            table[i] = $"{i} bytes";
        }
        return table;
    }

    #endregion

    #region VLAN Priority Display Text (8 entries for 3-bit field)

    private static readonly string[] VlanPriorityTable =
    [
        "Best Effort (0)",
        "Background (1)",
        "Excellent Effort (2)",
        "Critical Applications (3)",
        "Video (4)",
        "Voice (5)",
        "Internetwork Control (6)",
        "Network Control (7)"
    ];

    internal static string GetVlanPriorityDisplayText(byte pcp) => VlanPriorityTable[pcp & 0x07];

    #endregion

    #region ARP Opcode Display Text

    /// <summary>
    /// Returns display text for ARP opcodes.
    /// Well-known values (1-9) get named text, others fall back to numeric.
    /// </summary>
    internal static string GetArpOpcodeDisplayText(ushort opcode) => opcode switch
    {
        1 => "request (1)",
        2 => "reply (2)",
        3 => "RARP request (3)",
        4 => "RARP reply (4)",
        5 => "DRARP request (5)",
        6 => "DRARP reply (6)",
        7 => "DRARP error (7)",
        8 => "InARP request (8)",
        9 => "InARP reply (9)",
        _ => opcode.ToString()
    };

    #endregion

    #region ARP Hardware Type Display Text (256 entries for u8 range)

    private static readonly string[] ArpHwTypeTable = BuildArpHwTypeTable();

    internal static string GetArpHwTypeDisplayText(ushort hwType)
    {
        if (hwType < ArpHwTypeTable.Length)
        {
            return ArpHwTypeTable[hwType];
        }
        return hwType.ToString();
    }

    private static string[] BuildArpHwTypeTable()
    {
        string[] table = new string[256];
        table[0] = "Reserved (0)";
        table[1] = "Ethernet (1)";
        table[2] = "Experimental Ethernet (2)";
        table[3] = "Amateur Radio AX.25 (3)";
        table[4] = "Proteon ProNET Token Ring (4)";
        table[5] = "Chaos (5)";
        table[6] = "IEEE 802 (6)";
        table[7] = "ARCNET (7)";
        table[8] = "Hyperchannel (8)";
        table[9] = "Lanstar (9)";
        table[10] = "Autonet Short Address (10)";
        table[11] = "LocalTalk (11)";
        table[12] = "LocalNet (12)";
        table[13] = "Ultra link (13)";
        table[14] = "SMDS (14)";
        table[15] = "Frame Relay (15)";
        table[16] = "ATM (16)";
        table[17] = "HDLC (17)";
        table[18] = "Fibre Channel (18)";
        table[19] = "ATM (19)";
        table[20] = "Serial Line (20)";
        table[31] = "IPsec tunnel (31)";
        table[32] = "InfiniBand (32)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region ICMP Type Display Text (256 entries for u8 type field)

    private static readonly string[] IcmpTypeTable = BuildIcmpTypeTable();

    internal static string GetIcmpTypeDisplayText(byte type) => IcmpTypeTable[type];

    private static string[] BuildIcmpTypeTable()
    {
        string[] table = new string[256];
        table[0] = "Echo (ping) reply (0)";
        table[3] = "Destination unreachable (3)";
        table[4] = "Source quench (4)";
        table[5] = "Redirect (5)";
        table[8] = "Echo (ping) request (8)";
        table[9] = "Router advertisement (9)";
        table[10] = "Router solicitation (10)";
        table[11] = "Time-to-live exceeded (11)";
        table[12] = "Parameter problem (12)";
        table[13] = "Timestamp request (13)";
        table[14] = "Timestamp reply (14)";
        table[15] = "Information request (15)";
        table[16] = "Information reply (16)";
        table[17] = "Address mask request (17)";
        table[18] = "Address mask reply (18)";
        table[30] = "Traceroute (30)";
        table[40] = "Photuris (40)";
        table[42] = "Extended echo request (42)";
        table[43] = "Extended echo reply (43)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region ICMP Code Display Text (per type)

    /// <summary>
    /// Returns display text for ICMP code values, contextualized by type.
    /// </summary>
    internal static string GetIcmpCodeDisplayText(byte type, byte code) => type switch
    {
        3 => code switch // Destination Unreachable
        {
            0 => "Network unreachable (0)",
            1 => "Host unreachable (1)",
            2 => "Protocol unreachable (2)",
            3 => "Port unreachable (3)",
            4 => "Fragmentation needed (4)",
            5 => "Source route failed (5)",
            6 => "Destination network unknown (6)",
            7 => "Destination host unknown (7)",
            8 => "Source host isolated (8)",
            9 => "Network administratively prohibited (9)",
            10 => "Host administratively prohibited (10)",
            11 => "Network unreachable for ToS (11)",
            12 => "Host unreachable for ToS (12)",
            13 => "Communication administratively prohibited (13)",
            14 => "Host precedence violation (14)",
            15 => "Precedence cutoff in effect (15)",
            _ => code.ToString()
        },
        5 => code switch // Redirect
        {
            0 => "Redirect for network (0)",
            1 => "Redirect for host (1)",
            2 => "Redirect for ToS and network (2)",
            3 => "Redirect for ToS and host (3)",
            _ => code.ToString()
        },
        11 => code switch // Time Exceeded
        {
            0 => "TTL exceeded in transit (0)",
            1 => "Fragment reassembly time exceeded (1)",
            _ => code.ToString()
        },
        12 => code switch // Parameter Problem
        {
            0 => "Pointer indicates the error (0)",
            1 => "Missing a required option (1)",
            2 => "Bad length (2)",
            _ => code.ToString()
        },
        _ => code.ToString()
    };

    #endregion

    #region ICMPv6 Type Display Text (256 entries for u8 type field)

    private static readonly string[] Icmpv6TypeTable = BuildIcmpv6TypeTable();

    internal static string GetIcmpv6TypeDisplayText(byte type) => Icmpv6TypeTable[type];

    private static string[] BuildIcmpv6TypeTable()
    {
        string[] table = new string[256];
        // Error messages (0-127)
        table[1] = "Destination Unreachable (1)";
        table[2] = "Packet Too Big (2)";
        table[3] = "Time Exceeded (3)";
        table[4] = "Parameter Problem (4)";

        // Informational messages (128-255)
        table[128] = "Echo (ping) request (128)";
        table[129] = "Echo (ping) reply (129)";
        table[130] = "Multicast Listener Query (130)";
        table[131] = "Multicast Listener Report (131)";
        table[132] = "Multicast Listener Done (132)";
        table[133] = "Router Solicitation (133)";
        table[134] = "Router Advertisement (134)";
        table[135] = "Neighbor Solicitation (135)";
        table[136] = "Neighbor Advertisement (136)";
        table[137] = "Redirect (137)";
        table[138] = "Router Renumbering (138)";
        table[139] = "ICMP Node Information Query (139)";
        table[140] = "ICMP Node Information Response (140)";
        table[141] = "Inverse Neighbor Discovery Solicitation (141)";
        table[142] = "Inverse Neighbor Discovery Advertisement (142)";
        table[143] = "Multicast Listener Report v2 (143)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString();
        }
        return table;
    }

    #endregion

    #region SLL Packet Type Display Text

    /// <summary>
    /// Returns a display string for the SLL packet type field.
    /// Values: 0=Unicast, 1=Broadcast, 2=Multicast, 3=Other host, 4=Sent by us.
    /// </summary>
    internal static string GetSllPacketTypeDisplayText(ushort pktType) => pktType switch
    {
        0 => "Unicast to us (0)",
        1 => "Broadcast (1)",
        2 => "Multicast (2)",
        3 => "Sent to someone else (3)",
        4 => "Sent by us (4)",
        _ => $"Unknown ({pktType})",
    };

    #endregion

    #region LLC SAP Display Text (256 entries for u8 SAP field)

    private static readonly string[] LlcSapTable = BuildLlcSapTable();

    /// <summary>
    /// Returns a display string for the LLC SAP field (DSAP or SSAP).
    /// The low bit (I/G for DSAP, C/R for SSAP) is masked out before lookup.
    /// </summary>
    internal static string GetLlcSapDisplayText(byte sap) => LlcSapTable[sap];

    private static string[] BuildLlcSapTable()
    {
        string[] table = new string[256];
        table[0x00] = "Null LSAP (0x00)";
        table[0x02] = "Individual LLC Sublayer Management (0x02)";
        table[0x03] = "Group LLC Sublayer Management (0x03)";
        table[0x04] = "SNA Path Control (0x04)";
        table[0x06] = "DOD IP (0x06)";
        table[0x08] = "SNA (0x08)";
        table[0x0C] = "SNA (0x0c)";
        table[0x0E] = "ProWay-LAN (0x0e)";
        table[0x18] = "Texas Instruments (0x18)";
        table[0x42] = "Spanning Tree BPDU (0x42)";
        table[0x4E] = "EIA-RS 511 (0x4e)";
        table[0x5E] = "ISI IP (0x5e)";
        table[0x7E] = "ISO 8208 (0x7e)";
        table[0x80] = "XNS (0x80)";
        table[0x86] = "Nestar (0x86)";
        table[0x8E] = "ProWay-LAN (0x8e)";
        table[0x98] = "ARP (0x98)";
        table[0xAA] = "SNAP (0xaa)";
        table[0xBC] = "Banyan Vines (0xbc)";
        table[0xE0] = "Novell NetWare (0xe0)";
        table[0xF0] = "IBM NetBIOS (0xf0)";
        table[0xF4] = "IBM LAN Management (0xf4)";
        table[0xFE] = "ISO Network Layer (0xfe)";
        table[0xFF] = "Global DSAP (0xff)";

        for (int i = 0; i < 256; i++)
        {
            table[i] ??= $"0x{i:x2}";
        }
        return table;
    }
    #endregion

    #region IPv4 Option Type Name (256 entries for u8 option type byte)

    private static readonly string[] IpOptionNameTable = BuildIpOptionNameTable();

    /// <summary>Returns the short name for an IPv4 option type (0-255). Empty string for unknown types.</summary>
    internal static string GetIpOptionTypeName(byte optType) => IpOptionNameTable[optType];

    private static string[] BuildIpOptionNameTable()
    {
        string[] table = new string[256];
        table[0x00] = "End of Options List (EOOL)";
        table[0x01] = "No-Operation (NOP)";
        table[0x07] = "Record Route";
        table[0x0A] = "Experimental Measurement";
        table[0x0B] = "MTU Probe";
        table[0x0C] = "MTU Reply";
        table[0x19] = "Quick-Start";
        table[0x1E] = "RFC3692-style Experiment";
        table[0x44] = "Time Stamp";
        table[0x52] = "Traceroute";
        table[0x82] = "Security";
        table[0x83] = "Loose Source Route";
        table[0x85] = "Extended Security";
        table[0x86] = "Commercial IP Security";
        table[0x88] = "Stream Identifier";
        table[0x89] = "Strict Source Route";
        table[0x8E] = "Experimental Access Control";
        table[0x90] = "IMI Traffic Descriptor";
        table[0x91] = "Extended Internet Protocol";
        table[0x93] = "Address Extension";
        table[0x94] = "Router Alert";
        table[0x95] = "Selective Directed Broadcast";
        table[0x97] = "Dynamic Packet State";
        table[0x98] = "Upstream Multicast Packet";
        table[0x9A] = "Cilium DSR";
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv4 Option Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] IpOptionDisplayTextTable = BuildIpOptionDisplayTextTable();

    /// <summary>Returns the preformatted display text for an IPv4 option type.</summary>
    internal static string GetIpOptionTypeDisplayText(byte optType) => IpOptionDisplayTextTable[optType];

    private static string[] BuildIpOptionDisplayTextTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = IpOptionNameTable[i];
            // Known options: "Name (num)", unknown options: "num"
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString();
        }
        return table;
    }

    #endregion

    #region IPv4 Option Class (4 entries for the 2-bit class field)

    private static readonly string[] IpOptionClassTable =
    [
        "Control (0)",
        "Reserved (1)",
        "Debugging and Measurement (2)",
        "Reserved (3)",
    ];

    /// <summary>Returns display text for the 2-bit option class field (0-3).</summary>
    internal static string GetIpOptionClassDisplayText(byte optClass)
        => IpOptionClassTable[optClass & 0x03];

    #endregion

    #region IPv4 Timestamp Flag (16 entries for the 4-bit flag field)

    private static readonly string[] IpTimestampFlagTable = BuildIpTimestampFlagTable();

    /// <summary>Returns display text for the 4-bit timestamp flag field.</summary>
    internal static string GetIpTimestampFlagDisplayText(byte flag)
        => IpTimestampFlagTable[flag & 0x0F];

    private static string[] BuildIpTimestampFlagTable()
    {
        string[] table = new string[16];
        table[0] = "Time stamps only (0)";
        table[1] = "Time stamp and address (1)";
        table[3] = "Time stamps for prespecified addresses (3)";
        for (int i = 0; i < 16; i++)
        {
            table[i] ??= $"Unknown ({i})";
        }
        return table;
    }

    #endregion

    #region IPv4 Router Alert (match-based for known IANA values)

    /// <summary>
    /// Returns display text for a Router Alert value.
    /// Returns <see langword="null"/> for unknown values.
    /// </summary>
    internal static string? GetRouterAlertDisplayText(ushort value)
    {
        return value switch
        {
            0 => "Router shall examine packet (0)",
            >= 1 and <= 18 => $"Aggregated Reservation Nesting Level {value - 1} ({value})",
            32 => "QoS NSLP Aggregation Level 0 (32)",
            65535 => "Reserved (65535)",
            _ => null,
        };
    }

    #endregion

    #region IPv6 Extension Header Option Type Name (256 entries)

    private static readonly string[] Ipv6OptionNameTable = BuildIpv6OptionNameTable();

    /// <summary>Returns the short name for an IPv6 option type (0-255). Empty string for unknown.</summary>
    internal static string GetIpv6OptionTypeName(byte optType) => Ipv6OptionNameTable[optType];

    private static string[] BuildIpv6OptionNameTable()
    {
        string[] table = new string[256];
        table[0x00] = "Pad1";
        table[0x01] = "PadN";
        table[0x04] = "Tunnel Encapsulation Limit";
        table[0x05] = "Router Alert";
        table[0x07] = "CALIPSO";
        table[0x08] = "SMF_DPD";
        table[0x0F] = "PDM";
        table[0x13] = "APN6";
        table[0x23] = "RPL Option";
        table[0x26] = "Quick-Start";
        table[0x30] = "PMTU";
        table[0x31] = "IOAM";
        table[0x41] = "TPF";
        table[0x63] = "RPL Option (old)";
        table[0x6D] = "MPL Option";
        table[0x8B] = "ILNP Nonce";
        table[0x8C] = "Line-Identification Option";
        table[0xC2] = "Jumbo Payload";
        table[0xC9] = "Home Address";
        table[0xEE] = "DFF";
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv6 Extension Header Option Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] Ipv6OptionDisplayTextTable = BuildIpv6OptionDisplayTextTable();

    /// <summary>Returns preformatted display text for an IPv6 option type.</summary>
    internal static string GetIpv6OptionTypeDisplayText(byte optType) => Ipv6OptionDisplayTextTable[optType];

    private static string[] BuildIpv6OptionDisplayTextTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = Ipv6OptionNameTable[i];
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString();
        }
        return table;
    }

    #endregion

    #region IPv6 Option Action Display Text (4 entries for 2-bit action field)

    private static readonly string[] Ipv6OptActionTable =
    [
        "Skip and continue (0)",
        "Discard (1)",
        "Discard and send ICMP (2)",
        "Discard and send ICMP if not multicast (3)"
    ];

    /// <summary>Returns display text for the 2-bit IPv6 option action field.</summary>
    internal static string GetIpv6OptActionDisplayText(byte action) => Ipv6OptActionTable[action & 0x03];

    #endregion

    #region IPv6 Routing Type Name (256 entries)

    private static readonly string[] Ipv6RoutingTypeNameTable = BuildIpv6RoutingTypeNameTable();

    /// <summary>Returns the short name for an IPv6 routing header type (0-255). Empty string for unknown.</summary>
    internal static string GetIpv6RoutingTypeName(byte routingType) => Ipv6RoutingTypeNameTable[routingType];

    private static string[] BuildIpv6RoutingTypeNameTable()
    {
        string[] table = new string[256];
        table[0] = "Source Route (deprecated)";
        table[1] = "Nimrod (deprecated)";
        table[2] = "Mobile IPv6";
        table[3] = "RPL Source Route";
        table[4] = "Segment Routing (SRH)";
        table[5] = "CRH-16";
        table[6] = "CRH-32";
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv6 Routing Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] Ipv6RoutingTypeDisplayTextTable = BuildIpv6RoutingTypeDisplayTextTable();

    /// <summary>Returns preformatted display text for an IPv6 routing header type.</summary>
    internal static string GetIpv6RoutingTypeDisplayText(byte routingType) => Ipv6RoutingTypeDisplayTextTable[routingType];

    private static string[] BuildIpv6RoutingTypeDisplayTextTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = Ipv6RoutingTypeNameTable[i];
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString();
        }
        return table;
    }

    #endregion

    #region IPv6 Router Alert Display Text (match-based for known IANA values)

    /// <summary>
    /// Returns display text for an IPv6 Router Alert option value.
    /// Returns <see langword="null"/> for unknown values.
    /// </summary>
    internal static string? GetIpv6RouterAlertDisplayText(ushort value)
    {
        return value switch
        {
            0 => "MLD (0)",
            1 => "RSVP (1)",
            2 => "Active Networks (2)",
            4 => "NSIS/NATFW NSLP (4)",
            5 => "MPLS OAM (5)",
            65535 => "Reserved (65535)",
            _ => null,
        };
    }

    #endregion

    #region TCP Option Kind Display Text (256 entries for 8-bit field)

    private static readonly string[] TcpOptionNameTable = BuildTcpOptionNameTable();
    private static readonly string[] TcpOptionDisplayTextTable = BuildTcpOptionDisplayTextTable();

    /// <summary>Gets the name of a TCP option kind (e.g., "Maximum Segment Size").</summary>
    internal static string GetTcpOptionName(byte kind) => TcpOptionNameTable[kind];

    /// <summary>Gets the display text for a TCP option kind (e.g., "Maximum Segment Size (2)").</summary>
    internal static string GetTcpOptionDisplayText(byte kind) => TcpOptionDisplayTextTable[kind];

    private static string[] BuildTcpOptionNameTable()
    {
        string[] table = new string[256];
        // Fill with numeric fallback
        for (int i = 0; i < 256; i++)
        {
            table[i] = i.ToString();
        }

        // Well-known TCP option kinds (IANA registry)
        table[0] = "End of Option List";
        table[1] = "No-Operation";
        table[2] = "Maximum Segment Size";
        table[3] = "Window Scale";
        table[4] = "SACK Permitted";
        table[5] = "SACK";
        table[6] = "Echo";
        table[7] = "Echo Reply";
        table[8] = "Timestamps";
        table[9] = "Partial Order Connection Permitted";
        table[10] = "Partial Order Service Profile";
        table[11] = "CC";
        table[12] = "CC.NEW";
        table[13] = "CC.ECHO";
        table[14] = "Alternate Checksum Request";
        table[15] = "Alternate Checksum Data";
        table[19] = "MD5 Signature";
        table[20] = "SCPS Capabilities";
        table[27] = "Quick-Start Response";
        table[28] = "User Timeout Option";
        table[29] = "TCP Authentication Option";
        table[30] = "Multipath TCP";
        table[34] = "TCP Fast Open Cookie";
        table[172] = "Accurate ECN Order 0";
        table[174] = "Accurate ECN Order 1";
        table[253] = "RFC3692 Experiment 1";
        table[254] = "RFC3692 Experiment 2";

        return table;
    }

    private static string[] BuildTcpOptionDisplayTextTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = TcpOptionNameTable[i];
            table[i] = $"{name} ({i})";
        }
        return table;
    }
    #endregion
}
