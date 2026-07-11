// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Dynamic Host Configuration Protocol for IPv6 (RFC 8415). Parses the 4-byte
/// client/server header (msg-type + 24-bit transaction-id) and a curated subset
/// of common options. Relay forward / relay reply messages share the same
/// option block layout but use a 2-byte msg-type/hop-count + 32 bytes of
/// link/peer addresses; only the first byte (msg-type) is recognised here so
/// the header bytes are reported and option parsing is skipped for relay
/// messages until further support is added.
/// <para>Field tree structure:</para>
/// <code>
/// dhcpv6: DHCPv6 SOLICIT (1) - Transaction ID 0x123456
/// ├── dhcpv6.msgtype: SOLICIT (1)
/// ├── dhcpv6.xid: 0x00123456
/// └── dhcpv6.option: (TLV)
///     ├── dhcpv6.option.code: 1 (OPTION_CLIENTID)
///     ├── dhcpv6.option.length: 14
///     └── dhcpv6.option.value: (raw bytes)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("dhcpv6", "Dynamic Host Configuration Protocol for IPv6", Description = "DHCPv6 (RFC 8415)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortClient)]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortServer)]
public sealed partial class Dhcpv6Protocol : IProtocol
{
    #region Constants

    /// <summary>UDP port used by DHCPv6 clients.</summary>
    public const ulong UdpPortClient = 546;

    /// <summary>UDP port used by DHCPv6 servers.</summary>
    public const ulong UdpPortServer = 547;

    /// <summary>Minimum size of a client/server DHCPv6 message (msg-type + xid).</summary>
    private const int _FixedHeaderSize = 4;

    /// <summary>DHCPv6 message-type for RELAY-FORW.</summary>
    private const byte _MsgTypeRelayForward = 12;

    /// <summary>DHCPv6 message-type for RELAY-REPL.</summary>
    private const byte _MsgTypeRelayReply = 13;

    /// <summary>DHCPv6 option code OPTION_CLIENTID.</summary>
    private const ushort _OptionClientId = 1;

    /// <summary>DHCPv6 option code OPTION_SERVERID.</summary>
    private const ushort _OptionServerId = 2;

    /// <summary>DHCPv6 option code OPTION_IA_NA.</summary>
    private const ushort _OptionIaNa = 3;

    /// <summary>DHCPv6 option code OPTION_IA_TA.</summary>
    private const ushort _OptionIaTa = 4;

    /// <summary>DHCPv6 option code OPTION_IAADDR.</summary>
    private const ushort _OptionIaAddr = 5;

    /// <summary>DHCPv6 option code OPTION_ORO (Option Request Option).</summary>
    private const ushort _OptionOro = 6;

    /// <summary>DHCPv6 option code OPTION_ELAPSED_TIME.</summary>
    private const ushort _OptionElapsedTime = 8;

    /// <summary>DHCPv6 option code OPTION_STATUS_CODE.</summary>
    private const ushort _OptionStatusCode = 13;

    /// <summary>DHCPv6 option code OPTION_RAPID_COMMIT.</summary>
    private const ushort _OptionRapidCommit = 14;

    /// <summary>DHCPv6 option code OPTION_DNS_SERVERS.</summary>
    private const ushort _OptionDnsServers = 23;

    /// <summary>DHCPv6 option code OPTION_DOMAIN_LIST.</summary>
    private const ushort _OptionDomainList = 24;

    /// <summary>DHCPv6 option code OPTION_IA_PD.</summary>
    private const ushort _OptionIaPd = 25;

    /// <summary>DHCPv6 option code OPTION_IAPREFIX.</summary>
    private const ushort _OptionIaPrefix = 26;

    /// <summary>Index group for always-present DHCPv6 fields.</summary>
    private const string _Dhcpv6IndexGroup = "dhcpv6";

    /// <summary>Index group for DHCPv6 option fields.</summary>
    private const string _Dhcpv6OptionsIndexGroup = "dhcpv6.options";

    #endregion

    #region Fields

    [BytesField("dhcpv6", "DHCPv6", IndexGroup = _Dhcpv6IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("dhcpv6.msgtype", "Message type", IndexGroup = _Dhcpv6IndexGroup)]
    private FieldId _MsgTypeFieldId;

    [U64Field("dhcpv6.xid", "Transaction ID", IndexGroup = _Dhcpv6IndexGroup)]
    private FieldId _XidFieldId;

    [BytesField("dhcpv6.option", "Option", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionFieldId;

    [U64Field("dhcpv6.option.code", "Option code", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionCodeFieldId;

    [U64Field("dhcpv6.option.length", "Option length", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionLengthFieldId;

    [BytesField("dhcpv6.option.value", "Option value", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionValueFieldId;

    [U64Field("dhcpv6.option.elapsed_time", "Elapsed time", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionElapsedTimeFieldId;

    [BoolField("dhcpv6.option.rapid_commit", "Rapid commit", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionRapidCommitFieldId;

    [U64Field("dhcpv6.option.status_code", "Status code", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionStatusCodeFieldId;

    [IPv6Field("dhcpv6.option.dns_server", "Domain name server", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionDnsServerFieldId;

    [IPv6Field("dhcpv6.option.iaaddr", "IA Address", IndexGroup = _Dhcpv6OptionsIndexGroup)]
    private FieldId _OptionIaAddrFieldId;

    /// <summary>
    /// Parses a Dhcpv6 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _FixedHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _FixedHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;
        byte msgType = span[0];

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Dhcpv6GroupId);

        string msgTypeText = _GetMessageTypeText(msgType);
        LazyString summary = ZA.Lazy("DHCPv6 ", msgTypeText);
        parentField.SetPacketInfo(ZA.Lazy("DHCPv6 ", msgTypeText));

        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, summary);

        container.AppendWithCustomText(_MsgTypeFieldId, FieldValue.NewU64(msgType), msgTypeText);

        if (msgType is _MsgTypeRelayForward or _MsgTypeRelayReply)
        {
            // Relay messages have a different header layout (msg-type + hop-count + link-addr + peer-addr).
            // Recognise but do not decode further at this stage.
            return data.Length;
        }

        // 24-bit transaction ID stored in big-endian byte order at offsets 1..4.
        uint xid = ((uint)span[1] << 16) | ((uint)span[2] << 8) | span[3];
        container.Append(_XidFieldId, FieldValue.NewU64(xid));

        _ParseOptions(in container, data[_FixedHeaderSize..], in context);
        return data.Length;
    }

    /// <summary>
    /// Walks the DHCPv6 option block (RFC 8415 §16). Each option is a 2-byte
    /// big-endian code followed by a 2-byte big-endian length and the option
    /// data. There is no End sentinel — parsing stops at end of data.
    /// </summary>
    private void _ParseOptions(in MutField container, ReadOnlyMemory<byte> options, in ParseContext context)
    {
        ReadOnlySpan<byte> span = options.Span;
        int i = 0;
        while (i + 4 <= span.Length)
        {
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(span[i..(i + 2)]);
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(span[(i + 2)..(i + 4)]);
            if (i + 4 + length > span.Length)
            {
                return;
            }

            ReadOnlyMemory<byte> optionData = options.Slice(i + 4, length);
            string codeText = _GetOptionCodeText(code);

            // First well-formed option proves the dhcpv6.options group is present. Recording
            // here keeps the index free of false positives for option blocks that are empty or
            // truncated before any complete TLV.
            context.RecordGroupPresence(_Dhcpv6OptionsGroupId);

            MutField optContainer = container.AppendWithCustomText(
                _OptionFieldId,
                FieldValue.NewBytes(options.Slice(i, 4 + length)),
                ZA.Lazy("Option: (", (ulong)code, ") ", codeText));
            optContainer.AppendWithCustomText(_OptionCodeFieldId, FieldValue.NewU64(code), codeText);
            optContainer.Append(_OptionLengthFieldId, FieldValue.NewU64(length));
            optContainer.Append(_OptionValueFieldId, FieldValue.NewBytes(optionData));

            _EmitOptionPayload(in optContainer, code, optionData, in context);
            i += 4 + length;
        }
    }

    /// <summary>Emits option-specific child fields for known DHCPv6 option codes.</summary>
    private void _EmitOptionPayload(in MutField optContainer, ushort code, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        switch (code)
        {
            case _OptionElapsedTime when span.Length == 2:
                // Elapsed time uses 1/100 second units (RFC 8415 §21.9).
                optContainer.Append(_OptionElapsedTimeFieldId,
                    FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(span)));
                break;
            case _OptionRapidCommit:
                // Rapid commit is a present/absent flag (RFC 8415 §21.14, length must be 0).
                optContainer.Append(_OptionRapidCommitFieldId, FieldValue.NewBool(true));
                break;
            case _OptionStatusCode when span.Length >= 2:
                optContainer.Append(_OptionStatusCodeFieldId,
                    FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(span[..2])));
                break;
            case _OptionDnsServers:
                _EmitIpv6List(in optContainer, _OptionDnsServerFieldId, span, in context);
                break;
            case _OptionIaAddr when span.Length >= 16:
                optContainer.Append(_OptionIaAddrFieldId, FieldValue.NewIPv6(IPv6Address.FromBytes(span[..16])));
                break;
            default:
                // Unknown / unparsed — code, length and raw value are already attached.
                break;
        }
    }

    /// <summary>Emits each IPv6 address from a tightly packed 16-byte-aligned span.</summary>
    private static void _EmitIpv6List(in MutField optContainer, FieldId fieldId, ReadOnlySpan<byte> span, in ParseContext context)
    {
        for (int i = 0; i + 16 <= span.Length; i += 16)
        {
            optContainer.Append(fieldId, FieldValue.NewIPv6(IPv6Address.FromBytes(span[i..(i + 16)])));
        }
    }

    /// <summary>Returns a human-readable label for the DHCPv6 message type byte.</summary>
    private static string _GetMessageTypeText(byte msgType) => msgType switch
    {
        1 => "SOLICIT (1)",
        2 => "ADVERTISE (2)",
        3 => "REQUEST (3)",
        4 => "CONFIRM (4)",
        5 => "RENEW (5)",
        6 => "REBIND (6)",
        7 => "REPLY (7)",
        8 => "RELEASE (8)",
        9 => "DECLINE (9)",
        10 => "RECONFIGURE (10)",
        11 => "INFORMATION-REQUEST (11)",
        _MsgTypeRelayForward => "RELAY-FORW (12)",
        _MsgTypeRelayReply => "RELAY-REPL (13)",
        _ => "Unknown",
    };

    /// <summary>Returns a human-readable label for a DHCPv6 option code.</summary>
    private static string _GetOptionCodeText(ushort code) => code switch
    {
        _OptionClientId => "OPTION_CLIENTID",
        _OptionServerId => "OPTION_SERVERID",
        _OptionIaNa => "OPTION_IA_NA",
        _OptionIaTa => "OPTION_IA_TA",
        _OptionIaAddr => "OPTION_IAADDR",
        _OptionOro => "OPTION_ORO",
        _OptionElapsedTime => "OPTION_ELAPSED_TIME",
        _OptionStatusCode => "OPTION_STATUS_CODE",
        _OptionRapidCommit => "OPTION_RAPID_COMMIT",
        _OptionDnsServers => "OPTION_DNS_SERVERS",
        _OptionDomainList => "OPTION_DOMAIN_LIST",
        _OptionIaPd => "OPTION_IA_PD",
        _OptionIaPrefix => "OPTION_IAPREFIX",
        _ => "Unknown",
    };

    #endregion
}
