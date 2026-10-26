// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Column-oriented PBF block builder. Wraps the shared
/// <see cref="ColumnarPacketBatch"/> (also used by the Parquet and DuckDB exporters) and
/// serializes it to the PBF columnar wire format (<see cref="PbfFieldNumbers.BlockTypeColumnar"/>).
/// <para>
/// Each field becomes a separate <c>FieldColumn</c> sub-message (see
/// <see cref="PbfFieldNumbers.ColumnFieldId"/> and neighbouring constants). String columns
/// (value, custom representation, custom text) are written as plain repeated strings.
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
/// <b>Thread safety:</b> Not thread-safe; owned by <see cref="PbfExporter"/> and used from its single caller thread.
/// The span returned by <see cref="Build"/> aliases a pooled buffer and must be consumed before the next call to
/// <see cref="Build"/>, <see cref="Reset"/> or <see cref="Dispose"/>.
/// </para>
/// </summary>
internal sealed class ColumnarBlockBuilder : IDisposable
{
    #region Fields

    private readonly ColumnarPacketBatch _Batch;
    private readonly FieldPresence _FieldPresence;

    /// <summary>
    /// Pooled envelope buffer that owns the bytes returned by <see cref="Build"/>.
    /// Held as a field (not a local) so the rented memory is returned to the pool when
    /// <see cref="Reset"/> or <see cref="Dispose"/> runs.
    /// </summary>
    private PooledBuffer? _Envelope;

    #endregion

    #region Constructor

    /// <summary>Creates a new columnar block builder.</summary>
    /// <param name="maxFieldId">
    /// Exclusive upper bound of the field IDs tracked in the presence bitmap. IDs at or above it are not rejected
    /// (unlike the standard block builder); they are written normally but are missing from the presence bitmap.
    /// </param>
    /// <param name="maxPacketsPerBlock">Maximum packets before flush.</param>
    /// <param name="maxBlockSize">Maximum estimated block size before flush.</param>
    /// <param name="flags">Controls which optional per-packet/per-field data is captured.</param>
    /// <param name="isTimestampSorted">Whether the caller guarantees non-decreasing packet timestamps.</param>
    internal ColumnarBlockBuilder(
        int maxFieldId,
        int maxPacketsPerBlock = 50000,
        long maxBlockSize = 16 * 1024 * 1024,
        ColumnarDetailFlags flags = ColumnarDetailFlags.All,
        bool isTimestampSorted = false)
    {
        _Batch = new ColumnarPacketBatch(flags, maxPacketsPerBlock, maxBlockSize, isTimestampSorted);
        _FieldPresence = new FieldPresence(maxFieldId);
    }

    #endregion

    #region Properties

    /// <summary>Number of packets accumulated in the current block.</summary>
    internal int PacketCount => _Batch.PacketCount;

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

    /// <summary>Field presence bitmap for the current block, for trailer aggregation.</summary>
    internal FieldPresence FieldPresence => _FieldPresence;

    #endregion

    #region Public API

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

    /// <summary>
    /// Builds the columnar block as protobuf-encoded bytes (PBF columnar wire format).
    /// Returns a span that is valid until <see cref="Reset"/> or the next <see cref="Build"/> call.
    /// </summary>
    /// <returns>The serialized block, ready to be compressed and framed by the exporter.</returns>
    internal ReadOnlySpan<byte> Build()
    {
        // The header values (min/max timestamp and packet ID) and the presence bitmap are computed from the whole
        // batch here, once per block, instead of being tracked on every AddPacket call.
        _ComputeTimestampRange();
        _MarkFieldPresence();

        // The payload is assembled in a temporary pooled buffer because the envelope needs its final length first.
        // It is returned to the pool at the end of this method; only the envelope outlives the call.
        PooledBuffer payload = new(64 * 1024);

        // Block header: the same five fields as the standard block (shared field numbers).
        int packetCount = PacketCount;
        ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockPacketCount, (ulong)packetCount);

        if (packetCount > 0)
        {
            // Packet IDs are not guaranteed to be sorted, so a scan is needed; packet IDs are non-negative, so the
            // plain varint cast keeps them short.
            IReadOnlyList<int> packetIds = _Batch.PacketIds;
            int minId = packetIds[0], maxId = packetIds[0];
            for (int i = 1; i < packetCount; i++)
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
        // the optional columns follow only when their detail flag was requested, mirroring what the batch collected.
        _WriteDeltaSint64Column(ref payload, PbfFieldNumbers.ColumnarPacketIds, _Batch.PacketIds);
        _WriteDeltaSint64Column(ref payload, PbfFieldNumbers.ColumnarTimestamps, _Batch.Timestamps);

        if ((_Batch.Flags & ColumnarDetailFlags.IncludeInfo) != 0)
        {
            _WriteStringColumn(ref payload, PbfFieldNumbers.ColumnarInfos, _Batch.Infos);
        }

        if ((_Batch.Flags & ColumnarDetailFlags.IncludeFrameBytes) != 0)
        {
            _WriteBytesColumn(ref payload, PbfFieldNumbers.ColumnarFrameBytes, _Batch.FrameBytesList);
        }

        if ((_Batch.Flags & ColumnarDetailFlags.IncludeTopology) != 0)
        {
            _WriteTopology(ref payload);
        }

        _WriteFieldColumns(ref payload);

        // Wrap in block envelope. Release any previous envelope first (defensive: the
        // caller is expected to call Reset between blocks, but Build-without-Reset must
        // not leak the previous rental). The envelope is sized for the payload plus the block-type field and the
        // payload tag and length varint (at most 1 + 1 + 1 + 5 bytes), so writing it never regrows the buffer.
        _Envelope?.Return();
        PooledBuffer envelope = new(payload.Length + 32);
        ProtobufEncoder.WriteVarintField(ref envelope, PbfFieldNumbers.BlockType, PbfFieldNumbers.BlockTypeColumnar);
        ProtobufEncoder.WriteLengthDelimited(ref envelope, PbfFieldNumbers.BlockPayload, payload.WrittenSpan);
        payload.Return();

        _Envelope = envelope;
        return envelope.WrittenSpan;
    }

    /// <summary>Resets all state for reuse by the next block. Field catalog / bag metadata is retained.</summary>
    internal void Reset()
    {
        _Envelope?.Return();
        _Envelope = null;
        _Batch.Reset();
        _FieldPresence.Clear();
        MinTimestamp = 0;
        MaxTimestamp = 0;
    }

    /// <summary>Returns all pooled buffers and releases the underlying batch.</summary>
    public void Dispose()
    {
        _Envelope?.Return();
        _Envelope = null;
        _Batch.Dispose();
    }

    #endregion

    #region Private Helpers — Header

    /// <summary>Computes <see cref="MinTimestamp"/>/<see cref="MaxTimestamp"/> from the current batch.</summary>
    private void _ComputeTimestampRange()
    {
        IReadOnlyList<long> timestamps = _Batch.Timestamps;
        if (timestamps.Count == 0)
        {
            MinTimestamp = 0;
            MaxTimestamp = 0;
            return;
        }

        // Full scan: the sorted-timestamps declaration is metadata only (see the builder option) and does not
        // shortcut this to first/last, so the min/max written to the block header and trailer index are always exact.
        long minTs = timestamps[0], maxTs = timestamps[0];
        for (int i = 1; i < timestamps.Count; i++)
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
    /// columns even when <see cref="ColumnarDetailFlags.IncludeTopology"/> is cleared).
    /// </summary>
    private void _MarkFieldPresence()
    {
        // The return value of Mark (first occurrence) is not needed: columnar blocks carry no per-block metadata
        // deduplication, the bitmap is only aggregated into the trailer. Field IDs beyond the bitmap range are
        // ignored by Mark and therefore missing from the trailer bitmap (no exception, no data loss in the block).
        foreach (TopologyNode node in _Batch.Topology)
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

    #region Private Helpers — Column Writers

    /// <summary>
    /// Initial capacity for a column buffer: <paramref name="count"/> times <paramref name="bytesPerValue"/> plus
    /// <paramref name="fixedBytes"/>, a guess of the encoded size. The product is formed in 64 bits and capped at 64 MiB:
    /// with a very large <c>maxPacketsPerBlock</c> the 32-bit product wraps negative and the buffer constructor would reject
    /// it. The cap is a sizing hint, not a limit; the buffer still grows on demand, and the buffer itself reports when a
    /// column would exceed <see cref="Array.MaxLength"/> bytes.
    /// </summary>
    private static int _InitialColumnCapacity(int count, int bytesPerValue, int fixedBytes)
    {
        const long MaxInitialCapacity = 64L * 1024 * 1024;
        long guess = ((long)count * bytesPerValue) + fixedBytes;
        return checked((int)Math.Min(guess, MaxInitialCapacity));
    }

    /// <summary>
    /// Writes a delta-encoded sint64 column. The first element is the base value (sub-field 1);
    /// subsequent elements are deltas from their predecessor (sub-field 2). Encoding deltas
    /// instead of absolute values produces smaller varints for monotonic sequences such as
    /// packet IDs and timestamps.
    /// </summary>
    private static void _WriteDeltaSint64Column(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<long> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        // The column is encoded into its own temporary buffer first because the length prefix of the enclosing
        // field must precede the content. Initial capacity is a heuristic of about 2 bytes per value (most deltas fit
        // in one or two varint bytes); PooledBuffer grows if the guess is too small.
        PooledBuffer col = new(_InitialColumnCapacity(values.Count, 2, 16));
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);

        // Differences are computed in unchecked 64-bit arithmetic: an extreme jump wraps instead of throwing, and
        // a reader that accumulates with the same wrap-around reconstructs the exact values. The zigzag encoding
        // keeps negative deltas (out-of-order input) short as well.
        for (int i = 1; i < values.Count; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, values[i] - values[i - 1]);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>
    /// Writes a delta-encoded sint64 column from <see cref="int"/> values (widened at the wire;
    /// used for packet IDs which share the Core <see cref="PacketId"/> <see cref="int"/> range).
    /// </summary>
    private static void _WriteDeltaSint64Column(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        // Same layout as the long overload. The subtraction is widened to long first, so two ints can never overflow.
        PooledBuffer col = new(_InitialColumnCapacity(values.Count, 2, 16));
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);
        for (int i = 1; i < values.Count; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, (long)values[i] - values[i - 1]);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>
    /// Writes a repeated string column (one tag+value per row, in row order). Unlike the delta column writer and the
    /// nullable variant it also writes an empty sub-message when there are no rows.
    /// </summary>
    private static void _WriteStringColumn(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<string> values)
    {
        PooledBuffer col = new(_InitialColumnCapacity(values.Count, 16, 0));
        foreach (string value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>Writes a repeated bytes column. Null entries are written as zero-length byte strings.</summary>
    private static void _WriteBytesColumn(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<byte[]> values)
    {
        PooledBuffer col = new(_InitialColumnCapacity(values.Count, 16, 0));
        foreach (byte[] value in values)
        {
            ProtobufEncoder.WriteLengthDelimited(ref col, 1, value);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>Writes the <see cref="ColumnarPacketBatch.Topology"/> rows as four parallel columns.</summary>
    private void _WriteTopology(ref PooledBuffer buffer)
    {
        IReadOnlyList<TopologyNode> topology = _Batch.Topology;
        if (topology.Count == 0)
        {
            return;
        }

        // The four columns are written as parallel repeated fields of one sub-message, so row i is the i-th element
        // of each. Node and field IDs are never negative (plain varint); the parent node ID is -1 for top-level
        // fields, hence sint64 (zigzag) so that -1 costs one byte instead of ten.
        PooledBuffer topo = new(_InitialColumnCapacity(topology.Count, 4, 0));

        // Packet IDs: delta-encoded (topology rows are grouped by ascending packet insertion order).
        PooledBuffer packetIdCol = new(_InitialColumnCapacity(topology.Count, 2, 16));
        ProtobufEncoder.WriteSint64(ref packetIdCol, 1, topology[0].PacketId);
        for (int i = 1; i < topology.Count; i++)
        {
            ProtobufEncoder.WriteSint64(ref packetIdCol, 2, topology[i].PacketId - topology[i - 1].PacketId);
        }
        ProtobufEncoder.WriteLengthDelimited(ref topo, PbfFieldNumbers.TopologyPacketIds, packetIdCol.WrittenSpan);
        packetIdCol.Return();

        foreach (TopologyNode node in topology)
        {
            ProtobufEncoder.WriteVarintField(ref topo, PbfFieldNumbers.TopologyNodeIds, (ulong)node.NodeId);
        }
        foreach (TopologyNode node in topology)
        {
            ProtobufEncoder.WriteVarintField(ref topo, PbfFieldNumbers.TopologyFieldIds, (ulong)node.FieldId);
        }
        foreach (TopologyNode node in topology)
        {
            ProtobufEncoder.WriteSint64(ref topo, PbfFieldNumbers.TopologyParentNodeIds, node.ParentNodeId);
        }

        ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.ColumnarTopology, topo.WrittenSpan);
        topo.Return();
    }

    /// <summary>Writes every field's <see cref="FieldColumnBag"/> as a <c>FieldColumn</c> sub-message.</summary>
    private void _WriteFieldColumns(ref PooledBuffer buffer)
    {
        foreach (KeyValuePair<int, FieldColumnBag> entry in _Batch.FieldBags)
        {
            FieldColumnBag bag = entry.Value;
            if (bag.RowCount == 0)
            {
                continue;
            }

            // One FieldColumn sub-message per field ID, built in its own buffer (length prefix first) and returned to
            // the pool right after being copied into the payload. Bags that exist but received no rows in this block
            // (the batch keeps bags across blocks) are skipped.
            PooledBuffer colBuf = new(256);
            ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnFieldId, (ulong)bag.FieldIdValue);
            ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnFieldType, (ulong)bag.FieldType);

            _WriteDeltaSint64Column(ref colBuf, PbfFieldNumbers.ColumnPacketIds, bag.PacketIds);
            foreach (int nodeId in bag.NodeIds)
            {
                ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnNodeIds, (ulong)nodeId);
            }

            _WriteTypedValues(ref colBuf, bag);

            if ((_Batch.Flags & ColumnarDetailFlags.IncludeCustomRepresentation) != 0)
            {
                _WriteNullableStringColumn(ref colBuf, PbfFieldNumbers.ColumnCustomRepresentations, bag.CustomRepresentations);
            }

            if ((_Batch.Flags & ColumnarDetailFlags.IncludeCustomText) != 0)
            {
                _WriteNullableStringColumn(ref colBuf, PbfFieldNumbers.ColumnCustomTexts, bag.CustomTexts);
            }

            ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.ColumnarFieldColumns, colBuf.WrittenSpan);
            colBuf.Return();
        }
    }

    /// <summary>
    /// Writes the type-specific value column matching <see cref="FieldColumnBag.FieldType"/>. Numeric values are
    /// written as one repeated field entry per row directly in the column message (signed and timestamp values
    /// zigzag encoded, doubles as fixed64); string columns are wrapped in a sub-message; bytes and fixed-size
    /// address types share the bytes column, each as raw bytes.
    /// </summary>
    private static void _WriteTypedValues(ref PooledBuffer colBuf, FieldColumnBag bag)
    {
        switch (bag.FieldType)
        {
            case FieldType.Bool:
                foreach (bool value in bag.BoolValues)
                {
                    ProtobufEncoder.WriteBool(ref colBuf, PbfFieldNumbers.ColumnBoolValues, value);
                }
                break;
            case FieldType.I64:
                foreach (long value in bag.I64Values)
                {
                    ProtobufEncoder.WriteSint64(ref colBuf, PbfFieldNumbers.ColumnI64Values, value);
                }
                break;
            case FieldType.U64:
                foreach (ulong value in bag.U64Values)
                {
                    ProtobufEncoder.WriteVarintField(ref colBuf, PbfFieldNumbers.ColumnU64Values, value);
                }
                break;
            case FieldType.F64:
                foreach (double value in bag.F64Values)
                {
                    ProtobufEncoder.WriteDouble(ref colBuf, PbfFieldNumbers.ColumnF64Values, value);
                }
                break;
            case FieldType.Timestamp:
                foreach (long value in bag.TimestampValues)
                {
                    ProtobufEncoder.WriteSint64(ref colBuf, PbfFieldNumbers.ColumnTimestampValues, value);
                }
                break;
            case FieldType.String:
                _WriteNullableStringColumn(ref colBuf, PbfFieldNumbers.ColumnStringValues, bag.StringValues);
                break;
            case FieldType.Bytes:
            case FieldType.MacAddress:
            case FieldType.IPv4Address:
            case FieldType.IPv6Address:
            case FieldType.Eui64:
            case FieldType.Uuid:
                foreach (byte[]? value in bag.BytesValues)
                {
                    ProtobufEncoder.WriteLengthDelimited(ref colBuf, PbfFieldNumbers.ColumnBytesValues, value ?? []);
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Field {bag.FieldIdValue} has unsupported column type {bag.FieldType}.");
        }
    }

    /// <summary>
    /// Writes a repeated nullable string column, emitting null entries as empty strings. The column is omitted
    /// entirely when there are no rows. Null and empty therefore cannot be told apart by a PBF reader, unlike in
    /// the Parquet and DuckDB output, which keep nulls.
    /// </summary>
    private static void _WriteNullableStringColumn(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<string?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        PooledBuffer col = new(_InitialColumnCapacity(values.Count, 16, 0));
        foreach (string? value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value ?? string.Empty);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    #endregion
}
