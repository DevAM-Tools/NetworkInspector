// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
/// <para>
/// Design: a field's display text ("Name (number)") is read for every parsed packet. Formatting it on demand would
/// allocate one string per field per packet, so every text is formatted once here and the parse path only indexes an
/// array. A table is keyed by the full range of the wire field (byte: 256 entries, ushort: 65,536 entries), and
/// narrower bit fields are masked, so no input value can index out of range and no lookup needs a bounds branch of
/// its own. Values without a name fall back to their number, so the tables never contain <see langword="null"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> every table is built by the type initializer (which the runtime runs exactly once, under its
/// own lock) and is never modified afterwards, so all members are safe for concurrent use without synchronization.
/// Static field initializers run in textual order: a table that is derived from another table (the "display text"
/// tables built from the "name" tables) must stay declared below the table it reads.
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
    /// <remarks>
    /// The class has no explicit static constructor, so the runtime is allowed to defer the type initializer until
    /// the first static field access. <see cref="RuntimeHelpers.RunClassConstructor(RuntimeTypeHandle)"/> removes that
    /// freedom: the cost of building the 64K-entry tables is paid at a known point instead of inside the first packet.
    /// </remarks>
    internal static void EnsureInitialized() =>
        RuntimeHelpers.RunClassConstructor(typeof(DisplayTables).TypeHandle);

    #endregion

    #region DSCP Display Text (64 entries for 6-bit field)

    private static readonly string[] _DscpTable = _BuildDscpTable();

    /// <summary>Returns the display text for a DSCP value; only the low 6 bits are used.</summary>
    /// <param name="dscp">A DSCP value; the mask with 0x3F keeps any byte inside the 64-entry table.</param>
    internal static string GetDscpDisplayText(byte dscp) => _DscpTable[dscp & 0x3F];

    private static string[] _BuildDscpTable()
    {
        string[] table = new string[64];
        // Well-known DSCP values: class selectors (CSn), assured forwarding (AFxy) and expedited forwarding (EF).
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
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region ECN Display Text (4 entries for 2-bit field)

    // The order follows the 2-bit codepoints of RFC 3168: 01 is ECT(1) and 10 is ECT(0), so the index is the raw field value.
    private static readonly string[] _EcnTable =
    [
        "Not-ECT (0)",
        "ECT(1) (1)",
        "ECT(0) (2)",
        "CE (3)"
    ];

    /// <summary>Returns the display text for the 2-bit ECN field; only the low 2 bits are used.</summary>
    /// <param name="ecn">The ECN field value; the mask with 0x03 keeps any byte inside the table.</param>
    internal static string GetEcnDisplayText(byte ecn) => _EcnTable[ecn & 0x03];

    #endregion

    #region IP Protocol Display Text (256 entries for 8-bit field)

    private static readonly string[] _IpProtocolTable = _BuildIpProtocolTable();

    /// <summary>Returns the display text for an IP protocol / IPv6 next-header number; unnamed numbers print as the number.</summary>
    /// <param name="protocol">The 8-bit protocol number; the 256-entry table needs no range check.</param>
    internal static string GetIpProtocolDisplayText(byte protocol) => _IpProtocolTable[protocol];

    private static string[] _BuildIpProtocolTable()
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
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region EtherType Display Text (65,536 entries for zero-alloc u16 lookup)

    private static readonly string[] _EtherTypeTable = _BuildEtherTypeTable();

    /// <summary>Returns the display text for an EtherType; unnamed values print as lower-case "0xNNNN".</summary>
    /// <param name="etherType">The 16-bit EtherType; the 65,536-entry table needs no range check.</param>
    internal static string GetEtherTypeDisplayText(ushort etherType) => _EtherTypeTable[etherType];

    private static string[] _BuildEtherTypeTable()
    {
        // One-time cost: 65,536 references (512 KB on 64-bit, so a large-object-heap array that lives for the process)
        // plus one string per entry. Accepted so that every EtherType, named or not, is a single array read.
        string[] table = new string[65536];

        // Well-known EtherType values. The hex digits in the names are lower case to match the fallback format below.
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

    private static readonly string[] _HexU16Table = _BuildHexU16Table();

    /// <summary>Formats a 16-bit value as lower-case "0xNNNN" without allocating (the string is preformatted).</summary>
    /// <param name="value">The value to format.</param>
    internal static string FormatHexU16(ushort value) => _HexU16Table[value];

    private static string[] _BuildHexU16Table()
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
    /// <param name="value">The value to format; always printed with 8 lower-case hex digits.</param>
    // string.Create with a static lambda writes straight into the result string: no closure, no temporary buffer.
    // The two halves are copied from the u16 table (after its "0x" prefix) instead of formatting 8 digits by hand.
    internal static string FormatHexU32(uint value) =>
        string.Create(10, value, static (span, v) =>
        {
            span[0] = '0';
            span[1] = 'x';
            _HexU16Table[v >> 16].AsSpan(2).CopyTo(span[2..]);
            _HexU16Table[v & 0xFFFF].AsSpan(2).CopyTo(span[6..]);
        });

    /// <summary>
    /// Formats a 32-bit unsigned integer as "0xNNN" (3-digit hex) for standard CAN IDs.
    /// Single allocation (the result string), no intermediate allocations.
    /// </summary>
    /// <param name="value">The value to format; only the low 12 bits are printed (an 11-bit CAN ID always fits).</param>
    internal static string FormatHexU12(uint value) =>
        string.Create(5, value, static (span, v) =>
        {
            span[0] = '0';
            span[1] = 'x';
            // Use lower 3 hex digits from the U16 table (skip "0x" prefix + first digit). The mask keeps the index
            // below 0x1000, so the skipped first digit is always '0'.
            _HexU16Table[v & 0x0FFF].AsSpan(3).CopyTo(span[2..]);
        });

    #endregion

    #region Hex u8 Display Text (256 entries for zero-alloc u8 hex formatting)

    private static readonly string[] _HexU8Table = _BuildHexU8Table();

    /// <summary>Formats a byte as lower-case "0xNN" without allocating (the string is preformatted).</summary>
    /// <param name="value">The value to format.</param>
    internal static string FormatHexU8(byte value) => _HexU8Table[value];

    private static string[] _BuildHexU8Table()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = $"0x{i:x2}";
        }
        return table;
    }

    #endregion

    #region Byte Count Display Text ("N bytes", 0..2048)

    // Why a table: container representations such as "64 bytes" are needed for every parsed packet of several
    // protocols (IPv4 header length, ICMP and ICMPv6 message length, ARP message length). Formatting the number into a
    // fresh string per packet is garbage on the eager parse path, which must allocate nothing. The value range of the
    // lengths that occur is small and dense (a header or a message that fits a standard MTU), so one shared string per
    // length is cheaper than any per-packet work. 2049 entries cost about 100 KB once at type initialisation and
    // cover every IPv4 header length (at most 60), every ARP message (at most 1028) and every ICMP message that fits
    // a 2 KiB frame; a larger jumbo-frame message takes the allocating fallback, which is rare by definition.
    private static readonly string[] _ByteCountTable = _BuildByteCountTable();

    /// <summary>
    /// Returns precomputed "N bytes" display text for lengths 0-2048. Falls back to dynamic formatting (one
    /// allocation) for larger or negative values.
    /// </summary>
    /// <param name="byteCount">The length in bytes; negative values take the fallback path.</param>
    internal static string GetByteCountDisplayText(int byteCount)
    {
        // One unsigned comparison covers both bounds: a negative length wraps to a huge uint and misses the table.
        if ((uint)byteCount < (uint)_ByteCountTable.Length)
        {
            return _ByteCountTable[byteCount];
        }
        return string.Create(CultureInfo.InvariantCulture, $"{byteCount} bytes");
    }

    /// <summary>
    /// Returns precomputed "N bytes" display text for IPv4 header lengths. Same table as
    /// <see cref="GetByteCountDisplayText"/>; the name is kept so the IPv4 parser does not change.
    /// </summary>
    /// <param name="headerLength">The header length in bytes; negative values take the fallback path.</param>
    internal static string GetHeaderLengthDisplayText(int headerLength) => GetByteCountDisplayText(headerLength);

    private static string[] _BuildByteCountTable()
    {
        // Entries below 20 are filled as well so a malformed (too short) header still prints without the allocating
        // fallback; the build runs once, so the formatting cost here is irrelevant.
        string[] table = new string[2049];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = string.Create(CultureInfo.InvariantCulture, $"{i} bytes");
        }
        return table;
    }

    #endregion

    #region Decimal Text (0..255)

    // Why a table: a byte that has no name (an unnamed ICMP code, the code 0 of every echo) must still print as its
    // number. Calling ToString per packet allocates a string for the most frequent ICMP packets, so the 256 possible
    // texts are built once and shared.
    private static readonly string[] _DecimalTable = _BuildDecimalTable();

    /// <summary>Returns the shared decimal text of a byte value; never allocates.</summary>
    /// <param name="value">The value to print.</param>
    internal static string GetDecimalText(byte value) => _DecimalTable[value];

    private static string[] _BuildDecimalTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region VLAN Priority Display Text (8 entries for 3-bit field)

    private static readonly string[] _VlanPriorityTable =
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

    /// <summary>Returns the display text for the 3-bit VLAN priority code point (IEEE 802.1Q PCP); only the low 3 bits are used.</summary>
    /// <param name="pcp">The PCP value; the mask with 0x07 keeps any byte inside the table.</param>
    /// <remarks>The names follow the default 802.1Q priority-to-traffic-class naming, where 1 (Background) ranks below 0 (Best Effort).</remarks>
    internal static string GetVlanPriorityDisplayText(byte pcp) => _VlanPriorityTable[pcp & 0x07];

    #endregion

    #region ARP Opcode Display Text

    /// <summary>
    /// Returns display text for ARP opcodes: the registered values 0 to 25 and 65535 get a name, others fall back to
    /// the number.
    /// </summary>
    /// <param name="opcode">The 16-bit ARP operation code.</param>
    /// <remarks>
    /// A switch instead of a table: the named values are few and dense, and the 16-bit opcode of a normal ARP packet
    /// is 1 or 2, which the compiler resolves with a jump table. Only an unknown opcode allocates (its number
    /// string), and that is a malformed or experimental packet, not the hot case. The names follow the IANA
    /// registry (RFC 826 requests and replies, RFC 903 RARP, RFC 1293 InARP, RFC 2225 ATMARP NAK, RFC 2022 MARS,
    /// RFC 2176 MAPOS UNARP).
    /// </remarks>
    internal static string GetArpOpcodeDisplayText(ushort opcode) => opcode switch
    {
        0 => "reserved (0)",
        1 => "request (1)",
        2 => "reply (2)",
        3 => "RARP request (3)",
        4 => "RARP reply (4)",
        5 => "DRARP request (5)",
        6 => "DRARP reply (6)",
        7 => "DRARP error (7)",
        8 => "InARP request (8)",
        9 => "InARP reply (9)",
        10 => "ARP NAK (10)",
        11 => "MARS request (11)",
        12 => "MARS multi (12)",
        13 => "MARS mserv (13)",
        14 => "MARS join (14)",
        15 => "MARS leave (15)",
        16 => "MARS NAK (16)",
        17 => "MARS unserv (17)",
        18 => "MARS sjoin (18)",
        19 => "MARS sleave (19)",
        20 => "MARS grouplist request (20)",
        21 => "MARS grouplist reply (21)",
        22 => "MARS redirect map (22)",
        23 => "MAPOS UNARP (23)",
        24 => "experimental 1 (24)",
        25 => "experimental 2 (25)",
        65535 => "reserved (65535)",
        _ => opcode.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Returns the packet-info summary of an ARP packet whose opcode has no request/reply wording of its own
    /// (DRARP error, ATMARP NAK and every opcode the parser does not know).
    /// </summary>
    /// <param name="opcode">The 16-bit ARP operation code.</param>
    /// <remarks>Allocates for unknown opcodes only; the registered request and reply forms never reach this method.</remarks>
    internal static string GetArpOpcodeInfoText(ushort opcode) => opcode switch
    {
        7 => "DRARP Error",
        10 => "ARP NAK",
        _ => string.Create(CultureInfo.InvariantCulture, $"Unknown ARP opcode 0x{opcode:x4}"),
    };

    #endregion

    #region ARP Hardware Type Display Text (258 entries: registered values 0..38, 256, 257)

    // Why 258 entries and not 65536: only the registered hardware types are named; the rest print as numbers. A dense
    // table up to the highest registered value (257) keeps the lookup a single bounds check and costs under 3 KB.
    private static readonly string[] _ArpHwTypeTable = _BuildArpHwTypeTable();

    /// <summary>Returns the display text for a 16-bit ARP hardware type.</summary>
    /// <param name="hwType">
    /// The hardware type. Values inside the table use it; larger values are legal on the wire but unnamed, so they take
    /// the allocating numeric fallback instead of a 64K-entry table for a rare case.
    /// </param>
    internal static string GetArpHwTypeDisplayText(ushort hwType)
    {
        if (hwType < _ArpHwTypeTable.Length)
        {
            return _ArpHwTypeTable[hwType];
        }
        return hwType.ToString(CultureInfo.InvariantCulture);
    }

    private static string[] _BuildArpHwTypeTable()
    {
        string[] table = new string[258];
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
        table[21] = "ATM (21)";
        table[22] = "MIL-STD-188-220 (22)";
        table[23] = "Metricom STRIP (23)";
        table[24] = "IEEE 1394.1995 (24)";
        table[25] = "MAPOS (25)";
        table[26] = "Twinaxial (26)";
        table[27] = "EUI-64 (27)";
        table[28] = "HIPARP (28)";
        table[29] = "IP and ARP over ISO 7816-3 (29)";
        table[30] = "ARPSec (30)";
        table[31] = "IPsec tunnel (31)";
        table[32] = "InfiniBand (32)";
        table[33] = "TIA-102 Project 25 Common Air Interface (33)";
        table[34] = "Wiegand Interface (34)";
        table[35] = "Pure IP (35)";
        table[36] = "HW_EXP1 (36)";
        table[37] = "HFI (37)";
        table[38] = "Unified Bus (38)";
        table[256] = "HW_EXP2 (256)";
        table[257] = "AEthernet (257)";

        // Unregistered values print as the number. Entries are shared strings, so the lookup never allocates.
        for (int i = 0; i < table.Length; i++)
        {
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region ICMP Type Display Text (256 entries for u8 type field)

    private static readonly string[] _IcmpTypeTable = _BuildIcmpTypeTable();

    /// <summary>Returns the display text for an ICMPv4 type; unnamed types print as the number.</summary>
    /// <param name="type">The 8-bit ICMPv4 type.</param>
    internal static string GetIcmpTypeDisplayText(byte type) => _IcmpTypeTable[type];

    private static string[] _BuildIcmpTypeTable()
    {
        string[] table = new string[256];
        table[0] = "Echo (ping) reply (0)";
        table[1] = "Reserved (1)";
        table[2] = "Reserved (2)";
        table[3] = "Destination unreachable (3)";
        table[4] = "Source quench (4)";
        table[5] = "Redirect (5)";
        table[6] = "Alternate host address (6)";
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
        table[19] = "Reserved (for security) (19)";
        table[30] = "Traceroute (30)";
        table[31] = "Datagram Conversion Error (31)";
        table[32] = "Mobile Host Redirect (32)";
        table[33] = "IPv6 Where-Are-You (33)";
        table[34] = "IPv6 I-Am-Here (34)";
        table[35] = "Mobile Registration Request (35)";
        table[36] = "Mobile Registration Reply (36)";
        table[37] = "Domain Name Request (37)";
        table[38] = "Domain Name Reply (38)";
        table[39] = "SKIP (39)";
        table[40] = "Photuris (40)";
        table[41] = "Experimental mobility protocols (41)";
        table[42] = "Extended echo request (42)";
        table[43] = "Extended echo reply (43)";

        // Unassigned types print as the number; the entries are shared strings, so a lookup never allocates.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    // Container titles ("Internet Control Message Protocol, <type>"): the title depends on the type byte only, so the
    // 256 possible strings are concatenated once here and shared. Without the table the eager parse path would box a
    // (prefix, type text) tuple for every ICMP packet only to build the same few strings when the title is read.
    private static readonly string[] _IcmpContainerTitleTable = _BuildPrefixedTable("Internet Control Message Protocol, ", _IcmpTypeTable);

    /// <summary>Returns the shared container title of an ICMPv4 message, for example "Internet Control Message Protocol, Echo (ping) request (8)".</summary>
    /// <param name="type">The 8-bit ICMPv4 type.</param>
    internal static string GetIcmpContainerTitle(byte type) => _IcmpContainerTitleTable[type];

    /// <summary>Concatenates a prefix with every entry of a 256-entry text table once, so lookups never allocate.</summary>
    /// <param name="prefix">Text put in front of every entry.</param>
    /// <param name="source">The table whose entries are prefixed; must be initialised before this call (static field order).</param>
    private static string[] _BuildPrefixedTable(string prefix, string[] source)
    {
        string[] table = new string[source.Length];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = string.Concat(prefix, source[i]);
        }
        return table;
    }

    #endregion

    #region ICMP Code Display Text (per type)

    /// <summary>
    /// Returns display text for ICMP code values, contextualized by type.
    /// </summary>
    /// <param name="type">The ICMPv4 type; the meaning of a code depends on it.</param>
    /// <param name="code">The ICMPv4 code.</param>
    /// <remarks>
    /// Types 3, 5, 6, 9, 11, 12, 40, 42 and 43 define named codes; every other combination prints the plain number.
    /// Unlike the type tables this is a nested switch (type x code is sparse, a table would be mostly empty). The
    /// number fallback comes from <see cref="GetDecimalText"/>, so an unnamed code (and the code 0 of every echo, the
    /// most frequent ICMP packet) does not allocate a string.
    /// </remarks>
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
            _ => GetDecimalText(code)
        },
        5 => code switch // Redirect
        {
            0 => "Redirect for network (0)",
            1 => "Redirect for host (1)",
            2 => "Redirect for ToS and network (2)",
            3 => "Redirect for ToS and host (3)",
            _ => GetDecimalText(code)
        },
        11 => code switch // Time Exceeded
        {
            0 => "TTL exceeded in transit (0)",
            1 => "Fragment reassembly time exceeded (1)",
            _ => GetDecimalText(code)
        },
        12 => code switch // Parameter Problem
        {
            0 => "Pointer indicates the error (0)",
            1 => "Missing a required option (1)",
            2 => "Bad length (2)",
            _ => GetDecimalText(code)
        },
        6 => code switch // Alternate Host Address
        {
            0 => "Alternate address for host (0)",
            _ => GetDecimalText(code)
        },
        9 => code switch // Router Advertisement (RFC 1256, RFC 3344)
        {
            0 => "Normal router advertisement (0)",
            16 => "Does not route common traffic (16)",
            _ => GetDecimalText(code)
        },
        40 => code switch // Photuris
        {
            0 => "Bad SPI (0)",
            1 => "Authentication Failed (1)",
            2 => "Decompression Failed (2)",
            3 => "Decryption Failed (3)",
            4 => "Need Authentication (4)",
            5 => "Need Authorization (5)",
            _ => GetDecimalText(code)
        },
        42 => code switch // Extended Echo Request (RFC 8335)
        {
            0 => "No error (0)",
            _ => GetDecimalText(code)
        },
        43 => code switch // Extended Echo Reply (RFC 8335)
        {
            0 => "No error (0)",
            1 => "Malformed Query (1)",
            2 => "No Such Interface (2)",
            3 => "No Such Table Entry (3)",
            4 => "Multiple Interfaces Satisfy Query (4)",
            _ => GetDecimalText(code)
        },
        _ => GetDecimalText(code)
    };

    #endregion

    #region ICMPv6 Type Display Text (256 entries for u8 type field)

    private static readonly string[] _Icmpv6TypeTable = _BuildIcmpv6TypeTable();

    /// <summary>Returns the display text for an ICMPv6 type; unnamed types print as the number.</summary>
    /// <param name="type">The 8-bit ICMPv6 type (0-127 are error messages, 128-255 informational messages).</param>
    internal static string GetIcmpv6TypeDisplayText(byte type) => _Icmpv6TypeTable[type];

    private static string[] _BuildIcmpv6TypeTable()
    {
        string[] table = new string[256];
        // Error messages (0-127)
        table[1] = "Destination Unreachable (1)";
        table[2] = "Packet Too Big (2)";
        table[3] = "Time Exceeded (3)";
        table[4] = "Parameter Problem (4)";
        table[100] = "Private experimentation (100)";
        table[101] = "Private experimentation (101)";
        table[127] = "Reserved for expansion of ICMPv6 error messages (127)";

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
        table[144] = "Home Agent Address Discovery Request (144)";
        table[145] = "Home Agent Address Discovery Reply (145)";
        table[146] = "Mobile Prefix Solicitation (146)";
        table[147] = "Mobile Prefix Advertisement (147)";
        table[148] = "Certification Path Solicitation (148)";
        table[149] = "Certification Path Advertisement (149)";
        table[150] = "Experimental Mobility (150)";
        table[151] = "Multicast Router Advertisement (151)";
        table[152] = "Multicast Router Solicitation (152)";
        table[153] = "Multicast Router Termination (153)";
        table[154] = "FMIPv6 (154)";
        table[155] = "RPL Control (155)";
        table[156] = "Locator Update (156)";
        table[157] = "Duplicate Address Request (157)";
        table[158] = "Duplicate Address Confirmation (158)";
        table[159] = "MPL Control Message (159)";
        table[160] = "Extended Echo request (160)";
        table[161] = "Extended Echo reply (161)";
        table[200] = "Private experimentation (200)";
        table[201] = "Private experimentation (201)";
        table[255] = "Reserved for expansion of ICMPv6 informational messages (255)";

        // Unassigned types print as the number; the entries are shared strings, so a lookup never allocates.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    // Same idea as the ICMPv4 title table: the title depends on the type byte only, so it is built once.
    private static readonly string[] _Icmpv6ContainerTitleTable = _BuildPrefixedTable("Internet Control Message Protocol v6, ", _Icmpv6TypeTable);

    /// <summary>Returns the shared container title of an ICMPv6 message, for example "Internet Control Message Protocol v6, Echo (ping) request (128)".</summary>
    /// <param name="type">The 8-bit ICMPv6 type.</param>
    internal static string GetIcmpv6ContainerTitle(byte type) => _Icmpv6ContainerTitleTable[type];

    /// <summary>
    /// Returns display text for an ICMPv6 code, contextualized by type (RFC 4443, RFC 2894, RFC 4620, RFC 8335).
    /// </summary>
    /// <param name="type">The ICMPv6 type; the meaning of a code depends on it.</param>
    /// <param name="code">The ICMPv6 code.</param>
    /// <remarks>
    /// A nested switch for the same reason as <see cref="GetIcmpCodeDisplayText"/>: type x code is sparse. A code
    /// without a name prints its number from <see cref="GetDecimalText"/>, so no path allocates.
    /// </remarks>
    internal static string GetIcmpv6CodeDisplayText(byte type, byte code) => type switch
    {
        1 => code switch // Destination Unreachable
        {
            0 => "No route to destination (0)",
            1 => "Administratively prohibited (1)",
            2 => "Beyond scope of source address (2)",
            3 => "Address unreachable (3)",
            4 => "Port unreachable (4)",
            5 => "Source address failed ingress/egress policy (5)",
            6 => "Reject route to destination (6)",
            7 => "Error in Source Routing Header (7)",
            8 => "Headers too long (8)",
            _ => GetDecimalText(code)
        },
        3 => code switch // Time Exceeded
        {
            0 => "Hop limit exceeded in transit (0)",
            1 => "Fragment reassembly time exceeded (1)",
            _ => GetDecimalText(code)
        },
        4 => code switch // Parameter Problem
        {
            0 => "Erroneous header field encountered (0)",
            1 => "Unrecognized Next Header type encountered (1)",
            2 => "Unrecognized IPv6 option encountered (2)",
            3 => "IPv6 First Fragment has incomplete IPv6 Header Chain (3)",
            4 => "SR Upper-layer Header Error (4)",
            5 => "Unrecognized Next Header type encountered by intermediate node (5)",
            6 => "Extension header too big (6)",
            7 => "Extension header chain too long (7)",
            8 => "Too many extension headers (8)",
            9 => "Too many options in extension header (9)",
            10 => "Option too big (10)",
            _ => GetDecimalText(code)
        },
        138 => code switch // Router Renumbering
        {
            0 => "Command (0)",
            1 => "Result (1)",
            255 => "Sequence number reset (255)",
            _ => GetDecimalText(code)
        },
        139 => code switch // Node Information Query
        {
            0 => "Query subject = IPv6 addresses (0)",
            1 => "Query subject = DNS name or empty (1)",
            2 => "Query subject = IPv4 addresses (2)",
            _ => GetDecimalText(code)
        },
        140 => code switch // Node Information Reply
        {
            0 => "Successful (0)",
            1 => "Refused (1)",
            2 => "Unknown query type (2)",
            _ => GetDecimalText(code)
        },
        160 => code switch // Extended Echo request
        {
            0 => "No error (0)",
            _ => GetDecimalText(code)
        },
        161 => code switch // Extended Echo reply
        {
            0 => "No error (0)",
            1 => "Malformed Query (1)",
            2 => "No Such Interface (2)",
            3 => "No Such Table Entry (3)",
            4 => "Multiple Interfaces Satisfy Query (4)",
            _ => GetDecimalText(code)
        },
        _ => GetDecimalText(code)
    };

    #endregion

    #region NDP Option Display Text (256 entries each)

    // Why tables: an NDP message carries several options, and every option container shows its name and its length.
    // Both texts depend on one byte, so building them per option would allocate two strings (or two lazy-text boxes)
    // per option of every materialised Router Advertisement. 256 shared strings each remove that work.
    private static readonly string[] _NdpOptionTable = _BuildNdpOptionTable();
    private static readonly string[] _NdpOptionLengthTable = _BuildNdpOptionLengthTable();

    /// <summary>Returns the option container text, for example "Prefix Information (3)"; unassigned types print "Unknown Option (n)".</summary>
    /// <param name="optionType">The NDP option type byte.</param>
    internal static string GetNdpOptionDisplayText(byte optionType) => _NdpOptionTable[optionType];

    /// <summary>Returns the option length text, for example "4 (32 bytes)"; the length is counted in 8-byte units.</summary>
    /// <param name="lengthUnits">The option length byte (units of 8 bytes, including type and length).</param>
    internal static string GetNdpOptionLengthDisplayText(byte lengthUnits) => _NdpOptionLengthTable[lengthUnits];

    private static string[] _BuildNdpOptionTable()
    {
        string[] table = new string[256];
        table[1] = "Source Link-Layer Address";
        table[2] = "Target Link-Layer Address";
        table[3] = "Prefix Information";
        table[4] = "Redirected Header";
        table[5] = "MTU";
        table[6] = "NBMA Shortcut Limit Option";
        table[7] = "Advertisement Interval";
        table[8] = "Home Agent Information";
        table[9] = "Source Address List";
        table[10] = "Target Address List";
        table[11] = "CGA";
        table[12] = "RSA Signature";
        table[13] = "Timestamp";
        table[14] = "Nonce";
        table[15] = "Trust Anchor";
        table[16] = "Certificate";
        table[17] = "IP Address/Prefix Option";
        table[18] = "New Router Prefix Information";
        table[19] = "Link-layer Address";
        table[20] = "Neighbor Advertisement Acknowledgment";
        table[21] = "PvD ID";
        table[23] = "MAP";
        table[24] = "Route Information";
        table[25] = "RDNSS";
        table[26] = "RA Flags Extension";
        table[27] = "Handover Key Request";
        table[28] = "Handover Key Reply";
        table[29] = "Handover Assist Information";
        table[30] = "Mobile Node Identifier Option";
        table[31] = "DNS Search List";
        table[32] = "Proxy Signature (PS)";
        table[33] = "Address Registration Option";
        table[34] = "6LoWPAN Context Option";
        table[35] = "Authoritative Border Router";
        table[36] = "6LoWPAN Capability Indication Option";
        table[37] = "DHCP Captive-Portal";
        table[38] = "PREF64 Option";
        table[39] = "Crypto-ID Parameters Option (CIPO)";
        table[40] = "NDP Signature Option (NDPSO)";
        table[41] = "Resource Directory Address Option";
        table[42] = "Consistent Uptime Option";
        table[138] = "CARD Request";
        table[139] = "CARD Reply";
        table[144] = "Encrypted DNS Option";
        table[253] = "RFC3692-style Experiment 1";
        table[254] = "RFC3692-style Experiment 2";

        // Every entry ends with the numeric type so the text stays unambiguous when two registrations share a name.
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = string.Create(CultureInfo.InvariantCulture, $"{table[i] ?? "Unknown Option"} ({i})");
        }
        return table;
    }

    private static string[] _BuildNdpOptionLengthTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = string.Create(CultureInfo.InvariantCulture, $"{i} ({i * 8} bytes)");
        }
        return table;
    }

    #endregion

    #region SLL Packet Type Display Text

    /// <summary>
    /// Returns a display string for the SLL packet type field.
    /// Values: 0=Unicast, 1=Broadcast, 2=Multicast, 3=Other host, 4=Sent by us.
    /// </summary>
    /// <param name="pktType">The packet type of a Linux cooked capture (SLL) header.</param>
    /// <remarks>Only values 0-4 are named; any other value allocates a new "Unknown (n)" string (rare, so no table).</remarks>
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

    private static readonly string[] _LlcSapTable = _BuildLlcSapTable();

    /// <summary>
    /// Returns a display string for the LLC SAP field (DSAP or SSAP).
    /// The full byte is looked up: odd values name group addresses (for example 0x03, 0xFF), so the I/G or C/R bit
    /// is part of the key.
    /// </summary>
    /// <param name="sap">The raw 8-bit DSAP or SSAP value, including its lowest bit.</param>
    internal static string GetLlcSapDisplayText(byte sap) => _LlcSapTable[sap];

    private static string[] _BuildLlcSapTable()
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

    private static readonly string[] _IpOptionNameTable = _BuildIpOptionNameTable();

    /// <summary>Returns the short name for an IPv4 option type (0-255). Empty string for unknown types.</summary>
    /// <param name="optType">The full option type byte (copied flag, class and number together).</param>
    internal static string GetIpOptionTypeName(byte optType) => _IpOptionNameTable[optType];

    private static string[] _BuildIpOptionNameTable()
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

        // Unknown types become empty strings (never null): the display table below tests Length > 0 to tell a named
        // option from an unnamed one, and callers can use the name without a null check.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv4 Option Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] _IpOptionDisplayTextTable = _BuildIpOptionDisplayTextTable();

    /// <summary>Returns the preformatted display text for an IPv4 option type.</summary>
    /// <param name="optType">The full option type byte.</param>
    internal static string GetIpOptionTypeDisplayText(byte optType) => _IpOptionDisplayTextTable[optType];

    private static string[] _BuildIpOptionDisplayTextTable()
    {
        // Reads _IpOptionNameTable, so that field must be declared (and therefore initialized) before this one.
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = _IpOptionNameTable[i];
            // Known options: "Name (num)", unknown options: "num"
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region IPv4 Option Class (4 entries for the 2-bit class field)

    private static readonly string[] _IpOptionClassTable =
    [
        "Control (0)",
        "Reserved (1)",
        "Debugging and Measurement (2)",
        "Reserved (3)",
    ];

    /// <summary>Returns display text for the 2-bit option class field (0-3); only the low 2 bits are used.</summary>
    /// <param name="optClass">The option class (bits 6-5 of the option type byte, already shifted down).</param>
    internal static string GetIpOptionClassDisplayText(byte optClass)
        => _IpOptionClassTable[optClass & 0x03];

    #endregion

    #region IPv4 Timestamp Flag (16 entries for the 4-bit flag field)

    private static readonly string[] _IpTimestampFlagTable = _BuildIpTimestampFlagTable();

    /// <summary>Returns display text for the 4-bit timestamp flag field; only the low 4 bits are used.</summary>
    /// <param name="flag">The flag nibble of the IPv4 Time Stamp option (0, 1 and 3 are defined; others print as "Unknown").</param>
    internal static string GetIpTimestampFlagDisplayText(byte flag)
        => _IpTimestampFlagTable[flag & 0x0F];

    private static string[] _BuildIpTimestampFlagTable()
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
    /// <param name="value">The 16-bit value of the IPv4 Router Alert option (RFC 2113).</param>
    /// <remarks>
    /// Returns <see langword="null"/> instead of a number string so the caller decides how to print an unassigned
    /// value. A switch expression, not a table, because only a few ranges are assigned; the nesting-level range
    /// allocates its formatted text.
    /// </remarks>
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

    private static readonly string[] _Ipv6OptionNameTable = _BuildIpv6OptionNameTable();

    /// <summary>Returns the short name for an IPv6 option type (0-255). Empty string for unknown.</summary>
    /// <param name="optType">The full option type byte (action bits, change bit and number together).</param>
    internal static string GetIpv6OptionTypeName(byte optType) => _Ipv6OptionNameTable[optType];

    private static string[] _BuildIpv6OptionNameTable()
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

        // Unknown types become empty strings (never null), as in the IPv4 option name table.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv6 Extension Header Option Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] _Ipv6OptionDisplayTextTable = _BuildIpv6OptionDisplayTextTable();

    /// <summary>Returns preformatted display text for an IPv6 option type.</summary>
    /// <param name="optType">The full option type byte.</param>
    internal static string GetIpv6OptionTypeDisplayText(byte optType) => _Ipv6OptionDisplayTextTable[optType];

    private static string[] _BuildIpv6OptionDisplayTextTable()
    {
        // Reads _Ipv6OptionNameTable, so that field must be declared (and therefore initialized) before this one.
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = _Ipv6OptionNameTable[i];
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region IPv6 Option Action Display Text (4 entries for 2-bit action field)

    private static readonly string[] _Ipv6OptActionTable =
    [
        "Skip and continue (0)",
        "Discard (1)",
        "Discard and send ICMP (2)",
        "Discard and send ICMP if not multicast (3)"
    ];

    /// <summary>Returns display text for the 2-bit IPv6 option action field; only the low 2 bits are used.</summary>
    /// <param name="action">
    /// The two highest bits of an IPv6 option type, shifted down: they tell a node what to do with a packet whose
    /// option it does not recognize (RFC 8200 section 4.2).
    /// </param>
    internal static string GetIpv6OptActionDisplayText(byte action) => _Ipv6OptActionTable[action & 0x03];

    #endregion

    #region IPv6 Routing Type Name (256 entries)

    private static readonly string[] _Ipv6RoutingTypeNameTable = _BuildIpv6RoutingTypeNameTable();

    /// <summary>Returns the short name for an IPv6 routing header type (0-255). Empty string for unknown.</summary>
    /// <param name="routingType">The 8-bit routing type of an IPv6 Routing header.</param>
    internal static string GetIpv6RoutingTypeName(byte routingType) => _Ipv6RoutingTypeNameTable[routingType];

    private static string[] _BuildIpv6RoutingTypeNameTable()
    {
        string[] table = new string[256];
        table[0] = "Source Route (deprecated)";
        table[1] = "Nimrod (deprecated)";
        table[2] = "Mobile IPv6";
        table[3] = "RPL Source Route";
        table[4] = "Segment Routing (SRH)";
        table[5] = "CRH-16";
        table[6] = "CRH-32";

        // Unknown types become empty strings (never null), as in the option name tables.
        for (int i = 0; i < 256; i++)
        {
            table[i] ??= "";
        }
        return table;
    }

    #endregion

    #region IPv6 Routing Type Display Text (256 entries: "Name (num)" or "num")

    private static readonly string[] _Ipv6RoutingTypeDisplayTextTable = _BuildIpv6RoutingTypeDisplayTextTable();

    /// <summary>Returns preformatted display text for an IPv6 routing header type.</summary>
    /// <param name="routingType">The 8-bit routing type of an IPv6 Routing header.</param>
    internal static string GetIpv6RoutingTypeDisplayText(byte routingType) => _Ipv6RoutingTypeDisplayTextTable[routingType];

    private static string[] _BuildIpv6RoutingTypeDisplayTextTable()
    {
        // Reads _Ipv6RoutingTypeNameTable, so that field must be declared (and therefore initialized) before this one.
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            string name = _Ipv6RoutingTypeNameTable[i];
            table[i] = name.Length > 0 ? $"{name} ({i})" : i.ToString(CultureInfo.InvariantCulture);
        }
        return table;
    }

    #endregion

    #region IPv6 Router Alert Display Text (match-based for known IANA values)

    /// <summary>
    /// Returns display text for an IPv6 Router Alert option value.
    /// Returns <see langword="null"/> for unknown values.
    /// </summary>
    /// <param name="value">The 16-bit value of the IPv6 Router Alert option (RFC 2711). Unknown values yield <see langword="null"/>
    /// so the caller decides how to print them.</param>
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

    // The display table is built from the name table, so the name table must be declared first.
    private static readonly string[] _TcpOptionNameTable = _BuildTcpOptionNameTable();
    private static readonly string[] _TcpOptionDisplayTextTable = _BuildTcpOptionDisplayTextTable();

    /// <summary>
    /// Gets the name of a TCP option kind (e.g., "Maximum Segment Size"). Unlike the IPv4 and IPv6 option name tables,
    /// an unnamed kind yields its decimal number, not an empty string.
    /// </summary>
    /// <param name="kind">The 8-bit TCP option kind.</param>
    internal static string GetTcpOptionName(byte kind) => _TcpOptionNameTable[kind];

    /// <summary>Gets the display text for a TCP option kind (e.g., "Maximum Segment Size (2)").</summary>
    /// <param name="kind">The 8-bit TCP option kind.</param>
    internal static string GetTcpOptionDisplayText(byte kind) => _TcpOptionDisplayTextTable[kind];

    private static string[] _BuildTcpOptionNameTable()
    {
        string[] table = new string[256];

        // Fill with numeric fallback first (here the names overwrite it, the reverse of the other tables, which fill
        // the gaps afterwards); the display table below detects an unnamed kind by comparing name and number.
        for (int i = 0; i < 256; i++)
        {
            table[i] = i.ToString(CultureInfo.InvariantCulture);
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

    private static string[] _BuildTcpOptionDisplayTextTable()
    {
        string[] table = new string[256];
        for (int i = 0; i < 256; i++)
        {
            // An unnamed kind is its number already; print it once, as the IPv4 and IPv6 option tables do.
            string name = _TcpOptionNameTable[i];
            string number = i.ToString(CultureInfo.InvariantCulture);
            table[i] = name == number ? number : $"{name} ({i})";
        }
        return table;
    }
    #endregion
}
