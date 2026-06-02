// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Frame protocol — the entry point for all packet parsing.
/// Extracts frame metadata (id, timestamp, link type, length, interface info)
/// and dispatches to the appropriate link-layer protocol via the link type table.
/// <para>Field tree structure:</para>
/// <code>
/// frame: Frame 1, 128 bytes, Ethernet
/// ├── frame.id: 1
/// ├── frame.time: 2024-01-15 10:30:00.123456789
/// ├── frame.link_type: 1 (Ethernet)
/// ├── frame.len: 128
/// ├── frame.data: [128 bytes]
/// └── frame.interface: Interface
///     ├── frame.interface.id: 0
///     └── frame.interface.name: eth0
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("frame", "Frame", Description = "Frame metadata and link-layer dispatch")]
public sealed partial class FrameProtocol : IProtocol
{
    #region Table Name Constants

    /// <summary>Dispatch table name for link-layer protocol lookup by link type.</summary>
    public const string LinkTypeTableName = "frame.link_type";

    #endregion

    #region Index Group Constants

    /// <summary>Index group for always-present frame fields.</summary>
    private const string FrameIndexGroup = "frame";

    /// <summary>Index group for optional interface fields.</summary>
    private const string InterfaceIndexGroup = "frame.interface";

    #endregion

    #region Fields

    [NoneField("frame", "Frame", IndexGroup = FrameIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("frame.id", "Frame Number", IndexGroup = FrameIndexGroup)]
    private FieldId _IdFieldId;

    [TimestampField("frame.time", "Arrival Time", IndexGroup = FrameIndexGroup)]
    private FieldId _TimeFieldId;

    [U64Field("frame.link_type", "Link Type", IndexGroup = FrameIndexGroup)]
    private FieldId _LinkTypeFieldId;

    [U64Field("frame.len", "Frame Length", IndexGroup = FrameIndexGroup)]
    private FieldId _LengthFieldId;

    [BytesField("frame.data", "Frame Data", IndexGroup = FrameIndexGroup)]
    private FieldId _DataFieldId;

    [NoneField("frame.interface", "Interface", IndexGroup = InterfaceIndexGroup)]
    private FieldId _InterfaceFieldId;

    [U64Field("frame.interface.id", "Interface ID", IndexGroup = InterfaceIndexGroup)]
    private FieldId _InterfaceIdFieldId;

    [StringField("frame.interface.name", "Interface Name", IndexGroup = InterfaceIndexGroup)]
    private FieldId _InterfaceNameFieldId;

    #endregion

    #region Dispatch Table

    /// <summary>Link type dispatch table — routes to link-layer protocols (Ethernet, SLL, etc.).</summary>
    [ProtocolTableU64(LinkTypeTableName, "Link Type")]
    private ProtocolTableId _LinkTypeTableId;

    #endregion

    #region Pre-allocated populators (created once in OnStartCustom, shared across all packets)

    /// <summary>Pre-allocated delegate for frame field population — captures only 'this'.</summary>
    private LazyPopulator _Populator = null!;

    /// <summary>Pre-allocated delegate for interface field population — captures only 'this'.</summary>
    private LazyPopulator _InterfacePopulator = null!;

    // Sparse dispatch cache built from the link-type protocol table at stack start.
    // Linear scan over typically 1–3 entries; avoids dictionary hash computation per packet.
    // Pre-bound delegates for direct invocation without interface vtable dispatch.
    private (ulong Key, ParseDelegate Parse)[] _LinkTypeSparseCache = [];

    partial void OnStartCustom(Stack stack)
    {
        // Allocate once — each delegate captures only 'this' (a singleton per registered protocol).
        _Populator = PopulateFrameFields;
        _InterfacePopulator = PopulateInterfaceFields;
        // Link-type table has very few entries (typically just Ethernet = 1); cache all of them.
        // Instance cache stores IProtocol references for zero-indirection dispatch.
        _LinkTypeSparseCache = stack.BuildU64SparseDelegateCache(_LinkTypeTableId);
    }

    /// <summary>
    /// Populates frame child fields at materialisation time.
    /// Reads all needed data from <see cref="MutField.Packet"/> and the stored bytes
    /// to avoid per-packet closure allocations.
    /// </summary>
    private ParseResult PopulateFrameFields(in MutField container)
    {
        Frame frame = container.Packet.Frame;

        // Re-read metadata — no captured variables needed.
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        LinkType linkType = frame.LinkType;
        string linkTypeName = GetLinkTypeName(linkType);
        ulong linkTypeValue = (ulong)linkType;

        container.Append(_IdFieldId, FieldValue.NewU64((ulong)frame.Id.Value));
        container.Append(_TimeFieldId, FieldValue.NewTimestamp(frame.Timestamp));
        container.AppendWithCustomText(_LinkTypeFieldId, FieldValue.NewU64(linkTypeValue), linkTypeName);
        container.Append(_LengthFieldId, FieldValue.NewU64((ulong)data.Length));
        container.Append(_DataFieldId, FieldValue.NewBytes(data));

        // Append interface sub-tree if interface info is registered.
        if (frame.HasInterface)
        {
            FrameInterfaceInfo? interfaceInfo = null;
            container.Packet.Stack.FrameInterfaceRegistry.TryGet(frame.InterfaceId, out interfaceInfo);
            if (interfaceInfo is not null)
            {
                // Use the pre-cached _InterfacePopulator — zero per-packet allocation.
                container.AppendLazy(_InterfaceFieldId, FieldValue.None, _InterfacePopulator);
            }
        }

        return 0;
    }

    /// <summary>
    /// Populates interface child fields at materialisation time.
    /// Re-queries the interface registry from <see cref="MutField.Packet"/> instead of
    /// capturing the info object in a per-packet closure.
    /// </summary>
    private ParseResult PopulateInterfaceFields(in MutField container)
    {
        Frame frame = container.Packet.Frame;
        if (!frame.HasInterface)
        {
            return ParseError.InvalidData(ProtocolName, "No interface metadata available");
        }

        FrameInterfaceId interfaceId = frame.InterfaceId;
        if (!container.Packet.Stack.FrameInterfaceRegistry.TryGet(interfaceId, out FrameInterfaceInfo? interfaceInfo)
            || interfaceInfo is null)
        {
            return ParseError.InvalidData(ProtocolName, $"Interface {interfaceId.Value} not found in registry");
        }

        container.Append(_InterfaceIdFieldId, FieldValue.NewU64((ulong)interfaceId.Value));
        container.Append(_InterfaceNameFieldId, FieldValue.NewString(interfaceInfo.UiName));

        return 0;
    }

    /// <summary>
    /// Returns the human-readable display name for a given <see cref="LinkType"/>.
    /// </summary>
    /// <param name="linkType">The link-layer header type.</param>
    /// <returns>A static display name string.</returns>
    internal static string GetLinkTypeName(LinkType linkType) => linkType switch
    {
        LinkType.Null => "NULL/Loopback",
        LinkType.Ethernet => "Ethernet",
        LinkType.Ax25 => "AX.25",
        LinkType.Ieee8025 => "Token Ring",
        LinkType.ArcnetBsd => "ARCnet",
        LinkType.Slip => "SLIP",
        LinkType.Ppp => "PPP",
        LinkType.Fddi => "FDDI",
        LinkType.PppHdlc => "PPP HDLC",
        LinkType.PppEther => "PPPoE",
        LinkType.AtmRfc1483 => "ATM RFC 1483",
        LinkType.Raw => "Raw IP",
        LinkType.CHdlc => "Cisco HDLC",
        LinkType.Ieee80211 => "IEEE 802.11",
        LinkType.Frelay => "Frame Relay",
        LinkType.Loop => "Loopback",
        LinkType.LinuxSll => "Linux SLL",
        LinkType.Ltalk => "LocalTalk",
        LinkType.Pflog => "OpenBSD pflog",
        LinkType.Ieee80211Prism => "Prism 802.11",
        LinkType.IpOverFc => "IP over Fibre Channel",
        LinkType.SunAtm => "SunATM",
        LinkType.Ieee80211Radiotap => "Radiotap 802.11",
        LinkType.ArcnetLinux => "ARCnet (Linux)",
        LinkType.AppleIpOverIeee1394 => "Apple IP over FireWire",
        LinkType.Mtp2WithPhdr => "MTP2 with Pseudoheader",
        LinkType.Mtp2 => "MTP2",
        LinkType.Mtp3 => "MTP3",
        LinkType.Sccp => "SCCP",
        LinkType.Docsis => "DOCSIS",
        LinkType.LinuxIrda => "Linux IrDA",
        LinkType.CanSocketcan => "SocketCAN",
        _ => "Unknown",
    };

    /// <summary>
    /// Parses a Frame protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Frame metadata comes from Packet.Frame, not from the data buffer
        Frame frame = parentField.Packet.Frame;

        // Record presence in index
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_FrameGroupId);

        // Extract frame metadata needed for: summary closure + dispatch + index recording.
        // (Other fields are re-read inside PopulateFrameFields at materialisation time.)
        FrameId frameId = frame.Id;
        LinkType linkType = frame.LinkType;
        int frameLength = data.Length;
        string linkTypeName = GetLinkTypeName(linkType);
        ulong linkTypeValue = (ulong)linkType;

        // Check for interface info and record optional index group
        bool hasInterface = frame.HasInterface;
        FrameInterfaceId interfaceId = frame.InterfaceId;
        FrameInterfaceInfo? interfaceInfo = null;
        if (hasInterface)
        {
            context.Stack!.FrameInterfaceRegistry.TryGet(interfaceId, out interfaceInfo);
            if (interfaceInfo is not null)
            {
                context.RecordGroupPresence(_FrameInterfaceGroupId);
            }
        }

        // Summary captures only 3 small values (int, int, string-ref) from the eagerly-extracted locals.
        LazyString summary = ZA.Lazy("Frame ", frameId.Value, ": ", frameLength, " bytes (", linkTypeName, ")");

        // Store the full frame data in the field value so PopulateFrameFields can access it
        // without any captured state (reads from container.Value.Data.AsBytes()).
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, data, summary, _Populator);

        // Dispatch to link-layer protocol on parentField (sibling dispatch — all protocols are direct children of root)
        ParseResult dispatchResult = DispatchLinkType(in parentField, linkTypeValue, data, in context);
        if (dispatchResult.IsError)
        {
            return dispatchResult;
        }

        return data.Length;
    }

    /// <summary>
    /// Dispatches to the link-layer protocol by link type.
    /// Scans the pre-built sparse cache (typically 1–3 entries) before falling back to
    /// full table dispatch for multi-protocol keys or unknown link types.
    /// </summary>
    private ParseResult DispatchLinkType(
        in MutField parentField, ulong linkType, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Direct delegate call — no ProtocolId resolution, no vtable dispatch.
        foreach ((ulong key, ParseDelegate parse) in _LinkTypeSparseCache)
        {
            if (key == linkType)
            {
                return parse(in parentField, data, in context);
            }
        }

        // Fallback: multi-protocol link type key or link type not in cache.
        return parentField.TryCallNextProtocolU64(_LinkTypeTableId, linkType, data, in context);
    }
    #endregion
}
