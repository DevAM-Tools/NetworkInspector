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
    /// Minimum value for a real EtherType in Linux Cooked Capture.
    /// Values strictly greater than 0x0600 are IEEE 802.3 EtherTypes; 0x0600 itself is
    /// reserved as a Linux-internal type (LINUX_SLL_P_802_3 = 1 or similar internal codes),
    /// so only <c>etherType &gt; _MinEtherType</c> should trigger protocol dispatch.
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

    // Reuse Ethernet's EtherType table for dispatch
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    // Sparse dispatch cache (typically 4–6 EtherType entries)
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
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_SllGroupId);

        ReadOnlySpan<byte> span = data.Span;
        ushort pktType = BinaryPrimitives.ReadUInt16BigEndian(span);
        ushort haType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        ushort haLen = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        // Address field: 8 bytes starting at offset 6, but only haLen bytes are meaningful
        MacAddress srcMac = haLen >= 6
            ? MacAddress.FromBytes(span[6..12])
            : default;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(span[14..]);

        // Build lazy summary
        LazyString summary = ZA.Lazy("Linux cooked capture v1, Protocol: ",
            DisplayTables.GetEtherTypeDisplayText(etherType));

        // Append all fields eagerly (only 5 fields, no lazy needed)
        FieldValue headerValue = FieldValue.NewBytes(data[.._HeaderSize]);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, headerValue, summary);

        string pktTypeText = DisplayTables.GetSllPacketTypeDisplayText(pktType);
        container.AppendWithCustomText(_PktTypeFieldId, FieldValue.NewU64(pktType), pktTypeText);

        string haTypeText = DisplayTables.GetArpHwTypeDisplayText(haType);
        container.AppendWithCustomText(_HaTypeFieldId, FieldValue.NewU64(haType), haTypeText);

        container.Append(_HaLenFieldId, FieldValue.NewU64(haLen));
        container.Append(_SrcEthFieldId, FieldValue.NewMacAddress(srcMac));

        string etypeText = DisplayTables.GetEtherTypeDisplayText(etherType);
        container.AppendWithCustomText(_EtypeFieldId, FieldValue.NewU64(etherType), etypeText);

        // Dispatch to next protocol via EtherType.
        // Per man 7 packet / linux/if_ether.h, ETH_P_802_3 = 0x0001 and ETH_P_802_2 = 0x0004
        // are Linux-internal pseudo-types; 0x0600 itself is also a Linux-internal type
        // (ETH_P_LOOP = 0x0060 on some kernels, but 0x0600 is used as the lower 802.3 boundary).
        // Only values strictly greater than 0x0600 are genuine Ethernet II EtherTypes.
        ReadOnlyMemory<byte> payload = data[_HeaderSize..];
        if (etherType > _MinEtherType)
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
    private ParseResult _DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        foreach ((ulong key, ProtocolId id) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parentField.CallProtocol(id, payload, in context);
            }
        }

        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }
    #endregion
}
