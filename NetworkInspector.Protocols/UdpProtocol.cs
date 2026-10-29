// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// User Datagram Protocol (RFC 768) parser with checksum validation.
/// <para>Field tree structure:</para>
/// <code>
/// udp.stream: 0                                [eager, always present when IP layer exists]
/// udp: User Datagram Protocol, Src Port: 12345, Dst Port: 53
/// ├── udp.srcport: 12345
/// ├── udp.dstport: 53
/// ├── udp.length: 30
/// ├── udp.checksum: 0xabcd
/// ├── udp.checksum.status: [Good] / [Bad]  [optional, when verification enabled]
/// └── udp.payload: (22 bytes)              [optional]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The first parse of a packet id mutates <see cref="UdpStreamTracker"/>
/// and must therefore stay ordered and single-threaded (the session guarantees this under its parse
/// lock). Every later parse of an already-parsed id is a replay: it only reads the protocol-owned
/// effect store and is safe to run on any number of threads concurrently, including while further
/// packets are being parsed for the first time. The watermark <see cref="_Gate"/>
/// separates both paths without any caller-supplied mode flag.</para>
/// </remarks>
[Protocol("udp", "User Datagram Protocol", Description = "UDP (RFC 768)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class UdpProtocol : IProtocol
{
    #region Table Key Constants

    /// <summary>IP protocol number for UDP (17).</summary>
    public const ulong IpProtoKey = 17;

    /// <summary>IP protocol number value for pseudo-header computation.</summary>
    private const byte _UdpProtocolNumber = 17;

    #endregion

    #region Table Name Constants

    /// <summary>Dispatch table name for UDP port-based protocol lookup.</summary>
    public const string PortTableName = "udp.port";

    /// <summary>
    /// Heuristic table that is tried after no port parser consumed the payload. UDP finds it by this name when the stack
    /// starts (the source generator does not support heuristic table attributes), like TCP does with <c>tcp.heuristic</c>.
    /// </summary>
    public const string HeuristicTableName = "udp.heuristic";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present UDP fields.</summary>
    private const string _UdpIndexGroup = "udp";

    #endregion

    #region Fields

    /// <summary>
    /// The error of a re-parse whose first parse recorded no stream effect for this UDP layer. The payload is
    /// present, so an insufficient-data error would mislead.
    /// </summary>
    private static readonly ParseError _MissingStreamEffectError = ParseError.Custom(
        ProtocolName,
        "No recorded UDP stream for this layer: the packet is a re-parse, but its first parse recorded none.");

    // BytesField container carries header byte range for UI highlighting
    [BytesField("udp", "UDP", IndexGroup = _UdpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("udp.srcport", "Source Port", IndexGroup = _UdpIndexGroup)]
    private FieldId _SrcPortFieldId;

    [U64Field("udp.dstport", "Destination Port", IndexGroup = _UdpIndexGroup)]
    private FieldId _DstPortFieldId;

    [U64Field("udp.length", "Length", IndexGroup = _UdpIndexGroup)]
    private FieldId _LengthFieldId;

    [U64Field("udp.checksum", "Checksum", IndexGroup = _UdpIndexGroup)]
    private FieldId _ChecksumFieldId;

    // Checksum validation status (optional, only when verification is enabled and the datagram carries a checksum)
    [StringField("udp.checksum.status", "Checksum Status", IndexGroup = "udp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Field alias group ID assigned in _RegisterFieldsCustom for "udp.port" -> { udp.srcport, udp.dstport }.
    // Independent of the protocol table also named "udp.port" (PortTableName) — alias / field /
    // table namespaces do not collide. The alias name is metadata-only and never resolves
    // through GetFieldId, and no udp.port node is appended to the parse tree.
    private FieldAliasGroupId _PortAliasGroupId;

    #endregion

    #region Stream index (always present when IP layer exists, eagerly appended)

    /// <summary>Conversation index identifying the UDP "stream" (unique 4-tuple).</summary>
    [U64Field("udp.stream", "Stream index", IndexGroup = "udp.stream")]
    private FieldId _StreamFieldId;

    // Optional payload field
    [BytesField("udp.payload", "Payload", IndexGroup = "udp.payload")]
    private FieldId _PayloadFieldId;

    // Warning field appended when no enclosing IP layer is found (tunnel misconfiguration, etc.)
    [StringField("udp.error.no_ip", "No enclosing IP layer", IndexGroup = "udp.error")]
    private FieldId _NoIpLayerFieldId;

    // Reported when the UDP length field is below the 8-byte header size.
    [StringField("udp.error.bad_length", "Bad UDP length", IndexGroup = "udp.error")]
    private FieldId _BadLengthFieldId;

    // Reported when the stream table is full (see udp.max_tracked_streams): no udp.stream is assigned for a new tuple.
    [StringField("udp.error.stream_limit", "UDP stream limit reached", IndexGroup = "udp.error")]
    private FieldId _StreamLimitFieldId;

    // Port dispatch table
    [ProtocolTableU64(PortTableName, "UDP Port")]
    private ProtocolTableId _PortTableId;

    // Content-based fallback table, resolved by name in _OnStartCustom; invalid when the stack registered none.
    private HeuristicProtocolTableId _HeuristicTableId;

    // Runtime setting for checksum verification. Off by default: verification costs one pass over the datagram per packet
    // and needs the enclosing IP addresses for the pseudo-header.
    [BoolSetting("udp.verify_checksum", "Verify Checksum", "udp", Default = false)]
    private bool _VerifyChecksum;

    // Limit: number of UDP conversations (4-tuples) the tracker keeps. Value: default 10000; values at or above int.MaxValue are
    // clamped to int.MaxValue in _TryAppendUdpStream. Threat: every distinct tuple adds a dictionary entry that lives as long as
    // this protocol instance (the stack), so a scan or flood of distinct tuples would otherwise grow memory without bound.
    // Configurable (setting, changed by the operator/user), not a constant, because the right size depends on the host and the
    // capture. Posture: secure by default (tight default, raising it is opt-in), because the input comes from untrusted
    // networks. Exceeding it is an expected failure, reported per packet as udp.error.stream_limit, never a silent drop; the
    // datagram itself is still parsed and dispatched, only the stream index is not assigned. Same rules as
    // tcp.max_tracked_streams.
    [U64Setting("udp.max_tracked_streams", "Maximum tracked UDP streams", "udp", Default = 10000,
        Description = "Upper bound for the number of tracked UDP conversations. A new conversation beyond it gets no stream index "
            + "and is flagged; raising the value lets a flood of distinct address/port pairs use more memory.")]
    private ulong _MaxTrackedStreams;

    #endregion

    #region Cross-Protocol Field References
    // Resolved during OnStart via stack.GetFieldId() for reading IP addresses
    // from the field tree via sibling navigation (not flat-array scan). This is only the fallback: the normal path reads the
    // per-thread address caches of the IP protocols, which also work when no field tree is built.

    /// <summary>FieldId of the IPv4 container field ("ip"), used to identify the IP
    /// container when walking backwards through siblings.</summary>
    private FieldId _IpContainerFieldId;

    /// <summary>FieldId of the IPv6 container field ("ipv6"), used to identify the IPv6
    /// container when walking backwards through siblings.</summary>
    private FieldId _Ipv6ContainerFieldId;

    /// <summary>FieldId of ip.src (resolved at startup).</summary>
    private FieldId _IpSrcFieldId;

    /// <summary>FieldId of ip.dst (resolved at startup).</summary>
    private FieldId _IpDstFieldId;

    /// <summary>FieldId of ipv6.src (resolved at startup).</summary>
    private FieldId _Ipv6SrcFieldId;

    /// <summary>FieldId of ipv6.dst (resolved at startup).</summary>
    private FieldId _Ipv6DstFieldId;

    /// <summary>ProtocolId of the IPv4 protocol ("ip"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="IProtocol.Parse"/> is dispatched from an enclosing IPv4 layer.</summary>
    private ProtocolId _Ipv4ProtocolId;

    /// <summary>ProtocolId of the IPv6 protocol ("ipv6"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="IProtocol.Parse"/> is dispatched from an enclosing IPv6 layer.</summary>
    private ProtocolId _Ipv6ProtocolId;

    #endregion

    #region Stream tracking

    /// <summary>
    /// Tracks UDP conversations and assigns monotonic stream indices. Mutable cross-packet state: only the ordered
    /// first-parse path may call it (see the class remarks); replays use the recorded <see cref="StreamEffect"/>.
    /// </summary>
    private readonly UdpStreamTracker _StreamTracker = new();

    /// <summary>Resolves cross-protocol field IDs for checksum computation and stream tracking.</summary>
    partial void _OnStartCustom(Stack stack)
    {
        _IpContainerFieldId = stack.GetFieldId("ip") ?? FieldId.Invalid;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? FieldId.Invalid;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? FieldId.Invalid;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? FieldId.Invalid;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? FieldId.Invalid;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? FieldId.Invalid;
        _Ipv4ProtocolId = stack.GetProtocolId("ip") ?? ProtocolId.Invalid;
        _Ipv6ProtocolId = stack.GetProtocolId("ipv6") ?? ProtocolId.Invalid;
        _HeuristicTableId = stack.GetHeuristicProtocolTableId(HeuristicTableName) ?? HeuristicProtocolTableId.Invalid;
    }

    /// <summary>
    /// Registers protocol-owned alias groups. Adds "udp.port" -> { udp.srcport, udp.dstport }
    /// as metadata; the alias is reachable only via the alias-group APIs on IStack and is
    /// independent of the dispatch table also named "udp.port".
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _PortAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "udp.port",
            "Any-match alias for source/destination UDP ports.",
            [_SrcPortFieldId, _DstPortFieldId]);
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses a Udp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// <para>
    /// The first parse of a packet id draws stream indices from <see cref="UdpStreamTracker"/> and
    /// records them; every later parse of that id replays the recorded assignment and never touches
    /// the tracker. Both paths are separated by the watermark — see <see cref="_Gate"/>.
    /// </para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Packet packet = parentField.Packet;

        // The effect store is keyed by (packet id, buffer location of this layer's data); a slice outside the packet's
        // buffers cannot be keyed, so ingest and replay would disagree: reject it.
        if (!parentField.TryGetEffectLayerKey(data, out int layerKey))
        {
            return ParseError.Custom(ProtocolName, "Parse data is not a slice of this packet's buffers.");
        }

        // First parse or replay is decided only by the ingest gate (no caller-supplied mode flag). Only the outermost
        // scope raises the watermark, so nested UDP layers of one packet are all first parses. A replay holds the inert
        // default scope.
        bool isReplay = _Gate.IsReplay(packet.Id);
        IngestScope scope = isReplay
            ? default
            : _Gate.Enter(packet.Id);

        // The scope ends in finally, also after errors and exceptions, so a later parse of this id can never
        // re-enter the first-parse path and draw a second stream index from the tracker.
        try
        {
            return _ParseBody(in parentField, data, in context, layerKey, isReplay);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Parse body. <paramref name="layerKey"/> is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call and
    /// <paramref name="isReplay"/> selects effect replay over tracker mutation.
    /// </summary>
    private ParseResult _ParseBody(
        in MutField parentField,
        ReadOnlyMemory<byte> data,
        in ParseContext context,
        int layerKey,
        bool isReplay)
    {
        if (data.Length < UdpHeader.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_UdpGroupId);

        ReadOnlySpan<byte> span = data.Span;

        if (!UdpHeader.TryParse(span, out UdpHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
        }

        ushort srcPort = header.SrcPort;
        ushort dstPort = header.DstPort;
        ushort length = header.Length;
        ushort checksum = header.Checksum;

        // Compute payload bounds (guard against negative). The payload ends at the smaller of the UDP length field and the
        // available data: the length field is attacker-controlled and may claim more than was captured, or (invalid) less
        // than the header, and trailing bytes beyond it are not payload.
        int payloadLen = Math.Max(0, Math.Min(length - UdpHeader.HeaderSize, data.Length - UdpHeader.HeaderSize));
        ReadOnlyMemory<byte> payloadData = data.Slice(UdpHeader.HeaderSize, payloadLen);

        // Record optional payload group if payload exists
        if (payloadLen > 0)
        {
            context.RecordGroupPresence(_UdpPayloadGroupId);
        }

        // ── Stream tracking and checksum pre-computation ──────────────────────────────────────────
        // Read IP addresses from the per-protocol thread-local caches (populated by
        // IPv4Protocol/IPv6Protocol during their eager Parse, which a replay runs again on its own thread). Falls back to sibling-walk navigation
        // for edge cases (custom stacks, non-standard encapsulations). The caches avoid the field-tree walk and are the
        // only source when no field tree is built.
        //
        // CallerProtocolId selects which cache to check first. In tunnel scenarios
        // (e.g., IPv6-over-IPv4 / 6in4), both caches may hold valid entries for the same
        // PacketId — the outer IPv4 layer caches its addresses, then the inner IPv6 layer
        // caches its own. Without this guard, checking IPv4 first would return the outer
        // tunnel endpoints instead of the inner IPv6 endpoints, producing a wrong
        // pseudo-header and an incorrect stream key.
        PacketId packetId = parentField.Packet.Id;
        bool callerIsIpv4 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv4ProtocolId;
        bool callerIsIpv6 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv6ProtocolId;

        // Whether the enclosing layer is IPv6, set by the branches below. It decides how a zero checksum is read.
        bool enclosedByIpv6 = false;

        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            // IPv4: build connection key
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(src4.RawValue, dst4.RawValue, srcPort, dstPort);
            if (!_TryAppendUdpStream(in parentField, in context, in connKey, layerKey, isReplay))
            {
                return _MissingStreamEffectError;
            }
        }
        else if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
        {
            // IPv6: build connection key
            enclosedByIpv6 = true;
            UdpConnectionKey connKey = new(new UInt128(src6.High, src6.Low), new UInt128(dst6.High, dst6.Low), srcPort, dstPort);
            if (!_TryAppendUdpStream(in parentField, in context, in connKey, layerKey, isReplay))
            {
                return _MissingStreamEffectError;
            }
        }
        else if (parentField.HasFieldTree
            && _TryFindPreviousIpAddressesFallback(parentField, out IPv4Address fbSrc4, out IPv4Address fbDst4, in context))
        {
            // Fallback: IPv4 via sibling walk (edge case — cache miss)
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(fbSrc4.RawValue, fbDst4.RawValue, srcPort, dstPort);
            if (!_TryAppendUdpStream(in parentField, in context, in connKey, layerKey, isReplay))
            {
                return _MissingStreamEffectError;
            }
        }
        else if (parentField.HasFieldTree
            && _TryFindPreviousIpv6AddressesFallback(parentField, out IPv6Address fbSrc6, out IPv6Address fbDst6, in context))
        {
            // Fallback: IPv6 via sibling walk (edge case — cache miss)
            enclosedByIpv6 = true;
            UdpConnectionKey connKey = new(new UInt128(fbSrc6.High, fbSrc6.Low), new UInt128(fbDst6.High, fbDst6.Low), srcPort, dstPort);
            if (!_TryAppendUdpStream(in parentField, in context, in connKey, layerKey, isReplay))
            {
                return _MissingStreamEffectError;
            }
        }
        else
        {
            // No enclosing IP/IPv6 layer found — append error field for diagnostics.
            // This can happen with misconfigured tunnels or unusual link-layer encapsulations.
            parentField.Append(_NoIpLayerFieldId,
                FieldValue.NewString("No enclosing IPv4/IPv6 layer found for stream tracking"));
            context.RecordGroupPresence(_UdpErrorGroupId);
        }

        // Checksum verification is decided here, after the enclosing layer is known (needed for index group recording; the
        // status field itself is appended further below once the UDP container exists, so the result is computed once).
        // A zero checksum means "not computed" over IPv4 (RFC 768), so there is nothing to verify and no status field. Over
        // IPv6 the checksum is mandatory (RFC 8200 section 8.1) and a zero is invalid, so it is verified and reported as bad
        // instead of being silently treated as "not computed".
        bool checksumVerified = _VerifyChecksum && (checksum != 0 || enclosedByIpv6);
        if (checksumVerified)
        {
            context.RecordGroupPresence(_UdpChecksumStatusGroupId);
        }

        // A length below the 8-byte header cannot be right (RFC 768). It is reported by name instead of being accepted as
        // "no payload" without a sign.
        bool lengthBelowHeader = length < UdpHeader.HeaderSize;
        if (lengthBelowHeader)
        {
            context.RecordGroupPresence(_UdpErrorGroupId);
        }

        // Packet info is deferred: only the value-typed ports are captured (one state box, a static lambda, no closure) and the
        // text is built if the info column is read.
        // Set packet info so the info column reflects the transport layer ports.
        // Higher-level protocols (DNS, HTTP, etc.) can overwrite this later.
        parentField.SetPacketInfo(LazyString.FormatLazy(
            (srcPort, dstPort),
            static s => string.Create(CultureInfo.InvariantCulture, $"Src Port: {s.srcPort}, Dst Port: {s.dstPort}")));

        // Store the full UDP datagram (header + payload) in the field value (a slice, no copy).
        // The CustomRepresentation still shows "8 bytes" (the header size) to the user; it is a constant string, so
        // creating the LazyString allocates nothing.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        MutField udpContainer = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, "User Datagram Protocol, Src Port: ", srcPort, ", Dst Port: ", dstPort);

        // UDP is fully eager: every descriptive field is appended during Parse() so that
        // index group recording and downstream filtering never depend on materialisation.
        // The any-match name "udp.port" is exposed via the alias group registered in
        // _RegisterFieldsCustom; no duplicate udp.port field node is appended.
        udpContainer.Append(_SrcPortFieldId, FieldValue.NewU64(srcPort));
        udpContainer.Append(_DstPortFieldId, FieldValue.NewU64(dstPort));

        string csumText = DisplayTables.FormatHexU16(checksum);
        udpContainer.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText);

        if (checksumVerified)
        {
            bool? checksumValid = _ValidateChecksum(in udpContainer, data.Span, length, in context);
            string statusText = checksumValid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            udpContainer.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        udpContainer.Append(_LengthFieldId, FieldValue.NewU64(length));

        if (lengthBelowHeader)
        {
            parentField.Append(_BadLengthFieldId,
                FieldValue.NewString("UDP length is smaller than the 8-byte UDP header"));
        }

        if (payloadLen > 0)
        {
            udpContainer.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));
        }

        // Dispatch by port on parentField (sibling dispatch — reuse payloadData, no copy). The lower port is tried first
        // (the server side is usually the well-known, lower port); the higher port is only tried if the lower one consumed
        // nothing. Unlike TCP there is no connection state, hence no learned server port and no reassembly; the dispatch is
        // stateless, so replays need no recorded dispatch decision. The optional heuristic table runs last.
        if (payloadLen > 0)
        {
            ushort lowPort = Math.Min(srcPort, dstPort);
            ushort highPort = Math.Max(srcPort, dstPort);

            // Publish the ports for a parser reached through the heuristic table (it gets no dispatch key). Same thread, same
            // packet; withdrawn in the finally below so no later parser can mistake it for its own packet's ports.
            UdpPortCache.Publish(parentField.Packet.Id, srcPort, dstPort);
            try
            {
                ParseResult result = parentField.TryCallNextProtocolU64(_PortTableId, lowPort, payloadData, in context);
                if (result.TryPropagateError(out ParseResult error))
                {
                    return error;
                }

                bool consumed = result.TryGetConsumed(out _);
                if (!consumed && lowPort != highPort)
                {
                    ParseResult highResult = parentField.TryCallNextProtocolU64(_PortTableId, highPort, payloadData, in context);
                    if (highResult.TryPropagateError(out ParseResult highError))
                    {
                        return highError;
                    }

                    consumed = highResult.TryGetConsumed(out _);
                }

                // Content-based fallback, like TCP: only when no port parser consumed anything. Stateless (UDP keeps no
                // connection state), so a replay runs the same test and reaches the same parser; a callee that keeps its
                // own cross-packet state records its own replay effects.
                if (!consumed && _HeuristicTableId.IsValid)
                {
                    ParseResult heuristic = parentField.TryCallHeuristicProtocol(_HeuristicTableId, payloadData, in context);
                    if (heuristic.TryPropagateError(out ParseResult heuristicError))
                    {
                        return heuristicError;
                    }
                }
            }
            finally
            {
                UdpPortCache.Clear();
            }
        }

        // Consumed bytes: the declared UDP length, capped to what was captured, but never less than the header that was read:
        // a bogus length below 8 must not be reported as fewer consumed bytes than the header itself occupies.
        return Math.Min(Math.Max((int)length, UdpHeader.HeaderSize), data.Length);
    }

    /// <summary>
    /// Validates the UDP checksum (RFC 768: ones' complement sum over the pseudo-header, header and data, which is zero
    /// for a valid datagram). The IP addresses come from the per-thread address caches first, choosing the cache that
    /// matches the calling IP layer (tunnels can leave both filled for one packet); only when no cache matches is the
    /// field tree walked backwards.
    /// Returns <see langword="true"/> if valid, <see langword="false"/> if invalid,
    /// or <see langword="null"/> if no IP layer was found.
    /// </summary>
    /// <param name="container">The UDP container, used for the packet id and the sibling walk.</param>
    /// <param name="udpSpan">The datagram bytes starting at the UDP header.</param>
    /// <param name="udpLength">The UDP length field; used for the pseudo-header and to bound the summed bytes.</param>
    /// <param name="context">Parse context carrying the calling IP protocol.</param>
    /// <returns>The validation result, or <see langword="null"/> when the IP addresses cannot be determined.</returns>
    /// <remarks>
    /// Summing stops at the declared length (capped to the captured bytes). A datagram truncated by the capture
    /// therefore sums fewer bytes than the pseudo-header announces and is reported as bad, not as unverified.
    /// </remarks>
    private bool? _ValidateChecksum(
        in MutField container, ReadOnlySpan<byte> udpSpan, ushort udpLength, in ParseContext context)
    {
        int segmentLen = Math.Min(udpLength, udpSpan.Length);

        // Same cache selection rule as in _ParseBody: the calling IP layer decides which cache is consulted first, so the
        // inner addresses of a tunnel are used instead of the outer ones.
        PacketId packetId = container.Packet.Id;
        bool callerIsIpv4 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv4ProtocolId;
        bool callerIsIpv6 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv6ProtocolId;

        (IPv4Address Src, IPv4Address Dst)? ipv4 = null;
        (IPv6Address Src, IPv6Address Dst)? ipv6 = null;
        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            ipv4 = (src4, dst4);
        }
        else if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
        {
            ipv6 = (src6, dst6);
        }
        else if (!container.HasFieldTree
            || !IpAddressExtractor.TryFindPreviousIpAddresses(
                in container,
                _IpContainerFieldId, _Ipv6ContainerFieldId,
                _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
                out ipv4,
                out ipv6))
        {
            return null;
        }

        // Over IPv6 a zero checksum is invalid (RFC 8200 section 8.1; only IPv4 allows "not computed"), so it is bad without
        // summing: a coincidental sum of zero must not turn it into [Good].
        if (ipv6.HasValue && BinaryPrimitives.ReadUInt16BigEndian(udpSpan[6..]) == 0)
        {
            return false;
        }

        // Pseudo-header sum with the UDP length field (not the captured length), then fold in the captured datagram.
        ulong pseudoSum;
        if (ipv4.HasValue)
        {
            pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                ipv4.Value.Src.RawValue, ipv4.Value.Dst.RawValue, _UdpProtocolNumber, udpLength);
        }
        else
        {
            IPv6Address srcAddr = ipv6!.Value.Src;
            IPv6Address dstAddr = ipv6!.Value.Dst;
            pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                srcAddr.High, srcAddr.Low, dstAddr.High, dstAddr.Low, _UdpProtocolNumber, udpLength);
        }

        ushort result = InternetChecksum.ComputeWithPseudoHeader(udpSpan[..segmentLen], pseudoSum);
        return result == 0;
    }

    #endregion

    #region Stream tracking helpers

    /// <summary>
    /// Appends <c>udp.stream</c>. On a first parse the index comes from the stream tracker and is
    /// recorded for this layer; on a replay it comes from the recorded effect and the tracker stays
    /// untouched. Returns <see langword="false"/> when a replay finds no effect for this layer, so
    /// the caller degrades instead of falling back to a tracker mutation on a reader thread.
    /// </summary>
    private bool _TryAppendUdpStream(
        in MutField parentField,
        in ParseContext context,
        in UdpConnectionKey connKey,
        int layerKey,
        bool isReplay)
    {
        if (isReplay)
        {
            if (!_TryGetStreamEffect(parentField.Packet.Id, layerKey, out StreamEffect recorded))
            {
                return false;
            }

            if (recorded.IsLimitReached)
            {
                _AppendStreamLimit(in parentField, in context);
                return true;
            }

            parentField.Append(_StreamFieldId, FieldValue.NewU64(recorded.StreamIndex));
            context.RecordGroupPresence(_UdpStreamGroupId);
            return true;
        }

        // The setting is a ulong while the tracker compares against a Dictionary count (int), so values from int.MaxValue up
        // are clamped instead of overflowing the cast.
        int maxStreams = _MaxTrackedStreams >= int.MaxValue
            ? int.MaxValue
            : (int)_MaxTrackedStreams;

        // A full table is an expected failure: the datagram is still parsed, but it gets no stream index. The outcome is
        // recorded like an index, so a replay reproduces it even if the table had room later.
        if (!_StreamTracker.TryGetOrCreateStreamIndex(in connKey, maxStreams, out uint streamIndex))
        {
            _AppendStreamLimit(in parentField, in context);
            _RecordStreamEffect(parentField.Packet.Id, layerKey, StreamEffect.LimitReached);
            return true;
        }

        StreamEffect effect = new(streamIndex);
        parentField.Append(_StreamFieldId, FieldValue.NewU64(effect.StreamIndex));
        context.RecordGroupPresence(_UdpStreamGroupId);
        _RecordStreamEffect(parentField.Packet.Id, layerKey, effect);
        return true;
    }

    /// <summary>Reports that the stream table is full: names the setting, so the user knows which limit was hit.</summary>
    private void _AppendStreamLimit(in MutField parentField, in ParseContext context)
    {
        parentField.Append(_StreamLimitFieldId, FieldValue.NewString(
            "UDP stream table is full (setting udp.max_tracked_streams); no stream index assigned"));
        context.RecordGroupPresence(_UdpErrorGroupId);
    }

    #endregion

    #region Fallback IP Address Lookup (sibling walk)

    /// <summary>
    /// Fallback: walks previous siblings to find IPv4 addresses when the
    /// per-protocol thread-local caches do not contain cached data. Walking from the last sibling finds the
    /// immediately enclosing IP layer, which is the correct one in tunnels. Requires a field tree.
    /// </summary>
    private bool _TryFindPreviousIpAddressesFallback(
        in MutField parentField, out IPv4Address src, out IPv4Address dst, in ParseContext context)
    {
        // materialize: false — IP containers are already eager at parse time.
        if (parentField.TryGetLastChild(out MutField prev, materialize: false)
            && IpAddressExtractor.TryFindPreviousIpAddresses(in prev,
                _IpContainerFieldId, _Ipv6ContainerFieldId,
                _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
                out (IPv4Address Src, IPv4Address Dst)? ipv4, out _)
            && ipv4.HasValue)
        {
            src = ipv4.Value.Src;
            dst = ipv4.Value.Dst;
            return true;
        }
        src = default;
        dst = default;
        return false;
    }

    /// <summary>
    /// Fallback: walks previous siblings to find IPv6 addresses when the
    /// per-protocol thread-local caches do not contain cached data.
    /// </summary>
    private bool _TryFindPreviousIpv6AddressesFallback(
        in MutField parentField, out IPv6Address src, out IPv6Address dst, in ParseContext context)
    {
        // materialize: false — IP containers are already eager at parse time.
        if (parentField.TryGetLastChild(out MutField prev, materialize: false)
            && IpAddressExtractor.TryFindPreviousIpAddresses(in prev,
                _IpContainerFieldId, _Ipv6ContainerFieldId,
                _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
                out _, out (IPv6Address Src, IPv6Address Dst)? ipv6)
            && ipv6.HasValue)
        {
            src = ipv6.Value.Src;
            dst = ipv6.Value.Dst;
            return true;
        }
        src = default;
        dst = default;
        return false;
    }

    #endregion
}

/// <summary>
/// UDP header (8 bytes).
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |          Source Port          |       Destination Port        |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |            Length             |           Checksum            |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset reads with <see cref="BinaryPrimitives"/>) instead of a generated
/// parser, so the JIT elides the per-read bounds checks and the dissector gets plain integers; declaration order
/// mirrors the wire layout for readability only. Thread safety: immutable value type.
/// </remarks>
internal readonly struct UdpHeader
{
    /// <summary>Source port number (bytes 0..1).</summary>
    public ushort SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number (bytes 2..3).</summary>
    public ushort DstPort
    {
        get; init;
    }

    /// <summary>Length of the UDP datagram (header + payload) in bytes (bytes 4..5).</summary>
    public ushort Length
    {
        get; init;
    }

    /// <summary>One's complement checksum over pseudo-header, header, and payload (bytes 6..7).</summary>
    public ushort Checksum
    {
        get; init;
    }

    /// <summary>Serialized header size in bytes (8).</summary>
    internal const int HeaderSize = 8;

    /// <summary>
    /// Decodes the 8-byte header. Returns <see langword="false"/> with a default value when fewer than
    /// <see cref="HeaderSize"/> bytes are present. Longer input is accepted; only the first 8 bytes are read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out UdpHeader value)
    {
        // One length check up front; a short datagram is an expected failure, not an exception.
        if (source.Length < HeaderSize)
        {
            value = default;
            return false;
        }

        // Exact-size slice: after this the JIT knows every index below is in range and drops the bounds checks.
        ReadOnlySpan<byte> h = source[..HeaderSize];
        value = new UdpHeader
        {
            SrcPort = BinaryPrimitives.ReadUInt16BigEndian(h),          // 0..2
            DstPort = BinaryPrimitives.ReadUInt16BigEndian(h[2..]),     // 2..4
            Length = BinaryPrimitives.ReadUInt16BigEndian(h[4..]),      // 4..6
            Checksum = BinaryPrimitives.ReadUInt16BigEndian(h[6..]),    // 6..8
        };
        return true;
    }
}
