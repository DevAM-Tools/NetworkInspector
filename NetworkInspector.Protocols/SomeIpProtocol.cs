// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// SOME/IP (Scalable service-Oriented MiddlewarE over IP) protocol parser.
/// Automotive middleware protocol (AUTOSAR) for service-oriented communication.
/// <para><b>Thread safety:</b> Ordered single-threaded first parse per packet id; later parses of
/// an already-parsed id replay recorded state lock-free. See remarks for details.</para>
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
/// ├── someip.service.name / someip.method.name                 [only when the service is in the config file]
/// ├── someip.tp: SOME/IP-TP, Offset: 0 bytes, More Segments  [only when TP flag set]
/// │   ├── someip.tp.offset: 0 bytes
/// │   ├── someip.tp.more: true
/// │   ├── someip.tp.reserved: 0x0
/// │   └── someip.tp.dropped                                    [only when reassembly dropped or evicted a session]
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
/// └── someip.payload: (N bytes)                                [unless TP, SD, or a sub-protocol consumed it]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The first parse of a packet id feeds the SOME/IP-TP segment
/// reassembler and must therefore stay ordered and single-threaded (the session guarantees this
/// under its parse lock). Every later parse of an already-parsed id is a replay: it reads the
/// recorded reassembly outcome from the protocol-owned effect store instead of touching the
/// reassembler, and is safe to run on any number of threads concurrently. The watermark
/// <see cref="_IngestWatermark"/> separates both paths without any caller-supplied mode
/// flag.</para>
/// <para><b>Why stateful:</b> a SOME/IP-TP message spans several segments, so the reassembler (see
/// <c>SomeIpTpReassembler</c>) must see every segment exactly once and in capture order. A re-parse (UI, filter,
/// export) of an earlier packet therefore must not feed it again: it reads the result recorded by the first parse
/// (<see cref="EffectStore{TEffect}"/>, keyed by packet id and layer) and rebuilds the same field
/// tree from it.</para>
/// <para><b>Eager field tree:</b> unlike most protocols this one builds its fields during <see cref="Parse"/> instead of
/// through a lazy populator; the reason is documented at the construction site in <c>_ParseBody</c>.</para>
/// </remarks>
[Protocol("someip", "SOME/IP", Description = "SOME/IP (AUTOSAR)")]
[RegisterAtTable(UdpProtocol.PortTableName, UdpPortKey)]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class SomeIpProtocol : IProtocol
{
    #region Constants

    /// <summary>
    /// Default UDP port for SOME/IP (30490, the port used for SOME/IP Service Discovery). Services announced by SD
    /// normally use other ports, which have to be bound to this protocol separately.
    /// </summary>
    public const ulong UdpPortKey = 30490;

    /// <summary>Default TCP port for SOME/IP (30490, same well-known port as <see cref="UdpPortKey"/>).</summary>
    public const ulong TcpPortKey = 30490;

    /// <summary>
    /// Protocol table name for SOME/IP message ID dispatch. Keys are the 32-bit message id
    /// (<c>ServiceId &lt;&lt; 16 | MethodId</c>); payload deserializers register here to receive the SOME/IP payload.
    /// </summary>
    public const string MessageIdTableName = "someip.messageid";

    /// <summary>Index group for always-present SOME/IP fields.</summary>
    private const string _SomeIpIndexGroup = "someip";

    /// <summary>
    /// Error text for a reassembled TP message that cannot be handed on because the packet's table of bound buffers is
    /// full. Limit: 255 additional buffers per packet (a constant of the packet's effect keys, not configurable);
    /// secure by default. Exceeding it is an expected failure that is named, not a silent drop.
    /// </summary>
    private const string _BoundBufferLimitMessage =
        "Reassembled SOME/IP-TP message cannot be parsed further: a packet can hold at most 255 additional buffers";

    #endregion

    #region Protocol container

    [BytesField("someip", "SOME/IP", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch table — dispatches by Message ID to payload deserializers

    [ProtocolTableU64(MessageIdTableName, "SOME/IP Message ID")]
    private ProtocolTableId _MessageIdTableId;

    #endregion

    #region Header fields (always present)

    [U64Field("someip.messageid", "Message ID", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _MessageIdFieldId;

    [U64Field("someip.serviceid", "Service ID", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _ServiceIdFieldId;

    [U64Field("someip.methodid", "Method ID", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _MethodIdFieldId;

    [U64Field("someip.length", "Length", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _LengthFieldId;

    [U64Field("someip.clientid", "Client ID", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _ClientIdFieldId;

    [U64Field("someip.sessionid", "Session ID", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _SessionIdFieldId;

    [U64Field("someip.version", "Protocol Version", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("someip.ifversion", "Interface Version", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _IfVersionFieldId;

    [U64Field("someip.msgtype", "Message Type", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _MsgTypeFieldId;

    #endregion

    #region Message type sub-fields (ACK and TP flag decomposition)

    [BoolField("someip.msgtype.ack", "ACK Flag", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _MsgTypeAckFieldId;

    [BoolField("someip.msgtype.tp", "TP Flag", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _MsgTypeTpFieldId;

    [U64Field("someip.returncode", "Return Code", IndexGroup = _SomeIpIndexGroup)]
    private FieldId _ReturnCodeFieldId;

    #endregion

    #region SOME/IP-TP fields (conditional, when TP flag is set)

    private const string _TpIndexGroup = "someip.tp";

    [NoneField("someip.tp", "SOME/IP Transport Protocol", IndexGroup = _TpIndexGroup)]
    private FieldId _TpContainerFieldId;

    [U64Field("someip.tp.offset", "Offset", IndexGroup = _TpIndexGroup)]
    private FieldId _TpOffsetFieldId;

    [BoolField("someip.tp.more", "More Segments", IndexGroup = _TpIndexGroup)]
    private FieldId _TpMoreFieldId;

    [U64Field("someip.tp.reserved", "Reserved", IndexGroup = _TpIndexGroup)]
    private FieldId _TpReservedFieldId;

    [StringField("someip.tp.dropped", "TP Reassembly Dropped", IndexGroup = _TpIndexGroup)]
    private FieldId _TpDroppedFieldId;

    #endregion

    #region SOME/IP-SD fields (conditional, when message ID == 0xFFFF8100)

    private const string _SdIndexGroup = "someipSd";
    private const string _SdEntriesIndexGroup = "someipSd.entries";
    private const string _SdOptionsIndexGroup = "someipSd.options";

    [NoneField("someip_sd", "SOME/IP-SD", IndexGroup = _SdIndexGroup)]
    private FieldId _SdContainerFieldId;

    [U64Field("someip_sd.flags", "Flags", IndexGroup = _SdIndexGroup)]
    private FieldId _SdFlagsFieldId;

    [BoolField("someip_sd.flags.reboot", "Reboot", IndexGroup = _SdIndexGroup)]
    private FieldId _SdFlagsRebootFieldId;

    [BoolField("someip_sd.flags.unicast", "Unicast", IndexGroup = _SdIndexGroup)]
    private FieldId _SdFlagsUnicastFieldId;

    [BoolField("someip_sd.flags.initial_events", "Explicit Initial Data Events Request", IndexGroup = _SdIndexGroup)]
    private FieldId _SdFlagsInitialEventsFieldId;

    [NoneField("someip_sd.entries", "Entries Array", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntriesContainerFieldId;

    [NoneField("someip_sd.entry", "Entry", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryContainerFieldId;

    [U64Field("someip_sd.entry.type", "Type", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryTypeFieldId;

    [U64Field("someip_sd.entry.index1", "Index 1st Options", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryIndex1FieldId;

    [U64Field("someip_sd.entry.index2", "Index 2nd Options", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryIndex2FieldId;

    [U64Field("someip_sd.entry.n_opt_1", "Num Options 1", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryNumOpt1FieldId;

    [U64Field("someip_sd.entry.n_opt_2", "Num Options 2", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryNumOpt2FieldId;

    [U64Field("someip_sd.entry.serviceid", "Service ID", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryServiceIdFieldId;

    [U64Field("someip_sd.entry.instanceid", "Instance ID", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryInstanceIdFieldId;

    [U64Field("someip_sd.entry.majorver", "Major Version", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryMajorVerFieldId;

    [U64Field("someip_sd.entry.ttl", "TTL", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryTtlFieldId;

    [U64Field("someip_sd.entry.minorver", "Minor Version", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryMinorVerFieldId;

    [U64Field("someip_sd.entry.eventgroupid", "Eventgroup ID", IndexGroup = _SdEntriesIndexGroup)]
    private FieldId _SdEntryEventgroupIdFieldId;

    [NoneField("someip_sd.options", "Options Array", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionsContainerFieldId;

    [NoneField("someip_sd.option", "Option", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionContainerFieldId;

    [U64Field("someip_sd.option.length", "Length", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionLengthFieldId;

    [U64Field("someip_sd.option.type", "Type", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionTypeFieldId;

    [IPv4Field("someip_sd.option.ipv4", "IPv4 Address", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionIpv4FieldId;

    [IPv6Field("someip_sd.option.ipv6", "IPv6 Address", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionIpv6FieldId;

    [U64Field("someip_sd.option.proto", "L4 Protocol", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionProtoFieldId;

    [U64Field("someip_sd.option.port", "Port", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionPortFieldId;

    [StringField("someip_sd.option.config", "Configuration", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionConfigFieldId;

    [U64Field("someip_sd.option.lb_priority", "Priority", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionLbPriorityFieldId;

    [U64Field("someip_sd.option.lb_weight", "Weight", IndexGroup = _SdOptionsIndexGroup)]
    private FieldId _SdOptionLbWeightFieldId;

    #endregion

    #region Payload (conditional)

    [BytesField("someip.payload", "Payload", IndexGroup = "someip.payload")]
    private FieldId _PayloadFieldId;

    #endregion

    #region Service/method name fields (conditional, from config)

    private const string _SomeIpNameGroup = "someip.name";

    [StringField("someip.service.name", "Service Name", IndexGroup = _SomeIpNameGroup)]
    private FieldId _ServiceNameFieldId;

    [StringField("someip.method.name", "Method Name", IndexGroup = _SomeIpNameGroup)]
    private FieldId _MethodNameFieldId;

    #endregion

    #region Settings

    [StringSetting("someip.config_file", "Configuration File", "someip", Default = "")]
    private string? _ConfigFile;

    /// <summary>
    /// SOME/IP-TP fragment reassembly tracker (stateful). Not thread-safe: touched only by the ordered first parse of
    /// a packet, never by a replay (see <see cref="_IngestWatermark"/>).
    /// </summary>
    private readonly SomeIpTpReassembler _TpReassembler = new();

    /// <summary>Cached SD field IDs struct passed to SD parsing.</summary>
    private SomeIpSdFieldIds _SdFieldIds;

    /// <summary>
    /// Service ID → display name lookup, built from config. Built once at registration and read-only afterwards,
    /// so concurrent readers (replays on several threads) need no lock; a frozen dictionary is optimized for
    /// lookups at the cost of a slower build, which is the right trade for a table built once and read per packet.
    /// </summary>
    private FrozenDictionary<ushort, string> _ServiceNames = FrozenDictionary<ushort, string>.Empty;

    /// <summary>
    /// Message ID (serviceId &lt;&lt; 16 | methodId) → method name lookup. Same build-once, lock-free-read design
    /// as <see cref="_ServiceNames"/>.
    /// </summary>
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

    /// <summary>
    /// Registration hook (called by the generated registration code after the attribute fields exist): resets the
    /// reassembler, snapshots the field ids into the grouped structs, and loads the optional name configuration.
    /// Runs once during the single-threaded stack build.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        // Start every stack build with empty reassembly state.
        _TpReassembler.Clear();

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

        // Load SOME/IP configuration for service/method name resolution.
        // The return value is not needed: on failure config is null and the warning is exposed through
        // ConfigLoadWarning, so a missing or broken file leaves names unresolved but never blocks the protocol.
        builder.Settings.TryLoadReferencedJsonConfig(
            "someip.config_file", SomeIpConfigContext.Default.SomeIpConfig,
            out SomeIpConfig? config, out SettingsLoadWarning? configLoadWarning);
        ConfigLoadWarning = configLoadWarning;

        if (config?.Services is { Length: > 0 })
        {
            // Build mutable dictionaries first, then freeze them once. TryAdd keeps the first definition when a
            // service or message id appears twice in the file. The method key is the message id, so a method name
            // is only found for the exact (service, method) pair.
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
    /// Parses a SOME/IP message.
    /// <para>
    /// SOME/IP-TP messages are stateful: the first parse of a packet id feeds its segment into the
    /// shared reassembler and records the result, every later parse of that id replays the recorded
    /// result and leaves the reassembly sessions untouched. See <see cref="_IsReplay"/>.
    /// </para>
    /// </summary>
    /// <param name="parentField">Field that receives the SOME/IP container.</param>
    /// <param name="data">Message bytes; must be a slice of the packet's frame or bound buffers (needed for the layer key).</param>
    /// <param name="context">Parse context used to dispatch the payload and record index presence.</param>
    /// <returns>
    /// The bytes covered by the declared SOME/IP length (clamped to the available data), or a <see cref="ParseError"/>.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The layer key identifies this invocation inside the packet (buffer index plus offset), so the effect recorded
        // by the first parse can be found again on replay. A slice that is not part of the packet's buffers cannot be
        // keyed, so it is rejected instead of risking a replay of the wrong layer.
        Packet packet = parentField.Packet;
        if (!parentField.TryGetEffectLayerKey(data, out int layerKey))
        {
            return ParseError.Custom(ProtocolName, "Parse data is not a slice of this packet's buffers.");
        }

        // First parse versus replay: decided only by the watermark of already ingested packet ids, so no caller
        // flag is needed. Only the first parse may touch the reassembler (single ordered writer).
        // The watermark is raised only when the OUTERMOST first parse of this packet ends: a nested SOME/IP parse
        // of the same packet (for example inside a reassembled payload) must still be treated as a first parse, and
        // it would be mistaken for a replay if an inner parse raised the watermark to this packet id early.
        bool isReplay = _IsReplay(packet.Id);
        bool raiseWatermark = !isReplay && _ParseNesting == 0;
        if (!isReplay)
        {
            _ParseNesting++;
        }

        // try/finally so the nesting counter and the watermark stay consistent even when the body returns an error
        // or throws; a first parse that failed still counts as "ingested" (its effects, if any, were recorded).
        try
        {
            return _ParseBody(in parentField, data, in context, layerKey, isReplay);
        }
        finally
        {
            if (!isReplay)
            {
                _ParseNesting--;
                if (raiseWatermark)
                {
                    _RaiseWatermark(packet.Id);
                }
            }
        }
    }

    /// <summary>
    /// Parse body. <paramref name="layerKey"/> is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call and
    /// <paramref name="isReplay"/> selects replay of the recorded TP result over feeding the
    /// reassembler.
    /// </summary>
    private ParseResult _ParseBody(
        in MutField parentField,
        ReadOnlyMemory<byte> data,
        in ParseContext context,
        int layerKey,
        bool isReplay)
    {
        // Fixed 16-byte header (the explicit length check and TryParse's own check both guard the same bound).
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
        // The declared length is also clamped to what was captured (a truncated capture or a TCP segment that ends
        // mid-message is shown as far as it goes); a shorter declared length leaves the rest of the data unconsumed.
        int totalLen = header.Length > (uint)(int.MaxValue - 8)
            ? data.Length
            : Math.Min(8 + (int)header.Length, data.Length);

        // Determine TP flag and SD message to record index presence eagerly. Presence is recorded from the header alone,
        // before the field tree is built, so the index sees these groups even when a later step reports an error.
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

        // The SD groups follow what the SD parser will emit for these bytes (same length rules, see DetectGroups): a message
        // without payload, without options, or with a length that points past the payload has no such fields, and
        // recording the groups from the message ID alone would announce fields that do not exist.
        if (isSd)
        {
            ReadOnlySpan<byte> sdPayload = totalLen > SomeIpHeader.Size
                ? data.Span[SomeIpHeader.Size..totalLen]
                : default;
            SomeIpSdParser.DetectGroups(sdPayload, out bool hasSdContainer, out bool hasSdEntries, out bool hasSdOptions);
            if (hasSdContainer)
            {
                context.RecordGroupPresence(_SomeipSdGroupId);
            }

            if (hasSdEntries)
            {
                context.RecordGroupPresence(_SomeipSdEntriesGroupId);
            }

            if (hasSdOptions)
            {
                context.RecordGroupPresence(_SomeipSdOptionsGroupId);
            }
        }

        // Build summary: the packet-list text and the container summary both use the cached display strings
        // (table lookups) so no number formatting happens here.
        string msgTypeText = SomeIpDisplayTables.GetMsgTypeDisplayText(header.MessageType);

        parentField.SetPacketInfo(ZA.Lazy(
            "SOME/IP 0x",
            Helpers.DisplayTables.FormatHexU16(header.ServiceId),
            " ", msgTypeText));

        FieldValue containerValue = FieldValue.NewBytes(data[..totalLen]);

        // SOME/IP builds its field tree eagerly rather than via a lazy populator: TP
        // reassembly is stateful and must run exactly once, and the reassembled payload
        // must be dispatched to sub-protocols through the real ParseContext so that
        // sub-protocol presence is recorded in the index. A lazy populator receives no
        // ParseContext and therefore cannot dispatch, so eager construction is required.
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, "SOME/IP, Service: 0x",
            Helpers.DisplayTables.FormatHexU16(header.ServiceId),
            ", Method: 0x",
            Helpers.DisplayTables.FormatHexU16(header.MethodId),
            ", ", msgTypeText);
        ParseResult buildResult = _BuildSomeIpFields(in container, in context, layerKey, isReplay);
        if (buildResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        return totalLen;
    }

    /// <summary>
    /// Builds all SOME/IP fields from the container's stored bytes, performs SOME/IP-TP
    /// reassembly, and dispatches any reassembled or tail payload to registered
    /// sub-protocols using the supplied <paramref name="context"/>.
    /// <paramref name="layerKey"/> and <paramref name="isReplay"/> come from <see cref="IProtocol.Parse"/>
    /// and decide whether the TP segment feeds the reassembler or replays a recorded result.
    /// </summary>
    private ParseResult _BuildSomeIpFields(
        in MutField container, in ParseContext context, int layerKey, bool isReplay)
    {
        // The header is decoded again from the bytes stored on the container (rather than passed in), so this method
        // depends only on the container; the header decode is a handful of span reads.
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> someipData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (!SomeIpHeader.TryParse(someipData.Span, out SomeIpHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, SomeIpHeader.Size, (ulong)someipData.Length);
        }

        // Message ID
        container.AppendWithCustomText(_MessageIdFieldId,
            FieldValue.NewU64(header.MessageId), "0x", new Hex8(header.MessageId));

        // Service/Method IDs
        container.AppendWithCustomText(_ServiceIdFieldId,
            FieldValue.NewU64(header.ServiceId),
            Helpers.DisplayTables.FormatHexU16(header.ServiceId));
        container.AppendWithCustomText(_MethodIdFieldId,
            FieldValue.NewU64(header.MethodId),
            Helpers.DisplayTables.FormatHexU16(header.MethodId));

        // Service/Method names from configuration. The method name is looked up only for known services: the
        // name fields (and their index group presence) exist only when the config knows the service.
        if (_ServiceNames.TryGetValue(header.ServiceId, out string? serviceName))
        {
            context.RecordGroupPresence(_SomeipNameGroupId);
            container.Append(_ServiceNameFieldId, FieldValue.NewString(serviceName));
            if (_MethodNames.TryGetValue(header.MessageId, out string? methodName))
            {
                container.Append(_MethodNameFieldId, FieldValue.NewString(methodName));
            }
        }

        // Length
        container.Append(_LengthFieldId, FieldValue.NewU64(header.Length));

        // Client/Session IDs
        container.AppendWithCustomText(_ClientIdFieldId,
            FieldValue.NewU64(header.ClientId),
            Helpers.DisplayTables.FormatHexU16(header.ClientId));
        container.AppendWithCustomText(_SessionIdFieldId,
            FieldValue.NewU64(header.SessionId),
            Helpers.DisplayTables.FormatHexU16(header.SessionId));

        // Version fields
        container.Append(_VersionFieldId, FieldValue.NewU64(header.ProtocolVersion));
        container.Append(_IfVersionFieldId, FieldValue.NewU64(header.InterfaceVersion));

        // Message type with display text
        container.AppendWithCustomText(_MsgTypeFieldId,
            FieldValue.NewU64(header.MessageType),
            SomeIpDisplayTables.GetMsgTypeDisplayText(header.MessageType));

        // Decompose message type into ACK (bit 6, 0x40) and TP (bit 5, 0x20) flags
        bool isAck = (header.MessageType & 0x40) != 0;
        bool isTp = (header.MessageType & 0x20) != 0;
        container.Append(_MsgTypeAckFieldId, FieldValue.NewBool(isAck));
        container.Append(_MsgTypeTpFieldId, FieldValue.NewBool(isTp));

        // Return code
        container.AppendWithCustomText(_ReturnCodeFieldId,
            FieldValue.NewU64(header.ReturnCode),
            SomeIpDisplayTables.GetReturnCodeDisplayText(header.ReturnCode));

        // Determine payload region (after the 16-byte SOME/IP header)
        int payloadStart = SomeIpHeader.Size;

        // ── SOME/IP-TP header (4 bytes after SOME/IP header when TP flag set) ──
        // tpHandled tracks whether TP reassembly took ownership of the payload.
        // When true, the normal tail-payload dispatch below is skipped.
        bool tpHandled = false;

        // A TP-flagged message too short to hold the 4-byte TP header skips this block entirely and its remaining
        // bytes are treated as an ordinary payload below.
        if (isTp && someipData.Length >= payloadStart + SomeIpTpHeader.Size)
        {
            if (SomeIpTpHeader.TryParse(someipData.Span[payloadStart..], out SomeIpTpHeader tpHeader))
            {
                // Build TP summary text
                string moreSuffix = tpHeader.MoreSegments ? ", More Segments" : ", Last Segment";


                MutField tpField = container.AppendWithCustomText(
                    _TpContainerFieldId, FieldValue.None, "SOME/IP-TP, Offset: ", tpHeader.ByteOffset, " bytes", moreSuffix);

                // TP offset expressed in bytes
                tpField.AppendWithCustomText(_TpOffsetFieldId,
                    FieldValue.NewU64(tpHeader.ByteOffset),
                    (string)ZA.String(tpHeader.ByteOffset, " bytes"));
                tpField.Append(_TpMoreFieldId, FieldValue.NewBool(tpHeader.MoreSegments));
                tpField.AppendWithCustomText(_TpReservedFieldId,
                    FieldValue.NewU64(tpHeader.Reserved),
                    Helpers.DisplayTables.FormatHexU8(tpHeader.Reserved));

                // Reassembly: first parse feeds the shared reassembler and records the outcome; replay only reads
                // that recorded outcome. Both paths yield the same SomeIpTpReassemblyResult, so everything below
                // (payload dispatch, diagnostics) is identical and the replayed tree equals the first-parse tree.
                SomeIpTpReassemblyResult tpResult;
                if (isReplay)
                {
                    // Replay: never touch the shared reassembly sessions from what may be a reader
                    // thread. A missing effect means the first parse never reached the reassembler,
                    // so the segment is reported without reassembly. The bool result is intentionally
                    // not used: on a miss tpResult is the default (InProgress, no payload).
                    _TryGetTpEffect(container.Packet.Id, layerKey, out tpResult);
                }
                else
                {
                    // Wire the TP fragment into the reassembler.
                    // The fragment payload follows immediately after the 4-byte TP header.
                    // AddSegment copies the bytes (the span points into the frame buffer), and the result is recorded
                    // straight away so a later replay finds it; the effect store requires non-decreasing packet ids,
                    // which the ordered first parse guarantees.
                    ReadOnlySpan<byte> fragmentPayload = someipData.Span[(payloadStart + SomeIpTpHeader.Size)..];
                    SomeIpTpReassemblyKey tpKey = new(header.ServiceId, header.MethodId, header.ClientId, header.SessionId);
                    tpResult = _TpReassembler.AddSegment(
                        in tpKey, tpHeader.ByteOffset, fragmentPayload, tpHeader.MoreSegments);
                    _RecordTpEffect(container.Packet.Id, layerKey, in tpResult);
                }

                if (tpResult.Outcome == SomeIpTpOutcome.Complete && tpResult.Payload is not null)
                {
                    // All TP segments received — dispatch the reassembled payload to any
                    // registered sub-protocol, or fall back to raw bytes. The reassembled array is not a slice of
                    // the frame, so it is registered as an additional packet buffer first: nested parsers need a
                    // buffer the packet knows about to compute their layer key (BindParseBuffer returns an empty
                    // slice if the packet cannot take another buffer). The same array is replayed from the effect store.
                    ReadOnlyMemory<byte> reassembledMemory = container.BindParseBuffer(tpResult.Payload);
                    if (reassembledMemory.IsEmpty && tpResult.Payload.Length > 0)
                    {
                        // The packet holds at most 255 bound buffers. An empty slice would reach the sub-protocol as "no
                        // data" (or be attached as an empty someip.payload) without any sign of why, so the limit is named.
                        // A reassembled payload that is really empty binds to an empty slice and is not an error.
                        return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
                    }

                    if (header.MessageId != SomeIpSdParser.SdMessageId)
                    {
                        ParseResult dispatchResult = container.TryCallNextProtocolU64(
                            _MessageIdTableId, header.MessageId, reassembledMemory, in context);
                        if (dispatchResult.TryPropagateError(out ParseResult tpDispatchError))
                        {
                            return tpDispatchError;
                        }
                        if (!dispatchResult.TryGetConsumed(out int tpConsumed) || tpConsumed == 0)
                        {
                            container.Append(_PayloadFieldId, FieldValue.NewBytes(reassembledMemory));
                        }
                    }
                }
                else if (tpResult.Outcome == SomeIpTpOutcome.Dropped)
                {
                    // This segment's session was dropped (size limit exceeded, a malformed segment, or a segment beyond the
                    // end of the message). Reaching the session cap does not drop anything: the oldest session is evicted
                    // (see LruEvicted below). A diagnostic is surfaced so callers can detect the failure rather than
                    // silently receiving no output.
                    tpField.Append(_TpDroppedFieldId,
                        FieldValue.NewString("Reassembly session dropped (malformed segment or size limit exceeded)"));
                }

                // When an LRU eviction displaced an older session to make room for this one,
                // always emit a diagnostic regardless of this session's own outcome, so callers
                // can detect the silent loss of the evicted session.
                if (tpResult.LruEvicted)
                {
                    tpField.Append(_TpDroppedFieldId,
                        FieldValue.NewString("Reassembly session limit reached; an older session was evicted"));
                }

                // Outcome == InProgress: more fragments are expected; nothing to emit yet.

                // TP reassembly has taken ownership of this packet's payload.
                tpHandled = true;
            }

            // Advance past the TP header for the actual payload
            payloadStart += SomeIpTpHeader.Size;
        }

        // ── SOME/IP-SD (Service Discovery, message ID = 0xFFFF8100) ──
        // The SD payload is taken directly after the 16-byte SOME/IP header (not after payloadStart). SOME/IP-SD is not
        // segmented by SOME/IP-TP, so a well-formed SD message never has the TP flag. For a TP-flagged SD message the TP
        // header above is decoded and its segment is fed to the reassembler (the reassembled payload is not dispatched
        // for SD), and the SD parser then reads the four TP header bytes as SD flags and reserved bytes, which shows
        // meaningless SD fields. That behavior is kept here; what to show for such a message is an open decision.
        if (header.MessageId == SomeIpSdParser.SdMessageId && someipData.Length > SomeIpHeader.Size)
        {
            ReadOnlySpan<byte> sdPayload = someipData.Span[SomeIpHeader.Size..];
            ParseResult sdResult = SomeIpSdParser.Parse(in container, sdPayload, in _SdFieldIds);
            if (sdResult.TryPropagateError(out ParseResult sdError))
            {
                return sdError;
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
            if (dispatchResult.TryPropagateError(out ParseResult error))
            {
                return error;
            }

            // If no sub-protocol consumed the payload, append raw payload bytes
            if (!dispatchResult.TryGetConsumed(out int consumed) || consumed == 0)
            {
                container.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));
            }
        }

        return 0;
    }
    #endregion
}
