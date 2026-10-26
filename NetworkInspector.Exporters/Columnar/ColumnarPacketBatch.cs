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
/// not need to be re-derived for every block within one export run. The row lists keep their capacity across blocks, so
/// steady-state blocks do not regrow them.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. One writer thread calls <see cref="AddPacket"/>, <see cref="Reset"/> and
/// <see cref="Dispose"/>; a sink reads the batch only inside <see cref="IColumnarBatchSink.WriteBatch"/>, which the same
/// thread calls between <see cref="AddPacket"/> and <see cref="Reset"/>.
/// </para>
/// </summary>
internal sealed class ColumnarPacketBatch : IDisposable
{
    #region Fields

    /// <summary>Optional data that is captured per packet and field.</summary>
    internal ColumnarDetailFlags Flags { get; }

    /// <summary>Packet-count flush threshold reported by <see cref="AddPacket"/>.</summary>
    private int _MaxPacketsPerBlock { get; }

    /// <summary>Estimated-size flush threshold (bytes) reported by <see cref="AddPacket"/>; bounds the memory of one block.</summary>
    private long _MaxBlockSize { get; }

    /// <summary>Whether packets are added in non-decreasing timestamp order; sinks may use it to declare a sorted column.</summary>
    internal bool IsTimestampSorted { get; }

    // Initial capacities (256 packets, 8 topology nodes per packet, 64 distinct fields) are sizing hints that avoid the
    // first few List/Dictionary regrowths; they are not limits.
    private readonly List<int> _PacketIds = new(256);
    private readonly List<long> _Timestamps = new(256);
    private readonly List<string> _Infos = new(256);
    private readonly List<byte[]> _FrameBytesList = new(256);
    private readonly List<TopologyNode> _Topology = new(256 * 8);

    /// <summary>
    /// Undo log of the packet being added: one entry per field row, holding the bag's state before the row. Cleared at the
    /// start of every <see cref="AddPacket"/>; the list is reused, so adding a packet allocates nothing for it once warm.
    /// </summary>
    private readonly List<RowMark> _Journal = new(64);

    /// <summary>Column bags by field id. Created on first sight of a field and kept across <see cref="Reset"/>.</summary>
    private readonly Dictionary<int, FieldColumnBag> _FieldBags = new(64);

    /// <summary>Catalog rows by field id, created together with the bag in <see cref="_GetOrCreateBag"/>.</summary>
    private readonly Dictionary<int, FieldCatalogEntry> _Catalog = new(64);

    /// <summary>Estimated bytes of the packet-level lists (ids, timestamps, infos, frame bytes).</summary>
    private long _BatchEstimatedSize { get; set; }

    /// <summary>
    /// Running total of the bytes appended to all <see cref="FieldColumnBag"/>s since the last <see cref="Reset"/>.
    /// Kept incrementally so the flush check in <see cref="AddPacket"/> is O(1) instead of O(distinct fields).
    /// </summary>
    private long _FieldBagsEstimatedSize { get; set; }

    private bool _Disposed;

    /// <summary>State of one column bag before a row was added to it; the undo record of <see cref="_Journal"/>.</summary>
    /// <param name="Bag">The bag the row went into.</param>
    /// <param name="RowCount">Row count of the bag before the row.</param>
    /// <param name="EstimatedSizeBytes">Size estimate of the bag before the row.</param>
    /// <param name="Created">Whether the bag was created for this row (then it is removed again on rollback).</param>
    private readonly record struct RowMark(FieldColumnBag Bag, int RowCount, long EstimatedSizeBytes, bool Created);

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
    /// <exception cref="InvalidOperationException">
    /// The packet has no field tree (a skip packet), or a field value does not match the column type of its bag. The
    /// batch is unchanged in both cases (the packet is added completely or not at all), so the caller can skip the
    /// packet and keep using the batch.
    /// </exception>
    internal bool AddPacket(Packet packet)
    {
        // A skip packet has no parsed field tree, so there is nothing to put into the columns: fail loudly instead of
        // writing a packet row without values.
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
        }

        // All-or-nothing: a packet that fails part-way (a value that does not fit its column, or a deferred text that
        // throws) must not leave a packet row without all its field rows. The row counts of the packet-level lists and
        // the running size totals are remembered here; every field row is journaled with the state of its bag before the
        // row is added. On failure the journal and these marks undo everything, so the caller can skip the packet and
        // continue with a consistent batch (the rows of a half-added packet would otherwise be written to the output).
        int packetCountBefore = _PacketIds.Count;
        int infoCountBefore = _Infos.Count;
        int frameCountBefore = _FrameBytesList.Count;
        int topologyCountBefore = _Topology.Count;
        long batchSizeBefore = _BatchEstimatedSize;
        long fieldBagsSizeBefore = _FieldBagsEstimatedSize;
        _Journal.Clear();

        try
        {
            // Packet-level columns. The size estimate is a running total (O(1)) so the flush check below never walks the data.
            int packetIdValue = packet.Id.Value;
            _PacketIds.Add(packetIdValue);
            _Timestamps.Add(packet.Timestamp.AsNanos);
            _BatchEstimatedSize += 12; // int PacketId (4) + long Timestamp (8)

            // Optional columns: each is read only when its flag is set, so a disabled column costs neither time nor memory.
            if ((Flags & ColumnarDetailFlags.IncludeInfo) != 0)
            {
                string info = packet.Info;
                _Infos.Add(info);
                _BatchEstimatedSize += info.Length * 2; // UTF-16: two bytes per char
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

            // Field tree: the root is an invisible container, so its children are the top-level nodes (parent id -1). The tree
            // is expanded (materialize: true) because lazily expanded children would otherwise be missing from the export.
            // nodeCounter numbers the nodes in depth-first pre-order, restarting at 0 for every packet, so a parent's node id
            // is always smaller than its children's ids.
            Field root = packet.RootField();
            if (root.HasChildren(materialize: true))
            {
                int nodeCounter = 0;
                foreach (Field child in root.Children(materialize: true))
                {
                    _EncodeField(packet, child, parentNodeId: -1, ref nodeCounter);
                }
            }
        }
        catch
        {
            _RollBack(
                packetCountBefore, infoCountBefore, frameCountBefore, topologyCountBefore, batchSizeBefore, fieldBagsSizeBefore);
            throw;
        }

        // Flush when either bound is hit. The size bound is an estimate (see FieldColumnBag), so a block may exceed it
        // slightly; the packet that crosses the bound stays in this block (a packet is never split across blocks).
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
        _Journal.Clear();
        _Catalog.Clear();
        _BatchEstimatedSize = 0;
        _FieldBagsEstimatedSize = 0;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Adds one field (topology row and, when it carries a value, a column row) and then its children recursively.
    /// Recursion depth equals the nesting depth of the packet's field tree, which is bounded by the protocol nesting.
    /// </summary>
    /// <param name="packet">Owning packet.</param>
    /// <param name="field">Field to encode.</param>
    /// <param name="parentNodeId">Node id of the parent, or -1 for top-level fields.</param>
    /// <param name="nodeCounter">Next free node id of this packet; incremented once per field, with or without a value.</param>
    private void _EncodeField(Packet packet, Field field, int parentNodeId, ref int nodeCounter)
    {
        // The node id is taken before the children are visited (pre-order), and it is consumed even when the field has no
        // value column or the topology is disabled, so ids stay identical regardless of the detail flags.
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
            FieldColumnBag bag = _GetOrCreateBag(field, packet.Stack, data.Type, out bool bagCreated);

            // Account only the bytes this row appends, so the batch total stays O(1) to read. A type mismatch
            // throws before the bag mutates, so the delta is never applied for a rejected row.
            // The journal entry (reused list, no per-packet allocation) is what AddPacket uses to undo this row if a later
            // field of the same packet fails.
            long bagSizeBefore = bag.EstimatedSizeBytes;
            _Journal.Add(new RowMark(bag, bag.RowCount, bagSizeBefore, bagCreated));
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

    /// <summary>
    /// Undoes a packet that failed part-way (see <see cref="AddPacket"/>): the field rows go first, newest to oldest, so
    /// the earliest mark of a bag is applied last, and a bag created for this packet is dropped again together with its
    /// catalog row. Then the packet-level lists and the size totals return to their remembered values.
    /// </summary>
    private void _RollBack(
        int packetCount, int infoCount, int frameCount, int topologyCount, long batchSize, long fieldBagsSize)
    {
        for (int index = _Journal.Count - 1; index >= 0; index--)
        {
            RowMark mark = _Journal[index];
            mark.Bag.TruncateTo(mark.RowCount, mark.EstimatedSizeBytes);
            if (mark.Created)
            {
                _FieldBags.Remove(mark.Bag.FieldIdValue);
                _Catalog.Remove(mark.Bag.FieldIdValue);
            }
        }

        _Journal.Clear();
        _TrimTo(_PacketIds, packetCount);
        _TrimTo(_Timestamps, packetCount);
        _TrimTo(_Infos, infoCount);
        _TrimTo(_FrameBytesList, frameCount);
        _TrimTo(_Topology, topologyCount);
        _BatchEstimatedSize = batchSize;
        _FieldBagsEstimatedSize = fieldBagsSize;
    }

    /// <summary>Shortens <paramref name="list"/> to at most <paramref name="count"/> elements.</summary>
    private static void _TrimTo<T>(List<T> list, int count)
    {
        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
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

    /// <summary>
    /// Returns the column bag of a field, creating the bag and its catalog row on first sight of the field.
    /// </summary>
    /// <param name="field">The field being added.</param>
    /// <param name="stack">Protocol stack that holds the field registration.</param>
    /// <param name="extractedType">Type of the value that triggered the lookup; used when the field is not registered.</param>
    /// <param name="created"><see langword="true"/> when this call created the bag (and its catalog row).</param>
    /// <returns>The bag for the field id.</returns>
    private FieldColumnBag _GetOrCreateBag(Field field, Stack stack, FieldType extractedType, out bool created)
    {
        // Fast path: one dictionary lookup per value row.
        int fieldIdValue = field.FieldId.Value;
        if (_FieldBags.TryGetValue(fieldIdValue, out FieldColumnBag? bag))
        {
            created = false;
            return bag;
        }

        created = true;

        // Slow path, once per field and export: the declared (registered) type fixes the column type for the whole
        // export, so every row of a column has one type. Only a field that is unregistered or declares no type (None) falls
        // back to the type of the first value seen; later rows of a different type are rejected by FieldColumnBag.Add
        // instead of changing the column.
        FieldInfo? info = stack.GetField(field.FieldId);
        FieldType fieldType = info is { FieldType: not FieldType.None } ? info.FieldType : extractedType;

        bag = new(fieldIdValue, fieldType);
        _FieldBags[fieldIdValue] = bag;

        // Unregistered fields still get a catalog row, named by their numeric id, so every column in the output can be
        // looked up in the catalog. The table name is derived from the id only (field_{id}).
        string name = info?.Name ?? fieldIdValue.ToString(CultureInfo.InvariantCulture);
        string uiName = info?.UiName ?? name;
        int protocolIdValue = info?.ProtocolId.Value ?? -1;
        _Catalog[fieldIdValue] = new(
            fieldIdValue, name, uiName, fieldType, protocolIdValue, $"field_{fieldIdValue}", info?.Description);

        return bag;
    }

    #endregion
}
