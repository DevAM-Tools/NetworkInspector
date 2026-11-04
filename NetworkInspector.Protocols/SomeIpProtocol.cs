// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// SOME/IP (Scalable service-Oriented MiddlewarE over IP) message parser for the automotive middleware of AUTOSAR.
/// <para>
/// The parser receives one <em>unit</em> (a UDP datagram payload, one message framed out of a TCP stream, or the plaintext of a
/// decrypted record) and decodes the messages it contains into the field tree below. Everything that names a field of the header
/// is built eagerly during <see cref="Parse"/>, so the packet index sees every group the tree holds; the names of services,
/// methods and clients come from the immutable <see cref="SomeIpCompiledModel"/> that was built from the optional configuration.
/// </para>
/// <para>Field tree (a message; a unit holds one or more of them):</para>
/// <code>
/// someip: SOME/IP, Service: 0xF232 (ClimateControl), Method: 0x0001 (SetTemperature), REQUEST
/// ├── someip.messageid: 0xF2320001
/// ├── someip.serviceid: 0xF232 (ClimateControl)
/// ├── someip.service.name: Climate Control                [service known to the configuration]
/// ├── someip.methodid: 0x0001 (SetTemperature)
/// ├── someip.method.name: SetTemperature                  [method known to the configuration]
/// ├── someip.length: 24 (from client id)
/// ├── someip.clientid: 0x0001 (CockpitECU)
/// ├── someip.client.name: CockpitECU                      [client named by the configuration]
/// ├── someip.sessionid: 0x0010
/// ├── someip.version: 1
/// ├── someip.ifversion: 2
/// ├── someip.msgtype: REQUEST (0x00)
/// │   ├── someip.msgtype.base: REQUEST (0x00)
/// │   ├── someip.msgtype.ack: false
/// │   └── someip.msgtype.tp: false
/// ├── someip.returncode: E_OK (0x00)
/// ├── someip.error.*                                      [one String field per diagnostic]
/// ├── ClimateControl.v2: Climate Control                  [service node: the filter name of the service, eager]
/// │   └── ClimateControl.v2.SetTemperature: SetTemperature  [method node: the payload bytes as value, the decoded values lazy below it]
/// ├── someip.payload: 16 bytes                            [raw bytes of every payload no other parser took; a discovery message shows <c>someip_sd</c> instead]
/// └── someip.trailing: 5 bytes                            [fewer than 16 bytes after the last message]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread-safety:</b> the first parse of a packet id is ordered and single-threaded (the session serializes it under its
/// parse lock); every later parse of that id is a replay that reads only what the first parse recorded and may run on any number
/// of threads at the same time, also while later packets are first-parsed. <see cref="IngestGate"/> separates the two paths
/// without a caller-supplied flag, and the facts that depend on other packets live in the effect store of this protocol, never
/// in a field a replay could write. Everything else a parse reads (the model, the settings, the display tables) is immutable.</para>
/// <para><b>Why eager:</b> the header fields are cheap to build, are needed by the index and by filters on every packet, and the
/// acceptance decision of a unit that arrived without a dispatch key has to happen before anything is appended. A lazy header
/// would have to repeat that decision on materialization.</para>
/// </remarks>
[Protocol("someip", "SOME/IP", Description = "SOME/IP (AUTOSAR)")]
public sealed partial class SomeIpProtocol : IProtocol
{
    #region Constants

    /// <summary>The well-known port of SOME/IP over UDP and TCP (30490). It is also the port of service discovery.</summary>
    public const ulong DefaultPort = 30490;

    /// <summary>
    /// Protocol table name for the payload dispatch by message id. The key is the 32-bit message id
    /// (<c>service id &lt;&lt; 16 | method id</c>); a parser that registers here receives the payload of that message.
    /// </summary>
    public const string MessageIdTableName = "someip.messageid";

    /// <summary>Message id of SOME/IP service discovery (service 0xFFFF, method 0x8100).</summary>
    public const uint SdMessageId = SomeIpTables.SdMessageId;

    /// <summary>Index group of the header fields; every accepted unit records it.</summary>
    private const string _Group = "someip";

    #endregion

    #region Fields: container and header

    [BytesField("someip", "SOME/IP", IndexGroup = _Group)]
    private FieldId _ProtocolFieldId;

    [U64Field("someip.messageid", "Message ID", IndexGroup = _Group)]
    private FieldId _MessageIdFieldId;

    [U64Field("someip.serviceid", "Service ID", IndexGroup = _Group)]
    private FieldId _ServiceIdFieldId;

    [StringField("someip.service.name", "Service Name", IndexGroup = "someip.name")]
    private FieldId _ServiceNameFieldId;

    [U64Field("someip.methodid", "Method ID", IndexGroup = _Group)]
    private FieldId _MethodIdFieldId;

    [StringField("someip.method.name", "Method Name", IndexGroup = "someip.method.name")]
    private FieldId _MethodNameFieldId;

    [U64Field("someip.length", "Length", IndexGroup = _Group)]
    private FieldId _LengthFieldId;

    [U64Field("someip.clientid", "Client ID", IndexGroup = _Group)]
    private FieldId _ClientIdFieldId;

    [StringField("someip.client.name", "Client Name", IndexGroup = "someip.client.name")]
    private FieldId _ClientNameFieldId;

    [U64Field("someip.sessionid", "Session ID", IndexGroup = _Group)]
    private FieldId _SessionIdFieldId;

    [U64Field("someip.version", "Protocol Version", IndexGroup = _Group)]
    private FieldId _VersionFieldId;

    [U64Field("someip.ifversion", "Interface Version", IndexGroup = _Group)]
    private FieldId _IfVersionFieldId;

    [U64Field("someip.msgtype", "Message Type", IndexGroup = _Group)]
    private FieldId _MsgTypeFieldId;

    [U64Field("someip.msgtype.base", "Base Message Type", IndexGroup = _Group)]
    private FieldId _MsgTypeBaseFieldId;

    [BoolField("someip.msgtype.ack", "ACK Flag", IndexGroup = _Group)]
    private FieldId _MsgTypeAckFieldId;

    [BoolField("someip.msgtype.tp", "TP Flag", IndexGroup = _Group)]
    private FieldId _MsgTypeTpFieldId;

    [U64Field("someip.returncode", "Return Code", IndexGroup = _Group)]
    private FieldId _ReturnCodeFieldId;

    #endregion

    #region Fields: payload and diagnostics

    // The payload and the trailing bytes are raw regions. The group names of the diagnostics are camel case on purpose: the
    // generator builds the group field names from them (someip.error.lengthMin becomes _SomeipErrorLengthMinGroupId), and an
    // underscore in the group name would carry into an identifier.
    [BytesField("someip.payload", "Payload", IndexGroup = "someip.payload")]
    private FieldId _PayloadFieldId;

    [BoolField("someip.learned_port", "Accepted Through Learned Port", IndexGroup = "someip.learnedPort")]
    private FieldId _LearnedPortFieldId;

    [BytesField("someip.trailing", "Trailing Bytes", IndexGroup = "someip.error.trailing")]
    private FieldId _TrailingFieldId;

    [StringField("someip.error.incomplete", "Incomplete Header", IndexGroup = "someip.error.incomplete")]
    private FieldId _ErrorIncompleteFieldId;

    [StringField("someip.error.trailing", "Trailing Fragment", IndexGroup = "someip.error.trailing")]
    private FieldId _ErrorTrailingFieldId;

    [StringField("someip.error.length_min", "Length Below Minimum", IndexGroup = "someip.error.lengthMin")]
    private FieldId _ErrorLengthMinFieldId;

    [StringField("someip.error.truncated", "Truncated Message", IndexGroup = "someip.error.truncated")]
    private FieldId _ErrorTruncatedFieldId;

    [StringField("someip.error.version", "Unknown Protocol Version", IndexGroup = "someip.error.version")]
    private FieldId _ErrorVersionFieldId;

    [StringField("someip.error.unit_limit", "Message Limit Per Unit", IndexGroup = "someip.error.unitLimit")]
    private FieldId _ErrorUnitLimitFieldId;

    #endregion

    #region Fields: dispatch table

    [ProtocolTableU64(MessageIdTableName, "SOME/IP Message ID")]
    private ProtocolTableId _MessageIdTableId;

    #endregion

    #region Fields: bound ports

    /// <summary>The UDP ports SOME/IP is bound to (immutable; see <see cref="SomeIpPorts"/> for the rule).</summary>
    private readonly SomeIpPortSet _UdpPorts;

    /// <summary>The TCP ports SOME/IP is bound to (immutable).</summary>
    private readonly SomeIpPortSet _TcpPorts;

    /// <summary>The UDP ports announced by service discovery; <see langword="null"/> unless <c>someip.learn_sd_ports</c> is on.</summary>
    private readonly SomeIpLearnedPorts? _LearnedPorts;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates the protocol for one stack. <see cref="SomeIpRegistration"/> builds the model and the settings and calls this
    /// constructor; a stack without a configuration passes <see cref="SomeIpCompiledModel.Empty"/>.
    /// </summary>
    /// <param name="model">The compiled service description; read-only for the lifetime of the stack.</param>
    /// <param name="settings">The effective settings; copied, so a later change of a setting does not reach this instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    public SomeIpProtocol(SomeIpCompiledModel model, in SomeIpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        Settings = settings;
        _SdPopulator = _PopulateSd;
        _TpSegmentsPopulator = _PopulateTpSegments;
        _TpSessions = new SomeIpTpSessions(in settings);
        _TimingPopulator = _PopulateTiming;

        // The pending-request table exists only when the user switched the analysis on.
        _TimingTracker = settings.MethodTiming
            ? new SomeIpTimingTracker(settings.TimingMaxPending)
            : null;

        // The learned ports exist only when the user switched learning on; an 8 KiB set per stack that never uses it would be waste.
        _LearnedPorts = settings.LearnSdPorts
            ? new SomeIpLearnedPorts(settings.LearnSdPortsIgnore.Span)
            : null;

        // The ports this instance is bound to, by the same rule the registration binds them with. Built once here, read by every parse:
        // a bitmap test per unit instead of a lookup, and no allocation while parsing.
        _UdpPorts = SomeIpPortSet.Create(CollectionsMarshal.AsSpan(SomeIpPorts.WithDefault(model.UdpPorts.Span, alwaysWellKnown: true)));
        _TcpPorts = SomeIpPortSet.Create(CollectionsMarshal.AsSpan(SomeIpPorts.WithDefault(model.TcpPorts.Span, alwaysWellKnown: false)));
    }

    #endregion

    #region Properties

    /// <summary>The compiled service description that names services, methods and clients; empty without a configuration.</summary>
    public SomeIpCompiledModel Model { get; }

    /// <summary>The effective settings of this protocol instance.</summary>
    public SomeIpSettings Settings { get; }

    #endregion

    #region Parse

    /// <summary>
    /// Parses one unit: every SOME/IP message in <paramref name="data"/>, in order, until the data ends, a message is cut off, or
    /// the per-unit message limit is reached.
    /// </summary>
    /// <param name="parentField">Field that receives the SOME/IP containers (one per message).</param>
    /// <param name="data">The unit's bytes; a slice of the packet's buffers, which keys the effects of this unit.</param>
    /// <param name="context">Parse context used to dispatch a payload and to record index presence.</param>
    /// <returns>
    /// The bytes covered by the messages, the diagnostics and the trailing bytes; zero when the unit is not on a port that SOME/IP is
    /// bound to and is neither the declared plaintext of an encrypted transport nor accepted by content-based detection (nothing is
    /// appended, the transport keeps showing its payload).
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // First parse versus replay is decided by the gate alone, so no caller has to pass a mode flag. A replay holds the inert
        // default scope, which keeps the finally below free of a branch; the first parse opens the real one. The scope ends in
        // the using declaration, also after an error or an exception, so a later parse of this id can never take the mutating
        // path a second time.
        bool isReplay = _Gate.IsReplay(parentField.Packet.Id);
        using IngestScope scope = isReplay
            ? default
            : _Gate.Enter(parentField.Packet.Id);
        return _ParseUnit(in parentField, data, in context, isReplay);
    }

    /// <summary>
    /// The unit loop: the acceptance decision, then message after message until a stop condition. Its structure is the order of
    /// the checks that matter: acceptance first (nothing may be appended for a unit that is not ours), then for each message the
    /// room for a header (trailing bytes), the message limit, and only then the message itself.
    /// </summary>
    private ParseResult _ParseUnit(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context, bool isReplay)
    {
        // Unit effect key: the layer key of the unit's first byte, used only by the acceptance decision. A slice that is not part
        // of the packet's buffers (or lies beyond the 16 MiB offset range of a key) cannot be keyed; such a unit takes the
        // stateless path, where the decision depends on immutable state alone.
        int unitKey = parentField.TryGetEffectLayerKey(data, out int key)
            ? key
            : -1;

        // Acceptance gate. Whether a unit is SOME/IP depends on how it reached the parser, and the parse context cannot tell: most of the
        // ways a transport hands a payload on (framed messages of a stream, a cache of single-owner ports, the heuristic table) carry no
        // dispatch key, and a protocol called from another protocol inherits that protocol's key. The ports of the carrying
        // datagram or segment are published by the transport, so the parser checks them itself.
        bool accepted = _Accept(in parentField, unitKey, isReplay, out bool learned, out AcceptanceRecord pending);
        if (!accepted)
        {
            _FlushAcceptance(parentField.Packet.Id, ref pending);
            return 0;
        }

        // DTLS on a SOME/IP port (opt-in). Mixed fleets run cleartext and DTLS-protected SOME/IP on the same UDP ports; a datagram
        // that starts with a DTLS record header and is not a plausible SOME/IP message goes to the DTLS parser. The decision depends
        // on the bytes and on the published ports alone, so a replay repeats it without a record. Only units on a bound UDP port
        // are considered: a unit that reached the parser by detection or by a hand-off was chosen for its content already.
        if (Settings.DetectDtls
            && _DtlsProtocolId.IsValid
            && _IsBoundUdpPort(parentField.Packet.Id)
            && SomeIpDtlsProbe.LooksLikeDtls(data.Span)
            && !SomeIpDtlsProbe.LooksLikeSomeIp(data.Span))
        {
            return parentField.CallProtocol(_DtlsProtocolId, data, in context);
        }

        // The decision of an accepted unit is recorded when the unit ends, whatever way it ends (a message that shares its key with
        // the unit takes the decision into its own record earlier; see the message loop).
        try
        {
            return _ParseMessages(in parentField, data, in context, learned, isReplay, ref pending);
        }
        finally
        {
            _FlushAcceptance(parentField.Packet.Id, ref pending);
        }
    }

    /// <summary>The message loop of an accepted unit.</summary>
    private ParseResult _ParseMessages(
        in MutField parentField,
        ReadOnlyMemory<byte> data,
        in ParseContext context,
        bool learned,
        bool isReplay,
        ref AcceptanceRecord pending)
    {
        // Presence of the protocol itself is recorded per message below; here only the loop state is set up.
        int offset = 0;
        int count = 0;
        int max = Settings.MaxMessagesPerUnit;
        MutField lastContainer = default;
        InfoState info = default;

        while (true)
        {
            int remaining = data.Length - offset;

            // Fewer than 16 bytes cannot hold a header: they are shown, not dropped. Before any message the unit is an incomplete
            // header; behind a message the bytes are a trailing fragment.
            if (remaining < SomeIpHeader.Size)
            {
                if (remaining > 0)
                {
                    _AppendTrailing(in parentField, in lastContainer, count, data[offset..], in context);
                    offset = data.Length;
                }

                break;
            }

            // Limit someip.max_messages_per_unit: a datagram made of tiny messages would make one packet cost thousands of
            // containers. The check sits before the next message is parsed, so a unit of exactly the limit raises nothing; the
            // diagnostic goes to the last parsed message, and the rest of the unit is shown as raw bytes under it. The rest is consumed:
            // the transports do not show a tail that a payload parser leaves, so bytes left unconsumed would vanish from the packet.
            if (count == max)
            {
                _AppendUnitLimit(in lastContainer, data[offset..], in context);
                offset = data.Length;
                break;
            }

            // 16 bytes guaranteed by the check above.
            SomeIpHeader header = SomeIpHeader.Parse(data.Span.Slice(offset, SomeIpHeader.Size));
            ReadOnlyMemory<byte> messageData = data[offset..];
            ParseResult result = _ParseMessage(
                in parentField, messageData, in header, in context, learned, isReplay, ref pending, ref lastContainer, out bool stop, out MessageNames names);
            if (result.TryPropagateError(out ParseResult error))
            {
                return error;
            }

            _ = result.TryGetConsumed(out int consumed);
            if (count == 0)
            {
                info = new InfoState(0, header.ServiceId, names.ServiceName, header.MessageType, names.SdSummary);
            }


            offset += consumed;
            count++;
            if (stop)
            {
                break;
            }
        }

        // Packet info: one message reads "SOME/IP 0x1234 (Name) REQUEST", several read "SOME/IP (3 messages)". The text is built if
        // the info column is read; the state is one small box, the only allocation of the non-error path.
        parentField.SetPacketInfo(LazyString.FormatLazy(info with { Count = count }, static s => s.Format()));
        return offset;
    }

    /// <summary>
    /// Decides whether a unit is SOME/IP. A unit on a port that SOME/IP is bound to is accepted at once: the decision depends only
    /// on the published ports and the immutable port sets, so a replay reaches the same answer without any record, and a plain
    /// message costs no effect. Any other unit (a decrypted hand-off, content-based detection) is decided once by the first
    /// parse, recorded, and replayed, so the answer of the first parse never changes afterwards even if the facts it depended on do.
    /// </summary>
    private bool _Accept(in MutField parentField, int unitKey, bool isReplay, out bool learned, out AcceptanceRecord pending)
    {
        learned = false;
        pending = default;
        PacketId id = parentField.Packet.Id;
        if (_IsBoundPort(id))
        {
            return true;
        }

        if (isReplay)
        {
            // Replay: the recorded decision, learned flag included. A missing record means the first parse could not key the unit;
            // such a unit was decided without the learned ports (see below), so the decision rests on immutable state alone and
            // is evaluated again with the same result.
            if (unitKey >= 0 && _TryGetEffect(id, unitKey, out SomeIpMessageEffect recorded))
            {
                learned = recorded.Learned;
                return recorded.Accepted;
            }

            return _Decide(id);
        }

        bool accepted = _Decide(id);

        // The ports learned from service discovery are the one mutable fact in the decision, so they are consulted only when the
        // decision can be recorded (the unit has an effect key): a unit without one would be decided again on a replay, after more
        // ports were learned, and could change. UDP only: the ports of a TCP segment are not announced datagram ports.
        if (!accepted && unitKey >= 0 && _LearnedPorts is not null && _IsLearnedPort(id))
        {
            accepted = true;
            learned = true;
        }

        // The decision is recorded, but not here: the first message of the unit sits at the same layer key as the unit, and a
        // segment or timing fact of that message must share one record with the decision (the store refuses a second record for a
        // key). The unit loop flushes the record after the messages, when no message claimed it.
        if (unitKey >= 0)
        {
            pending = new AcceptanceRecord(unitKey, accepted, learned);
        }

        return accepted;
    }

    /// <summary>
    /// The acceptance decision of a unit that still has to be recorded. A message effect whose key equals the unit key takes it over
    /// (<see cref="_RecordMessageEffect"/>); otherwise <see cref="_FlushAcceptance"/> records it alone.
    /// </summary>
    private struct AcceptanceRecord(int unitKey, bool accepted, bool learned)
    {
        internal readonly int UnitKey = unitKey;
        internal readonly bool Accepted = accepted;
        internal readonly bool Learned = learned;
        internal bool Open = true;
    }

    /// <summary>Records the decision of a unit when no message effect carried it.</summary>
    private void _FlushAcceptance(PacketId id, ref AcceptanceRecord pending)
    {
        if (pending.Open)
        {
            pending.Open = false;
            _RecordEffect(id, pending.UnitKey, SomeIpMessageEffect.ForAcceptance(pending.Accepted, pending.Learned));
        }
    }

    /// <summary>
    /// Records the effect of one message (a segment outcome, a timing pair). When the message sits at the key of the unit, the
    /// unit's acceptance decision is stored in the same record, because a key holds one record.
    /// </summary>
    private void _RecordMessageEffect(PacketId id, int messageKey, ref AcceptanceRecord pending, SomeIpMessageEffect effect)
    {
        if (pending.Open && pending.UnitKey == messageKey)
        {
            pending.Open = false;
            effect = effect with { Accepted = pending.Accepted, Learned = pending.Learned };
        }

        _RecordEffect(id, messageKey, in effect);
    }

    /// <summary>Whether the datagram runs on a port that service discovery announced. Either end counts, as for a bound port.</summary>
    private bool _IsLearnedPort(PacketId id) =>
        UdpPortCache.TryGet(id, out ushort source, out ushort destination)
        && (_LearnedPorts!.Contains(source) || _LearnedPorts.Contains(destination));

    /// <summary>Whether the carrying datagram runs on a UDP port that SOME/IP is bound to (either end counts, as for any bound port).</summary>
    private bool _IsBoundUdpPort(PacketId id) =>
        UdpPortCache.TryGet(id, out ushort source, out ushort destination)
        && (_UdpPorts.Contains(source) || _UdpPorts.Contains(destination));

    /// <summary>
    /// The DTLS protocol of the stack, resolved when it registers (it may register after SOME/IP). Invalid in a stack without DTLS, in
    /// which case the detection does nothing. Assigned once on the single-threaded build phase, read-only afterwards.
    /// </summary>
    private ProtocolId _DtlsProtocolId = ProtocolId.Invalid;

    /// <summary>
    /// Whether the carrying datagram or segment runs on a port that SOME/IP is bound to (the configured ports plus the well-known
    /// port by the rule of <see cref="SomeIpPorts"/>). Either end counts, because the server port is the destination of a request
    /// and the source of its response. The transport publishes the ports; a unit that no transport published ports for (a direct
    /// call, a nested protocol) is not on a bound port.
    /// </summary>
    private bool _IsBoundPort(PacketId id)
    {
        // Both caches are keyed by the packet id, not by the layer: a tunnel can leave both set for one packet, so each transport
        // is judged by its own list and either match accepts the unit.
        if (UdpPortCache.TryGet(id, out ushort udpSource, out ushort udpDestination)
            && (_UdpPorts.Contains(udpSource) || _UdpPorts.Contains(udpDestination)))
        {
            return true;
        }

        if (TcpPortCache.TryGet(id, out ushort tcpSource, out ushort tcpDestination)
            && (_TcpPorts.Contains(tcpSource) || _TcpPorts.Contains(tcpDestination)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// The acceptance rule for a unit that is not on a bound port. Two things make such a unit SOME/IP. The first is a decrypted
    /// hand-off on a port the configuration lists for that transport: the user declared the port, and the decrypting protocol
    /// published the marker for this packet. The second is that the user switched content-based detection on. A plausible header is
    /// not proof of SOME/IP, so detection is opt-in; unrelated traffic that happens to start like a header would otherwise be
    /// decoded as SOME/IP on every port.
    /// </summary>
    private bool _Decide(PacketId id)
    {
        if (PlaintextHandoff.TryGet(id, out PlaintextTransport transport, out ushort port))
        {
            ReadOnlyMemory<ushort> listed = transport == PlaintextTransport.Tls
                ? Model.TlsPorts
                : Model.DtlsPorts;
            if (listed.Span.Contains(port))
            {
                return true;
            }
        }

        return Settings.Heuristic;
    }

    #endregion

    #region Message

    /// <summary>
    /// Parses one message at the start of <paramref name="messageData"/>: resolves its names, applies the length rules, appends the
    /// container with the header fields and the diagnostics, and hands the payload on. The header was decoded by the caller from
    /// the first 16 bytes.
    /// </summary>
    /// <param name="parentField">Field that receives the container.</param>
    /// <param name="messageData">The unit from the start of this message to its end (at least 16 bytes).</param>
    /// <param name="header">The decoded header of this message.</param>
    /// <param name="context">Parse context.</param>
    /// <param name="learned">Whether the unit was accepted through a port that service discovery announced.</param>
    /// <param name="isReplay">Whether this is a re-parse; a re-parse never changes the learned ports.</param>
    /// <param name="pending">The unit's acceptance decision that is still to be recorded; a segment effect at the unit's key takes it over.</param>
    /// <param name="lastContainer">
    /// Receives the container of this message, so the loop can hang trailing bytes and the unit limit diagnostic under the last message.
    /// </param>
    /// <param name="stop">True when the loop must not look for another message behind this one (the message was cut off).</param>
    /// <param name="names">The names the model gave to this message, for the packet info.</param>
    /// <returns>The bytes this message covers, or the error of the payload dispatch.</returns>
    private ParseResult _ParseMessage(
        in MutField parentField,
        ReadOnlyMemory<byte> messageData,
        in SomeIpHeader header,
        in ParseContext context,
        bool learned,
        bool isReplay,
        ref AcceptanceRecord pending,
        ref MutField lastContainer,
        out bool stop,
        out MessageNames names)
    {
        // Names first, because they decide which groups the tree will hold. A service is resolved by id and interface version
        // (the major version is part of the identity of a service definition); when only another version is defined the name is
        // still shown, because a renamed or re-versioned interface is still recognisably the same service. The method is looked
        // up only inside the resolved definition: method ids of different versions are different methods.
        bool hasModel = !Model.Services.IsEmpty;

        // A service or member the registration skipped (its name is held by another component) has no field id; it is treated as
        // not configured, so no node is appended and nothing is decoded into fields that do not exist. Display texts are the UI names.
        bool serviceResolved = Model.TryGetService(header.ServiceId, header.InterfaceVersion, out int serviceIndex)
            && Model.Services.Span[serviceIndex].FieldId.IsValid;
        string? serviceName = null;
        string? methodName = null;
        SomeIpServiceInfo? service = null;
        SomeIpMemberInfo? member = null;
        if (serviceResolved)
        {
            service = Model.Services.Span[serviceIndex];
            serviceName = service.UiName;
            if (service.MemberById.TryGetValue(header.MethodId, out int memberIndex) && service.Members.Span[memberIndex].FieldId.IsValid)
            {
                member = service.Members.Span[memberIndex];
                methodName = member.UiName;
            }
        }
        else if (Model.TryGetServiceName(header.ServiceId, out string? anyVersionName))
        {
            serviceName = anyVersionName;
        }

        bool clientNamed = Model.TryGetClientName(header.ClientId, out string? clientName);
        names = new MessageNames(serviceName, methodName, clientName, Member: member, Service: service);

        // Length rules. The length field is untrusted: it counts the bytes behind the length field itself, so the declared total
        // is 8 + length. The sum is formed in long, so a hostile 0xFFFFFFFF can neither wrap nor be mistaken for a small value.
        // Below 8 the header contradicts itself (the 8 bytes it must cover are not even there): only the 16 header bytes are
        // consumed, and the next message is looked for right behind them. Above the available data the message is cut off
        // (a capture limit or a segment that ends mid-message): what is there is shown, and nothing can follow it.
        bool lengthBelowMin = header.Length < SomeIpHeader.MinimumLength;
        long declaredTotal = SomeIpHeader.LengthPrefixSize + (long)header.Length;
        bool truncated = !lengthBelowMin && declaredTotal > messageData.Length;
        int consumed = lengthBelowMin
            ? SomeIpHeader.Size
            : (int)Math.Min(declaredTotal, messageData.Length);
        int payloadLength = lengthBelowMin
            ? 0
            : consumed - SomeIpHeader.Size;
        bool badVersion = header.ProtocolVersion != SomeIpHeader.SupportedProtocolVersion;

        // Presence first: the index has to see every group the tree will hold, whatever happens below. A group is recorded
        // exactly when its field is appended, except the payload group, which is a superset (a parser that consumes the payload
        // leaves no raw field behind, and a filter verifies a candidate on the tree anyway).
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_SomeipGroupId);
        if (serviceName is not null)
        {
            context.RecordGroupPresence(_SomeipNameGroupId);
        }

        if (methodName is not null)
        {
            context.RecordGroupPresence(_SomeipMethodNameGroupId);
        }

        if (clientNamed)
        {
            context.RecordGroupPresence(_SomeipClientNameGroupId);
        }

        // The identity nodes are appended on every path of a message whose header resolves them (see _AppendIdentity), so their
        // groups are exact: a filter on a service or a method is answered by the index and one look at an eager node, never by
        // a decode of the payload.
        if (service is not null)
        {
            context.RecordGroupPresence(service.Group);
        }

        if (member is not null)
        {
            context.RecordGroupPresence(member.Group);
        }

        if (lengthBelowMin)
        {
            context.RecordGroupPresence(_SomeipErrorLengthMinGroupId);
        }

        if (truncated)
        {
            context.RecordGroupPresence(_SomeipErrorTruncatedGroupId);
        }

        if (badVersion)
        {
            context.RecordGroupPresence(_SomeipErrorVersionGroupId);
        }

        // The raw payload group is recorded for every message that can show raw bytes. A discovery message shows its payload as the
        // lazy container instead, so it records the groups of that container (see the scan) and not this one; only a payload too
        // short for the container falls back to raw bytes, and records the group at that point.
        bool isDiscovery = header.MessageId == SdMessageId;
        if (learned)
        {
            context.RecordGroupPresence(_SomeipLearnedPortGroupId);
        }

        // The TP flag promises a TP header of its own. A message that cannot hold one (and whose length field is sound, so the
        // length diagnostic is not the cause) is malformed: its bytes stay raw, even for a discovery message, and the error says
        // so; they are never decoded as parameters, which would invent values.
        bool tpBranch = header.IsTp && payloadLength >= SomeIpTpHeader.Size;
        bool tpTooShort = header.IsTp && !tpBranch && !lengthBelowMin;
        if (payloadLength > 0 && !tpBranch && (tpTooShort || !isDiscovery))
        {
            context.RecordGroupPresence(_SomeipPayloadGroupId);
        }

        if (tpTooShort)
        {
            context.RecordGroupPresence(_SomeipTpErrorMalformedGroupId);
        }

        // The container's value is this message's bytes (a slice, no copy). Its display text is passed as parts (eight is the most
        // the overloads take): a parse that builds no tree drops them without formatting, and a parse that does formats them on
        // first read. The name parts are NameSuffix values, which build " (Name)" only when read, so no string is concatenated
        // per message.
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId,
            FieldValue.NewBytes(messageData[..consumed]),
            "SOME/IP, Service: ",
            new HexId(header.ServiceId),
            new NameSuffix(serviceName, hasModel && serviceName is null),
            ", Method: ",
            new HexId(header.MethodId),
            new NameSuffix(methodName, serviceResolved && methodName is null),
            ", ",
            SomeIpTables.MessageTypeName(header.MessageType));
        lastContainer = container;

        _AppendHeader(in container, in header, serviceName, methodName, clientName, hasModel, serviceResolved);
        _AppendMessageDiagnostics(in container, in header, lengthBelowMin, truncated, messageData.Length, badVersion);
        if (learned)
        {
            container.Append(_LearnedPortFieldId, FieldValue.NewBool(true));
        }


        stop = truncated;

        // Method timing of a message that is not segmented (a segmented one is timed by its segment, see the TP branch). A request
        // registers and a response pairs on the first parse; the container is appended behind the payload, whichever way the
        // payload branch below returns.
        TimingFact timing = default;
        int timingKey = -1;
        if (_TimingTracker is not null && !header.IsTp)
        {
            timing = _TimePlainMessage(in parentField, messageData, in header, isReplay, ref pending, out timingKey);
        }

        if (payloadLength == 0)
        {
            // A discovery message without payload is malformed (the fixed part is missing): the scan says so, nothing is expanded.
            if (tpTooShort)
            {
                _AppendTpTooShort(in container, ReadOnlyMemory<byte>.Empty);
            }
            else if (isDiscovery)
            {
                _ = _AppendSd(in container, ReadOnlyMemory<byte>.Empty, in context, isReplay, out _);
            }
            else if (!lengthBelowMin && !truncated)
            {
                // A member whose direction declares parameters but a message without payload: the sender left them out.
                _AppendMissingPayload(in container, in header, names.Member, in context);
            }

            _AppendIdentity(in container, in names, ReadOnlyMemory<byte>.Empty, populator: null);
            if (timing.Present && timingKey >= 0)
            {
                _AppendTiming(in container, timingKey, in context);
            }

            return consumed;
        }

        ReadOnlyMemory<byte> payload = messageData.Slice(SomeIpHeader.Size, payloadLength);
        if (tpTooShort)
        {
            _AppendTpTooShort(in container, payload);
            _AppendIdentity(in container, in names, payload, populator: null);
            return consumed;
        }

        // A segmented message: the segment is fed to the reassembler, and only the segment that delivers a payload (the whole
        // message, or the part of it that arrived without a gap) hands it on, as a buffer the packet binds. Every other segment
        // shows its TP header fields and nothing of its own bytes.
        if (tpBranch)
        {
            int messageKey = parentField.TryGetEffectLayerKey(messageData, out int key)
                ? key
                : -1;
            ParseResult segment = _AppendTp(
                in container,
                payload,
                in header,
                in context,
                messageKey,
                isReplay,
                ref pending,
                out ReadOnlyMemory<byte> delivered,
                out bool partial,
                out TimingFact segmentTiming);
            if (segment.TryPropagateError(out ParseResult segmentError))
            {
                return segmentError;
            }

            if (delivered.IsEmpty)
            {
                _AppendIdentity(in container, in names, ReadOnlyMemory<byte>.Empty, populator: null);
                return consumed;
            }

            ParseResult handedOn = _HandOffPayload(in container, in header, delivered, in context, isReplay, ref names, true, partial);
            if (handedOn.TryPropagateError(out ParseResult handOffError))
            {
                return handOffError;
            }

            if (segmentTiming.Present && messageKey >= 0)
            {
                _AppendTiming(in container, messageKey, in context);
            }

            return consumed;
        }

        ParseResult result = _HandOffPayload(in container, in header, payload, in context, isReplay, ref names, false, partial: false);
        if (result.TryPropagateError(out ParseResult resultError))
        {
            return resultError;
        }

        if (timing.Present && timingKey >= 0)
        {
            _AppendTiming(in container, timingKey, in context);
        }

        return consumed;
    }

    /// <summary>
    /// Gives the payload of a message to whoever decodes it. A discovery message has its own structure: the scan decides what the
    /// index sees, and the container expands on demand; a payload too short for it stays visible as raw bytes (the scan has put the
    /// diagnostic on the message container). Any other payload goes first to a parser registered for the message id on the dispatch
    /// table, and what nobody consumes stays visible as raw bytes.
    /// </summary>
    /// <param name="container">The message container.</param>
    /// <param name="header">The header of the message.</param>
    /// <param name="payload">The payload to hand on: the message payload, or the buffer a segmented message delivered.</param>
    /// <param name="context">Parse context.</param>
    /// <param name="isReplay">Whether this is a re-parse.</param>
    /// <param name="names">Receives the discovery summary for the packet info.</param>
    /// <param name="partial">Whether the payload is the gap-free prefix of an incomplete segmented message; no checksum is verified then.</param>
    /// <param name="recordPayloadGroup">
    /// Whether the raw payload group has not been recorded yet for this message: a plain message records it with the other groups
    /// before the tree is built; the payload of a segmented message is only known now.
    /// </param>
    private ParseResult _HandOffPayload(
        in MutField container,
        in SomeIpHeader header,
        ReadOnlyMemory<byte> payload,
        in ParseContext context,
        bool isReplay,
        ref MessageNames names,
        bool recordPayloadGroup,
        bool partial)
    {
        if (header.MessageId == SdMessageId)
        {
            if (_AppendSd(in container, payload, in context, isReplay, out string? summary))
            {
                names = names with { SdSummary = summary };
                return 0;
            }

            context.RecordGroupPresence(_SomeipPayloadGroupId);
            container.Append(_PayloadFieldId, FieldValue.NewBytes(payload));
            return 0;
        }

        ParseResult dispatched = container.TryCallNextProtocolU64(_MessageIdTableId, header.MessageId, payload, in context);
        if (dispatched.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        if (dispatched.TryGetConsumed(out int payloadConsumed) && payloadConsumed > 0)
        {
            // Another parser took the payload: no raw payload field, but the identity is still the message's.
            _AppendIdentity(in container, in names, payload, populator: null);
            return 0;
        }

        if (recordPayloadGroup)
        {
            context.RecordGroupPresence(_SomeipPayloadGroupId);
        }

        // The raw bytes of every payload nobody consumed, the identity nodes, and for a member the model defines the lazy decoded
        // values below the method node (or the marker of a switched-off decode).
        _AppendPayload(in container, in header, in names, payload, partial, in context);
        return 0;
    }

    /// <summary>
    /// Appends the header fields in wire order. The display texts are table lookups or lazy two-part texts, so a parse that
    /// builds no tree formats nothing. The unknown marker is shown only when a configuration exists: a stack without one cannot
    /// call anything "unknown", and the suffix would be noise on every message.
    /// </summary>
    private void _AppendHeader(
        in MutField container,
        in SomeIpHeader header,
        string? serviceName,
        string? methodName,
        string? clientName,
        bool hasModel,
        bool serviceResolved)
    {
        container.AppendWithCustomText(_MessageIdFieldId, FieldValue.NewU64(header.MessageId), "0x", new UpperHex8(header.MessageId));

        _AppendNamedId(in container, _ServiceIdFieldId, header.ServiceId, serviceName, unknown: hasModel && serviceName is null);
        if (serviceName is not null)
        {
            container.Append(_ServiceNameFieldId, FieldValue.NewString(serviceName));
        }

        _AppendNamedId(in container, _MethodIdFieldId, header.MethodId, methodName, unknown: serviceResolved && methodName is null);
        if (methodName is not null)
        {
            container.Append(_MethodNameFieldId, FieldValue.NewString(methodName));
        }

        // The length counts from the client id, not from the start of the message; the text says so because the common mistake
        // is to read it as the message size.
        container.AppendWithCustomText(_LengthFieldId, FieldValue.NewU64(header.Length), header.Length, " (from client id)");

        _AppendNamedId(in container, _ClientIdFieldId, header.ClientId, clientName, unknown: false);
        if (clientName is not null)
        {
            container.Append(_ClientNameFieldId, FieldValue.NewString(clientName));
        }

        container.AppendWithCustomText(_SessionIdFieldId, FieldValue.NewU64(header.SessionId), new HexId(header.SessionId));
        container.Append(_VersionFieldId, FieldValue.NewU64(header.ProtocolVersion));
        container.Append(_IfVersionFieldId, FieldValue.NewU64(header.InterfaceVersion));

        // The message type carries its decomposition as children, so a filter can select a request whether or not it is
        // segmented or acknowledged without listing every combination.
        MutField messageType = container.AppendWithCustomText(
            _MsgTypeFieldId, FieldValue.NewU64(header.MessageType), new LazyString(SomeIpTables.MessageType(header.MessageType)));
        messageType.AppendWithCustomText(
            _MsgTypeBaseFieldId, FieldValue.NewU64(header.BaseType), new LazyString(SomeIpTables.BaseType(header.BaseType)));
        messageType.Append(_MsgTypeAckFieldId, FieldValue.NewBool(header.IsAck));
        messageType.Append(_MsgTypeTpFieldId, FieldValue.NewBool(header.IsTp));

        container.AppendWithCustomText(
            _ReturnCodeFieldId, FieldValue.NewU64(header.ReturnCode), new LazyString(SomeIpTables.ReturnCode(header.ReturnCode)));
    }

    /// <summary>Appends a 16-bit id with its hex text and, when known, its name; or the unknown marker; or the plain hex text.</summary>
    private static void _AppendNamedId(in MutField container, FieldId fieldId, ushort value, string? name, bool unknown)
    {
        container.AppendWithCustomText(fieldId, FieldValue.NewU64(value), new HexId(value), new NameSuffix(name, unknown));
    }

    /// <summary>
    /// Appends the diagnostics of one message. They are String fields with stable names; the sentence carries the numbers and is
    /// built with the invariant culture, and only here, on the error path, so a well-formed message formats nothing.
    /// </summary>
    private void _AppendMessageDiagnostics(
        in MutField container,
        in SomeIpHeader header,
        bool lengthBelowMin,
        bool truncated,
        int available,
        bool badVersion)
    {
        if (lengthBelowMin)
        {
            container.Append(
                _ErrorLengthMinFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The length field is {header.Length}, below the minimum of {SomeIpHeader.MinimumLength}; only the 16 header bytes are consumed.")));
        }

        if (truncated)
        {
            container.Append(
                _ErrorTruncatedFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The message declares {SomeIpHeader.LengthPrefixSize + (long)header.Length} bytes (length {header.Length} plus 8) but only {available} are available.")));
        }

        if (badVersion)
        {
            container.Append(
                _ErrorVersionFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Protocol version {header.ProtocolVersion} is not defined; the only defined version is {SomeIpHeader.SupportedProtocolVersion}.")));
        }
    }

    /// <summary>
    /// Shows the bytes that cannot hold a header. Before any message the unit is an incomplete header: a container of its own
    /// holds the bytes, so the packet shows that SOME/IP was recognised but cut short. Behind a message the bytes are a trailing
    /// fragment of the unit and hang under that message. Both are shown, never dropped: the bytes may be the start of a message
    /// that the capture cut off.
    /// </summary>
    private void _AppendTrailing(
        in MutField parentField,
        in MutField lastContainer,
        int count,
        ReadOnlyMemory<byte> bytes,
        in ParseContext context)
    {
        context.RecordGroupPresence(_SomeipErrorTrailingGroupId);
        if (count == 0)
        {
            context.RecordProtocolPresence(_ProtocolId);
            context.RecordGroupPresence(_SomeipGroupId);
            context.RecordGroupPresence(_SomeipErrorIncompleteGroupId);
            MutField incomplete = parentField.AppendWithCustomText(_ProtocolFieldId, FieldValue.NewBytes(bytes), "SOME/IP (incomplete header)");
            incomplete.Append(
                _ErrorIncompleteFieldId,
                FieldValue.NewString(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Incomplete SOME/IP header: {bytes.Length} of {SomeIpHeader.Size} bytes.")));
            incomplete.Append(_TrailingFieldId, FieldValue.NewBytes(bytes));
            return;
        }

        lastContainer.Append(_TrailingFieldId, FieldValue.NewBytes(bytes));
        lastContainer.Append(
            _ErrorTrailingFieldId,
            FieldValue.NewString(string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes.Length} bytes follow the last message, fewer than the {SomeIpHeader.Size}-byte header of another message.")));
    }

    /// <summary>
    /// Marks the last decoded message of a unit that has more messages than <c>someip.max_messages_per_unit</c> allows and shows the
    /// rest of the unit as raw bytes under it. The bytes are shown, not dropped: the limit protects the field tree from a flood of
    /// containers, not the reader from seeing that more data was there.
    /// </summary>
    private void _AppendUnitLimit(in MutField lastContainer, ReadOnlyMemory<byte> rest, in ParseContext context)
    {
        context.RecordGroupPresence(_SomeipErrorUnitLimitGroupId);
        context.RecordGroupPresence(_SomeipErrorTrailingGroupId);
        lastContainer.Append(_TrailingFieldId, FieldValue.NewBytes(rest));
        lastContainer.Append(
            _ErrorUnitLimitFieldId,
            FieldValue.NewString(SomeIpLimits.MaxMessagesPerUnit.HitText(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The unit holds more messages than are decoded; the remaining {rest.Length} bytes are not decoded"),
                (ulong)Settings.MaxMessagesPerUnit)));
    }

    #endregion

    #region Helper types

    /// <summary>The names and the member that the model gave to one message; null where it knows none. The member travels with the names so that
    /// the payload branch does not look the same service and method up a second time.</summary>
    private readonly record struct MessageNames(
        string? ServiceName, string? MethodName, string? ClientName, string? SdSummary = null, SomeIpMemberInfo? Member = null,
        SomeIpServiceInfo? Service = null);

    /// <summary>
    /// The state of the packet info text. Count 0 is a unit with an incomplete header, 1 a single message, more a bundle. A small
    /// value type behind one box, formatted by a static method, so the text costs one allocation until it is read.
    /// </summary>
    private readonly record struct InfoState(int Count, ushort ServiceId, string? ServiceName, byte MessageType, string? SdSummary = null)
    {
        internal string Format()
        {
            if (Count == 0)
            {
                return "SOME/IP (incomplete header)";
            }

            // A discovery message reads as its entries ("SOME/IP-SD [Offer] (multicast)"): that is what the list is scanned for.
            if (Count == 1 && SdSummary is not null)
            {
                return SdSummary;
            }

            if (Count > 1)
            {
                return string.Create(CultureInfo.InvariantCulture, $"SOME/IP ({Count} messages)");
            }

            HexId hex = new(ServiceId);
            string type = SomeIpTables.MessageTypeName(MessageType);
            return ServiceName is null
                ? string.Create(CultureInfo.InvariantCulture, $"SOME/IP {hex} {type}")
                : string.Create(CultureInfo.InvariantCulture, $"SOME/IP {hex} ({ServiceName}) {type}");
        }
    }

    /// <summary>
    /// A 16-bit identifier as its specification writes it: <c>0x</c> and four uppercase hex digits (service 0xF232). A display part,
    /// formatted when the text is first read. The shared 16-bit table of the host is lowercase, which is why it is not used for
    /// identifiers whose documentation and configuration files are written in uppercase.
    /// </summary>
    private readonly struct HexId(ushort value)
    {
        /// <inheritdoc/>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"0x{value:X4}");
    }

    /// <summary>
    /// The text that follows an id: the name in parentheses when the model knows one, the unknown marker when a configuration
    /// exists but does not know the id, nothing otherwise. It is a display part: the text is built by <see cref="ToString"/> when
    /// the display text is first read, never while parsing, which is why the name is not concatenated onto the id up front.
    /// </summary>
    private readonly struct NameSuffix(string? name, bool unknown)
    {
        /// <inheritdoc/>
        public override string ToString()
        {
            if (name is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $" ({name})");
            }

            return unknown
                ? " (unknown)"
                : "";
        }
    }

    #endregion
}
