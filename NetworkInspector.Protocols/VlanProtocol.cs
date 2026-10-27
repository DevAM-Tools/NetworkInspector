// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// IEEE 802.1Q VLAN tag protocol parser.
/// <para>Field tree structure:</para>
/// <code>
/// vlan: 802.1Q Virtual LAN, PRI: 5, DEI: 0, ID: 100
/// ├── vlan.priority: 5 (Voice)
/// ├── vlan.dei: false
/// ├── vlan.id: 100
/// └── vlan.etype: 0x0800 (IPv4)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// The only instance state is the field and protocol ids (assigned by the generated registration code) and the
/// populator delegate and dispatch cache (assigned in <c>_OnStartCustom</c>), all during the single-threaded build phase
/// and read-only thereafter, so <see cref="IProtocol.Parse"/> may be invoked concurrently from any number of threads
/// on the same instance without external synchronisation. The innermost VLAN id of the packet being parsed is kept in a
/// <c>[ThreadStatic]</c> slot (see <see cref="SetLastVlan"/>), so it is never shared between threads. The protocol keeps
/// no cross-packet state, so a replay simply parses again and refills that slot.</para>
/// <para>Both 0x8100 (802.1Q customer tag) and 0x88A8 (802.1ad service tag, Q-in-Q) are dispatched here; stacked tags
/// are handled by dispatching back into this protocol, one call per tag.</para>
/// </remarks>
[Protocol("vlan", "802.1Q Virtual LAN", Description = "IEEE 802.1Q VLAN tagging")]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey8021Q)]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKeyQinQ)]
public sealed partial class VlanProtocol : IProtocol
{
    /// <summary>VLAN tag header size in bytes.</summary>
    private const int _HeaderSize = 4;

    #region Table Key Constants

    /// <summary>EtherType key for IEEE 802.1Q VLAN tag (0x8100).</summary>
    public const ulong EtherTypeKey8021Q = 0x8100;

    /// <summary>EtherType key for IEEE 802.1ad Q-in-Q (0x88A8).</summary>
    public const ulong EtherTypeKeyQinQ = 0x88A8;

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present VLAN fields.</summary>
    private const string _VlanIndexGroup = "vlan";

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("vlan", "VLAN", IndexGroup = _VlanIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("vlan.priority", "Priority", IndexGroup = _VlanIndexGroup)]
    private FieldId _PriorityFieldId;

    [BoolField("vlan.dei", "DEI", IndexGroup = _VlanIndexGroup)]
    private FieldId _DeiFieldId;

    [U64Field("vlan.id", "ID", IndexGroup = _VlanIndexGroup)]
    private FieldId _IdFieldId;

    [U64Field("vlan.etype", "Type", IndexGroup = _VlanIndexGroup)]
    private FieldId _EtherTypeFieldId;

    // Dispatch using the Ethernet protocol's EtherType table (resolved at registration time)
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    #endregion

    #region Pre-allocated populator (created once in _OnStartCustom, shared across all packets)

    /// <summary>Pre-allocated delegate for VLAN field population — captures only 'this'.</summary>
    private LazyPopulator _Populator = null!;

    // Sparse dispatch cache built from the EtherType protocol table at stack start. It holds only the EtherTypes that have
    // exactly one registered protocol; the rest go through the table lookup in _DispatchEtherType.
    // Linear scan over typically 4–6 entries; avoids dictionary hash computation per packet.
    private (ulong Key, ProtocolId Id)[] _EtherTypeSparseCache = [];

    /// <summary>
    /// Pre-allocates the lazy-field populator delegate and builds the EtherType dispatch cache.
    /// Neither allocation occurs per packet — both are one-time costs at stack start.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateVlanFields;
        _EtherTypeSparseCache = stack.BuildU64SparseIdCache(_EtherTypeTableId);
    }

    /// <summary>
    /// Populates VLAN child fields at materialisation time.
    /// Re-parses the 4-byte header from the stored bytes to avoid per-packet closures: the container value is the only
    /// state a populator receives, and capturing the decoded values would allocate a closure per packet.
    /// </summary>
    private ParseResult _PopulateVlanFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> headerBytes))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        if (!VlanHeader.TryParse(headerBytes.Span, out VlanHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, VlanHeader._HeaderSize, (ulong)headerBytes.Length);
        }

        byte pcp = header.Priority;
        bool dei = header.Dei != 0;
        ushort vid = header.VlanId;
        ushort etherType = header.EtherType;

        string pcpText = DisplayTables.GetVlanPriorityDisplayText(pcp);
        container.AppendWithCustomText(_PriorityFieldId, FieldValue.NewU64(pcp), pcpText);
        container.Append(_DeiFieldId, FieldValue.NewBool(dei));
        container.Append(_IdFieldId, FieldValue.NewU64(vid));

        string etherTypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtherTypeFieldId, FieldValue.NewU64(etherType), etherTypeText);

        return 0;
    }

    /// <summary>
    /// Parses a Vlan protocol unit from the supplied <paramref name="data"/> buffer,
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
        context.RecordGroupPresence(_VlanGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Decode the fixed header by hand (one length check)
        if (!VlanHeader.TryParse(span, out VlanHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, VlanHeader._HeaderSize, (ulong)data.Length);
        }

        byte pcp = header.Priority;
        bool dei = header.Dei != 0;
        ushort vid = header.VlanId;
        ushort etherType = header.EtherType;

        // Last write wins, so a nested tag replaces the outer one. TCP reads this as the innermost VLAN.
        // Per-thread, replaced on every VLAN of this packet, valid only until the next packet parsed on this thread.
        // A thread-static slot is used instead of a field-tree lookup so the TCP stream key can use it without a tree
        // (skip mode) and without any locking.
        SetLastVlan(parentField.Packet.Id, vid);

        // Store the 4-byte header so _PopulateVlanFields can re-parse without captured variables. The container title
        // is built from the typed pcp/dei/vid values as format fragments, only if the packet wants custom text.
        ReadOnlyMemory<byte> headerBytes = data[.._HeaderSize];
        FieldValue headerValue = FieldValue.NewBytes(headerBytes)
            .WithCustomRepresentation(new LazyString("4 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, headerValue, "802.1Q Virtual LAN, PRI: ", pcp, ", DEI: ", dei ? 1 : 0, ", ID: ", vid, _Populator);

        // Dispatch to next protocol on parentField (sibling dispatch: the next layer is a sibling of the lazy vlan node, and
        // is parsed eagerly; only the VLAN's own child fields are deferred). The payload is a slice, not a copy.
        ReadOnlyMemory<byte> payload = data[_HeaderSize..];
        if (_EtherTypeTableId.IsValid)
        {
            ParseResult dispatchResult = _DispatchEtherType(in parentField, etherType, payload, in context);
            if (dispatchResult.TryPropagateError(out ParseResult error))
            {
                return error;
            }
        }

        return data.Length;
    }

    /// <summary>
    /// Caches the VLAN id of the tag just parsed.
    /// Per-thread. Replaced on every VLAN of this packet, so the last write is the innermost tag.
    /// Valid only until the next packet is parsed on this thread. VLAN id 0 is stored and treated as absent by TCP.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetLastVlan(PacketId packetId, ushort vlanId)
        => _LastVlanCache = (packetId.Value, vlanId);

    /// <summary>
    /// Reads the last VLAN id cached for <paramref name="packetId"/>.
    /// Returns <see langword="false"/> when the slot is empty, belongs to another packet, or the id is 0
    /// (VID 0 is a priority-only tag that carries no VLAN identity, IEEE 802.1Q).
    /// </summary>
    /// <param name="packetId">Packet whose innermost VLAN id is wanted; a slot written for another packet is ignored.</param>
    /// <param name="vlanId">The VLAN id, or 0 when none is available.</param>
    /// <returns><see langword="true"/> when a non-zero VLAN id of this packet is cached.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetLastVlan(PacketId packetId, out ushort vlanId)
    {
        (int PacketId, ushort VlanId)? cached = _LastVlanCache;
        if (cached.HasValue && cached.Value.PacketId == packetId.Value && cached.Value.VlanId != 0)
        {
            vlanId = cached.Value.VlanId;
            return true;
        }

        vlanId = 0;
        return false;
    }

    /// <summary>Per-thread last VLAN tag parsed on this thread. Null until the first VLAN.</summary>
    [ThreadStatic]
    private static (int PacketId, ushort VlanId)? _LastVlanCache;

    /// <summary>
    /// Dispatches to the next protocol by EtherType.
    /// Scans the pre-built sparse cache first (typically 4–6 entries, all in L1 D-cache);
    /// falls back to full table dispatch for multi-protocol keys or unknown EtherTypes.
    /// An EtherType without any protocol is not an error: the table dispatch reports "not dispatched" and the payload
    /// is left undecoded.
    /// </summary>
    private ParseResult _DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        // Cached ProtocolId; CallProtocol returns ParseError for invalid ids.
        foreach ((ulong key, ProtocolId id) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parentField.CallProtocol(id, payload, in context);
            }
        }

        // Fallback: multi-protocol EtherType key or EtherType not in cache.
        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }

    #endregion
}

/// <summary>
/// IEEE 802.1Q VLAN tag header (4 bytes).
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |PRI|D|       VLAN ID          |          EtherType            |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
/// <remarks>
/// Decoded by hand (one length check, fixed-offset reads with <see cref="BinaryPrimitives"/>) instead of a generated
/// parser, so the JIT elides the per-read bounds checks and the dissector gets plain integers; declaration order
/// mirrors the wire layout for readability only. The first 16-bit word (tag control information) packs PCP, DEI and
/// VID and is split by shifts. Thread safety: immutable value type.
/// </remarks>
internal readonly struct VlanHeader
{
    /// <summary>Priority Code Point (bits 15..13 of the tag control word, 802.1p).</summary>
    public byte Priority
    {
        get; init;
    }

    /// <summary>Drop Eligible Indicator (bit 12 of the tag control word).</summary>
    public byte Dei
    {
        get; init;
    }

    /// <summary>VLAN Identifier (bits 11..0 of the tag control word).</summary>
    public ushort VlanId
    {
        get; init;
    }

    /// <summary>Encapsulated protocol EtherType (bytes 2..3).</summary>
    public ushort EtherType
    {
        get; init;
    }

    /// <summary>Serialized header size in bytes (4).</summary>
    internal const int _HeaderSize = 4;

    /// <summary>
    /// Decodes the 4-byte tag. Returns <see langword="false"/> with a default value when fewer than
    /// <see cref="_HeaderSize"/> bytes are present. Longer input is accepted; only the first 4 bytes are read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> source, out VlanHeader value)
    {
        // One length check up front; a short tag is an expected failure, not an exception.
        if (source.Length < _HeaderSize)
        {
            value = default;
            return false;
        }

        // Exact-size slice: after this the JIT knows every index below is in range and drops the bounds checks.
        ReadOnlySpan<byte> h = source[.._HeaderSize];
        ushort tagControl = BinaryPrimitives.ReadUInt16BigEndian(h);
        value = new VlanHeader
        {
            Priority = (byte)(tagControl >> 13),                         // word 0..2, bits 15..13
            Dei = (byte)((tagControl >> 12) & 0x01),                     // word 0..2, bit 12
            VlanId = (ushort)(tagControl & 0x0FFF),                      // word 0..2, bits 11..0
            EtherType = BinaryPrimitives.ReadUInt16BigEndian(h[2..]),    // 2..4
        };
        return true;
    }
}
