// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// SOME/IP (Scalable service-Oriented MiddlewarE over IP) protocol parser.
/// Automotive middleware protocol (AUTOSAR) for service-oriented communication.
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. See remarks for details.</para>
/// <para>Field tree structure:</para>
/// <code>
/// someip: SOME/IP, Service: 0x0123, Method: 0x4567, REQUEST
/// ├── someip.messageid: 0x01234567
/// ├── someip.serviceid: 0x0123
/// ├── someip.methodid: 0x4567
/// ├── someip.length: 24
/// ├── someip.clientid: 0x0001
/// ├── someip.sessionid: 0x0001
/// ├── someip.version: 1
/// ├── someip.ifversion: 1
/// ├── someip.msgtype: REQUEST (0x00)
/// │   ├── someip.msgtype.ack: false
/// │   └── someip.msgtype.tp: false
/// ├── someip.returncode: E_OK (0x00)
/// ├── someip.tp: SOME/IP-TP, Offset: 0 bytes, More Segments  [only when TP flag set]
/// │   ├── someip.tp.offset: 0 bytes
/// │   ├── someip.tp.more: true
/// │   └── someip.tp.reserved: 0x0
/// ├── someip_sd: SOME/IP-SD                                    [only when msgid=0xFFFF8100]
/// │   ├── someip_sd.flags: 0xC0
/// │   │   ├── someip_sd.flags.reboot: true
/// │   │   ├── someip_sd.flags.unicast: true
/// │   │   └── someip_sd.flags.initial_events: false
/// │   ├── someip_sd.entries: Entries Array (2 entries)
/// │   │   └── someip_sd.entry: OfferService
/// │   │       ├── someip_sd.entry.type: OfferService (0x01)
/// │   │       ├── someip_sd.entry.index1: 0
/// │   │       ├── someip_sd.entry.index2: 0
/// │   │       ├── someip_sd.entry.n_opt_1: 1
/// │   │       ├── someip_sd.entry.n_opt_2: 0
/// │   │       ├── someip_sd.entry.serviceid: 0x0001
/// │   │       ├── someip_sd.entry.instanceid: 0x0001
/// │   │       ├── someip_sd.entry.majorver: 1
/// │   │       ├── someip_sd.entry.ttl: 3
/// │   │       └── someip_sd.entry.minorver: 0
/// │   └── someip_sd.options: Options Array (12 bytes)
/// │       └── someip_sd.option: IPv4 Endpoint
/// │           ├── someip_sd.option.length: 9
/// │           ├── someip_sd.option.type: IPv4 Endpoint (0x04)
/// │           ├── someip_sd.option.ipv4: 192.168.1.100
/// │           ├── someip_sd.option.proto: UDP (17)
/// │           └── someip_sd.option.port: 30490
/// └── someip.payload: (N bytes)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.
/// The TP segment reassembler accumulates mutable state across packets and must not be
/// accessed concurrently.</para>
/// </remarks>
[Protocol("someip", "SOME/IP", Description = "SOME/IP (AUTOSAR)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortKey)]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class SomeIpProtocol : IProtocol
{
    #region Constants

    /// <summary>Default UDP port for SOME/IP.</summary>
    public const ulong UdpPortKey = 30490;

    /// <summary>Default TCP port for SOME/IP.</summary>
    public const ulong TcpPortKey = 30490;

    /// <summary>Protocol table name for SOME/IP message ID dispatch.</summary>
    public const string MessageIdTableName = "someip.messageid";

    /// <summary>Index group for always-present SOME/IP fields.</summary>
    private const string SomeIpIndexGroup = "someip";

    #endregion

    #region Protocol container

    [BytesField("someip", "SOME/IP", IndexGroup = SomeIpIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch table — dispatches by Message ID to payload deserializers

    [ProtocolTableU64(MessageIdTableName, "SOME/IP Message ID")]
    private ProtocolTableId _MessageIdTableId;

    #endregion

    #region Header fields (always present)

    [U64Field("someip.messageid", "Message ID", IndexGroup = SomeIpIndexGroup)]
    private FieldId _MessageIdFieldId;

    [U64Field("someip.serviceid", "Service ID", IndexGroup = SomeIpIndexGroup)]
    private FieldId _ServiceIdFieldId;

    [U64Field("someip.methodid", "Method ID", IndexGroup = SomeIpIndexGroup)]
    private FieldId _MethodIdFieldId;

    [U64Field("someip.length", "Length", IndexGroup = SomeIpIndexGroup)]
    private FieldId _LengthFieldId;

    [U64Field("someip.clientid", "Client ID", IndexGroup = SomeIpIndexGroup)]
    private FieldId _ClientIdFieldId;

    [U64Field("someip.sessionid", "Session ID", IndexGroup = SomeIpIndexGroup)]
    private FieldId _SessionIdFieldId;

    [U64Field("someip.version", "Protocol Version", IndexGroup = SomeIpIndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("someip.ifversion", "Interface Version", IndexGroup = SomeIpIndexGroup)]
    private FieldId _IfVersionFieldId;

    [U64Field("someip.msgtype", "Message Type", IndexGroup = SomeIpIndexGroup)]
    private FieldId _MsgTypeFieldId;

    #endregion

    #region Message type sub-fields (ACK and TP flag decomposition)

    [BoolField("someip.msgtype.ack", "ACK Flag", IndexGroup = SomeIpIndexGroup)]
    private FieldId _MsgTypeAckFieldId;

    [BoolField("someip.msgtype.tp", "TP Flag", IndexGroup = SomeIpIndexGroup)]
    private FieldId _MsgTypeTpFieldId;

    [U64Field("someip.returncode", "Return Code", IndexGroup = SomeIpIndexGroup)]
    private FieldId _ReturnCodeFieldId;

    #endregion

    #region SOME/IP-TP fields (conditional, when TP flag is set)

    private const string TpIndexGroup = "someip.tp";

    [NoneField("someip.tp", "SOME/IP Transport Protocol", IndexGroup = TpIndexGroup)]
    private FieldId _TpContainerFieldId;

    [U64Field("someip.tp.offset", "Offset", IndexGroup = TpIndexGroup)]
    private FieldId _TpOffsetFieldId;

    [BoolField("someip.tp.more", "More Segments", IndexGroup = TpIndexGroup)]
    private FieldId _TpMoreFieldId;

    [U64Field("someip.tp.reserved", "Reserved", IndexGroup = TpIndexGroup)]
    private FieldId _TpReservedFieldId;

    [StringField("someip.tp.dropped", "TP Reassembly Dropped", IndexGroup = TpIndexGroup)]
    private FieldId _TpDroppedFieldId;

    #endregion

    #region SOME/IP-SD fields (conditional, when message ID == 0xFFFF8100)

    private const string SdIndexGroup = "someipSd";
    private const string SdEntriesIndexGroup = "someipSd.entries";
    private const string SdOptionsIndexGroup = "someipSd.options";

    [NoneField("someip_sd", "SOME/IP-SD", IndexGroup = SdIndexGroup)]
    private FieldId _SdContainerFieldId;

    [U64Field("someip_sd.flags", "Flags", IndexGroup = SdIndexGroup)]
    private FieldId _SdFlagsFieldId;

    [BoolField("someip_sd.flags.reboot", "Reboot", IndexGroup = SdIndexGroup)]
    private FieldId _SdFlagsRebootFieldId;

    [BoolField("someip_sd.flags.unicast", "Unicast", IndexGroup = SdIndexGroup)]
    private FieldId _SdFlagsUnicastFieldId;

    [BoolField("someip_sd.flags.initial_events", "Explicit Initial Data Events Request", IndexGroup = SdIndexGroup)]
    private FieldId _SdFlagsInitialEventsFieldId;

    [NoneField("someip_sd.entries", "Entries Array", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntriesContainerFieldId;

    [NoneField("someip_sd.entry", "Entry", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryContainerFieldId;

    [U64Field("someip_sd.entry.type", "Type", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryTypeFieldId;

    [U64Field("someip_sd.entry.index1", "Index 1st Options", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryIndex1FieldId;

    [U64Field("someip_sd.entry.index2", "Index 2nd Options", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryIndex2FieldId;

    [U64Field("someip_sd.entry.n_opt_1", "Num Options 1", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryNumOpt1FieldId;

    [U64Field("someip_sd.entry.n_opt_2", "Num Options 2", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryNumOpt2FieldId;

    [U64Field("someip_sd.entry.serviceid", "Service ID", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryServiceIdFieldId;

    [U64Field("someip_sd.entry.instanceid", "Instance ID", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryInstanceIdFieldId;

    [U64Field("someip_sd.entry.majorver", "Major Version", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryMajorVerFieldId;

    [U64Field("someip_sd.entry.ttl", "TTL", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryTtlFieldId;

    [U64Field("someip_sd.entry.minorver", "Minor Version", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryMinorVerFieldId;

    [U64Field("someip_sd.entry.eventgroupid", "Eventgroup ID", IndexGroup = SdEntriesIndexGroup)]
    private FieldId _SdEntryEventgroupIdFieldId;

    [NoneField("someip_sd.options", "Options Array", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionsContainerFieldId;

    [NoneField("someip_sd.option", "Option", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionContainerFieldId;

    [U64Field("someip_sd.option.length", "Length", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionLengthFieldId;

    [U64Field("someip_sd.option.type", "Type", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionTypeFieldId;

    [IPv4Field("someip_sd.option.ipv4", "IPv4 Address", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionIpv4FieldId;

    [IPv6Field("someip_sd.option.ipv6", "IPv6 Address", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionIpv6FieldId;

    [U64Field("someip_sd.option.proto", "L4 Protocol", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionProtoFieldId;

    [U64Field("someip_sd.option.port", "Port", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionPortFieldId;

    [StringField("someip_sd.option.config", "Configuration", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionConfigFieldId;

    [U64Field("someip_sd.option.lb_priority", "Priority", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionLbPriorityFieldId;

    [U64Field("someip_sd.option.lb_weight", "Weight", IndexGroup = SdOptionsIndexGroup)]
    private FieldId _SdOptionLbWeightFieldId;

    #endregion

    #region Payload (conditional)

    [BytesField("someip.payload", "Payload", IndexGroup = "someip.payload")]
    private FieldId _PayloadFieldId;

    #endregion

    #region Service/method name fields (conditional, from config)

    private const string SomeIpNameGroup = "someip.name";

    [StringField("someip.service.name", "Service Name", IndexGroup = SomeIpNameGroup)]
    private FieldId _ServiceNameFieldId;

    [StringField("someip.method.name", "Method Name", IndexGroup = SomeIpNameGroup)]
    private FieldId _MethodNameFieldId;

    #endregion

    #region Settings

    [StringSetting("someip.config_file", "Configuration File", "someip", Default = "")]
    private string? _ConfigFile;

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    /// <summary>SOME/IP-TP fragment reassembly tracker (stateful).</summary>
    private readonly SomeIpTpReassembler _TpReassembler = new();

    /// <summary>Cached TP field IDs struct passed to TP parsing.</summary>
    private SomeIpTpFieldIds _TpFieldIds;

    /// <summary>Cached SD field IDs struct passed to SD parsing.</summary>
    private SomeIpSdFieldIds _SdFieldIds;

    /// <summary>Service ID → display name lookup, built from config.</summary>
    private FrozenDictionary<ushort, string> _ServiceNames = FrozenDictionary<ushort, string>.Empty;

    /// <summary>Message ID (serviceId &lt;&lt; 16 | methodId) → method name lookup.</summary>
    private FrozenDictionary<uint, string> _MethodNames = FrozenDictionary<uint, string>.Empty;

    /// <summary>
    /// Warning produced during registration if the config file referenced by
    /// <c>someip.config_file</c> could not be loaded; <see langword="null"/> when loading
    /// succeeded or no path was configured.
    /// </summary>
    public SettingsLoadWarning? ConfigLoadWarning
    {
        get; private set;
    }

    partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _Populator = (in MutField container) => PopulateSomeIpFields(in container);
        _TpReassembler.Clear();

        // Populate TP field IDs struct
        _TpFieldIds = new SomeIpTpFieldIds
        {
            Container = _TpContainerFieldId,
            Offset = _TpOffsetFieldId,
            MoreSegments = _TpMoreFieldId,
            Reserved = _TpReservedFieldId,
        };

        // Populate SD field IDs struct
        _SdFieldIds = new SomeIpSdFieldIds
        {
            Container = _SdContainerFieldId,
            Flags = _SdFlagsFieldId,
            FlagsReboot = _SdFlagsRebootFieldId,
            FlagsUnicast = _SdFlagsUnicastFieldId,
            FlagsInitialEvents = _SdFlagsInitialEventsFieldId,
            EntriesContainer = _SdEntriesContainerFieldId,
            EntryContainer = _SdEntryContainerFieldId,
            EntryType = _SdEntryTypeFieldId,
            EntryIndex1 = _SdEntryIndex1FieldId,
            EntryIndex2 = _SdEntryIndex2FieldId,
            EntryNumOpt1 = _SdEntryNumOpt1FieldId,
            EntryNumOpt2 = _SdEntryNumOpt2FieldId,
            EntryServiceId = _SdEntryServiceIdFieldId,
            EntryInstanceId = _SdEntryInstanceIdFieldId,
            EntryMajorVer = _SdEntryMajorVerFieldId,
            EntryTtl = _SdEntryTtlFieldId,
            EntryMinorVer = _SdEntryMinorVerFieldId,
            EntryEventgroupId = _SdEntryEventgroupIdFieldId,
            OptionsContainer = _SdOptionsContainerFieldId,
            OptionContainer = _SdOptionContainerFieldId,
            OptionLength = _SdOptionLengthFieldId,
            OptionType = _SdOptionTypeFieldId,
            OptionIpv4 = _SdOptionIpv4FieldId,
            OptionIpv6 = _SdOptionIpv6FieldId,
            OptionProto = _SdOptionProtoFieldId,
            OptionPort = _SdOptionPortFieldId,
            OptionConfigString = _SdOptionConfigFieldId,
            OptionLbPriority = _SdOptionLbPriorityFieldId,
            OptionLbWeight = _SdOptionLbWeightFieldId,
        };

        // Load SOME/IP configuration for service/method name resolution
        builder.Settings.TryLoadReferencedJsonConfig(
            "someip.config_file", SomeIpConfigContext.Default.SomeIpConfig,
            out SomeIpConfig? config, out SettingsLoadWarning? configLoadWarning);
        ConfigLoadWarning = configLoadWarning;

        if (config?.Services is { Length: > 0 })
        {
            Dictionary<ushort, string> serviceNames = new(config.Services.Length);
            Dictionary<uint, string> methodNames = new();

            foreach (SomeIpServiceEntry service in config.Services)
            {
                serviceNames.TryAdd(service.ServiceId, service.Name);
                foreach (SomeIpMethodEntry method in service.Methods)
                {
                    uint msgId = ((uint)service.ServiceId << 16) | method.MethodId;
                    methodNames.TryAdd(msgId, method.Name);
                }
            }

            _ServiceNames = serviceNames.ToFrozenDictionary();
            _MethodNames = methodNames.ToFrozenDictionary();
        }
        else
        {
            _ServiceNames = FrozenDictionary<ushort, string>.Empty;
            _MethodNames = FrozenDictionary<uint, string>.Empty;
        }
    }

    /// <summary>
    /// Parses a SOME/IP message. Uses lazy population for field details.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < SomeIpHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, SomeIpHeader.Size, (ulong)data.Length);
        }

        if (!SomeIpHeader.TryParse(data.Span, out SomeIpHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, SomeIpHeader.Size, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_SomeipGroupId);

        // SOME/IP spec: Length counts from ClientId onwards
        // (ClientId[2] + SessionId[2] + ProtocolVersion[1] + InterfaceVersion[1] + MessageType[1] + ReturnCode[1] = 8 bytes minimum, with no payload).
        // Values below 8 are structurally invalid.
        if (header.Length < 8)
        {
            return ParseError.InvalidData(ProtocolName, "SOME/IP length field below minimum (8)");
        }

        // Guard against integer overflow when adding the 8-byte fixed prefix to produce the declared total.
        // header.Length is uint; if it exceeds int.MaxValue - 8 the addition overflows when cast to int.
        // In that case the declared size exceeds any representable buffer, so clamp directly to available data.
        int totalLen = header.Length > (uint)(int.MaxValue - 8)
            ? data.Length
            : Math.Min(8 + (int)header.Length, data.Length);

        // Determine TP flag and SD message to record index presence eagerly
        bool isTp = (header.MessageType & 0x20) != 0;
        bool isSd = header.MessageId == SomeIpSdParser.SdMessageId;

        if (totalLen > SomeIpHeader.Size && !isSd)
        {
            context.RecordGroupPresence(_SomeipPayloadGroupId);
        }

        if (isTp)
        {
            context.RecordGroupPresence(_SomeipTpGroupId);
        }

        if (isSd)
        {
            context.RecordGroupPresence(_SomeipSdGroupId);
            context.RecordGroupPresence(_SomeipSdEntriesGroupId);
            context.RecordGroupPresence(_SomeipSdOptionsGroupId);
        }

        // Build summary
        string msgTypeText = SomeIpDisplayTables.GetMsgTypeDisplayText(header.MessageType);
        LazyString summary = ZA.Lazy(
            "SOME/IP, Service: 0x",
            Helpers.DisplayTables.FormatHexU16(header.ServiceId),
            ", Method: 0x",
            Helpers.DisplayTables.FormatHexU16(header.MethodId),
            ", ", msgTypeText);

        parentField.SetPacketInfo(ZA.Lazy(
            "SOME/IP 0x",
            Helpers.DisplayTables.FormatHexU16(header.ServiceId),
            " ", msgTypeText));

        FieldValue containerValue = FieldValue.NewBytes(data[..totalLen]);
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return totalLen;
    }

    /// <summary>
    /// Populates all SOME/IP fields from stored packet bytes.
    /// </summary>
    private ParseResult PopulateSomeIpFields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> someipData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (!SomeIpHeader.TryParse(someipData.Span, out SomeIpHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, SomeIpHeader.Size, (ulong)someipData.Length);
        }

        // Message ID
        LazyString msgIdText = ZA.Lazy("0x", new Hex8(header.MessageId));
        container.AppendWithCustomText(_MessageIdFieldId,
            FieldValue.NewU64(header.MessageId), msgIdText, in context);

        // Service/Method IDs
        container.AppendWithCustomText(_ServiceIdFieldId,
            FieldValue.NewU64(header.ServiceId),
            Helpers.DisplayTables.FormatHexU16(header.ServiceId), in context);
        container.AppendWithCustomText(_MethodIdFieldId,
            FieldValue.NewU64(header.MethodId),
            Helpers.DisplayTables.FormatHexU16(header.MethodId), in context);

        // Service/Method names from configuration
        if (_ServiceNames.TryGetValue(header.ServiceId, out string? serviceName))
        {
            context.RecordGroupPresence(_SomeipNameGroupId);
            container.Append(_ServiceNameFieldId, FieldValue.NewString(serviceName), in context);
            if (_MethodNames.TryGetValue(header.MessageId, out string? methodName))
            {
                container.Append(_MethodNameFieldId, FieldValue.NewString(methodName), in context);
            }
        }

        // Length
        container.Append(_LengthFieldId, FieldValue.NewU64(header.Length), in context);

        // Client/Session IDs
        container.AppendWithCustomText(_ClientIdFieldId,
            FieldValue.NewU64(header.ClientId),
            Helpers.DisplayTables.FormatHexU16(header.ClientId), in context);
        container.AppendWithCustomText(_SessionIdFieldId,
            FieldValue.NewU64(header.SessionId),
            Helpers.DisplayTables.FormatHexU16(header.SessionId), in context);

        // Version fields
        container.Append(_VersionFieldId, FieldValue.NewU64(header.ProtocolVersion), in context);
        container.Append(_IfVersionFieldId, FieldValue.NewU64(header.InterfaceVersion), in context);

        // Message type with display text
        container.AppendWithCustomText(_MsgTypeFieldId,
            FieldValue.NewU64(header.MessageType),
            SomeIpDisplayTables.GetMsgTypeDisplayText(header.MessageType), in context);

        // Decompose message type into ACK and TP flags
        bool isAck = (header.MessageType & 0x40) != 0;
        bool isTp = (header.MessageType & 0x20) != 0;
        container.Append(_MsgTypeAckFieldId, FieldValue.NewBool(isAck), in context);
        container.Append(_MsgTypeTpFieldId, FieldValue.NewBool(isTp), in context);

        // Return code
        container.AppendWithCustomText(_ReturnCodeFieldId,
            FieldValue.NewU64(header.ReturnCode),
            SomeIpDisplayTables.GetReturnCodeDisplayText(header.ReturnCode), in context);

        // Determine payload region (after the 16-byte SOME/IP header)
        int payloadStart = SomeIpHeader.Size;

        // ── SOME/IP-TP header (4 bytes after SOME/IP header when TP flag set) ──
        // tpHandled tracks whether TP reassembly took ownership of the payload.
        // When true, the normal tail-payload dispatch below is skipped.
        bool tpHandled = false;

        if (isTp && someipData.Length >= payloadStart + SomeIpTpHeader.Size)
        {
            context.RecordGroupPresence(_SomeipTpGroupId);

            if (SomeIpTpHeader.TryParse(someipData.Span[payloadStart..], out SomeIpTpHeader tpHeader))
            {
                // Build TP summary text
                string moreSuffix = tpHeader.MoreSegments ? ", More Segments" : ", Last Segment";
                LazyString tpSummary = ZA.Lazy(
                    "SOME/IP-TP, Offset: ", tpHeader.ByteOffset, " bytes", moreSuffix);

                MutField tpField = container.AppendWithCustomText(
                    _TpContainerFieldId, FieldValue.None, tpSummary, in context);

                // TP offset expressed in bytes
                tpField.AppendWithCustomText(_TpOffsetFieldId,
                    FieldValue.NewU64(tpHeader.ByteOffset),
                    (string)ZA.String(tpHeader.ByteOffset, " bytes"), in context);
                tpField.Append(_TpMoreFieldId, FieldValue.NewBool(tpHeader.MoreSegments), in context);
                tpField.AppendWithCustomText(_TpReservedFieldId,
                    FieldValue.NewU64(tpHeader.Reserved),
                    Helpers.DisplayTables.FormatHexU8(tpHeader.Reserved), in context);

                // Wire the TP fragment into the reassembler.
                // The fragment payload follows immediately after the 4-byte TP header.
                ReadOnlySpan<byte> fragmentPayload = someipData.Span[(payloadStart + SomeIpTpHeader.Size)..];
                SomeIpTpReassemblyKey tpKey = new(header.ServiceId, header.MethodId, header.ClientId, header.SessionId);
                SomeIpTpReassemblyResult tpResult = _TpReassembler.AddSegment(in tpKey, tpHeader.ByteOffset, fragmentPayload, tpHeader.MoreSegments);

                if (tpResult.Outcome == SomeIpTpOutcome.Complete && tpResult.Payload is not null)
                {
                    // All TP segments received — dispatch the reassembled payload to any
                    // registered sub-protocol, or fall back to raw bytes.
                    ReadOnlyMemory<byte> reassembledMemory = tpResult.Payload;
                    if (header.MessageId != SomeIpSdParser.SdMessageId)
                    {
                        ParseResult dispatchResult = container.TryCallNextProtocolU64(
                            _MessageIdTableId, header.MessageId, reassembledMemory, in context);
                        if (dispatchResult.IsError)
                        {
                            return dispatchResult;
                        }
                        if (!dispatchResult.IsSuccess || dispatchResult.Value == 0)
                        {
                            container.Append(_PayloadFieldId, FieldValue.NewBytes(reassembledMemory), in context);
                        }
                    }
                }
                else if (tpResult.Outcome == SomeIpTpOutcome.Dropped)
                {
                    // Session was evicted (cap hit or size overflow) — surface a diagnostic so
                    // callers can detect the failure rather than silently receiving no output.
                    tpField.Append(_TpDroppedFieldId,
                        FieldValue.NewString("Reassembly session dropped (cap or size limit exceeded)"), in context);
                }

                // When an LRU eviction displaced an older session to make room for this one,
                // always emit a diagnostic regardless of this session's own outcome, so callers
                // can detect the silent loss of the evicted session.
                if (tpResult.LruEvicted)
                {
                    tpField.Append(_TpDroppedFieldId,
                        FieldValue.NewString("Reassembly session limit reached; an older session was evicted"), in context);
                }

                // Outcome == InProgress: more fragments are expected; nothing to emit yet.

                // TP reassembly has taken ownership of this packet's payload.
                tpHandled = true;
            }

            // Advance past the TP header for the actual payload
            payloadStart += SomeIpTpHeader.Size;
        }

        // ── SOME/IP-SD (Service Discovery, message ID = 0xFFFF8100) ──
        if (header.MessageId == SomeIpSdParser.SdMessageId && someipData.Length > SomeIpHeader.Size)
        {
            context.RecordGroupPresence(_SomeipSdGroupId);
            context.RecordGroupPresence(_SomeipSdEntriesGroupId);
            context.RecordGroupPresence(_SomeipSdOptionsGroupId);

            ReadOnlySpan<byte> sdPayload = someipData.Span[SomeIpHeader.Size..];
            ParseResult sdResult = SomeIpSdParser.Parse(in container, sdPayload, in _SdFieldIds, in context);
            if (sdResult.IsError)
            {
                return sdResult;
            }
        }

        // Payload (remaining data after SOME/IP header and optional TP header)
        // Skip payload for SD messages — they have been fully parsed above.
        // Skip for TP messages — the fragment was handed to the reassembler above;
        // the dispatch happens when the last segment arrives (tpHandled == true).
        if (!tpHandled && header.MessageId != SomeIpSdParser.SdMessageId && someipData.Length > payloadStart)
        {
            ReadOnlyMemory<byte> payloadData = someipData[payloadStart..];

            // Try to dispatch payload to a sub-protocol registered on someip.messageid
            ParseResult dispatchResult = container.TryCallNextProtocolU64(
                _MessageIdTableId, header.MessageId, payloadData, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }

            // If no sub-protocol consumed the payload, append raw payload bytes
            if (!dispatchResult.IsSuccess || dispatchResult.Value == 0)
            {
                container.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData), in context);
            }
        }

        return 0;
    }
    #endregion
}
