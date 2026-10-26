// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Linux Cooked Capture v1 (SLL) protocol parser.
/// Used for captures on the Linux "any" interface (link type 113).
/// <para>Field tree structure:</para>
/// <code>
/// sll: Linux cooked capture v1, Protocol: IPv4 (0x0800)
/// ├── sll.pkttype: 0 (Unicast to us)
/// ├── sll.hatype: 1 (Ethernet)
/// ├── sll.halen: 6
/// ├── sll.src.eth: AA:BB:CC:DD:EE:FF
/// └── sll.etype: 0x0800 (IPv4)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("sll", "Linux cooked capture v1", Description = "SLL (Linux Cooked Capture)")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, LinkTypeKey)]
public sealed partial class SllProtocol : IProtocol
{
    /// <summary>SLL v1 header size in bytes.</summary>
    private const int _HeaderSize = 16;

    /// <summary>Link type key for Linux Cooked Capture v1.</summary>
    public const ulong LinkTypeKey = (ulong)LinkType.LinuxSll;

    /// <summary>
    /// Dispatch threshold for the SLL protocol field. In IEEE 802.3 a value of 1536 (0x0600) or more is an EtherType
    /// and a smaller value is a frame length; the Linux cooked header also uses small values for Linux-internal
    /// pseudo protocols (for example 0x0001 for raw 802.3 and 0x0004 for 802.2) that are not EtherTypes.
    /// Only <c>etherType &gt;= _MinEtherType</c> triggers protocol dispatch, the same boundary as in <c>EthernetProtocol</c>
    /// (IEEE 802.3 clause 3.2.6: 0x0600 itself is an EtherType).
    /// </summary>
    private const ushort _MinEtherType = 0x0600;

    #region Index Group Constants

    /// <summary>Index group for always-present SLL fields.</summary>
    private const string _SllIndexGroup = "sll";

    #endregion

    #region Fields

    [BytesField("sll", "Linux cooked capture v1", IndexGroup = _SllIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("sll.pkttype", "Packet type", IndexGroup = _SllIndexGroup)]
    private FieldId _PktTypeFieldId;

    [U64Field("sll.hatype", "Link-layer address type", IndexGroup = _SllIndexGroup)]
    private FieldId _HaTypeFieldId;

    [U64Field("sll.halen", "Link-layer address length", IndexGroup = _SllIndexGroup)]
    private FieldId _HaLenFieldId;

    [MacField("sll.src.eth", "Source", IndexGroup = _SllIndexGroup)]
    private FieldId _SrcEthFieldId;

    [U64Field("sll.etype", "Protocol", IndexGroup = _SllIndexGroup)]
    private FieldId _EtypeFieldId;

    // SLL carries a genuine EtherType, so it shares Ethernet's EtherType table instead of defining its own:
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
    /// Parses a Sll protocol unit from the supplied <paramref name="data"/> buffer,
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
        // Fixed 16-byte header: shorter input cannot be decoded at all, so fail before any field is appended.
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_SllGroupId);

        // Decode the header with span reads (no allocation). SLL v1 layout, all fields big-endian:
        // PacketType(2) + HaType(2) + HaLen(2) + Address(8) + Protocol(2).
        ReadOnlySpan<byte> span = data.Span;
        ushort pktType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort haType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);

        // Address field: 8 bytes starting at offset 6, but only haLen bytes are meaningful. It is shown as a
        // MAC only when at least 6 address bytes are valid (Ethernet-like link layers); otherwise the field is
        // still appended with the default all-zero MAC. The slice is within the 16-byte header checked above.
        MacAddress srcMac = haLen >= 6
            ? MacAddress.FromBytes(span[6..12])
            : default;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(span[14..]);

        // Append all fields eagerly (only 5 fields, no lazy container needed). The container stores the
        // header bytes by reference (a ReadOnlyMemory slice, no copy) and its summary text embeds the
        // EtherType display text.
        FieldValue headerValue = FieldValue.NewBytes(data[.._HeaderSize]);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, headerValue, "Linux cooked capture v1, Protocol: ",
            DisplayTables.GetEtherTypeDisplayText(etherType));

        string pktTypeText = DisplayTables.GetSllPacketTypeDisplayText(pktType);
        container.AppendWithCustomText(_PktTypeFieldId, FieldValue.NewU64(pktType), pktTypeText);

        string haTypeText = DisplayTables.GetArpHwTypeDisplayText(haType);
        container.AppendWithCustomText(_HaTypeFieldId, FieldValue.NewU64(haType), haTypeText);

        container.Append(_HaLenFieldId, FieldValue.NewU64(haLen));
        container.Append(_SrcEthFieldId, FieldValue.NewMacAddress(srcMac));

        string etypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtypeFieldId, FieldValue.NewU64(etherType), etypeText);

        // Dispatch to next protocol via EtherType. Per man 7 packet / linux/if_ether.h, ETH_P_802_3 = 0x0001
        // and ETH_P_802_2 = 0x0004 are Linux-internal pseudo-types, not EtherTypes, so nothing is dispatched
        // for values below _MinEtherType (see there). Either way the whole frame is reported as consumed.
        // The payload is dispatched under parentField (a sibling of this container), like Ethernet does.
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
    /// <param name="payload">Bytes after the SLL header.</param>
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
