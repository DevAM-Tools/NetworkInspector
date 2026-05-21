// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Ethernet II / IEEE 802.3 protocol parser.
/// <para>Field tree structure:</para>
/// <code>
/// eth: Ethernet II, Src: XX:XX:XX:XX:XX:XX, Dst: XX:XX:XX:XX:XX:XX
/// ├── eth.dst: XX:XX:XX:XX:XX:XX
/// │   ├── eth.dst.ig: false (Individual/Group bit)
/// │   ├── eth.dst.lg: false (Local/Global bit)
/// │   └── eth.addr: XX:XX:XX:XX:XX:XX          [any-match]
/// ├── eth.src: XX:XX:XX:XX:XX:XX
/// │   ├── eth.src.ig: false
/// │   ├── eth.src.lg: false
/// │   └── eth.addr: XX:XX:XX:XX:XX:XX          [any-match]
/// ├── eth.type: 0x0800 (IPv4)
/// ├── eth.padding: (N bytes)      [optional, when frame padded to minimum]
/// ├── eth.trailer: (N bytes)      [optional, extra bytes after payload+padding]
/// ├── eth.fcs: 0x12345678          [optional, when FCS checking enabled]
/// └── eth.fcs.status: [Good]       [optional, FCS validation result]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("eth", "Ethernet", Description = "Ethernet II / IEEE 802.3")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, (ulong)LinkType.Ethernet)]
public sealed partial class EthernetProtocol : IProtocol
{
    /// <summary>Ethernet frame header size in bytes (6 dst + 6 src + 2 type/len).</summary>
    private const int HeaderSize = 14;

    /// <summary>Minimum EtherType value distinguishing Ethernet II from 802.3.</summary>
    private const ushort MinEtherType = 0x0600;

    /// <summary>Minimum Ethernet payload size (bytes) before padding is required.</summary>
    private const int MinPayloadSize = 46;

    #region Table Name Constants

    /// <summary>Dispatch table name for EtherType-based protocol lookup.</summary>
    public const string EtherTypeTableName = "eth.type";

    /// <summary>Dispatch table name for IEEE 802.3 length-based protocol lookup.</summary>
    public const string Ieee8023TableName = "eth.ieee8023";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present Ethernet fields.</summary>
    private const string EthIndexGroup = "eth";

    #endregion

    #region Fields

    // ETH-02: BytesField container carries header byte range for UI highlighting
    [BytesField("eth", "Ethernet", IndexGroup = EthIndexGroup)]
    private FieldId _ProtocolFieldId;

    [MacField("eth.dst", "Destination", IndexGroup = EthIndexGroup)]
    private FieldId _DstFieldId;

    [BoolField("eth.dst.ig", "I/G bit", IndexGroup = EthIndexGroup)]
    private FieldId _DstIgFieldId;

    [BoolField("eth.dst.lg", "L/G bit", IndexGroup = EthIndexGroup)]
    private FieldId _DstLgFieldId;

    [MacField("eth.src", "Source", IndexGroup = EthIndexGroup)]
    private FieldId _SrcFieldId;

    [BoolField("eth.src.ig", "I/G bit", IndexGroup = EthIndexGroup)]
    private FieldId _SrcIgFieldId;

    [BoolField("eth.src.lg", "L/G bit", IndexGroup = EthIndexGroup)]
    private FieldId _SrcLgFieldId;

    // Combined address field (Wireshark eth.addr compatibility).
    // Appended twice per frame — once for destination and once for source — so that
    // filter expressions like `eth.addr == XX:XX:XX:XX:XX:XX` match either endpoint.
    // The filter engine handles multi-occurrence MAC fields with "any-match" semantics
    // via StackValue.MacCollection.
    [MacField("eth.addr", "Address", IndexGroup = EthIndexGroup)]
    private FieldId _AddrFieldId;

    // Mutually exclusive: type (Ethernet II) vs length (802.3)
    [U64Field(EtherTypeTableName, "Type", IndexGroup = "eth.type")]
    private FieldId _TypeFieldId;

    [U64Field("eth.len", "Length", IndexGroup = "eth.len")]
    private FieldId _LenFieldId;

    // ETH-01: Padding and trailer fields (optional)
    [BytesField("eth.padding", "Padding", IndexGroup = "eth.padding")]
    private FieldId _PaddingFieldId;

    [BytesField("eth.trailer", "Trailer", IndexGroup = "eth.trailer")]
    private FieldId _TrailerFieldId;

    // FCS fields (optional, when FCS checking is enabled)
    [U64Field("eth.fcs", "Frame Check Sequence", IndexGroup = "eth.fcs")]
    private FieldId _FcsFieldId;

    [StringField("eth.fcs.status", "FCS Status", IndexGroup = "eth.fcs")]
    private FieldId _FcsStatusFieldId;

    // EtherType dispatch table
    [ProtocolTableU64(EtherTypeTableName, "EtherType")]
    private ProtocolTableId _EtherTypeTableId;

    // IEEE 802.3 dispatch table (for length-based frames → LLC)
    [ProtocolTableU64(Ieee8023TableName, "IEEE 802.3")]
    private ProtocolTableId _Ieee8023TableId;

    #endregion

    #region Settings

    [BoolSetting("eth.assume_fcs", "Assume FCS present", "eth", Default = false)]
    private bool _AssumeFcs;

    // Pre-allocated delegate: created once in OnStartCustom, reused for every packet.
    // Captures only `this` (singleton) — zero per-packet allocation.
    private LazyPopulator _Populator = null!;

    // Sparse dispatch cache built from the EtherType protocol table at stack start.
    // Linear scan over typically 4–6 entries; avoids dictionary hash computation per packet.
    // Pre-bound delegates for direct invocation without interface vtable dispatch.
    private (ulong Key, ParseDelegate Parse)[] _EtherTypeSparseCache = [];

    /// <summary>
    /// Pre-allocates the lazy-field populator delegate and builds the EtherType dispatch cache.
    /// Neither allocation occurs per packet — both are one-time costs at stack start.
    /// </summary>
    partial void OnStartCustom(Stack stack)
    {
        _Populator = (in MutField container) => PopulateEthernetFields(in container);
        // Sparse cache: EtherType is 16-bit (65 536 possible values) but only 4–6 are registered.
        // A tiny array scan beats a dictionary for such small sets.
        // Delegate cache stores pre-bound ParseDelegate for direct invocation.
        _EtherTypeSparseCache = stack.BuildU64SparseDelegateCache(_EtherTypeTableId);
    }

    /// <summary>
    /// Builds the Ethernet child-field tree from the header bytes stored in
    /// <paramref name="container"/>'s field value.  Called lazily on first access.
    /// All address and type/length fields are populated here — no downstream
    /// protocol requires MAC addresses eagerly, so deferring all fields to
    /// the lazy populator avoids per-packet field appends on the hot path.
    /// </summary>
    private ParseResult PopulateEthernetFields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> hdrBytes))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (hdrBytes.Length < HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, HeaderSize, (ulong)hdrBytes.Length);
        }

        ReadOnlySpan<byte> span = hdrBytes.Span;

        // Parse MAC addresses and decompose I/G and L/G bits.
        // eth.addr is appended as a child of the respective dst/src field
        // for structured tree navigation and any-match filter semantics.
        MacAddress dst = MacAddress.FromBytes(span[..6]);
        MacAddress src = MacAddress.FromBytes(span[6..12]);

        MutField dstField = container.Append(_DstFieldId, FieldValue.NewMacAddress(dst), in context);
        dstField.Append(_DstIgFieldId, FieldValue.NewBool(dst.IsMulticast), in context);
        dstField.Append(_DstLgFieldId, FieldValue.NewBool(dst.IsLocal), in context);
        dstField.Append(_AddrFieldId, FieldValue.NewMacAddress(dst), in context);

        MutField srcField = container.Append(_SrcFieldId, FieldValue.NewMacAddress(src), in context);
        srcField.Append(_SrcIgFieldId, FieldValue.NewBool(src.IsMulticast), in context);
        srcField.Append(_SrcLgFieldId, FieldValue.NewBool(src.IsLocal), in context);
        srcField.Append(_AddrFieldId, FieldValue.NewMacAddress(src), in context);

        ushort typeOrLen = BinaryPrimitives.ReadUInt16BigEndian(span[12..14]);

        if (typeOrLen >= MinEtherType)
        {
            string displayText = DisplayTables.GetEtherTypeDisplayText(typeOrLen);
            container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(typeOrLen), displayText, in context);
        }
        else
        {
            container.Append(_LenFieldId, FieldValue.NewU64(typeOrLen), in context);
        }

        return 0;
    }

    /// <summary>
    /// Parses a Ethernet protocol unit from the supplied <paramref name="data"/> buffer,
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

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_EthGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Read only EtherType/length at parse time — MAC addresses are parsed lazily
        // inside the populator (which re-reads from stored header bytes).
        ushort typeOrLen = BinaryPrimitives.ReadUInt16BigEndian(span[12..14]);

        // Record optional index groups based on frame type
        if (typeOrLen >= MinEtherType)
        {
            context.RecordGroupPresence(_EthTypeGroupId);
        }
        else
        {
            context.RecordGroupPresence(_EthLenGroupId);
        }

        // Eagerly parse MAC addresses (12-byte memory copy) — the ToString()
        // formatting is still deferred via ZA.Lazy until the summary is displayed.
        MacAddress src = MacAddress.FromBytes(span[6..12]);
        MacAddress dst = MacAddress.FromBytes(span[..6]);
        ReadOnlyMemory<byte> hdrBytes = data[..HeaderSize];
        LazyString summary = typeOrLen >= MinEtherType
            ? ZA.Lazy("Ethernet II, Src: ", src, ", Dst: ", dst)
            : ZA.Lazy("IEEE 802.3, Src: ", src, ", Dst: ", dst);

        // Create lazy protocol field with BytesField value and custom summary text.
        // CustomRepresentation shows the header byte count alongside the field value.
        // _Populator is pre-allocated in OnStartCustom — no per-packet closure.
        FieldValue headerValue = FieldValue.NewBytes(hdrBytes)
            .WithCustomRepresentation(new LazyString("14 bytes"));
        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, headerValue, summary, _Populator);

        // All address fields (dst, src, ig, lg, addr) are populated lazily by
        // PopulateEthernetFields — no downstream protocol requires MAC addresses
        // eagerly, so deferring them reduces per-packet field appends.

        // Cache Ethernet MAC addresses in the thread-local field directly on this
        // protocol for potential downstream use (ARP, 802.1X, diagnostics).
        SetCachedAddresses(parentField.Packet.Id, src, dst);

        // Dispatch to next protocol on parentField (sibling dispatch)
        // If FCS is assumed present, strip the last 4 bytes before dispatch.
        const int FcsSize = 4;
        bool hasFcs = _AssumeFcs && data.Length >= HeaderSize + FcsSize;
        ReadOnlyMemory<byte> payloadRegion = hasFcs ? data[HeaderSize..^FcsSize] : data[HeaderSize..];
        int childConsumed = 0;
        if (typeOrLen >= MinEtherType)
        {
            ParseResult dispatchResult = DispatchEtherType(in parentField, typeOrLen, payloadRegion, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
            childConsumed = dispatchResult.Value;
        }
        else
        {
            // IEEE 802.3: typeOrLen is the payload length, dispatch to LLC handler.
            // Limit payload to stated length to strip padding before LLC parsing.
            int llcLen = typeOrLen;
            ReadOnlyMemory<byte> llcPayload = llcLen <= payloadRegion.Length ? payloadRegion[..llcLen] : payloadRegion;
            ParseResult dispatchResult = parentField.TryCallNextProtocolU64(
                _Ieee8023TableId, 1UL, llcPayload, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
            childConsumed = dispatchResult.Value;
        }

        AppendPaddingAndTrailer(parentField, data, typeOrLen, childConsumed, hasFcs, in context);

        // Append FCS fields after padding/trailer (at the very end of the frame)
        if (hasFcs)
        {
            context.RecordGroupPresence(_EthFcsGroupId);
            ReadOnlySpan<byte> fcsBytes = data.Span[^FcsSize..];
            uint fcsValue = BinaryPrimitives.ReadUInt32BigEndian(fcsBytes);

            // CRC32 is computed over the entire frame excluding FCS
            uint computed = Helpers.Crc32.Compute(data.Span[..^FcsSize]);
            // Ethernet FCS is stored in little-endian byte order on the wire
            uint fcsOnWire = BinaryPrimitives.ReadUInt32LittleEndian(fcsBytes);
            bool fcsValid = computed == fcsOnWire;

            parentField.AppendWithCustomText(_FcsFieldId,
                FieldValue.NewU64(fcsValue),
                DisplayTables.FormatHexU32(fcsValue), in context);
            parentField.Append(_FcsStatusFieldId,
                FieldValue.NewString(fcsValid ? "[Good]" : "[Bad]"), in context);
        }

        return data.Length;
    }

    /// <summary>
    /// Detects and appends padding/trailer fields after payload dispatch.
    /// Padding is added by NICs to meet minimum 60-byte frame requirement.
    /// Trailer is any extra bytes beyond padding.
    /// When FCS is present, the last 4 bytes are excluded from the calculation.
    /// </summary>
    private void AppendPaddingAndTrailer(
        in MutField parentField, ReadOnlyMemory<byte> data,
        ushort typeOrLen, int childConsumed, bool hasFcs, in ParseContext context)
    {
        // Effective data length excludes FCS (4 bytes at the end)
        int effectiveLen = hasFcs ? data.Length - 4 : data.Length;
        int payloadSize = effectiveLen - HeaderSize;

        // Determine actual payload length as reported by child protocol
        int declaredPayloadLen;
        if (typeOrLen < MinEtherType)
        {
            // 802.3: typeOrLen IS the payload length
            declaredPayloadLen = typeOrLen;
        }
        else if (childConsumed > 0)
        {
            // Ethernet II: child protocol tells us how much it consumed
            declaredPayloadLen = childConsumed;
        }
        else
        {
            // No child consumed data — no padding/trailer detection possible
            return;
        }

        int extraBytes = payloadSize - declaredPayloadLen;
        if (extraBytes <= 0)
        {
            return;
        }

        int extraStart = HeaderSize + declaredPayloadLen;

        // Calculate how much padding is needed to reach minimum frame size (60 bytes)
        int minFramePayload = MinPayloadSize; // 46 bytes
        int paddingNeeded = Math.Max(0, minFramePayload - declaredPayloadLen);
        int paddingBytes = Math.Min(paddingNeeded, extraBytes);
        int trailerBytes = extraBytes - paddingBytes;

        if (paddingBytes > 0)
        {
            context.RecordGroupPresence(_EthPaddingGroupId);
            ReadOnlyMemory<byte> paddingData = data.Slice(extraStart, paddingBytes);
            parentField.Append(_PaddingFieldId, FieldValue.NewBytes(paddingData), in context);
        }

        if (trailerBytes > 0)
        {
            context.RecordGroupPresence(_EthTrailerGroupId);
            ReadOnlyMemory<byte> trailerData = data.Slice(extraStart + paddingBytes, trailerBytes);
            parentField.Append(_TrailerFieldId, FieldValue.NewBytes(trailerData), in context);
        }
    }

    /// <summary>
    /// Dispatches to the next protocol by EtherType.
    /// Scans the pre-built sparse cache first (typically 4–6 entries, all in L1 D-cache);
    /// falls back to full table dispatch for multi-protocol keys or unknown EtherTypes.
    /// </summary>
    private ParseResult DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        // Direct delegate call — no ProtocolId resolution, no vtable dispatch.
        foreach ((ulong key, ParseDelegate parse) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parse(in parentField, payload, in context);
            }
        }

        // Fallback: multi-protocol EtherType key or EtherType not in cache.
        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }
    #endregion

    #region Thread-Local Address Cache

    /// <summary>
    /// Per-thread cache for the current packet's Ethernet src/dst MAC addresses.
    /// Written by <see cref="Parse"/> before dispatching; available to downstream
    /// protocols (ARP, 802.1X). Null means no data cached yet on this thread.
    /// </summary>
    [ThreadStatic]
    private static (int PacketId, MacAddress Src, MacAddress Dst)? _ThreadCache;

    /// <summary>Caches the Ethernet src/dst addresses for the current packet on this thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCachedAddresses(PacketId packetId, MacAddress src, MacAddress dst)
        => _ThreadCache = (packetId.Value, src, dst);

    /// <summary>
    /// Attempts to read the cached Ethernet MAC addresses for the specified packet.
    /// Returns <see langword="false"/> if no data is cached or the packet ID
    /// does not match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCachedAddresses(PacketId packetId, out MacAddress src, out MacAddress dst)
    {
        (int PacketId, MacAddress Src, MacAddress Dst)? c = _ThreadCache;
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
