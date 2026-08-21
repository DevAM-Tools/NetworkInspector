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
    /// <param name="maxFieldId">Maximum expected field ID, sizing the presence bitmap.</param>
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
    /// </summary>
    internal bool AddPacket(Packet packet) => _Batch.AddPacket(packet);

    /// <summary>
    /// Builds the columnar block as protobuf-encoded bytes (PBF columnar wire format).
    /// Returns a span that is valid until <see cref="Reset"/> or the next <see cref="Build"/> call.
    /// </summary>
    internal ReadOnlySpan<byte> Build()
    {
        _ComputeTimestampRange();
        _MarkFieldPresence();

        PooledBuffer payload = new(64 * 1024);

        int packetCount = PacketCount;
        ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockPacketCount, (ulong)packetCount);

        if (packetCount > 0)
        {
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
        // not leak the previous rental).
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

        PooledBuffer col = new(values.Count * 2 + 16);
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);
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

        PooledBuffer col = new(values.Count * 2 + 16);
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]);
        for (int i = 1; i < values.Count; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, (long)values[i] - values[i - 1]);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>Writes a repeated string column (one tag+value per row, in row order).</summary>
    private static void _WriteStringColumn(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<string> values)
    {
        PooledBuffer col = new(values.Count * 16);
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
        PooledBuffer col = new(values.Count * 16);
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

        PooledBuffer topo = new(topology.Count * 4);

        // Packet IDs: delta-encoded (topology rows are grouped by ascending packet insertion order).
        PooledBuffer packetIdCol = new(topology.Count * 2 + 16);
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

    /// <summary>Writes the type-specific value column matching <see cref="FieldColumnBag.FieldType"/>.</summary>
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

    /// <summary>Writes a repeated nullable string column, emitting null entries as empty strings.</summary>
    private static void _WriteNullableStringColumn(ref PooledBuffer buffer, int fieldNumber, IReadOnlyList<string?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        PooledBuffer col = new(values.Count * 16);
        foreach (string? value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value ?? string.Empty);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    #endregion
}
