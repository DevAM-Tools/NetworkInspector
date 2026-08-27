// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Internet Protocol version 4 (RFC 791) parser.
/// <para>Field tree structure:</para>
/// <code>
/// ip: Internet Protocol Version 4, Src: 192.168.1.100, Dst: 10.0.0.1
/// ├── ip.version: 4
/// ├── ip.hdr_len: 20
/// ├── ip.dscp: 0 (Default (BE))
/// ├── ip.ecn: 0 (Not-ECT)
/// ├── ip.len: 60
/// ├── ip.id: 0x1234
/// ├── ip.flags: [DF]
/// │   ├── ip.flags.rb: false (Reserved)
/// │   ├── ip.flags.df: true (Don't Fragment)
/// │   └── ip.flags.mf: false (More Fragments)
/// ├── ip.frag_offset: 0
/// ├── ip.ttl: 64
/// ├── ip.proto: 17 (UDP)
/// ├── ip.checksum: 0xabcd
/// ├── ip.checksum.status: [Good] / [Bad]  [optional, when verification enabled]
/// ├── ip.src: 192.168.1.100                 [eager]
/// ├── ip.dst: 10.0.0.1                       [eager]
/// └── ip.options: Options (N bytes)       [optional, when IHL > 5]
///     ├── ip.opt.record_route: Record Route (N bytes, M entries)
///     │   ├── ip.opt.type: 7 (Record Route)
///     │   ├── ip.opt.type.copy: false
///     │   ├── ip.opt.type.class: 0 (Control)
///     │   ├── ip.opt.type.number: 7
///     │   ├── ip.opt.len: N
///     │   ├── ip.opt.ptr: P
///     │   └── ip.opt.addr: 192.168.1.1 (repeated)
///     ├── ip.opt.loose_source_route: Loose Source Route (...)
///     ├── ip.opt.strict_source_route: Strict Source Route (...)
///     ├── ip.opt.timestamp: Internet Timestamp (...)
///     │   ├── ip.opt.type: ...
///     │   ├── ip.opt.len: N
///     │   ├── ip.opt.ptr: P
///     │   ├── ip.opt.overflow: O
///     │   ├── ip.opt.flag: F
///     │   ├── ip.opt.time_stamp: T (timestamps only mode)
///     │   └── ip.opt.time_stamp_addr + ip.opt.time_stamp (addr+ts mode)
///     ├── ip.opt.router_alert: Router Alert: ...
///     ├── ip.opt.security: Security (N bytes)
///     ├── ip.opt.stream_id: Stream Identifier: N
///     ├── ip.opt.nop: No-Operation (NOP)
///     ├── ip.opt.eol: End of Options List (EOL)
///     └── ip.opt.padding: (remaining bytes after EOL)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The first parse of a packet id feeds the fragment reassembly engine
/// and must therefore stay ordered and single-threaded (the session guarantees this under its parse
/// lock). Every later parse of an already-parsed id is a replay: it reads the recorded
/// defragmentation outcome from the protocol-owned effect store instead of touching the fragment
/// buffers, and is safe to run on any number of threads concurrently. The watermark
/// <see cref="_IngestWatermark"/> separates both paths without any caller-supplied mode flag.</para>
/// </remarks>
[Protocol("ip", "Internet Protocol Version 4", Description = "IPv4 (RFC 791)")]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey)]
public sealed partial class IPv4Protocol : IProtocol
{
    /// <summary>Expected IP version number.</summary>
    private const byte _ExpectedVersion = 4;

    #region Table Key Constants

    /// <summary>EtherType key for IPv4 (0x0800).</summary>
    public const ulong EtherTypeKey = 0x0800;

    #endregion

    #region Table Name Constants

    /// <summary>Dispatch table name for IP protocol number lookup.</summary>
    public const string IpProtoTableName = "ip.proto";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present IPv4 fields.</summary>
    private const string _IpIndexGroup = "ip";

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("ip", "IPv4", IndexGroup = _IpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("ip.version", "Version", IndexGroup = _IpIndexGroup)]
    private FieldId _VersionFieldId;

    [U64Field("ip.hdr_len", "Header Length", IndexGroup = _IpIndexGroup)]
    private FieldId _HdrLenFieldId;

    [U64Field("ip.dscp", "DSCP", IndexGroup = _IpIndexGroup)]
    private FieldId _DscpFieldId;

    [U64Field("ip.ecn", "ECN", IndexGroup = _IpIndexGroup)]
    private FieldId _EcnFieldId;

    [U64Field("ip.len", "Total Length", IndexGroup = _IpIndexGroup)]
    private FieldId _TotalLenFieldId;

    [U64Field("ip.id", "Identification", IndexGroup = _IpIndexGroup)]
    private FieldId _IdFieldId;

    [NoneField("ip.flags", "Flags", IndexGroup = _IpIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("ip.flags.rb", "Reserved bit", IndexGroup = _IpIndexGroup)]
    private FieldId _FlagsRbFieldId;

    [BoolField("ip.flags.df", "Don't Fragment", IndexGroup = _IpIndexGroup)]
    private FieldId _FlagsDfFieldId;

    [BoolField("ip.flags.mf", "More Fragments", IndexGroup = _IpIndexGroup)]
    private FieldId _FlagsMfFieldId;

    [U64Field("ip.frag_offset", "Fragment Offset", IndexGroup = _IpIndexGroup)]
    private FieldId _FragOffsetFieldId;

    [U64Field("ip.ttl", "Time To Live", IndexGroup = _IpIndexGroup)]
    private FieldId _TtlFieldId;

    [U64Field(IpProtoTableName, "Protocol", IndexGroup = _IpIndexGroup)]
    private FieldId _ProtoFieldId;

    [U64Field("ip.checksum", "Header Checksum", IndexGroup = _IpIndexGroup)]
    private FieldId _ChecksumFieldId;

    // Checksum validation status (optional, only when verification enabled)
    [StringField("ip.checksum.status", "Checksum Status", IndexGroup = "ip.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    [IPv4Field("ip.src", "Source Address", IndexGroup = _IpIndexGroup)]
    private FieldId _SrcFieldId;

    [IPv4Field("ip.dst", "Destination Address", IndexGroup = _IpIndexGroup)]
    private FieldId _DstFieldId;

    // Field alias group ID assigned in _RegisterFieldsCustom for "ip.addr" -> { ip.src, ip.dst }.
    // Alias names are metadata-only and never resolve through GetFieldId; the parse tree
    // contains no ip.addr node.
    private FieldAliasGroupId _AddrAliasGroupId;

    // Options container field (optional, when IHL > 5)
    [NoneField("ip.options", "Options", IndexGroup = "ip.options")]
    private FieldId _OptionsFieldId;

    #endregion

    #region Option sub-field IDs (registered via OptionFieldIds)

    // Type decomposition fields (repeated per option)
    [U64Field("ip.opt.type", "Type", IndexGroup = "ip.options")]
    private FieldId _OptTypeFieldId;

    [BoolField("ip.opt.type.copy", "Copied on fragmentation", IndexGroup = "ip.options")]
    private FieldId _OptTypeCopyFieldId;

    [U64Field("ip.opt.type.class", "Class", IndexGroup = "ip.options")]
    private FieldId _OptTypeClassFieldId;

    [U64Field("ip.opt.type.number", "Number", IndexGroup = "ip.options")]
    private FieldId _OptTypeNumberFieldId;

    // Common option fields
    [U64Field("ip.opt.len", "Length", IndexGroup = "ip.options")]
    private FieldId _OptLenFieldId;

    [U64Field("ip.opt.ptr", "Pointer", IndexGroup = "ip.options")]
    private FieldId _OptPtrFieldId;

    [BytesField("ip.opt.padding", "Padding", IndexGroup = "ip.options")]
    private FieldId _OptPaddingFieldId;

    [BytesField("ip.opt.data", "Data", IndexGroup = "ip.options")]
    private FieldId _OptDataFieldId;

    // Per-option-type container fields
    [NoneField("ip.opt.eol", "End of Options List", IndexGroup = "ip.options")]
    private FieldId _OptEolFieldId;

    [NoneField("ip.opt.nop", "No-Operation", IndexGroup = "ip.options")]
    private FieldId _OptNopFieldId;

    [NoneField("ip.opt.record_route", "Record Route", IndexGroup = "ip.options")]
    private FieldId _OptRecordRouteFieldId;

    [NoneField("ip.opt.loose_source_route", "Loose Source Route", IndexGroup = "ip.options")]
    private FieldId _OptLooseSourceRouteFieldId;

    [NoneField("ip.opt.strict_source_route", "Strict Source Route", IndexGroup = "ip.options")]
    private FieldId _OptStrictSourceRouteFieldId;

    [NoneField("ip.opt.timestamp", "Internet Timestamp", IndexGroup = "ip.options")]
    private FieldId _OptTimestampFieldId;

    [NoneField("ip.opt.router_alert", "Router Alert", IndexGroup = "ip.options")]
    private FieldId _OptRouterAlertFieldId;

    [NoneField("ip.opt.security", "Security", IndexGroup = "ip.options")]
    private FieldId _OptSecurityFieldId;

    [NoneField("ip.opt.stream_id", "Stream Identifier", IndexGroup = "ip.options")]
    private FieldId _OptStreamIdFieldId;

    [NoneField("ip.opt.unknown", "Unknown Option", IndexGroup = "ip.options")]
    private FieldId _OptUnknownFieldId;

    // Specific value fields
    [IPv4Field("ip.opt.addr", "Address", IndexGroup = "ip.options")]
    private FieldId _OptAddrFieldId;

    [U64Field("ip.opt.overflow", "Overflow", IndexGroup = "ip.options")]
    private FieldId _OptOverflowFieldId;

    [U64Field("ip.opt.flag", "Flag", IndexGroup = "ip.options")]
    private FieldId _OptFlagFieldId;

    [U64Field("ip.opt.time_stamp", "Timestamp", IndexGroup = "ip.options")]
    private FieldId _OptTimeStampFieldId;

    [IPv4Field("ip.opt.time_stamp_addr", "Address", IndexGroup = "ip.options")]
    private FieldId _OptTimeStampAddrFieldId;

    [U64Field("ip.opt.ra", "Router Alert", IndexGroup = "ip.options")]
    private FieldId _OptRaFieldId;

    [U64Field("ip.opt.sid", "Stream ID", IndexGroup = "ip.options")]
    private FieldId _OptSidFieldId;

    // Protocol dispatch table
    [ProtocolTableU64(IpProtoTableName, "IP Protocol")]
    private ProtocolTableId _IpProtoTableId;

    // Runtime setting for checksum verification
    [BoolSetting("ip.verify_checksum", "Verify Checksum", "ip", Default = false)]
    private bool _VerifyChecksum;

    // Pre-allocated delegate: created once in _OnStartCustom, reused for every packet.
    // Captures only `this` (singleton) — zero per-packet allocation.
    private LazyPopulator _Populator = null!;

    // Dense dispatch cache for the IP protocol byte (256 entries).
    // Built once in OnStart from the ip.proto table; avoids a dictionary lookup per packet.
    // Non-null entry → single protocol; CallProtocol returns ParseError for invalid ids.
    // Null entry → zero or multiple protocols → fall back to full table dispatch.
    private ProtocolId?[] _IpProtoIdCache = [];

    // Pre-allocated option field IDs struct, built once in _OnStartCustom.
    private OptionFieldIds _OptionFieldIds;

    // IP fragment reassembly engine — datagram-based, keyed by (src, dst, id, protocol).
    private readonly DatagramDefragmenter<DatagramFragmentKey> _Defragmenter = new();

    /// <summary>
    /// Pre-allocates the lazy-field populator delegate and builds the IP-protocol dispatch cache.
    /// Neither allocation occurs per packet — both are one-time costs at stack start.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateIPv4;
        _OptionFieldIds = _BuildOptionFieldIds();
        // Pre-build 256-entry dense cache: IP protocol field is a u8, so the full domain
        // fits in ~1 kB and direct array indexing replaces dictionary lookup per packet.
        _IpProtoIdCache = stack.BuildU64IdCache(_IpProtoTableId, 256);
    }

    /// <summary>
    /// Registers protocol-owned alias groups. Adds "ip.addr" -> { ip.src, ip.dst } as
    /// metadata; the alias is reachable only via the alias-group APIs on IStack.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _AddrAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "ip.addr",
            "Any-match alias for source/destination IPv4 addresses.",
            [_SrcFieldId, _DstFieldId]);
    }

    /// <summary>
    /// Lazy populator: version, header length, DSCP, ECN, total length, identification,
    /// flags, fragment offset, TTL, checksum, and options. Source address, destination
    /// address, and protocol number are eagerly appended by <see cref="IProtocol.Parse"/>.
    /// Called on first access of the IPv4 container's children.
    /// </summary>
    private ParseResult _PopulateIPv4(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> headerBytes))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        if (headerBytes.Length < IPv4Header.MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv4Header.MinHeaderSize, (ulong)headerBytes.Length);
        }

        ReadOnlySpan<byte> span = headerBytes.Span;
        if (!IPv4Header.TryParse(span, out IPv4Header header, out _))
        {
            return ParseError.InvalidData(ProtocolName, "Failed to parse IPv4 header");
        }

        IPv4Address src = IPv4Header.GetSrc(span);
        IPv4Address dst = IPv4Header.GetDst(span);

        // ip.src and ip.dst are eagerly appended by Parse() as non-lazy children so that
        // IpAddressExtractor can find them without materialising the lazy group.
        // The populator skips them to avoid duplicate entries.

        // ip.addr is exposed as an alias group registered in _RegisterFieldsCustom; no
        // duplicate ip.addr field node is appended to the parse tree.

        byte version = header.Version;
        int headerLen = header.HeaderLength; // bytes
        byte dscp = header.Dscp;
        byte ecn = header.Ecn;
        ushort totalLength = header.TotalLength.Value;
        ushort identification = header.Identification.Value;
        bool reservedBit = header.ReservedFlag != 0;
        bool dontFragment = header.DontFragment != 0;
        bool moreFragments = header.MoreFragments != 0;
        ushort fragOffset = header.FragmentOffset;
        byte ttl = header.Ttl;
        byte protocol = header.Protocol;
        ushort checksum = header.Checksum.Value;

        container.Append(_VersionFieldId, FieldValue.NewU64(version));

        string hdrLenText = DisplayTables.GetHeaderLengthDisplayText(headerLen);
        container.AppendWithCustomText(_HdrLenFieldId, FieldValue.NewU64((ulong)headerLen), hdrLenText);

        string dscpText = DisplayTables.GetDscpDisplayText(dscp);
        container.AppendWithCustomText(_DscpFieldId, FieldValue.NewU64(dscp), dscpText);

        string ecnText = DisplayTables.GetEcnDisplayText(ecn);
        container.AppendWithCustomText(_EcnFieldId, FieldValue.NewU64(ecn), ecnText);

        container.Append(_TotalLenFieldId, FieldValue.NewU64(totalLength));

        string idText = DisplayTables.FormatHexU16(identification);
        container.AppendWithCustomText(_IdFieldId, FieldValue.NewU64(identification), idText);

        // Flags container — precomputed display text lists active flag abbreviations.
        MutField flagsField = container.AppendWithCustomText(
            _FlagsFieldId, FieldValue.None,
            IPv4.IPv4FlagsFormatter.Format(reservedBit, dontFragment, moreFragments));
        flagsField.Append(_FlagsRbFieldId, FieldValue.NewBool(reservedBit));
        flagsField.Append(_FlagsDfFieldId, FieldValue.NewBool(dontFragment));
        flagsField.Append(_FlagsMfFieldId, FieldValue.NewBool(moreFragments));
        container.Append(_FragOffsetFieldId, FieldValue.NewU64(fragOffset));
        container.Append(_TtlFieldId, FieldValue.NewU64(ttl));

        // ip.proto is eagerly appended by Parse() so it is present without materialising the lazy ip group.

        string csumText = DisplayTables.FormatHexU16(checksum);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText);

        // IPV4-02: Re-verify checksum from the stored header bytes at materialisation time.
        if (_VerifyChecksum)
        {
            ushort computed = InternetChecksum.Compute(span);
            bool checksumValid = computed == 0;
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(checksumValid ? "[Good]" : "[Bad]"));
        }

        // IPV4-01: Parse individual options when present (IHL > 5).
        if (headerBytes.Length > IPv4Header.MinHeaderSize)
        {
            int optionsLen = headerBytes.Length - IPv4Header.MinHeaderSize;
            ReadOnlySpan<byte> optionsSpan = span[IPv4Header.MinHeaderSize..headerBytes.Length];

            MutField optionsContainer = container.AppendWithCustomText(
                _OptionsFieldId, FieldValue.None,
                (string)ZA.String("Options (", optionsLen, " bytes)"));

            IPv4OptionsParser.Parse(optionsContainer, optionsSpan, in _OptionFieldIds);
        }

        return 0;
    }

    /// <summary>
    /// Parses a IPv4 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// <para>
    /// Fragmented packets are stateful: the first parse feeds the fragment into the shared
    /// defragmenter and records the outcome, every later parse of that packet id replays the recorded
    /// outcome and leaves the fragment buffers untouched. See <see cref="_IsReplay"/>.
    /// </para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Packet packet = parentField.Packet;
        int layerKey = parentField.Packet.GetEffectLayerKey(data);
        bool isReplay = _IsReplay(packet.Id);
        bool raiseWatermark = !isReplay && _ParseNesting == 0;
        if (!isReplay)
        {
            _ParseNesting++;
        }

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
    /// Parse body. <paramref name="layerKey"/> is <see cref="Packet.GetEffectLayerKey"/> at the parse call and
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
        if (data.Length < IPv4Header.MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv4Header.MinHeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_IpGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Parse header using BinaryParsable-generated parser
        if (!IPv4Header.TryParse(span, out IPv4Header header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, IPv4Header.MinHeaderSize, (ulong)data.Length);
        }

        byte version = header.Version;
        int headerLen = header.HeaderLength; // bytes
        if (version != _ExpectedVersion)
        {
            return ParseError.InvalidData(ProtocolName, $"Expected version 4, got {version}");
        }
        if (headerLen < IPv4Header.MinHeaderSize || headerLen > data.Length)
        {
            return ParseError.InvalidData(ProtocolName, $"Invalid header length: {headerLen}");
        }

        // Only extract the fields needed at parse time: src/dst for the summary
        // string and the thread-local address cache, and protocol + totalLength
        // for dispatch. All other header fields are re-parsed lazily inside
        // PopulateIPv4Fields.
        IPv4Address src = IPv4Header.GetSrc(span);
        IPv4Address dst = IPv4Header.GetDst(span);
        byte protocol = header.Protocol;
        ushort totalLength = header.TotalLength.Value;

        // Validate checksum at parse time if setting is enabled.
        // RecordIndexPresence must happen at parse time, not during lazy materialisation.
        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_IpChecksumStatusGroupId);
        }

        // Detect options presence and record the optional index group at parse time.
        bool hasOptions = headerLen > IPv4Header.MinHeaderSize;
        if (hasOptions)
        {
            context.RecordGroupPresence(_IpOptionsGroupId);
        }

        // Summary closure captures only src and dst (8 bytes total as IPv4Address value types).
        LazyString summary = ZA.Lazy(
            "Internet Protocol Version 4, Src: ", src, ", Dst: ", dst);

        // Store the full header bytes (up to headerLen) in the field value so that
        // PopulateIPv4Fields can re-parse all fields without any captured state.
        // Use DisplayTables.GetHeaderLengthDisplayText() to avoid per-packet string allocation
        // for header length representation (table covers all valid IPv4 IHL values 20–60).
        ReadOnlyMemory<byte> headerBytes = data[..headerLen];
        string hdrLenRepresentation = DisplayTables.GetHeaderLengthDisplayText(headerLen);
        FieldValue headerValue = FieldValue.NewBytes(headerBytes)
            .WithCustomRepresentation(hdrLenRepresentation);
        MutField ipContainer = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, headerValue, summary, _Populator);

        // Eagerly append ip.src, ip.dst, and ip.proto as non-lazy children so downstream
        // protocols (e.g., TCP/UDP) can locate IPv4 addresses via IpAddressExtractor
        // without materialising the lazy ip group.
        ipContainer.Append(_SrcFieldId, FieldValue.NewIPv4(src));
        ipContainer.Append(_DstFieldId, FieldValue.NewIPv4(dst));
        ipContainer.AppendWithCustomText(_ProtoFieldId, FieldValue.NewU64(protocol), DisplayTables.GetIpProtocolDisplayText(protocol));

        // Cache raw IPv4 addresses in the thread-local field directly on this protocol
        // so downstream transport protocols (TCP, UDP) can read them without
        // sibling-walk field-tree navigation.
        SetCachedAddresses(parentField.Packet.Id, src, dst);

        // Dispatch to next protocol on parentField (sibling dispatch).
        // Fragment handling: if MF is set or fragment offset is non-zero, this is a fragment
        // that needs reassembly before dispatching to the next protocol.
        int payloadStart = headerLen;
        bool moreFragments = header.MoreFragments != 0;
        ushort fragOffset = header.FragmentOffset;

        // Guard against negative payload length with Math.Max(0, ...)
        int payloadLen = Math.Max(0, Math.Min(totalLength - headerLen, data.Length - headerLen));
        if (payloadLen > 0)
        {
            bool isFragment = moreFragments || fragOffset != 0;

            if (isFragment)
            {
                byte[]? reassembled;
                if (isReplay)
                {
                    // Replay: never touch the shared fragment buffers from what may be a reader
                    // thread. A missing effect means the first parse never reached the defragmenter,
                    // so there is nothing to dispatch and the fragment fields stand on their own.
                    reassembled = _FindReassembledDatagram(parentField.Packet.Id, layerKey);
                }
                else
                {
                    // Route through defragmenter — fragment offset is in 8-byte units
                    DatagramFragmentKey key = new(src.RawValue, dst.RawValue,
                        header.Identification.Value, protocol);
                    int byteOffset = fragOffset * 8; // convert to byte offset

                    reassembled = _Defragmenter.ProcessFragment(
                        key, byteOffset, moreFragments, data.Span.Slice(payloadStart, payloadLen));
                    _RecordDefragEffect(parentField.Packet.Id, layerKey, reassembled);
                }

                if (reassembled is not null)
                {
                    // Bind the datagram so nested Parse can key effects on this packet's buffers.
                    ReadOnlyMemory<byte> reassembledPayload = parentField.Packet.BindParseBuffer(reassembled);
                    ParseResult dispatchResult = _DispatchIpProtocol(
                        in parentField, protocol, reassembledPayload, in context);
                    if (dispatchResult.TryPropagateError(out ParseResult error))
                    {
                        return error;
                    }
                }
                // Else: fragment stored, waiting for more fragments — no dispatch yet
            }
            else
            {
                // Non-fragmented packet — dispatch directly
                ReadOnlyMemory<byte> payload = data.Slice(payloadStart, payloadLen);
                ParseResult dispatchResult = _DispatchIpProtocol(
                    in parentField, protocol, payload, in context);
                if (dispatchResult.TryPropagateError(out ParseResult error))
                {
                    return error;
                }
            }
        }

        return Math.Min(totalLength, data.Length);
    }

    /// <summary>
    /// Dispatches to the next protocol by IP protocol number.
    /// Uses the pre-cached <see cref="ProtocolId"/> for the common single-protocol case;
    /// falls back to full table dispatch for multi-protocol keys or entries outside the cache.
    /// </summary>
    private ParseResult _DispatchIpProtocol(
        in MutField parentField, byte protocol, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        ProtocolId? cached = _IpProtoIdCache.Length > 0 ? _IpProtoIdCache[protocol] : null;
        return cached is ProtocolId protocolId
            ? parentField.CallProtocol(protocolId, payload, in context)
            : parentField.TryCallNextProtocolU64(_IpProtoTableId, protocol, payload, in context);
    }

    /// <summary>
    /// Bundles all option-related field IDs into a single struct for efficient passing
    /// to <see cref="IPv4OptionsParser"/>. Avoids passing 20+ individual FieldId parameters.
    /// </summary>
    internal readonly struct OptionFieldIds
    {
        // Option type decomposition
        internal FieldId OptTypeFieldId
        {
            get; init;
        }
        internal FieldId OptTypeCopyFieldId
        {
            get; init;
        }
        internal FieldId OptTypeClassFieldId
        {
            get; init;
        }
        internal FieldId OptTypeNumberFieldId
        {
            get; init;
        }

        // Common
        internal FieldId OptLenFieldId
        {
            get; init;
        }
        internal FieldId OptPtrFieldId
        {
            get; init;
        }
        internal FieldId PaddingFieldId
        {
            get; init;
        }
        internal FieldId OptDataFieldId
        {
            get; init;
        }

        // Per-option containers
        internal FieldId EolFieldId
        {
            get; init;
        }
        internal FieldId NopFieldId
        {
            get; init;
        }
        internal FieldId RecordRouteFieldId
        {
            get; init;
        }
        internal FieldId LooseSourceRouteFieldId
        {
            get; init;
        }
        internal FieldId StrictSourceRouteFieldId
        {
            get; init;
        }
        internal FieldId TimestampFieldId
        {
            get; init;
        }
        internal FieldId RouterAlertFieldId
        {
            get; init;
        }
        internal FieldId SecurityFieldId
        {
            get; init;
        }
        internal FieldId StreamIdFieldId
        {
            get; init;
        }
        internal FieldId UnknownFieldId
        {
            get; init;
        }

        // Specific value fields
        internal FieldId OptAddrFieldId
        {
            get; init;
        }
        internal FieldId OptOverflowFieldId
        {
            get; init;
        }
        internal FieldId OptFlagFieldId
        {
            get; init;
        }
        internal FieldId OptTimeStampFieldId
        {
            get; init;
        }
        internal FieldId OptTimeStampAddrFieldId
        {
            get; init;
        }
        internal FieldId OptRaFieldId
        {
            get; init;
        }
        internal FieldId OptSidFieldId
        {
            get; init;
        }
    }

    /// <summary>Builds the <see cref="OptionFieldIds"/> struct from the protocol's registered fields.</summary>
    private OptionFieldIds _BuildOptionFieldIds() => new()
    {
        OptTypeFieldId = _OptTypeFieldId,
        OptTypeCopyFieldId = _OptTypeCopyFieldId,
        OptTypeClassFieldId = _OptTypeClassFieldId,
        OptTypeNumberFieldId = _OptTypeNumberFieldId,
        OptLenFieldId = _OptLenFieldId,
        OptPtrFieldId = _OptPtrFieldId,
        PaddingFieldId = _OptPaddingFieldId,
        OptDataFieldId = _OptDataFieldId,
        EolFieldId = _OptEolFieldId,
        NopFieldId = _OptNopFieldId,
        RecordRouteFieldId = _OptRecordRouteFieldId,
        LooseSourceRouteFieldId = _OptLooseSourceRouteFieldId,
        StrictSourceRouteFieldId = _OptStrictSourceRouteFieldId,
        TimestampFieldId = _OptTimestampFieldId,
        RouterAlertFieldId = _OptRouterAlertFieldId,
        SecurityFieldId = _OptSecurityFieldId,
        StreamIdFieldId = _OptStreamIdFieldId,
        UnknownFieldId = _OptUnknownFieldId,
        OptAddrFieldId = _OptAddrFieldId,
        OptOverflowFieldId = _OptOverflowFieldId,
        OptFlagFieldId = _OptFlagFieldId,
        OptTimeStampFieldId = _OptTimeStampFieldId,
        OptTimeStampAddrFieldId = _OptTimeStampAddrFieldId,
        OptRaFieldId = _OptRaFieldId,
        OptSidFieldId = _OptSidFieldId,
    };

    #region Thread-Local Address Cache

    /// <summary>
    /// Per-thread cache for the current packet's IPv4 src/dst addresses.
    /// Written by <see cref="IProtocol.Parse"/> before dispatching to the next protocol;
    /// consumed by downstream protocols (TCP, UDP). Null means no data cached yet
    /// on this thread (correct default for <see langword="[ThreadStatic]"/>).
    /// </summary>
    [ThreadStatic]
    private static (int PacketId, IPv4Address Src, IPv4Address Dst)? _ThreadCache;

    /// <summary>Caches the IPv4 src/dst addresses for the current packet on this thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCachedAddresses(PacketId packetId, IPv4Address src, IPv4Address dst)
        => _ThreadCache = (packetId.Value, src, dst);

    /// <summary>
    /// Attempts to read the cached IPv4 addresses for the specified packet.
    /// Returns <see langword="false"/> if no data is cached or the packet ID
    /// does not match (stale entry from a previous packet).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCachedAddresses(PacketId packetId, out IPv4Address src, out IPv4Address dst)
    {
        (int PacketId, IPv4Address Src, IPv4Address Dst)? c = _ThreadCache;
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
/// IPv4 header (20 bytes minimum, without options).
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |Version|  IHL  |    DSCP   |ECN|         Total Length          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |         Identification        |Flg|     Fragment Offset      |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |  Time to Live |    Protocol   |        Header Checksum       |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                       Source Address                         |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                    Destination Address                       |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
[BinaryParsable]
internal readonly partial struct IPv4Header
{
    /// <summary>IP version (4 bits, must be 4).</summary>
    [BinaryField(BitCount = 4)]
    public byte Version
    {
        get; init;
    }

    /// <summary>Internet Header Length in 32-bit words (4 bits, min 5).</summary>
    [BinaryField(BitCount = 4)]
    public byte Ihl
    {
        get; init;
    }

    /// <summary>Differentiated Services Code Point (6 bits).</summary>
    [BinaryField(BitCount = 6)]
    public byte Dscp
    {
        get; init;
    }

    /// <summary>Explicit Congestion Notification (2 bits).</summary>
    [BinaryField(BitCount = 2)]
    public byte Ecn
    {
        get; init;
    }

    /// <summary>Total length of the IP datagram in bytes.</summary>
    public U16BE TotalLength
    {
        get; init;
    }

    /// <summary>Identification field for fragment reassembly.</summary>
    public U16BE Identification
    {
        get; init;
    }

    /// <summary>Reserved flag bit (1 bit, must be zero).</summary>
    [BinaryField(BitCount = 1)]
    public byte ReservedFlag
    {
        get; init;
    }

    /// <summary>Don't Fragment flag (1 bit).</summary>
    [BinaryField(BitCount = 1)]
    public byte DontFragment
    {
        get; init;
    }

    /// <summary>More Fragments flag (1 bit).</summary>
    [BinaryField(BitCount = 1)]
    public byte MoreFragments
    {
        get; init;
    }

    /// <summary>Fragment offset in 8-byte units (13 bits).</summary>
    [BinaryField(BitCount = 13)]
    public ushort FragmentOffset
    {
        get; init;
    }

    /// <summary>Time to live (hop count).</summary>
    public byte Ttl
    {
        get; init;
    }

    /// <summary>Protocol number of the encapsulated payload.</summary>
    public byte Protocol
    {
        get; init;
    }

    /// <summary>One's complement checksum of the header.</summary>
    public U16BE Checksum
    {
        get; init;
    }

    /// <summary>Source IPv4 address (as 32-bit big-endian value).</summary>
    public U32BE SrcAddr
    {
        get; init;
    }

    /// <summary>Destination IPv4 address (as 32-bit big-endian value).</summary>
    public U32BE DstAddr
    {
        get; init;
    }

    /// <summary>Minimum header size in bytes (IHL=5, no options).</summary>
    internal const int MinHeaderSize = 20;

    /// <summary>Computes the header length in bytes from the IHL field.</summary>
    internal int HeaderLength => Ihl * 4;

    /// <summary>Extracts the source IPv4 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv4Address GetSrc(ReadOnlySpan<byte> data) => IPv4Address.FromBytes(data[12..16]);

    /// <summary>Extracts the destination IPv4 address from raw header data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IPv4Address GetDst(ReadOnlySpan<byte> data) => IPv4Address.FromBytes(data[16..20]);
    #endregion
}
