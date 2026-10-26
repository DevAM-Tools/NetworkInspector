// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Each protocol instance manages its own
/// <see cref="DatagramDefragmenter{TKey}"/> and must serialize all access to it. The owning
/// protocols (IPv4, IPv6) satisfy this by feeding fragments only during the ordered first parse of
/// a packet id and by recording the reassembly outcome per packet; any later parse of that id
/// replays the recorded outcome instead of calling <see cref="ProcessFragment"/> again. Without
/// that guard, a concurrent re-parse would corrupt the fragment buffers — see
/// <c>PROTOCOL_GUIDE.md</c>, "First parse vs. re-parse".
/// <see cref="ReassembledCount"/> and <see cref="EvictedCount"/> are backed by <see langword="volatile"/> fields that
/// the owning thread updates with <see cref="Interlocked"/>, so a monitoring thread may read them at any time.
/// <see cref="PendingCount"/> reads the dictionary and is only valid on the owning thread (or after the caller has
/// established a happens-before edge with it).
/// </para>
/// <para>
/// <b>Memory limits (threat: a capture of incomplete or hostile fragment sequences exhausts memory):</b>
/// the number of concurrent in-progress datagrams is bounded by <c>maxEntries</c> and the oldest entry is evicted first;
/// the size of one datagram is bounded by the IP payload limit and a fragment-count limit (see
/// <see cref="DatagramFragmentBuffer"/>). Every discard, completion, eviction, and <see cref="Clear"/> returns the
/// pooled fragment arrays.
/// </para>
/// </summary>
/// <typeparam name="TKey">
/// The fragment identification key type (e.g. <see cref="DatagramFragmentKey"/> for IPv4,
/// <see cref="IPv6DatagramFragmentKey"/> for IPv6).
/// </typeparam>
public sealed class DatagramDefragmenter<TKey> where TKey : struct, IEquatable<TKey>
{
    #region Constants

    /// <summary>
    /// Default maximum number of concurrent in-progress reassembly entries.
    /// Limit record: threat is memory exhaustion by datagrams whose remaining fragments never arrive (each entry holds
    /// pooled copies of its fragments). Configurable by the code that creates the defragmenter (the <c>maxEntries</c>
    /// constructor parameter, any positive value; no end-user setting). Posture: secure by default, because the default
    /// is finite and loosening is an explicit constructor argument. When the limit is reached the entry created
    /// first (insertion order, not last activity) is evicted and the eviction is counted in <see cref="EvictedCount"/>.
    /// The number 1024 itself has no recorded derivation.
    /// </summary>
    private const int _DefaultMaxEntries = 1024;

    #endregion

    #region Fields

    /// <summary>In-progress reassembly buffers keyed by fragment identification tuple.</summary>
    private readonly Dictionary<TKey, DatagramFragmentBuffer> _Buffers = [];

    /// <summary>
    /// Insertion order for deterministic FIFO eviction. Each entry names the buffer it queued: after a discard or a
    /// completion the key may start a new buffer, and only the entry of the live buffer counts.
    /// </summary>
    private readonly Queue<(TKey Key, DatagramFragmentBuffer Buffer)> _InsertionOrder = new();

    /// <summary>Maximum concurrent reassembly entries to prevent unbounded memory usage (set once by the constructor).</summary>
    private readonly int _MaxEntries = _DefaultMaxEntries;

    /// <summary>
    /// When true, overlapping fragments (per RFC 5722) cause the datagram to be discarded.
    /// Set for IPv6 defragmentation; IPv4 uses largest-wins overlap handling instead.
    /// </summary>
    private readonly bool _DropOnOverlap;

    #endregion

    #region Properties

    /// <summary>Total number of datagrams successfully reassembled.</summary>
    private volatile int _ReassembledCount;

    /// <summary>
    /// Number of in-progress entries dropped due to capacity pressure.
    /// Volatile so a monitoring thread sees current values; written only by the owning thread via Interlocked.
    /// </summary>
    private volatile int _EvictedCount;

    /// <summary>Total number of datagrams successfully reassembled.</summary>
    public int ReassembledCount => _ReassembledCount;

    /// <summary>
    /// Total number of in-progress reassembly entries that were dropped because the
    /// pending-entry limit (constructor <c>maxEntries</c> / <c>_MaxEntries</c>) was reached.
    /// Useful as a diagnostic counter to detect lost reassembly results upstream.
    /// </summary>
    public int EvictedCount => _EvictedCount;

    /// <summary>Number of in-progress reassembly entries.</summary>
    public int PendingCount => _Buffers.Count;

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
    /// Must be greater than zero.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is zero or negative.</exception>
    public DatagramDefragmenter(bool dropOnOverlap = false, int maxEntries = _DefaultMaxEntries)
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
    /// The returned array is a fresh allocation owned by the caller (not pooled); the fragment copies stay internal.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    public byte[]? ProcessFragment(
        TKey key,
        int offset,
        bool moreFragments,
        ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        // A fragment that reaches past the 65535-byte datagram limit can never belong to a valid datagram. Drop any
        // partial state under this key as well (the datagram is poisoned) and report "not complete". No entry is created,
        // so such a fragment cannot consume a slot of the entry limit.
        if (offset >= 65535 || data.Length > 65535 - offset)
        {
            if (_Buffers.Remove(key, out DatagramFragmentBuffer? oversize))
            {
                oversize.Release();
            }
            return null;
        }

        // Opportunistic drain of stale queue heads. When a previous call completed or discarded a datagram we removed
        // its entry from `_Buffers` but left its queue entry in `_InsertionOrder` (removing from the middle of a
        // Queue is not possible; the queue tracks raw insertion order, not membership). Without this drain the
        // queue would grow linearly with every reassembled datagram on long-running
        // captures. Bounded work per call: at most O(stale prefix length) and amortized
        // O(1) because each enqueue can only be drained once.
        while (_InsertionOrder.TryPeek(out (TKey Key, DatagramFragmentBuffer Buffer) staleHead) && !_IsLive(staleHead))
        {
            _InsertionOrder.Dequeue();
        }

        // Precondition for callers: a non-fragmented datagram (MF=0 and offset=0) is complete by itself and must not be
        // passed here; this method would still treat it as a one-fragment datagram.

        // Find or open the buffer of this datagram.
        if (!_Buffers.TryGetValue(key, out DatagramFragmentBuffer? buffer))
        {
            // Enforce max entries limit — evict oldest entry if at capacity.
            // This prevents unbounded growth from incomplete fragment sequences.
            // Eviction happens before the insert so the dictionary never exceeds the limit.
            if (_Buffers.Count >= _MaxEntries)
            {
                _EvictOldestEntry();
            }

            buffer = new();
            _Buffers[key] = buffer;
            _InsertionOrder.Enqueue((key, buffer));
        }

        // buffer is guaranteed non-null: either found by TryGetValue or freshly created above.
        // The buffer copies the payload into pooled arrays: the caller's span is only valid for this call.
        FragmentAddResult addResult = buffer!.AddFragment(offset, moreFragments, data, _DropOnOverlap);

        if (addResult == FragmentAddResult.OverlapDiscarded)
        {
            // RFC 5722: overlapping fragments — discard the entire datagram silently.
            // Remove the poisoned buffer so future fragments for the same key start fresh.
            buffer.Release();
            _Buffers.Remove(key);
            return null;
        }

        if (addResult == FragmentAddResult.OversizeDiscarded)
        {
            // The terminal fragment would exceed MaxTotalLength — this datagram can never
            // complete within safe bounds. Remove the buffer immediately to avoid holding
            // memory for an entry that will never be reassembled.
            buffer.Release();
            _Buffers.Remove(key);
            return null;
        }

        if (addResult != FragmentAddResult.Complete)
        {
            return null;
        }

        // Datagram complete — reassemble and remove the entry. The corresponding
        // entry in `_InsertionOrder` becomes a stale tombstone and is drained at the
        // top of the next call (or by `_EvictOldestEntry`).
        // Reassemble returns null when the fragments are inconsistent (a fragment extends past the terminal length); the
        // datagram is then dropped, and the pooled copies are released either way.
        byte[]? reassembled = buffer.Reassemble();
        buffer.Release();
        _Buffers.Remove(key);

        if (reassembled is not null)
        {
            Interlocked.Increment(ref _ReassembledCount);
        }

        return reassembled;
    }

    /// <summary>
    /// Clears all in-progress reassembly buffers.
    /// Call this when the session ends or the stack is reset.
    /// </summary>
    public void Clear()
    {
        foreach (DatagramFragmentBuffer buffer in _Buffers.Values)
        {
            buffer.Release();
        }
        _Buffers.Clear();
        _InsertionOrder.Clear();
        Interlocked.Exchange(ref _ReassembledCount, 0);
        Interlocked.Exchange(ref _EvictedCount, 0);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Evicts the oldest entry (by insertion order) to make room for new fragments.
    /// Uses a FIFO queue for deterministic eviction order that does not depend on
    /// Dictionary implementation details.
    /// </summary>
    private void _EvictOldestEntry()
    {
        // Drain stale entries that may have already been removed (e.g. completed datagrams).
        // Only count an actual buffer removal as an eviction — stale queue entries are not.
        while (_InsertionOrder.Count > 0)
        {
            (TKey Key, DatagramFragmentBuffer Buffer) oldest = _InsertionOrder.Dequeue();
            if (_IsLive(oldest))
            {
                _ = _Buffers.Remove(oldest.Key);
                oldest.Buffer.Release();
                Interlocked.Increment(ref _EvictedCount);
                return;
            }
        }
    }

    /// <summary>
    /// Whether a queue entry still names the buffer stored under its key. An entry left by a completed or discarded
    /// datagram is stale even when the key has started a new buffer since.
    /// </summary>
    private bool _IsLive((TKey Key, DatagramFragmentBuffer Buffer) entry) =>
        _Buffers.TryGetValue(entry.Key, out DatagramFragmentBuffer? live) && ReferenceEquals(live, entry.Buffer);

    #endregion
}
