// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Reassembles datagrams from fragments for any fragmenting protocol (IPv4, IPv6, SOME/IP-TP, and every later user).
/// <para>
/// The owner supplies a key type that identifies one datagram and a <see cref="FragmentReassemblyOptions"/> value; this type
/// supplies the table of open datagrams, the check order of <see cref="Add"/>, pooled fragment copies, eviction, the limits
/// and one <see cref="FragmentOutcome"/> per call. Protocol code never sees a fragment list.
/// </para>
/// <para>
/// <b>Check order of <see cref="Add"/>:</b> extent, alignment, lookup or create (with count eviction), restart, byte budget
/// (with eviction), insert with the overlap policy, completion, gap report. Every check that can refuse a fragment runs
/// before a byte is copied, so a hostile fragment costs no allocation.
/// </para>
/// <para>
/// <b>Eviction order.</b> Open datagrams sit in one doubly linked list in eviction order, the head being evicted first. A
/// datagram is linked when it is created; under <see cref="FragmentEvictionPolicy.LeastRecentlyUsed"/> every fragment it
/// receives moves it to the tail. Linking, unlinking and moving are O(1) and the list never holds more entries than there are
/// open datagrams, so repeated touches cannot grow it. Rejected alternative: a queue with lazy deletion, which needs a
/// stamp per entry and a periodic rebuild to stay bounded and cannot skip the datagram currently being filled without
/// reordering the queue.
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe, by design. One owner feeds it from the ordered first parse only: the owning protocol
/// calls <see cref="Add"/> behind <c>IngestGate</c> and records the outcome per packet; any later parse of that packet replays
/// the recorded outcome and never calls <see cref="Add"/> again. A concurrent re-parse would corrupt the fragment buffers.
/// <see cref="ReassembledCount"/> and <see cref="EvictedCount"/> are volatile and updated with <see cref="Interlocked"/> by
/// the owner thread, so a monitoring thread may read them at any time. <see cref="PendingCount"/> and
/// <see cref="BufferedBytes"/> are valid on the owner thread only (or after a happens-before edge with it). No lock is used:
/// a lock would only protect against a call pattern that the contract already forbids and would cost every fragment.
/// </para>
/// <para>
/// <b>Memory limits (threat: a capture of incomplete or hostile fragment streams exhausts memory):</b>
/// <see cref="FragmentReassemblyOptions.MaxPending"/> bounds the open datagrams,
/// <see cref="FragmentReassemblyOptions.MaxDatagramLength"/> the extent of one, <see cref="FragmentReassemblyOptions.MaxFragments"/>
/// the fragment list, and <see cref="FragmentReassemblyOptions.MaxBufferedBytes"/> the stored bytes of all together. Every
/// discard, completion, eviction and <see cref="Clear"/> returns the pooled arrays.
/// </para>
/// </summary>
/// <typeparam name="TKey">
/// The datagram identification (for example <see cref="DatagramFragmentKey"/> for IPv4, <see cref="IPv6DatagramFragmentKey"/> for IPv6).
/// </typeparam>
public sealed class FragmentReassembler<TKey> where TKey : struct, IEquatable<TKey>
{
    #region Fields

    /// <summary>The validated options; fixed for the lifetime of the reassembler.</summary>
    private readonly FragmentReassemblyOptions _Options;

    /// <summary>The pools fragment copies and fragment lists are rented from.</summary>
    private readonly FragmentPools _Pools;

    /// <summary>Open datagrams by identification.</summary>
    private readonly Dictionary<TKey, FragmentBuffer<TKey>> _Buffers = [];

    /// <summary>Head of the eviction order (evicted first); null when no datagram is open.</summary>
    private FragmentBuffer<TKey>? _Head;

    /// <summary>Tail of the eviction order (evicted last).</summary>
    private FragmentBuffer<TKey>? _Tail;

    /// <summary>Stored payload bytes of all open datagrams. Owner thread only.</summary>
    private long _BufferedBytes;

    /// <summary>Datagrams successfully reassembled. Volatile so a monitoring thread sees current values; written by the owner via Interlocked.</summary>
    private volatile int _ReassembledCount;

    /// <summary>Datagrams dropped because a count or byte limit was reached. Volatile like <see cref="_ReassembledCount"/>.</summary>
    private volatile int _EvictedCount;

    #endregion

    #region Constructor

    /// <summary>Initialises a reassembler with validated options.</summary>
    /// <param name="options">The limits and policies; the presets reproduce IPv4 and IPv6.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> fail <see cref="FragmentReassemblyOptions.TryValidate"/>; the message names the option. A bad value is a programming error of the owner, not input.</exception>
    public FragmentReassembler(in FragmentReassemblyOptions options)
        : this(in options, FragmentPools.Shared)
    {
    }

    /// <summary>
    /// Initialises a reassembler that rents from <paramref name="pools"/>. Internal: only a test replaces the shared pools,
    /// to make the allocation measurement independent of how many arrays the shared pools happen to retain.
    /// </summary>
    internal FragmentReassembler(in FragmentReassemblyOptions options, FragmentPools pools)
    {
        if (!options.TryValidate(out string? problem))
        {
            throw new ArgumentException(problem, nameof(options));
        }

        ArgumentNullException.ThrowIfNull(pools);
        _Options = options;
        _Pools = pools;
    }

    #endregion

    #region Properties

    /// <summary>Number of open datagrams. Valid on the owner thread.</summary>
    public int PendingCount => _Buffers.Count;

    /// <summary>
    /// Stored payload bytes of all open datagrams. Valid on the owner thread. The pooled arrays behind the stored bytes can be
    /// up to twice as large, because the pool rounds a rental up to a power of two.
    /// </summary>
    public long BufferedBytes => _BufferedBytes;

    /// <summary>Total number of datagrams successfully reassembled. Readable from any thread.</summary>
    public int ReassembledCount => _ReassembledCount;

    /// <summary>
    /// Total number of open datagrams that were dropped because the count limit or the byte budget was reached. A diagnostic
    /// to detect lost reassembly results. Readable from any thread.
    /// </summary>
    public int EvictedCount => _EvictedCount;

    /// <summary>Number of entries in the eviction order. Equals <see cref="PendingCount"/>; exposed so a test can prove that touches do not grow it.</summary>
    internal int EvictionOrderLength
    {
        get
        {
            int length = 0;
            for (FragmentBuffer<TKey>? node = _Head; node is not null; node = node.Next)
            {
                length++;
            }

            return length;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Processes one fragment and reports what happened.
    /// </summary>
    /// <param name="key">The datagram identification.</param>
    /// <param name="offset">
    /// Byte position of the fragment in the datagram. A <see langword="long"/> because the offset of some formats reaches
    /// 2^32 - 16 and must be refused by the extent check, not wrapped. A negative offset is refused like an oversized one.
    /// </param>
    /// <param name="moreFragments">False for the last fragment of the datagram.</param>
    /// <param name="data">The fragment bytes; copied, the span is only valid for this call.</param>
    /// <param name="contributor">An opaque tag kept per stored fragment and returned in offset order on delivery (the owner passes the packet id, which makes the tags the list of contributing packets).</param>
    /// <returns>The outcome; see <see cref="FragmentOutcome"/>. Never throws for fragment content.</returns>
    public FragmentOutcome Add(in TKey key, long offset, bool moreFragments, ReadOnlySpan<byte> data, int contributor)
    {
        FragmentOutcomeFlags flags = FragmentOutcomeFlags.None;
        int evicted = 0;

        // 1. Extent. Threat: one fragment at a huge offset makes the completed datagram request a huge buffer. The extent
        // (offset plus length) is checked in long arithmetic before anything is created or copied, and the key's open datagram
        // is released because it is poisoned by a fragment that can never belong to a valid datagram. The offset >= limit term
        // keeps IPv4's rule that offset 65,535 is refused even for an empty fragment.
        if (offset < 0 || offset >= _Options.MaxDatagramLength || offset + data.Length > _Options.MaxDatagramLength)
        {
            _RemoveKey(in key);
            return FragmentOutcome.Discarded(FragmentDiscardReason.ExtentLimit, flags, evicted);
        }

        int start = (int)offset; // safe: 0 <= offset < MaxDatagramLength <= Array.MaxLength

        // 2. Alignment. A format fact (offsets count in units of NonLastAlignment bytes), so every non-last fragment is a
        // non-empty multiple of it. The check runs before a datagram is created, so an empty flood opens no datagram.
        if (_Options.NonLastAlignment > 1 && moreFragments && (data.Length == 0 || data.Length % _Options.NonLastAlignment != 0))
        {
            return FragmentOutcome.Discarded(FragmentDiscardReason.Alignment, flags, evicted);
        }

        // 3. Lookup or create. Creating at the count limit first evicts one datagram in policy order, so the table never
        // exceeds the limit. An existing datagram that receives a fragment becomes the most recently used one.
        bool created = false;
        if (!_Buffers.TryGetValue(key, out FragmentBuffer<TKey>? buffer))
        {
            if (_Buffers.Count >= _Options.MaxPending && _EvictOne(exclude: null))
            {
                flags |= FragmentOutcomeFlags.EvictedForCount;
                evicted++;
            }

            buffer = new FragmentBuffer<TKey>(in key, _Pools);
            _Buffers[key] = buffer;
            _Link(buffer);
            created = true;
        }
        else if (_Options.EvictionPolicy == FragmentEvictionPolicy.LeastRecentlyUsed)
        {
            _MoveToTail(buffer);
        }

        // 4. Restart. A sender that restarts a transfer begins at offset 0 again, so a fragment at offset 0 while the datagram
        // already has its beginning is a second beginning (a fragment at offset 0 that merely arrives late is the missing start,
        // not a restart, or an out-of-order transfer would erase itself); the stored fragments belong to the abandoned
        // attempt. The fresh datagram counts as newly created for the creation order.
        if (_Options.RestartOnZeroOffset && start == 0 && buffer.HasStart)
        {
            _BufferedBytes -= buffer.StoredBytes;
            buffer.Release();
            _MoveToTail(buffer);
            flags |= FragmentOutcomeFlags.Restarted;
        }

        // 5. Budget. Threat: datagrams that each respect the per-datagram limit exhaust memory together. Evict other datagrams
        // in policy order until the fragment fits. The datagram being filled is never the victim, because evicting it would
        // discard the very fragment that is being added. The comparison is written as a subtraction so it cannot overflow.
        // If this one datagram alone cannot fit, it is released and the fragment refused.
        while (data.Length > _Options.MaxBufferedBytes - _BufferedBytes && _EvictOne(exclude: buffer))
        {
            flags |= FragmentOutcomeFlags.EvictedForBudget;
            evicted++;
        }

        if (data.Length > _Options.MaxBufferedBytes - _BufferedBytes)
        {
            _Remove(buffer);
            return FragmentOutcome.Discarded(FragmentDiscardReason.BufferLimit, flags, evicted);
        }

        // 6. Insert with the overlap policy. The buffer reports a refusal as a reason; the owner decides what happens to the
        // datagram. Bytes are accounted as the difference of the buffer's stored bytes, which covers replaced duplicates.
        long storedBefore = buffer.StoredBytes;
        FragmentDiscardReason reason = buffer.Add(
            start, moreFragments, data, contributor, in _Options, out bool overlapConflict, out _);
        _BufferedBytes += buffer.StoredBytes - storedBefore;
        if (overlapConflict)
        {
            flags |= FragmentOutcomeFlags.OverlapConflict;
        }

        if (reason != FragmentDiscardReason.None)
        {
            return _Refuse(buffer, created, reason, flags, evicted);
        }

        // A fragment that stored nothing (an empty non-last fragment under the first-wins policy) must not leave an empty
        // datagram behind: a flood of them with distinct keys would otherwise occupy every slot and evict real datagrams.
        if (buffer.FragmentCount == 0)
        {
            _Remove(buffer);
            return FragmentOutcome.Pending(flags, evicted);
        }

        // 7. Completion. The datagram leaves the table before the outcome is built; the payload is a plain array bound to the
        // packet and the pooled copies are returned either way.
        if (buffer.IsComplete())
        {
            byte[]? assembled = buffer.Assemble();
            int[] contributors = assembled is null
                ? []
                : buffer.GetContributors();
            _Remove(buffer);
            if (assembled is null)
            {
                // The stored fragments cannot form a datagram (one extends past the end, or the datagram is empty).
                return FragmentOutcome.Discarded(FragmentDiscardReason.BeyondEnd, flags, evicted);
            }

            Interlocked.Increment(ref _ReassembledCount);
            return FragmentOutcome.Complete(flags, evicted, assembled, contributors);
        }

        // 8. Gap behind a known end. Once the last fragment fixed the total length and a hole remains, the decodable part (the bytes
        // from offset 0 without a hole) is reported once; the datagram stays open so that a late fragment can still complete it.
        // The report waits for a non-empty prefix: without a fragment at offset 0 nothing is decodable, so a lone last fragment
        // (for example of a transfer whose beginning was evicted) stays pending, and the report happens when the beginning arrives.
        // Without this policy the hole is just "pending".
        if (_Options.GapPolicy == FragmentGapPolicy.PrefixOnLastThenComplete && buffer.TotalLength >= 0 && buffer.HasStart && !buffer.PartialReported)
        {
            buffer.PartialReported = true;
            buffer.BuildPartial(out byte[] payload, out int prefixLength, out FragmentRange[] gaps, out FragmentRange[] covered);
            return FragmentOutcome.PartialPrefix(flags, evicted, payload, prefixLength, buffer.TotalLength, buffer.GetContributors(), gaps, covered);
        }

        // 9. Otherwise pending. A pending outcome carries no arrays, so an in-order fragment costs no allocation here.
        return FragmentOutcome.Pending(flags, evicted);
    }

    /// <summary>
    /// Releases every open datagram and resets the counters. Call when the session ends or the stack is reset.
    /// </summary>
    public void Clear()
    {
        foreach (FragmentBuffer<TKey> buffer in _Buffers.Values)
        {
            buffer.Release();
        }

        _Buffers.Clear();
        _Head = null;
        _Tail = null;
        _BufferedBytes = 0;
        Interlocked.Exchange(ref _ReassembledCount, 0);
        Interlocked.Exchange(ref _EvictedCount, 0);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Handles a refusal from the buffer. A datagram that held nothing before is removed again, so a refused fragment never
    /// leaves an empty datagram that occupies a slot. Whether an open datagram survives depends on the reason: a refusal that
    /// makes the datagram impossible releases it, a refusal of only this fragment keeps it.
    /// </summary>
    private FragmentOutcome _Refuse(
        FragmentBuffer<TKey> buffer,
        bool created,
        FragmentDiscardReason reason,
        FragmentOutcomeFlags flags,
        int evicted)
    {
        // BeyondEnd from the buffer refuses only this fragment (a stray segment behind the end of an open transfer);
        // every other reason means the datagram cannot complete correctly and is released.
        bool keep = reason == FragmentDiscardReason.BeyondEnd && buffer.FragmentCount > 0 && !created;
        if (!keep)
        {
            _Remove(buffer);
        }

        return FragmentOutcome.Discarded(reason, flags, evicted);
    }

    /// <summary>Releases and unlinks the open datagram of <paramref name="key"/>, if there is one.</summary>
    private void _RemoveKey(in TKey key)
    {
        if (_Buffers.TryGetValue(key, out FragmentBuffer<TKey>? buffer))
        {
            _Remove(buffer);
        }
    }

    /// <summary>Releases a datagram: returns its pooled arrays, removes it from the table and from the eviction order.</summary>
    private void _Remove(FragmentBuffer<TKey> buffer)
    {
        _ = _Buffers.Remove(buffer.Key);
        _Unlink(buffer);
        _BufferedBytes -= buffer.StoredBytes;
        buffer.Release();
    }

    /// <summary>
    /// Evicts the first datagram in eviction order that is not <paramref name="exclude"/> and counts the eviction.
    /// </summary>
    /// <returns>False when no other datagram is open.</returns>
    private bool _EvictOne(FragmentBuffer<TKey>? exclude)
    {
        FragmentBuffer<TKey>? victim = _Head;
        if (victim is not null && ReferenceEquals(victim, exclude))
        {
            victim = victim.Next;
        }

        if (victim is null)
        {
            return false;
        }

        _Remove(victim);
        Interlocked.Increment(ref _EvictedCount);
        return true;
    }

    /// <summary>Appends a datagram at the tail of the eviction order.</summary>
    private void _Link(FragmentBuffer<TKey> buffer)
    {
        buffer.Previous = _Tail;
        buffer.Next = null;
        if (_Tail is null)
        {
            _Head = buffer;
        }
        else
        {
            _Tail.Next = buffer;
        }

        _Tail = buffer;
    }

    /// <summary>Removes a datagram from the eviction order.</summary>
    private void _Unlink(FragmentBuffer<TKey> buffer)
    {
        if (buffer.Previous is null)
        {
            _Head = buffer.Next;
        }
        else
        {
            buffer.Previous.Next = buffer.Next;
        }

        if (buffer.Next is null)
        {
            _Tail = buffer.Previous;
        }
        else
        {
            buffer.Next.Previous = buffer.Previous;
        }

        buffer.Previous = null;
        buffer.Next = null;
    }

    /// <summary>Moves a datagram to the tail of the eviction order (most recently used or newly created).</summary>
    private void _MoveToTail(FragmentBuffer<TKey> buffer)
    {
        if (ReferenceEquals(_Tail, buffer))
        {
            return;
        }

        _Unlink(buffer);
        _Link(buffer);
    }

    #endregion
}
