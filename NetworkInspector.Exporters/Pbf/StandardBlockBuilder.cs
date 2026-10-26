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
/// <para>
/// <b>Wire layout of a block:</b> the envelope holds the block type (<see cref="PbfFieldNumbers.BlockTypeStandard"/>)
/// and a length-delimited payload. The payload starts with the five header fields (packet count, minimum and maximum
/// packet ID, minimum and maximum timestamp) followed by one length-delimited message per packet. Each packet
/// holds ID, timestamp, optional info, optional same-flags and one nested message per top-level field, which nests
/// its children the same way.
/// </para>
/// <para>
/// <b>Same-as-previous contract:</b> a value, custom representation, custom text or packet info that equals the
/// previously serialized one is omitted and signalled by a bit in the same-flags field. "Previous" means the
/// previous occurrence of the same field ID in serialization order (depth-first, packet by packet) within the same
/// block; <see cref="Reset"/> clears all previous values and the field-info deduplication, so every block can be
/// decoded on its own. A reader must therefore replay fields in the written order and keep one previous value per
/// field ID. The same holds for the name, UI name and type of a field, which are written only for the first
/// occurrence of a field ID in a block.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by <see cref="PbfExporter"/> and used from its single caller thread.
/// The span returned by <see cref="Build"/> aliases an internal buffer and must be consumed before the next call
/// to <see cref="Build"/> or <see cref="Reset"/>.
/// </para>
/// </summary>
internal sealed class StandardBlockBuilder
{
    /// <summary>Accumulated serialized packets of the current block (the payload without header).</summary>
    private PooledBuffer _Buffer;
    private readonly FieldPresence _FieldPresence;

    /// <summary>Field ids newly marked in <see cref="_FieldPresence"/> by the packet being added (undo log of AddPacket).</summary>
    private readonly List<int> _PresenceJournal = new(64);
    private readonly PreviousFieldValueStore _PreviousFields;
    private readonly int _MaxFieldId;
    private string? _PreviousPacketInfo;
    private int _PacketCount;
    private int _MinPacketId;
    private int _MaxPacketId;
    private long _MinTimestamp;
    private long _MaxTimestamp;

    // Flush thresholds: the block is handed back for writing once either one is reached. They bound the memory held
    // by the open block (_Buffer grows to about _MaxBlockSize) and therefore also the size of a block a reader must
    // decompress at once. Supplied by PbfExporter from its builder options (default 50 000 packets / 16 MiB).
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize; // bytes

    /// <summary>
    /// Number of field subtrees dropped in this block because the protocol tree
    /// exceeded <see cref="_MaxNestingDepth"/>. Reset by <see cref="Reset"/>.
    /// Exposed so <see cref="PbfExporter"/> can report the truncation through the error
    /// tolerance mechanism instead of failing silently.
    /// </summary>
    private int _TruncatedFieldCount;

    // Per-depth scratch buffers used during packet/field serialization.
    // Index 0 = packet buffer, 1 = top-level field buffer, 2+ = nested.
    // Pre-allocated once and Reset() between uses — zero per-call allocations
    // for protocol trees with depth ≤ _MaxNestingDepth.
    // Why one buffer per depth: a length-delimited message needs its length before its content, so each nesting level
    // is encoded completely into its own buffer and then copied into the parent. A level can reuse its buffer for the
    // next sibling because the parent has copied the previous one by then. Cost: every byte is copied once per level
    // of nesting; the naive alternative of renting a new PooledBuffer per field would allocate per field instead.
    //
    // Limit: _MaxNestingDepth = 16 levels (the packet buffer at index 0 plus 15 field levels). Threat: unbounded
    // recursion and an unbounded number of scratch buffers for a pathological or malicious capture with a very deep
    // field tree. Constant, not configurable. Posture: secure by default. Exceeding it is not silent: the
    // deeper fields are dropped and counted in _TruncatedFieldCount so the exporter reports it as MalformedData.
    private const int _MaxNestingDepth = 16;
    private readonly PooledBuffer[] _Scratch;

    // Reusable envelope buffer: holds the final serialized block bytes returned
    // from Build(). Reset() before each Build() call so its array is reused rather than rented per block.
    private PooledBuffer _EnvelopeBuffer;

    /// <summary>
    /// Creates a new standard block builder.
    /// </summary>
    /// <param name="maxFieldId">
    /// Exclusive upper bound of the field IDs this builder accepts; <see cref="AddPacket"/> rejects larger IDs.
    /// </param>
    /// <param name="maxPacketsPerBlock">Maximum packets before a flush is triggered.</param>
    /// <param name="maxBlockSize">Maximum serialized block size in bytes before flush.</param>
    /// <remarks>Arguments are not validated: the constructor is internal and <see cref="PbfExporter"/> validates its options.</remarks>
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
    /// <param name="packet">A packet parsed with a field tree.</param>
    /// <returns><see langword="true"/> when the packet count or serialized size threshold has been reached.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="packet"/> has no field tree (skip packet).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field ID is outside <c>[0, maxFieldId)</c>.</exception>
    /// <remarks>
    /// All-or-nothing: when the method throws, the block is as it was before the call. The packet's bytes are not
    /// appended, the block bounds are not widened, and the field-info and same-as-previous state does not keep anything
    /// the discarded bytes would have established. The previous values are cleared rather than restored, so the next
    /// packet is written in full (no same-as-previous flag refers to anything the failed packet touched).
    /// </remarks>
    internal bool AddPacket(Packet packet)
    {
        if (!packet.HasFieldTree)
        {
            throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");
        }

        int packetId = packet.Id.Value;
        long timestamp = packet.Timestamp.AsNanos;

        // Track min/max for block metadata (block header and trailer index let readers skip blocks by ID or time
        // range without decoding them). Timestamps need not be sorted, so both bounds are tracked per packet.
        // The new bounds are computed into locals and applied only after the packet was serialized: a packet that fails
        // must not widen the bounds of a block that does not contain it.
        int newMinPacketId = packetId;
        int newMaxPacketId = packetId;
        long newMinTimestamp = timestamp;
        long newMaxTimestamp = timestamp;
        if (_PacketCount > 0)
        {
            newMinPacketId = Math.Min(packetId, _MinPacketId);
            newMaxPacketId = Math.Max(packetId, _MaxPacketId);
            newMinTimestamp = Math.Min(timestamp, _MinTimestamp);
            newMaxTimestamp = Math.Max(timestamp, _MaxTimestamp);
        }

        // Undo information for a failure part-way through: the diagnostic counter, the length of the block buffer, and the
        // field ids this packet marked as seen (journal, reused list). The previous-value state is reset instead of
        // restored (see below).
        int truncatedBefore = _TruncatedFieldCount;
        int bufferLengthBefore = _Buffer.Length;
        _PresenceJournal.Clear();

        try
        {
            // Serialize the packet using _Scratch[0] as the packet buffer (no allocation).
            _Scratch[0].Reset();
            _SerializePacket(packet);

            // Write as nested message in the block: tag + length + content. The tag reuses the number of PacketField;
            // within the block payload it is distinguished from BlockMaxTimestamp (same number) by its wire type.
            ProtobufEncoder.WriteLengthDelimited(ref _Buffer, PbfFieldNumbers.PacketField, _Scratch[0].WrittenSpan);
        }
        catch
        {
            // A field that was marked as seen while this packet was serialized would never get its name, UI name and type
            // written (the bytes are discarded), yet later packets would leave them out. Unmark those fields.
            foreach (int markedFieldId in _PresenceJournal)
            {
                _FieldPresence.Unmark(markedFieldId);
            }

            // The previous values may include this packet's fields, whose bytes are discarded; a later same-as-previous
            // flag would then point at a value the block does not contain. Forget them: the next packet is written in
            // full. Restoring the old values would need a copy of every touched entry on the per-field hot path.
            _PreviousFields.Reset();
            _PreviousPacketInfo = null;
            _TruncatedFieldCount = truncatedBefore;
            _Buffer.TruncateTo(bufferLengthBefore);
            throw;
        }

        _MinPacketId = newMinPacketId;
        _MaxPacketId = newMaxPacketId;
        _MinTimestamp = newMinTimestamp;
        _MaxTimestamp = newMaxTimestamp;
        _PacketCount++;

        // Check if block should be flushed. The size check uses the serialized (uncompressed) byte count, which is
        // exact and O(1); compression happens later and only shrinks the stored block.
        return _PacketCount >= _MaxPacketsPerBlock || _Buffer.Length >= _MaxBlockSize;
    }

    /// <summary>
    /// Builds the complete block as protobuf-encoded bytes using the block envelope format.
    /// Returns a span that is valid until the next <see cref="Build"/> or <see cref="Reset"/> call.
    /// Uses pre-allocated scratch buffers (_Scratch[0] for header, _Scratch[1] for payload)
    /// and _EnvelopeBuffer for the envelope — no per-call heap allocations once the buffers have grown.
    /// </summary>
    /// <returns>The serialized block, ready to be compressed and framed by the exporter.</returns>
    internal ReadOnlySpan<byte> Build()
    {
        // The header goes in front of the already serialized packets. Because the header contains the final packet
        // count and min/max values it can only be produced now, and protobuf needs the payload length before the
        // payload, so header + packets are first assembled contiguously in the payload scratch buffer and then
        // wrapped in the envelope. This costs two full copies of the block (packets -> payload -> envelope); the
        // alternative of writing the header first would need the final values at the start of the block.
        // The scratch buffers at depth 0 and 1 are free to use here: no packet is being serialized during Build.
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
        // Every block must decode on its own, so all cross-packet state is cleared: field-info deduplication
        // (presence), previous field values and the previous packet info. The accumulation buffer keeps its array
        // (no ArrayPool rent/return per block).
        _Buffer.Reset();
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

    /// <summary>
    /// Returns all pre-allocated buffers to the pool. Idempotent (<see cref="PooledBuffer.Return"/> is). The builder
    /// must not be used afterwards: a later write would lazily rent a fresh array that nothing returns.
    /// </summary>
    internal void Dispose()
    {
        _Buffer.Return();
        _EnvelopeBuffer.Return();
        foreach (PooledBuffer scratch in _Scratch)
        {
            scratch.Return();
        }
    }

    /// <summary>Deepest nesting level that is serialized (level 0 is the packet itself); deeper fields are dropped and counted.</summary>
    internal static int MaxNestingDepth => _MaxNestingDepth;

    /// <summary>Returns the field presence bitmap for the trailer.</summary>
    internal ReadOnlySpan<byte> FieldPresenceBytes => _FieldPresence.AsBytes();

    // ========================================================================
    // Private serialization
    // ========================================================================

    /// <summary>
    /// Serializes a single packet into <see cref="_Scratch"/>[0].
    /// Uses depth-indexed scratch buffers for nested field serialization —
    /// no <see cref="PooledBuffer"/> objects are allocated per call.
    /// </summary>
    private void _SerializePacket(Packet packet)
    {
        ref PooledBuffer buffer = ref _Scratch[0];

        // Packet ID (non-negative, so the plain varint cast keeps it short)
        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketId, (ulong)packet.Id.Value);

        // Timestamp (zigzag for potentially negative values)
        ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.PacketTimestamp, packet.Timestamp.AsNanos);

        // Info string with same-as-previous: a repeat of the previous packet's info is replaced by a flag bit
        // instead of writing the string again. An empty info is simply omitted and, unlike a repeat, carries no flag.
        // The previous info is updated even for an empty one, so an empty info breaks the chain of repeats.
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

        // Fields (depth 1 for top-level fields, deeper for children). Each top-level field is encoded into
        // _Scratch[1] and then copied into the packet buffer as a length-delimited message.
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

        // Guard against field IDs outside the configured range (trust boundary: IDs come from protocol
        // registrations and are bounded by the bitmap sizes of the file format). An ID >= _MaxFieldId could neither
        // be recorded in the block's presence bitmap: its name/type would be repeated in every occurrence and it
        // would be missing from the trailer's field bitmap.
        // Failing loudly was chosen over degrading silently; the exception propagates out of AddPacket, where the
        // exporter's error-tolerance mechanism skips the whole packet (not only this field).
        if ((uint)fieldIdValue >= (uint)_MaxFieldId)
        {
            throw new ArgumentOutOfRangeException(nameof(field),
                $"Field ID {fieldIdValue} is outside the PBF field ID range [0, {_MaxFieldId}).");
        }

        // Field ID — always present
        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldFieldId, (ulong)fieldIdValue);

        // Field info dedup: only emit name/ui_name/type on first occurrence in this block. Names are long strings
        // repeated for every field of every packet, so writing them once per block keeps the block compact and
        // self-describing; readers rebuild the field table per block (Reset clears the presence state).
        bool isFirstOccurrence = _FieldPresence.Mark(fieldIdValue);
        if (isFirstOccurrence)
        {
            // Journal the mark so AddPacket can undo it if this packet fails before its bytes reach the block.
            _PresenceJournal.Add(fieldIdValue);

            FieldInfo? info = field.FieldInfo;
            if (info is not null)
            {
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldName, info.Name);
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldUiName, info.UiName);
                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldType, (ulong)info.FieldType);
            }
        }

        // Field value with same-as-previous. Payloads are Core FieldValueData; comparison is
        // typed via FieldValueData.Equals — never formatted through FieldValueFormatter (formatting every value to
        // text only to compare it would allocate and cost far more than comparing the typed payloads).
        // The custom texts are always extracted here (the Standard format has no detail flags); they are lazy
        // strings, so reading them materializes them.
        ColumnarDetailFlags extractFlags =
            ColumnarDetailFlags.IncludeCustomRepresentation | ColumnarDetailFlags.IncludeCustomText;
        FieldValue fieldValue = field.Value;
        FieldValueData data = fieldValue.Data;
        bool hasValue = data.Type != FieldType.None;
        string? customRepresentation = FieldValueMaterializer.GetCustomRepresentation(fieldValue, extractFlags);
        string? customText = FieldValueMaterializer.GetCustomText(field, extractFlags);

        uint fieldSameFlags = _PreviousFields.CompareAndUpdate(
            fieldIdValue, hasValue, in data, customRepresentation, customText);

        // Value, omitted when it equals the previous occurrence of this field ID (the reader restores it from the
        // flag). Container fields have no value and are never flagged.
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

        // Children (recursive, next depth level): each child is encoded into the next level's scratch buffer
        // and copied into this level's buffer before the next sibling reuses it.
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

    /// <summary>
    /// Writes a Core <see cref="FieldValueData"/> using the appropriate protobuf encoding: signed integers and
    /// timestamps as zigzag varints, unsigned as plain varints, doubles as fixed64, booleans as 0/1 varints,
    /// strings as UTF-8 and bytes/addresses as the raw byte form produced by each type's <c>ToBytes</c>.
    /// The accessor results are intentionally not checked: the type discriminant was just read from the same value,
    /// so the matching accessor cannot fail.
    /// </summary>
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
    /// (stackalloc scratch of the exact size: 6 for MAC, 4 for IPv4, 16 for IPv6, 8 for EUI-64, 16 for UUID).
    /// Variable <see cref="FieldType.Bytes"/> still copies via
    /// <see cref="FieldValueMaterializer.ToBytesArray"/>, which allocates one array per value.
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
