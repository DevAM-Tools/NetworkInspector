// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
/// ├── ipv6.nxt: 0 (Hop-by-Hop Options)
/// ├── ipv6.hlim: 64
/// ├── ipv6.src: 2001:db8::1                  [eager]
/// ├── ipv6.dst: 2001:db8::2                  [eager]
/// ├── ipv6.addr: 2001:db8::1                [any-match, lazy]
/// ├── ipv6.addr: 2001:db8::2                [any-match, lazy]
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
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.
/// The fragment reassembly engine (<see cref="_Defragmenter"/>) accumulates mutable state
/// across packets and must not be accessed concurrently.</para>
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
    private const string Ipv6IndexGroup = "ipv6";

    /// <summary>Maximum number of extension headers to walk before giving up (DoS protection).</summary>
    internal const int MaxExtensionHeaders = 16;

    #endregion

    #region Known extension header next-header values (RFC 8200, §4)
    internal const byte NhHopByHop = 0;
    internal const byte NhRouting = 43;
    internal const byte NhFragment = 44;
    internal const byte NhEsp = 50;
    internal const byte NhAh = 51;
    internal const byte NhNoNextHeader = 59;
    internal const byte NhDestination = 60;

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("ipv6", "IPv6", IndexGroup = Ipv6IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("ipv6.version", "Version", IndexGroup = Ipv6IndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("ipv6.tclass", "Traffic Class", IndexGroup = Ipv6IndexGroup)]
    private FieldId _TclassFieldId;

    [U64Field("ipv6.tclass.dscp", "DSCP", IndexGroup = Ipv6IndexGroup)]
    private FieldId _DscpFieldId;

    [U64Field("ipv6.tclass.ecn", "ECN", IndexGroup = Ipv6IndexGroup)]
    private FieldId _EcnFieldId;

    [U64Field("ipv6.flow", "Flow Label", IndexGroup = Ipv6IndexGroup)]
    private FieldId _FlowFieldId;

    [U64Field("ipv6.plen", "Payload Length", IndexGroup = Ipv6IndexGroup)]
    private FieldId _PayloadLenFieldId;

    [U64Field("ipv6.nxt", "Next Header", IndexGroup = Ipv6IndexGroup)]
    private FieldId _NextHeaderFieldId;

    [U64Field("ipv6.hlim", "Hop Limit", IndexGroup = Ipv6IndexGroup)]
    private FieldId _HopLimitFieldId;

    [IPv6Field("ipv6.src", "Source", IndexGroup = Ipv6IndexGroup)]
    private FieldId _SrcFieldId;

    [IPv6Field("ipv6.dst", "Destination", IndexGroup = Ipv6IndexGroup)]
    private FieldId _DstFieldId;

    // Combined address field (Wireshark ipv6.addr compatibility).
    // Appended twice (once for src, once for dst) to enable any-match filter semantics:
    // `ipv6.addr == ::1` matches either source or destination.
    [IPv6Field("ipv6.addr", "Address", IndexGroup = Ipv6IndexGroup)]
    private FieldId _AddrFieldId;

    #endregion

    #region Extension Header Field IDs (optional, grouped under "ipv6.ext")

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

    #region Pre-allocated populator (created once in OnStartCustom, shared across all packets)

    /// <summary>Pre-allocated delegate for IPv6 field population — captures only 'this'.</summary>
    private LazyPopulator _Populator = null!;

    // Dense dispatch cache for the IPv6 next-header byte (256 entries, ~2 kB).
    // Built once in OnStart from the ip.proto table (shared with IPv4); avoids a dictionary
    // lookup per packet. Pre-bound delegates for direct invocation without vtable dispatch.
    private ParseDelegate?[] _IpProtoDelegateCache = [];

    // Pre-allocated extension header field IDs struct, built once in OnStartCustom.
    private ExtHeaderFieldIds _ExtHeaderFieldIds;

    // IPv6 fragment reassembly engine — keyed by (src, dst, identification).
    // RFC 5722: overlapping fragments MUST cause the entire datagram to be silently discarded.
    private readonly DatagramDefragmenter<IPv6DatagramFragmentKey> _Defragmenter = new(dropOnOverlap: true);

    /// <summary>
    /// Pre-allocates the lazy-field populator delegate and builds the next-header dispatch cache.
    /// Neither allocation occurs per packet — both are one-time costs at stack start.
    /// </summary>
    partial void OnStartCustom(Stack stack)
    {
        _Populator = (in MutField container) => PopulateIPv6Fields(in container);
        _ExtHeaderFieldIds = BuildExtHeaderFieldIds();
        // IPv6 next-header is also an 8-bit IP protocol number; share the same ip.proto table.
        // Delegate cache stores pre-bound ParseDelegate for direct invocation.
        _IpProtoDelegateCache = stack.BuildU64DelegateCache(_IpProtoTableId, 256);
    }

    /// <summary>
    /// Populates IPv6 child fields at materialisation time.
    /// Re-parses the fixed 40-byte header and walks extension headers from the stored bytes
    /// to avoid per-packet closure allocations.
    /// </summary>
    private ParseResult PopulateIPv6Fields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = data.Span;
        if (!IPv6Header.TryParse(span, out IPv6Header header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        byte trafficClass = header.TrafficClass;
        byte dscp = (byte)(trafficClass >> 2);
        byte ecn = (byte)(trafficClass & 0x03);
        uint flowLabel = header.FlowLabel;
        ushort payloadLength = header.PayloadLength.Value;
        byte nextHeader = header.NextHeader;
        byte hopLimit = header.HopLimit;

        container.Append(_VersionFieldId, FieldValue.NewU64(header.Version), in context);
        container.Append(_TclassFieldId, FieldValue.NewU64(trafficClass), in context);

        string dscpText = DisplayTables.GetDscpDisplayText(dscp);
        container.AppendWithCustomText(_DscpFieldId, FieldValue.NewU64(dscp), dscpText, in context);

        string ecnText = DisplayTables.GetEcnDisplayText(ecn);
        container.AppendWithCustomText(_EcnFieldId, FieldValue.NewU64(ecn), ecnText, in context);

        container.Append(_FlowFieldId, FieldValue.NewU64(flowLabel), in context);
        container.Append(_PayloadLenFieldId, FieldValue.NewU64(payloadLength), in context);

        string nxtText = DisplayTables.GetIpProtocolDisplayText(nextHeader);
        container.AppendWithCustomText(_NextHeaderFieldId, FieldValue.NewU64(nextHeader), nxtText, in context);

        container.Append(_HopLimitFieldId, FieldValue.NewU64(hopLimit), in context);

        // Parse extension headers into individual sub-fields if present.
        // The stored data includes the full fixed header + extension header region so
        // the parser can re-walk extension headers with field decomposition.
        if (data.Length > IPv6Header.HeaderSize && IsExtensionHeader(nextHeader))
        {
            ReadOnlySpan<byte> extData = span[IPv6Header.HeaderSize..];
            IPv6ExtensionHeaderParser.Parse(container, extData, nextHeader, in _ExtHeaderFieldIds, in context);
        }

        // ipv6.addr any-match fields — deferred to the lazy populator to avoid
        // 2 boxing allocations (NewIPv6) per packet on the hot parse path.
        // Re-extract src/dst from the stored header bytes.
        IPv6Address populatorSrc = IPv6Header.GetSrc(span);
        IPv6Address populatorDst = IPv6Header.GetDst(span);
        container.Append(_AddrFieldId, FieldValue.NewIPv6(populatorSrc), in context);
        container.Append(_AddrFieldId, FieldValue.NewIPv6(populatorDst), in context);

        return 0;
    }

    /// <summary>
    /// Parses a IPv6 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < IPv6Header.HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Ipv6GroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Parse header using BinaryParsable-generated parser
        if (!IPv6Header.TryParse(span, out IPv6Header header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv6Header.HeaderSize, (ulong)data.Length);
        }

        byte version = header.Version;
        if (version != IPv6Header.ExpectedVersion)
        {
            return ParseError.InvalidData(ProtocolName, $"Expected version 6, got {version}");
        }

        // Only extract fields needed for: version check, extension-header walk, dispatch, index recording,
        // summary closure, and eager src/dst append. All other fields are deferred to PopulateIPv6Fields.
        ushort payloadLength = header.PayloadLength.Value;
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
            }
            else if (finalNextHeader == NhEsp)
            {
                // ESP terminates extension header chain — payload is encrypted
                break;
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
                finalNextHeader = span[extOffset]; // next header at offset 0
                extOffset += extLen;
                extTotalLen += extLen;
            }

            depthCount++;
        }

        // Determine whether extension headers were found
        bool hasExtHeaders = extTotalLen > 0;
        if (hasExtHeaders)
        {
            context.RecordGroupPresence(_Ipv6ExtGroupId);
        }
        if (hasFragment)
        {
            context.RecordGroupPresence(_Ipv6FraghdrGroupId);
        }

        // Summary closure captures only src + dst (2 × 16-byte structs) — smaller than the full header.
        LazyString summary = ZA.Lazy(
            "Internet Protocol Version 6, Src: ", src, ", Dst: ", dst);

        // Store the full IPv6 data (fixed header + extension headers) so PopulateIPv6Fields
        // can reconstruct all fields including extension header sub-fields.
        int storedLen = hasExtHeaders
            ? Math.Min(IPv6Header.HeaderSize + extTotalLen, data.Length)
            : IPv6Header.HeaderSize;
        ReadOnlyMemory<byte> headerBytes = data[..storedLen];
        FieldValue headerValue = FieldValue.NewBytes(headerBytes)
            .WithCustomRepresentation(new LazyString("40 bytes"));
        MutField protoField = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, headerValue, summary, _Populator);

        // Eagerly append src/dst as non-lazy children so downstream protocols
        // (e.g., UDP/TCP) can read IPv6 addresses from the tree without materializing.
        // ipv6.addr any-match fields are deferred to the lazy populator to avoid
        // 2 boxing allocations (NewIPv6) per packet on the hot parse path.
        protoField.Append(_SrcFieldId, FieldValue.NewIPv6(src), in context);
        protoField.Append(_DstFieldId, FieldValue.NewIPv6(dst), in context);

        // Cache IPv6 addresses in the thread-local field directly on this protocol
        // so downstream transport protocols (TCP, UDP) can read them without
        // sibling-walk field-tree navigation.
        SetCachedAddresses(parentField.Packet.Id, src, dst);

        // Dispatch to next protocol using the final next-header value (after walking extension chain)
        int payloadStart = extOffset; // after fixed header + all extension headers
        int payloadLen = Math.Max(0, Math.Min(payloadLength - extTotalLen, data.Length - payloadStart));

        if (payloadLen > 0 && _IpProtoTableId.IsValid)
        {
            // IPv6 fragment reassembly: if fragment header was detected, route through defragmenter
            if (hasFragment)
            {
                bool isFragment = moreFragments || fragOffset != 0;
                if (isFragment)
                {
                    IPv6DatagramFragmentKey key = new(src.High, src.Low, dst.High, dst.Low, fragIdentification);
                    int byteOffset = fragOffset * 8; // convert to byte offset

                    byte[]? reassembled = _Defragmenter.ProcessFragment(
                        key, byteOffset, moreFragments, data.Span.Slice(payloadStart, payloadLen));

                    if (reassembled is not null)
                    {
                        // All fragments received — dispatch the reassembled datagram
                        ReadOnlyMemory<byte> reassembledPayload = reassembled;
                        ParseResult dispatchResult = DispatchNextHeader(
                            in parentField, finalNextHeader, reassembledPayload, in context);
                        if (dispatchResult.IsError)
                        {
                            return dispatchResult;
                        }
                    }
                    // Else: fragment stored, waiting for more fragments — no dispatch yet
                }
                else
                {
                    // unfragmented packet that happens to have a fragment header (offset=0, MF=0)
                    ReadOnlyMemory<byte> payload = data.Slice(payloadStart, payloadLen);
                    ParseResult dispatchResult = DispatchNextHeader(
                        in parentField, finalNextHeader, payload, in context);
                    if (dispatchResult.IsError)
                    {
                        return dispatchResult;
                    }
                }
            }
            else
            {
                // No fragment header — dispatch directly
                ReadOnlyMemory<byte> payload = data.Slice(payloadStart, payloadLen);
                ParseResult dispatchResult = DispatchNextHeader(in parentField, finalNextHeader, payload, in context);
                if (dispatchResult.IsError)
                {
                    return dispatchResult;
                }
            }
        }

        // Return total consumed: fixed header + payload length (clamped to available data)
        return Math.Min(IPv6Header.HeaderSize + payloadLength, data.Length);
    }

    /// <summary>
    /// Dispatches to the next protocol by IPv6 next-header (IP protocol) number.
    /// Uses the pre-cached delegate for the common single-protocol case (O(1) array lookup);
    /// falls back to full table dispatch for multi-protocol keys or entries outside the cache.
    /// </summary>
    private ParseResult DispatchNextHeader(
        in MutField parentField, byte nextHeader, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        // _IpProtoDelegateCache has 256 entries after OnStart; non-null means single registered protocol.
        // Direct delegate call — no ProtocolId resolution, no bounds check, no vtable dispatch.
        ParseDelegate? fastParse = _IpProtoDelegateCache.Length > 0 ? _IpProtoDelegateCache[nextHeader] : null;
        return fastParse is not null
            ? fastParse(in parentField, payload, in context)
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
    /// to <see cref="IPv6ExtensionHeaderParser"/>.
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
    private ExtHeaderFieldIds BuildExtHeaderFieldIds() => new()
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
    /// Written by <see cref="Parse"/> before dispatching; consumed by downstream
    /// protocols (TCP, UDP). Null means no data cached yet on this thread.
    /// </summary>
    [ThreadStatic]
    private static (int PacketId, IPv6Address Src, IPv6Address Dst)? _ThreadCache;

    /// <summary>Caches the IPv6 src/dst addresses for the current packet on this thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCachedAddresses(PacketId packetId, IPv6Address src, IPv6Address dst)
        => _ThreadCache = (packetId.Value, src, dst);

    /// <summary>
    /// Attempts to read the cached IPv6 addresses for the specified packet.
    /// Returns <see langword="false"/> if no data is cached or the packet ID
    /// does not match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCachedAddresses(PacketId packetId, out IPv6Address src, out IPv6Address dst)
    {
        (int PacketId, IPv6Address Src, IPv6Address Dst)? c = _ThreadCache;
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
[BinaryParsable]
internal readonly partial struct IPv6Header
{
    /// <summary>IP version (4 bits, must be 6).</summary>
    [BinaryField(BitCount = 4)]
    public byte Version
    {
        get; init;
    }

    /// <summary>Traffic Class (8 bits: DSCP + ECN).</summary>
    [BinaryField(BitCount = 8)]
    public byte TrafficClass
    {
        get; init;
    }

    /// <summary>Flow Label (20 bits).</summary>
    [BinaryField(BitCount = 20)]
    public uint FlowLabel
    {
        get; init;
    }

    /// <summary>Payload length in bytes (excluding the 40-byte fixed header).</summary>
    public U16BE PayloadLength
    {
        get; init;
    }

    /// <summary>Next header protocol number (same as IPv4 Protocol field).</summary>
    public byte NextHeader
    {
        get; init;
    }

    /// <summary>Hop limit (decremented by each router).</summary>
    public byte HopLimit
    {
        get; init;
    }

    /// <summary>Source address high 64 bits.</summary>
    public U64BE SrcHi
    {
        get; init;
    }

    /// <summary>Source address low 64 bits.</summary>
    public U64BE SrcLo
    {
        get; init;
    }

    /// <summary>Destination address high 64 bits.</summary>
    public U64BE DstHi
    {
        get; init;
    }

    /// <summary>Destination address low 64 bits.</summary>
    public U64BE DstLo
    {
        get; init;
    }

    /// <summary>Fixed header size in bytes (40).</summary>
    internal const int HeaderSize = 40;

    /// <summary>Expected IP version number.</summary>
    internal const byte ExpectedVersion = 6;

    /// <summary>Extracts the source IPv6 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address GetSrc(ReadOnlySpan<byte> data) => IPv6Address.FromBytes(data[8..24]);

    /// <summary>Extracts the destination IPv6 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv6Address GetDst(ReadOnlySpan<byte> data) => IPv6Address.FromBytes(data[24..40]);
    #endregion
}
