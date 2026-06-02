// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Dynamic Host Configuration Protocol for IPv4 (RFC 2131 / RFC 2132).
/// Parses the BOOTP fixed header (240 bytes incl. magic cookie) and a
/// curated subset of common DHCP options.
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
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
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
    private const int FixedHeaderSize = 240;

    /// <summary>BOOTP magic cookie (RFC 2131 §3) preceding the option block.</summary>
    private const uint MagicCookie = 0x63825363u;

    /// <summary>DHCP option code marking the end of the option block.</summary>
    private const byte OptionEnd = 255;

    /// <summary>DHCP option code for padding (single-byte option, no length).</summary>
    private const byte OptionPad = 0;

    /// <summary>DHCP option code for the Subnet Mask (RFC 2132 §3.3).</summary>
    private const byte OptionSubnetMask = 1;

    /// <summary>DHCP option code for the Router list (RFC 2132 §3.5).</summary>
    private const byte OptionRouter = 3;

    /// <summary>DHCP option code for the DNS server list (RFC 2132 §3.8).</summary>
    private const byte OptionDns = 6;

    /// <summary>DHCP option code for the Host Name (RFC 2132 §3.14).</summary>
    private const byte OptionHostName = 12;

    /// <summary>DHCP option code for the Requested IP Address (RFC 2132 §9.1).</summary>
    private const byte OptionRequestedIp = 50;

    /// <summary>DHCP option code for the IP Address Lease Time (RFC 2132 §9.2).</summary>
    private const byte OptionLeaseTime = 51;

    /// <summary>DHCP option code for the DHCP Message Type (RFC 2132 §9.6).</summary>
    private const byte OptionMessageType = 53;

    /// <summary>DHCP option code for the Server Identifier (RFC 2132 §9.7).</summary>
    private const byte OptionServerId = 54;

    /// <summary>DHCP option code for the Parameter Request List (RFC 2132 §9.8).</summary>
    private const byte OptionParamRequestList = 55;

    /// <summary>DHCP option code for the Vendor Class Identifier (RFC 2132 §9.13).</summary>
    private const byte OptionVendorClass = 60;

    /// <summary>DHCP option code for the Client Identifier (RFC 2132 §9.14).</summary>
    private const byte OptionClientId = 61;

    /// <summary>Index group for always-present DHCP fields.</summary>
    private const string DhcpIndexGroup = "dhcp";

    /// <summary>Index group for optional DHCP option fields.</summary>
    private const string DhcpOptionsIndexGroup = "dhcp.options";

    #endregion

    #region Fields

    [BytesField("dhcp", "Dynamic Host Configuration Protocol", IndexGroup = DhcpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("dhcp.type", "Message op code", IndexGroup = DhcpIndexGroup)]
    private FieldId _OpFieldId;

    [U64Field("dhcp.hw.type", "Hardware type", IndexGroup = DhcpIndexGroup)]
    private FieldId _HwTypeFieldId;

    [U64Field("dhcp.hw.len", "Hardware address length", IndexGroup = DhcpIndexGroup)]
    private FieldId _HwLenFieldId;

    [U64Field("dhcp.hops", "Hops", IndexGroup = DhcpIndexGroup)]
    private FieldId _HopsFieldId;

    [U64Field("dhcp.id", "Transaction ID", IndexGroup = DhcpIndexGroup)]
    private FieldId _XidFieldId;

    [U64Field("dhcp.secs", "Seconds elapsed", IndexGroup = DhcpIndexGroup)]
    private FieldId _SecsFieldId;

    [U64Field("dhcp.flags", "Bootp flags", IndexGroup = DhcpIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("dhcp.flags.bc", "Broadcast flag", IndexGroup = DhcpIndexGroup)]
    private FieldId _FlagsBroadcastFieldId;

    [IPv4Field("dhcp.ip.client", "Client IP address", IndexGroup = DhcpIndexGroup)]
    private FieldId _CiAddrFieldId;

    [IPv4Field("dhcp.ip.your", "Your (client) IP address", IndexGroup = DhcpIndexGroup)]
    private FieldId _YiAddrFieldId;

    [IPv4Field("dhcp.ip.server", "Next server IP address", IndexGroup = DhcpIndexGroup)]
    private FieldId _SiAddrFieldId;

    [IPv4Field("dhcp.ip.relay", "Relay agent IP address", IndexGroup = DhcpIndexGroup)]
    private FieldId _GiAddrFieldId;

    [MacField("dhcp.hw.mac_addr", "Client MAC address", IndexGroup = DhcpIndexGroup)]
    private FieldId _ChAddrFieldId;

    [U64Field("dhcp.cookie", "Magic cookie", IndexGroup = DhcpIndexGroup)]
    private FieldId _CookieFieldId;

    [BytesField("dhcp.option", "Option", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionFieldId;

    [U64Field("dhcp.option.type", "Option type", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionTypeFieldId;

    [U64Field("dhcp.option.length", "Option length", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionLengthFieldId;

    [U64Field("dhcp.option.dhcp", "DHCP message type", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionDhcpMsgTypeFieldId;

    [IPv4Field("dhcp.option.subnet_mask", "Subnet mask", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionSubnetMaskFieldId;

    [IPv4Field("dhcp.option.router", "Router", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionRouterFieldId;

    [IPv4Field("dhcp.option.dns_server", "Domain name server", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionDnsFieldId;

    [IPv4Field("dhcp.option.requested_ip", "Requested IP address", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionRequestedIpFieldId;

    [IPv4Field("dhcp.option.dhcp_server_id", "DHCP server identifier", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionServerIdFieldId;

    [U64Field("dhcp.option.lease_time", "IP address lease time", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionLeaseTimeFieldId;

    [StringField("dhcp.option.hostname", "Host name", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionHostNameFieldId;

    [StringField("dhcp.option.vendor_class_id", "Vendor class identifier", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionVendorClassFieldId;

    [BytesField("dhcp.option.client_id", "Client identifier", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionClientIdFieldId;

    [BytesField("dhcp.option.parameter_request_list", "Parameter Request List", IndexGroup = DhcpOptionsIndexGroup)]
    private FieldId _OptionParamRequestListFieldId;

    /// <summary>
    /// Parses a Dhcp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < FixedHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, FixedHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(span[236..240]);
        if (cookie != MagicCookie)
        {
            return ParseError.InvalidData(ProtocolName, "Invalid BOOTP magic cookie");
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DhcpGroupId);

        // Fixed BOOTP header decoding (network byte order).
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
        // Only the first 6 bytes of chaddr are used for Ethernet (hlen == 6); the rest is padding.
        MacAddress chaddr = MacAddress.FromBytes(span[28..34]);

        // Walk option block once to locate the DHCP message type for the summary line.
        byte msgType = ScanDhcpMessageType(span[FixedHeaderSize..]);
        string opText = op switch
        {
            1 => "Boot Request (1)",
            2 => "Boot Reply (2)",
            _ => "Unknown"
        };
        string msgTypeText = GetDhcpMessageTypeText(msgType);
        LazyString summary = ZA.Lazy("DHCP ", msgTypeText, " - Transaction ID 0x", xid.ToString("X8"));
        parentField.SetPacketInfo(ZA.Lazy("DHCP ", msgTypeText, " - Transaction ID 0x", xid.ToString("X8")));

        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, summary);

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
            ZA.Lazy(Helpers.DisplayTables.FormatHexU16(flags), Dhcp.DhcpFlagsFormatter.Format(flags)));
        container.Append(_FlagsBroadcastFieldId, FieldValue.NewBool(broadcastFlag));
        container.Append(_CiAddrFieldId, FieldValue.NewIPv4(ciaddr));
        container.Append(_YiAddrFieldId, FieldValue.NewIPv4(yiaddr));
        container.Append(_SiAddrFieldId, FieldValue.NewIPv4(siaddr));
        container.Append(_GiAddrFieldId, FieldValue.NewIPv4(giaddr));
        container.Append(_ChAddrFieldId, FieldValue.NewMacAddress(chaddr));
        container.Append(_CookieFieldId, FieldValue.NewU64(cookie));

        // Walk options. Bytes after the cookie are TLV-encoded except the single-byte
        // sentinels Pad (0x00) and End (0xFF).
        ParseOptions(in container, data[FixedHeaderSize..], in context);
        return data.Length;
    }

    /// <summary>
    /// Scans the option block once to find the DHCP message type (option 53).
    /// Returns 0 when no message type option is found.
    /// </summary>
    private static byte ScanDhcpMessageType(ReadOnlySpan<byte> options)
    {
        int i = 0;
        while (i < options.Length)
        {
            byte type = options[i];
            if (type == OptionEnd)
            {
                return 0;
            }
            if (type == OptionPad)
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
            if (type == OptionMessageType && length == 1)
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
    /// match on the option code.
    /// </summary>
    private void ParseOptions(in MutField container, ReadOnlyMemory<byte> options, in ParseContext context)
    {
        ReadOnlySpan<byte> span = options.Span;
        int i = 0;
        while (i < span.Length)
        {
            byte type = span[i];
            if (type == OptionEnd)
            {
                return;
            }
            if (type == OptionPad)
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
            string typeText = GetDhcpOptionTypeText(type);

            // First real TLV option proves the dhcp.options group is present. Recording here
            // (rather than unconditionally in Parse) keeps the index free of false positives
            // for messages that carry only Pad/End sentinels after the magic cookie.
            context.RecordGroupPresence(_DhcpOptionsGroupId);

            MutField optContainer = container.AppendWithCustomText(
                _OptionFieldId,
                FieldValue.NewBytes(options.Slice(i, 2 + length)),
                ZA.Lazy("Option: (", (ulong)type, ") ", typeText));
            optContainer.AppendWithCustomText(_OptionTypeFieldId, FieldValue.NewU64(type), typeText);
            optContainer.Append(_OptionLengthFieldId, FieldValue.NewU64(length));

            EmitOptionPayload(in optContainer, type, optionData, in context);
            i += 2 + length;
        }
    }

    /// <summary>Emits the option-specific child fields for a recognised option code.</summary>
    private void EmitOptionPayload(in MutField optContainer, byte type, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        switch (type)
        {
            case OptionMessageType when span.Length == 1:
                optContainer.AppendWithCustomText(
                    _OptionDhcpMsgTypeFieldId,
                    FieldValue.NewU64(span[0]),
                    GetDhcpMessageTypeText(span[0]));
                break;
            case OptionSubnetMask when span.Length == 4:
                optContainer.Append(_OptionSubnetMaskFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case OptionRouter:
                EmitIpv4List(in optContainer, _OptionRouterFieldId, span, in context);
                break;
            case OptionDns:
                EmitIpv4List(in optContainer, _OptionDnsFieldId, span, in context);
                break;
            case OptionRequestedIp when span.Length == 4:
                optContainer.Append(_OptionRequestedIpFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case OptionServerId when span.Length == 4:
                optContainer.Append(_OptionServerIdFieldId,
                    FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span))));
                break;
            case OptionLeaseTime when span.Length == 4:
                optContainer.Append(_OptionLeaseTimeFieldId,
                    FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(span)));
                break;
            case OptionHostName:
                optContainer.Append(_OptionHostNameFieldId,
                    FieldValue.NewString(System.Text.Encoding.ASCII.GetString(span)));
                break;
            case OptionVendorClass:
                optContainer.Append(_OptionVendorClassFieldId,
                    FieldValue.NewString(System.Text.Encoding.ASCII.GetString(span)));
                break;
            case OptionClientId:
                optContainer.Append(_OptionClientIdFieldId, FieldValue.NewBytes(data));
                break;
            case OptionParamRequestList:
                optContainer.Append(_OptionParamRequestListFieldId, FieldValue.NewBytes(data));
                break;
            default:
                // Unknown / unparsed option — type and length are already attached.
                break;
        }
    }

    /// <summary>Emits each IPv4 address from a tightly packed 4-byte-aligned span.</summary>
    private static void EmitIpv4List(in MutField optContainer, FieldId fieldId, ReadOnlySpan<byte> span, in ParseContext context)
    {
        for (int i = 0; i + 4 <= span.Length; i += 4)
        {
            optContainer.Append(fieldId,
                FieldValue.NewIPv4(new IPv4Address(BinaryPrimitives.ReadUInt32BigEndian(span[i..(i + 4)]))));
        }
    }

    /// <summary>Returns a human readable label for the DHCP message type (option 53 value).</summary>
    private static string GetDhcpMessageTypeText(byte msgType) => msgType switch
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
    private static string GetDhcpOptionTypeText(byte type) => type switch
    {
        OptionSubnetMask => "Subnet Mask",
        OptionRouter => "Router",
        OptionDns => "Domain Name Server",
        OptionHostName => "Host Name",
        OptionRequestedIp => "Requested IP Address",
        OptionLeaseTime => "IP Address Lease Time",
        OptionMessageType => "DHCP Message Type",
        OptionServerId => "DHCP Server Identifier",
        OptionParamRequestList => "Parameter Request List",
        OptionVendorClass => "Vendor Class Identifier",
        OptionClientId => "Client Identifier",
        OptionEnd => "End",
        OptionPad => "Pad",
        _ => "Unknown",
    };

    #endregion
}
