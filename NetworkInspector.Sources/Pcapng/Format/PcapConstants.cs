// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Constants for the PCAPNG and legacy PCAP file formats.
/// Includes block types, magic numbers, option codes, and default values.
/// </summary>
internal static class PcapConstants
{
    #region Block type constants (PCAPNG)

    /// <summary>Section Header Block — marks the start of a new section.</summary>
    internal const uint BlockTypeSHB = 0x0A0D_0D0A;

    /// <summary>Interface Description Block — describes a capture interface.</summary>
    internal const uint BlockTypeIDB = 0x0000_0001;

    /// <summary>Packet Block (obsolete, replaced by EPB).</summary>
    internal const uint BlockTypePB = 0x0000_0002;

    /// <summary>Simple Packet Block — minimal packet format, no options.</summary>
    internal const uint BlockTypeSPB = 0x0000_0003;

    /// <summary>Name Resolution Block.</summary>
    internal const uint BlockTypeNRB = 0x0000_0004;

    /// <summary>Interface Statistics Block.</summary>
    internal const uint BlockTypeISB = 0x0000_0005;

    /// <summary>Enhanced Packet Block — standard packet format with timestamps and options.</summary>
    internal const uint BlockTypeEPB = 0x0000_0006;

    /// <summary>IRIG Timestamp Block (experimental).</summary>
    internal const uint BlockTypeITB = 0x0000_0007;

    /// <summary>ARINC 429 Block (experimental).</summary>
    internal const uint BlockTypeArinc429 = 0x0000_0008;

    /// <summary>Decryption Secrets Block.</summary>
    internal const uint BlockTypeDSB = 0x0000_000A;

    /// <summary>Custom Block (copyable).</summary>
    internal const uint BlockTypeCBCopy = 0x0000_0BAD;

    /// <summary>Custom Block (non-copyable).</summary>
    internal const uint BlockTypeCBNoCopy = 0x4000_0BAD;

    #endregion

    #region Magic numbers

    /// <summary>PCAPNG byte-order magic (native little-endian).</summary>
    internal const uint PcapngMagic = 0x1A2B_3C4D;

    /// <summary>PCAPNG byte-order magic (swapped — file is big-endian).</summary>
    internal const uint PcapngSwappedMagic = 0x4D3C_2B1A;

    /// <summary>Legacy PCAP magic — microsecond timestamps, native byte order.</summary>
    internal const uint PcapMagicMicros = 0xA1B2_C3D4;

    /// <summary>Legacy PCAP magic — microsecond timestamps, swapped byte order.</summary>
    internal const uint PcapSwappedMagicMicros = 0xD4C3_B2A1;

    /// <summary>Legacy PCAP magic — nanosecond timestamps, native byte order.</summary>
    internal const uint PcapMagicNanos = 0xA1B2_3C4D;

    /// <summary>Legacy PCAP magic — nanosecond timestamps, swapped byte order.</summary>
    internal const uint PcapSwappedMagicNanos = 0x4D3C_B2A1;

    #endregion

    #region Option codes

    /// <summary>End of options (terminator).</summary>
    internal const ushort OptEndOfOpt = 0;

    /// <summary>Comment option (any block).</summary>
    internal const ushort OptComment = 1;

    // SHB options
    /// <summary>Hardware description (SHB).</summary>
    internal const ushort OptShbHardware = 2;

    /// <summary>Operating system description (SHB).</summary>
    internal const ushort OptShbOs = 3;

    /// <summary>User application name (SHB).</summary>
    internal const ushort OptShbUserAppl = 4;

    // IDB options
    /// <summary>Interface name.</summary>
    internal const ushort OptIfName = 2;

    /// <summary>Interface description.</summary>
    internal const ushort OptIfDescription = 3;

    /// <summary>IPv4 address.</summary>
    internal const ushort OptIfIpv4Addr = 4;

    /// <summary>IPv6 address.</summary>
    internal const ushort OptIfIpv6Addr = 5;

    /// <summary>MAC address.</summary>
    internal const ushort OptIfMacAddr = 6;

    /// <summary>EUI address.</summary>
    internal const ushort OptIfEuiAddr = 7;

    /// <summary>Interface speed (bits per second).</summary>
    internal const ushort OptIfSpeed = 8;

    /// <summary>Timestamp resolution (power of 10 or 2).</summary>
    internal const ushort OptIfTsResol = 9;

    /// <summary>Timezone.</summary>
    internal const ushort OptIfTzone = 10;

    /// <summary>Capture filter expression.</summary>
    internal const ushort OptIfFilter = 11;

    /// <summary>Operating system on which the interface runs.</summary>
    internal const ushort OptIfOs = 12;

    /// <summary>FCS length in bytes.</summary>
    internal const ushort OptIfFcsLen = 13;

    /// <summary>Timestamp offset applied to all packet timestamps.</summary>
    internal const ushort OptIfTsOffset = 14;

    // EPB options
    /// <summary>Packet flags (EPB).</summary>
    internal const ushort OptEpbFlags = 2;

    /// <summary>Packet hash (EPB).</summary>
    internal const ushort OptEpbHash = 3;

    /// <summary>Drop count (EPB).</summary>
    internal const ushort OptEpbDropCount = 4;

    /// <summary>Packet ID (EPB).</summary>
    internal const ushort OptEpbPacketId = 5;

    /// <summary>Queue index (EPB).</summary>
    internal const ushort OptEpbQueue = 6;

    /// <summary>Verdict (EPB).</summary>
    internal const ushort OptEpbVerdict = 7;

    #endregion

    #region Default values

    /// <summary>Default snap length (262144 bytes).</summary>
    internal const uint DefaultSnapLength = 262_144;

    /// <summary>Timestamp resolution for microseconds (10^6 divisions per second).</summary>
    internal const ulong TsResolMicroseconds = 1_000_000;

    /// <summary>Timestamp resolution for nanoseconds (10^9 divisions per second).</summary>
    internal const ulong TsResolNanoseconds = 1_000_000_000;

    /// <summary>Minimum PCAPNG block size: type (4) + length (4) + trailing length (4).</summary>
    internal const uint MinBlockSize = 12;

    /// <summary>SHB fixed size before options: type(4) + len(4) + magic(4) + ver(4) + section_len(8) + trailing_len(4) = 28 bytes.</summary>
    internal const int ShbFixedSize = 28;

    /// <summary>IDB fixed size before options: type(4) + len(4) + linktype(2) + reserved(2) + snaplen(4) + trailing_len(4) = 20 bytes.</summary>
    internal const int IdbFixedSize = 20;

    /// <summary>EPB fixed size before data: type(4) + len(4) + iface(4) + ts_hi(4) + ts_lo(4)
    /// + cap_len(4) + orig_len(4) + trailing_len(4) = 32 bytes.</summary>
    internal const int EpbFixedSize = 32;

    /// <summary>SPB fixed size before data: type(4) + len(4) + orig_len(4) + trailing_len(4) = 16 bytes.</summary>
    internal const int SpbFixedSize = 16;

    /// <summary>PB fixed size before data: type(4) + len(4) + iface(2) + drops(2) + ts_hi(4) + ts_lo(4)
    /// + cap_len(4) + orig_len(4) + trailing_len(4) = 32 bytes.</summary>
    internal const int PbFixedSize = 32;

    /// <summary>Legacy PCAP global header size: 24 bytes.</summary>
    internal const int PcapGlobalHeaderSize = 24;

    /// <summary>Legacy PCAP packet header size: 16 bytes.</summary>
    internal const int PcapPacketHeaderSize = 16;

    /// <summary>PCAPNG version major.</summary>
    internal const ushort PcapngVersionMajor = 1;

    /// <summary>PCAPNG version minor.</summary>
    internal const ushort PcapngVersionMinor = 0;

    #endregion

    #region Helper methods

    /// <summary>Returns whether the block type is a known PCAPNG block type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsKnownBlockType(uint blockType) =>
        blockType is BlockTypeSHB or BlockTypeIDB or BlockTypePB or BlockTypeSPB
            or BlockTypeNRB or BlockTypeISB or BlockTypeEPB or BlockTypeITB
            or BlockTypeArinc429 or BlockTypeDSB or BlockTypeCBCopy or BlockTypeCBNoCopy;

    /// <summary>Returns whether the block type is a packet block (EPB, SPB, or PB).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsPacketBlock(uint blockType) =>
        blockType is BlockTypeEPB or BlockTypeSPB or BlockTypePB;

    /// <summary>Returns a human-readable name for a block type.</summary>
    internal static string BlockTypeName(uint blockType) => blockType switch
    {
        BlockTypeSHB => "Section Header Block",
        BlockTypeIDB => "Interface Description Block",
        BlockTypePB => "Packet Block (obsolete)",
        BlockTypeSPB => "Simple Packet Block",
        BlockTypeNRB => "Name Resolution Block",
        BlockTypeISB => "Interface Statistics Block",
        BlockTypeEPB => "Enhanced Packet Block",
        BlockTypeITB => "IRIG Timestamp Block",
        BlockTypeArinc429 => "ARINC 429 Block",
        BlockTypeDSB => "Decryption Secrets Block",
        BlockTypeCBCopy => "Custom Block (copyable)",
        BlockTypeCBNoCopy => "Custom Block (non-copyable)",
        _ => $"Unknown Block (0x{blockType:X8})",
    };
    #endregion
}
