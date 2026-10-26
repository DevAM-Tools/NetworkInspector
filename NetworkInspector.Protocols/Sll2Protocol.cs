// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
    private const int _HeaderSize = 20;

    /// <summary>Link type key for Linux Cooked Capture v2.</summary>
    public const ulong LinkTypeKey = (ulong)LinkType.LinuxSll2;

    /// <summary>
    /// Dispatch threshold for the SLL2 protocol field. In IEEE 802.3 a value of 1536 (0x0600) or more is an EtherType
    /// and a smaller value is a frame length; the Linux cooked header also uses small values for Linux-internal
    /// pseudo protocols that are not EtherTypes. Only <c>etherType &gt;= _MinEtherType</c> triggers protocol dispatch, the same
    /// boundary as in <c>EthernetProtocol</c> (IEEE 802.3 clause 3.2.6: 0x0600 itself is an EtherType).
    /// </summary>
    private const ushort _MinEtherType = 0x0600;

    #region Index Group Constants

    /// <summary>Index group for always-present SLL2 fields.</summary>
    private const string _Sll2IndexGroup = "sll2";

    #endregion

    #region Fields

    [BytesField("sll2", "Linux cooked capture v2", IndexGroup = _Sll2IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("sll2.etype", "Protocol", IndexGroup = _Sll2IndexGroup)]
    private FieldId _EtypeFieldId;

    [U64Field("sll2.reserved", "Reserved", IndexGroup = _Sll2IndexGroup)]
    private FieldId _ReservedFieldId;

    [U64Field("sll2.if_index", "Interface index", IndexGroup = _Sll2IndexGroup)]
    private FieldId _IfIndexFieldId;

    [U64Field("sll2.hatype", "Link-layer address type", IndexGroup = _Sll2IndexGroup)]
    private FieldId _HaTypeFieldId;

    [U64Field("sll2.pkttype", "Packet type", IndexGroup = _Sll2IndexGroup)]
    private FieldId _PktTypeFieldId;

    [U64Field("sll2.halen", "Link-layer address length", IndexGroup = _Sll2IndexGroup)]
    private FieldId _HaLenFieldId;

    [MacField("sll2.src.eth", "Source", IndexGroup = _Sll2IndexGroup)]
    private FieldId _SrcEthFieldId;

    // SLL2 carries a genuine EtherType, so it shares Ethernet's EtherType table instead of defining its own:
    // every protocol registered for an EtherType is reachable from both link layers.
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    // Sparse dispatch cache built once at stack start (typically 4–6 EtherType entries). It maps an
    // EtherType to its single registered protocol so the per-frame path scans a tiny array instead of
    // doing a table lookup. Keys with several (or no) protocols are not in the cache and take the table path.
    // Written once in _OnStartCustom before parsing begins, read-only afterwards (no synchronization needed).
    private (ulong Key, ProtocolId Id)[] _EtherTypeSparseCache = [];

    partial void _OnStartCustom(Stack stack) =>
        _EtherTypeSparseCache = stack.BuildU64SparseIdCache(_EtherTypeTableId);

    /// <summary>
    /// Parses a Sll2 protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>
    /// The whole <paramref name="data"/> length (the payload is attributed to this protocol even when no next protocol is
    /// dispatched), or a <see cref="ParseError"/> describing the failure.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Fixed 20-byte header: shorter input cannot be decoded at all, so fail before any field is appended.
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Sll2GroupId);

        // Decode the header with span reads (no allocation). All multi-byte header fields are read as
        // big-endian (network order), including the interface index.
        ReadOnlySpan<byte> span = data.Span;

        // SLL2 header layout: Protocol(2) + Reserved(2) + InterfaceIndex(4) +
        //                     HaType(2) + PktType(1) + HaLen(1) + Address(8)
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort reserved = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        uint ifIndex = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        ushort haType = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        byte pktType = span[10];
        byte haLen = span[11];

        // Address field: 8 bytes starting at offset 12, only haLen bytes meaningful. It is shown as a MAC
        // only when at least 6 address bytes are valid (Ethernet-like link layers); otherwise the field is
        // still appended with the default all-zero MAC. The span slice is within the 20-byte header checked above.
        MacAddress srcMac = haLen >= 6
            ? MacAddress.FromBytes(span[12..18])
            : default;

        // Append all fields eagerly (only 7 fields, no lazy container needed). The container stores the
        // header bytes by reference (a ReadOnlyMemory slice, no copy) and its summary text embeds the
        // EtherType display text.
        FieldValue headerValue = FieldValue.NewBytes(data[.._HeaderSize]);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, headerValue, "Linux cooked capture v2, Protocol: ",
            DisplayTables.GetEtherTypeDisplayText(etherType));

        string etypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtypeFieldId, FieldValue.NewU64(etherType), etypeText);

        container.Append(_ReservedFieldId, FieldValue.NewU64(reserved));
        container.Append(_IfIndexFieldId, FieldValue.NewU64(ifIndex));

        string haTypeText = DisplayTables.GetArpHwTypeDisplayText(haType);
        container.AppendWithCustomText(_HaTypeFieldId, FieldValue.NewU64(haType), haTypeText);

        string pktTypeText = DisplayTables.GetSllPacketTypeDisplayText(pktType);
        container.AppendWithCustomText(_PktTypeFieldId, FieldValue.NewU64(pktType), pktTypeText);

        container.Append(_HaLenFieldId, FieldValue.NewU64(haLen));
        container.Append(_SrcEthFieldId, FieldValue.NewMacAddress(srcMac));

        // Dispatch to next protocol via EtherType. The payload is dispatched under parentField (a sibling of
        // this container), like Ethernet does. Values below _MinEtherType are not EtherTypes (see
        // _MinEtherType), so nothing is dispatched for them. Either way the whole frame is reported as consumed.
        // "No protocol registered for this EtherType" (NotDispatched) is not an error; only real callee errors
        // propagate.
        ReadOnlyMemory<byte> payload = data[_HeaderSize..];
        // >= and not >: 0x0600 is the first value that is an EtherType (IEEE 802.3), as Ethernet treats it; a strict
        // comparison would silently skip a protocol registered for exactly that EtherType.
        if (etherType >= _MinEtherType)
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
    /// Dispatches to the next protocol by EtherType using the sparse cache.
    /// Falls back to full table dispatch for unknown EtherTypes.
    /// </summary>
    /// <param name="parentField">Field under which the next protocol appends its container.</param>
    /// <param name="etherType">EtherType key (only values of at least <see cref="_MinEtherType"/> reach this method).</param>
    /// <param name="payload">Bytes after the SLL2 header.</param>
    /// <param name="context">Parse context forwarded to the next protocol.</param>
    /// <returns>The callee result, or <see cref="ParseResult.NotDispatched"/> when no protocol is registered for the key.</returns>
    private ParseResult _DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        // Fast path: key with exactly one registered protocol. The linear scan over a handful of entries
        // beats a dictionary lookup and avoids touching the table.
        foreach ((ulong key, ProtocolId id) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parentField.CallProtocol(id, payload, in context);
            }
        }

        // Slow path: multi-protocol EtherType key or EtherType not in the cache; the table resolves it.
        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }
    #endregion
}
