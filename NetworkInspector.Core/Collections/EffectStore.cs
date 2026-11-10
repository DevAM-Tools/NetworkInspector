// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Append-only sparse store keyed by <c>(PacketId, LayerKey)</c>. First-parses write in
/// non-decreasing <see cref="PacketId"/> order; replay readers binary-search by packet id, then
/// match layer key on the entry or its nested-layer chain.
/// Single ordered ingest writer; lock-free readers. Concurrent <see cref="Record"/> throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose:</b> a stateful protocol (reassembly, stream tracking) changes shared state while a packet is ingested
/// for the first time. A later parse of the same packet (UI, filter, export) must not touch that state again, so the
/// protocol records the small result of its first parse here and replays it on every later parse. That keeps re-parses
/// lock-free and field-identical while later packets are still being ingested.
/// </para>
/// <para>
/// <b>Layout:</b> one packed row per packet id. The first layer recorded for a packet lives inline in the row; further
/// (nested) layers of the same packet, for example a protocol parsed from a reassembled payload, hang off the row
/// as a short linked chain.
/// A single row per packet keeps the common case (one effect per packet) at one array slot and no extra allocation;
/// the chain is only allocated for nested layers. Rows are ordered by packet id, which is what makes the lookup a
/// binary search instead of a hash lookup per replay.
/// </para>
/// <para>
/// <b>Layer key:</b> protocols pass <see cref="MutField.TryGetEffectLayerKey"/> so ingest and
/// replay identify the same invocation. The key packs the index of the packet buffer and the byte offset of the
/// parse slice in it; remaining byte length is not a key.
/// </para>
/// <para>
/// <b>Thread-safety:</b> one <see cref="Record"/> at a time; lock-free
/// readers. Chunk storage, growth, and entry publication are delegated to
/// <see cref="ChunkedAppendOnlyStore{T}"/> (slot write, then count). Nested-layer nodes
/// added to an already published entry are published with
/// <see cref="Volatile"/> writes on the chain head; readers
/// <see cref="ChunkedAppendOnlyStore{T}.ReadVolatileRefField{TField}"/> that location.
/// <see cref="Clear"/> replaces the generation of the packed store, so a concurrent <see cref="TryGet"/> misses
/// instead of returning a post-refill row under a stale index. Concurrent
/// <see cref="Clear"/> with <see cref="Record"/> is a restart; ingest should be idle.
/// </para>
/// </remarks>
/// <typeparam name="TEffect">Effect payload recorded for one protocol layer.</typeparam>
public sealed class EffectStore<TEffect> where TEffect : struct
{
    #region Configuration and state

    /// <summary>
    /// One packed row: the first layer of a packet inline, further layers behind <see cref="More"/>. A mutable struct
    /// because the single writer extends <see cref="More"/> in place through a reference into the store.
    /// </summary>
    private struct Entry : ISortKeyed
    {
        /// <summary>Packet id of the row; strictly ascending across rows.</summary>
        public int PacketId;

        /// <summary>Layer key of the inline (first) layer.</summary>
        public int LayerKey;

        /// <summary>Effect of the inline (first) layer.</summary>
        public TEffect Effect;

        /// <summary>
        /// Head of the chain of further layers of this packet, newest first; null when there are none. Written only by
        /// the writer with <see cref="Volatile"/> writes and read by readers through a volatile read, because the row
        /// itself was already published when a nested layer is added.
        /// </summary>
        public LayerNode? More;

        /// <summary>Entries are appended in ascending packet-id order, so the id is the sort key.</summary>
        public readonly int SortKey => PacketId;
    }

    /// <summary>
    /// A further layer of an already published row. Fully initialized before it is linked in (the writer publishes the
    /// head with a volatile write), and immutable afterwards, so readers can walk the chain without synchronization.
    /// </summary>
    private sealed class LayerNode
    {
        /// <summary>Layer key of this layer.</summary>
        public int LayerKey;

        /// <summary>Effect of this layer.</summary>
        public TEffect Effect;

        /// <summary>The next older layer, or null at the end of the chain.</summary>
        public LayerNode? Next;
    }

    /// <summary>Default chunk shift: 2^14 = 16 384 rows per inner array.</summary>
    private const int _DefaultChunkShift = 14;

    /// <summary>Packed rows in ascending packet-id order; owns chunking, growth, and publication of the row count.</summary>
    private readonly ChunkedAppendOnlyStore<Entry> _Entries;

    /// <summary>
    /// 1 while a <see cref="Record"/> runs. A misuse detector, not a lock: the store's own append gate only covers the
    /// append itself, whereas <see cref="Record"/> also compares against the tail row and mutates its chain, so the
    /// whole call must be single-writer. A second concurrent call throws instead of waiting.
    /// </summary>
    private volatile int _RecordGate;

    /// <summary>Creates a store with the default chunk size (16 384 entries per inner array).</summary>
    public EffectStore()
        : this(_DefaultChunkShift)
    {
    }

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array).
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public EffectStore(int chunkShift) =>
        _Entries = new(chunkShift);

    #endregion

    #region Recording and replay

    /// <summary>
    /// Number of packed tail rows. Nested layers of the same packet share one row.
    /// A <see cref="Record"/> with a packet id greater than the tail appends another row.
    /// A lower packet id than the tail throws.
    /// </summary>
    public int Count => _Entries.Count;

    /// <summary>
    /// Packet id of the most recently appended entry, or <see cref="int.MinValue"/> when empty.
    /// A <see cref="Clear"/> racing the read also yields <see cref="int.MinValue"/>.
    /// </summary>
    public int TailPacketId
    {
        get
        {
            int count = _Entries.Count;
            if (count == 0)
            {
                return int.MinValue;
            }

            return _Entries.TryReadPublished(count - 1, out Entry tail)
                ? tail.PacketId
                : int.MinValue;
        }
    }

    /// <summary>
    /// Records one layer effect during first parse. When the tail entry already belongs to
    /// <paramref name="packetId"/>, the effect is prepended to that entry's nested-layer chain.
    /// Duplicate <paramref name="layerKey"/> on the same packet throws.
    /// </summary>
    /// <param name="packetId">
    /// Dense packet id of the ingest currently in flight. Must be a valid
    /// <see cref="Ids.ArrayIndexIdRange"/> index and not less than the current tail packet id.
    /// </param>
    /// <param name="layerKey">
    /// Packed buffer-and-offset key from <see cref="MutField.TryGetEffectLayerKey"/>. Range is not
    /// validated here; the caller guarantees the value came from that helper on the packet in flight.
    /// </param>
    /// <param name="effect">Immutable effect payload for this layer.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="packetId"/> is not a valid dense array index.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The same <paramref name="packetId"/> already recorded <paramref name="layerKey"/>,
    /// <paramref name="packetId"/> is less than the tail packet id, or a concurrent writer called
    /// <see cref="Record"/>.
    /// </exception>
    public void Record(int packetId, int layerKey, in TEffect effect)
    {
        Ids.ArrayIndexIdRange.ValidateIndexOrThrow(packetId, nameof(packetId));

        // Misuse detector, not a lock: two writers would corrupt the ordering and the chain, so fail loudly.
        if (Interlocked.CompareExchange(ref _RecordGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent Record is not supported.");
        }

        try
        {
            // Only the tail row can still receive layers: packet ids are non-decreasing, so an older row is final.
            // The reference points into the live slot, so the chain head can be extended in place.
            int count = _Entries.Count;
            if (count > 0)
            {
                ref Entry tail = ref _Entries.ItemRef(count - 1);

                // An older id would break the sort order that TryGet's binary search relies on.
                if (packetId < tail.PacketId)
                {
                    throw new InvalidOperationException(
                        "Effect rows must be recorded in non-decreasing packet-id order. "
                        + Invariant($"Tail is {tail.PacketId.ToString(CultureInfo.InvariantCulture)}, got {packetId.ToString(CultureInfo.InvariantCulture)}."));
                }

                // Same packet: add a nested layer to the existing row instead of a new row, so the row id stays unique
                // and TryGet's binary search finds one row per packet.
                if (packetId == tail.PacketId)
                {
                    if (_LayerKeyExists(ref tail, layerKey))
                    {
                        throw new InvalidOperationException(
                            Invariant($"Duplicate layer key {layerKey.ToString(CultureInfo.InvariantCulture)} ")
                            + Invariant($"for packet {packetId.ToString(CultureInfo.InvariantCulture)}."));
                    }

                    // Build the node completely first, then publish it as the new chain head with a release write:
                    // a reader that sees the node sees its fields and the rest of the chain.
                    LayerNode node = new()
                    {
                        LayerKey = layerKey,
                        Effect = effect,
                        Next = Volatile.Read(ref tail.More),
                    };

                    Volatile.Write(ref tail.More, node);
                    return;
                }
            }

            // New packet (or first ever row): append a row; the store publishes it with a release write of its count.
            _Entries.Append(new Entry { PacketId = packetId, LayerKey = layerKey, Effect = effect });
        }
        finally
        {
            _ = Interlocked.Exchange(ref _RecordGate, 0);
        }
    }

    /// <summary>
    /// Replays the effect recorded for <paramref name="packetId"/> and <paramref name="layerKey"/>.
    /// Returns <see langword="false"/> on a miss, including when <see cref="Clear"/> raced or the
    /// published row at the search index no longer has that packet id.
    /// Lock-free and allocation-free; safe to call from any number of threads, also while <see cref="Record"/> runs.
    /// </summary>
    /// <param name="packetId">Packet id to look up.</param>
    /// <param name="layerKey">Packed buffer-and-offset key from <see cref="MutField.TryGetEffectLayerKey"/>.</param>
    /// <param name="effect">Recorded payload when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a published entry matches both keys.</returns>
    public bool TryGet(int packetId, int layerKey, out TEffect effect)
    {
        // Locate the row. The key-extractor is a static lambda, so the compiler caches the delegate and a lookup
        // allocates nothing (replays run for every re-parse of every packet).
        int index = _Entries.BinarySearch(packetId, static (in Entry e) => e.PacketId);

        // Re-read the row as a copy and verify its id: a Clear plus refill between the search and this read could have
        // put a different packet at the same index, and returning that row's effect would be a silent wrong result.
        if (index < 0
            || !_Entries.TryReadPublished(index, out Entry entry)
            || entry.PacketId != packetId)
        {
            effect = default;
            return false;
        }

        // Fast path: the inline (first) layer, no chain walk.
        if (entry.LayerKey == layerKey)
        {
            effect = entry.Effect;
            return true;
        }

        // Nested layers: re-read the chain head from the live slot with a volatile read instead of using entry.More.
        // The copy above is a plain read and can predate a node the writer linked in afterwards; the volatile read
        // pairs with the writer's volatile write. Nodes are immutable once linked, so walking the chain needs no lock.
        LayerNode? node = _Entries.ReadVolatileRefField(index, static (ref Entry e) => ref e.More);
        for (; node is not null; node = node.Next)
        {
            if (node.LayerKey == layerKey)
            {
                effect = node.Effect;
                return true;
            }
        }

        effect = default;
        return false;
    }

    /// <summary>
    /// Drops all recorded effects. Concurrent <see cref="TryGet"/> observes a miss.
    /// Must run while ingest is idle.
    /// </summary>
    public void Clear() =>
        _Entries.Clear();

    /// <summary>
    /// Whether the row already holds <paramref name="layerKey"/>, inline or in its chain. Writer-side check for
    /// <see cref="Record"/>: only the writer changes the chain, so this walk sees every node linked so far.
    /// </summary>
    /// <param name="tail">The tail row, by reference into the live slot.</param>
    /// <param name="layerKey">Key to look for.</param>
    /// <returns><see langword="true"/> when the inline layer or a chain node has the key.</returns>
    private static bool _LayerKeyExists(ref Entry tail, int layerKey)
    {
        if (tail.LayerKey == layerKey)
        {
            return true;
        }

        for (LayerNode? node = Volatile.Read(ref tail.More); node is not null; node = node.Next)
        {
            if (node.LayerKey == layerKey)
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
