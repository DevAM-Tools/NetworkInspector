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
/// <b>Layer key:</b> protocols pass <see cref="Packet.GetEffectLayerKey"/> so ingest and
/// replay identify the same invocation. Remaining byte length is not a key.
/// </para>
/// <para>
/// <b>Thread-safety:</b> one <see cref="Record"/> at a time; lock-free
/// readers. Chunk storage, growth, and entry publication are delegated to
/// <see cref="ChunkedAppendOnlyStore{T}"/> (slot write, then count). Nested-layer nodes
/// added to an already published entry are published with
/// <see cref="Volatile"/> writes on the chain head; readers
/// <see cref="ChunkedAppendOnlyStore{T}.ReadVolatileRefField{TField}"/> that location.
/// Packed <see cref="Clear"/> uses a seqlock so concurrent <see cref="TryGet"/> misses instead of
/// returning a post-refill row under a stale index. Concurrent
/// <see cref="Clear"/> with <see cref="Record"/> is a restart; ingest should be idle.
/// </para>
/// </remarks>
/// <typeparam name="TEffect">Effect payload recorded for one protocol layer.</typeparam>
public sealed class EffectStore<TEffect> where TEffect : struct
{
    #region Nested types

    private struct Entry : ISortKeyed
    {
        public int PacketId;
        public int LayerKey;
        public TEffect Effect;
        public LayerNode? More;

        /// <summary>Entries are appended in ascending packet-id order, so the id is the sort key.</summary>
        public readonly int SortKey => PacketId;
    }

    private sealed class LayerNode
    {
        public int LayerKey;
        public TEffect Effect;
        public LayerNode? Next;
    }

    #endregion

    #region Constants

    private const int _DefaultChunkShift = 14;

    #endregion

    #region Fields

    private readonly ChunkedAppendOnlyStore<Entry> _Entries;
    private volatile int _RecordGate;

    #endregion

    #region Constructors

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

    #region Public API

    /// <summary>
    /// Number of packed tail rows. Nested layers of the same packet share one row.
    /// A <see cref="Record"/> with a packet id greater than the tail appends another row.
    /// A lower packet id than the tail throws.
    /// </summary>
    public int Count => _Entries.Count;

    /// <summary>
    /// Packet id of the most recently appended entry, or <see cref="int.MinValue"/> when empty.
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
    /// Packed buffer-and-offset key from <see cref="Packet.GetEffectLayerKey"/>. Range is not
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

        if (Interlocked.CompareExchange(ref _RecordGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent Record is not supported.");
        }

        try
        {
            int count = _Entries.Count;
            if (count > 0)
            {
                ref Entry tail = ref _Entries.ItemRef(count - 1);
                if (packetId < tail.PacketId)
                {
                    throw new InvalidOperationException(
                        $"Effect rows must be recorded in non-decreasing packet-id order. Tail is {tail.PacketId.ToString(CultureInfo.InvariantCulture)}, got {packetId.ToString(CultureInfo.InvariantCulture)}.");
                }

                if (packetId == tail.PacketId)
                {
                    if (_LayerKeyExists(ref tail, layerKey))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate layer key {layerKey.ToString(CultureInfo.InvariantCulture)} for packet {packetId.ToString(CultureInfo.InvariantCulture)}.");
                    }

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
    /// </summary>
    /// <param name="packetId">Packet id to look up.</param>
    /// <param name="layerKey">Packed buffer-and-offset key from <see cref="Packet.GetEffectLayerKey"/>.</param>
    /// <param name="effect">Recorded payload when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a published entry matches both keys.</returns>
    public bool TryGet(int packetId, int layerKey, out TEffect effect)
    {
        int index = _Entries.BinarySearch(packetId, static (in Entry e) => e.PacketId);
        if (index < 0
            || !_Entries.TryReadPublished(index, out Entry entry)
            || entry.PacketId != packetId)
        {
            effect = default;
            return false;
        }

        if (entry.LayerKey == layerKey)
        {
            effect = entry.Effect;
            return true;
        }

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

    #endregion

    #region Private helpers

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
