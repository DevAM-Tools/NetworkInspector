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
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("udp", "User Datagram Protocol", Description = "UDP (RFC 768)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class UdpProtocol : IProtocol
{
    #region Table Key Constants

    /// <summary>IP protocol number for UDP (17).</summary>
    public const ulong IpProtoKey = 17;

    /// <summary>IP protocol number value for pseudo-header computation.</summary>
    private const byte UdpProtocolNumber = 17;

    #endregion

    #region Table Name Constants

    /// <summary>Dispatch table name for UDP port-based protocol lookup.</summary>
    public const string PortTableName = "udp.port";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present UDP fields.</summary>
    private const string UdpIndexGroup = "udp";

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("udp", "UDP", IndexGroup = UdpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("udp.srcport", "Source Port", IndexGroup = UdpIndexGroup)]
    private FieldId _SrcPortFieldId;

    [U64Field("udp.dstport", "Destination Port", IndexGroup = UdpIndexGroup)]
    private FieldId _DstPortFieldId;

    [U64Field("udp.length", "Length", IndexGroup = UdpIndexGroup)]
    private FieldId _LengthFieldId;

    [U64Field("udp.checksum", "Checksum", IndexGroup = UdpIndexGroup)]
    private FieldId _ChecksumFieldId;

    // UDP-02: Checksum validation status (optional, only when verification enabled)
    [StringField("udp.checksum.status", "Checksum Status", IndexGroup = "udp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Field alias group ID assigned in RegisterFieldsCustom for "udp.port" -> { udp.srcport, udp.dstport }.
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

    // Port dispatch table
    [ProtocolTableU64(PortTableName, "UDP Port")]
    private ProtocolTableId _PortTableId;

    // UDP-02: Runtime setting for checksum verification
    [BoolSetting("udp.verify_checksum", "Verify Checksum", "udp", Default = false)]
    private bool _VerifyChecksum;

    #endregion

    #region Cross-Protocol Field References
    // Resolved during OnStart via stack.GetFieldId() for reading IP addresses
    // from the field tree via sibling navigation (not flat-array scan).

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
    /// <see cref="Parse"/> is dispatched from an enclosing IPv4 layer.</summary>
    private ProtocolId _Ipv4ProtocolId;

    /// <summary>ProtocolId of the IPv6 protocol ("ipv6"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="Parse"/> is dispatched from an enclosing IPv6 layer.</summary>
    private ProtocolId _Ipv6ProtocolId;

    #endregion

    #region Stream tracking

    /// <summary>Tracks UDP conversations and assigns monotonic stream indices.</summary>
    private readonly UdpStreamTracker _StreamTracker = new();

    /// <summary>Resolves cross-protocol field IDs for checksum computation and stream tracking.</summary>
    partial void OnStartCustom(Stack stack)
    {
        _IpContainerFieldId = stack.GetFieldId("ip") ?? FieldId.Invalid;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? FieldId.Invalid;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? FieldId.Invalid;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? FieldId.Invalid;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? FieldId.Invalid;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? FieldId.Invalid;
        _Ipv4ProtocolId = stack.GetProtocolId("ip") ?? ProtocolId.Invalid;
        _Ipv6ProtocolId = stack.GetProtocolId("ipv6") ?? ProtocolId.Invalid;
    }

    /// <summary>
    /// Registers protocol-owned alias groups. Adds "udp.port" -> { udp.srcport, udp.dstport }
    /// as metadata; the alias is reachable only via the alias-group APIs on IStack and is
    /// independent of the dispatch table also named "udp.port".
    /// </summary>
    partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _PortAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "udp.port",
            "Any-match alias for source/destination UDP ports.",
            [_SrcPortFieldId, _DstPortFieldId]);
    }

    /// <summary>
    /// Parses a Udp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < UdpHeader.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_UdpGroupId);

        ReadOnlySpan<byte> span = data.Span;

        if (!UdpHeader.TryParse(span, out UdpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
        }

        ushort srcPort = header.SrcPort.Value;
        ushort dstPort = header.DstPort.Value;
        ushort length = header.Length.Value;
        ushort checksum = header.Checksum.Value;

        // Compute payload bounds (guard against negative)
        int payloadLen = Math.Max(0, Math.Min(length - UdpHeader.HeaderSize, data.Length - UdpHeader.HeaderSize));
        ReadOnlyMemory<byte> payloadData = payloadLen > 0 ? data.Slice(UdpHeader.HeaderSize, payloadLen) : ReadOnlyMemory<byte>.Empty;

        // Validate checksum at parse time (needed for index group recording).
        // The checksum status field itself is appended eagerly further below once the
        // UDP container exists, so the validation result is computed exactly once.
        bool checksumVerified = _VerifyChecksum && checksum != 0; // UDP checksum 0 means "not computed"
        if (checksumVerified)
        {
            context.RecordGroupPresence(_UdpChecksumStatusGroupId);
        }

        // Record optional payload group if payload exists
        if (payloadLen > 0)
        {
            context.RecordGroupPresence(_UdpPayloadGroupId);
        }

    #endregion

        #region Stream tracking and checksum pre-computation
        // Read IP addresses from the per-protocol thread-local caches (populated by
        // IPv4Protocol/IPv6Protocol during Parse). Falls back to sibling-walk navigation
        // for edge cases (custom stacks, non-standard encapsulations).
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

        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            // IPv4: build connection key
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(src4.RawValue, dst4.RawValue, srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex));
            context.RecordGroupPresence(_UdpStreamGroupId);
        }
        else if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
        {
            // IPv6: build connection key
            UdpConnectionKey connKey = new(new UInt128(src6.High, src6.Low), new UInt128(dst6.High, dst6.Low), srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex));
            context.RecordGroupPresence(_UdpStreamGroupId);
        }
        else if (TryFindPreviousIpAddressesFallback(parentField, out IPv4Address fbSrc4, out IPv4Address fbDst4, in context))
        {
            // Fallback: IPv4 via sibling walk (edge case — cache miss)
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(fbSrc4.RawValue, fbDst4.RawValue, srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex));
            context.RecordGroupPresence(_UdpStreamGroupId);
        }
        else if (TryFindPreviousIpv6AddressesFallback(parentField, out IPv6Address fbSrc6, out IPv6Address fbDst6, in context))
        {
            // Fallback: IPv6 via sibling walk (edge case — cache miss)
            UdpConnectionKey connKey = new(new UInt128(fbSrc6.High, fbSrc6.Low), new UInt128(fbDst6.High, fbDst6.Low), srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex));
            context.RecordGroupPresence(_UdpStreamGroupId);
        }
        else
        {
            // No enclosing IP/IPv6 layer found — append error field for diagnostics.
            // This can happen with misconfigured tunnels or unusual link-layer encapsulations.
            parentField.Append(_NoIpLayerFieldId,
                FieldValue.NewString("No enclosing IPv4/IPv6 layer found for stream tracking"));
            context.RecordGroupPresence(_UdpErrorGroupId);
        }

        // Summary and packetInfo use ZA.Lazy to defer string formatting.
        LazyString summary = ZA.Lazy(
            "User Datagram Protocol, Src Port: ", srcPort, ", Dst Port: ", dstPort);

        // Set packet info so the info column reflects the transport layer ports.
        // Higher-level protocols (DNS, HTTP, etc.) can overwrite this later.
        parentField.SetPacketInfo(ZA.Lazy("Src Port: ", srcPort, ", Dst Port: ", dstPort));

        // Store the full UDP datagram (header + payload) in the field value.
        // The CustomRepresentation still shows "8 bytes" (the header size) to the user.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        MutField udpContainer = parentField.AppendWithCustomText(_ProtocolFieldId, containerValue, summary);

        // UDP is fully eager: every descriptive field is appended during Parse() so that
        // index group recording and downstream filtering never depend on materialisation.
        // The any-match name "udp.port" is exposed via the alias group registered in
        // RegisterFieldsCustom; no duplicate udp.port field node is appended.
        udpContainer.Append(_SrcPortFieldId, FieldValue.NewU64(srcPort));
        udpContainer.Append(_DstPortFieldId, FieldValue.NewU64(dstPort));

        string csumText = DisplayTables.FormatHexU16(checksum);
        udpContainer.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText);

        if (checksumVerified)
        {
            bool? checksumValid = ValidateChecksum(in udpContainer, data.Span, length);
            string statusText = checksumValid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            udpContainer.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        udpContainer.Append(_LengthFieldId, FieldValue.NewU64(length));

        if (payloadLen > 0)
        {
            udpContainer.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));
        }

        // Dispatch by port on parentField (sibling dispatch — reuse payloadData)
        if (payloadLen > 0)
        {
            ushort lowPort = Math.Min(srcPort, dstPort);
            ushort highPort = Math.Max(srcPort, dstPort);

            ParseResult result = parentField.TryCallNextProtocolU64(_PortTableId, lowPort, payloadData, in context);
            if (result.IsError)
            {
                return result;
            }

            bool dispatched = result.Value > 0;
            if (!dispatched && lowPort != highPort)
            {
                ParseResult highResult = parentField.TryCallNextProtocolU64(_PortTableId, highPort, payloadData, in context);
                if (highResult.IsError)
                {
                    return highResult;
                }
            }
        }

        return Math.Min(length, data.Length);
    }

    /// <summary>
    /// Validates the UDP checksum by walking previous siblings to find typed IP addresses.
    /// Returns <see langword="true"/> if valid, <see langword="false"/> if invalid,
    /// or <see langword="null"/> if no IP layer was found.
    /// </summary>
    private bool? ValidateChecksum(in MutField container, ReadOnlySpan<byte> udpSpan, ushort udpLength)
    {
        int segmentLen = Math.Min(udpLength, udpSpan.Length);

        // Walk previous siblings to find typed IP addresses
        Field startField = container.AsField();
        if (!IpAddressExtractor.TryFindPreviousIpAddresses(startField,
            _IpContainerFieldId, _Ipv6ContainerFieldId,
            _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
            out (IPv4Address Src, IPv4Address Dst)? ipv4,
            out (IPv6Address Src, IPv6Address Dst)? ipv6))
        {
            return null; // No IP layer found — cannot validate
        }

        ulong pseudoSum;
        if (ipv4.HasValue)
        {
            pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                ipv4.Value.Src.RawValue, ipv4.Value.Dst.RawValue, UdpProtocolNumber, udpLength);
        }
        else
        {
            // ipv6 is guaranteed non-null when TryFindPreviousIpAddresses returns true
            IPv6Address src6 = ipv6!.Value.Src;
            IPv6Address dst6 = ipv6!.Value.Dst;
            pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                src6.High, src6.Low, dst6.High, dst6.Low, UdpProtocolNumber, udpLength);
        }

        ushort result = InternetChecksum.ComputeWithPseudoHeader(udpSpan[..segmentLen], pseudoSum);
        return result == 0;
    }

        #endregion

    #region Fallback IP Address Lookup (sibling walk)

    /// <summary>
    /// Fallback: walks previous siblings to find IPv4 addresses when the
    /// per-protocol thread-local caches do not contain cached data.
    /// </summary>
    private bool TryFindPreviousIpAddressesFallback(
        in MutField parentField, out IPv4Address src, out IPv4Address dst, in ParseContext context)
    {
        Field root = parentField.AsField();
        if (root.TryGetLastChild(out Field prev)
            && IpAddressExtractor.TryFindPreviousIpAddresses(prev,
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
    private bool TryFindPreviousIpv6AddressesFallback(
        in MutField parentField, out IPv6Address src, out IPv6Address dst, in ParseContext context)
    {
        Field root = parentField.AsField();
        if (root.TryGetLastChild(out Field prev)
            && IpAddressExtractor.TryFindPreviousIpAddresses(prev,
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
[BinaryParsable]
internal readonly partial struct UdpHeader
{
    /// <summary>Source port number.</summary>
    public U16BE SrcPort
    {
        get; init;
    }

    /// <summary>Destination port number.</summary>
    public U16BE DstPort
    {
        get; init;
    }

    /// <summary>Length of the UDP datagram (header + payload) in bytes.</summary>
    public U16BE Length
    {
        get; init;
    }

    /// <summary>One's complement checksum over pseudo-header, header, and payload.</summary>
    public U16BE Checksum
    {
        get; init;
    }

    /// <summary>Serialized header size in bytes (8).</summary>
    internal const int HeaderSize = 8;
}
