// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Protocols.Icmpv6;

namespace NetworkInspector.Protocols;

/// <summary>
/// Internet Control Message Protocol for IPv6 (RFC 4443) parser with optional checksum validation.
/// <para>Field tree structure:</para>
/// <code>
/// icmpv6: Internet Control Message Protocol v6
/// ├── icmpv6.type: 128 (Echo Request)
/// ├── icmpv6.code: 0
/// ├── icmpv6.checksum: 0xabcd
/// ├── icmpv6.checksum.status: [Good]          [optional, when verification enabled]
/// ├── icmpv6.echo.identifier: 0x1234         [optional, echo only]
/// ├── icmpv6.echo.sequence_number: 1         [optional, echo only]
/// └── icmpv6.data: (32 bytes)                [optional, payload]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("icmpv6", "Internet Control Message Protocol v6", Description = "ICMPv6 (RFC 4443)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class Icmpv6Protocol : IProtocol
{
    #region Constants

    /// <summary>IP protocol number for ICMPv6 (58).</summary>
    public const ulong IpProtoKey = 58;

    /// <summary>ICMPv6 protocol number for pseudo-header computation.</summary>
    private const byte Icmpv6ProtocolNumber = 58;

    /// <summary>ICMPv6 header size in bytes (always 8).</summary>
    private const int HeaderSize = 8;

    /// <summary>Index group for always-present ICMPv6 fields.</summary>
    private const string Icmpv6IndexGroup = "icmpv6";

    /// <summary>ICMPv6 type: Echo Request.</summary>
    private const byte TypeEchoRequest = 128;

    /// <summary>ICMPv6 type: Echo Reply.</summary>
    private const byte TypeEchoReply = 129;

    /// <summary>ICMPv6 type: Multicast Listener Query (RFC 2710).</summary>
    private const byte TypeMldQuery = 130;

    /// <summary>ICMPv6 type: Multicast Listener Report (RFC 2710).</summary>
    private const byte TypeMldReport = 131;

    /// <summary>ICMPv6 type: Multicast Listener Done (RFC 2710).</summary>
    private const byte TypeMldDone = 132;

    #endregion

    #region Fields

    [BytesField("icmpv6", "ICMPv6", IndexGroup = Icmpv6IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("icmpv6.type", "Type", IndexGroup = Icmpv6IndexGroup)]
    private FieldId _TypeFieldId;

    [U64Field("icmpv6.code", "Code", IndexGroup = Icmpv6IndexGroup)]
    private FieldId _CodeFieldId;

    [U64Field("icmpv6.checksum", "Checksum", IndexGroup = Icmpv6IndexGroup)]
    private FieldId _ChecksumFieldId;

    // Optional: checksum validation status
    [StringField("icmpv6.checksum.status", "Checksum Status", IndexGroup = "icmpv6.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Optional: echo request/reply fields
    [U64Field("icmpv6.echo.identifier", "Identifier", IndexGroup = "icmpv6.echo")]
    private FieldId _EchoIdentFieldId;

    [U64Field("icmpv6.echo.sequence_number", "Sequence Number", IndexGroup = "icmpv6.echo")]
    private FieldId _EchoSeqFieldId;

    // Optional: payload data
    [BytesField("icmpv6.data", "Data", IndexGroup = "icmpv6.data")]
    private FieldId _DataFieldId;

    #endregion

    #region MLD fields (Multicast Listener Discovery — types 130–132)

    [U64Field("icmpv6.mld.maximum_response_delay", "Maximum Response Delay", IndexGroup = "icmpv6.mld")]
    private FieldId _MldMaxResponseDelayFieldId;

    [IPv6Field("icmpv6.mld.multicast_address", "Multicast Address", IndexGroup = "icmpv6.mld")]
    private FieldId _MldMulticastAddressFieldId;

    #endregion

    #region NDP fields (Neighbor Discovery Protocol — types 133-137)

    // Router Advertisement
    [U64Field("icmpv6.nd.ra.cur_hop_limit", "Current Hop Limit", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaCurHopLimitFieldId;

    [NoneField("icmpv6.nd.ra.flags", "Flags", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagsFieldId;

    [BoolField("icmpv6.nd.ra.flag.managed", "Managed Address Configuration", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagManagedFieldId;

    [BoolField("icmpv6.nd.ra.flag.other", "Other Configuration", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagOtherFieldId;

    [U64Field("icmpv6.nd.ra.router_lifetime", "Router Lifetime", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaRouterLifetimeFieldId;

    [U64Field("icmpv6.nd.ra.reachable_time", "Reachable Time", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaReachableTimeFieldId;

    [U64Field("icmpv6.nd.ra.retrans_timer", "Retransmission Timer", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaRetransTimerFieldId;

    // Target address (shared by NS, NA, Redirect)
    [IPv6Field("icmpv6.nd.target_address", "Target Address", IndexGroup = "icmpv6.nd.target")]
    private FieldId _NdTargetAddressFieldId;

    // Neighbor Advertisement flags
    [NoneField("icmpv6.nd.na.flags", "Flags", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagsFieldId;

    [BoolField("icmpv6.nd.na.flag.router", "Router", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagRouterFieldId;

    [BoolField("icmpv6.nd.na.flag.solicited", "Solicited", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagSolicitedFieldId;

    [BoolField("icmpv6.nd.na.flag.override", "Override", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagOverrideFieldId;

    // Redirect destination
    [IPv6Field("icmpv6.nd.redirect.dst", "Destination Address", IndexGroup = "icmpv6.nd.redirect")]
    private FieldId _NdRedirectDstFieldId;

    // NDP options
    [NoneField("icmpv6.nd.opt", "Option", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptContainerFieldId;

    [U64Field("icmpv6.nd.opt.type", "Type", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptTypeFieldId;

    [U64Field("icmpv6.nd.opt.len", "Length", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLenFieldId;

    [MacField("icmpv6.nd.opt.linkaddr", "Link-Layer Address", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLinkAddrFieldId;

    [U64Field("icmpv6.nd.opt.prefix.length", "Prefix Length", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixLengthFieldId;

    [BoolField("icmpv6.nd.opt.prefix.flag.onlink", "On-Link", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagOnLinkFieldId;

    [BoolField("icmpv6.nd.opt.prefix.flag.auto", "Autonomous", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagAutoFieldId;

    [U64Field("icmpv6.nd.opt.prefix.valid_lifetime", "Valid Lifetime", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixValidLifetimeFieldId;

    [U64Field("icmpv6.nd.opt.prefix.preferred_lifetime", "Preferred Lifetime", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixPreferredLifetimeFieldId;

    [IPv6Field("icmpv6.nd.opt.prefix", "Prefix", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFieldId;

    [U64Field("icmpv6.nd.opt.mtu", "MTU", IndexGroup = "icmpv6.nd.opt.mtu")]
    private FieldId _NdOptMtuFieldId;

    [U64Field("icmpv6.nd.opt.rdnss.lifetime", "Lifetime", IndexGroup = "icmpv6.nd.opt.rdnss")]
    private FieldId _NdOptRdnssLifetimeFieldId;

    [IPv6Field("icmpv6.nd.opt.rdnss", "DNS Server", IndexGroup = "icmpv6.nd.opt.rdnss")]
    private FieldId _NdOptRdnssAddressFieldId;

    // Settings
    [BoolSetting("icmpv6.verify_checksum", "Verify Checksum", "icmpv6", Default = false)]
    private bool _VerifyChecksum;

    #endregion

    #region Cross-Protocol Field References
    private FieldId _Ipv6SrcFieldId;
    private FieldId _Ipv6DstFieldId;

    // Pre-allocated populator delegate
    private LazyPopulator _Populator = null!;

    /// <summary>NDP field IDs struct populated in OnStartCustom.</summary>
    private Icmpv6NdpFieldIds _NdpFieldIds;

    partial void OnStartCustom(Stack stack)
    {
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? default;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? default;
        _Populator = (in MutField container) => PopulateIcmpv6Fields(in container);

        // Populate NDP field IDs struct
        _NdpFieldIds = new Icmpv6NdpFieldIds
        {
            RaCurHopLimit = _NdRaCurHopLimitFieldId,
            RaFlags = _NdRaFlagsFieldId,
            RaFlagManaged = _NdRaFlagManagedFieldId,
            RaFlagOther = _NdRaFlagOtherFieldId,
            RaRouterLifetime = _NdRaRouterLifetimeFieldId,
            RaReachableTime = _NdRaReachableTimeFieldId,
            RaRetransTimer = _NdRaRetransTimerFieldId,
            TargetAddress = _NdTargetAddressFieldId,
            NaFlags = _NdNaFlagsFieldId,
            NaFlagRouter = _NdNaFlagRouterFieldId,
            NaFlagSolicited = _NdNaFlagSolicitedFieldId,
            NaFlagOverride = _NdNaFlagOverrideFieldId,
            RedirectDstAddress = _NdRedirectDstFieldId,
            OptContainer = _NdOptContainerFieldId,
            OptType = _NdOptTypeFieldId,
            OptLen = _NdOptLenFieldId,
            OptLinkAddr = _NdOptLinkAddrFieldId,
            OptPrefixLength = _NdOptPrefixLengthFieldId,
            OptPrefixFlagOnLink = _NdOptPrefixFlagOnLinkFieldId,
            OptPrefixFlagAuto = _NdOptPrefixFlagAutoFieldId,
            OptPrefixValidLifetime = _NdOptPrefixValidLifetimeFieldId,
            OptPrefixPreferredLifetime = _NdOptPrefixPreferredLifetimeFieldId,
            OptPrefix = _NdOptPrefixFieldId,
            OptMtu = _NdOptMtuFieldId,
            OptRdnssLifetime = _NdOptRdnssLifetimeFieldId,
            OptRdnssAddress = _NdOptRdnssAddressFieldId,
        };
    }

    /// <summary>
    /// Populates ICMPv6 child fields lazily from stored datagram bytes.
    /// </summary>
    private ParseResult PopulateIcmpv6Fields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> icmpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (icmpData.Length < HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, HeaderSize, (ulong)icmpData.Length);
        }

        ReadOnlySpan<byte> span = icmpData.Span;
        byte type = span[0];
        byte code = span[1];
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);

        // Type with display text
        string typeText = DisplayTables.GetIcmpv6TypeDisplayText(type);
        container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(type), typeText, in context);

        // Code
        container.Append(_CodeFieldId, FieldValue.NewU64(code), in context);

        // Checksum
        string csumText = DisplayTables.FormatHexU16(checksum);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText, in context);

        // Checksum validation (uses IPv6 pseudo-header)
        if (_VerifyChecksum)
        {
            bool valid = ValidateChecksum(in container, icmpData.Span, in context);
            string statusText = valid ? "[Good]" : "[Bad]";
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText), in context);
        }

        // Echo Request/Reply: identifier and sequence number
        bool isEcho = type == TypeEchoRequest || type == TypeEchoReply;
        if (isEcho)
        {
            ushort ident = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);

            string identHex = DisplayTables.FormatHexU16(ident);
            container.AppendWithCustomText(_EchoIdentFieldId, FieldValue.NewU64(ident), identHex, in context);
            container.Append(_EchoSeqFieldId, FieldValue.NewU64(seq), in context);
        }

        // MLD messages (types 130-132): parse multicast listener fields
        bool isMld = type is >= TypeMldQuery and <= TypeMldDone;
        if (isMld && icmpData.Length >= 24)
        {
            // MLD message body starts at byte 4: MaxResponseDelay(2) + Reserved(2) + MulticastAddress(16)
            ushort maxResponseDelay = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            container.Append(_MldMaxResponseDelayFieldId, FieldValue.NewU64(maxResponseDelay), in context);

            // Multicast address at bytes 8-23
            ulong high = BinaryPrimitives.ReadUInt64BigEndian(span[8..16]);
            ulong low = BinaryPrimitives.ReadUInt64BigEndian(span[16..24]);
            container.Append(_MldMulticastAddressFieldId, FieldValue.NewIPv6(new IPv6Address(high, low)), in context);
        }

        // NDP messages (types 133-137): parse after the 4-byte ICMPv6 header
        if (Icmpv6NdpParser.IsNdpType(type) && icmpData.Length > 4)
        {
            Icmpv6NdpParser.Parse(in container, span[4..], type, in _NdpFieldIds, in context);
        }

        // Payload data (bytes after the 8-byte header, only for non-NDP/non-Echo/non-MLD messages)
        if (!isEcho && !isMld && !Icmpv6NdpParser.IsNdpType(type) && icmpData.Length > HeaderSize)
        {
            ReadOnlyMemory<byte> payloadData = icmpData[HeaderSize..];
            container.Append(_DataFieldId, FieldValue.NewBytes(payloadData), in context);
        }

        return 0;
    }

    /// <summary>
    /// Parses a Icmpv6 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Icmpv6GroupId);

        ReadOnlySpan<byte> span = data.Span;
        byte type = span[0];

        // Record optional index groups
        bool isEcho = type == TypeEchoRequest || type == TypeEchoReply;
        if (isEcho)
        {
            context.RecordGroupPresence(_Icmpv6EchoGroupId);
        }

        // Record NDP index groups based on message type
        if (Icmpv6NdpParser.IsNdpType(type))
        {
            context.RecordGroupPresence(_Icmpv6NdOptGroupId);
            switch (type)
            {
                case 134: // Router Advertisement
                    context.RecordGroupPresence(_Icmpv6NdRaGroupId);
                    break;
                case 135: // Neighbor Solicitation
                case 136: // Neighbor Advertisement
                case 137: // Redirect
                    context.RecordGroupPresence(_Icmpv6NdTargetGroupId);
                    break;
            }
            if (type == 136)
            {
                context.RecordGroupPresence(_Icmpv6NdNaGroupId);
            }
            if (type == 137)
            {
                context.RecordGroupPresence(_Icmpv6NdRedirectGroupId);
            }
        }

        // Record MLD index group
        bool isMld = type is >= TypeMldQuery and <= TypeMldDone;
        if (isMld)
        {
            context.RecordGroupPresence(_Icmpv6MldGroupId);
        }

        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_Icmpv6ChecksumStatusGroupId);
        }

        if (!isEcho && !isMld && !Icmpv6NdpParser.IsNdpType(type) && data.Length > HeaderSize)
        {
            context.RecordGroupPresence(_Icmpv6DataGroupId);
        }

        // Summary text
        LazyString summary = ZA.Lazy(
            "Internet Control Message Protocol v6, ",
            DisplayTables.GetIcmpv6TypeDisplayText(type));

        // Packet info
        parentField.SetPacketInfo(new LazyString(
            DisplayTables.GetIcmpv6TypeDisplayText(type)));

        // Store entire ICMPv6 message for lazy populator
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString("8 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Validates the ICMPv6 checksum using IPv6 pseudo-header.
    /// ICMPv6 checksum is mandatory and uses the IPv6 pseudo-header (src, dst, length, next header).
    /// </summary>
    private bool ValidateChecksum(in MutField container, ReadOnlySpan<byte> icmpSpan, in ParseContext context)
    {
        Packet packet = container.Packet;

        if (!_Ipv6SrcFieldId.IsValid
            || !packet.TryGetFieldValue(_Ipv6SrcFieldId, out FieldValue ipv6Src)
            || !packet.TryGetFieldValue(_Ipv6DstFieldId, out FieldValue ipv6Dst)
            || ipv6Src.Type != FieldType.IPv6Address
            || ipv6Dst.Type != FieldType.IPv6Address)
        {
            return false; // No IPv6 layer found
        }

        if (!ipv6Src.Data.TryGetAsIPv6(out IPv6Address srcAddr)
            || !ipv6Dst.Data.TryGetAsIPv6(out IPv6Address dstAddr))
        {
            return false; // Type mismatch — cannot extract addresses
        }

        // Compute pseudo-header sum directly from ulong high/low halves (no stackalloc / byte conversion)
        ushort icmpLen = (ushort)icmpSpan.Length;
        ulong pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
            srcAddr.High, srcAddr.Low, dstAddr.High, dstAddr.Low, Icmpv6ProtocolNumber, icmpLen);

        ushort result = InternetChecksum.ComputeWithPseudoHeader(icmpSpan, pseudoSum);
        return result == 0;
    }
    #endregion
}
