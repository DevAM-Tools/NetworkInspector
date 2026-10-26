// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Dynamic Host Configuration Protocol for IPv4 (RFC 2131 / RFC 2132).
/// Parses the BOOTP fixed header (240 bytes incl. magic cookie) and a
/// curated subset of common DHCP options. The BOOTP <c>sname</c> (64 bytes) and <c>file</c> (128 bytes) fields and the
/// bytes of <c>chaddr</c> after the first six are not decoded; they remain visible in the container's raw bytes.
/// <para>Field tree structure:</para>
/// <code>
/// dhcp: DHCP DISCOVER, Transaction ID 0x12345678
/// ├── dhcp.type: Boot Request (1)
/// ├── dhcp.hw.type: Ethernet (1)
/// ├── dhcp.hw.len: 6
/// ├── dhcp.hops: 0
/// ├── dhcp.id: 0x12345678
/// ├── dhcp.secs: 0
/// ├── dhcp.flags: 0x0000 [None]
/// ├── dhcp.flags.bc: false
/// ├── dhcp.ip.client: 0.0.0.0
/// ├── dhcp.ip.your:   0.0.0.0
/// ├── dhcp.ip.server: 0.0.0.0
/// ├── dhcp.ip.relay:  0.0.0.0
/// ├── dhcp.hw.mac_addr: AA:BB:CC:DD:EE:FF
/// ├── dhcp.cookie: 0x63825363
/// └── dhcp.option: (TLV)
///     ├── dhcp.option.type: 53 (DHCP Message Type)
///     ├── dhcp.option.length: 1
///     └── dhcp.option.dhcp: Discover (1)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("dhcp", "Dynamic Host Configuration Protocol", Description = "DHCPv4 (RFC 2131)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortClient)]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortServer)]
public sealed partial class DhcpProtocol : IProtocol
{
    #region Constants

    /// <summary>UDP port used by DHCP clients (BOOTP-Client).</summary>
    public const ulong UdpPortClient = 68;

    /// <summary>UDP port used by DHCP servers (BOOTP-Server).</summary>
    public const ulong UdpPortServer = 67;

    /// <summary>Fixed BOOTP header size in bytes including the magic cookie.</summary>
    private const int _FixedHeaderSize = 240;

    /// <summary>Hardware address length (hlen) of a MAC address: 6 bytes.</summary>
    private const int _EthernetHardwareAddressLength = 6;

    /// <summary>BOOTP magic cookie (RFC 2131 §3) preceding the option block.</summary>
    private const uint _MagicCookie = 0x63825363u;

    /// <summary>DHCP option code marking the end of the option block.</summary>
    private const byte _OptionEnd = 255;

    /// <summary>DHCP option code for padding (single-byte option, no length).</summary>
    private const byte _OptionPad = 0;

    /// <summary>DHCP option code for the Subnet Mask (RFC 2132 §3.3).</summary>
    private const byte _OptionSubnetMask = 1;

    /// <summary>DHCP option code for the Router list (RFC 2132 §3.5).</summary>
    private const byte _OptionRouter = 3;

    /// <summary>DHCP option code for the DNS server list (RFC 2132 §3.8).</summary>
    private const byte _OptionDns = 6;

    /// <summary>DHCP option code for the Host Name (RFC 2132 §3.14).</summary>
    private const byte _OptionHostName = 12;

    /// <summary>DHCP option code for the Requested IP Address (RFC 2132 §9.1).</summary>
    private const byte _OptionRequestedIp = 50;

    /// <summary>DHCP option code for the IP Address Lease Time (RFC 2132 §9.2).</summary>
    private const byte _OptionLeaseTime = 51;

    /// <summary>DHCP option code for the DHCP Message Type (RFC 2132 §9.6).</summary>
    private const byte _OptionMessageType = 53;

    /// <summary>DHCP option code for the Server Identifier (RFC 2132 §9.7).</summary>
    private const byte _OptionServerId = 54;

    /// <summary>DHCP option code for the Parameter Request List (RFC 2132 §9.8).</summary>
    private const byte _OptionParamRequestList = 55;

    /// <summary>DHCP option code for the Vendor Class Identifier (RFC 2132 §9.13).</summary>
    private const byte _OptionVendorClass = 60;

    /// <summary>DHCP option code for the Client Identifier (RFC 2132 §9.14).</summary>
    private const byte _OptionClientId = 61;

    /// <summary>Index group for always-present DHCP fields.</summary>
    private const string _DhcpIndexGroup = "dhcp";

    /// <summary>Index group for optional DHCP option fields.</summary>
    private const string _DhcpOptionsIndexGroup = "dhcp.options";

    #endregion

    #region Fields

    [BytesField("dhcp", "Dynamic Host Configuration Protocol", IndexGroup = _DhcpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("dhcp.type", "Message op code", IndexGroup = _DhcpIndexGroup)]
    private FieldId _OpFieldId;

    [U64Field("dhcp.hw.type", "Hardware type", IndexGroup = _DhcpIndexGroup)]
    private FieldId _HwTypeFieldId;

    [U64Field("dhcp.hw.len", "Hardware address length", IndexGroup = _DhcpIndexGroup)]
    private FieldId _HwLenFieldId;

    [U64Field("dhcp.hops", "Hops", IndexGroup = _DhcpIndexGroup)]
    private FieldId _HopsFieldId;

    [U64Field("dhcp.id", "Transaction ID", IndexGroup = _DhcpIndexGroup)]
    private FieldId _XidFieldId;

    [U64Field("dhcp.secs", "Seconds elapsed", IndexGroup = _DhcpIndexGroup)]
    private FieldId _SecsFieldId;

    [U64Field("dhcp.flags", "Bootp flags", IndexGroup = _DhcpIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("dhcp.flags.bc", "Broadcast flag", IndexGroup = _DhcpIndexGroup)]
    private FieldId _FlagsBroadcastFieldId;

    [IPv4Field("dhcp.ip.client", "Client IP address", IndexGroup = _DhcpIndexGroup)]
    private FieldId _CiAddrFieldId;

    [IPv4Field("dhcp.ip.your", "Your (client) IP address", IndexGroup = _DhcpIndexGroup)]
    private FieldId _YiAddrFieldId;

    [IPv4Field("dhcp.ip.server", "Next server IP address", IndexGroup = _DhcpIndexGroup)]
    private FieldId _SiAddrFieldId;

    [IPv4Field("dhcp.ip.relay", "Relay agent IP address", IndexGroup = _DhcpIndexGroup)]
    private FieldId _GiAddrFieldId;

    [MacField("dhcp.hw.mac_addr", "Client MAC address", IndexGroup = _DhcpIndexGroup)]
    private FieldId _ChAddrFieldId;

    [U64Field("dhcp.cookie", "Magic cookie", IndexGroup = _DhcpIndexGroup)]
    private FieldId _CookieFieldId;

    [BytesField("dhcp.option", "Option", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionFieldId;

    [U64Field("dhcp.option.type", "Option type", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionTypeFieldId;

    [U64Field("dhcp.option.length", "Option length", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionLengthFieldId;

    [U64Field("dhcp.option.dhcp", "DHCP message type", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionDhcpMsgTypeFieldId;

    [IPv4Field("dhcp.option.subnet_mask", "Subnet mask", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionSubnetMaskFieldId;

    [IPv4Field("dhcp.option.router", "Router", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionRouterFieldId;

    [IPv4Field("dhcp.option.dns_server", "Domain name server", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionDnsFieldId;

    [IPv4Field("dhcp.option.requested_ip", "Requested IP address", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionRequestedIpFieldId;

    [IPv4Field("dhcp.option.dhcp_server_id", "DHCP server identifier", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionServerIdFieldId;

    [U64Field("dhcp.option.lease_time", "IP address lease time", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionLeaseTimeFieldId;

    [StringField("dhcp.option.hostname", "Host name", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionHostNameFieldId;

    [StringField("dhcp.option.vendor_class_id", "Vendor class identifier", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionVendorClassFieldId;

    [BytesField("dhcp.option.client_id", "Client identifier", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionClientIdFieldId;

    [BytesField("dhcp.option.parameter_request_list", "Parameter Request List", IndexGroup = _DhcpOptionsIndexGroup)]
    private FieldId _OptionParamRequestListFieldId;

    /// <summary>
    /// Parses a DHCPv4 message from the supplied <paramref name="data"/> buffer and appends the decoded
    /// fields under <paramref name="parentField"/>. DHCP is a leaf protocol: nothing is dispatched further.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte (the UDP payload).</param>
    /// <param name="context">Parse context; used to record protocol and index-group presence.</param>
    /// <returns>
    /// The whole <paramref name="data"/> length (including any bytes after the End option), or a
    /// <see cref="ParseError"/> when the data is shorter than the 240-byte fixed header or the magic cookie is wrong.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _FixedHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _FixedHeaderSize, (ulong)data.Length);
        }

        // The magic cookie (header bytes 236-239) is what separates DHCP from plain BOOTP, which shares the UDP ports
        // 67/68 but has no cookie and a different option area; such messages are rejected rather than mis-decoded.
        ReadOnlySpan<byte> span = data.Span;
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(span[236..240]);
        if (cookie != _MagicCookie)
        {
            return ParseError.InvalidData(ProtocolName, "Invalid BOOTP magic cookie");
        }

        // Presence is recorded only after the length and cookie checks, so rejected messages are not indexed as DHCP.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DhcpGroupId);

        // Fixed BOOTP header decoding (network byte order). Offsets: op 0, htype 1, hlen 2, hops 3, xid 4, secs 8,
        // flags 10, ciaddr 12, yiaddr 16, siaddr 20, giaddr 24, chaddr 28 (16 bytes), sname 44 (64), file 108 (128).
        byte op = span[0];
        byte htype = span[1];
        byte hlen = span[2];
        byte hops = span[3];
        uint xid = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
        ushort secs = BinaryPrimitives.ReadUInt16BigEndian(span[8..10]);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(span[10..12]);
        IPv4Address ciaddr = new(BinaryPrimitives.ReadUInt32BigEndian(span[12..16]));
        IPv4Address yiaddr = new(BinaryPrimitives.ReadUInt32BigEndian(span[16..20]));
        IPv4Address siaddr = new(BinaryPrimitives.ReadUInt32BigEndian(span[20..24]));
        IPv4Address giaddr = new(BinaryPrimitives.ReadUInt32BigEndian(span[24..28]));
        // chaddr is shown as a MAC address only when the hardware address length says it is one (hlen == 6, Ethernet and
        // other IEEE 802 networks; the rest of the 16-byte field is padding). For any other length the first 6 bytes are
        // not a MAC address (a shorter address is followed by padding, a longer one is cut), so no MAC field is emitted
        // instead of showing a made-up address. The raw bytes stay visible in the container value.
        bool hasMacAddress = hlen == _EthernetHardwareAddressLength;
        MacAddress chaddr = hasMacAddress
            ? MacAddress.FromBytes(span[28..34])
            : default;

        // Walk the option block once up front to find the DHCP message type: the container title and the packet info
        // are created before the options are decoded and both name the message type. The options are then walked a
        // second time by _ParseOptions; the cheap pre-scan only reads, it allocates nothing.
        byte msgType = _ScanDhcpMessageType(span[_FixedHeaderSize..]);
        string opText = op switch
        {
            1 => "Boot Request (1)",
            2 => "Boot Reply (2)",
            _ => "Unknown"
        };
        string msgTypeText = _GetDhcpMessageTypeText(msgType);
        // The transaction ID is formatted by the Hex8 formatter straight into the text buffer, only when the text is
        // built: formatting it into a string here would allocate for every message, including those parsed without a field tree.
        parentField.SetPacketInfo(ZA.Lazy("DHCP ", msgTypeText, " - Transaction ID 0x", new Hex8(xid)));

        // The container covers the whole message (header and options) so the raw bytes of the undecoded BOOTP fields
        // stay selectable in the UI.
        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, containerValue,
            "DHCP ", msgTypeText, " - Transaction ID 0x", new Hex8(xid));

        container.AppendWithCustomText(_OpFieldId, FieldValue.NewU64(op), opText);
        container.Append(_HwTypeFieldId, FieldValue.NewU64(htype));
        container.Append(_HwLenFieldId, FieldValue.NewU64(hlen));
        container.Append(_HopsFieldId, FieldValue.NewU64(hops));
        container.Append(_XidFieldId, FieldValue.NewU64(xid));
        container.Append(_SecsFieldId, FieldValue.NewU64(secs));
        // Bit 15 (0x8000) is the DHCP broadcast flag (RFC 2131 §2).
        // Display shows hex value followed by the active flag name in brackets.
        bool broadcastFlag = (flags & 0x8000) != 0;
        container.AppendWithCustomText(_FlagsFieldId, FieldValue.NewU64(flags),
            Helpers.DisplayTables.FormatHexU16(flags), Dhcp.DhcpFlagsFormatter.Format(flags));
        container.Append(_FlagsBroadcastFieldId, FieldValue.NewBool(broadcastFlag));
        container.Append(_CiAddrFieldId, FieldValue.NewIPv4(ciaddr));
        container.Append(_YiAddrFieldId, FieldValue.NewIPv4(yiaddr));
        container.Append(_SiAddrFieldId, FieldValue.NewIPv4(siaddr));
        container.Append(_GiAddrFieldId, FieldValue.NewIPv4(giaddr));
        if (hasMacAddress)
        {
            container.Append(_ChAddrFieldId, FieldValue.NewMacAddress(chaddr));
        }

        container.Append(_CookieFieldId, FieldValue.NewU64(cookie));

        // Walk options. Bytes after the cookie are TLV-encoded except the single-byte
        // sentinels Pad (0x00) and End (0xFF). A malformed option block ends the walk without an error: the fixed
        // header above has already been decoded, so the message is still reported.
        _ParseOptions(in container, data[_FixedHeaderSize..], in context);
        return data.Length;
    }

    /// <summary>
    /// Scans the option block once to find the DHCP message type (option 53).
    /// Returns 0 when no message type option is found, or when the block is truncated or malformed before it
    /// (the same bounds rules as <see cref="_ParseOptions"/>, so both walks agree on where the options end).
    /// </summary>
    private static byte _ScanDhcpMessageType(ReadOnlySpan<byte> options)
    {
        // Bounds: i + 2 + length is the end of the current option. Both operands are far below int.MaxValue
        // (length is one byte, i is an index into a packet), so the sums cannot overflow.
        int i = 0;
        while (i < options.Length)
        {
            byte type = options[i];
            if (type == _OptionEnd)
            {
                return 0;
            }
            if (type == _OptionPad)
            {
                i++;
                continue;
            }
            if (i + 1 >= options.Length)
            {
                return 0;
            }
            byte length = options[i + 1];
            if (i + 2 + length > options.Length)
            {
                return 0;
            }
            if (type == _OptionMessageType && length == 1)
            {
                return options[i + 2];
            }
            i += 2 + length;
        }
        return 0;
    }

    /// <summary>
    /// Walks the TLV option block and emits one container per option.
    /// Unknown options are emitted with type and length only so filters can still
    /// match on the option code. Stops silently at the End option, or at the first option whose length
    /// byte or value would run past the block (truncated or malformed block); options before that point are kept.
    /// </summary>
    private void _ParseOptions(in MutField container, ReadOnlyMemory<byte> options, in ParseContext context)
    {
        // The span is used for the bounds-checked walk; options.Slice produces the Memory views stored in the fields.
        ReadOnlySpan<byte> span = options.Span;
        int i = 0;
        while (i < span.Length)
        {
            byte type = span[i];
            if (type == _OptionEnd)
            {
                return;
            }
            if (type == _OptionPad)
            {
                i++;
                continue;
            }
            if (i + 1 >= span.Length)
            {
                return;
            }
            byte length = span[i + 1];
            if (i + 2 + length > span.Length)
            {
                return;
            }

            ReadOnlyMemory<byte> optionData = options.Slice(i + 2, length);
            string typeText = _GetDhcpOptionTypeText(type);

            // First real TLV option proves the dhcp.options group is present. Recording here
            // (rather than unconditionally in Parse) keeps the index free of false positives
            // for messages that carry only Pad/End sentinels after the magic cookie.
            context.RecordGroupPresence(_DhcpOptionsGroupId);

            MutField optContainer = container.AppendWithCustomText(
                _OptionFieldId,
                FieldValue.NewBytes(options.Slice(i, 2 + length)),
                "Option: (", (ulong)type, ") ", typeText);
            optContainer.AppendWithCustomText(_OptionTypeFieldId, FieldValue.NewU64(type), typeText);
            optContainer.Append(_OptionLengthFieldId, FieldValue.NewU64(length));

            _EmitOptionPayload(in optContainer, type, optionData);
            i += 2 + length;
        }
    }

    /// <summary>
    /// Emits the option-specific child fields for a recognised option code. A recognised option whose length
    /// does not match its fixed size (for example a subnet mask that is not 4 bytes) falls through to the default
    /// branch and keeps only the generic type and length fields; the payload is never reinterpreted.
    /// </summary>
    private void _EmitOptionPayload(in MutField optContainer, byte type, ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        switch (type)
        {
            case _OptionMessageType when span.Length == 1:
                optContainer.AppendWithCustomText(
                    _OptionDhcpMsgTypeFieldId,
                    FieldValue.NewU64(span[0]),
                    _GetDhcpMessageTypeText(span[0]));
                break;
            case _OptionSubnetMask when span.Length == 4:
                optContainer.Append(_OptionSubnetMaskFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case _OptionRouter:
                _EmitIpv4List(in optContainer, _OptionRouterFieldId, span);
                break;
            case _OptionDns:
                _EmitIpv4List(in optContainer, _OptionDnsFieldId, span);
                break;
            case _OptionRequestedIp when span.Length == 4:
                optContainer.Append(_OptionRequestedIpFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case _OptionServerId when span.Length == 4:
                optContainer.Append(_OptionServerIdFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case _OptionLeaseTime when span.Length == 4:
                optContainer.Append(_OptionLeaseTimeFieldId,
                    FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(span)));
                break;
            // Text options are decoded as ASCII (RFC 2132 §3.14 specifies the host name as ASCII text); any byte above
            // 0x7F becomes '?' instead of failing, so malformed or legacy values still display. The string allocation
            // is the cost of exposing these options as a StringField.
            case _OptionHostName:
                optContainer.Append(_OptionHostNameFieldId,
                    FieldValue.NewString(System.Text.Encoding.ASCII.GetString(span)));
                break;
            case _OptionVendorClass:
                optContainer.Append(_OptionVendorClassFieldId,
                    FieldValue.NewString(System.Text.Encoding.ASCII.GetString(span)));
                break;
            case _OptionClientId:
                optContainer.Append(_OptionClientIdFieldId, FieldValue.NewBytes(data));
                break;
            case _OptionParamRequestList:
                optContainer.Append(_OptionParamRequestListFieldId, FieldValue.NewBytes(data));
                break;
            default:
                // Unknown / unparsed option — type and length are already attached.
                break;
        }
    }

    /// <summary>
    /// Emits each IPv4 address from a tightly packed 4-byte-aligned span (router and DNS lists). Trailing bytes that do
    /// not fill a whole address are ignored.
    /// </summary>
    private static void _EmitIpv4List(in MutField optContainer, FieldId fieldId, ReadOnlySpan<byte> span)
    {
        for (int i = 0; i + 4 <= span.Length; i += 4)
        {
            optContainer.Append(fieldId,
                FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span[i..(i + 4)]))));
        }
    }

    /// <summary>Returns a human readable label for the DHCP message type (option 53 value).</summary>
    private static string _GetDhcpMessageTypeText(byte msgType) => msgType switch
    {
        1 => "Discover (1)",
        2 => "Offer (2)",
        3 => "Request (3)",
        4 => "Decline (4)",
        5 => "ACK (5)",
        6 => "NAK (6)",
        7 => "Release (7)",
        8 => "Inform (8)",
        _ => "Unknown",
    };

    /// <summary>Returns a human readable label for the DHCP option code (TLV type byte).</summary>
    private static string _GetDhcpOptionTypeText(byte type) => type switch
    {
        _OptionSubnetMask => "Subnet Mask",
        _OptionRouter => "Router",
        _OptionDns => "Domain Name Server",
        _OptionHostName => "Host Name",
        _OptionRequestedIp => "Requested IP Address",
        _OptionLeaseTime => "IP Address Lease Time",
        _OptionMessageType => "DHCP Message Type",
        _OptionServerId => "DHCP Server Identifier",
        _OptionParamRequestList => "Parameter Request List",
        _OptionVendorClass => "Vendor Class Identifier",
        _OptionClientId => "Client Identifier",
        _OptionEnd => "End",
        _OptionPad => "Pad",
        _ => "Unknown",
    };

    #endregion
}
