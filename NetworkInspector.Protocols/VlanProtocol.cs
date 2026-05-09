// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("vlan", "802.1Q Virtual LAN", Description = "IEEE 802.1Q VLAN tagging")]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey8021Q)]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKeyQinQ)]
public sealed partial class VlanProtocol : IProtocol
{
    /// <summary>VLAN tag header size in bytes.</summary>
    private const int HeaderSize = 4;

    #region Table Key Constants

    /// <summary>EtherType key for IEEE 802.1Q VLAN tag (0x8100).</summary>
    public const ulong EtherTypeKey8021Q = 0x8100;

    /// <summary>EtherType key for IEEE 802.1ad Q-in-Q (0x88A8).</summary>
    public const ulong EtherTypeKeyQinQ = 0x88A8;

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present VLAN fields.</summary>
    private const string VlanIndexGroup = "vlan";

    #endregion

    #region Fields

    // BytesField container carries header byte range for UI highlighting
    [BytesField("vlan", "VLAN", IndexGroup = VlanIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("vlan.priority", "Priority", IndexGroup = VlanIndexGroup)]
    private FieldId _PriorityFieldId;

    [BoolField("vlan.dei", "DEI", IndexGroup = VlanIndexGroup)]
    private FieldId _DeiFieldId;

    [U64Field("vlan.id", "ID", IndexGroup = VlanIndexGroup)]
    private FieldId _IdFieldId;

    [U64Field("vlan.etype", "Type", IndexGroup = VlanIndexGroup)]
    private FieldId _EtherTypeFieldId;

    // Dispatch using the Ethernet protocol's EtherType table (resolved at registration time)
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    #endregion

    #region Pre-allocated populator (created once in OnStartCustom, shared across all packets)

    /// <summary>Pre-allocated delegate for VLAN field population — captures only 'this'.</summary>
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
        _Populator = (in MutField container) => PopulateVlanFields(in container);
        _EtherTypeSparseCache = stack.BuildU64SparseDelegateCache(_EtherTypeTableId);
    }

    /// <summary>
    /// Populates VLAN child fields at materialisation time.
    /// Re-parses the 4-byte header from the stored bytes to avoid per-packet closures.
    /// </summary>
    private ParseResult PopulateVlanFields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> headerBytes))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        if (!VlanHeader.TryParse(headerBytes.Span, out VlanHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, VlanHeader.HeaderSize, (ulong)headerBytes.Length);
        }

        byte pcp = header.Priority;
        bool dei = header.Dei != 0;
        ushort vid = header.VlanId;
        ushort etherType = header.EtherType.Value;

        string pcpText = DisplayTables.GetVlanPriorityDisplayText(pcp);
        container.AppendWithCustomText(_PriorityFieldId, FieldValue.NewU64(pcp), pcpText, in context);
        container.Append(_DeiFieldId, FieldValue.NewBool(dei), in context);
        container.Append(_IdFieldId, FieldValue.NewU64(vid), in context);

        string etherTypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtherTypeFieldId, FieldValue.NewU64(etherType), etherTypeText, in context);

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
        if (data.Length < HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, HeaderSize, (ulong)data.Length);
        }

        // Record presence in index (no-op when no index attached)
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_VlanGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Parse header using BinaryParsable-generated parser
        if (!VlanHeader.TryParse(span, out VlanHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, VlanHeader.HeaderSize, (ulong)data.Length);
        }

        byte pcp = header.Priority;
        bool dei = header.Dei != 0;
        ushort vid = header.VlanId;
        ushort etherType = header.EtherType.Value;

        // Summary closure captures pcp (byte), dei (bool), vid (ushort) via ZA.Lazy.
        LazyString summary = ZA.Lazy(
            "802.1Q Virtual LAN, PRI: ", pcp, ", DEI: ", (dei ? 1 : 0), ", ID: ", vid);

        // Store the 4-byte header so PopulateVlanFields can re-parse without captured variables.
        ReadOnlyMemory<byte> headerBytes = data[..HeaderSize];
        FieldValue headerValue = FieldValue.NewBytes(headerBytes)
            .WithCustomRepresentation(new LazyString("4 bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, headerValue, summary, _Populator);

        // Dispatch to next protocol on parentField (sibling dispatch)
        ReadOnlyMemory<byte> payload = data[HeaderSize..];
        if (_EtherTypeTableId.IsValid)
        {
            ParseResult dispatchResult = DispatchEtherType(in parentField, etherType, payload, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
        }

        return data.Length;
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
[BinaryParsable]
internal readonly partial struct VlanHeader
{
    /// <summary>Priority Code Point (3 bits, 802.1p).</summary>
    [BinaryField(BitCount = 3)]
    public byte Priority
    {
        get; init;
    }

    /// <summary>Drop Eligible Indicator (1 bit).</summary>
    [BinaryField(BitCount = 1)]
    public byte Dei
    {
        get; init;
    }

    /// <summary>VLAN Identifier (12 bits).</summary>
    [BinaryField(BitCount = 12)]
    public ushort VlanId
    {
        get; init;
    }

    /// <summary>Encapsulated protocol EtherType.</summary>
    public U16BE EtherType
    {
        get; init;
    }

    /// <summary>Serialized header size in bytes (4).</summary>
    internal const int HeaderSize = 4;
    #endregion
}
