// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols;

/// <summary>
/// Linux Cooked Capture v2 (SLL2) protocol parser.
/// Used for captures on newer Linux kernels (link type 276).
/// <para>Field tree structure:</para>
/// <code>
/// sll2: Linux cooked capture v2, Protocol: IPv4 (0x0800)
/// ├── sll2.etype: 0x0800 (IPv4)
/// ├── sll2.reserved: 0
/// ├── sll2.if_index: 3
/// ├── sll2.hatype: 1 (Ethernet)
/// ├── sll2.pkttype: 0 (Unicast to us)
/// ├── sll2.halen: 6
/// └── sll2.src.eth: AA:BB:CC:DD:EE:FF
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("sll2", "Linux cooked capture v2", Description = "SLL v2 (Linux Cooked Capture v2)")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, LinkTypeKey)]
public sealed partial class Sll2Protocol : IProtocol
{
    /// <summary>SLL v2 header size in bytes.</summary>
    private const int HeaderSize = 20;

    /// <summary>Link type key for Linux Cooked Capture v2.</summary>
    public const ulong LinkTypeKey = (ulong)LinkType.LinuxSll2;

    /// <summary>
    /// Minimum value for a real EtherType in Linux Cooked Capture v2.
    /// Values strictly greater than 0x0600 are IEEE 802.3 EtherTypes; 0x0600 itself is
    /// reserved as a Linux-internal type, so only <c>etherType &gt; MinEtherType</c>
    /// should trigger protocol dispatch.
    /// </summary>
    private const ushort MinEtherType = 0x0600;

    #region Index Group Constants

    /// <summary>Index group for always-present SLL2 fields.</summary>
    private const string Sll2IndexGroup = "sll2";

    #endregion

    #region Fields

    [BytesField("sll2", "Linux cooked capture v2", IndexGroup = Sll2IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("sll2.etype", "Protocol", IndexGroup = Sll2IndexGroup)]
    private FieldId _EtypeFieldId;

    [U64Field("sll2.reserved", "Reserved", IndexGroup = Sll2IndexGroup)]
    private FieldId _ReservedFieldId;

    [U64Field("sll2.if_index", "Interface index", IndexGroup = Sll2IndexGroup)]
    private FieldId _IfIndexFieldId;

    [U64Field("sll2.hatype", "Link-layer address type", IndexGroup = Sll2IndexGroup)]
    private FieldId _HaTypeFieldId;

    [U64Field("sll2.pkttype", "Packet type", IndexGroup = Sll2IndexGroup)]
    private FieldId _PktTypeFieldId;

    [U64Field("sll2.halen", "Link-layer address length", IndexGroup = Sll2IndexGroup)]
    private FieldId _HaLenFieldId;

    [MacField("sll2.src.eth", "Source", IndexGroup = Sll2IndexGroup)]
    private FieldId _SrcEthFieldId;

    // Reuse Ethernet's EtherType table for dispatch
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    // Sparse dispatch cache (typically 4–6 EtherType entries)
    private (ulong Key, ParseDelegate Parse)[] _EtherTypeSparseCache = [];

    partial void OnStartCustom(Stack stack) =>
        _EtherTypeSparseCache = stack.BuildU64SparseDelegateCache(_EtherTypeTableId);

    /// <summary>
    /// Parses a Sll2 protocol unit from the supplied <paramref name="data"/> buffer,
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
        context.RecordGroupPresence(_Sll2GroupId);

        ReadOnlySpan<byte> span = data.Span;

        // SLL2 header layout: Protocol(2) + Reserved(2) + InterfaceIndex(4) +
        //                     HaType(2) + PktType(1) + HaLen(1) + Address(8)
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort reserved = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        uint ifIndex = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        ushort haType = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        byte pktType = span[10];
        byte haLen = span[11];
        // Address field: 8 bytes starting at offset 12, only haLen bytes meaningful
        MacAddress srcMac = haLen >= 6
            ? MacAddress.FromBytes(span[12..18])
            : default;

        // Build lazy summary
        LazyString summary = ZA.Lazy("Linux cooked capture v2, Protocol: ",
            DisplayTables.GetEtherTypeDisplayText(etherType));

        // Append all fields eagerly (only 7 fields, no lazy needed)
        FieldValue headerValue = FieldValue.NewBytes(data[..HeaderSize]);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, headerValue, summary, in context);

        string etypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtypeFieldId, FieldValue.NewU64(etherType), etypeText, in context);

        container.Append(_ReservedFieldId, FieldValue.NewU64(reserved), in context);
        container.Append(_IfIndexFieldId, FieldValue.NewU64(ifIndex), in context);

        string haTypeText = DisplayTables.GetArpHwTypeDisplayText(haType);
        container.AppendWithCustomText(_HaTypeFieldId, FieldValue.NewU64(haType), haTypeText, in context);

        string pktTypeText = DisplayTables.GetSllPacketTypeDisplayText(pktType);
        container.AppendWithCustomText(_PktTypeFieldId, FieldValue.NewU64(pktType), pktTypeText, in context);

        container.Append(_HaLenFieldId, FieldValue.NewU64(haLen), in context);
        container.Append(_SrcEthFieldId, FieldValue.NewMacAddress(srcMac), in context);

        // Dispatch to next protocol via EtherType.
        // Only values strictly greater than 0x0600 are genuine Ethernet II EtherTypes.
        ReadOnlyMemory<byte> payload = data[HeaderSize..];
        if (etherType > MinEtherType)
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
    /// Dispatches to the next protocol by EtherType using the sparse cache.
    /// Falls back to full table dispatch for unknown EtherTypes.
    /// </summary>
    private ParseResult DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        foreach ((ulong key, ParseDelegate parse) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parse(in parentField, payload, in context);
            }
        }

        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }
    #endregion
}
