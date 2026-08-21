// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Accumulates packets into a standard (row-oriented) PBF block.
/// Each packet is serialized as a nested protobuf message with its fields inline.
/// <para>
/// Features:
/// <list type="bullet">
///   <item>Field-info deduplication per block (via <see cref="FieldPresence"/>)</item>
///   <item>Same-as-previous optimization for packet info and field values</item>
///   <item>Configurable flush thresholds by packet count and block size</item>
/// </list>
/// </para>
/// <para>
/// <b>Allocation strategy:</b> Per-depth scratch buffers in <see cref="_Scratch"/>
/// are pre-allocated once and reused for every packet's serialization, eliminating
/// the O(fields) <see cref="PooledBuffer"/> allocations that the naïve per-field
/// approach would incur. <see cref="_EnvelopeBuffer"/> is similarly pre-allocated
/// and reused across block flushes.
/// </para>
/// </summary>
internal sealed class StandardBlockBuilder
{
    private PooledBuffer _Buffer;
    private readonly FieldPresence _FieldPresence;
    private readonly PreviousFieldValueStore _PreviousFields;
    private readonly int _MaxFieldId;
    private string? _PreviousPacketInfo;
    private int _PacketCount;
    private int _MinPacketId;
    private int _MaxPacketId;
    private long _MinTimestamp;
    private long _MaxTimestamp;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize; // bytes

    /// <summary>
    /// Number of field subtrees silently dropped in this block because the protocol tree
    /// exceeded <see cref="_MaxNestingDepth"/>. Reset by <see cref="Reset"/>.
    /// Exposed so <see cref="PbfExporter"/> can report the truncation through the error
    /// tolerance mechanism instead of failing silently.
    /// </summary>
    private int _TruncatedFieldCount;

    // Per-depth scratch buffers used during packet/field serialization.
    // Index 0 = packet buffer, 1 = top-level field buffer, 2+ = nested.
    // Pre-allocated once and Reset() between uses — zero per-call allocations
    // for protocol trees with depth ≤ _MaxNestingDepth (HIGH-1 fix).
    private const int _MaxNestingDepth = 16;
    private readonly PooledBuffer[] _Scratch;

    // Reusable envelope buffer: holds the final serialized block bytes returned
    // from Build(). Reset() before each Build() call (HIGH-5 / MEDIUM-2 fix).
    private PooledBuffer _EnvelopeBuffer;

    /// <summary>
    /// Creates a new standard block builder.
    /// </summary>
    /// <param name="maxFieldId">Maximum expected field ID for the presence bitmap.</param>
    /// <param name="maxPacketsPerBlock">Maximum packets before a flush is triggered.</param>
    /// <param name="maxBlockSize">Maximum serialized block size in bytes before flush.</param>
    internal StandardBlockBuilder(int maxFieldId, int maxPacketsPerBlock = 50000, long maxBlockSize = 16 * 1024 * 1024)
    {
        _MaxFieldId = maxFieldId;
        _Buffer = new PooledBuffer(64 * 1024);
        _FieldPresence = new FieldPresence(maxFieldId);
        _PreviousFields = new PreviousFieldValueStore(maxFieldId);
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;

        // Pre-allocate per-depth scratch buffers. Depth 0 = packet, 1 = top-level
        // field, 2.._MaxNestingDepth-1 = nested children. Each buffer starts small
        // and grows on demand; after warm-up they reach steady state.
        _Scratch = new PooledBuffer[_MaxNestingDepth];
        for (int i = 0; i < _MaxNestingDepth; i++)
        {
            _Scratch[i] = new PooledBuffer(256);
        }

        // Pre-allocate the envelope buffer. Typical block is < 1 MB after encoding;
        // starting at 64 KiB and growing on demand avoids most reallocations.
        _EnvelopeBuffer = new PooledBuffer(64 * 1024);
    }

    /// <summary>Number of packets currently in the block.</summary>
    internal int PacketCount => _PacketCount;

    /// <summary>Current serialized size of the block data.</summary>
    internal int CurrentSize => _Buffer.Length;

    /// <summary>Minimum packet ID in this block (same range as <see cref="PacketId"/>).</summary>
    internal int MinPacketId => _MinPacketId;

    /// <summary>Maximum packet ID in this block (same range as <see cref="PacketId"/>).</summary>
    internal int MaxPacketId => _MaxPacketId;

    /// <summary>Minimum timestamp (nanos) in this block.</summary>
    internal long MinTimestamp => _MinTimestamp;

    /// <summary>Maximum timestamp (nanos) in this block.</summary>
    internal long MaxTimestamp => _MaxTimestamp;

    /// <summary>
    /// Number of field subtrees dropped because the protocol tree exceeded
    /// <see cref="_MaxNestingDepth"/> during this block's serialization.
    /// Reset to zero by <see cref="Reset"/>.
    /// </summary>
    internal int TruncatedFieldCount => _TruncatedFieldCount;

    /// <summary>
    /// Adds a packet to the block. Returns <c>true</c> if the block should be flushed
    /// after this packet (threshold reached).
    /// </summary>
    internal bool AddPacket(Packet packet)
    {
        int packetId = packet.Id.Value;
        long timestamp = packet.Timestamp.AsNanos;

        // Track min/max for block metadata
        if (_PacketCount == 0)
        {
            _MinPacketId = packetId;
            _MaxPacketId = packetId;
            _MinTimestamp = timestamp;
            _MaxTimestamp = timestamp;
        }
        else
        {
            if (packetId < _MinPacketId)
            {
                _MinPacketId = packetId;
            }
            if (packetId > _MaxPacketId)
            {
                _MaxPacketId = packetId;
            }
            if (timestamp < _MinTimestamp)
            {
                _MinTimestamp = timestamp;
            }
            if (timestamp > _MaxTimestamp)
            {
                _MaxTimestamp = timestamp;
            }
        }

        // Serialize the packet using _Scratch[0] as the packet buffer (no allocation).
        _Scratch[0].Reset();
        _SerializePacket(packet);

        // Write as nested message in the block: tag + length + content
        ProtobufEncoder.WriteLengthDelimited(ref _Buffer, PbfFieldNumbers.PacketField, _Scratch[0].WrittenSpan);

        _PacketCount++;

        // Check if block should be flushed
        return _PacketCount >= _MaxPacketsPerBlock || _Buffer.Length >= _MaxBlockSize;
    }

    /// <summary>
    /// Builds the complete block as protobuf-encoded bytes using the block envelope format.
    /// Returns a span that is valid until <see cref="Reset"/> is called.
    /// Uses pre-allocated scratch buffers (_Scratch[0] for header, _Scratch[1] for payload)
    /// and _EnvelopeBuffer for the envelope — no per-call heap allocations (HIGH-5 fix).
    /// </summary>
    internal ReadOnlySpan<byte> Build()
    {
        // Build block header into _Scratch[0] (always small — 5 varint fields ≤ 60 bytes)
        ref PooledBuffer headerScratch = ref _Scratch[0];
        headerScratch.Reset();
        ProtobufEncoder.WriteVarintField(ref headerScratch, PbfFieldNumbers.BlockPacketCount, (ulong)_PacketCount);
        ProtobufEncoder.WriteVarintField(ref headerScratch, PbfFieldNumbers.BlockMinPacketId, (ulong)_MinPacketId);
        ProtobufEncoder.WriteVarintField(ref headerScratch, PbfFieldNumbers.BlockMaxPacketId, (ulong)_MaxPacketId);
        ProtobufEncoder.WriteSint64(ref headerScratch, PbfFieldNumbers.BlockMinTimestamp, _MinTimestamp);
        ProtobufEncoder.WriteSint64(ref headerScratch, PbfFieldNumbers.BlockMaxTimestamp, _MaxTimestamp);

        // Combine header + packet data into _Scratch[1] (payload)
        ref PooledBuffer payloadScratch = ref _Scratch[1];
        payloadScratch.Reset();
        payloadScratch.Write(headerScratch.WrittenSpan);
        payloadScratch.Write(_Buffer.WrittenSpan);

        // Build envelope into _EnvelopeBuffer (block type + length-delimited payload)
        _EnvelopeBuffer.Reset();
        ProtobufEncoder.WriteVarintField(ref _EnvelopeBuffer, PbfFieldNumbers.BlockType, PbfFieldNumbers.BlockTypeStandard);
        ProtobufEncoder.WriteLengthDelimited(ref _EnvelopeBuffer, PbfFieldNumbers.BlockPayload, payloadScratch.WrittenSpan);

        return _EnvelopeBuffer.WrittenSpan;
    }

    /// <summary>
    /// Resets all per-block state for reuse with the next block.
    /// Calls <see cref="PooledBuffer.Reset"/> on the accumulation buffer (no
    /// allocation — the rented array is retained for the next block).
    /// </summary>
    internal void Reset()
    {
        _Buffer.Reset();        // reuse existing array, no ArrayPool rent/return (MEDIUM-2 fix)
        _FieldPresence.Clear();
        _PreviousFields.Reset();
        _PreviousPacketInfo = null;
        _PacketCount = 0;
        _MinPacketId = 0;
        _MaxPacketId = 0;
        _MinTimestamp = 0;
        _MaxTimestamp = 0;
        _TruncatedFieldCount = 0;
    }

    /// <summary>Returns all pre-allocated buffers to the pool.</summary>
    internal void Dispose()
    {
        _Buffer.Return();
        _EnvelopeBuffer.Return();
        foreach (PooledBuffer scratch in _Scratch)
        {
            scratch.Return();
        }
    }

    /// <summary>Returns the field presence bitmap for the trailer.</summary>
    internal ReadOnlySpan<byte> FieldPresenceBytes => _FieldPresence.AsBytes();

    // ========================================================================
    // Private serialization
    // ========================================================================

    /// <summary>
    /// Serializes a single packet into <see cref="_Scratch"/>[0].
    /// Uses depth-indexed scratch buffers for nested field serialization —
    /// no <see cref="PooledBuffer"/> objects are allocated per call (HIGH-1 fix).
    /// </summary>
    private void _SerializePacket(Packet packet)
    {
        ref PooledBuffer buffer = ref _Scratch[0];

        // Packet ID
        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketId, (ulong)packet.Id.Value);

        // Timestamp (zigzag for potentially negative values)
        ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.PacketTimestamp, packet.Timestamp.AsNanos);

        // Info string with same-as-previous
        string info = packet.Info;
        uint packetSameFlags = 0;
        if (info.Length > 0)
        {
            if (_PreviousPacketInfo is not null
                && string.Equals(_PreviousPacketInfo, info, StringComparison.Ordinal))
            {
                packetSameFlags |= SameFlags.PacketSameInfo;
            }
            else
            {
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.PacketInfo, info);
            }
        }
        _PreviousPacketInfo = info;

        // Same flags
        if (packetSameFlags != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketSameFlags, packetSameFlags);
        }

        // Fields (depth 1 for top-level fields, deeper for children).
        // materialize: true — PBF export must include lazy protocol trees.
        Field root = packet.RootField();
        if (root.HasChildren(materialize: true))
        {
            foreach (Field child in root.Children(materialize: true))
            {
                _SerializeField(child, depth: 1);
                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.PacketField, _Scratch[1].WrittenSpan);
            }
        }
    }

    /// <summary>
    /// Serializes a single field recursively using <see cref="_Scratch"/>[<paramref name="depth"/>].
    /// Children are written into <c>_Scratch[depth + 1]</c>, copied into this level's
    /// buffer, then the child buffer is overwritten on the next sibling — zero per-call
    /// allocations for protocol trees with depth &lt; <see cref="_MaxNestingDepth"/>.
    /// </summary>
    private void _SerializeField(Field field, int depth)
    {
        if (depth >= _MaxNestingDepth)
        {
            // Protocol tree is deeper than _MaxNestingDepth; this field and all its
            // descendants are dropped. Count for diagnostic reporting so PbfExporter
            // can surface the truncation via the error tolerance mechanism.
            _TruncatedFieldCount++;
            return;
        }

        ref PooledBuffer buffer = ref _Scratch[depth];
        buffer.Reset();

        int fieldIdValue = field.FieldId.Value;

        // Guard against field IDs outside the configured range. Field IDs >= _MaxFieldId
        // would corrupt the presence bitmap (silently missing from metadata deduplication);
        // throwing here lets the caller's error-tolerance mechanism skip only this field.
        if ((uint)fieldIdValue >= (uint)_MaxFieldId)
        {
            throw new ArgumentOutOfRangeException(nameof(field),
                $"Field ID {fieldIdValue} is outside the PBF field ID range [0, {_MaxFieldId}).");
        }

        // Field ID — always present
        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldFieldId, (ulong)fieldIdValue);

        // Field info dedup: only emit name/ui_name/type on first occurrence in this block
        bool isFirstOccurrence = _FieldPresence.Mark(fieldIdValue);
        if (isFirstOccurrence)
        {
            FieldInfo? info = field.FieldInfo;
            if (info is not null)
            {
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldName, info.Name);
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldUiName, info.UiName);
                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldType, (ulong)info.FieldType);
            }
        }

        // Field value with same-as-previous. Payloads are Core FieldValueData; comparison is
        // typed via FieldValueData.Equals — never formatted through FieldValueFormatter.
        ColumnarDetailFlags extractFlags =
            ColumnarDetailFlags.IncludeCustomRepresentation | ColumnarDetailFlags.IncludeCustomText;
        FieldValue fieldValue = field.Value;
        FieldValueData data = fieldValue.Data;
        bool hasValue = data.Type != FieldType.None;
        string? customRepresentation = FieldValueMaterializer.GetCustomRepresentation(fieldValue, extractFlags);
        string? customText = FieldValueMaterializer.GetCustomText(field, extractFlags);

        uint fieldSameFlags = _PreviousFields.CompareAndUpdate(
            fieldIdValue, hasValue, in data, customRepresentation, customText);

        // Value
        if (hasValue && (fieldSameFlags & SameFlags.FieldSameValue) == 0)
        {
            _WriteFieldValueData(ref buffer, in data);
        }

        // Custom representation text
        if (customRepresentation is not null
            && (fieldSameFlags & SameFlags.FieldSameCustomRepresentation) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValueText, customRepresentation);
        }

        // Custom text
        if (customText is not null
            && (fieldSameFlags & SameFlags.FieldSameCustomText) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldCustomText, customText);
        }

        // Same flags
        if (fieldSameFlags != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldSameFlags, fieldSameFlags);
        }

        // Children (recursive, next depth level).
        // materialize: true — PBF export must include lazy protocol trees.
        if (field.HasChildren(materialize: true) && depth + 1 < _MaxNestingDepth)
        {
            foreach (Field child in field.Children(materialize: true))
            {
                _SerializeField(child, depth + 1);
                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldChild, _Scratch[depth + 1].WrittenSpan);
            }
        }
        else if (field.HasChildren(materialize: true))
        {
            // We are at _MaxNestingDepth - 1: children would exceed the depth limit.
            // Count each such field so PbfExporter can report the truncation.
            _TruncatedFieldCount++;
        }
    }

    /// <summary>Writes a Core <see cref="FieldValueData"/> using the appropriate protobuf encoding.</summary>
    private static void _WriteFieldValueData(ref PooledBuffer buffer, in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.I64:
                data.TryGetAsI64(out long i64Value);
                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, i64Value);
                break;
            case FieldType.U64:
                data.TryGetAsU64(out ulong u64Value);
                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldValue, u64Value);
                break;
            case FieldType.F64:
                data.TryGetAsF64(out double f64Value);
                ProtobufEncoder.WriteDouble(ref buffer, PbfFieldNumbers.FieldValue, f64Value);
                break;
            case FieldType.String:
                if (data.TryGetAsString(out string stringValue))
                {
                    ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValue, stringValue);
                }
                break;
            case FieldType.Bytes:
            case FieldType.MacAddress:
            case FieldType.IPv4Address:
            case FieldType.IPv6Address:
            case FieldType.Eui64:
            case FieldType.Uuid:
                _WriteBytePayload(ref buffer, in data);
                break;
            case FieldType.Bool:
                data.TryGetAsBool(out bool boolValue);
                ProtobufEncoder.WriteBool(ref buffer, PbfFieldNumbers.FieldValue, boolValue);
                break;
            case FieldType.Timestamp:
                data.TryGetAsTimestamp(out Timestamp timestamp);
                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, timestamp.AsNanos);
                break;
            default:
                // FieldType.None is filtered by hasValue above.
                break;
        }
    }

    /// <summary>
    /// Writes bytes / fixed-size address payloads without a heap clone for fixed sizes
    /// (stackalloc scratch). Variable <see cref="FieldType.Bytes"/> still copies via
    /// <see cref="FieldValueMaterializer.ToBytesArray"/> for lifetime safety.
    /// </summary>
    private static void _WriteBytePayload(ref PooledBuffer buffer, in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.Bytes:
                byte[] bytes = FieldValueMaterializer.ToBytesArray(in data) ?? [];
                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, bytes);
                break;
            case FieldType.MacAddress:
                if (data.TryGetAsMacAddress(out MacAddress mac))
                {
                    Span<byte> scratch = stackalloc byte[6];
                    mac.ToBytes(scratch);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, scratch);
                }
                break;
            case FieldType.IPv4Address:
                if (data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    Span<byte> scratch = stackalloc byte[4];
                    ipv4.ToBytes(scratch);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, scratch);
                }
                break;
            case FieldType.IPv6Address:
                if (data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    Span<byte> scratch = stackalloc byte[16];
                    ipv6.ToBytes(scratch);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, scratch);
                }
                break;
            case FieldType.Eui64:
                if (data.TryGetAsEui64(out Eui64 eui64))
                {
                    Span<byte> scratch = stackalloc byte[8];
                    eui64.ToBytes(scratch);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, scratch);
                }
                break;
            case FieldType.Uuid:
                if (data.TryGetAsUuid(out Uuid uuid))
                {
                    Span<byte> scratch = stackalloc byte[16];
                    uuid.ToBytes(scratch);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, scratch);
                }
                break;
        }
    }
}
