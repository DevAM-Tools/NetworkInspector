// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Column-oriented PBF block builder. Wraps the shared
/// <see cref="ColumnarPacketBatch"/> (also used by the Parquet and DuckDB exporters) and
/// serializes it to the PBF columnar wire format (<see cref="PbfFieldNumbers.BlockTypeColumnar"/>).
/// <para>
/// Each field becomes a separate <c>FieldColumn</c> sub-message (see
/// <see cref="PbfFieldNumbers.ColumnFieldId"/> and neighbouring constants). The string-value column is written as plain
/// repeated strings; the custom-representation and custom-text columns are presence-encoded (a present text, also an empty one, or
/// a run of absent rows) and left out when no row has a text; the address types are integers in packed fixed-width columns
/// (<see cref="PbfFieldNumbers.ColumnFixed32Values"/>).
/// </para>
/// <para>
/// <b>Two-phase design:</b> <see cref="AddPacket"/> only appends the packet to the shared batch (cheap, no encoding);
/// the protobuf encoding of the whole block happens once in <see cref="Build"/>. Collecting first is what allows the
/// delta encoding, the per-field grouping and the min/max header values, none of which are known per packet.
/// </para>
/// <para>
/// <b>Wire layout of a block:</b> envelope (block type <see cref="PbfFieldNumbers.BlockTypeColumnar"/> plus a
/// length-delimited payload). The payload holds the five header fields shared with the standard block, then the
/// packet ID and timestamp columns, the optional info, frame-bytes and topology columns, and one
/// <c>FieldColumn</c> sub-message per field ID that has rows in this block.
/// </para>
/// <para>
/// <b>Allocation strategy:</b> the builder owns four <see cref="PooledBuffer"/> objects for its whole life (payload, envelope, one
/// field column, one sub-column), created in the constructor on the caller's thread (exporter guide §11.7) and returned in
/// <see cref="Dispose"/>. Every <see cref="Build"/> resets and refills them, so a block allocates no buffer object and, once the
/// buffers reached the size of the largest block, no array. The column writers read the batch through spans, never through
/// the list interfaces.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one thread at a time. The caller's thread fills the block; in
/// <see cref="ExportWriteMode.Background"/> the writer thread then calls <see cref="Build"/> and <see cref="Reset"/> and hands the
/// block back. The hand-off of <see cref="BatchWriterPipeline{TBatch}"/> is the memory barrier between the two (see
/// <see cref="IExportBatch"/>); no member may keep state that belongs to one specific thread.
/// The span returned by <see cref="Build"/> aliases a pooled buffer and must be consumed before the next call to
/// <see cref="Build"/>, <see cref="Reset"/> or <see cref="Dispose"/>.
/// </para>
/// </summary>
internal sealed class ColumnarBlockBuilder : IDisposable
{
    #region Configuration and state

    /// <summary>The shared batch that collects the packets of the open block and is encoded by <see cref="Build"/>.</summary>
    private readonly ColumnarPacketBatch _Batch;

    /// <summary>Field ids that have rows in the built block; merged into the trailer's global bitmap by the block writer.</summary>
    private readonly FieldPresence _FieldPresence;

    /// <summary>
    /// Assembles the block payload (header, packet-level columns, field columns) before the envelope wraps it, because the envelope
    /// needs the payload length first. Reset at the start of every <see cref="Build"/>.
    /// </summary>
    private readonly PooledBuffer _Payload = new(64 * 1024);

    /// <summary>
    /// Owns the bytes <see cref="Build"/> returns (block type plus the length-delimited payload). Reset at the start of every
    /// <see cref="Build"/>, so the returned span stays valid until the next <see cref="Build"/> or <see cref="Dispose"/>.
    /// </summary>
    private readonly PooledBuffer _Envelope = new(64 * 1024);

    /// <summary>
    /// Encodes one length-delimited message whose length prefix must precede its content: a <c>FieldColumn</c> in
    /// <see cref="_WriteFieldColumns"/>, the topology message in <see cref="_WriteTopology"/>. Reset before each use; never used by
    /// two messages at once (a field column is copied into <see cref="_Payload"/> before the next one starts).
    /// </summary>
    private readonly PooledBuffer _ColumnScratch = new(256);

    /// <summary>
    /// Encodes one sub-column (a delta or string column) inside a message or the payload. Reset at the entry of each column writer;
    /// it is the innermost buffer, so no writer that uses it calls another one that does.
    /// </summary>
    private readonly PooledBuffer _SubColumnScratch = new(1024);

    /// <summary>Creates a new columnar block builder.</summary>
    /// <param name="maxFieldId">
    /// Exclusive upper bound of the field IDs tracked in the presence bitmap. IDs at or above it are not rejected
    /// (unlike the standard block builder); they are written normally but are missing from the presence bitmap.
    /// </param>
    /// <param name="content">Validated export content; decides the shape, the optional columns, and the topology (hierarchy only).</param>
    /// <param name="maxPacketsPerBlock">Maximum packets before flush.</param>
    /// <param name="maxBlockSize">Maximum estimated block size before flush.</param>
    /// <param name="isTimestampSorted">Whether the caller guarantees non-decreasing packet timestamps.</param>
    internal ColumnarBlockBuilder(
        int maxFieldId,
        ExportContent content,
        int maxPacketsPerBlock = BatchLimits.DefaultPacketsPerBatch,
        long maxBlockSize = BatchLimits.DefaultBytesPerBatch,
        bool isTimestampSorted = false)
    {
        _Batch = new ColumnarPacketBatch(content, maxPacketsPerBlock, maxBlockSize, isTimestampSorted);
        _FieldPresence = new FieldPresence(maxFieldId);
    }

    /// <summary>Number of packets accumulated in the current block.</summary>
    internal int PacketCount => _Batch.PacketCount;

    /// <summary>
    /// The batch the block is built from. The flat shapes stage fields straight into it while the packet is parsed (it is the
    /// <see cref="IFieldStage"/> of the exporter); the hierarchy shape adds finished packets through <see cref="AddPacket"/>.
    /// </summary>
    internal ColumnarPacketBatch Batch => _Batch;

    /// <summary>
    /// In-memory estimate of pending columnar payload size (same basis as flush thresholds).
    /// Used for live <see cref="IExportByteProgress"/> without filesystem probes.
    /// </summary>
    internal long EstimatedPendingBytes => _Batch.EstimatedSizeBytes;

    /// <summary>
    /// Minimum timestamp (nanoseconds) in the most recently built block.
    /// Only valid after <see cref="Build"/> is called and before <see cref="Reset"/> is called.
    /// </summary>
    internal long MinTimestamp
    {
        get; private set;
    }

    /// <summary>
    /// Maximum timestamp (nanoseconds) in the most recently built block.
    /// Only valid after <see cref="Build"/> is called and before <see cref="Reset"/> is called.
    /// </summary>
    internal long MaxTimestamp
    {
        get; private set;
    }

    /// <summary>Field presence bitmap of the built block, for trailer aggregation; valid after <see cref="Build"/>.</summary>
    internal FieldPresence FieldPresence => _FieldPresence;

    #endregion

    #region Packet intake

    /// <summary>
    /// Adds a packet to the columnar block. Returns <c>true</c> if the block
    /// should be flushed after this packet (packet count or estimated size threshold reached).
    /// The size threshold is the batch's in-memory estimate, not the encoded size.
    /// </summary>
    /// <param name="packet">A packet parsed with a field tree.</param>
    /// <returns><see langword="true"/> when the caller should call <see cref="Build"/> and flush the block.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="packet"/> has no field tree, or a field's value type
    /// does not match its declared type.</exception>
    internal bool AddPacket(Packet packet) => _Batch.AddPacket(packet);

    #endregion

    #region Block assembly

    /// <summary>
    /// Builds the columnar block as protobuf-encoded bytes (PBF columnar wire format).
    /// Returns a span that is valid until <see cref="Reset"/> or the next <see cref="Build"/> call.
    /// </summary>
    /// <returns>The serialized block, ready to be compressed and framed by the exporter.</returns>
    /// <exception cref="InvalidOperationException">
    /// A field column has a type the format cannot store, or a buffer would exceed <see cref="Array.MaxLength"/> bytes.
    /// </exception>
    /// <remarks>Cost: one pass over every column of the batch; allocation-free once the four buffers have reached the block's size.</remarks>
    internal ReadOnlySpan<byte> Build()
    {
        // The header values (min/max timestamp and packet ID) and the presence bitmap are computed from the whole
        // batch here, once per block, instead of being tracked on every AddPacket call.
        _ComputeTimestampRange();
        _MarkFieldPresence();

        // The payload is assembled first because the envelope needs its final length; both buffers are reused from block to block.
        _Payload.Reset();
        PooledBuffer payload = _Payload;

        // Block header: the same five fields as the standard block (shared field numbers).
        int packetCount = PacketCount;
        ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockPacketCount, (ulong)packetCount);

        if (packetCount > 0)
        {
            // Packet IDs are not guaranteed to be sorted, so a scan is needed; packet IDs are non-negative, so the
            // plain varint cast keeps them short.
            ReadOnlySpan<int> packetIds = _Batch.PacketIdSpan;
            int minId = packetIds[0];
            int maxId = packetIds[0];
            for (int i = 1; i < packetIds.Length; i++)
            {
                if (packetIds[i] < minId)
                {
                    minId = packetIds[i];
                }

                if (packetIds[i] > maxId)
                {
                    maxId = packetIds[i];
                }
            }

            ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockMinPacketId, (ulong)minId);
            ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockMaxPacketId, (ulong)maxId);
            ProtobufEncoder.WriteSint64(ref payload, PbfFieldNumbers.BlockMinTimestamp, MinTimestamp);
            ProtobufEncoder.WriteSint64(ref payload, PbfFieldNumbers.BlockMaxTimestamp, MaxTimestamp);
        }

        // Packet-level columns: IDs and timestamps are delta encoded (small differences for consecutive packets),
        // the optional columns follow only when their content member is set, mirroring what the batch collected.
        _WriteDeltaSint64Column(ref payload, PbfFieldNumbers.ColumnarPacketIds, _Batch.PacketIdSpan);
        _WriteDeltaSint64Column(ref payload, PbfFieldNumbers.ColumnarTimestamps, _Batch.TimestampSpan);

        if (_Batch.Content.Info)
        {
            _WriteStringColumn(ref payload, PbfFieldNumbers.ColumnarInfos, _Batch.InfoSpan);
        }

        if (_Batch.Content.FrameBytes)
        {
            _WriteFrameBytesColumn(ref payload, PbfFieldNumbers.ColumnarFrameBytes);
        }

        if (_Batch.Content.Buffers)
        {
            _WriteBuffers(ref payload);
        }

        if (_Batch.Content.Shape == ExportShape.Hierarchy)
        {
            _WriteTopology(ref payload);
        }

        _WriteFieldColumns(ref payload);

        // Wrap in the block envelope: block type plus the length-delimited payload.
        _Envelope.Reset();
        PooledBuffer envelope = _Envelope;
        ProtobufEncoder.WriteVarintField(ref envelope, PbfFieldNumbers.BlockType, PbfFieldNumbers.BlockTypeColumnar);
        ProtobufEncoder.WriteLengthDelimited(ref envelope, PbfFieldNumbers.BlockPayload, payload.WrittenSpan);
        return envelope.WrittenSpan;
    }

    /// <summary>Resets all state for reuse by the next block. Field catalog / bag metadata is retained; the buffers are kept.</summary>
    internal void Reset()
    {
        _Batch.Reset();
        _FieldPresence.Clear();
        MinTimestamp = 0;
        MaxTimestamp = 0;
    }

    /// <summary>Returns the four pooled buffers and releases the underlying batch; must run after the writer thread ended.</summary>
    public void Dispose()
    {
        _Payload.Return();
        _Envelope.Return();
        _ColumnScratch.Return();
        _SubColumnScratch.Return();
        _Batch.Dispose();
    }

    /// <summary>Computes <see cref="MinTimestamp"/>/<see cref="MaxTimestamp"/> from the current batch.</summary>
    private void _ComputeTimestampRange()
    {
        ReadOnlySpan<long> timestamps = _Batch.TimestampSpan;
        if (timestamps.Length == 0)
        {
            MinTimestamp = 0;
            MaxTimestamp = 0;
            return;
        }

        // Full scan: the sorted-timestamps declaration is metadata only (see the builder option) and does not
        // shortcut this to first/last, so the min/max written to the block header and trailer index are always exact.
        long minTs = timestamps[0];
        long maxTs = timestamps[0];
        for (int i = 1; i < timestamps.Length; i++)
        {
            if (timestamps[i] < minTs)
            {
                minTs = timestamps[i];
            }

            if (timestamps[i] > maxTs)
            {
                maxTs = timestamps[i];
            }
        }

        MinTimestamp = minTs;
        MaxTimestamp = maxTs;
    }

    /// <summary>
    /// Marks every field ID observed in this block as present, so <see cref="PbfExporter"/> can
    /// merge it into the trailer's global field bitmap. Presence is taken from both topology
    /// (covers pure <see cref="FieldType.None"/> containers) and field-column bags (covers value
    /// columns even in the flat shapes, which have no topology).
    /// </summary>
    private void _MarkFieldPresence()
    {
        // The return value of Mark (first occurrence) is not needed: columnar blocks carry no per-block metadata
        // deduplication, the bitmap is only aggregated into the trailer. Field IDs beyond the bitmap range are
        // ignored by Mark and therefore missing from the trailer bitmap (no exception, no data loss in the block).
        foreach (ref readonly TopologyNode node in _Batch.TopologySpan)
        {
            if (node.FieldId >= 0)
            {
                _FieldPresence.Mark(node.FieldId);
            }
        }

        foreach (int fieldIdValue in _Batch.FieldBags.Keys)
        {
            if (fieldIdValue >= 0)
            {
                _FieldPresence.Mark(fieldIdValue);
            }
        }
    }

    #endregion

    #region Packet-level columns

    /// <summary>
    /// Writes a delta-encoded sint64 column. The first element is the base value (sub-field 1);
    /// subsequent elements are deltas from their predecessor (sub-field 2). Encoding deltas
    /// instead of absolute values produces smaller varints for monotonic sequences such as
    /// packet IDs and timestamps.
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    /// <param name="values">The column values in row order; nothing is written for an empty column.</param>
    private void _WriteDeltaSint64Column(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<long> values)
    {
        if (values.Length == 0)
        {
            return;
        }

        // The column is encoded into the sub-column scratch first because the length prefix of the enclosing field must precede
        // the content.
        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);

        // Differences are computed in unchecked 64-bit arithmetic: an extreme jump wraps instead of throwing, and
        // a reader that accumulates with the same wrap-around reconstructs the exact values. The zigzag encoding
        // keeps negative deltas (out-of-order input) short as well.
        for (int i = 1; i < values.Length; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, values[i] - values[i - 1]);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    /// <summary>
    /// Writes a delta-encoded sint64 column from <see cref="int"/> values (widened at the wire;
    /// used for packet IDs which share the Core <see cref="PacketId"/> <see cref="int"/> range).
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    /// <param name="values">The column values in row order; nothing is written for an empty column.</param>
    private void _WriteDeltaSint64Column(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<int> values)
    {
        if (values.Length == 0)
        {
            return;
        }

        // Same layout as the long overload. The subtraction is widened to long first, so two ints can never overflow.
        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, (long)values[i] - values[i - 1]);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    /// <summary>
    /// Writes a repeated string column (one tag+value per row, in row order). Unlike the delta column writer and the
    /// nullable variant it also writes an empty sub-message when there are no rows.
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    /// <param name="values">One text per row.</param>
    private void _WriteStringColumn(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<string> values)
    {
        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        foreach (string value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    /// <summary>
    /// Writes the frame bytes straight from the batch arena: no <c>byte[]</c> per frame (the arena already holds the copy of each
    /// frame, and the encoder's copy into the block buffer is the one copy the block needs). Perf: reading the batch's array list
    /// instead would build one array per frame per flush, garbage of the block's size.
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    private void _WriteFrameBytesColumn(ref PooledBuffer buffer, int fieldNumber)
    {
        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        int count = _Batch.PacketCount;
        for (int i = 0; i < count; i++)
        {
            ProtobufEncoder.WriteLengthDelimited(ref col, 1, _Batch.GetFrameBytes(i));
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    /// <summary>
    /// Writes the additional packet buffers of the batch as one <see cref="PbfFieldNumbers.ColumnarBuffers"/> sub-message with
    /// three parallel columns: the owning packet ids (delta encoded), the buffer indexes, and the bytes. The field is omitted when
    /// the block has no buffer row, so an export whose packets carry no additional buffers costs nothing.
    /// </summary>
    /// <param name="buffer">The payload the buffers message is appended to.</param>
    /// <remarks>
    /// Perf: the bytes come from the batch arena as spans, so no <c>byte[]</c> is created per buffer; the encoder's copies into
    /// the message scratch and then into the block are the two copies the length prefixes need (the same as the topology message).
    /// </remarks>
    private void _WriteBuffers(ref PooledBuffer buffer)
    {
        int rows = _Batch.BufferRowCount;
        if (rows == 0)
        {
            return;
        }

        PooledBuffer message = _ColumnScratch;
        message.Reset();

        // Packet ids: base value then differences, like the packet id column; rows of one packet repeat the id, so most
        // differences are 0 and cost one byte. The sub-column scratch holds the delta column until it is copied into the message.
        ReadOnlySpan<int> packetIds = _Batch.BufferPacketIdSpan;
        PooledBuffer packetIdColumn = _SubColumnScratch;
        packetIdColumn.Reset();
        ProtobufEncoder.WriteSint64(ref packetIdColumn, 1, packetIds[0]);
        for (int i = 1; i < packetIds.Length; i++)
        {
            ProtobufEncoder.WriteSint64(ref packetIdColumn, 2, (long)packetIds[i] - packetIds[i - 1]);
        }

        ProtobufEncoder.WriteLengthDelimited(ref message, PbfFieldNumbers.BuffersPacketIds, packetIdColumn.WrittenSpan);

        // Indexes: one varint per row (1 to 255, so one or two bytes), one tag per row like the topology columns.
        foreach (byte index in _Batch.BufferIndexSpan)
        {
            ProtobufEncoder.WriteVarintField(ref message, PbfFieldNumbers.BuffersIndexes, index);
        }

        // Bytes: one length-delimited entry per row, straight from the arena; an empty buffer is a zero-length entry, so the
        // column always has exactly one entry per row.
        for (int i = 0; i < rows; i++)
        {
            ProtobufEncoder.WriteLengthDelimited(ref message, PbfFieldNumbers.BuffersBytes, _Batch.GetBufferBytes(i));
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.ColumnarBuffers, message.WrittenSpan);
    }

    /// <summary>
    /// Writes the <see cref="ColumnarPacketBatch.Topology"/> rows as four parallel columns and, with custom texts in the content, the
    /// presence-encoded text column of the nodes without a value.
    /// </summary>
    /// <param name="buffer">The payload the topology message is appended to.</param>
    private void _WriteTopology(ref PooledBuffer buffer)
    {
        ReadOnlySpan<TopologyNode> topology = _Batch.TopologySpan;
        if (topology.Length == 0)
        {
            return;
        }

        // The four columns are written as parallel repeated fields of one sub-message, so row i is the i-th element
        // of each. Node and field IDs are never negative (plain varint); the parent node ID is -1 for top-level
        // fields, hence sint64 (zigzag) so that -1 costs one byte instead of ten.
        PooledBuffer topo = _ColumnScratch;
        topo.Reset();

        // Packet IDs: delta-encoded (topology rows are grouped by ascending packet insertion order). The sub-column scratch holds
        // the delta column only until it is copied into the topology message.
        PooledBuffer packetIdCol = _SubColumnScratch;
        packetIdCol.Reset();
        ProtobufEncoder.WriteSint64(ref packetIdCol, 1, topology[0].PacketId);
        for (int i = 1; i < topology.Length; i++)
        {
            ProtobufEncoder.WriteSint64(ref packetIdCol, 2, (long)topology[i].PacketId - topology[i - 1].PacketId);
        }

        ProtobufEncoder.WriteLengthDelimited(ref topo, PbfFieldNumbers.TopologyPacketIds, packetIdCol.WrittenSpan);

        foreach (ref readonly TopologyNode node in topology)
        {
            ProtobufEncoder.WriteVarintField(ref topo, PbfFieldNumbers.TopologyNodeIds, (ulong)node.NodeId);
        }

        foreach (ref readonly TopologyNode node in topology)
        {
            ProtobufEncoder.WriteVarintField(ref topo, PbfFieldNumbers.TopologyFieldIds, (ulong)node.FieldId);
        }

        foreach (ref readonly TopologyNode node in topology)
        {
            ProtobufEncoder.WriteSint64(ref topo, PbfFieldNumbers.TopologyParentNodeIds, node.ParentNodeId);
        }

        // The display line of a node without a value (a protocol layer, a group) is a presence-encoded text column like the field texts:
        // a value node and a node whose dissector set no text are absent rows, so a reader can rebuild the same line as the Text export.
        if (_Batch.Content.CustomText)
        {
            _WriteTopologyTexts(ref topo, topology);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.ColumnarTopology, topo.WrittenSpan);
    }

    /// <summary>
    /// Writes the custom texts of the topology rows as one presence-encoded column (<see cref="WriteTextColumn"/>) into the topology
    /// message; nothing when no row has a text.
    /// </summary>
    /// <param name="topo">The topology message being built.</param>
    /// <param name="topology">The rows of the block, in row order.</param>
    /// <remarks>
    /// Perf: <see cref="WriteTextColumn"/> takes a span of strings, so the texts are copied into a pooled array (a reference copy per row,
    /// no string allocation) that is cleared on return so the pool keeps no reference to a text.
    /// </remarks>
    private void _WriteTopologyTexts(ref PooledBuffer topo, ReadOnlySpan<TopologyNode> topology)
    {
        string?[] texts = ArrayPool<string?>.Shared.Rent(topology.Length);
        try
        {
            for (int i = 0; i < topology.Length; i++)
            {
                texts[i] = topology[i].CustomText;
            }

            WriteTextColumn(ref topo, PbfFieldNumbers.TopologyCustomTexts, texts.AsSpan(0, topology.Length));
        }
        finally
        {
            ArrayPool<string?>.Shared.Return(texts, clearArray: true);
        }
    }

    #endregion

    #region Field columns

    /// <summary>Writes every field's <see cref="FieldColumnBag"/> as a <c>FieldColumn</c> sub-message.</summary>
    /// <param name="buffer">The payload the field columns are appended to.</param>
    private void _WriteFieldColumns(ref PooledBuffer buffer)
    {
        foreach (KeyValuePair<int, FieldColumnBag> entry in _Batch.FieldBags)
        {
            FieldColumnBag bag = entry.Value;
            if (bag.RowCount == 0)
            {
                continue;
            }

            // One FieldColumn sub-message per field ID, built in the column scratch (length prefix first) and copied into the
            // payload right after. Bags that exist but received no rows in this block (the batch keeps bags across blocks) are
            // skipped. Perf: one reused buffer for all columns of all blocks, not one buffer object per column.
            PooledBuffer colBuf = _ColumnScratch;
            colBuf.Reset();
            ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnFieldId, (ulong)bag.FieldIdValue);
            ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnFieldType, (ulong)bag.FieldType);

            _WriteDeltaSint64Column(ref colBuf, PbfFieldNumbers.ColumnPacketIds, bag.PacketIdSpan);
            foreach (int position in bag.PositionSpan)
            {
                ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnNodeIds, (ulong)position);
            }

            _WriteTypedValues(ref colBuf, bag);

            // The two text columns carry presence (absent rows are runs, an empty text is an entry), so a reader can rebuild the
            // display line only from texts a dissector set (EXPORTER_GUIDE § Rebuilding the display line). The string value column
            // below keeps its own encoding: a string value is data, not an optional text.
            if (_Batch.Content.CustomRepresentation)
            {
                WriteTextColumn(ref colBuf, PbfFieldNumbers.ColumnCustomRepresentations, bag.CustomRepresentationSpan);
            }

            if (_Batch.Content.CustomText)
            {
                WriteTextColumn(ref colBuf, PbfFieldNumbers.ColumnCustomTexts, bag.CustomTextSpan);
            }

            ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.ColumnarFieldColumns, colBuf.WrittenSpan);
        }
    }

    /// <summary>
    /// Writes the type-specific value column matching <see cref="FieldColumnBag.FieldType"/>. Numeric values are
    /// written as one repeated field entry per row directly in the column message (signed and timestamp values
    /// zigzag encoded, doubles as fixed64); string columns are wrapped in a sub-message; a bytes column repeats one
    /// length-delimited entry per row; the address types are integers in packed fixed-width columns (see the region
    /// <c>Address columns</c>).
    /// </summary>
    /// <param name="colBuf">The field column message the values are appended to.</param>
    /// <param name="bag">The bag whose values are written.</param>
    /// <exception cref="InvalidOperationException">The bag's field type has no column in the format.</exception>
    private void _WriteTypedValues(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        // Perf: spans, not the list interfaces: no boxed enumerator per column and no interface call per value.
        switch (bag.FieldType)
        {
            case FieldType.Bool:
                foreach (bool value in bag.BoolSpan)
                {
                    ProtobufEncoder.WriteBool(ref colBuf, PbfFieldNumbers.ColumnBoolValues, value);
                }

                break;
            case FieldType.I64:
                foreach (long value in bag.I64Span)
                {
                    ProtobufEncoder.WriteSint64(ref colBuf, PbfFieldNumbers.ColumnI64Values, value);
                }

                break;
            case FieldType.U64:
                foreach (ulong value in bag.U64Span)
                {
                    ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnU64Values, value);
                }

                break;
            case FieldType.F64:
                foreach (double value in bag.F64Span)
                {
                    ProtobufEncoder.WriteDouble(ref colBuf, PbfFieldNumbers.ColumnF64Values, value);
                }

                break;
            case FieldType.Timestamp:
                foreach (long value in bag.TimestampSpan)
                {
                    ProtobufEncoder.WriteSint64(ref colBuf, PbfFieldNumbers.ColumnTimestampValues, value);
                }

                break;
            case FieldType.String:
                _WriteNullableStringColumn(ref colBuf, PbfFieldNumbers.ColumnStringValues, bag.StringSpan);
                break;
            case FieldType.Bytes:
                // The bytes come from the arena: no byte[] per value (BytesValues would create one per row at flush).
                for (int row = 0; row < bag.RowCount; row++)
                {
                    ProtobufEncoder.WriteLengthDelimited(ref colBuf, PbfFieldNumbers.ColumnBytesValues, bag.GetBlob(row));
                }

                break;
            case FieldType.IPv4Address:
                _WriteIPv4Column(ref colBuf, bag);
                break;
            case FieldType.MacAddress:
                _WriteMacColumn(ref colBuf, bag);
                break;
            case FieldType.Eui64:
                _WriteEui64Column(ref colBuf, bag);
                break;
            case FieldType.IPv6Address:
                _WriteIPv6Columns(ref colBuf, bag);
                break;
            case FieldType.Uuid:
                _WriteUuidColumns(ref colBuf, bag);
                break;

            default:
                FieldColumnBag.ThrowUnsupportedColumnType(bag.FieldIdValue, bag.FieldType);
                break;
        }
    }

    #endregion

    #region Address columns

    // The five address types are written as integers in packed fixed-width columns (one length prefix per column, no tag per value),
    // whose numeric order is the address order. Why fixed width and packed: an address uses all its bits, so a varint would not be
    // shorter, and a packed column of n values is exactly n times the width, so a reader can index a row without scanning. The
    // columns are written straight into the column buffer (ProtobufEncoder.ReservePackedFixed32/64): no scratch array and no
    // allocation per value. Each loop reads the address with the typed accessor of its type; the type was fixed when the bag was
    // created, so a false is a broken invariant and is reported from a cold helper (the block build fails, the batch is not
    // written wrong). Sync: PbfFieldNumbers.ColumnFixed32Values / ColumnFixed64Values / ColumnFixed64LowValues and the PBF README
    // § Field values; docs/decisions/export-integer-addresses.md.

    /// <summary>Writes the packed fixed32 column of an <see cref="FieldType.IPv4Address"/> bag (field 19), one address per row.</summary>
    /// <param name="colBuf">The field column message the column is appended to.</param>
    /// <param name="bag">An IPv4 bag with at least one row.</param>
    /// <exception cref="InvalidOperationException">A row is no IPv4 value (a broken invariant).</exception>
    private static void _WriteIPv4Column(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> payload = ProtobufEncoder.ReservePackedFixed32(ref colBuf, PbfFieldNumbers.ColumnFixed32Values, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsIPv4(out IPv4Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv4Address, values[row].Type);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(payload[(row * 4)..], address.RawValue);
        }
    }

    /// <summary>Writes the packed fixed64 column of a <see cref="FieldType.MacAddress"/> bag (field 20), the 48-bit address in each entry.</summary>
    /// <param name="colBuf">The field column message the column is appended to.</param>
    /// <param name="bag">A MAC bag with at least one row.</param>
    /// <exception cref="InvalidOperationException">A row is no MAC value (a broken invariant).</exception>
    private static void _WriteMacColumn(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> payload = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64Values, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsMacAddress(out MacAddress address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.MacAddress, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(payload[(row * 8)..], address.RawValue);
        }
    }

    /// <summary>Writes the packed fixed64 column of a <see cref="FieldType.Eui64"/> bag (field 20), one identifier per row.</summary>
    /// <param name="colBuf">The field column message the column is appended to.</param>
    /// <param name="bag">An EUI-64 bag with at least one row.</param>
    /// <exception cref="InvalidOperationException">A row is no EUI-64 value (a broken invariant).</exception>
    private static void _WriteEui64Column(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> payload = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64Values, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsEui64(out Eui64 identifier))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Eui64, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(payload[(row * 8)..], identifier.RawValue);
        }
    }

    /// <summary>
    /// Writes the two packed fixed64 columns of an <see cref="FieldType.IPv6Address"/> bag: the high 64 bits of every row (field 20)
    /// and then the low 64 bits (field 21).
    /// </summary>
    /// <param name="colBuf">The field column message the columns are appended to.</param>
    /// <param name="bag">An IPv6 bag with at least one row.</param>
    /// <exception cref="InvalidOperationException">A row is no IPv6 value (a broken invariant).</exception>
    private static void _WriteIPv6Columns(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> high = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64Values, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsIPv6(out IPv6Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv6Address, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(high[(row * 8)..], address.High);
        }

        // The second column is reserved only after the first one is complete: reserving grows the buffer and would move the array
        // under a span that is still being filled.
        Span<byte> low = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64LowValues, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsIPv6(out IPv6Address address))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.IPv6Address, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(low[(row * 8)..], address.Low);
        }
    }

    /// <summary>
    /// Writes the two packed fixed64 columns of a <see cref="FieldType.Uuid"/> bag: the high 64 bits of every row (field 20) and then
    /// the low 64 bits (field 21).
    /// </summary>
    /// <param name="colBuf">The field column message the columns are appended to.</param>
    /// <param name="bag">A UUID bag with at least one row.</param>
    /// <exception cref="InvalidOperationException">A row is no UUID value (a broken invariant).</exception>
    private static void _WriteUuidColumns(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        ReadOnlySpan<FieldValueData> values = bag.FixedValueSpan;
        Span<byte> high = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64Values, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsUuid(out Uuid uuid))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Uuid, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(high[(row * 8)..], uuid.High);
        }

        // Reserved after the first column is complete, see _WriteIPv6Columns.
        Span<byte> low = ProtobufEncoder.ReservePackedFixed64(ref colBuf, PbfFieldNumbers.ColumnFixed64LowValues, values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            if (!values[row].TryGetAsUuid(out Uuid uuid))
            {
                FieldColumnBag.ThrowBrokenColumnType(bag.FieldIdValue, FieldType.Uuid, values[row].Type);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(low[(row * 8)..], uuid.Low);
        }
    }

    #endregion

    #region Text and string columns

    /// <summary>
    /// Writes the string-value column of a <see cref="FieldType.String"/> field: one entry per row, in row order, a
    /// <see langword="null"/> value as an empty string. The column is omitted entirely when there are no rows. Null and empty
    /// therefore cannot be told apart by a PBF reader here; the optional text columns do not have this limit
    /// (<see cref="WriteTextColumn"/>).
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    /// <param name="values">One text per row; <see langword="null"/> entries are written as empty strings.</param>
    private void _WriteNullableStringColumn(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<string?> values)
    {
        if (values.Length == 0)
        {
            return;
        }

        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        foreach (string? value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value ?? string.Empty);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    /// <summary>
    /// Writes a custom-text or custom-representation column with presence: sub-field 1 (<see cref="PbfFieldNumbers.TextColumnPresent"/>)
    /// is one present text, also an empty one; sub-field 2 (<see cref="PbfFieldNumbers.TextColumnAbsentRun"/>) is the number of
    /// consecutive rows without a text. The entries follow the rows in order. The column is omitted when no row has a text, so an
    /// export without texts costs nothing.
    /// </summary>
    /// <param name="buffer">The message the column is appended to.</param>
    /// <param name="fieldNumber">Field number of the column in that message.</param>
    /// <param name="values">
    /// One entry per row: <see langword="null"/> for a row without a text (never written as an empty string), the text otherwise.
    /// </param>
    /// <remarks>
    /// Perf and size: a field whose dissector sets a text on few rows (a column of mostly absent rows) costs one varint per
    /// run instead of one empty string (2 bytes) per absent row. Rejected alternative: a presence bitmap column (a second column
    /// and more code, not smaller than the runs for the usual all-or-nothing columns). Cost: one pass to find out whether any row
    /// has a text, one pass to encode; no allocation. Not thread-safe: it uses the builder's sub-column scratch.
    /// Visibility: internal so the encoder tests can feed rows without parsing packets.
    /// Sync: <see cref="PbfFieldNumbers.TextColumnPresent"/>, the PBF README § Texts, and the exporter tests' decoder.
    /// </remarks>
    internal void WriteTextColumn(ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<string?> values)
    {
        // A column with no text on any row is left out: the reader treats a missing column as "every row absent".
        bool anyPresent = false;
        foreach (string? value in values)
        {
            if (value is not null)
            {
                anyPresent = true;
                break;
            }
        }

        if (!anyPresent)
        {
            return;
        }

        // The entries are encoded into the sub-column scratch first because the length prefix of the column must precede its content.
        // Invariant: an absent run is flushed before the next present text and after the last row, so runs are maximal and the
        // entry sequence is unique for a given row list.
        PooledBuffer col = _SubColumnScratch;
        col.Reset();
        int absentRun = 0;
        foreach (string? value in values)
        {
            if (value is null)
            {
                absentRun++;
                continue;
            }

            if (absentRun > 0)
            {
                ProtobufEncoder.WriteVarintField(ref col, PbfFieldNumbers.TextColumnAbsentRun, (ulong)absentRun);
                absentRun = 0;
            }

            ProtobufEncoder.WriteString(ref col, PbfFieldNumbers.TextColumnPresent, value);
        }

        if (absentRun > 0)
        {
            ProtobufEncoder.WriteVarintField(ref col, PbfFieldNumbers.TextColumnAbsentRun, (ulong)absentRun);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
    }

    #endregion
}
