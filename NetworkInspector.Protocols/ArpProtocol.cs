// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Address Resolution Protocol (RFC 826) parser.
/// Supports variable hardware-address size and protocol-address size as specified
/// in the ARP fixed header, not only the standard Ethernet/IPv4 case.
/// <para>Field tree structure (Ethernet/IPv4 case):</para>
/// <code>
/// arp: Address Resolution Protocol (request/reply)
/// ├── arp.hw.type: 1 (Ethernet)
/// ├── arp.proto.type: 0x0800 (IPv4)
/// ├── arp.hw.size: 6
/// ├── arp.proto.size: 4
/// ├── arp.opcode: 1 (request) / 2 (reply)
/// ├── arp.src.hw_mac: AA:BB:CC:DD:EE:FF  (or arp.src.hw_raw for non-6-byte HW addresses)
/// ├── arp.src.proto_ipv4: 192.168.1.1    (or arp.src.proto_raw for non-4-byte proto addresses)
/// ├── arp.dst.hw_mac: 00:00:00:00:00:00
/// ├── arp.dst.proto_ipv4: 192.168.1.2
/// ├── arp.isgratuitous: true/false
/// └── arp.isprobe: true/false
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("arp", "Address Resolution Protocol", Description = "ARP (RFC 826)")]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey)]
public sealed partial class ArpProtocol : IProtocol
{
    #region Constants

    /// <summary>EtherType value for ARP (0x0806).</summary>
    public const ulong EtherTypeKey = 0x0806;

    /// <summary>
    /// Minimum ARP header size in bytes: fixed portion before the variable-length
    /// hardware and protocol addresses (HTYPE+PTYPE+HLEN+PLEN+OPER = 8 bytes).
    /// Total size = 8 + 2*hwSize + 2*protoSize.
    /// </summary>
    private const int _MinHeaderSize = 8;

    /// <summary>Hardware address size for Ethernet (6 bytes = MAC address).</summary>
    private const byte _EthernetHwSize = 6;

    /// <summary>Protocol address size for IPv4 (4 bytes).</summary>
    private const byte _IPv4ProtoSize = 4;

    /// <summary>Index group for all ARP fields (always present).</summary>
    private const string _ArpIndexGroup = "arp";

    /// <summary>ARP opcode for Request.</summary>
    private const ushort _OpcodeRequest = 1;

    /// <summary>ARP opcode for Reply.</summary>
    private const ushort _OpcodeReply = 2;

    #endregion

    #region Fields

    [BytesField("arp", "ARP", IndexGroup = _ArpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("arp.hw.type", "Hardware type", IndexGroup = _ArpIndexGroup)]
    private FieldId _HwTypeFieldId;

    [U64Field("arp.proto.type", "Protocol type", IndexGroup = _ArpIndexGroup)]
    private FieldId _ProtoTypeFieldId;

    [U64Field("arp.hw.size", "Hardware size", IndexGroup = _ArpIndexGroup)]
    private FieldId _HwSizeFieldId;

    [U64Field("arp.proto.size", "Protocol size", IndexGroup = _ArpIndexGroup)]
    private FieldId _ProtoSizeFieldId;

    [U64Field("arp.opcode", "Opcode", IndexGroup = _ArpIndexGroup)]
    private FieldId _OpcodeFieldId;

    [MacField("arp.src.hw_mac", "Sender MAC address", IndexGroup = _ArpIndexGroup)]
    private FieldId _SrcHwMacFieldId;

    /// <summary>Raw sender hardware address when <c>arp.hw.size</c> != 6.</summary>
    [BytesField("arp.src.hw_raw", "Sender hardware address (raw)", IndexGroup = _ArpIndexGroup)]
    private FieldId _SrcHwRawFieldId;

    [IPv4Field("arp.src.proto_ipv4", "Sender IP address", IndexGroup = _ArpIndexGroup)]
    private FieldId _SrcProtoIpv4FieldId;

    /// <summary>Raw sender protocol address when <c>arp.proto.size</c> != 4.</summary>
    [BytesField("arp.src.proto_raw", "Sender protocol address (raw)", IndexGroup = _ArpIndexGroup)]
    private FieldId _SrcProtoRawFieldId;

    [MacField("arp.dst.hw_mac", "Target MAC address", IndexGroup = _ArpIndexGroup)]
    private FieldId _DstHwMacFieldId;

    /// <summary>Raw target hardware address when <c>arp.hw.size</c> != 6.</summary>
    [BytesField("arp.dst.hw_raw", "Target hardware address (raw)", IndexGroup = _ArpIndexGroup)]
    private FieldId _DstHwRawFieldId;

    [IPv4Field("arp.dst.proto_ipv4", "Target IP address", IndexGroup = _ArpIndexGroup)]
    private FieldId _DstProtoIpv4FieldId;

    /// <summary>Raw target protocol address when <c>arp.proto.size</c> != 4.</summary>
    [BytesField("arp.dst.proto_raw", "Target protocol address (raw)", IndexGroup = _ArpIndexGroup)]
    private FieldId _DstProtoRawFieldId;

    // Gratuitous ARP: sender IP == target IP (used for address announcement or duplicate detection)
    [BoolField("arp.isgratuitous", "Is gratuitous", IndexGroup = _ArpIndexGroup)]
    private FieldId _IsGratuitousFieldId;

    // ARP probe: sender IP is 0.0.0.0 (RFC 5227 — address conflict detection)
    [BoolField("arp.isprobe", "Is probe", IndexGroup = _ArpIndexGroup)]
    private FieldId _IsProbeFieldId;

    /// <summary>
    /// Parses an ARP packet, supporting variable hardware-address and protocol-address sizes
    /// as declared in the HLEN/PLEN fields (RFC 826).
    /// When HLEN = 6 the standard MAC fields are emitted; otherwise raw bytes fields are used.
    /// When PLEN = 4 the standard IPv4 fields are emitted; otherwise raw bytes fields are used.
    /// ARP is a leaf protocol — no lazy population, direct Append().
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // Read the fixed 8-byte header to learn address field sizes
        ushort hwType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort protoType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        byte hwSize = span[4];
        byte protoSize = span[5];
        ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);

        // Compute and validate the total header size based on the declared address lengths.
        // ARP variable-length layout: 8 + hwSize + protoSize + hwSize + protoSize
        int totalSize = _MinHeaderSize + 2 * hwSize + 2 * protoSize;
        if (data.Length < totalSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)totalSize, (ulong)data.Length);
        }

        // Read sender and target addresses at variable offsets
        int senderHwOffset = _MinHeaderSize;
        int senderProtoOffset = senderHwOffset + hwSize;
        int targetHwOffset = senderProtoOffset + protoSize;
        int targetProtoOffset = targetHwOffset + hwSize;

        ReadOnlySpan<byte> senderHwSpan = span.Slice(senderHwOffset, hwSize);
        ReadOnlySpan<byte> senderProtoSpan = span.Slice(senderProtoOffset, protoSize);
        ReadOnlySpan<byte> targetHwSpan = span.Slice(targetHwOffset, hwSize);
        ReadOnlySpan<byte> targetProtoSpan = span.Slice(targetProtoOffset, protoSize);

        // Build typed address values (MAC/IPv4 for standard sizes, raw bytes otherwise)
        MacAddress senderMac = hwSize == _EthernetHwSize ? MacAddress.FromBytes(senderHwSpan) : default;
        MacAddress targetMac = hwSize == _EthernetHwSize ? MacAddress.FromBytes(targetHwSpan) : default;
        IPv4Address senderIp = protoSize == _IPv4ProtoSize
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(senderProtoSpan)) : default;
        IPv4Address targetIp = protoSize == _IPv4ProtoSize
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(targetProtoSpan)) : default;

        // Build summary text based on opcode (only for standard Ethernet/IPv4 ARP)
        bool isStandardArp = hwSize == _EthernetHwSize && protoSize == _IPv4ProtoSize;
        LazyString summary = isStandardArp
            ? opcode == _OpcodeRequest
                ? ZA.Lazy("Address Resolution Protocol (request), Who has ", targetIp, "? Tell ", senderIp)
                : opcode == _OpcodeReply
                    ? ZA.Lazy("Address Resolution Protocol (reply), ", senderIp, " is at ", senderMac)
                    : ZA.Lazy("Address Resolution Protocol (opcode ", DisplayTables.GetArpOpcodeDisplayText(opcode), ")")
            : ZA.Lazy("Address Resolution Protocol (opcode ", DisplayTables.GetArpOpcodeDisplayText(opcode), ")");

        // Set packet info for the info column (standard ARP only)
        if (isStandardArp)
        {
            if (opcode == _OpcodeRequest)
            {
                parentField.SetPacketInfo(ZA.Lazy("Who has ", targetIp, "? Tell ", senderIp));
            }
            else if (opcode == _OpcodeReply)
            {
                parentField.SetPacketInfo(ZA.Lazy(senderIp, " is at ", senderMac));
            }
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_ArpGroupId);

        // ARP is a leaf protocol with few fields — no lazy populator needed.
        FieldValue containerValue = FieldValue.NewBytes(data[..totalSize])
            .WithCustomRepresentation(new LazyString(ZA.String(totalSize, " bytes")));
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, summary);

        // Fixed header fields (always present)
        container.AppendWithCustomText(_HwTypeFieldId, FieldValue.NewU64(hwType),
            DisplayTables.GetArpHwTypeDisplayText(hwType));
        container.AppendWithCustomText(_ProtoTypeFieldId, FieldValue.NewU64(protoType),
            DisplayTables.GetEtherTypeDisplayText(protoType));
        container.Append(_HwSizeFieldId, FieldValue.NewU64(hwSize));
        container.Append(_ProtoSizeFieldId, FieldValue.NewU64(protoSize));
        container.AppendWithCustomText(_OpcodeFieldId, FieldValue.NewU64(opcode),
            DisplayTables.GetArpOpcodeDisplayText(opcode));

        // Sender hardware address
        if (hwSize == _EthernetHwSize)
        {
            container.Append(_SrcHwMacFieldId, FieldValue.NewMacAddress(senderMac));
        }
        else
        {
            container.Append(_SrcHwRawFieldId, FieldValue.NewBytes(data.Slice(senderHwOffset, hwSize)));
        }

        // Sender protocol address
        if (protoSize == _IPv4ProtoSize)
        {
            container.Append(_SrcProtoIpv4FieldId, FieldValue.NewIPv4(senderIp));
        }
        else
        {
            container.Append(_SrcProtoRawFieldId, FieldValue.NewBytes(data.Slice(senderProtoOffset, protoSize)));
        }

        // Target hardware address
        if (hwSize == _EthernetHwSize)
        {
            container.Append(_DstHwMacFieldId, FieldValue.NewMacAddress(targetMac));
        }
        else
        {
            container.Append(_DstHwRawFieldId, FieldValue.NewBytes(data.Slice(targetHwOffset, hwSize)));
        }

        // Target protocol address
        if (protoSize == _IPv4ProtoSize)
        {
            container.Append(_DstProtoIpv4FieldId, FieldValue.NewIPv4(targetIp));
        }
        else
        {
            container.Append(_DstProtoRawFieldId, FieldValue.NewBytes(data.Slice(targetProtoOffset, protoSize)));
        }

        // Gratuitous ARP: sender IP equals target IP (used for address announcement)
        // Only meaningful for standard IPv4 ARP
        bool isGratuitous = isStandardArp && senderIp == targetIp;
        container.Append(_IsGratuitousFieldId, FieldValue.NewBool(isGratuitous));

        // ARP probe: sender IP is 0.0.0.0 (RFC 5227 — used for duplicate address detection)
        bool isProbe = isStandardArp && senderIp.IsZero;
        container.Append(_IsProbeFieldId, FieldValue.NewBool(isProbe));

        return totalSize;
    }
    #endregion
}
