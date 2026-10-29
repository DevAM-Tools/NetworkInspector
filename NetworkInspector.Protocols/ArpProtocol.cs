// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Address Resolution Protocol (RFC 826) parser, including the ATM variant of RFC 2225.
/// Supports variable hardware-address size and protocol-address size as specified
/// in the ARP fixed header, not only the standard Ethernet/IPv4 case.
/// <para>Field tree structure (Ethernet/IPv4 case):</para>
/// <code>
/// arp: Address Resolution Protocol (request)
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
/// ├── arp.isprobe: true/false
/// └── arp.isannouncement: true/false
/// </code>
/// <para>ATM hardware types (16, 19, 21) have a different 12-byte header and show
/// <c>arp.src.htype/hlen/stype/slen/pln</c> and the <c>arp.dst.*</c> counterparts instead of the two size fields, plus
/// the ATM numbers and subaddresses.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The instance holds no mutable state after <c>RegisterFields</c> (the field and
/// group IDs are assigned once during the single-threaded stack build). <see cref="Parse"/> only reads them and
/// writes into the per-packet <see cref="MutField"/>, so it may run concurrently on several threads, each with its own
/// packet.</para>
/// <para><b>Why ARP stays eager although it has many fields:</b> a packet carries 12 to 14 of the 33 registered ones. ARP is a
/// leaf that never dispatches a payload, its total size is bounded by the header (at most 1028 bytes), and the packet info
/// text reads most of the fields anyway. A lazy populator would need the same bytes again for the info text and save nothing.</para>
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

    /// <summary>Size of the fixed ATMARP header (RFC 2225 section 5): types, four length bytes, opcode and the protocol lengths.</summary>
    private const int _AtmHeaderSize = 12;

    // The two sizes below select the typed field path (MAC / IPv4 value, formatted display text). Any other HLEN/PLEN
    // is still parsed correctly but exposed as raw bytes, because RFC 826 allows ARP on other link and network layers.

    /// <summary>Hardware address size for Ethernet (6 bytes = MAC address).</summary>
    private const byte _EthernetHwSize = 6;

    /// <summary>Protocol address size for IPv4 (4 bytes).</summary>
    private const byte _IPv4ProtoSize = 4;

    /// <summary>Hardware type of Ethernet (10 Mb and faster), IANA ARP hardware type 1.</summary>
    private const ushort _HwTypeEthernet = 1;

    /// <summary>Hardware type of IEEE 802 networks, IANA ARP hardware type 6; carries the same 6-byte MAC addresses.</summary>
    private const ushort _HwTypeIeee802 = 6;

    /// <summary>Protocol type (EtherType) of IPv4.</summary>
    private const ushort _ProtoTypeIPv4 = 0x0800;

    /// <summary>Index group for all ARP fields (always present).</summary>
    private const string _ArpIndexGroup = "arp";

    /// <summary>ARP opcode for Request.</summary>
    private const ushort _OpcodeRequest = 1;

    /// <summary>ARP opcode for Reply.</summary>
    private const ushort _OpcodeReply = 2;

    /// <summary>ARP opcode for RARP Request (RFC 903).</summary>
    private const ushort _OpcodeRarpRequest = 3;

    /// <summary>ARP opcode for RARP Reply.</summary>
    private const ushort _OpcodeRarpReply = 4;

    /// <summary>ARP opcode for DRARP Request.</summary>
    private const ushort _OpcodeDrarpRequest = 5;

    /// <summary>ARP opcode for DRARP Reply.</summary>
    private const ushort _OpcodeDrarpReply = 6;

    /// <summary>ARP opcode for DRARP Error.</summary>
    private const ushort _OpcodeDrarpError = 7;

    /// <summary>ARP opcode for InARP Request (RFC 2390).</summary>
    private const ushort _OpcodeInArpRequest = 8;

    /// <summary>ARP opcode for InARP Reply.</summary>
    private const ushort _OpcodeInArpReply = 9;

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

    // Gratuitous ARP: a request or reply whose sender and target protocol addresses are equal (RFC 5227 section 3:
    // address announcement or relocation, and the conflict check of a host that learns it).
    [BoolField("arp.isgratuitous", "Is gratuitous", IndexGroup = _ArpIndexGroup)]
    private FieldId _IsGratuitousFieldId;

    // ARP probe: a request that is not gratuitous and whose sender protocol address is unspecified (RFC 5227 section 2.1.1).
    [BoolField("arp.isprobe", "Is probe", IndexGroup = _ArpIndexGroup)]
    private FieldId _IsProbeFieldId;

    // ARP announcement: a gratuitous request whose target hardware address is all zero (RFC 5227 section 2.3).
    [BoolField("arp.isannouncement", "Is announcement", IndexGroup = _ArpIndexGroup)]
    private FieldId _IsAnnouncementFieldId;

    #endregion

    #region ATMARP fields (RFC 2225)

    // The type/length bytes of the ATM numbers carry a flag (E.164 or NSAP) and a 6-bit length; the subaddress bytes
    // carry the same. The fields keep the Ethernet path's names for the protocol addresses (arp.src.proto_ipv4 ...) so a
    // filter on the IP address works for every hardware type.
    [U64Field("arp.src.htype", "Sender ATM number type", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcHtypeFieldId;

    [U64Field("arp.src.hlen", "Sender ATM number length", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcHlenFieldId;

    [U64Field("arp.src.stype", "Sender ATM subaddress type", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcStypeFieldId;

    [U64Field("arp.src.slen", "Sender ATM subaddress length", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcSlenFieldId;

    [U64Field("arp.src.pln", "Sender protocol address length", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcPlnFieldId;

    [U64Field("arp.dst.htype", "Target ATM number type", IndexGroup = "arp.atm")]
    private FieldId _AtmDstHtypeFieldId;

    [U64Field("arp.dst.hlen", "Target ATM number length", IndexGroup = "arp.atm")]
    private FieldId _AtmDstHlenFieldId;

    [U64Field("arp.dst.stype", "Target ATM subaddress type", IndexGroup = "arp.atm")]
    private FieldId _AtmDstStypeFieldId;

    [U64Field("arp.dst.slen", "Target ATM subaddress length", IndexGroup = "arp.atm")]
    private FieldId _AtmDstSlenFieldId;

    [U64Field("arp.dst.pln", "Target protocol address length", IndexGroup = "arp.atm")]
    private FieldId _AtmDstPlnFieldId;

    [StringField("arp.src.atm_num_e164", "Sender ATM number (E.164)", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcNumE164FieldId;

    [BytesField("arp.src.atm_num_nsap", "Sender ATM number (NSAP)", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcNumNsapFieldId;

    [BytesField("arp.src.atm_subaddr", "Sender ATM subaddress", IndexGroup = "arp.atm")]
    private FieldId _AtmSrcSubaddrFieldId;

    [StringField("arp.dst.atm_num_e164", "Target ATM number (E.164)", IndexGroup = "arp.atm")]
    private FieldId _AtmDstNumE164FieldId;

    [BytesField("arp.dst.atm_num_nsap", "Target ATM number (NSAP)", IndexGroup = "arp.atm")]
    private FieldId _AtmDstNumNsapFieldId;

    [BytesField("arp.dst.atm_subaddr", "Target ATM subaddress", IndexGroup = "arp.atm")]
    private FieldId _AtmDstSubaddrFieldId;

    #endregion

    #region Parse

    /// <summary>
    /// Parses an ARP packet, supporting variable hardware-address and protocol-address sizes
    /// as declared in the HLEN/PLEN fields (RFC 826), and the ATMARP layout for the ATM hardware types.
    /// When HLEN = 6 the standard MAC fields are emitted; otherwise raw bytes fields are used.
    /// When PLEN = 4 the standard IPv4 fields are emitted; otherwise raw bytes fields are used.
    /// ARP is a leaf protocol — no lazy population, direct Append().
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">
    /// Parse context; ARP never dispatches a payload and only uses it to record protocol and index-group presence
    /// (a no-op when no packet index is attached).
    /// </param>
    /// <returns>
    /// The size of the ARP message (<c>8 + 2 * HLEN + 2 * PLEN</c>, or the ATMARP size) so that the caller can classify
    /// any following bytes (for example Ethernet minimum-frame padding) as trailing data, or a <see cref="ParseError"/>
    /// describing the failure when the data is shorter than the fixed header or than the size the header declares.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // Read the first four bytes to learn the hardware type. The ATM types use a different header, so the layout
        // is selected before HLEN and PLEN are read: byte 4 and 5 are type/length bytes there, not sizes.
        ushort hwType = BinaryPrimitives.ReadUInt16BigEndian(span);
        if (hwType is 16 or 19 or 21)
        {
            return _ParseAtmArp(in parentField, data, in context);
        }

        ushort protoType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        byte hwSize = span[4];
        byte protoSize = span[5];
        ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);

        // Compute and validate the total message size based on the declared address lengths. HLEN and PLEN come from
        // the wire (untrusted), so the whole length is checked once here; every Slice below is then in range and
        // cannot throw. Both sizes are single bytes (at most 255), so the total is at most 1028 and cannot overflow.
        // ARP variable-length layout: 8 + hwSize + protoSize + hwSize + protoSize
        int totalSize = _MinHeaderSize + 2 * hwSize + 2 * protoSize;
        if (data.Length < totalSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)totalSize, (ulong)data.Length);
        }

        // Locate sender and target addresses; the wire order is sender hardware, sender protocol, target hardware,
        // target protocol, and the offsets move with the declared sizes.
        int senderHwOffset = _MinHeaderSize;
        int senderProtoOffset = senderHwOffset + hwSize;
        int targetHwOffset = senderProtoOffset + protoSize;
        int targetProtoOffset = targetHwOffset + hwSize;

        ReadOnlySpan<byte> senderHwSpan = span.Slice(senderHwOffset, hwSize);
        ReadOnlySpan<byte> senderProtoSpan = span.Slice(senderProtoOffset, protoSize);
        ReadOnlySpan<byte> targetHwSpan = span.Slice(targetHwOffset, hwSize);
        ReadOnlySpan<byte> targetProtoSpan = span.Slice(targetProtoOffset, protoSize);

        // Build typed address values (MAC/IPv4 for standard sizes). For other sizes they stay default and are never
        // read: the raw-bytes fields below are used instead. The typed field is chosen by the declared size alone, so
        // an address of the right size is always shown as a MAC or IPv4 value; whether the hardware and protocol TYPE
        // also match is a separate, stricter test (isStandardArp) that only the flags and the info text use.
        // IPv4Address is built from a big-endian read because ARP carries addresses in network byte order.
        MacAddress senderMac = hwSize == _EthernetHwSize ? MacAddress.FromBytes(senderHwSpan) : default;
        MacAddress targetMac = hwSize == _EthernetHwSize ? MacAddress.FromBytes(targetHwSpan) : default;
        IPv4Address senderIp = protoSize == _IPv4ProtoSize
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(senderProtoSpan)) : default;
        IPv4Address targetIp = protoSize == _IPv4ProtoSize
            ? new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(targetProtoSpan)) : default;

        // A packet is "standard" when its addresses really are Ethernet MACs and IPv4 addresses: the hardware type
        // must be Ethernet or IEEE 802 and the protocol type IPv4, not just the sizes. A 6-byte hardware address of
        // another link type is not a MAC, and a 4-byte protocol address of another protocol is not an IPv4 address,
        // so the info text must not name it as one.
        bool isStandardArp = hwType is _HwTypeEthernet or _HwTypeIeee802
            && hwSize == _EthernetHwSize
            && protoType == _ProtoTypeIPv4
            && protoSize == _IPv4ProtoSize;

        // Derived flags (RFC 5227). Computed from the raw address bytes so they also hold for other address sizes.
        // An empty protocol address (PLEN 0) never counts: two empty addresses are trivially equal, which would flag
        // every such packet as gratuitous.
        //  - gratuitous: request or reply with sender protocol address == target protocol address.
        //  - announcement: a gratuitous request whose target hardware address is all zero.
        //  - probe: a request that is not gratuitous and whose sender protocol address is all zero.
        // Other opcodes (RARP, InARP, ...) never set a flag.
        bool isRequestOrReply = opcode is _OpcodeRequest or _OpcodeReply;
        bool hasProtoAddress = protoSize > 0;
        bool isGratuitous = isRequestOrReply && hasProtoAddress && senderProtoSpan.SequenceEqual(targetProtoSpan);
        bool isAnnouncement = isGratuitous
            && opcode == _OpcodeRequest
            && !targetHwSpan[..Math.Min((int)hwSize, 6)].ContainsAnyExcept((byte)0);
        bool isProbe = !isGratuitous
            && hasProtoAddress
            && opcode == _OpcodeRequest
            && !senderProtoSpan.ContainsAnyExcept((byte)0);

        _SetPacketInfo(
            in parentField, opcode, isStandardArp, isGratuitous, isAnnouncement, isProbe,
            senderIp, targetIp, senderMac, targetMac,
            data.Slice(senderHwOffset, hwSize), data.Slice(senderProtoOffset, protoSize), data.Slice(targetProtoOffset, protoSize));

        // Record presence in the cross-packet index (no-op when no index is attached). Done after validation so a
        // truncated message is not indexed as an ARP packet.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_ArpGroupId);

        // ARP is a leaf protocol with few fields — no lazy populator needed. The container value covers exactly the
        // ARP message (not trailing Ethernet padding); its representation shows the byte count instead of a hex dump.
        // The count comes from a shared table (totalSize is at most 1028), so no string is built per packet.
        FieldValue containerValue = FieldValue.NewBytes(data[..totalSize])
            .WithCustomRepresentation(new LazyString(DisplayTables.GetByteCountDisplayText(totalSize)));
        MutField container;
        if (isStandardArp && opcode == _OpcodeRequest)
        {
            container = parentField.AppendWithCustomText(
                _ProtocolFieldId, containerValue,
                isGratuitous
                    ? "Address Resolution Protocol (request/gratuitous ARP), Who has "
                    : "Address Resolution Protocol (request), Who has ",
                targetIp, "? Tell ", senderIp);
        }
        else if (isStandardArp && opcode == _OpcodeReply)
        {
            container = parentField.AppendWithCustomText(
                _ProtocolFieldId, containerValue,
                isGratuitous
                    ? "Address Resolution Protocol (reply/gratuitous ARP), "
                    : "Address Resolution Protocol (reply), ",
                senderIp, " is at ", senderMac);
        }
        else
        {
            container = parentField.AppendWithCustomText(
                _ProtocolFieldId, containerValue,
                "Address Resolution Protocol (opcode ", DisplayTables.GetArpOpcodeDisplayText(opcode), ")");
        }

        // Fixed header fields (always present), appended in wire order. The texts come from shared tables and are
        // wrapped in LazyString: a bare string would bind to the generic overload and box a copy per packet.
        container.AppendWithCustomText(_HwTypeFieldId, FieldValue.NewU64(hwType),
            new LazyString(DisplayTables.GetArpHwTypeDisplayText(hwType)));
        container.AppendWithCustomText(_ProtoTypeFieldId, FieldValue.NewU64(protoType),
            new LazyString(DisplayTables.GetEtherTypeDisplayText(protoType)));
        container.Append(_HwSizeFieldId, FieldValue.NewU64(hwSize));
        container.Append(_ProtoSizeFieldId, FieldValue.NewU64(protoSize));
        container.AppendWithCustomText(_OpcodeFieldId, FieldValue.NewU64(opcode),
            new LazyString(DisplayTables.GetArpOpcodeDisplayText(opcode)));

        // Address fields in wire order. A typed field (MAC / IPv4) is emitted for the standard sizes, otherwise
        // the raw-bytes field; exactly one of the two is present per address.
        _AppendAddresses(in container, data, hwSize, protoSize, senderHwOffset, senderProtoOffset, targetHwOffset, targetProtoOffset,
            senderMac, targetMac, senderIp, targetIp);

        // Derived flags, always emitted (false when they do not apply) so every ARP packet carries all three fields.
        container.Append(_IsGratuitousFieldId, FieldValue.NewBool(isGratuitous));
        container.Append(_IsProbeFieldId, FieldValue.NewBool(isProbe));
        container.Append(_IsAnnouncementFieldId, FieldValue.NewBool(isAnnouncement));

        return totalSize;
    }

    /// <summary>
    /// Appends the four address fields in wire order. Split from <see cref="Parse"/> to keep the method readable: it
    /// holds no logic beyond choosing between the typed and the raw field per address.
    /// </summary>
    private void _AppendAddresses(
        in MutField container, ReadOnlyMemory<byte> data, byte hwSize, byte protoSize,
        int senderHwOffset, int senderProtoOffset, int targetHwOffset, int targetProtoOffset,
        MacAddress senderMac, MacAddress targetMac, IPv4Address senderIp, IPv4Address targetIp)
    {
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
    }

    #endregion

    #region Packet info

    /// <summary>State of the info text of a standard Ethernet/IPv4 packet; small value types only, boxed once per packet.</summary>
    private readonly record struct StandardInfoState(
        ushort Opcode, bool IsGratuitous, bool IsAnnouncement, bool IsProbe,
        IPv4Address SenderIp, IPv4Address TargetIp, MacAddress SenderMac, MacAddress TargetMac);

    /// <summary>State of the info text of a packet with other address types; the addresses are shown as hexadecimal bytes.</summary>
    private readonly record struct RawInfoState(
        ushort Opcode, ReadOnlyMemory<byte> SenderHw, ReadOnlyMemory<byte> SenderProto, ReadOnlyMemory<byte> TargetProto);

    /// <summary>
    /// Sets the packet info (summary column) text from the opcode and the derived flags. The text is formatted lazily,
    /// when the summary is read, from a small value state; the lambdas are static, so they capture nothing.
    /// </summary>
    /// <remarks>
    /// A standard packet names its addresses in the usual ARP wording ("Who has X? Tell Y", "X is at M"), and a
    /// gratuitous, announcement or probe packet says so, because those are the forms address-conflict analysis
    /// looks for. A packet of another address type still shows its opcode and the addresses as hexadecimal bytes,
    /// so the summary is never empty for a valid message.
    /// </remarks>
    private static void _SetPacketInfo(
        in MutField parentField, ushort opcode,
        bool isStandardArp, bool isGratuitous, bool isAnnouncement, bool isProbe,
        IPv4Address senderIp, IPv4Address targetIp, MacAddress senderMac, MacAddress targetMac,
        ReadOnlyMemory<byte> senderHw, ReadOnlyMemory<byte> senderProto, ReadOnlyMemory<byte> targetProto)
    {
        if (isStandardArp)
        {
            StandardInfoState state = new(opcode, isGratuitous, isAnnouncement, isProbe, senderIp, targetIp, senderMac, targetMac);
            parentField.SetPacketInfo(LazyString.FormatLazy(state, static s => _FormatStandardInfo(s)));
            return;
        }

        // Request and reply of another address type: the opcode wording with the raw addresses. Other opcodes show
        // their name only, which is a shared table string and costs no box.
        if (opcode is _OpcodeRequest or _OpcodeReply)
        {
            RawInfoState raw = new(opcode, senderHw, senderProto, targetProto);
            parentField.SetPacketInfo(LazyString.FormatLazy(raw, static s => _FormatRawInfo(s)));
            return;
        }

        parentField.SetPacketInfo(new LazyString(DisplayTables.GetArpOpcodeDisplayText(opcode)));
    }

    /// <summary>Formats the info text of a standard packet; runs when the text is read.</summary>
    private static string _FormatStandardInfo(StandardInfoState s) => s.Opcode switch
    {
        _OpcodeRequest when s.IsAnnouncement => string.Create(CultureInfo.InvariantCulture, $"ARP Announcement for {s.TargetIp}"),
        _OpcodeRequest when s.IsGratuitous => string.Create(CultureInfo.InvariantCulture, $"Gratuitous ARP for {s.TargetIp} (Request)"),
        _OpcodeRequest when s.IsProbe => string.Create(CultureInfo.InvariantCulture, $"Who has {s.TargetIp}? (ARP Probe)"),
        _OpcodeRequest => string.Create(CultureInfo.InvariantCulture, $"Who has {s.TargetIp}? Tell {s.SenderIp}"),
        _OpcodeReply when s.IsGratuitous => string.Create(CultureInfo.InvariantCulture, $"Gratuitous ARP for {s.SenderIp} (Reply)"),
        _OpcodeReply => string.Create(CultureInfo.InvariantCulture, $"{s.SenderIp} is at {s.SenderMac}"),
        _OpcodeRarpRequest or _OpcodeDrarpRequest or _OpcodeInArpRequest
            => string.Create(CultureInfo.InvariantCulture, $"Who is {s.TargetMac}? Tell {s.SenderMac}"),
        _OpcodeRarpReply or _OpcodeDrarpReply
            => string.Create(CultureInfo.InvariantCulture, $"{s.TargetMac} is at {s.TargetIp}"),
        _OpcodeInArpReply => string.Create(CultureInfo.InvariantCulture, $"{s.SenderMac} is at {s.SenderIp}"),
        _ => DisplayTables.GetArpOpcodeInfoText(s.Opcode),
    };

    /// <summary>Formats the info text of a request or reply with other address types; the addresses are hexadecimal bytes.</summary>
    private static string _FormatRawInfo(RawInfoState s)
    {
        string sender = Convert.ToHexStringLower(s.SenderProto.Span);
        string target = Convert.ToHexStringLower(s.TargetProto.Span);
        return s.Opcode == _OpcodeRequest
            ? string.Create(CultureInfo.InvariantCulture, $"Who has {target}? Tell {sender}")
            : string.Create(CultureInfo.InvariantCulture, $"{sender} is at {Convert.ToHexStringLower(s.SenderHw.Span)}");
    }

    #endregion

    #region ATMARP

    /// <summary>
    /// Parses the ATMARP layout (RFC 2225 section 5) used by the ATM hardware types 16, 19 and 21: a 12-byte header
    /// (hardware type, protocol type, sender ATM number and subaddress type/length bytes, opcode, sender protocol
    /// length, target ATM number and subaddress type/length bytes, target protocol length) followed by the six address
    /// fields.
    /// </summary>
    /// <remarks>
    /// Every length is a field of the untrusted header: the ATM number and subaddress lengths are 6-bit values and the
    /// protocol lengths one byte, so the total is at most 12 + 4 x 63 + 2 x 255 = 774 bytes and is validated once
    /// before any slice is taken. The E.164 flag (bit 6 of a type/length byte) selects the ASCII form of the number;
    /// without it the number is an NSAP address shown as raw bytes.
    /// </remarks>
    private ParseResult _ParseAtmArp(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _AtmHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _AtmHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;
        ushort hwType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort protoType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        byte srcNumInfo = span[4];
        byte srcSubInfo = span[5];
        ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        byte srcProtoLen = span[8];
        byte dstNumInfo = span[9];
        byte dstSubInfo = span[10];
        byte dstProtoLen = span[11];

        // The low six bits of a type/length byte are the length; bit 6 marks E.164 (otherwise NSAP).
        int srcNumLen = srcNumInfo & 0x3F;
        int srcSubLen = srcSubInfo & 0x3F;
        int dstNumLen = dstNumInfo & 0x3F;
        int dstSubLen = dstSubInfo & 0x3F;
        int totalSize = _AtmHeaderSize + srcNumLen + srcSubLen + srcProtoLen + dstNumLen + dstSubLen + dstProtoLen;
        if (data.Length < totalSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)totalSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_ArpGroupId);
        context.RecordGroupPresence(_ArpAtmGroupId);

        // Shared text for the summary: the opcode names are the ones of the Ethernet form; ATM requests and replies
        // are told apart from them so a reader knows which layout was used.
        parentField.SetPacketInfo(new LazyString(opcode switch
        {
            _OpcodeRequest => "ATMARP request",
            _OpcodeReply => "ATMARP reply",
            _OpcodeInArpRequest => "InATMARP request",
            _OpcodeInArpReply => "InATMARP reply",
            _ => DisplayTables.GetArpOpcodeDisplayText(opcode),
        }));

        FieldValue containerValue = FieldValue.NewBytes(data[..totalSize])
            .WithCustomRepresentation(new LazyString(DisplayTables.GetByteCountDisplayText(totalSize)));
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, containerValue,
            "Address Resolution Protocol (opcode ", DisplayTables.GetArpOpcodeDisplayText(opcode), ")");

        container.AppendWithCustomText(_HwTypeFieldId, FieldValue.NewU64(hwType), new LazyString(DisplayTables.GetArpHwTypeDisplayText(hwType)));
        container.AppendWithCustomText(_ProtoTypeFieldId, FieldValue.NewU64(protoType), new LazyString(DisplayTables.GetEtherTypeDisplayText(protoType)));
        container.AppendWithCustomText(_OpcodeFieldId, FieldValue.NewU64(opcode), new LazyString(DisplayTables.GetArpOpcodeDisplayText(opcode)));

        // Length and type fields of the header, in wire order.
        container.AppendWithCustomText(_AtmSrcHtypeFieldId, FieldValue.NewU64((ulong)((srcNumInfo >> 6) & 1)), new LazyString(_AtmTypeText(srcNumInfo)));
        container.Append(_AtmSrcHlenFieldId, FieldValue.NewU64((ulong)srcNumLen));
        container.AppendWithCustomText(_AtmSrcStypeFieldId, FieldValue.NewU64((ulong)((srcSubInfo >> 6) & 1)), new LazyString(_AtmTypeText(srcSubInfo)));
        container.Append(_AtmSrcSlenFieldId, FieldValue.NewU64((ulong)srcSubLen));
        container.Append(_AtmSrcPlnFieldId, FieldValue.NewU64(srcProtoLen));
        container.AppendWithCustomText(_AtmDstHtypeFieldId, FieldValue.NewU64((ulong)((dstNumInfo >> 6) & 1)), new LazyString(_AtmTypeText(dstNumInfo)));
        container.Append(_AtmDstHlenFieldId, FieldValue.NewU64((ulong)dstNumLen));
        container.AppendWithCustomText(_AtmDstStypeFieldId, FieldValue.NewU64((ulong)((dstSubInfo >> 6) & 1)), new LazyString(_AtmTypeText(dstSubInfo)));
        container.Append(_AtmDstSlenFieldId, FieldValue.NewU64((ulong)dstSubLen));
        container.Append(_AtmDstPlnFieldId, FieldValue.NewU64(dstProtoLen));

        // Address fields in wire order: sender number, sender subaddress, sender protocol, then the target's.
        int offset = _AtmHeaderSize;
        _AppendAtmNumber(in container, data, ref offset, srcNumLen, (srcNumInfo & 0x40) != 0, _AtmSrcNumE164FieldId, _AtmSrcNumNsapFieldId);
        _AppendAtmBytes(in container, data, ref offset, srcSubLen, _AtmSrcSubaddrFieldId);
        _AppendAtmProtocolAddress(in container, data, ref offset, srcProtoLen, _SrcProtoIpv4FieldId, _SrcProtoRawFieldId);
        _AppendAtmNumber(in container, data, ref offset, dstNumLen, (dstNumInfo & 0x40) != 0, _AtmDstNumE164FieldId, _AtmDstNumNsapFieldId);
        _AppendAtmBytes(in container, data, ref offset, dstSubLen, _AtmDstSubaddrFieldId);
        _AppendAtmProtocolAddress(in container, data, ref offset, dstProtoLen, _DstProtoIpv4FieldId, _DstProtoRawFieldId);

        return totalSize;
    }

    /// <summary>Text of the address-type bit of an ATM type/length byte.</summary>
    private static string _AtmTypeText(byte info) => (info & 0x40) != 0
        ? "E.164 (1)"
        : "NSAP (0)";

    /// <summary>Appends an ATM number as ASCII text (E.164) or as raw NSAP bytes and advances the offset.</summary>
    private static void _AppendAtmNumber(
        in MutField container, ReadOnlyMemory<byte> data, ref int offset, int length, bool isE164, FieldId e164FieldId, FieldId nsapFieldId)
    {
        if (length == 0)
        {
            return;
        }

        if (isE164)
        {
            // E.164 numbers are ASCII digits (RFC 2225 section 5.1). Latin-1 widening keeps a hostile byte from throwing.
            container.Append(e164FieldId, FieldValue.NewString(Encoding.Latin1.GetString(data.Span.Slice(offset, length))));
        }
        else
        {
            container.Append(nsapFieldId, FieldValue.NewBytes(data.Slice(offset, length)));
        }

        offset += length;
    }

    /// <summary>Appends an address as raw bytes and advances the offset; a zero length emits nothing.</summary>
    private static void _AppendAtmBytes(in MutField container, ReadOnlyMemory<byte> data, ref int offset, int length, FieldId fieldId)
    {
        if (length == 0)
        {
            return;
        }

        container.Append(fieldId, FieldValue.NewBytes(data.Slice(offset, length)));
        offset += length;
    }

    /// <summary>Appends the protocol address of an ATMARP message: an IPv4 value for 4 bytes, raw bytes otherwise.</summary>
    private static void _AppendAtmProtocolAddress(
        in MutField container, ReadOnlyMemory<byte> data, ref int offset, int length, FieldId ipv4FieldId, FieldId rawFieldId)
    {
        if (length == _IPv4ProtoSize)
        {
            container.Append(ipv4FieldId, FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(offset, 4)))));
        }
        else
        {
            container.Append(rawFieldId, FieldValue.NewBytes(data.Slice(offset, length)));
        }

        offset += length;
    }

    #endregion
}
