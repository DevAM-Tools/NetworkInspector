// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Ethernet II / IEEE 802.3 protocol parser.
/// <para>Field tree structure:</para>
/// <code>
/// eth: Ethernet II, Src: XX:XX:XX:XX:XX:XX, Dst: XX:XX:XX:XX:XX:XX
/// ├── eth.dst: XX:XX:XX:XX:XX:XX  (CustomText carries I/G + L/G semantics)
/// ├── eth.src: XX:XX:XX:XX:XX:XX  (CustomText carries I/G + L/G semantics)
/// ├── eth.type: 0x0800 (IPv4)
/// ├── eth.padding: (N bytes)      [optional, when frame padded to minimum]
/// ├── eth.trailer: (N bytes)      [optional, extra bytes after payload+padding]
/// ├── eth.fcs: 0x12345678          [optional, when FCS checking enabled]
/// └── eth.fcs.status: [Good]       [optional, FCS validation result]
/// </code>
/// <para>
/// The any-match name <c>eth.addr</c> is exposed via a field alias group registered
/// in <see cref="_RegisterFieldsCustom"/> that resolves to <c>{ eth.dst, eth.src }</c>;
/// no <c>eth.addr</c> field node is appended to the parse tree.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>_RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("eth", "Ethernet", Description = "Ethernet II / IEEE 802.3")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, (ulong)LinkType.Ethernet)]
public sealed partial class EthernetProtocol : IProtocol
{
    /// <summary>Ethernet frame header size in bytes (6 dst + 6 src + 2 type/len).</summary>
    private const int _HeaderSize = 14;

    /// <summary>Minimum EtherType value distinguishing Ethernet II from 802.3.</summary>
    private const ushort _MinEtherType = 0x0600;

    /// <summary>Minimum Ethernet payload size (bytes) before padding is required.</summary>
    private const int _MinPayloadSize = 46;

    #region Table Name Constants

    /// <summary>Dispatch table name for EtherType-based protocol lookup.</summary>
    public const string EtherTypeTableName = "eth.type";

    /// <summary>Dispatch table name for IEEE 802.3 length-based protocol lookup.</summary>
    public const string Ieee8023TableName = "eth.ieee8023";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present Ethernet fields.</summary>
    private const string _EthIndexGroup = "eth";

    #endregion

    #region Fields

    // ETH-02: BytesField container carries header byte range for UI highlighting
    [BytesField("eth", "Ethernet", IndexGroup = _EthIndexGroup)]
    private FieldId _ProtocolFieldId;

    [MacField("eth.dst", "Destination", IndexGroup = _EthIndexGroup)]
    private FieldId _DstFieldId;

    [MacField("eth.src", "Source", IndexGroup = _EthIndexGroup)]
    private FieldId _SrcFieldId;

    // Field alias group ID assigned in _RegisterFieldsCustom for "eth.addr" -> { eth.dst, eth.src }.
    // The alias name is metadata-only: GetFieldId("eth.addr") never resolves, and the parse
    // tree never contains a separate eth.addr node. Filter engines that need any-match semantics
    // must consult the alias registry instead of enumerating duplicate field nodes.
    private FieldAliasGroupId _AddrAliasGroupId;

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

    // Sparse dispatch cache built from the EtherType protocol table at stack start.
    // Linear scan over typically 4–6 entries; avoids dictionary hash computation per packet.
    // Pre-bound delegates for direct invocation without interface vtable dispatch.
    private (ulong Key, ParseDelegate Parse)[] _EtherTypeSparseCache = [];

    /// <summary>
    /// Registers protocol-owned alias groups. Runs at build time after all canonical
    /// fields are registered. Adds "eth.addr" -> { eth.dst, eth.src } as metadata.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _AddrAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "eth.addr",
            "Any-match alias for source/destination MAC addresses.",
            [_DstFieldId, _SrcFieldId]);
    }

    /// <summary>
    /// All four I/G + L/G display strings, indexed by <c>(IsMulticast ? 2 : 0) | (IsLocal ? 1 : 0)</c>.
    /// Precomputed to eliminate per-packet string interpolation allocations.
    /// </summary>
    private static readonly string[] _MacBitsTable =
    [
        "Unicast, Globally Unique",        // 0b00  !multicast, !local
        "Unicast, Locally Administered",   // 0b01  !multicast,  local
        "Multicast, Globally Unique",      // 0b10   multicast, !local
        "Multicast, Locally Administered", // 0b11   multicast,  local
    ];

    /// <summary>
    /// Returns the I/G + L/G display string for <paramref name="address"/>
    /// from the precomputed <see cref="_MacBitsTable"/>; no allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string _FormatMacAddressBits(MacAddress address)
        => _MacBitsTable[(address.IsMulticast ? 2 : 0) | (address.IsLocal ? 1 : 0)];

    /// <summary>
    /// Builds the EtherType dispatch cache at stack start. One-time cost: a tiny array scan
    /// for the 4–6 registered EtherType entries beats a dictionary for per-packet lookup.
    /// </summary>
    partial void _OnStartCustom(Stack stack) =>
        _EtherTypeSparseCache = stack.BuildU64SparseDelegateCache(_EtherTypeTableId);

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
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_EthGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Read only EtherType/length at parse time — MAC addresses are parsed lazily
        // inside the populator (which re-reads from stored header bytes).
        ushort typeOrLen = BinaryPrimitives.ReadUInt16BigEndian(span[12..14]);

        // Record optional index groups based on frame type
        if (typeOrLen >= _MinEtherType)
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
        ReadOnlyMemory<byte> hdrBytes = data[.._HeaderSize];
        LazyString summary = typeOrLen >= _MinEtherType
            ? ZA.Lazy("Ethernet II, Src: ", src, ", Dst: ", dst)
            : ZA.Lazy("IEEE 802.3, Src: ", src, ", Dst: ", dst);

        // Create protocol container field with BytesField value and custom summary text.
        // CustomRepresentation shows the header byte count alongside the field value.
        FieldValue headerValue = FieldValue.NewBytes(hdrBytes)
            .WithCustomRepresentation(new LazyString("14 bytes"));
        MutField ethContainer = parentField.AppendWithCustomText(
            _ProtocolFieldId, headerValue, summary);

        // Eagerly append eth.dst, eth.src, and eth.type/eth.len so these key identifier
        // fields are present in the field tree during the initial parse pass.
        // The alias group "eth.addr" is metadata-only (registered in _RegisterFieldsCustom).
        // CustomText combines the MAC address with its I/G+L/G annotation; ZA.Lazy defers
        // string allocation until the value is actually rendered.
        ethContainer.AppendWithCustomText(_DstFieldId, FieldValue.NewMacAddress(dst), ZA.Lazy(dst, " (", _FormatMacAddressBits(dst), ")"));
        ethContainer.AppendWithCustomText(_SrcFieldId, FieldValue.NewMacAddress(src), ZA.Lazy(src, " (", _FormatMacAddressBits(src), ")"));
        if (typeOrLen >= _MinEtherType)
        {
            ethContainer.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(typeOrLen),
                DisplayTables.GetEtherTypeDisplayText(typeOrLen));
        }
        else
        {
            ethContainer.Append(_LenFieldId, FieldValue.NewU64(typeOrLen));
        }

        // Cache Ethernet MAC addresses in the thread-local field directly on this
        // protocol for potential downstream use (ARP, 802.1X, diagnostics).
        SetCachedAddresses(parentField.Packet.Id, src, dst);

        // Dispatch to next protocol on parentField (sibling dispatch)
        // If FCS is assumed present, strip the last 4 bytes before dispatch.
        const int FcsSize = 4;
        bool hasFcs = _AssumeFcs && data.Length >= _HeaderSize + FcsSize;
        ReadOnlyMemory<byte> payloadRegion = hasFcs ? data[_HeaderSize..^FcsSize] : data[_HeaderSize..];
        int childConsumed = 0;
        if (typeOrLen >= _MinEtherType)
        {
            ParseResult dispatchResult = _DispatchEtherType(in parentField, typeOrLen, payloadRegion, in context);
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

        _AppendPaddingAndTrailer(parentField, data, typeOrLen, childConsumed, hasFcs, in context);

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
                DisplayTables.FormatHexU32(fcsValue));
            parentField.Append(_FcsStatusFieldId,
                FieldValue.NewString(fcsValid ? "[Good]" : "[Bad]"));
        }

        return data.Length;
    }

    /// <summary>
    /// Detects and appends padding/trailer fields after payload dispatch.
    /// Padding is added by NICs to meet minimum 60-byte frame requirement.
    /// Trailer is any extra bytes beyond padding.
    /// When FCS is present, the last 4 bytes are excluded from the calculation.
    /// </summary>
    private void _AppendPaddingAndTrailer(
        in MutField parentField, ReadOnlyMemory<byte> data,
        ushort typeOrLen, int childConsumed, bool hasFcs, in ParseContext context)
    {
        // Effective data length excludes FCS (4 bytes at the end)
        int effectiveLen = hasFcs ? data.Length - 4 : data.Length;
        int payloadSize = effectiveLen - _HeaderSize;

        // Determine actual payload length as reported by child protocol
        int declaredPayloadLen;
        if (typeOrLen < _MinEtherType)
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

        int extraStart = _HeaderSize + declaredPayloadLen;

        // Calculate how much padding is needed to reach minimum frame size (60 bytes)
        int minFramePayload = _MinPayloadSize; // 46 bytes
        int paddingNeeded = Math.Max(0, minFramePayload - declaredPayloadLen);
        int paddingBytes = Math.Min(paddingNeeded, extraBytes);
        int trailerBytes = extraBytes - paddingBytes;

        if (paddingBytes > 0)
        {
            context.RecordGroupPresence(_EthPaddingGroupId);
            ReadOnlyMemory<byte> paddingData = data.Slice(extraStart, paddingBytes);
            parentField.Append(_PaddingFieldId, FieldValue.NewBytes(paddingData));
        }

        if (trailerBytes > 0)
        {
            context.RecordGroupPresence(_EthTrailerGroupId);
            ReadOnlyMemory<byte> trailerData = data.Slice(extraStart + paddingBytes, trailerBytes);
            parentField.Append(_TrailerFieldId, FieldValue.NewBytes(trailerData));
        }
    }

    /// <summary>
    /// Dispatches to the next protocol by EtherType.
    /// Scans the pre-built sparse cache first (typically 4–6 entries, all in L1 D-cache);
    /// falls back to full table dispatch for multi-protocol keys or unknown EtherTypes.
    /// </summary>
    private ParseResult _DispatchEtherType(
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
