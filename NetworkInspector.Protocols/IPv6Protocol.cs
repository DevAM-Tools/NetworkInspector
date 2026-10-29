// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Internet Protocol version 6 (RFC 8200) parser with extension header chain support
/// and IPv6 fragment reassembly.
/// <para>Field tree structure:</para>
/// <code>
/// ipv6: Internet Protocol Version 6, Src: 2001:db8::1, Dst: 2001:db8::2
/// ├── ipv6.version: 6
/// ├── ipv6.tclass: 0
/// ├── ipv6.tclass.dscp: 0 (Default (BE))
/// ├── ipv6.tclass.ecn: 0 (Not-ECT)
/// ├── ipv6.flow: 0x00000
/// ├── ipv6.plen: 40
/// ├── ipv6.nxt: 0 (Hop-by-Hop Options)       [eager]
/// ├── ipv6.hlim: 64
/// ├── ipv6.src: 2001:db8::1                  [eager]
/// ├── ipv6.dst: 2001:db8::2                  [eager]
/// ├── ipv6.hopopts: Hop-by-Hop Options Header (16 bytes)  [optional]
/// │   ├── ipv6.hopopts.nxt: 43 (IPv6-Route)
/// │   ├── ipv6.hopopts.len: 1
/// │   ├── ipv6.hopopts.len_oct: 16
/// │   └── ipv6.opt: Router Alert (5)
/// │       ├── ipv6.opt.type: 5 (Router Alert)
/// │       ├── ipv6.opt.type.action: 0 (Skip and continue)
/// │       ├── ipv6.opt.type.change: No
/// │       ├── ipv6.opt.type.rest: 5
/// │       ├── ipv6.opt.length: 2
/// │       └── ipv6.opt.router_alert: 0 (MLD)
/// ├── ipv6.routing: Routing Header (Type 4: SRH, Segments Left: 2)  [optional]
/// │   ├── ipv6.routing.nxt: 44 (IPv6-Frag)
/// │   ├── ipv6.routing.len: 4
/// │   ├── ipv6.routing.len_oct: 40
/// │   ├── ipv6.routing.type: 4 (Segment Routing (SRH))
/// │   ├── ipv6.routing.segleft: 2
/// │   ├── ipv6.routing.srh.last_entry: 1
/// │   ├── ipv6.routing.srh.flags: 0x00
/// │   ├── ipv6.routing.srh.tag: 0x0000
/// │   ├── ipv6.routing.srh.addr: 2001:db8::10
/// │   └── ipv6.routing.srh.addr: 2001:db8::20
/// ├── ipv6.fraghdr: Fragment Header (Offset: 0, More: true, ID: 0x12345678)  [optional]
/// │   ├── ipv6.fraghdr.nxt: 6 (TCP)
/// │   ├── ipv6.fraghdr.reserved_octet: 0
/// │   ├── ipv6.fraghdr.offset: 0
/// │   ├── ipv6.fraghdr.reserved_bits: 0
/// │   ├── ipv6.fraghdr.more: true
/// │   └── ipv6.fraghdr.ident: 0x12345678
/// ├── ipv6.dstopts: Destination Options Header (8 bytes)  [optional]
/// ├── ipv6.ah: Authentication Header (SPI: 0x..., Seq: N)  [optional]
/// └── ipv6.esp: Encapsulating Security Payload (SPI: 0x..., Seq: N)  [optional]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The first parse of a packet id feeds the fragment reassembly engine
/// (<see cref="_Defragmenter"/>) and must therefore stay ordered and single-threaded (the session
/// guarantees this under its parse lock). Every later parse of an already-parsed id is a replay: it
/// reads the recorded defragmentation outcome from the protocol-owned effect store instead of
/// touching the fragment buffers, and is safe to run on any number of threads concurrently. The
/// watermark <see cref="_Gate"/> separates both paths without any caller-supplied mode
/// flag.</para>
/// </remarks>
[Protocol("ipv6", "Internet Protocol Version 6", Description = "IPv6 (RFC 8200)")]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey)]
public sealed partial class IPv6Protocol : IProtocol
{
    #region Table Key Constants

    /// <summary>EtherType key for IPv6 (0x86DD).</summary>
    public const ulong EtherTypeKey = 0x86DD;

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present IPv6 fields.</summary>
    private const string _Ipv6IndexGroup = "ipv6";

    /// <summary>
    /// Error text for a reassembled datagram that cannot be handed on because the packet's table of bound buffers is full.
    /// Limit: 255 additional buffers per packet (a constant of the packet's effect keys, not configurable); secure by
    /// default. Exceeding it is an expected failure that is named, not a silent drop.
    /// </summary>
    private const string _BoundBufferLimitMessage =
        "Reassembled IPv6 datagram cannot be parsed further: a packet can hold at most 255 additional buffers";

    /// <summary>
    /// Maximum number of extension headers to walk before giving up (DoS protection).
    /// </summary>
    /// <remarks>
    /// Threat: a crafted packet can chain many tiny extension headers (each at least 8 bytes), so the walk in
    /// <see cref="IPv6Protocol"/> and <see cref="IPv6ExtensionHeaderParser"/> would cost CPU linear in the packet
    /// size. Constant, not configurable: RFC 8200 section 4.1 says each extension header should occur at most once
    /// (Destination Options at most twice), so 16 is far above any legitimate chain and costs a legitimate packet
    /// nothing. Posture: secure by default, because the input is untrusted wire data. Exceeding the limit is not a
    /// parse error: the walk just stops, and the bytes after the last header that was read are dispatched under the
    /// extension-header number that was reached last.
    /// </remarks>
    internal const int MaxExtensionHeaders = 16;

    #endregion

    #region Known extension header next-header values (RFC 8200, §4)

    /// <summary>Next-header value of the Hop-by-Hop Options header.</summary>
    internal const byte NhHopByHop = 0;

    /// <summary>Next-header value of the Routing header.</summary>
    internal const byte NhRouting = 43;

    /// <summary>Next-header value of the Fragment header (fixed 8 bytes, unlike the generic length encoding).</summary>
    internal const byte NhFragment = 44;

    /// <summary>Next-header value of Encapsulating Security Payload; it ends the walkable extension header chain.</summary>
    internal const byte NhEsp = 50;

    /// <summary>Next-header value of the Authentication Header (length counted in 4-octet units, unlike the generic encoding).</summary>
    internal const byte NhAh = 51;

    /// <summary>Next-header value meaning that no header follows.</summary>
    internal const byte NhNoNextHeader = 59;

    /// <summary>Next-header value of the Destination Options header.</summary>
    internal const byte NhDestination = 60;

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("ipv6", "IPv6", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("ipv6.version", "Version", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("ipv6.tclass", "Traffic Class", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _TclassFieldId;

    [U64Field("ipv6.tclass.dscp", "DSCP", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _DscpFieldId;

    [U64Field("ipv6.tclass.ecn", "ECN", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _EcnFieldId;

    [U64Field("ipv6.flow", "Flow Label", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _FlowFieldId;

    [U64Field("ipv6.plen", "Payload Length", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _PayloadLenFieldId;

    [U64Field("ipv6.nxt", "Next Header", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _NextHeaderFieldId;

    [U64Field("ipv6.hlim", "Hop Limit", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _HopLimitFieldId;

    [IPv6Field("ipv6.src", "Source", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _SrcFieldId;

    [IPv6Field("ipv6.dst", "Destination", IndexGroup = _Ipv6IndexGroup)]
    private FieldId _DstFieldId;

    // Field alias group ID assigned in _RegisterFieldsCustom for "ipv6.addr" -> { ipv6.src, ipv6.dst }.
    // Alias is metadata-only; GetFieldId("ipv6.addr") never resolves and no ipv6.addr node
    // is appended to the parse tree.
    private FieldAliasGroupId _AddrAliasGroupId;

    #endregion


    #region Hop-by-Hop Options
    [NoneField("ipv6.hopopts", "Hop-by-Hop Options Header", IndexGroup = "ipv6.ext")]
    private FieldId _HopoptsFieldId;

    [U64Field("ipv6.hopopts.nxt", "Next Header", IndexGroup = "ipv6.ext")]
    private FieldId _HopoptsNxtFieldId;

    [U64Field("ipv6.hopopts.len", "Length", IndexGroup = "ipv6.ext")]
    private FieldId _HopoptsLenFieldId;

    [U64Field("ipv6.hopopts.len_oct", "Length (octets)", IndexGroup = "ipv6.ext")]
    private FieldId _HopoptsLenOctFieldId;

    #endregion

    #region Destination Options
    [NoneField("ipv6.dstopts", "Destination Options Header", IndexGroup = "ipv6.ext")]
    private FieldId _DstoptsFieldId;

    [U64Field("ipv6.dstopts.nxt", "Next Header", IndexGroup = "ipv6.ext")]
    private FieldId _DstoptsNxtFieldId;

    [U64Field("ipv6.dstopts.len", "Length", IndexGroup = "ipv6.ext")]
    private FieldId _DstoptsLenFieldId;

    [U64Field("ipv6.dstopts.len_oct", "Length (octets)", IndexGroup = "ipv6.ext")]
    private FieldId _DstoptsLenOctFieldId;

    #endregion

    #region TLV Options (shared for Hop-by-Hop and Destination Options)
    [NoneField("ipv6.opt", "IPv6 Option", IndexGroup = "ipv6.ext")]
    private FieldId _OptFieldId;

    [U64Field("ipv6.opt.type", "Type", IndexGroup = "ipv6.ext")]
    private FieldId _OptTypeFieldId;

    [U64Field("ipv6.opt.type.action", "Action", IndexGroup = "ipv6.ext")]
    private FieldId _OptTypeActionFieldId;

    [BoolField("ipv6.opt.type.change", "May Change", IndexGroup = "ipv6.ext")]
    private FieldId _OptTypeChangeFieldId;

    [U64Field("ipv6.opt.type.rest", "Low-Order Bits", IndexGroup = "ipv6.ext")]
    private FieldId _OptTypeRestFieldId;

    [U64Field("ipv6.opt.length", "Length", IndexGroup = "ipv6.ext")]
    private FieldId _OptLengthFieldId;

    [NoneField("ipv6.opt.pad1", "Pad1", IndexGroup = "ipv6.ext")]
    private FieldId _OptPad1FieldId;

    [NoneField("ipv6.opt.padn", "PadN", IndexGroup = "ipv6.ext")]
    private FieldId _OptPadnFieldId;

    [U64Field("ipv6.opt.router_alert", "Router Alert", IndexGroup = "ipv6.ext")]
    private FieldId _OptRouterAlertFieldId;

    [U64Field("ipv6.opt.jumbo", "Jumbo Payload Length", IndexGroup = "ipv6.ext")]
    private FieldId _OptJumboFieldId;

    [U64Field("ipv6.opt.tel", "Tunnel Encapsulation Limit", IndexGroup = "ipv6.ext")]
    private FieldId _OptTelFieldId;

    [IPv6Field("ipv6.opt.mipv6.home_address", "Home Address", IndexGroup = "ipv6.ext")]
    private FieldId _OptHomeAddressFieldId;

    [BytesField("ipv6.opt.unknown", "Unknown Option Data", IndexGroup = "ipv6.ext")]
    private FieldId _OptUnknownFieldId;

    #endregion

    #region Routing Header
    [NoneField("ipv6.routing", "Routing Header", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingFieldId;

    [U64Field("ipv6.routing.nxt", "Next Header", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingNxtFieldId;

    [U64Field("ipv6.routing.len", "Length", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingLenFieldId;

    [U64Field("ipv6.routing.len_oct", "Length (octets)", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingLenOctFieldId;

    [U64Field("ipv6.routing.type", "Type", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingTypeFieldId;

    [U64Field("ipv6.routing.segleft", "Segments Left", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingSegleftFieldId;

    [BytesField("ipv6.routing.unknown_data", "Type-Specific Data", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingUnknownDataFieldId;

    [BytesField("ipv6.routing.mipv6.reserved", "Reserved", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingMipv6ReservedFieldId;

    [IPv6Field("ipv6.routing.mipv6.home_address", "Home Address", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingMipv6HomeAddressFieldId;

    [U64Field("ipv6.routing.srh.last_entry", "Last Entry", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingSrhLastEntryFieldId;

    [U64Field("ipv6.routing.srh.flags", "Flags", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingSrhFlagsFieldId;

    [BytesField("ipv6.routing.srh.tag", "Tag", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingSrhTagFieldId;

    [IPv6Field("ipv6.routing.srh.addr", "Address", IndexGroup = "ipv6.ext")]
    private FieldId _RoutingSrhAddrFieldId;

    #endregion

    #region Fragment Header
    [NoneField("ipv6.fraghdr", "Fragment Header", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrFieldId;

    [U64Field("ipv6.fraghdr.nxt", "Next Header", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrNxtFieldId;

    [U64Field("ipv6.fraghdr.reserved_octet", "Reserved octet", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrReservedOctetFieldId;

    [U64Field("ipv6.fraghdr.offset", "Offset", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrOffsetFieldId;

    [U64Field("ipv6.fraghdr.reserved_bits", "Reserved bits", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrReservedBitsFieldId;

    [BoolField("ipv6.fraghdr.more", "More Fragments", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrMoreFieldId;

    [U64Field("ipv6.fraghdr.ident", "Identification", IndexGroup = "ipv6.fraghdr")]
    private FieldId _FraghdrIdentFieldId;

    #endregion

    #region Authentication Header (AH)
    [NoneField("ipv6.ah", "Authentication Header", IndexGroup = "ipv6.ah")]
    private FieldId _AhFieldId;

    [U64Field("ipv6.ah.nxt", "Next Header", IndexGroup = "ipv6.ah")]
    private FieldId _AhNxtFieldId;

    [U64Field("ipv6.ah.length", "Payload Length", IndexGroup = "ipv6.ah")]
    private FieldId _AhLengthFieldId;

    [U64Field("ipv6.ah.reserved", "Reserved", IndexGroup = "ipv6.ah")]
    private FieldId _AhReservedFieldId;

    [U64Field("ipv6.ah.spi", "Security Parameters Index", IndexGroup = "ipv6.ah")]
    private FieldId _AhSpiFieldId;

    [U64Field("ipv6.ah.seq", "Sequence Number", IndexGroup = "ipv6.ah")]
    private FieldId _AhSeqFieldId;

    [BytesField("ipv6.ah.icv", "Integrity Check Value", IndexGroup = "ipv6.ah")]
    private FieldId _AhIcvFieldId;

    #endregion

    #region Encapsulating Security Payload (ESP)
    [NoneField("ipv6.esp", "Encapsulating Security Payload", IndexGroup = "ipv6.esp")]
    private FieldId _EspFieldId;

    [U64Field("ipv6.esp.spi", "Security Parameters Index", IndexGroup = "ipv6.esp")]
    private FieldId _EspSpiFieldId;

    [U64Field("ipv6.esp.seq", "Sequence Number", IndexGroup = "ipv6.esp")]
    private FieldId _EspSeqFieldId;

    // Reuses the same IP protocol dispatch table as IPv4 (resolved at registration time)
    [UsesTable(IPv4Protocol.IpProtoTableName)]
    private ProtocolTableId _IpProtoTableId;

    #endregion

    #region Pre-allocated populator (created once in _OnStartCustom, shared across all packets)

    /// <summary>Pre-allocated delegate for IPv6 field population — captures only 'this'.</summary>
    private LazyPopulator _Populator = null!;

    // Dense dispatch cache for the IPv6 next-header byte (256 entries).
    // Built once in OnStart from the ip.proto table (shared with IPv4); avoids a dictionary
    // lookup per packet. An entry is null when the table has zero or several protocols for that key; the caller
    // then falls back to full table dispatch. The array is empty until OnStart ran (see _DispatchNextHeader).
    // The array is written once at start and only read afterwards, so concurrent re-parses need no synchronization.
    // CallProtocol returns ParseError for invalid ids.
    private ProtocolId?[] _IpProtoIdCache = [];

    // Pre-allocated extension header field IDs struct, built once in _OnStartCustom. The lazy populator receives
    // it by reference, which avoids a per-packet closure or copy of the roughly fifty field ids.
    private ExtHeaderFieldIds _ExtHeaderFieldIds;

    // IPv6 fragment reassembly engine — keyed by (src, dst, identification).
    // RFC 5722: overlapping fragments MUST cause the entire datagram to be silently discarded.
    // Only the ordered first-parse path may call into it (it is not thread-safe); replays read the outcome from
    // the effect store in IPv6Protocol.Effects.cs instead.
    private readonly FragmentReassembler<IPv6DatagramFragmentKey> _Defragmenter = new(FragmentReassemblyOptions.IPv6);

    /// <summary>
    /// Pre-allocates the lazy-field populator delegate and builds the next-header dispatch cache.
    /// Neither allocation occurs per packet — both are one-time costs at stack start.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateIPv6;
        _ExtHeaderFieldIds = _BuildExtHeaderFieldIds();
        // IPv6 next-header is also an 8-bit IP protocol number; share the same ip.proto table.
        _IpProtoIdCache = stack.BuildU64IdCache(_IpProtoTableId, 256);
    }

    /// <summary>
    /// Registers protocol-owned alias groups. Adds "ipv6.addr" -> { ipv6.src, ipv6.dst }
    /// as metadata; the alias is reachable only via the alias-group APIs on IStack.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _AddrAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "ipv6.addr",
            "Any-match alias for source/destination IPv6 addresses.",
            [_SrcFieldId, _DstFieldId]);
    }

    /// <summary>
    /// Lazy populator: version, traffic class, DSCP, ECN, flow label, payload length,
    /// hop limit, and extension headers. The identifying ipv6.src, ipv6.dst and ipv6.nxt
    /// fields are appended eagerly by <see cref="IProtocol.Parse"/> so downstream
    /// transport protocols (UDP/TCP) can read them without triggering materialisation.
    /// Re-parses the fixed 40-byte header and walks extension headers from the stored
    /// bytes (the container value) instead of capturing parse state in a per-packet closure,
    /// which would allocate for every packet although most packets are never expanded.
    /// Called on first access of the IPv6 container's children.
    /// <para>
    /// Thread safety: runs from the lazy materialization of one packet; it reads only the immutable container
    /// bytes and the field ids fixed at start, and shares no mutable protocol state.
    /// </para>
    /// </summary>
    private ParseResult _PopulateIPv6(in MutField container)
    {
        // The container value is the header slice stored by _ParseBody (fixed header plus the walked extension
        // headers). Both checks only guard against a foreign or corrupt container; _ParseBody already validated it.
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = data.Span;
        if (!IPv6Header.TryParse(span, out IPv6Header header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        // Split the 8-bit traffic class into its DSCP (upper 6 bits) and ECN (lower 2 bits) parts (RFC 2474 / RFC 3168).
        byte trafficClass = header.TrafficClass;
        byte dscp = (byte)(trafficClass >> 2);
        byte ecn = (byte)(trafficClass & 0x03);
        uint flowLabel = header.FlowLabel;
        ushort payloadLength = header.PayloadLength;
        byte nextHeader = header.NextHeader;
        byte hopLimit = header.HopLimit;

        // Append the fixed header fields in wire order. The display texts of DSCP and ECN come from DisplayTables.
        container.Append(_VersionFieldId, FieldValue.NewU64(header.Version));
        container.Append(_TclassFieldId, FieldValue.NewU64(trafficClass));

        string dscpText = DisplayTables.GetDscpDisplayText(dscp);
        container.AppendWithCustomText(_DscpFieldId, FieldValue.NewU64(dscp), dscpText);

        string ecnText = DisplayTables.GetEcnDisplayText(ecn);
        container.AppendWithCustomText(_EcnFieldId, FieldValue.NewU64(ecn), ecnText);

        container.Append(_FlowFieldId, FieldValue.NewU64(flowLabel));
        container.Append(_PayloadLenFieldId, FieldValue.NewU64(payloadLength));

        // ipv6.nxt (fixed-header Next Header) is appended eagerly in Parse(); not here.
        container.Append(_HopLimitFieldId, FieldValue.NewU64(hopLimit));

        // Parse extension headers into individual sub-fields if present. The stored data includes the full fixed
        // header + extension header region (_ParseBody stores exactly the walked chain), so the parser can re-walk
        // the extension headers with field decomposition. This second walk is the price of keeping the eager parse
        // path free of field creation; it is only paid for packets whose children are actually expanded.
        if (data.Length > IPv6Header.HeaderSize && IsExtensionHeader(nextHeader))
        {
            ReadOnlySpan<byte> extData = span[IPv6Header.HeaderSize..];
            IPv6ExtensionHeaderParser.Parse(container, extData, nextHeader, in _ExtHeaderFieldIds);
        }

        // ipv6.addr is exposed as an alias group registered in _RegisterFieldsCustom;
        // no duplicate ipv6.addr field node is appended to the parse tree.

        return 0;
    }

    /// <summary>
    /// Parses a IPv6 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// <para>
    /// Fragmented packets are stateful: the first parse feeds the fragment into the shared
    /// defragmenter and records the outcome, every later parse of that packet id replays the recorded
    /// outcome and leaves the fragment buffers untouched. See <see cref="_Gate"/>.
    /// </para>
    /// <para>
    /// Thread safety: a first parse must be serialized by the caller (ordered ingest); replays may run on any
    /// number of threads concurrently. See the class remarks.
    /// </para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The layer key (location of 'data' inside the packet buffers) distinguishes several IPv6 layers of one
        // packet in the effect store, because the packet id alone would let a tunnelled inner layer overwrite the
        // outer layer's recorded defragmentation outcome. Data that is not a slice of the packet's buffers cannot be
        // keyed, so it is rejected up front.
        Packet packet = parentField.Packet;
        if (!parentField.TryGetEffectLayerKey(data, out int layerKey))
        {
            return ParseError.Custom(ProtocolName, "Parse data is not a slice of this packet's buffers.");
        }

        // Decide between first parse and replay once, before any nested layer runs. The ingest gate counts nesting on first
        // parses only (replays never write protocol state, they hold the inert default scope), and only the outermost
        // scope raises the watermark: raising it earlier would make a later layer of the same packet look like a replay
        // and skip the defragmenter.
        bool isReplay = _Gate.IsReplay(packet.Id);
        IngestScope scope = isReplay
            ? default
            : _Gate.Enter(packet.Id);

        // The finally block runs on every exit (error results and exceptions included), so the scope cannot leak and the
        // packet is always marked as parsed once, even when its first parse failed.
        try
        {
            return _ParseBody(in parentField, data, in context, layerKey, isReplay);
        }
        finally
        {
            // The address cache is keyed by packet id only and is meant for the protocols dispatched from inside this
            // parse. Left behind, its entry could be taken for the addresses of a different packet with the same id
            // on this thread (for example one of another session, or a later expansion of an earlier packet), which
            // would give a wrong pseudo header or stream key. Clearing it here makes such a stale read impossible; a
            // reader that finds no entry falls back to the field tree.
            _ThreadCache = null;

            scope.Dispose();
        }
    }

    /// <summary>
    /// Parse body. <paramref name="layerKey"/> is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call and
    /// <paramref name="isReplay"/> selects replay of the recorded defragmentation outcome over
    /// feeding the defragmenter.
    /// </summary>
    private ParseResult _ParseBody(
        in MutField parentField,
        ReadOnlyMemory<byte> data,
        in ParseContext context,
        int layerKey,
        bool isReplay)
    {
        if (data.Length < IPv6Header.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Ipv6GroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Decode the fixed header by hand (one length check)
        if (!IPv6Header.TryParse(span, out IPv6Header header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        byte version = header.Version;
        if (version != IPv6Header.ExpectedVersion)
        {
            return ParseError.InvalidData(ProtocolName, $"Expected version 6, got {version}");
        }

        // Only extract fields needed for: version check, extension-header walk, dispatch, index recording,
        // summary closure, and eager src/dst append. All other fields are deferred to the lazy populator.
        ushort payloadLength = header.PayloadLength;
        byte nextHeader = header.NextHeader;
        IPv6Address src = IPv6Header.GetSrc(span);
        IPv6Address dst = IPv6Header.GetDst(span);

        // Walk extension header chain to find the final next-header value and detect fragments.
        // Extension headers are in the payload area, after the 40-byte fixed header.
        int extOffset = IPv6Header.HeaderSize; // offset from start of data
        int extTotalLen = 0; // total bytes consumed by extension headers
        byte finalNextHeader = nextHeader;
        int depthCount = 0;

        // Fragment header tracking
        bool hasFragment = false;
        ushort fragOffset = 0;
        bool moreFragments = false;
        uint fragIdentification = 0;
        byte fragInnerNextHeader = 0; // next header from fragment header (transport protocol)

        // Security header tracking — the index decision for ipv6.ah / ipv6.esp must be made
        // eagerly here so the presence index is complete without lazy materialization. The
        // emission guards below mirror IPv6ExtensionHeaderParser.ParseAhHeader/ParseEspHeader
        // exactly so a recorded group is always backed by an emitted field (no false positives).
        bool hasAh = false;
        bool hasEsp = false;

        // Whether the chain contains at least one option-bearing extension header (Hop-by-Hop,
        // Routing or Destination Options). Only these headers materialize fields in the "ipv6.ext"
        // index group; Fragment, AH and ESP carry their own groups (ipv6.fraghdr / ipv6.ah / ipv6.esp).
        // Tracking this separately keeps the presence index free of false positives for fragment-only,
        // AH-only or ESP-only chains, while extTotalLen still accounts for every header's bytes so the
        // stored slice covers the whole chain for lazy materialization.
        bool hasExtGroupHeader = false;

        while (depthCount < MaxExtensionHeaders && IsExtensionHeader(finalNextHeader))
        {
            int remaining = data.Length - extOffset;
            if (remaining < 2)
            {
                // Not enough data for extension header — stop walking
                break;
            }

            if (finalNextHeader == NhFragment)
            {
                // Fragment header is fixed 8 bytes (not the standard length encoding)
                if (remaining < 8)
                {
                    break;
                }

                hasFragment = true;
                fragInnerNextHeader = span[extOffset]; // next header (transport protocol)
                ushort offsetFlagsWord = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(extOffset + 2, 2));
                fragOffset = (ushort)((offsetFlagsWord & 0xFFF8) >> 3);
                moreFragments = (offsetFlagsWord & 0x0001) != 0;
                fragIdentification = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(extOffset + 4, 4));

                finalNextHeader = fragInnerNextHeader;
                extOffset += 8;
                extTotalLen += 8;

                // A real fragment ends the walk. The bytes after its header are a slice of the fragmentable part of
                // the original datagram: for a non-first fragment they start in the middle of it, so a payload byte
                // that looks like an extension header number must not be read as a header (that would cut bytes out
                // of the fragment data and corrupt the reassembly). For the first fragment, extension headers that
                // follow belong to the fragmentable part and are part of the data handed to the reassembly, so that
                // all fragments share the same offsets. The next-header value of the fragment header is what names
                // the first header of the reassembled data. An atomic fragment (offset 0, no More flag) carries the
                // whole datagram, so its chain continues as usual.
                if (IPv6ExtensionHeaderParser.IsRealFragment(offsetFlagsWord))
                {
                    break;
                }
            }
            else if (finalNextHeader == NhEsp)
            {
                // ESP terminates extension header chain — payload is encrypted. The lazy parser
                // emits the esp field only when at least the fixed 8-byte SPI+Seq header fits.
                if (remaining >= 8)
                {
                    hasEsp = true;

                    // The 8 bytes of SPI and sequence number belong to the header part: the lazy parser reads them from
                    // the stored slice, and the payload (ciphertext) starts after them. Without this advance the field
                    // was recorded in the presence index but never emitted.
                    extOffset += 8;
                    extTotalLen += 8;
                }
                break;
            }
            else if (finalNextHeader == NhAh)
            {
                // Authentication Header uses 4-octet length units: total = (payload_len + 2) * 4,
                // minimum 12 bytes. This differs from the standard 8-octet encoding below, so it
                // must be handled explicitly to match the lazy parser's emission and advance the
                // chain correctly.
                if (remaining < 12)
                {
                    break;
                }

                byte ahPayloadLen = span[extOffset + 1];
                int ahTotalLen = (ahPayloadLen + 2) * 4;
                // Payload length 0 (8 bytes) leaves no room for the sequence number: malformed, as the lazy parser
                // treats it.
                if (ahTotalLen < 12 || remaining < ahTotalLen)
                {
                    break;
                }

                hasAh = true;
                finalNextHeader = span[extOffset]; // next header at offset 0
                extOffset += ahTotalLen;
                extTotalLen += ahTotalLen;
            }
            else
            {
                // Standard extension header: next_header(1) + hdr_ext_len(1) + data
                // hdr_ext_len is in 8-octet units, not including the first 8 octets
                byte hdrExtLen = span[extOffset + 1];
                int extLen = (hdrExtLen + 1) * 8; // total length in bytes
                if (remaining < extLen)
                {
                    break;
                }
                hasExtGroupHeader = true;
                finalNextHeader = span[extOffset]; // next header at offset 0
                extOffset += extLen;
                extTotalLen += extLen;
            }

            depthCount++;
        }

        // Determine whether extension headers were found
        bool hasExtHeaders = extTotalLen > 0;
        if (hasExtGroupHeader)
        {
            context.RecordGroupPresence(_Ipv6ExtGroupId);
        }
        if (hasFragment)
        {
            context.RecordGroupPresence(_Ipv6FraghdrGroupId);
        }
        if (hasAh)
        {
            context.RecordGroupPresence(_Ipv6AhGroupId);
        }
        if (hasEsp)
        {
            context.RecordGroupPresence(_Ipv6EspGroupId);
        }

        // Store the full IPv6 data (fixed header + extension headers) so the lazy populator
        // can reconstruct all fields including extension header sub-fields. The slice references the packet
        // buffer; nothing is copied. The Math.Min is defensive: the walk only accepts headers that fit into 'data'.
        int storedLen = hasExtHeaders
            ? Math.Min(IPv6Header.HeaderSize + extTotalLen, data.Length)
            : IPv6Header.HeaderSize;
        ReadOnlyMemory<byte> headerBytes = data[..storedLen];

        // The stored value holds the extension-header chain too; its representation names the stored length. The
        // common case without extension headers keeps the static text (no per-packet formatting).
        FieldValue headerValue = FieldValue.NewBytes(headerBytes)
            .WithCustomRepresentation(hasExtHeaders
                // Eager text (one string): the field tree stores it right away; InvariantCulture per the repository rule.
                ? new LazyString(string.Create(CultureInfo.InvariantCulture, $"{storedLen} bytes"))
                : new LazyString("40 bytes"));

        // The container text "Internet Protocol Version 6, Src: ..., Dst: ..." is formatted lazily from src and
        // dst only (two 16-byte structs), which is smaller than capturing the whole header, and only when
        // custom text is wanted for this field at all. The populator delegate is the pre-allocated instance.
        MutField protoField = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, headerValue, "Internet Protocol Version 6, Src: ", src, ", Dst: ", dst, _Populator);

        // Eagerly append src/dst as non-lazy children so downstream protocols
        // (e.g., UDP/TCP) can read IPv6 addresses from the tree without materializing.
        // The any-match name "ipv6.addr" is exposed via the alias group registered in
        // _RegisterFieldsCustom; no duplicate field node is appended.
        protoField.Append(_SrcFieldId, FieldValue.NewIPv6(src));
        protoField.Append(_DstFieldId, FieldValue.NewIPv6(dst));

        // Eagerly append ipv6.nxt (fixed-header Next Header) so that the common
        // protocol-selector field is present without triggering the lazy populator.
        string nxtTextEager = DisplayTables.GetIpProtocolDisplayText(nextHeader);
        protoField.AppendWithCustomText(_NextHeaderFieldId, FieldValue.NewU64(nextHeader), nxtTextEager);

        // Cache IPv6 addresses in the thread-local field directly on this protocol
        // so downstream transport protocols (TCP, UDP) can read them without
        // sibling-walk field-tree navigation.
        SetCachedAddresses(parentField.Packet.Id, src, dst, header.HopLimit);

        // Dispatch to next protocol using the final next-header value (after walking extension chain)
        int payloadStart = extOffset; // after fixed header + all extension headers
        // The payload ends at the shorter of the length announced by the header and the bytes actually present:
        // the announced length trims link-layer padding (for example short Ethernet frames), the present bytes trim
        // truncated captures. The Plen field counts the extension headers, hence the subtraction. A jumbogram
        // (Plen 0, RFC 2675) therefore yields an empty payload and is not dispatched.
        int payloadLen = Math.Max(0, Math.Min(payloadLength - extTotalLen, data.Length - payloadStart));

        if (payloadLen > 0 && _IpProtoTableId.IsValid)
        {
            // IPv6 fragment reassembly: if fragment header was detected, route through defragmenter
            if (hasFragment)
            {
                // RFC 8200 section 4.5: a packet is a real fragment unless offset and More flag are both zero
                // ("atomic fragment"), which carries the whole datagram and needs no reassembly.
                bool isFragment = moreFragments || fragOffset != 0;
                if (isFragment)
                {
                    // Only the fragment that completes a datagram yields a non-null result and dispatches the
                    // reassembled payload; every other fragment only parses its own headers.
                    byte[]? reassembled;
                    if (isReplay)
                    {
                        // Replay: never touch the shared fragment buffers from what may be a reader
                        // thread. A missing effect means the first parse never reached the
                        // defragmenter, so there is nothing to dispatch.
                        reassembled = _FindReassembledDatagram(parentField.Packet.Id, layerKey);
                    }
                    else
                    {
                        // First parse: the defragmenter is single-threaded state, fed in packet order. The outcome
                        // is recorded in the effect store so replays reproduce it. The key uses the source and
                        // destination addresses plus the identification (RFC 8200 section 4.5).
                        IPv6DatagramFragmentKey key = new(src.High, src.Low, dst.High, dst.Low, fragIdentification);
                        int byteOffset = fragOffset * 8; // convert to byte offset

                        FragmentOutcome outcome = _Defragmenter.Add(
                            in key, byteOffset, moreFragments, data.Span.Slice(payloadStart, payloadLen), parentField.Packet.Id.Value);

                        // Only a completed datagram is dispatched; every other outcome (pending or refused) shows the fragment alone.
                        reassembled = outcome.Kind == FragmentOutcomeKind.Complete
                            ? outcome.Payload
                            : null;
                        _RecordDefragEffect(parentField.Packet.Id, layerKey, reassembled);
                    }

                    if (reassembled is not null)
                    {
                        // Bind the datagram so nested Parse can key effects on this packet's buffers.
                        ReadOnlyMemory<byte> reassembledPayload = parentField.BindParseBuffer(reassembled);
                        if (reassembledPayload.IsEmpty && reassembled.Length > 0)
                        {
                            // The packet holds at most 255 bound buffers. An empty slice would reach the next protocol
                            // as "no data" without any sign of why, so the limit is named here.
                            return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
                        }

                        ParseResult dispatchResult = _DispatchNextHeader(
                            in parentField, finalNextHeader, reassembledPayload, in context);
                        if (dispatchResult.TryPropagateError(out ParseResult error))
                        {
                            return error;
                        }
                    }
                    // Else: fragment stored, waiting for more fragments — no dispatch yet
                }
                else
                {
                    // unfragmented packet that happens to have a fragment header (offset=0, MF=0)
                    ReadOnlyMemory<byte> payload = data.Slice(payloadStart, payloadLen);
                    ParseResult dispatchResult = _DispatchNextHeader(
                        in parentField, finalNextHeader, payload, in context);
                    if (dispatchResult.TryPropagateError(out ParseResult error))
                    {
                        return error;
                    }
                }
            }
            else
            {
                // No fragment header — dispatch directly
                ReadOnlyMemory<byte> payload = data.Slice(payloadStart, payloadLen);
                ParseResult dispatchResult = _DispatchNextHeader(in parentField, finalNextHeader, payload, in context);
                if (dispatchResult.TryPropagateError(out ParseResult error))
                {
                    return error;
                }
            }
        }

        // Return total consumed: fixed header + payload length (clamped to available data)
        return Math.Min(IPv6Header.HeaderSize + payloadLength, data.Length);
    }

    /// <summary>
    /// Dispatches to the next protocol by IPv6 next-header (IP protocol) number.
    /// Uses the pre-cached <see cref="ProtocolId"/> for the common single-protocol case;
    /// falls back to full table dispatch for multi-protocol keys or entries outside the cache.
    /// </summary>
    private ParseResult _DispatchNextHeader(
        in MutField parentField, byte nextHeader, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        ProtocolId? cached = _IpProtoIdCache.Length > 0 ? _IpProtoIdCache[nextHeader] : null;
        return cached is ProtocolId protocolId
            ? parentField.CallProtocol(protocolId, payload, in context)
            : parentField.TryCallNextProtocolU64(_IpProtoTableId, nextHeader, payload, in context);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the given next-header value indicates an
    /// extension header that should be walked through to find the transport protocol.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsExtensionHeader(byte nextHeader) =>
        nextHeader is NhHopByHop or NhRouting or NhFragment or NhAh or NhDestination or NhEsp;

    /// <summary>
    /// Bundles all extension header field IDs into a single struct for efficient passing
    /// to <see cref="IPv6ExtensionHeaderParser"/>. A readonly struct passed by <see langword="in"/> avoids both
    /// a long parameter list and a heap allocation. Every property carries the id of the private field with the
    /// same name plus <c>FieldId</c> in <see cref="IPv6Protocol"/> (see <c>_BuildExtHeaderFieldIds</c>); the
    /// properties are therefore not documented one by one.
    /// <para>Thread safety: immutable after construction.</para>
    /// </summary>
    internal readonly struct ExtHeaderFieldIds
    {
        // Hop-by-Hop Options
        internal FieldId HopoptsFieldId
        {
            get; init;
        }
        internal FieldId HopoptsNxtFieldId
        {
            get; init;
        }
        internal FieldId HopoptsLenFieldId
        {
            get; init;
        }
        internal FieldId HopoptsLenOctFieldId
        {
            get; init;
        }

        // Destination Options
        internal FieldId DstoptsFieldId
        {
            get; init;
        }
        internal FieldId DstoptsNxtFieldId
        {
            get; init;
        }
        internal FieldId DstoptsLenFieldId
        {
            get; init;
        }
        internal FieldId DstoptsLenOctFieldId
        {
            get; init;
        }

        // TLV Options (shared)
        internal FieldId OptFieldId
        {
            get; init;
        }
        internal FieldId OptTypeFieldId
        {
            get; init;
        }
        internal FieldId OptTypeActionFieldId
        {
            get; init;
        }
        internal FieldId OptTypeChangeFieldId
        {
            get; init;
        }
        internal FieldId OptTypeRestFieldId
        {
            get; init;
        }
        internal FieldId OptLengthFieldId
        {
            get; init;
        }
        internal FieldId OptPad1FieldId
        {
            get; init;
        }
        internal FieldId OptPadnFieldId
        {
            get; init;
        }
        internal FieldId OptRouterAlertFieldId
        {
            get; init;
        }
        internal FieldId OptJumboFieldId
        {
            get; init;
        }
        internal FieldId OptTelFieldId
        {
            get; init;
        }
        internal FieldId OptHomeAddressFieldId
        {
            get; init;
        }
        internal FieldId OptUnknownFieldId
        {
            get; init;
        }

        // Routing Header
        internal FieldId RoutingFieldId
        {
            get; init;
        }
        internal FieldId RoutingNxtFieldId
        {
            get; init;
        }
        internal FieldId RoutingLenFieldId
        {
            get; init;
        }
        internal FieldId RoutingLenOctFieldId
        {
            get; init;
        }
        internal FieldId RoutingTypeFieldId
        {
            get; init;
        }
        internal FieldId RoutingSegleftFieldId
        {
            get; init;
        }
        internal FieldId RoutingUnknownDataFieldId
        {
            get; init;
        }
        internal FieldId RoutingMipv6ReservedFieldId
        {
            get; init;
        }
        internal FieldId RoutingMipv6HomeAddressFieldId
        {
            get; init;
        }
        internal FieldId RoutingSrhLastEntryFieldId
        {
            get; init;
        }
        internal FieldId RoutingSrhFlagsFieldId
        {
            get; init;
        }
        internal FieldId RoutingSrhTagFieldId
        {
            get; init;
        }
        internal FieldId RoutingSrhAddrFieldId
        {
            get; init;
        }

        // Fragment Header
        internal FieldId FraghdrFieldId
        {
            get; init;
        }
        internal FieldId FraghdrNxtFieldId
        {
            get; init;
        }
        internal FieldId FraghdrReservedOctetFieldId
        {
            get; init;
        }
        internal FieldId FraghdrOffsetFieldId
        {
            get; init;
        }
        internal FieldId FraghdrReservedBitsFieldId
        {
            get; init;
        }
        internal FieldId FraghdrMoreFieldId
        {
            get; init;
        }
        internal FieldId FraghdrIdentFieldId
        {
            get; init;
        }

        // Authentication Header (AH)
        internal FieldId AhFieldId
        {
            get; init;
        }
        internal FieldId AhNxtFieldId
        {
            get; init;
        }
        internal FieldId AhLengthFieldId
        {
            get; init;
        }
        internal FieldId AhReservedFieldId
        {
            get; init;
        }
        internal FieldId AhSpiFieldId
        {
            get; init;
        }
        internal FieldId AhSeqFieldId
        {
            get; init;
        }
        internal FieldId AhIcvFieldId
        {
            get; init;
        }

        // ESP
        internal FieldId EspFieldId
        {
            get; init;
        }
        internal FieldId EspSpiFieldId
        {
            get; init;
        }
        internal FieldId EspSeqFieldId
        {
            get; init;
        }
    }

    /// <summary>Builds the <see cref="ExtHeaderFieldIds"/> struct from the protocol's registered fields.</summary>
    private ExtHeaderFieldIds _BuildExtHeaderFieldIds() => new()
    {
        HopoptsFieldId = _HopoptsFieldId,
        HopoptsNxtFieldId = _HopoptsNxtFieldId,
        HopoptsLenFieldId = _HopoptsLenFieldId,
        HopoptsLenOctFieldId = _HopoptsLenOctFieldId,
        DstoptsFieldId = _DstoptsFieldId,
        DstoptsNxtFieldId = _DstoptsNxtFieldId,
        DstoptsLenFieldId = _DstoptsLenFieldId,
        DstoptsLenOctFieldId = _DstoptsLenOctFieldId,
        OptFieldId = _OptFieldId,
        OptTypeFieldId = _OptTypeFieldId,
        OptTypeActionFieldId = _OptTypeActionFieldId,
        OptTypeChangeFieldId = _OptTypeChangeFieldId,
        OptTypeRestFieldId = _OptTypeRestFieldId,
        OptLengthFieldId = _OptLengthFieldId,
        OptPad1FieldId = _OptPad1FieldId,
        OptPadnFieldId = _OptPadnFieldId,
        OptRouterAlertFieldId = _OptRouterAlertFieldId,
        OptJumboFieldId = _OptJumboFieldId,
        OptTelFieldId = _OptTelFieldId,
        OptHomeAddressFieldId = _OptHomeAddressFieldId,
        OptUnknownFieldId = _OptUnknownFieldId,
        RoutingFieldId = _RoutingFieldId,
        RoutingNxtFieldId = _RoutingNxtFieldId,
        RoutingLenFieldId = _RoutingLenFieldId,
        RoutingLenOctFieldId = _RoutingLenOctFieldId,
        RoutingTypeFieldId = _RoutingTypeFieldId,
        RoutingSegleftFieldId = _RoutingSegleftFieldId,
        RoutingUnknownDataFieldId = _RoutingUnknownDataFieldId,
        RoutingMipv6ReservedFieldId = _RoutingMipv6ReservedFieldId,
        RoutingMipv6HomeAddressFieldId = _RoutingMipv6HomeAddressFieldId,
        RoutingSrhLastEntryFieldId = _RoutingSrhLastEntryFieldId,
        RoutingSrhFlagsFieldId = _RoutingSrhFlagsFieldId,
        RoutingSrhTagFieldId = _RoutingSrhTagFieldId,
        RoutingSrhAddrFieldId = _RoutingSrhAddrFieldId,
        FraghdrFieldId = _FraghdrFieldId,
        FraghdrNxtFieldId = _FraghdrNxtFieldId,
        FraghdrReservedOctetFieldId = _FraghdrReservedOctetFieldId,
        FraghdrOffsetFieldId = _FraghdrOffsetFieldId,
        FraghdrReservedBitsFieldId = _FraghdrReservedBitsFieldId,
        FraghdrMoreFieldId = _FraghdrMoreFieldId,
        FraghdrIdentFieldId = _FraghdrIdentFieldId,
        AhFieldId = _AhFieldId,
        AhNxtFieldId = _AhNxtFieldId,
        AhLengthFieldId = _AhLengthFieldId,
        AhReservedFieldId = _AhReservedFieldId,
        AhSpiFieldId = _AhSpiFieldId,
        AhSeqFieldId = _AhSeqFieldId,
        AhIcvFieldId = _AhIcvFieldId,
        EspFieldId = _EspFieldId,
        EspSpiFieldId = _EspSpiFieldId,
        EspSeqFieldId = _EspSeqFieldId,
    };

    #region Thread-Local Address Cache

    /// <summary>
    /// Per-thread cache for the current packet's IPv6 src/dst addresses.
    /// Written by <see cref="IProtocol.Parse"/> before dispatching; consumed by downstream
    /// protocols (TCP, UDP). Null means no data cached yet on this thread.
    /// <para>
    /// Why thread-static: transport protocols need the addresses for every segment (checksum pseudo header,
    /// stream key). Reading them from this per-thread slot avoids a field-tree walk to the sibling ipv6 node, and
    /// because every thread owns its slot, concurrent replays of different packets need no synchronization. The
    /// stored packet id makes a stale entry (left over from an earlier packet on the same thread) detectable, so
    /// readers must fall back to the field tree when <see cref="TryGetCachedAddresses"/> returns
    /// <see langword="false"/>. The value is a struct, so a write allocates nothing. The write happens before the
    /// payload is dispatched, so in a tunnel the innermost IPv6 layer overwrites the outer one, which is the layer
    /// the transport protocol above it belongs to.
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static (int PacketId, IPv6Address Src, IPv6Address Dst, byte HopLimit)? _ThreadCache;

    /// <summary>
    /// Caches the IPv6 src/dst addresses and the hop limit for the current packet on this thread. The hop limit travels
    /// with the addresses because ICMPv6 needs it while it parses, also in the mode that builds no field tree (where the
    /// IPv6 container and its bytes do not exist), and the index must not depend on that mode.
    /// </summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="src">Source address.</param>
    /// <param name="dst">Destination address.</param>
    /// <param name="hopLimit">Hop limit of the header; 0 when the writer does not know it (tests only).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCachedAddresses(PacketId packetId, IPv6Address src, IPv6Address dst, byte hopLimit = 0)
        => _ThreadCache = (packetId.Value, src, dst, hopLimit);

    /// <summary>
    /// Attempts to read the cached hop limit for the specified packet; false when nothing is cached for it.
    /// Like the addresses it describes the innermost IPv6 layer parsed so far, and it is valid only while the IPv6 parse is
    /// still on the call stack (the entry is cleared in the <c>finally</c> block of <c>Parse</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCachedHopLimit(PacketId packetId, out byte hopLimit)
    {
        (int PacketId, IPv6Address Src, IPv6Address Dst, byte HopLimit)? c = _ThreadCache;
        if (c.HasValue && c.Value.PacketId == packetId.Value)
        {
            hopLimit = c.Value.HopLimit;
            return true;
        }

        hopLimit = 0;
        return false;
    }

    /// <summary>
    /// Attempts to read the cached IPv6 addresses for the specified packet.
    /// Returns <see langword="false"/> if no data is cached or the packet ID
    /// does not match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCachedAddresses(PacketId packetId, out IPv6Address src, out IPv6Address dst)
    {
        (int PacketId, IPv6Address Src, IPv6Address Dst, byte HopLimit)? c = _ThreadCache;
        if (c.HasValue && c.Value.PacketId == packetId.Value)
        {
            src = c.Value.Src;
            dst = c.Value.Dst;
            return true;
        }
        src = default;
        dst = default;
        return false;
    }

    #endregion
}

/// <summary>
/// IPv6 fixed header (40 bytes).
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |Version| Traffic Class |           Flow Label                 |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |         Payload Length        |  Next Header  |   Hop Limit  |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                                                              |
/// +                                                              +
/// |                       Source Address                         |
/// +                        (128 bits)                            +
/// |                                                              |
/// +                                                              +
/// |                                                              |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                                                              |
/// +                                                              +
/// |                    Destination Address                       |
/// +                        (128 bits)                            +
/// |                                                              |
/// +                                                              +
/// |                                                              |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset reads with <see cref="BinaryPrimitives"/>) instead of a generated
/// parser, so the JIT elides the per-read bounds checks and the dissector gets plain integers; declaration order
/// mirrors the wire layout for readability only. The first four bytes pack version, traffic class and flow label and
/// are split by shifts. Thread safety: immutable value type.
/// </remarks>
internal readonly struct IPv6Header
{
    /// <summary>IP version (bits 31..28 of the first word, must be 6).</summary>
    public byte Version
    {
        get; init;
    }

    /// <summary>Traffic Class (bits 27..20 of the first word: DSCP + ECN).</summary>
    public byte TrafficClass
    {
        get; init;
    }

    /// <summary>Flow Label (bits 19..0 of the first word).</summary>
    public uint FlowLabel
    {
        get; init;
    }

    /// <summary>
    /// Payload length in bytes (bytes 4..5; excluding the 40-byte fixed header, including extension headers; 0 when a Jumbo Payload option carries the length).
    /// </summary>
    public ushort PayloadLength
    {
        get; init;
    }

    /// <summary>Next header protocol number (byte 6, same as IPv4 Protocol field).</summary>
    public byte NextHeader
    {
        get; init;
    }

    /// <summary>Hop limit (byte 7, decremented by each router).</summary>
    public byte HopLimit
    {
        get; init;
    }

    /// <summary>Source address high 64 bits (bytes 8..15).</summary>
    public ulong SrcHi
    {
        get; init;
    }

    /// <summary>Source address low 64 bits (bytes 16..23).</summary>
    public ulong SrcLo
    {
        get; init;
    }

    /// <summary>Destination address high 64 bits (bytes 24..31).</summary>
    public ulong DstHi
    {
        get; init;
    }

    /// <summary>Destination address low 64 bits (bytes 32..39).</summary>
    public ulong DstLo
    {
        get; init;
    }

    /// <summary>Fixed header size in bytes (40).</summary>
    internal const int HeaderSize = 40;

    /// <summary>Expected IP version number.</summary>
    internal const byte ExpectedVersion = 6;

    /// <summary>
    /// Decodes the fixed 40-byte header. Returns <see langword="false"/> with a default value when fewer than
    /// <see cref="HeaderSize"/> bytes are present. Longer input is accepted; only the first 40 bytes are read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out IPv6Header value)
    {
        // One length check up front; a short buffer is an expected failure, not an exception.
        if (source.Length < HeaderSize)
        {
            value = default;
            return false;
        }

        // Exact-size slice: after this the JIT knows every index below is in range and drops the bounds checks.
        ReadOnlySpan<byte> h = source[..HeaderSize];
        uint versionClassFlow = BinaryPrimitives.ReadUInt32BigEndian(h);
        value = new IPv6Header
        {
            Version = (byte)(versionClassFlow >> 28),                        // word 0..4, bits 31..28
            TrafficClass = (byte)((versionClassFlow >> 20) & 0xFF),          // word 0..4, bits 27..20
            FlowLabel = versionClassFlow & 0x000F_FFFF,                      // word 0..4, bits 19..0
            PayloadLength = BinaryPrimitives.ReadUInt16BigEndian(h[4..]),    // 4..6
            NextHeader = h[6],                                               // 6
            HopLimit = h[7],                                                 // 7
            SrcHi = BinaryPrimitives.ReadUInt64BigEndian(h[8..]),            // 8..16
            SrcLo = BinaryPrimitives.ReadUInt64BigEndian(h[16..]),           // 16..24
            DstHi = BinaryPrimitives.ReadUInt64BigEndian(h[24..]),           // 24..32
            DstLo = BinaryPrimitives.ReadUInt64BigEndian(h[32..]),           // 32..40
        };
        return true;
    }

    /// <summary>Extracts the source IPv6 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address GetSrc(ReadOnlySpan<byte> data) => IPv6Address.FromBytes(data[8..24]);

    /// <summary>Extracts the destination IPv6 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address GetDst(ReadOnlySpan<byte> data) => IPv6Address.FromBytes(data[24..40]);
    #endregion
}
