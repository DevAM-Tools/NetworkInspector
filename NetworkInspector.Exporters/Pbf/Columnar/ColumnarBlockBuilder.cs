// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf.Columnar;

/// <summary>
/// Column-oriented PBF block builder. Groups field values by column for better
/// compression and query performance. Each field becomes a separate column with
/// optional dictionary encoding for repeated string values.
/// <para>
/// Topology (field-tree structure per packet) is stored as two flat parallel
/// arrays plus per-packet offsets, avoiding one <c>List&lt;int&gt;</c> allocation
/// per packet that the previous per-packet-list approach incurred.
/// </para>
/// <para>
/// The <see cref="_Columns"/> dictionary is intentionally kept alive across
/// block boundaries so field metadata does not need to be re-transmitted on
/// every block. Callers operating on extremely long captures with very diverse
/// protocol mixes should be aware that column metadata grows for the lifetime of
/// the exporter. In practice, the number of distinct field IDs in any realistic
/// protocol mix is bounded (typically ≤ 10 000).
/// </para>
/// </summary>
internal sealed class ColumnarBlockBuilder : IDisposable
{
    private readonly Dictionary<int, ColumnBuilder> _Columns = new(64);
    private readonly List<long> _PacketIds = new(256);
    private readonly List<long> _Timestamps = new(256);
    private readonly List<string> _InfoStrings = new(256);

    // Flat topology arrays: all packets' field IDs / child counts stored sequentially.
    // Per-packet start offset and length stored in _TopologyOffsets / _TopologyLengths.
    // This eliminates one List<int> allocation per packet (previously: two new List<int>
    // per AddPacket call = HIGH-6 fix).
    private readonly List<int> _FlatTopologyFieldIds = new(256 * 8);
    private readonly List<int> _FlatTopologyChildCounts = new(256 * 8);
    private readonly List<int> _TopologyOffsets = new(256);
    private readonly List<int> _TopologyLengths = new(256);

    private readonly FieldPresence _FieldPresence;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize;

    // Running estimated serialized size (in bytes) for the current block.
    // Used to enforce _MaxBlockSize in AddPacket, which was previously never
    // checked in columnar mode (CRITICAL-2 fix).
    private long _EstimatedSize;

    /// <summary>
    /// Pooled envelope buffer that owns the bytes returned by <see cref="Build"/>.
    /// Held as a field (not a local) so the rented memory is returned to the pool when
    /// <see cref="Reset"/> or <see cref="Dispose"/> runs.
    /// </summary>
    private PooledBuffer? _Envelope;

    /// <summary>
    /// Creates a new columnar block builder.
    /// </summary>
    /// <param name="maxFieldId">Maximum expected field ID.</param>
    /// <param name="maxPacketsPerBlock">Maximum packets before flush.</param>
    /// <param name="maxBlockSize">Maximum estimated block size before flush.</param>
    internal ColumnarBlockBuilder(
        int maxFieldId,
        int maxPacketsPerBlock = 50000,
        long maxBlockSize = 16 * 1024 * 1024)
    {
        _FieldPresence = new FieldPresence(maxFieldId);
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
    }

    /// <summary>Number of packets accumulated.</summary>
    internal int PacketCount => _PacketIds.Count;

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

    /// <summary>
    /// Adds a packet to the columnar block. Returns <c>true</c> if the block
    /// should be flushed after this packet (packet count or estimated size threshold reached).
    /// </summary>
    internal bool AddPacket(Packet packet)
    {
        _PacketIds.Add(packet.Id.Value);
        _Timestamps.Add(packet.Timestamp.AsNanos);
        _InfoStrings.Add(packet.Info);

        // Track estimated size: 16 bytes per packet for IDs/timestamps (encoded varints),
        // plus the info string. Field values are tracked inside CollectFieldValues.
        _EstimatedSize += 16 + packet.Info.Length;

        // Encode topology into flat arrays (no per-packet List<int> allocation).
        int topoStart = _FlatTopologyFieldIds.Count;
        TopologyEncoder.Encode(packet.RootField(), _FlatTopologyFieldIds, _FlatTopologyChildCounts);
        int topoCount = _FlatTopologyFieldIds.Count - topoStart;
        _TopologyOffsets.Add(topoStart);
        _TopologyLengths.Add(topoCount);
        _EstimatedSize += topoCount * 4; // rough: 2 varints per topology entry

        // Collect field values into columns (depth-first traversal)
        CollectFieldValues(packet.RootField());

        return PacketCount >= _MaxPacketsPerBlock || _EstimatedSize >= _MaxBlockSize;
    }

    /// <summary>
    /// Builds the columnar block as protobuf-encoded bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Build()
    {
        PooledBuffer payload = new(64 * 1024);

        // Block header: packet count, min/max IDs and timestamps
        ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockPacketCount, (ulong)PacketCount);

        if (PacketCount > 0)
        {
            long minId = _PacketIds[0], maxId = _PacketIds[0];
            long minTs = _Timestamps[0], maxTs = _Timestamps[0];
            for (int i = 1; i < PacketCount; i++)
            {
                if (_PacketIds[i] < minId)
                {
                    minId = _PacketIds[i];
                }
                if (_PacketIds[i] > maxId)
                {
                    maxId = _PacketIds[i];
                }
                if (_Timestamps[i] < minTs)
                {
                    minTs = _Timestamps[i];
                }
                if (_Timestamps[i] > maxTs)
                {
                    maxTs = _Timestamps[i];
                }
            }
            // Expose min/max timestamps so the caller (PbfExporter) can write correct
            // block-index entries in the trailer even for columnar blocks.
            MinTimestamp = minTs;
            MaxTimestamp = maxTs;
            ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockMinPacketId, (ulong)minId);
            ProtobufEncoder.WriteVarintField(ref payload, PbfFieldNumbers.BlockMaxPacketId, (ulong)maxId);
            ProtobufEncoder.WriteSint64(ref payload, PbfFieldNumbers.BlockMinTimestamp, minTs);
            ProtobufEncoder.WriteSint64(ref payload, PbfFieldNumbers.BlockMaxTimestamp, maxTs);
        }

        // Delta-encode packet IDs (inline, avoids long[] allocation from DeltaEncoder)
        WriteColumnSint64(ref payload, 10, CollectionsMarshal.AsSpan(_PacketIds));

        // Delta-encode timestamps (inline)
        WriteColumnSint64(ref payload, 11, CollectionsMarshal.AsSpan(_Timestamps));

        // Info strings column
        WriteColumnStrings(ref payload, 12, _InfoStrings);

        // Topology columns
        WriteTopologyColumns(ref payload);

        // Field value columns
        WriteFieldColumns(ref payload);

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

    /// <summary>Resets all state for reuse.</summary>
    internal void Reset()
    {
        _Envelope?.Return();
        _Envelope = null;
        _PacketIds.Clear();
        _Timestamps.Clear();
        _InfoStrings.Clear();
        _FlatTopologyFieldIds.Clear();
        _FlatTopologyChildCounts.Clear();
        _TopologyOffsets.Clear();
        _TopologyLengths.Clear();
        _EstimatedSize = 0;
        _FieldPresence.Clear();
        foreach (ColumnBuilder column in _Columns.Values)
        {
            column.Reset();
        }
    }

    /// <summary>
    /// Returns all pooled buffers to the pool.
    /// Called by <see cref="PbfExporter"/> when the exporter is finished.
    /// </summary>
    public void Dispose()
    {
        _Envelope?.Return();
        _Envelope = null;
    }

    /// <summary>Returns the field presence for trailer aggregation.</summary>
    internal FieldPresence FieldPresence => _FieldPresence;

    // ========================================================================
    // Private helpers
    // ========================================================================

    /// <summary>
    /// Collects field values into column builders for all descendants of <paramref name="rootField"/>.
    /// Uses the struct-based <see cref="FieldDescendantEnumerator"/> (inline stack, zero-alloc for
    /// trees up to 16 levels deep) instead of recursion to avoid per-level C# stack frame growth.
    /// </summary>
    private void CollectFieldValues(Field rootField)
    {
        // Descendants() returns FieldDescendantEnumerable whose GetEnumerator() resolves to the
        // ref-struct FieldDescendantEnumerator via duck-typing — no IEnumerable<Field> boxing.
        foreach (Field field in rootField.Descendants())
        {
            int fieldIdValue = field.FieldId.Value;
            _FieldPresence.Mark(fieldIdValue);

            ColumnBuilder column = GetOrCreateColumn(fieldIdValue);
            FieldValue value = field.Value;
            string? valueStr = FormatFieldValue(value);
            string? customRepresentation = !value.CustomRepresentation.IsNull
                ? value.CustomRepresentation.AsString : null;
            LazyString customText = field.CustomText;
            string? customTextStr = !customText.IsNull ? customText.AsString : null;
            column.AddRow(valueStr, customRepresentation, customTextStr);

            // Accumulate estimated encoded size contribution for this field value.
            _EstimatedSize += (valueStr?.Length ?? 0) + (customRepresentation?.Length ?? 0)
                + (customTextStr?.Length ?? 0) + 4; // 4 = protobuf overhead estimate
        }
    }

    /// <summary>Gets or creates a column builder for the given field ID.</summary>
    private ColumnBuilder GetOrCreateColumn(int fieldIdValue)
    {
        if (!_Columns.TryGetValue(fieldIdValue, out ColumnBuilder? column))
        {
            column = new ColumnBuilder(fieldIdValue);
            _Columns[fieldIdValue] = column;
        }
        return column;
    }

    /// <summary>
    /// Writes a delta-encoded sint64 column. The first element is stored as the base
    /// value (field 1); subsequent elements are stored as deltas from their predecessor
    /// (field 2). Encoding deltas instead of absolute values produces smaller varints
    /// for monotonic sequences such as packet IDs and timestamps.
    /// No intermediate array is allocated; deltas are computed inline (LOW-3 fix).
    /// </summary>
    private static void WriteColumnSint64(
        ref PooledBuffer buffer, int fieldNumber, ReadOnlySpan<long> values)
    {
        if (values.IsEmpty)
        {
            return;
        }

        PooledBuffer col = new(values.Length * 2 + 16);
        ProtobufEncoder.WriteSint64(ref col, 1, values[0]); // base = first value
        for (int i = 1; i < values.Length; i++)
        {
            ProtobufEncoder.WriteSint64(ref col, 2, values[i] - values[i - 1]); // delta
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>Writes a string column.</summary>
    private static void WriteColumnStrings(
        ref PooledBuffer buffer, int fieldNumber, List<string> values)
    {
        PooledBuffer col = new(values.Count * 16);
        foreach (string value in values)
        {
            ProtobufEncoder.WriteString(ref col, 1, value);
        }
        ProtobufEncoder.WriteLengthDelimited(ref buffer, fieldNumber, col.WrittenSpan);
        col.Return();
    }

    /// <summary>Writes topology columns (field IDs and child counts per packet).</summary>
    private void WriteTopologyColumns(ref PooledBuffer buffer)
    {
        PooledBuffer topo = new(PacketCount * 64);
        for (int i = 0; i < PacketCount; i++)
        {
            // Per-packet topology as nested message, using flat topology arrays
            // (no per-packet List<int> allocation).
            PooledBuffer packetTopo = new(64);
            int offset = _TopologyOffsets[i];
            int length = _TopologyLengths[i];
            for (int j = offset; j < offset + length; j++)
            {
                ProtobufEncoder.WriteVarintField(ref packetTopo, 1, (ulong)_FlatTopologyFieldIds[j]);
            }
            for (int j = offset; j < offset + length; j++)
            {
                ProtobufEncoder.WriteVarintField(ref packetTopo, 2, (ulong)_FlatTopologyChildCounts[j]);
            }
            ProtobufEncoder.WriteLengthDelimited(ref topo, 1, packetTopo.WrittenSpan);
            packetTopo.Return();
        }
        // Topology column at field 13
        ProtobufEncoder.WriteLengthDelimited(ref buffer, 13, topo.WrittenSpan);
        topo.Return();
    }

    /// <summary>Writes all field value columns.</summary>
    private void WriteFieldColumns(ref PooledBuffer buffer)
    {
        foreach (KeyValuePair<int, ColumnBuilder> entry in _Columns)
        {
            PooledBuffer colBuf = new(256);
            ColumnBuilder column = entry.Value;

            // Field ID for this column
            ProtobufEncoder.WriteVarintField(ref colBuf, 1, (ulong)column.FieldIdValue);

            // Values
            IReadOnlyList<string?> values = column.Values;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not null)
                {
                    ProtobufEncoder.WriteString(ref colBuf, 2, values[i]);
                }
            }

            // Custom representations
            IReadOnlyList<string?> customRepresentations = column.CustomRepresentations;
            for (int i = 0; i < customRepresentations.Count; i++)
            {
                if (customRepresentations[i] is not null)
                {
                    ProtobufEncoder.WriteString(ref colBuf, 3, customRepresentations[i]);
                }
            }

            // Custom texts
            IReadOnlyList<string?> customTexts = column.CustomTexts;
            for (int i = 0; i < customTexts.Count; i++)
            {
                if (customTexts[i] is not null)
                {
                    ProtobufEncoder.WriteString(ref colBuf, 4, customTexts[i]);
                }
            }

            // Column at field 14
            ProtobufEncoder.WriteLengthDelimited(ref buffer, 14, colBuf.WrittenSpan);
            colBuf.Return();
        }
    }

    /// <summary>Formats a field value as string for columnar storage.</summary>
    private static string? FormatFieldValue(FieldValue value) => FieldValueFormatter.Format(value);
}
