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
    private readonly PreviousFieldStore _PreviousFields;
    private string? _PreviousPacketInfo;
    private int _PacketCount;
    private ulong _MinPacketId;
    private ulong _MaxPacketId;
    private long _MinTimestamp;
    private long _MaxTimestamp;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize; // bytes

    /// <summary>
    /// Number of field subtrees silently dropped in this block because the protocol tree
    /// exceeded <see cref="MaxNestingDepth"/>. Reset by <see cref="Reset"/>.
    /// Exposed so <see cref="PbfExporter"/> can report the truncation through the error
    /// tolerance mechanism instead of failing silently.
    /// </summary>
    private int _TruncatedFieldCount;

    // Per-depth scratch buffers used during packet/field serialization.
    // Index 0 = packet buffer, 1 = top-level field buffer, 2+ = nested.
    // Pre-allocated once and Reset() between uses — zero per-call allocations
    // for protocol trees with depth ≤ MaxNestingDepth (HIGH-1 fix).
    private const int MaxNestingDepth = 16;
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
        _Buffer = new PooledBuffer(64 * 1024);
        _FieldPresence = new FieldPresence(maxFieldId);
        _PreviousFields = new PreviousFieldStore(maxFieldId);
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;

        // Pre-allocate per-depth scratch buffers. Depth 0 = packet, 1 = top-level
        // field, 2..MaxNestingDepth-1 = nested children. Each buffer starts small
        // and grows on demand; after warm-up they reach steady state.
        _Scratch = new PooledBuffer[MaxNestingDepth];
        for (int i = 0; i < MaxNestingDepth; i++)
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

    /// <summary>Minimum packet ID in this block.</summary>
    internal ulong MinPacketId => _MinPacketId;

    /// <summary>Maximum packet ID in this block.</summary>
    internal ulong MaxPacketId => _MaxPacketId;

    /// <summary>Minimum timestamp (nanos) in this block.</summary>
    internal long MinTimestamp => _MinTimestamp;

    /// <summary>Maximum timestamp (nanos) in this block.</summary>
    internal long MaxTimestamp => _MaxTimestamp;

    /// <summary>
    /// Number of field subtrees dropped because the protocol tree exceeded
    /// <see cref="MaxNestingDepth"/> during this block's serialization.
    /// Reset to zero by <see cref="Reset"/>.
    /// </summary>
    internal int TruncatedFieldCount => _TruncatedFieldCount;

    /// <summary>
    /// Adds a packet to the block. Returns <c>true</c> if the block should be flushed
    /// after this packet (threshold reached).
    /// </summary>
    internal bool AddPacket(Packet packet)
    {
        ulong packetId = (ulong)packet.Id.Value;
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
        SerializePacket(packet);

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
        ProtobufEncoder.WriteVarintField(ref headerScratch, PbfFieldNumbers.BlockMinPacketId, _MinPacketId);
        ProtobufEncoder.WriteVarintField(ref headerScratch, PbfFieldNumbers.BlockMaxPacketId, _MaxPacketId);
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
    private void SerializePacket(Packet packet)
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

        // Fields (depth 1 for top-level fields, deeper for children)
        Field root = packet.RootField();
        if (root.HasChildren)
        {
            foreach (Field child in root.Children())
            {
                SerializeField(child, depth: 1);
                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.PacketField, _Scratch[1].WrittenSpan);
            }
        }
    }

    /// <summary>
    /// Serializes a single field recursively using <see cref="_Scratch"/>[<paramref name="depth"/>].
    /// Children are written into <c>_Scratch[depth + 1]</c>, copied into this level's
    /// buffer, then the child buffer is overwritten on the next sibling — zero per-call
    /// allocations for protocol trees with depth &lt; <see cref="MaxNestingDepth"/>.
    /// </summary>
    private void SerializeField(Field field, int depth)
    {
        if (depth >= MaxNestingDepth)
        {
            // Protocol tree is deeper than MaxNestingDepth; this field and all its
            // descendants are dropped. Count for diagnostic reporting so PbfExporter
            // can surface the truncation via the error tolerance mechanism.
            _TruncatedFieldCount++;
            return;
        }

        ref PooledBuffer buffer = ref _Scratch[depth];
        buffer.Reset();

        int fieldIdValue = field.FieldId.Value;

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

        // Field value with same-as-previous
        FieldValue value = field.Value;
        string? valueStr = FormatFieldValue(value);
        string? valueCustomRepresentation = !value.CustomRepresentation.IsNull
            ? value.CustomRepresentation.AsString : null;
        LazyString customText = field.CustomText;
        string? customTextStr = !customText.IsNull ? customText.AsString : null;

        uint fieldSameFlags = _PreviousFields.CompareAndUpdate(
            fieldIdValue, valueStr, valueCustomRepresentation, customTextStr);

        // Value
        if (value.Type != FieldType.None && (fieldSameFlags & SameFlags.FieldSameValue) == 0)
        {
            WriteFieldValue(ref buffer, value);
        }

        // Custom representation text
        if (valueCustomRepresentation is not null && (fieldSameFlags & SameFlags.FieldSameCustomRepresentation) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValueText, valueCustomRepresentation);
        }

        // Custom text
        if (customTextStr is not null && (fieldSameFlags & SameFlags.FieldSameCustomText) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldCustomText, customTextStr);
        }

        // Same flags
        if (fieldSameFlags != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldSameFlags, fieldSameFlags);
        }

        // Children (recursive, next depth level)
        if (field.HasChildren && depth + 1 < MaxNestingDepth)
        {
            foreach (Field child in field.Children())
            {
                SerializeField(child, depth + 1);
                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldChild, _Scratch[depth + 1].WrittenSpan);
            }
        }
        else if (field.HasChildren)
        {
            // We are at MaxNestingDepth - 1: children would exceed the depth limit.
            // Count each such field so PbfExporter can report the truncation.
            _TruncatedFieldCount++;
        }
    }

    /// <summary>Writes the field value using the appropriate protobuf encoding.</summary>
    private static void WriteFieldValue(ref PooledBuffer buffer, FieldValue value)
    {
        switch (value.Type)
        {
            case FieldType.I64:
                if (!value.Data.TryGetAsI64(out long i64))
                {
                    break;
                }
                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, i64);
                break;
            case FieldType.U64:
                if (!value.Data.TryGetAsU64(out ulong u64))
                {
                    break;
                }
                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldValue, u64);
                break;
            case FieldType.F64:
                if (!value.Data.TryGetAsF64(out double f64))
                {
                    break;
                }
                ProtobufEncoder.WriteDouble(ref buffer, PbfFieldNumbers.FieldValue, f64);
                break;
            case FieldType.String:
                if (!value.Data.TryGetAsString(out string str))
                {
                    break;
                }
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValue, str);
                break;
            case FieldType.Bytes:
                {
                    if (!value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytesVal))
                    {
                        break;
                    }
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, bytesVal.Span);
                    break;
                }
            case FieldType.MacAddress:
                {
                    if (!value.Data.TryGetAsMacAddress(out MacAddress mac))
                    {
                        break;
                    }
                    Span<byte> macBytes = stackalloc byte[6];
                    mac.ToBytes(macBytes);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, macBytes);
                    break;
                }
            case FieldType.IPv4Address:
                {
                    if (!value.Data.TryGetAsIPv4(out IPv4Address ipv4))
                    {
                        break;
                    }
                    Span<byte> ip = stackalloc byte[4];
                    ipv4.ToBytes(ip);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, ip);
                    break;
                }
            case FieldType.Bool:
                if (!value.Data.TryGetAsBool(out bool boolVal))
                {
                    break;
                }
                ProtobufEncoder.WriteBool(ref buffer, PbfFieldNumbers.FieldValue, boolVal);
                break;
            case FieldType.Timestamp:
                if (!value.Data.TryGetAsTimestamp(out Timestamp ts))
                {
                    break;
                }
                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, ts.AsNanos);
                break;
            case FieldType.IPv6Address:
                {
                    if (!value.Data.TryGetAsIPv6(out IPv6Address ipv6))
                    {
                        break;
                    }
                    Span<byte> ip6 = stackalloc byte[16];
                    ipv6.ToBytes(ip6);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, ip6);
                    break;
                }
            case FieldType.Eui64:
                {
                    if (!value.Data.TryGetAsEui64(out Eui64 eui))
                    {
                        break;
                    }
                    Span<byte> euiBytes = stackalloc byte[8];
                    eui.ToBytes(euiBytes);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, euiBytes);
                    break;
                }
            case FieldType.Uuid:
                {
                    if (!value.Data.TryGetAsUuid(out Uuid uuid))
                    {
                        break;
                    }
                    Span<byte> uuidBytes = stackalloc byte[16];
                    uuid.ToBytes(uuidBytes);
                    ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, uuidBytes);
                    break;
                }
            default:
                // FieldType.None — container/grouping field carries no value.
                break;
        }
    }

    /// <summary>Formats a field value as string for same-as-previous comparison.</summary>
    private static string? FormatFieldValue(FieldValue value) => FieldValueFormatter.Format(value);
}
