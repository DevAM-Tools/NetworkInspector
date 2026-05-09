// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Reassembles IP datagrams from fragments.
/// <para>
/// Tracks in-progress reassembly buffers keyed by a caller-defined fragment key type.
/// When all fragments of a datagram arrive, the complete payload is returned.
/// A configurable limit on concurrent reassembly entries prevents unbounded memory growth
/// from incomplete fragment sequences.
/// </para>
/// <para>
/// When constructed with <c>dropOnOverlap: true</c>, overlapping fragments (different offsets
/// with intersecting byte ranges) cause the entire datagram to be silently discarded,
/// implementing the RFC 5722 requirement for IPv6 fragment reassembly.
/// </para>
/// </summary>
/// <typeparam name="TKey">
/// The fragment identification key type (e.g. <see cref="DatagramFragmentKey"/> for IPv4,
/// <see cref="IPv6DatagramFragmentKey"/> for IPv6).
/// </typeparam>
internal sealed class DatagramDefragmenter<TKey> where TKey : struct, IEquatable<TKey>
{
    #region Constants

    /// <summary>Default maximum number of concurrent in-progress reassembly entries.</summary>
    private const int DefaultMaxEntries = 1024;

    #endregion

    #region Fields

    /// <summary>In-progress reassembly buffers keyed by fragment identification tuple.</summary>
    private readonly Dictionary<TKey, DatagramFragmentBuffer> _Buffers = [];

    /// <summary>Insertion order for deterministic FIFO eviction.</summary>
    private readonly Queue<TKey> _InsertionOrder = new();

    /// <summary>Maximum concurrent reassembly entries to prevent unbounded memory usage.</summary>
    private readonly int _MaxEntries = DefaultMaxEntries;

    /// <summary>
    /// When true, overlapping fragments (per RFC 5722) cause the datagram to be discarded.
    /// Set for IPv6 defragmentation; IPv4 uses largest-wins overlap handling instead.
    /// </summary>
    private readonly bool _DropOnOverlap;

    #endregion

    #region Properties

    /// <summary>Total number of datagrams successfully reassembled.</summary>
    private long _ReassembledCount;

    /// <summary>Number of in-progress entries dropped due to capacity pressure.</summary>
    private long _EvictedCount;

    /// <summary>Total number of datagrams successfully reassembled.</summary>
    internal long ReassembledCount => _ReassembledCount;

    /// <summary>
    /// Total number of in-progress reassembly entries that were dropped because the
    /// pending-entry limit (<see cref="DefaultMaxEntries"/>) was reached. Useful as a
    /// diagnostic counter to detect lost reassembly results upstream.
    /// </summary>
    internal long EvictedCount => _EvictedCount;

    /// <summary>Number of in-progress reassembly entries.</summary>
    internal int PendingCount => _Buffers.Count;

    #endregion

    #region Constructor

    /// <summary>
    /// Initialises a new defragmenter.
    /// </summary>
    /// <param name="dropOnOverlap">
    /// When <c>true</c>, any fragment whose byte range overlaps that of an existing
    /// fragment at a different offset causes the entire datagram to be silently discarded.
    /// Set to <c>true</c> for IPv6 per RFC 5722; leave <c>false</c> for IPv4.
    /// </param>
    /// <param name="maxEntries">
    /// Maximum number of concurrent in-progress reassembly buffers.
    /// Excess entries are evicted oldest-first to prevent unbounded memory growth.
    /// </param>
    internal DatagramDefragmenter(bool dropOnOverlap = false, int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        _DropOnOverlap = dropOnOverlap;
        _MaxEntries = maxEntries;
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Processes an IP fragment. Returns the reassembled payload when all fragments
    /// of a datagram have been received, or <c>null</c> if more fragments are needed.
    /// When <c>dropOnOverlap</c> is true (IPv6), returns <c>null</c> and discards the
    /// datagram's buffer on overlap, per RFC 5722.
    /// </summary>
    /// <param name="key">The fragment identification key.</param>
    /// <param name="offset">
    /// Byte offset of this fragment within the original datagram payload.
    /// This is the raw fragment offset from the IP header multiplied by 8.
    /// </param>
    /// <param name="moreFragments">True if the More Fragments (MF) flag is set.</param>
    /// <param name="data">The fragment payload data.</param>
    /// <returns>
    /// The complete reassembled payload when all fragments are received,
    /// <c>null</c> if reassembly is still in progress or the datagram was discarded.
    /// </returns>
    internal byte[]? ProcessFragment(
        TKey key,
        int offset,
        bool moreFragments,
        ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        // Opportunistic drain of stale queue heads. On the success path of a previous
        // call we removed the entry from `_Buffers` but left its key in `_InsertionOrder`
        // (the queue tracks raw insertion order, not membership). Without this drain the
        // queue would grow linearly with every reassembled datagram on long-running
        // captures. Bounded work per call: at most O(stale prefix length) and amortized
        // O(1) because each enqueue can only be drained once.
        while (_InsertionOrder.TryPeek(out TKey staleHead) && !_Buffers.ContainsKey(staleHead))
        {
            _InsertionOrder.Dequeue();
        }

        // Non-fragmented datagram check: if MF=0 and offset=0, it's a complete datagram.
        // This case should be handled by the caller before calling this method.

        if (!_Buffers.TryGetValue(key, out DatagramFragmentBuffer? buffer))
        {
            // Enforce max entries limit — evict oldest entry if at capacity.
            // This prevents unbounded growth from incomplete fragment sequences.
            if (_Buffers.Count >= _MaxEntries)
            {
                EvictOldestEntry();
            }

            buffer = new DatagramFragmentBuffer();
            _Buffers[key] = buffer;
            _InsertionOrder.Enqueue(key);
        }

        // buffer is guaranteed non-null: either found by TryGetValue or freshly created above.
        FragmentAddResult addResult = buffer!.AddFragment(offset, moreFragments, data, _DropOnOverlap);

        if (addResult == FragmentAddResult.OverlapDiscarded)
        {
            // RFC 5722: overlapping fragments — discard the entire datagram silently.
            // Remove the poisoned buffer so future fragments for the same key start fresh.
            _Buffers.Remove(key);
            return null;
        }

        if (addResult == FragmentAddResult.OversizeDiscarded)
        {
            // The terminal fragment would exceed MaxTotalLength — this datagram can never
            // complete within safe bounds. Remove the buffer immediately to avoid holding
            // memory for an entry that will never be reassembled.
            _Buffers.Remove(key);
            return null;
        }

        if (addResult != FragmentAddResult.Complete)
        {
            return null;
        }

        // Datagram complete — reassemble and remove the entry. The corresponding
        // entry in `_InsertionOrder` becomes a stale tombstone and is drained at the
        // top of the next call (or by `EvictOldestEntry`).
        byte[]? reassembled = buffer.Reassemble();
        _Buffers.Remove(key);

        if (reassembled is not null)
        {
            _ReassembledCount++;
        }

        return reassembled;
    }

    /// <summary>
    /// Clears all in-progress reassembly buffers.
    /// Call this when the session ends or the stack is reset.
    /// </summary>
    internal void Clear()
    {
        _Buffers.Clear();
        _InsertionOrder.Clear();
        _ReassembledCount = 0;
        _EvictedCount = 0;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Evicts the oldest entry (by insertion order) to make room for new fragments.
    /// Uses a FIFO queue for deterministic eviction order that does not depend on
    /// Dictionary implementation details.
    /// </summary>
    private void EvictOldestEntry()
    {
        // Drain stale entries that may have already been removed (e.g. completed datagrams).
        // Only count an actual buffer removal as an eviction — stale queue entries are not.
        while (_InsertionOrder.Count > 0)
        {
            TKey oldest = _InsertionOrder.Dequeue();
            if (_Buffers.Remove(oldest))
            {
                _EvictedCount++;
                return;
            }
        }
    }

    #endregion
}
