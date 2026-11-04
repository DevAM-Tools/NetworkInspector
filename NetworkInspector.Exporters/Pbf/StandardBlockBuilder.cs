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
/// holds ID, timestamp, optional info, optional same-flags, the optional frame bytes
/// (<see cref="PbfFieldNumbers.PacketFrameBytes"/>), the optional additional buffers (one
/// <see cref="PbfFieldNumbers.PacketBuffer"/> record each), and one nested message per top-level field, which nests its children the
/// same way.
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
/// <b>Text presence:</b> a custom representation (<see cref="PbfFieldNumbers.FieldValueText"/>) and a custom text
/// (<see cref="PbfFieldNumbers.FieldCustomText"/>) are written only when the dissector set one; an empty text is a zero-length
/// field, so a reader tells "set but empty" from "absent" by the field's presence. A text that equals the previous occurrence's is
/// omitted and signalled by its same-flag, never an absent one: an absent text is never flagged.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one thread at a time. The caller's thread fills the block; in
/// <see cref="ExportWriteMode.Background"/> the writer thread then calls <see cref="Build"/> and <see cref="Reset"/> and hands the
/// block back. The hand-off of <see cref="BatchWriterPipeline{TBatch}"/> is the memory barrier between the two (see
/// <see cref="IExportBatch"/>); no member may keep state that belongs to one specific thread.
/// The span returned by <see cref="Build"/> aliases an internal buffer and must be consumed before the next call
/// to <see cref="Build"/> or <see cref="Reset"/>.
/// </para>
/// </summary>
internal sealed class StandardBlockBuilder
{
    #region Configuration and state

    /// <summary>Accumulated serialized packets of the current block (the payload without header).</summary>
    private PooledBuffer _Buffer;

    /// <summary>Field ids already described (name, UI name, type) in the open block; cleared with every block.</summary>
    private readonly FieldPresence _FieldPresence;

    /// <summary>Field ids newly marked in <see cref="_FieldPresence"/> by the packet being added (undo log of AddPacket).</summary>
    private readonly List<int> _PresenceJournal = new(64);

    /// <summary>
    /// Same-as-previous state of the field values. Created by the first packet and sized by its stack, so a small stack takes the
    /// dense array path of the store; ids beyond the first stack's field count still compare correctly through its dictionary.
    /// </summary>
    private PreviousFieldValueStore? _PreviousFields;

    /// <summary>Exclusive upper bound of the field ids this builder accepts (the size of the presence bitmap).</summary>
    private readonly int _MaxFieldId;

    // The content members are read once into booleans: the serializer tests them per packet and per field.

    /// <summary>Whether the packet info text is written (and read from the packet at all).</summary>
    private readonly bool _IncludeInfo;

    /// <summary>Whether the raw frame bytes of every packet are written as the packet field <see cref="PbfFieldNumbers.PacketFrameBytes"/>.</summary>
    private readonly bool _IncludeFrameBytes;

    /// <summary>Whether the additional packet buffers are written as the repeated packet field <see cref="PbfFieldNumbers.PacketBuffer"/>.</summary>
    private readonly bool _IncludeBuffers;

    /// <summary>Whether each value's custom representation is written.</summary>
    private readonly bool _IncludeCustomRepresentation;

    /// <summary>Whether each field's custom text is written.</summary>
    private readonly bool _IncludeCustomText;

    /// <summary>Info text of the previously serialized packet of the block, for the packet's same-as-previous flag; null at the start.</summary>
    private string? _PreviousPacketInfo;

    /// <summary>Number of packets in the open block.</summary>
    private int _PacketCount;

    /// <summary>Smallest packet id in the open block; meaningful when <see cref="_PacketCount"/> is above 0.</summary>
    private int _MinPacketId;

    /// <summary>Largest packet id in the open block; meaningful when <see cref="_PacketCount"/> is above 0.</summary>
    private int _MaxPacketId;

    /// <summary>Smallest packet timestamp (nanoseconds) in the open block; meaningful when <see cref="_PacketCount"/> is above 0.</summary>
    private long _MinTimestamp;

    /// <summary>Largest packet timestamp (nanoseconds) in the open block; meaningful when <see cref="_PacketCount"/> is above 0.</summary>
    private long _MaxTimestamp;

    /// <summary>
    /// Flush threshold by packet count: the block is handed back for writing once either threshold is reached. The thresholds
    /// bound the memory held by the open block (<see cref="_Buffer"/> grows to about <see cref="_MaxBlockSize"/>) and therefore
    /// also the size of a block a reader must decompress at once. Supplied by <see cref="PbfExporter"/> from its builder options
    /// (default 50 000 packets / 16 MiB, see <see cref="BatchLimits"/>).
    /// </summary>
    private readonly int _MaxPacketsPerBlock;

    /// <summary>Flush threshold by serialized block size in bytes; see <see cref="_MaxPacketsPerBlock"/>.</summary>
    private readonly long _MaxBlockSize;

    /// <summary>
    /// Number of field subtrees dropped in this block because the protocol tree
    /// exceeded <see cref="_MaxNestingDepth"/>. Reset by <see cref="Reset"/>.
    /// Exposed so <see cref="PbfExporter"/> can report the truncation through the error
    /// tolerance mechanism instead of failing silently.
    /// </summary>
    private int _TruncatedFieldCount;

    /// <summary>
    /// Deepest nesting level that is serialized (level 0 is the packet itself).
    /// <para>
    /// Per-depth scratch buffers are used during packet/field serialization: index 0 = packet buffer, 1 = top-level field buffer,
    /// 2+ = nested. They are pre-allocated once and reset between uses — zero per-call allocations for protocol trees with depth
    /// below this constant. Why one buffer per depth: a length-delimited message needs its length before its content, so each
    /// nesting level is encoded completely into its own buffer and then copied into the parent. A level can reuse its buffer for
    /// the next sibling because the parent has copied the previous one by then. Cost: every byte is copied once per level of
    /// nesting; the naive alternative of renting a new <see cref="PooledBuffer"/> per field would allocate per field instead.
    /// </para>
    /// <para>
    /// Limit: 16 levels (the packet buffer at index 0 plus 15 field levels). Threat: unbounded recursion and an unbounded number
    /// of scratch buffers for a pathological or malicious capture with a very deep field tree. Constant, not configurable.
    /// Posture: secure by default. Exceeding it is not silent: the deeper fields are dropped and counted in
    /// <see cref="_TruncatedFieldCount"/> so the exporter reports it as MalformedData.
    /// </para>
    /// </summary>
    private const int _MaxNestingDepth = 16;

    /// <summary>The per-depth scratch buffers described at <see cref="_MaxNestingDepth"/>; index 0 holds the packet being serialized.</summary>
    private readonly PooledBuffer[] _Scratch;

    /// <summary>
    /// Reusable envelope buffer: holds the final serialized block bytes returned from <see cref="Build"/>. Reset before each
    /// <see cref="Build"/> call so its array is reused rather than rented per block.
    /// </summary>
    private PooledBuffer _EnvelopeBuffer;

    /// <summary>
    /// Creates a new standard block builder.
    /// </summary>
    /// <param name="maxFieldId">
    /// Exclusive upper bound of the field IDs this builder accepts; <see cref="AddPacket"/> rejects larger IDs.
    /// </param>
    /// <param name="content">Validated export content; decides the optional info, frame bytes, custom representation, and custom text.</param>
    /// <param name="maxPacketsPerBlock">Maximum packets before a flush is triggered.</param>
    /// <param name="maxBlockSize">Maximum serialized block size in bytes before flush.</param>
    /// <remarks>Arguments are not validated: the constructor is internal and <see cref="PbfExporter"/> validates its options.</remarks>
    internal StandardBlockBuilder(
        int maxFieldId,
        ExportContent content,
        int maxPacketsPerBlock = BatchLimits.DefaultPacketsPerBatch,
        long maxBlockSize = BatchLimits.DefaultBytesPerBatch)
    {
        ArgumentNullException.ThrowIfNull(content);
        _MaxFieldId = maxFieldId;
        _IncludeInfo = content.Info;
        _IncludeFrameBytes = content.FrameBytes;
        _IncludeBuffers = content.Buffers;
        _IncludeCustomRepresentation = content.CustomRepresentation;
        _IncludeCustomText = content.CustomText;
        _Buffer = new PooledBuffer(64 * 1024);
        _FieldPresence = new FieldPresence(maxFieldId);
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

    #endregion

    #region Packet intake

    /// <summary>
    /// Adds a packet to the block. Returns <c>true</c> if the block should be flushed
    /// after this packet (threshold reached).
    /// </summary>
    /// <param name="packet">A packet parsed with a field tree.</param>
    /// <returns><see langword="true"/> when the packet count or serialized size threshold has been reached.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="packet"/> has no field tree (skip packet).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field ID is outside <c>[0, maxFieldId)</c>.</exception>
    /// <remarks>
    /// All-or-nothing: when the method throws, the block is unchanged by the call. The packet's bytes are not
    /// appended, the block bounds are not widened, and the field-info and same-as-previous state does not keep anything
    /// the discarded bytes would have established. The previous values are cleared rather than restored, so the next
    /// packet is written in full (no same-as-previous flag refers to anything the failed packet touched).
    /// </remarks>
    internal bool AddPacket(Packet packet)
    {
        if (!packet.HasFieldTree)
        {
            _ThrowSkipPacket();
        }

        return _Append(packet, stage: null);
    }

    /// <summary>
    /// A packet without a field tree reached the tree path: the exporter refuses skip packets before the builder, so this is a
    /// broken invariant, reported from a cold helper (the message every exporter with a hierarchy shape reports).
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowSkipPacket() =>
        throw new InvalidOperationException("Skip packets cannot be exported (HasFieldTree is false).");

    /// <summary>
    /// Appends one packet of a flat shape from the committed stage: packet id, timestamp, optional info, then one field record per
    /// staged entry in arrival order (field id, occurrence, value, texts). Same all-or-nothing contract, presence journal, and
    /// same-as-previous compression as <see cref="AddPacket"/>: the previous-value state is keyed by field id, so repeated
    /// occurrences compress against each other the same way repeated packets do.
    /// </summary>
    /// <param name="packet">The committed packet (for id, timestamp, info, and field names on first presence).</param>
    /// <param name="stage">The committed stage; its entries belong to <paramref name="packet"/>.</param>
    /// <returns><see langword="true"/> when the block reached a flush bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A field ID is outside <c>[0, maxFieldId)</c>.</exception>
    internal bool AddStagedPacket(Packet packet, RowFieldStage stage)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(stage);
        return _Append(packet, stage);
    }

    /// <summary>
    /// The shared body of both ways in: the block bounds, the undo journal, the all-or-nothing rollback, and the flush check. Only
    /// the serialization of the packet differs (<paramref name="stage"/> is <see langword="null"/> for the field tree).
    /// </summary>
    private bool _Append(Packet packet, RowFieldStage? stage)
    {
        int packetId = packet.Id.Value;
        long timestamp = packet.Timestamp.AsNanos;

        // Perf: the previous-value store is sized by the stack of the first packet (once per export), capped at the field id
        // range this builder accepts; passing the id limit instead would exceed the store's dense threshold and send every field
        // through the dictionary path.
        _PreviousFields ??= new PreviousFieldValueStore(Math.Min(packet.Stack.FieldCount, _MaxFieldId));

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
            if (stage is null)
            {
                _SerializePacket(packet);
            }
            else
            {
                _SerializeStagedPacket(packet, stage);
            }

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
            _PreviousFields?.Reset();
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

    #endregion

    #region Block assembly

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
        _PreviousFields?.Reset();
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

    #endregion

    #region Record serialization

    /// <summary>
    /// Serializes a single packet into <see cref="_Scratch"/>[0].
    /// Uses depth-indexed scratch buffers for nested field serialization —
    /// no <see cref="PooledBuffer"/> objects are allocated per call.
    /// </summary>
    /// <param name="packet">A packet with a field tree; its fields are materialized (lazy protocol trees included).</param>
    /// <exception cref="ArgumentOutOfRangeException">A field ID is outside <c>[0, maxFieldId)</c>.</exception>
    private void _SerializePacket(Packet packet)
    {
        ref PooledBuffer buffer = ref _Scratch[0];
        _WritePacketHeader(ref buffer, packet);

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
    /// Serializes a packet of a flat shape into <see cref="_Scratch"/>[0]: the packet header of the tree path, then one flat field
    /// record per staged entry, in arrival order. There are no nested records, so no depth limit applies.
    /// </summary>
    /// <param name="packet">The committed packet (for id, timestamp, info, and field names on first presence).</param>
    /// <param name="stage">The committed stage; its entries belong to <paramref name="packet"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A field ID is outside <c>[0, maxFieldId)</c>.</exception>
    private void _SerializeStagedPacket(Packet packet, RowFieldStage stage)
    {
        ref PooledBuffer buffer = ref _Scratch[0];
        _WritePacketHeader(ref buffer, packet);

        foreach (ref readonly RowFieldStage.Entry entry in stage.Entries)
        {
            _WriteFlatField(ref _Scratch[1], packet.Stack, stage, in entry);
            ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.PacketField, _Scratch[1].WrittenSpan);
        }
    }

    /// <summary>
    /// Writes the packet-level part both ways in share: id, timestamp, the optional info with its same-as-previous flag, the
    /// packet same flags, and the optional frame bytes.
    /// </summary>
    /// <param name="buffer">The packet buffer (<see cref="_Scratch"/>[0]) the header fields are appended to.</param>
    /// <param name="packet">The packet whose id, timestamp, and (when the content asks for them) info and frame bytes are written.</param>
    private void _WritePacketHeader(ref PooledBuffer buffer, Packet packet)
    {
        // Packet ID (non-negative, so the plain varint cast keeps it short)
        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketId, (ulong)packet.Id.Value);

        // Timestamp (zigzag for potentially negative values)
        ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.PacketTimestamp, packet.Timestamp.AsNanos);

        // Info string with same-as-previous: a repeat of the previous packet's info is replaced by a flag bit
        // instead of writing the string again. An empty info is simply omitted and, unlike a repeat, carries no flag.
        // The previous info is updated even for an empty one, so an empty info breaks the chain of repeats. A content without
        // info neither reads nor writes it (reading it would cost the summary line of every packet for nothing).
        uint packetSameFlags = 0;
        if (_IncludeInfo)
        {
            string info = packet.Info;
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
        }

        // Same flags
        if (packetSameFlags != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketSameFlags, packetSameFlags);
        }

        // Frame bytes: opt-in, because the field tree already holds the parsed bytes and the copy would roughly double a default
        // export. The bytes are encoded into the packet buffer here, inside AddPacket, because the packet's buffers are recycled
        // after OnPacket returns. No same-as-previous rule: frames of consecutive packets are practically never equal, and a
        // comparison would cost a pass over every frame. An empty frame is a zero-length field (the field is present in every
        // record of an export with frames), so a reader never has to guess between "empty" and "not exported".
        if (_IncludeFrameBytes)
        {
            ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.PacketFrameBytes, packet.Frame.Data.Span);
        }

        if (_IncludeBuffers)
        {
            _WritePacketBuffers(ref buffer, packet);
        }
    }

    /// <summary>
    /// Writes the additional buffers of <paramref name="packet"/> (index 1 up to <see cref="Packet.BufferCount"/> - 1) as one repeated
    /// packet field <see cref="PbfFieldNumbers.PacketBuffer"/> per buffer: a sub-message with the index and the bytes. A packet
    /// without additional buffers writes nothing; a bound empty buffer writes a record with zero-length bytes.
    /// </summary>
    /// <param name="buffer">The packet buffer (<see cref="_Scratch"/>[0]) the records are appended to.</param>
    /// <param name="packet">The packet whose buffers are encoded.</param>
    /// <exception cref="InvalidOperationException">
    /// A buffer inside the packet's own buffer count is missing (broken invariant); <see cref="_Append"/> rolls the block back.
    /// </exception>
    /// <remarks>
    /// The bytes are encoded into the block here, inside <c>AddPacket</c>, because the packet's buffers are recycled after
    /// <c>OnPacket</c> returns. Perf: the length of the sub-message is computed from the varint sizes instead of encoding the record
    /// into a scratch buffer first, so a large buffer is copied once, straight into the packet buffer. No same-as-previous rule: the
    /// buffers of consecutive packets are practically never equal.
    /// </remarks>
    private static void _WritePacketBuffers(ref PooledBuffer buffer, Packet packet)
    {
        int bufferCount = packet.BufferCount;
        for (int index = 1; index < bufferCount; index++)
        {
            ReadOnlySpan<byte> bytes = PacketBuffers.Get(packet, index);

            // Sub-message length = index field (tag 1 byte + varint) + bytes field (tag 1 byte + length varint + the bytes). The
            // tags are one byte because the sub-field numbers are below 16. Sync: ProtobufEncoder.VarintSize must match the
            // varints written below, which the PBF tests prove by decoding the records.
            int length = 1 + ProtobufEncoder.VarintSize((ulong)index) + 1 + ProtobufEncoder.VarintSize((ulong)bytes.Length) + bytes.Length;
            ProtobufEncoder.WriteTag(ref buffer, PbfFieldNumbers.PacketBuffer, 2);
            ProtobufEncoder.WriteVarint(ref buffer, (ulong)length);
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.PacketBufferIndex, (ulong)index);
            ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.PacketBufferBytes, bytes);
        }
    }

    /// <summary>
    /// Writes one flat field record into <paramref name="buffer"/>: field id, name / UI name / type on the first presence in the
    /// block (the same dedup as the tree path), the occurrence when it is not 0, the value, and the texts. Values and texts use the
    /// same encoders and the same same-as-previous state as the tree path, so a reader decodes a value the same way in every shape.
    /// </summary>
    /// <param name="buffer">The record buffer (<see cref="_Scratch"/>[1]); reset at entry and filled with one field record.</param>
    /// <param name="stack">The stack of the packet; names the field on its first presence in the block.</param>
    /// <param name="stage">The committed stage that owns the bytes of a <see cref="FieldType.Bytes"/> entry.</param>
    /// <param name="entry">The staged entry to write.</param>
    /// <exception cref="ArgumentOutOfRangeException">The field ID is outside <c>[0, maxFieldId)</c>.</exception>
    private void _WriteFlatField(ref PooledBuffer buffer, Stack stack, RowFieldStage stage, in RowFieldStage.Entry entry)
    {
        buffer.Reset();
        int fieldIdValue = entry.FieldId;

        // Same trust-boundary guard as the tree path: an id beyond the bitmaps could not be recorded in the presence state.
        if ((uint)fieldIdValue >= (uint)_MaxFieldId)
        {
            throw new ArgumentOutOfRangeException(nameof(entry),
                $"Field ID {fieldIdValue} is outside the PBF field ID range [0, {_MaxFieldId}).");
        }

        ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldFieldId, (ulong)fieldIdValue);
        if (_FieldPresence.Mark(fieldIdValue))
        {
            // Journal the mark so _Append can undo it if this packet fails before its bytes reach the block.
            _PresenceJournal.Add(fieldIdValue);

            FieldInfo? info = stack.GetField(new FieldId(fieldIdValue));
            if (info is not null)
            {
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldName, info.Name);
                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldUiName, info.UiName);
                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldType, (ulong)info.FieldType);
            }
        }

        // Occurrence 0 is the common case and is omitted (the protobuf default), so a two-field selection costs no extra byte per
        // value; a reader treats an absent occurrence as 0.
        if (entry.Occurrence != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldOccurrence, (ulong)entry.Occurrence);
        }

        // A bytes payload lives in the stage's arena; the comparison and the encoder take a value, so the arena range is wrapped as
        // memory (scalars, strings, and addresses are inline in the entry). Perf: no array per value. The range stays valid until
        // the stage's next BeginPacket, which is after this record is written, and the previous-value store copies the bytes into
        // its own array before then.
        FieldValueData data = entry.Value;
        if (entry.Type == FieldType.Bytes)
        {
            data = FieldValueData.NewBytes(stage.GetBytesMemory(in entry));
        }

        string? customRepresentation = entry.Representation;
        string? customText = entry.Text;
        uint fieldSameFlags = _PreviousFields!.CompareAndUpdate( // Invariant: _Append created the store before any field is written.
            fieldIdValue, hasValue: true, in data, customRepresentation, customText);

        if ((fieldSameFlags & SameFlags.FieldSameValue) == 0)
        {
            _WriteFieldValueData(ref buffer, fieldIdValue, in data);
        }

        if (customRepresentation is not null && (fieldSameFlags & SameFlags.FieldSameCustomRepresentation) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValueText, customRepresentation);
        }

        if (customText is not null && (fieldSameFlags & SameFlags.FieldSameCustomText) == 0)
        {
            ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldCustomText, customText);
        }

        if (fieldSameFlags != 0)
        {
            ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldSameFlags, fieldSameFlags);
        }
    }

    /// <summary>
    /// Serializes a single field recursively using <see cref="_Scratch"/>[<paramref name="depth"/>].
    /// Children are written into <c>_Scratch[depth + 1]</c>, copied into this level's
    /// buffer, then the child buffer is overwritten on the next sibling — zero per-call
    /// allocations for protocol trees with depth &lt; <see cref="_MaxNestingDepth"/>.
    /// </summary>
    /// <param name="field">The field to serialize with its children.</param>
    /// <param name="depth">Nesting level of the field (1 for a top-level field); a depth at the limit drops the field and counts it.</param>
    /// <exception cref="ArgumentOutOfRangeException">The field ID is outside <c>[0, maxFieldId)</c>.</exception>
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
        // The custom texts are extracted only when the content asks for them (a cleared flag costs nothing); they are lazy
        // strings, so reading them materializes them.

        FieldValue fieldValue = field.Value;
        FieldValueData data = fieldValue.Data;
        bool hasValue = data.Type != FieldType.None;
        string? customRepresentation = OptionalTextReader.GetCustomRepresentation(fieldValue, _IncludeCustomRepresentation);
        string? customText = OptionalTextReader.GetCustomText(field, _IncludeCustomText);

        uint fieldSameFlags = _PreviousFields!.CompareAndUpdate( // Invariant: _Append created the store before any field is written.
            fieldIdValue, hasValue, in data, customRepresentation, customText);

        // Value, omitted when it equals the previous occurrence of this field ID (the reader restores it from the
        // flag). Container fields have no value and are never flagged.
        if (hasValue && (fieldSameFlags & SameFlags.FieldSameValue) == 0)
        {
            _WriteFieldValueData(ref buffer, fieldIdValue, in data);
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

    #endregion

    #region Value encoding

    /// <summary>
    /// Writes a Core <see cref="FieldValueData"/> using the appropriate protobuf encoding: signed integers and
    /// timestamps as zigzag varints, unsigned as plain varints, doubles as fixed64, booleans as 0/1 varints,
    /// strings as UTF-8, bytes as length-delimited, and the address types as integers (see
    /// <see cref="_WriteValuePayload"/>). Every typed accessor result is checked: the type discriminant was just read from the same
    /// value, so a failing accessor is a broken invariant and is reported, never written as a missing or zero value.
    /// </summary>
    /// <param name="buffer">The field record the value is appended to.</param>
    /// <param name="fieldIdValue">The field id of the record; names the field in a broken-invariant message.</param>
    /// <param name="data">The value; a <see cref="FieldType.None"/> value writes nothing.</param>
    /// <exception cref="InvalidOperationException">A value whose typed accessor fails (a broken invariant).</exception>
    private static void _WriteFieldValueData(ref PooledBuffer buffer, int fieldIdValue, in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.I64:
                if (!data.TryGetAsI64(out long i64Value))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.I64, data.Type);
                }

                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, i64Value);
                break;
            case FieldType.U64:
                if (!data.TryGetAsU64(out ulong u64Value))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.U64, data.Type);
                }

                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldValue, u64Value);
                break;
            case FieldType.F64:
                if (!data.TryGetAsF64(out double f64Value))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.F64, data.Type);
                }

                ProtobufEncoder.WriteDouble(ref buffer, PbfFieldNumbers.FieldValue, f64Value);
                break;
            case FieldType.String:
                if (!data.TryGetAsString(out string stringValue))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.String, data.Type);
                }

                ProtobufEncoder.WriteString(ref buffer, PbfFieldNumbers.FieldValue, stringValue);
                break;
            case FieldType.Bytes:
            case FieldType.MacAddress:
            case FieldType.IPv4Address:
            case FieldType.IPv6Address:
            case FieldType.Eui64:
            case FieldType.Uuid:
                _WriteValuePayload(ref buffer, fieldIdValue, in data);
                break;
            case FieldType.Bool:
                if (!data.TryGetAsBool(out bool boolValue))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.Bool, data.Type);
                }

                ProtobufEncoder.WriteBool(ref buffer, PbfFieldNumbers.FieldValue, boolValue);
                break;
            case FieldType.Timestamp:
                if (!data.TryGetAsTimestamp(out Timestamp timestamp))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.Timestamp, data.Type);
                }

                ProtobufEncoder.WriteSint64(ref buffer, PbfFieldNumbers.FieldValue, timestamp.AsNanos);
                break;
            default:
                // FieldType.None is filtered by hasValue above.
                break;
        }
    }

    /// <summary>
    /// Writes the value of a <see cref="FieldType.Bytes"/> or address field into the field record. Bytes are written straight from
    /// the value's memory (no array per value; the encoder's copy into the block buffer is the one copy the block needs). The
    /// addresses are written as integers, whose numeric order is the address order:
    /// <list type="table">
    ///   <item><term>IPv4</term><description>field 5 fixed32 (<see cref="IPv4Address.RawValue"/>)</description></item>
    ///   <item><term>MAC</term><description>field 5 varint (the 48-bit <see cref="MacAddress.RawValue"/>)</description></item>
    ///   <item><term>EUI-64</term><description>field 5 fixed64 (<see cref="Eui64.RawValue"/>)</description></item>
    ///   <item><term>IPv6, UUID</term><description>field 5 fixed64 with the high 64 bits and field 11 fixed64 with the low 64 bits</description></item>
    /// </list>
    /// </summary>
    /// <param name="buffer">The field record the payload is appended to.</param>
    /// <param name="fieldIdValue">The field id of the record; names the field in a broken-invariant message.</param>
    /// <param name="data">A bytes or address value.</param>
    /// <exception cref="InvalidOperationException">An address value whose typed accessor fails (a broken invariant).</exception>
    /// <remarks>
    /// Perf: no scratch buffer and no array; each integer is written with the encoder's fixed-width methods. Why integers and why
    /// these wire types: <c>docs/decisions/export-integer-addresses.md</c>. Rejected alternative for MAC: fixed64, which is larger
    /// than the length-delimited 6 bytes it replaces. The same-as-previous flag of the field covers both IPv6 and UUID halves, so
    /// they are written or omitted together (the caller decides).
    /// Sync: <see cref="PbfFieldNumbers.FieldValue"/> and the PBF README § Field values.
    /// </remarks>
    private static void _WriteValuePayload(ref PooledBuffer buffer, int fieldIdValue, in FieldValueData data)
    {
        switch (data.Type)
        {
            case FieldType.Bytes:
                // A value that is no byte payload is written as an empty payload.
                ReadOnlySpan<byte> bytes = ReadOnlySpan<byte>.Empty;
                if (data.TryGetAsBytes(out ReadOnlyMemory<byte> memory))
                {
                    bytes = memory.Span;
                }

                ProtobufEncoder.WriteLengthDelimited(ref buffer, PbfFieldNumbers.FieldValue, bytes);
                break;
            case FieldType.MacAddress:
                // Invariant: the type was just read from this value, so the accessor cannot fail; a false is a broken invariant
                // and is reported from a cold helper (the packet is rolled back, not written wrong).
                if (!data.TryGetAsMacAddress(out MacAddress mac))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.MacAddress, data.Type);
                }

                ProtobufEncoder.WriteVarintField(ref buffer, PbfFieldNumbers.FieldValue, mac.RawValue);
                break;
            case FieldType.IPv4Address:
                if (!data.TryGetAsIPv4(out IPv4Address ipv4))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.IPv4Address, data.Type);
                }

                ProtobufEncoder.WriteFixed32Field(ref buffer, PbfFieldNumbers.FieldValue, ipv4.RawValue);
                break;
            case FieldType.IPv6Address:
                if (!data.TryGetAsIPv6(out IPv6Address ipv6))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.IPv6Address, data.Type);
                }

                ProtobufEncoder.WriteFixed64Field(ref buffer, PbfFieldNumbers.FieldValue, ipv6.High);
                ProtobufEncoder.WriteFixed64Field(ref buffer, PbfFieldNumbers.FieldValueLow, ipv6.Low);
                break;
            case FieldType.Eui64:
                if (!data.TryGetAsEui64(out Eui64 eui64))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.Eui64, data.Type);
                }

                ProtobufEncoder.WriteFixed64Field(ref buffer, PbfFieldNumbers.FieldValue, eui64.RawValue);
                break;
            case FieldType.Uuid:
                if (!data.TryGetAsUuid(out Uuid uuid))
                {
                    FieldColumnBag.ThrowBrokenColumnType(fieldIdValue, FieldType.Uuid, data.Type);
                }

                ProtobufEncoder.WriteFixed64Field(ref buffer, PbfFieldNumbers.FieldValue, uuid.High);
                ProtobufEncoder.WriteFixed64Field(ref buffer, PbfFieldNumbers.FieldValueLow, uuid.Low);
                break;
            default:
                // The caller passes only Bytes and the five address types.
                break;
        }
    }

    #endregion
}
