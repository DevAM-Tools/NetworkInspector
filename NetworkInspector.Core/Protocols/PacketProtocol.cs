// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Packet protocol — the top-level entry point called by <see cref="Packet.ParseFrame(PacketId, Stack, Frame)"/>.
/// <para>Responsibilities:</para>
/// <list type="number">
///   <item>Appends packet metadata fields (id, timestamp, frame source id) eagerly to the tree.</item>
///   <item>Dispatches to the frame protocol (auto-discovered by name "frame" at build time,
///         or overridden per-packet via <see cref="Packet.FirstProtocolOverride"/>).</item>
///   <item>After dispatch completes, appends <c>packet.info</c> as a lazy string value —
///         the summary is deferred until first access (filter, export, or <see cref="Packet.Info"/>)
///         and cached in-heap via <see cref="ZeroAlloc.LazyString"/>.</item>
/// </list>
/// <para>Field tree structure:</para>
/// <code>
/// packet: Packet 1
/// ├── packet.id: 1
/// ├── packet.timestamp: 2024-01-15 10:30:00.123456789
/// ├── packet.frame_source_id: 0
/// └── packet.info: "DNS Standard query ..."   ← lazily evaluated on first read
/// </code>
/// </summary>
internal sealed class PacketProtocol : IProtocol
{
    #region Protocol Name Constants

    /// <summary>Machine-readable protocol name constant. Matches <see cref="Name"/>.</summary>
    internal const string ProtocolName = "packet";

    #endregion

    #region Index Group

    /// <summary>Index group for always-present packet fields.</summary>
    private const string PacketIndexGroup = ProtocolName;

    #endregion

    #region Field IDs

    /// <summary>Container field for the packet metadata subtree.</summary>
    private FieldId _PacketFieldId;

    /// <summary>Unique packet identifier.</summary>
    private FieldId _IdFieldId;

    /// <summary>Packet capture timestamp.</summary>
    private FieldId _TimestampFieldId;

    /// <summary>Frame source identifier (maps to capture interface/source).</summary>
    private FieldId _FrameSourceIdFieldId;

    /// <summary>Packet info/summary string (set by sub-protocols during parsing).</summary>
    private FieldId _InfoFieldId;

    /// <summary>Index group ID for cross-packet indexing.</summary>
    private IndexGroupId _PacketGroupId;

    #endregion

    #region IProtocol Implementation

    public string Name => ProtocolName;
    public string UiName => "Packet";
    public string? Description => "Top-level packet protocol with metadata fields and frame dispatch";

    /// <summary>
    /// Registers packet-level fields and the index group.
    /// Called during stack building from <see cref="StackBuilder"/>.
    /// </summary>
    internal void RegisterWith(IStackBuilder builder, ProtocolId protocolId)
    {
        _PacketGroupId = builder.GetOrCreateIndexGroup(PacketIndexGroup);

        _PacketFieldId = builder.RegisterFieldInGroup(
            protocolId, "packet", "Packet", FieldType.None, PacketIndexGroup,
            "Container for packet-level metadata");

        _IdFieldId = builder.RegisterFieldInGroup(
            protocolId, "packet.id", "Packet Number", FieldType.U64, PacketIndexGroup,
            "Unique sequential packet identifier");

        _TimestampFieldId = builder.RegisterFieldInGroup(
            protocolId, "packet.timestamp", "Timestamp", FieldType.Timestamp, PacketIndexGroup,
            "Capture timestamp");

        _FrameSourceIdFieldId = builder.RegisterFieldInGroup(
            protocolId, "packet.frame_source_id", "Frame Source", FieldType.U64, PacketIndexGroup,
            "Frame source identifier");

        _InfoFieldId = builder.RegisterFieldInGroup(
            protocolId, "packet.info", "Info", FieldType.String, PacketIndexGroup,
            "Packet summary/info line set by sub-protocols");
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Packet packet = parentField.Packet;

        // Record protocol and index group presence for cross-packet indexing
        context.RecordGroupPresence(_PacketGroupId);

        // Append the packet container field eagerly (NOT lazy — packet.info must
        // be immediately available after parsing completes)
        MutField packetContainer = parentField.Append(_PacketFieldId, FieldValue.None, in context);

        // Append packet metadata fields eagerly
        packetContainer.Append(_IdFieldId, FieldValue.NewU64((ulong)packet.Id.Value), in context);
        packetContainer.Append(_TimestampFieldId, FieldValue.NewTimestamp(packet.Timestamp), in context);
        packetContainer.Append(_FrameSourceIdFieldId, FieldValue.NewU64((ulong)packet.FrameSourceId.Value), in context);

        // Dispatch to the first protocol after PacketProtocol:
        // - Per-packet override (set via ParseFrame overload) takes priority
        // - Falls back to the stack's auto-discovered frame protocol
        ProtocolId dispatchTarget = packet.FirstProtocolOverride.IsValid
            ? packet.FirstProtocolOverride
            : context.Stack!.FrameProtocolId;

        if (dispatchTarget.IsValid)
        {
            ParseResult dispatchResult = parentField.CallProtocol(dispatchTarget, data, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
        }

        // After dispatch: append packet.info as a lazy string value.
        // Sub-protocols set Packet._Info (a LazyString) via SetPacketInfo during parsing.
        // By wrapping it in a LazyStringValue (heap-resident reference type), the factory
        // is not evaluated here — evaluation is deferred to first access via Packet.Info,
        // TryGetFieldValue("packet.info"), or any exporter that reads the field value.
        // The LazyStringValue caches the result in-heap so all copies of the FieldValueData
        // share one factory invocation.
        MutField infoField = packetContainer.Append(_InfoFieldId, FieldValue.NewLazyString(packet.InfoLazy), in context);
        packet.SetInfoFieldIndex(infoField.StorageIndex);

        return data.Length;
    }
    #endregion
}