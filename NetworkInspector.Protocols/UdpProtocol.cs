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
/// ├── udp.port: 12345                          [any-match, appended for both src and dst]
/// ├── udp.port: 53                             [any-match, appended for both src and dst]
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

    // Pre-computed pseudo-header sum, eagerly appended in Parse() when checksum
    // verification is enabled. Avoids re-walking siblings in the lazy populator.
    [U64Field("udp.pseudo_sum", "Pseudo-Header Sum", IndexGroup = "udp.checksum.status")]
    private FieldId _PseudoHeaderSumFieldId;

    // Combined port field (Wireshark udp.port compatibility).
    // Appended twice per datagram — once for source and once for destination — so that
    // filter expressions like `udp.port == 53` match either endpoint.
    [U64Field("udp.port", "Port", IndexGroup = UdpIndexGroup)]
    private FieldId _PortFieldId;

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

    // Second lazy group container: stores header bytes for the details populator.
    // Groups length and payload separately from the identifying port and checksum fields.
    [BytesField("udp.hdr", "Header Details", IndexGroup = UdpIndexGroup)]
    private FieldId _HdrDetailsFieldId;

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
        _IpContainerFieldId = stack.GetFieldId("ip") ?? default;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? default;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? default;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? default;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? default;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? default;
        _Ipv4ProtocolId = stack.GetProtocolId("ip") ?? default;
        _Ipv6ProtocolId = stack.GetProtocolId("ipv6") ?? default;

        // Pre-allocate the populator delegate so it is reused for every packet.
        // Captures only `this` (singleton) — zero per-packet closure allocation.
        _Populator = (in MutField container) => PopulateUdpPrimary(in container);
        _DetailsPopulator = (in MutField container) => PopulateUdpDetails(in container);
    }

    /// <summary>
    /// Primary lazy group: port and checksum fields for the UDP datagram.
    /// Registers <c>udp.hdr</c> as a second lazy group containing length and payload.
    /// Called on first access of the UDP container's children.
    /// <para>Checksum validation stays here because <see cref="ValidateChecksum"/> requires
    /// this container (the UDP container) to walk previous siblings for the pre-computed
    /// pseudo-header sum appended in <see cref="Parse"/>.</para>
    /// </summary>
    private ParseResult PopulateUdpPrimary(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> udpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (udpData.Length < UdpHeader.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)udpData.Length);
        }

        ReadOnlySpan<byte> headerSpan = udpData.Span[..UdpHeader.HeaderSize];
        if (!UdpHeader.TryParse(headerSpan, out UdpHeader header, out _))
        {
            return ParseError.InvalidData(ProtocolName, "Failed to parse UDP header");
        }

        ushort srcPort = header.SrcPort.Value;
        ushort dstPort = header.DstPort.Value;
        ushort length = header.Length.Value;
        ushort checksum = header.Checksum.Value;

        // Port fields first — filter expressions like `udp.srcport == X` find them
        // without walking past the other header fields.
        container.Append(_SrcPortFieldId, FieldValue.NewU64(srcPort), in context);
        container.Append(_DstPortFieldId, FieldValue.NewU64(dstPort), in context);
        container.Append(_PortFieldId, FieldValue.NewU64(srcPort), in context);
        container.Append(_PortFieldId, FieldValue.NewU64(dstPort), in context);

        // Checksum stays in this (primary) group: ValidateChecksum walks previous siblings
        // of this UDP container to find the pre-computed pseudo-header sum from Parse().
        string csumText = DisplayTables.FormatHexU16(checksum);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText, in context);

        bool checksumVerified = _VerifyChecksum && checksum != 0;
        if (checksumVerified)
        {
            bool? checksumValid = ValidateChecksum(in container, udpData.Span, length, in context);
            string statusText = checksumValid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText), in context);
        }

        // Register the second lazy group for length and payload.
        // Stores the full datagram bytes so the details populator can read them directly.
        container.AppendLazyWithCustomText(
            _HdrDetailsFieldId,
            FieldValue.NewBytes(udpData),
            new LazyString("Header Details"),
            _DetailsPopulator);

        return 0;
    }

    /// <summary>
    /// Details lazy group: length and payload fields for the UDP datagram.
    /// Fires only when <c>udp.hdr</c> children are accessed (e.g., during full materialisation).
    /// </summary>
    private ParseResult PopulateUdpDetails(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> udpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (udpData.Length < UdpHeader.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)udpData.Length);
        }

        ReadOnlySpan<byte> headerSpan = udpData.Span[..UdpHeader.HeaderSize];
        if (!UdpHeader.TryParse(headerSpan, out UdpHeader header, out _))
        {
            return ParseError.InvalidData(ProtocolName, "Failed to parse UDP header");
        }

        ushort length = header.Length.Value;
        container.Append(_LengthFieldId, FieldValue.NewU64(length), in context);

        // Reconstruct the payload slice from the full stored datagram data.
        int payloadLen = Math.Max(0, Math.Min(length - UdpHeader.HeaderSize, udpData.Length - UdpHeader.HeaderSize));
        if (payloadLen > 0)
        {
            ReadOnlyMemory<byte> payloadData = udpData.Slice(UdpHeader.HeaderSize, payloadLen);
            container.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData), in context);
        }

        return 0;
    }

    // Pre-allocated delegates — set once in OnStartCustom.
    private LazyPopulator _Populator = null!;
    private LazyPopulator _DetailsPopulator = null!;

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

        // UDP-02: Validate checksum at parse time (needed for index group recording).
        // The actual validation result is re-computed inside PopulateUdpFields at
        // materialisation time to avoid capturing it in a per-packet closure.
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
            // IPv4: build connection key and optional pseudo-header sum directly
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(src4.RawValue, dst4.RawValue, srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex), in context);
            context.RecordGroupPresence(_UdpStreamGroupId);

            if (checksumVerified)
            {
                ulong pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                    src4.RawValue, dst4.RawValue, UdpProtocolNumber, length);
                parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(pseudoSum), in context);
            }
        }
        else if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
        {
            // IPv6: build connection key and optional pseudo-header sum directly
            UdpConnectionKey connKey = new(new UInt128(src6.High, src6.Low), new UInt128(dst6.High, dst6.Low), srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex), in context);
            context.RecordGroupPresence(_UdpStreamGroupId);

            if (checksumVerified)
            {
                ulong pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                    src6.High, src6.Low, dst6.High, dst6.Low, UdpProtocolNumber, length);
                parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(pseudoSum), in context);
            }
        }
        else if (TryFindPreviousIpAddressesFallback(parentField, out IPv4Address fbSrc4, out IPv4Address fbDst4, in context))
        {
            // Fallback: IPv4 via sibling walk (edge case — cache miss)
            UdpConnectionKey connKey = UdpConnectionKey.FromIPv4(fbSrc4.RawValue, fbDst4.RawValue, srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex), in context);
            context.RecordGroupPresence(_UdpStreamGroupId);

            if (checksumVerified)
            {
                ulong pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                    fbSrc4.RawValue, fbDst4.RawValue, UdpProtocolNumber, length);
                parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(pseudoSum), in context);
            }
        }
        else if (TryFindPreviousIpv6AddressesFallback(parentField, out IPv6Address fbSrc6, out IPv6Address fbDst6, in context))
        {
            // Fallback: IPv6 via sibling walk (edge case — cache miss)
            UdpConnectionKey connKey = new(new UInt128(fbSrc6.High, fbSrc6.Low), new UInt128(fbDst6.High, fbDst6.Low), srcPort, dstPort);
            uint streamIndex = _StreamTracker.GetOrCreateStreamIndex(in connKey);
            parentField.Append(_StreamFieldId, FieldValue.NewU64(streamIndex), in context);
            context.RecordGroupPresence(_UdpStreamGroupId);

            if (checksumVerified)
            {
                ulong pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                    fbSrc6.High, fbSrc6.Low, fbDst6.High, fbDst6.Low, UdpProtocolNumber, length);
                parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(pseudoSum), in context);
            }
        }
        else
        {
            // No enclosing IP/IPv6 layer found — append error field for diagnostics.
            // This can happen with misconfigured tunnels or unusual link-layer encapsulations.
            parentField.Append(_NoIpLayerFieldId,
                FieldValue.NewString("No enclosing IPv4/IPv6 layer found for stream tracking"), in context);
            context.RecordGroupPresence(_UdpErrorGroupId);
        }

        // Summary and packetInfo use ZA.Lazy to defer string formatting.
        LazyString summary = ZA.Lazy(
            "User Datagram Protocol, Src Port: ", srcPort, ", Dst Port: ", dstPort);

        // Set packet info so the info column reflects the transport layer ports.
        // Higher-level protocols (DNS, HTTP, etc.) can overwrite this later.
        parentField.SetPacketInfo(ZA.Lazy("Src Port: ", srcPort, ", Dst Port: ", dstPort));

        // Store the full UDP datagram (header + payload) in the field value so that
        // PopulateUdpFields can reconstruct the payload slice without captured state.
        // The CustomRepresentation still shows "8 bytes" (the header size) to the user.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

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
    /// Validates the UDP checksum. First tries to use the pre-computed pseudo-header
    /// sum eagerly appended in <see cref="Parse"/>. Falls back to previous-sibling
    /// navigation for typed IP address lookup if the pre-computed value is not available.
    /// Returns <see langword="true"/> if valid, <see langword="false"/> if invalid,
    /// or <see langword="null"/> if no IP layer was found.
    /// </summary>
    private bool? ValidateChecksum(in MutField container, ReadOnlySpan<byte> udpSpan, ushort udpLength, in ParseContext context)
    {
        int segmentLen = Math.Min(udpLength, udpSpan.Length);

        // Fast path: use pre-computed pseudo-header sum from Parse().
        // The pseudo-header sum field is 1-2 siblings before the UDP container,
        // so this walk is very short compared to a full IP address lookup.
        if (TryReadPseudoHeaderSum(container.AsField(), out ulong precomputedSum))
        {
            ushort result = InternetChecksum.ComputeWithPseudoHeader(udpSpan[..segmentLen], precomputedSum);
            return result == 0;
        }

        // Fallback: walk previous siblings to find typed IP addresses (handles edge
        // cases where pseudo-header sum was not pre-computed, e.g. no enclosing IP layer)
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

        ushort fallbackResult = InternetChecksum.ComputeWithPseudoHeader(udpSpan[..segmentLen], pseudoSum);
        return fallbackResult == 0;
    }

    /// <summary>
    /// Attempts to read the pre-computed pseudo-header sum from a previous sibling.
    /// The pseudo-header sum field is eagerly appended just before the UDP container
    /// in Parse(), so it is typically 1-2 siblings back.
    /// </summary>
    private bool TryReadPseudoHeaderSum(Field containerField, out ulong pseudoSum)
    {
        // Walk at most 3 siblings back — the pseudo-header sum is always
        // immediately before the container in the sibling list.
        Field field = containerField;
        int maxWalk = 3;
        while (maxWalk-- > 0 && field.TryGetPrev(out field))
        {
            if (field.FieldId == _PseudoHeaderSumFieldId
                && field.Value.Data.TryGetAsU64(out pseudoSum))
            {
                return true;
            }
        }

        pseudoSum = 0;
        return false;
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
