// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Accumulates packets into a column-oriented in-memory batch shared by every columnar exporter
/// (PBF columnar blocks, Parquet, DuckDB). Each field becomes a <see cref="FieldColumnBag"/>
/// keyed by field ID; the field-tree topology is flattened into <see cref="Topology"/> rows so
/// sinks can reconstruct hierarchy with a self-join instead of nested containers.
/// <para>
/// The <see cref="Catalog"/> and per-field <see cref="FieldColumnBag"/> instances are kept alive
/// across <see cref="Reset"/> calls (only their row-level state is cleared) so field metadata does
/// not need to be re-derived for every block within one export run.
/// </para>
/// </summary>
internal sealed class ColumnarPacketBatch : IDisposable
{
    #region Fields

    internal ColumnarDetailFlags Flags { get; }
    private int _MaxPacketsPerBlock { get; }
    private long _MaxBlockSize { get; }
    internal bool IsTimestampSorted { get; }

    private readonly List<int> _PacketIds = new(256);
    private readonly List<long> _Timestamps = new(256);
    private readonly List<string> _Infos = new(256);
    private readonly List<byte[]> _FrameBytesList = new(256);
    private readonly List<TopologyNode> _Topology = new(256 * 8);

    private readonly Dictionary<int, FieldColumnBag> _FieldBags = new(64);
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    /// <summary>Estimated bytes of the packet-level lists (ids, timestamps, infos, frame bytes).</summary>
    private long _BatchEstimatedSize { get; set; }

    /// <summary>
    /// Running total of the bytes appended to all <see cref="FieldColumnBag"/>s since the last <see cref="Reset"/>.
    /// Kept incrementally so the flush check in <see cref="AddPacket"/> is O(1) instead of O(distinct fields).
    /// </summary>
    private long _FieldBagsEstimatedSize { get; set; }

    private bool _Disposed;

    #endregion

    #region Constructor

    /// <summary>Creates a new columnar packet batch.</summary>
    /// <param name="flags">Controls which optional data is captured per packet/field.</param>
    /// <param name="maxPacketsPerBlock">Maximum packets before <see cref="AddPacket"/> signals a flush.</param>
    /// <param name="maxBlockSize">Maximum estimated size (bytes) before <see cref="AddPacket"/> signals a flush.</param>
    /// <param name="isTimestampSorted">Whether the caller guarantees packets are added in non-decreasing timestamp order.</param>
    internal ColumnarPacketBatch(
        ColumnarDetailFlags flags,
        int maxPacketsPerBlock,
        long maxBlockSize,
        bool isTimestampSorted)
    {
        Flags = flags;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        IsTimestampSorted = isTimestampSorted;
    }

    #endregion

    #region Properties

    /// <summary>Number of packets accumulated in this batch.</summary>
    internal int PacketCount => _PacketIds.Count;

    /// <summary>Field metadata keyed by field ID, populated lazily as fields are first observed.</summary>
    internal IReadOnlyDictionary<int, FieldCatalogEntry> Catalog => _Catalog;

    /// <summary>Flattened field-tree topology rows across all packets in this batch.</summary>
    internal IReadOnlyList<TopologyNode> Topology => _Topology;

    /// <summary>Packet identifiers, one per packet (same range as <see cref="PacketId"/>).</summary>
    internal IReadOnlyList<int> PacketIds => _PacketIds;

    /// <summary>Packet timestamps (nanoseconds since Unix epoch), one per packet.</summary>
    internal IReadOnlyList<long> Timestamps => _Timestamps;

    /// <summary>
    /// Packet summary strings, populated only when <see cref="ColumnarDetailFlags.IncludeInfo"/>
    /// is set; empty otherwise.
    /// </summary>
    internal IReadOnlyList<string> Infos => _Infos;

    /// <summary>
    /// Raw captured frame bytes, populated only when <see cref="ColumnarDetailFlags.IncludeFrameBytes"/>
    /// is set; empty otherwise.
    /// </summary>
    internal IReadOnlyList<byte[]> FrameBytesList => _FrameBytesList;

    /// <summary>Per-field column bags, keyed by field ID.</summary>
    internal IReadOnlyDictionary<int, FieldColumnBag> FieldBags => _FieldBags;

    /// <summary>
    /// Rough estimate of the memory consumed by this batch, in bytes: the packet-level lists plus
    /// <see cref="FieldBagsEstimatedSizeBytes"/>. O(1); both parts are running totals.
    /// </summary>
    internal long EstimatedSizeBytes => _BatchEstimatedSize + _FieldBagsEstimatedSize;

    /// <summary>
    /// Running total of the estimated bytes held by all <see cref="FieldBags"/>. Always equal to the sum of
    /// every bag's <see cref="FieldColumnBag.EstimatedSizeBytes"/>; maintained incrementally by <see cref="AddPacket"/>
    /// and reset by <see cref="Reset"/>.
    /// </summary>
    internal long FieldBagsEstimatedSizeBytes => _FieldBagsEstimatedSize;

    #endregion

    #region Public API

    /// <summary>
    /// Adds a packet's identifiers, optional info/frame bytes, and its full field tree
    /// (topology and per-field values) to the batch.
    /// </summary>
    /// <param name="packet">The packet to add.</param>
    /// <returns><see langword="true"/> if the caller should flush this batch (packet count or estimated size threshold reached).</returns>
    internal bool AddPacket(Packet packet)
    {
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
        }

        int packetIdValue = packet.Id.Value;
        _PacketIds.Add(packetIdValue);
        _Timestamps.Add(packet.Timestamp.AsNanos);
        _BatchEstimatedSize += 12; // int PacketId (4) + long Timestamp (8)

        if ((Flags & ColumnarDetailFlags.IncludeInfo) != 0)
        {
            string info = packet.Info;
            _Infos.Add(info);
            _BatchEstimatedSize += info.Length * 2;
        }

        if ((Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0)
        {
            // Exact-length copy required: sinks retain byte[] until flush and packet buffers
            // may be recycled after OnPacket returns. Prefer uninitialized alloc + CopyTo
            // over MemoryExtensions.ToArray for a single clear copy path.
            ReadOnlySpan<byte> frameSpan = packet.Frame.Data.Span;
            byte[] frameBytes = GC.AllocateUninitializedArray<byte>(frameSpan.Length);
            frameSpan.CopyTo(frameBytes);
            _FrameBytesList.Add(frameBytes);
            _BatchEstimatedSize += frameBytes.Length;
        }

        Field root = packet.RootField();
        if (root.HasChildren(materialize: true))
        {
            int nodeCounter = 0;
            foreach (Field child in root.Children(materialize: true))
            {
                _EncodeField(packet, child, parentNodeId: -1, ref nodeCounter);
            }
        }

        return PacketCount >= _MaxPacketsPerBlock || EstimatedSizeBytes >= _MaxBlockSize;
    }

    /// <summary>
    /// Clears all row-level state (packets, topology, and field bag rows) for reuse by the next
    /// block. Field metadata (<see cref="Catalog"/> and the <see cref="FieldColumnBag"/> instances
    /// themselves) is retained.
    /// </summary>
    internal void Reset()
    {
        // The running total must match the summed form; checked here (once per block) rather than per packet.
        System.Diagnostics.Debug.Assert(
            _FieldBagsEstimatedSize == _SumFieldBagSizes(),
            "Running field-bag size total diverged from the sum of the bags.");

        _PacketIds.Clear();
        _Timestamps.Clear();
        _Infos.Clear();
        _FrameBytesList.Clear();
        _Topology.Clear();
        _BatchEstimatedSize = 0;
        _FieldBagsEstimatedSize = 0;

        foreach (FieldColumnBag bag in _FieldBags.Values)
        {
            bag.Reset();
        }
    }

    /// <summary>Clears all state, including field metadata. The batch must not be used after disposal.</summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }
        _Disposed = true;

        _PacketIds.Clear();
        _Timestamps.Clear();
        _Infos.Clear();
        _FrameBytesList.Clear();
        _Topology.Clear();
        _FieldBags.Clear();
        _Catalog.Clear();
        _BatchEstimatedSize = 0;
        _FieldBagsEstimatedSize = 0;
    }

    #endregion

    #region Private Helpers

    private void _EncodeField(Packet packet, Field field, int parentNodeId, ref int nodeCounter)
    {
        int nodeId = nodeCounter++;
        int packetIdValue = packet.Id.Value;

        if ((Flags & ColumnarDetailFlags.IncludeTopology) != 0)
        {
            _Topology.Add(new(packetIdValue, nodeId, field.FieldId.Value, parentNodeId));
        }

        if (FieldValueMaterializer.HasValueColumn(field))
        {
            FieldValue fieldValue = field.Value;
            FieldValueData data = fieldValue.Data;
            FieldColumnBag bag = _GetOrCreateBag(field, packet.Stack, data.Type);

            // Account only the bytes this row appends, so the batch total stays O(1) to read. A type mismatch
            // throws before the bag mutates, so the delta is never applied for a rejected row.
            long bagSizeBefore = bag.EstimatedSizeBytes;
            bag.Add(
                packetIdValue,
                nodeId,
                in data,
                FieldValueMaterializer.GetCustomRepresentation(fieldValue, Flags),
                FieldValueMaterializer.GetCustomText(field, Flags));
            _FieldBagsEstimatedSize += bag.EstimatedSizeBytes - bagSizeBefore;
        }

        if (field.HasChildren(materialize: true))
        {
            foreach (Field child in field.Children(materialize: true))
            {
                _EncodeField(packet, child, nodeId, ref nodeCounter);
            }
        }
    }

    /// <summary>Sums every bag's estimate; the reference form of <see cref="_FieldBagsEstimatedSize"/> for the debug check.</summary>
    /// <returns>Sum of <see cref="FieldColumnBag.EstimatedSizeBytes"/> over all bags.</returns>
    private long _SumFieldBagSizes()
    {
        long total = 0;
        foreach (FieldColumnBag bag in _FieldBags.Values)
        {
            total += bag.EstimatedSizeBytes;
        }
        return total;
    }

    private FieldColumnBag _GetOrCreateBag(Field field, Stack stack, FieldType extractedType)
    {
        int fieldIdValue = field.FieldId.Value;
        if (_FieldBags.TryGetValue(fieldIdValue, out FieldColumnBag? bag))
        {
            return bag;
        }

        FieldInfo? info = stack.GetField(field.FieldId);
        FieldType fieldType = info is { FieldType: not FieldType.None } ? info.FieldType : extractedType;

        bag = new(fieldIdValue, fieldType);
        _FieldBags[fieldIdValue] = bag;

        string name = info?.Name ?? fieldIdValue.ToString(CultureInfo.InvariantCulture);
        string uiName = info?.UiName ?? name;
        int protocolIdValue = info?.ProtocolId.Value ?? -1;
        _Catalog[fieldIdValue] = new(
            fieldIdValue, name, uiName, fieldType, protocolIdValue, $"field_{fieldIdValue}", info?.Description);

        return bag;
    }

    #endregion
}
