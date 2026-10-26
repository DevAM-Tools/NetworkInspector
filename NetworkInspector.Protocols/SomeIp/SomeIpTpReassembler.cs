// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Outcome of a <see cref="SomeIpTpReassembler.AddSegment"/> call, allowing callers to
/// distinguish in-progress accumulation from completion and from silent drops.
/// </summary>
internal enum SomeIpTpOutcome
{
    /// <summary>More fragments are still needed; no action required.</summary>
    InProgress,

    /// <summary>All fragments received; <see cref="SomeIpTpReassemblyResult.Payload"/> holds the reassembled bytes.</summary>
    Complete,

    /// <summary>
    /// This fragment's own session was abandoned and removed: its accumulated size would exceed the per-message limit, the
    /// segment was malformed (empty or not a multiple of 16 bytes although more segments follow, or beyond the end of the
    /// message), or the offset and length arithmetic was malformed. The caller should emit a diagnostic. Hitting the session
    /// cap does not produce this outcome (the new session is accepted and an old one is evicted instead, see
    /// <see cref="SomeIpTpReassemblyResult.LruEvicted"/>).
    /// </summary>
    Dropped
}

/// <summary>
/// Result of a single <see cref="SomeIpTpReassembler.AddSegment"/> call.
/// Stored verbatim in the protocol's effect store so a later re-parse of the same packet replays it without touching the
/// reassembler (the <paramref name="Payload"/> array therefore stays referenced for as long as the store lives).
/// Thread safety: immutable value type.
/// </summary>
/// <param name="Outcome">Outcome of this fragment addition.</param>
/// <param name="Payload">Reassembled payload; non-null only when <see cref="Outcome"/> is <see cref="SomeIpTpOutcome.Complete"/>.</param>
/// <param name="LruEvicted">
/// True when a separate (LRU-evicted) session was silently removed to make room for this one.
/// The caller should emit a diagnostic for the evicted session even if this session itself
/// is still in progress.
/// </param>
internal readonly record struct SomeIpTpReassemblyResult(
    SomeIpTpOutcome Outcome,
    byte[]? Payload,
    bool LruEvicted);

/// <summary>
/// Key for identifying a SOME/IP-TP reassembly session.
/// A session is uniquely identified by the tuple (ServiceId, MethodId, ClientId, SessionId).
/// </summary>
/// <remarks>
/// The key carries no network addresses or ports: segments of two different peers that use the same four ids end up in
/// the same session (known limitation: the reassembler has no access to the transport flow). Thread safety: immutable
/// value type.
/// </remarks>
/// <param name="ServiceId">SOME/IP Service ID.</param>
/// <param name="MethodId">SOME/IP Method ID.</param>
/// <param name="ClientId">SOME/IP Client ID.</param>
/// <param name="SessionId">SOME/IP Session ID.</param>
internal readonly record struct SomeIpTpReassemblyKey(
    ushort ServiceId,
    ushort MethodId,
    ushort ClientId,
    ushort SessionId);

/// <summary>
/// A single fragment in a SOME/IP-TP reassembly session.
/// </summary>
/// <param name="Offset">Byte offset where this fragment starts in the reassembled message.</param>
/// <param name="Data">
/// Fragment payload data. An owned copy: the frame buffer that carried the segment is not kept alive by the session.
/// </param>
internal readonly record struct SomeIpTpFragment(uint Offset, byte[] Data);

/// <summary>
/// Tracks SOME/IP-TP fragment reassembly sessions.
/// Segments are identified by (ServiceID, MethodID, ClientID, SessionID) and
/// reassembled based on byte offsets from the TP header.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> not thread-safe and holds no lock. It is mutated only by the ordered, single-threaded first
/// parse of each packet; later parses replay the recorded <see cref="SomeIpTpReassemblyResult"/> and never call it
/// (see <c>SomeIpProtocol</c>).
/// </para>
/// <para>
/// <b>Algorithm:</b> each segment is copied, appended to its session, and the session's fragments are kept sorted
/// by offset (a new fragment is inserted behind equal offsets, so a later overlapping segment wins). The last segment
/// (More Segments clear) fixes the total size; the message is complete when the sorted
/// fragments cover <c>[0, total)</c> without a gap (overlaps are tolerated). The fragments are then copied into one
/// buffer and the session is removed.
/// </para>
/// <para>
/// <b>Trust boundary:</b> offsets, lengths, and the number of sessions come straight from the wire (attacker
/// controlled), so memory is bounded by the two limits below and all uint arithmetic is overflow-guarded.
/// </para>
/// </remarks>
internal sealed class SomeIpTpReassembler
{
    /// <summary>
    /// Maximum number of concurrent reassembly sessions. Value: 1024. Threat: memory exhaustion by opening unbounded
    /// sessions with segments that never complete. Constant, not configurable (no setting exists for it); posture:
    /// secure by default. When the cap is reached the least recently used session is evicted and the new one is
    /// accepted instead of being rejected; the price is that an honest session can be evicted by a flood of new
    /// ones (reported through <see cref="SomeIpTpReassemblyResult.LruEvicted"/>).
    /// </summary>
    private const int _MaxSessions = 1024;

    /// <summary>
    /// Maximum total reassembled message size (1 MiB). Threat: a stream of segments that inflates one session without
    /// bound. Constant, not configurable; posture: secure by default. A session that would exceed it is dropped
    /// and reported (<see cref="SomeIpTpOutcome.Dropped"/>), never truncated silently. Together with
    /// <see cref="_MaxSessions"/> this bounds the retained fragment bytes to roughly 1 GiB in the worst case.
    /// </summary>
    private const int _MaxReassembledSize = 1024 * 1024; // bytes

    /// <summary>
    /// Alignment of every segment except the last, in bytes: the SOME/IP-TP offset field counts 16-byte units, so a
    /// segment that is followed by another one must end on a 16-byte boundary. A fact of the format, hence a constant.
    /// </summary>
    private const uint _SegmentAlignment = 16;

    /// <summary>Active reassembly sessions.</summary>
    private readonly Dictionary<SomeIpTpReassemblyKey, ReassemblyState> _Sessions = new();

    /// <summary>
    /// Monotonically increasing counter, incremented on each <see cref="AddSegment"/> call.
    /// Used to identify the least-recently-used session for eviction when the session cap is reached.
    /// </summary>
    private uint _PacketSerial;

    /// <summary>
    /// Adds a TP segment for reassembly and returns an explicit result indicating completion,
    /// in-progress accumulation, or that the session was dropped.
    /// </summary>
    /// <param name="key">Reassembly session identifier.</param>
    /// <param name="byteOffset">Byte offset from the TP header.</param>
    /// <param name="data">Fragment payload data.</param>
    /// <param name="moreSegments">True if more segments follow.</param>
    /// <returns>
    /// A <see cref="SomeIpTpReassemblyResult"/> indicating whether reassembly is complete
    /// (<see cref="SomeIpTpOutcome.Complete"/> with <see cref="SomeIpTpReassemblyResult.Payload"/>),
    /// still in progress (<see cref="SomeIpTpOutcome.InProgress"/>), or the session was dropped
    /// (<see cref="SomeIpTpOutcome.Dropped"/>).
    /// </returns>
    internal SomeIpTpReassemblyResult AddSegment(in SomeIpTpReassemblyKey key, uint byteOffset, ReadOnlySpan<byte> data, bool moreSegments)
    {
        // Every call advances the serial, including the ones that create or drop a session, so "lowest serial"
        // always means "touched longest ago". The serial is a uint and wraps after about 4 billion segments; a
        // wrap can only make a later eviction pick a slightly wrong victim, never corrupt a session.
        uint serial = ++_PacketSerial;
        bool lruEvicted = false;

        // Find or create the session. Creating at capacity first evicts the session with the oldest serial
        // (a linear scan over at most _MaxSessions entries, paid only when a new session arrives at the cap).
        if (!_Sessions.TryGetValue(key, out ReassemblyState? state))
        {
            // At capacity: evict the least-recently-used session before accepting the new one.
            if (_Sessions.Count >= _MaxSessions)
            {
                lruEvicted = _EvictLru();
            }

            state = new ReassemblyState(serial);
            _Sessions[key] = state;
        }
        else
        {
            state.LastSeenSerial = serial;
        }

        // Feed the fragment. On completion the session is removed, so the only live reference to the assembled
        // payload is the returned result (which the caller records for replay).
        if (state.AddFragment(byteOffset, data, moreSegments, _MaxReassembledSize))
        {
            // Reassembly complete — extract the assembled payload and remove session.
            byte[] assembled = state.Assemble();
            _Sessions.Remove(key);
            return new SomeIpTpReassemblyResult { Outcome = SomeIpTpOutcome.Complete, Payload = assembled, LruEvicted = lruEvicted };
        }

        // A dropped session is removed immediately so its buffered fragments are released; later segments of the
        // same message start a fresh session (which normally cannot complete because the earlier segments are gone).
        if (state.IsDropped)
        {
            _Sessions.Remove(key);
            return new SomeIpTpReassemblyResult { Outcome = SomeIpTpOutcome.Dropped, LruEvicted = lruEvicted };
        }

        return new SomeIpTpReassemblyResult { Outcome = SomeIpTpOutcome.InProgress, LruEvicted = lruEvicted };
    }

    /// <summary>
    /// Evicts the session that was last updated the longest time ago (lowest serial).
    /// Returns true if a session was actually evicted.
    /// </summary>
    private bool _EvictLru()
    {
        SomeIpTpReassemblyKey? victim = null;
        uint lowestSerial = uint.MaxValue;
        foreach (KeyValuePair<SomeIpTpReassemblyKey, ReassemblyState> pair in _Sessions)
        {
            if (pair.Value.LastSeenSerial < lowestSerial)
            {
                lowestSerial = pair.Value.LastSeenSerial;
                victim = pair.Key;
            }
        }
        if (victim.HasValue)
        {
            _Sessions.Remove(victim.Value);
            return true;
        }
        return false;
    }

    /// <summary>Returns the number of active reassembly sessions.</summary>
    internal int ActiveSessionCount => _Sessions.Count;

    /// <summary>Clears all reassembly sessions.</summary>
    internal void Clear() => _Sessions.Clear();

    /// <summary>
    /// Internal state for a single reassembly session. Collects fragments,
    /// detects gaps, and assembles the complete message.
    /// </summary>
    private sealed class ReassemblyState
    {
        /// <summary>Collected fragments, sorted by offset after each addition.</summary>
        private readonly List<SomeIpTpFragment> _Fragments = [];

        /// <summary>Total expected size (known once the last segment arrives).</summary>
        private uint? _TotalSize; // bytes

        /// <summary>Running count of total data bytes received.</summary>
        private uint _ReceivedBytes; // bytes

        /// <summary>
        /// Serial value from the last <see cref="SomeIpTpReassembler.AddSegment"/> call that touched this session.
        /// Used by the LRU eviction policy.
        /// </summary>
        internal uint LastSeenSerial { get; set; }

        /// <summary>True if this session should be removed without emitting a complete payload (overflow, etc.).</summary>
        internal bool IsDropped { get; private set; }

        /// <summary>Creates a new session, recording the serial at creation time.</summary>
        internal ReassemblyState(uint initialSerial)
        {
            LastSeenSerial = initialSerial;
        }

        /// <summary>
        /// Adds a fragment.  Returns true if the message is now complete.
        /// Sets <see cref="IsDropped"/> if the fragment would push the received bytes over <paramref name="maxSize"/>
        /// or if offset and length wrap around the 32-bit range.
        /// </summary>
        /// <param name="offset">Byte offset of the fragment inside the reassembled message.</param>
        /// <param name="data">Fragment payload; copied, so the caller's buffer is not retained.</param>
        /// <param name="moreSegments"><see langword="false"/> for the last segment, which fixes the total size.</param>
        /// <param name="maxSize">Upper limit for the total of received bytes of this session.</param>
        /// <returns><see langword="true"/> when the fragments now cover the whole message.</returns>
        internal bool AddFragment(uint offset, ReadOnlySpan<byte> data, bool moreSegments, int maxSize)
        {
            uint fragLen = (uint)data.Length;

            // SOME/IP-TP: every segment except the last carries a non-zero multiple of 16 bytes (the offset field counts
            // 16-byte units). A shorter, empty or unaligned non-last segment is malformed. Rejecting it also bounds the
            // work: without the rule, empty segments add list entries without adding received bytes, so the received
            // bytes limit below would never stop them while every addition still sorts the list. With it, a session
            // holds at most maxSize / 16 fragments.
            if (moreSegments && (fragLen == 0 || fragLen % _SegmentAlignment != 0))
            {
                IsDropped = true;
                return false;
            }

            // A segment that starts at or after the end fixed by the last segment cannot belong to the message. Without
            // this check it would stay in the sorted list behind the covered range and make the completeness walk treat
            // the end of the message as a gap, so the session could never complete. The session is discarded and
            // reported instead (rejected alternative: ignoring the segment silently, which hides a sender that
            // contradicts itself). An empty segment at the end is the last segment itself and is not beyond it.
            if (_TotalSize is uint knownTotal && fragLen > 0 && offset >= knownTotal)
            {
                IsDropped = true;
                return false;
            }

            // Record total size when we see the last segment; guard against offset+fragLen overflow.
            // The total is not checked against maxSize here on purpose: completion requires contiguous coverage of
            // [0, total) by stored fragments, and stored bytes are capped below, so an oversized total can never
            // complete (and therefore never reaches Assemble's allocation); it just stays incomplete until evicted.
            if (!moreSegments)
            {
                if (fragLen > uint.MaxValue - offset)
                {
                    // Malformed: offset + length wraps uint32 — treat as a dropped session.
                    IsDropped = true;
                    return false;
                }
                _TotalSize = offset + fragLen;

                // Segments stored before the end was known may lie beyond it (same rule as above). The list is sorted,
                // so the highest non-empty entry decides; empty entries are earlier "last" segments, which are not
                // beyond anything.
                for (int index = _Fragments.Count - 1; index >= 0; index--)
                {
                    if (_Fragments[index].Data.Length == 0)
                    {
                        continue;
                    }

                    if (_Fragments[index].Offset >= _TotalSize.Value)
                    {
                        IsDropped = true;
                        return false;
                    }

                    break;
                }
            }

            // Overflow protection — mark dropped if received bytes would exceed size limit.
            // Guard uint addition to prevent wrap-around on crafted data. This is the check that bounds the memory a
            // single session can hold (the session cap bounds the number of sessions).
            if (fragLen > uint.MaxValue - _ReceivedBytes || _ReceivedBytes + fragLen > (uint)maxSize)
            {
                IsDropped = true;
                return false;
            }

            // The span points into the frame buffer, so the bytes must be copied; the copy is the only allocation per
            // segment (plus the final assembly buffer). Overlapping or duplicate fragments are kept as they are and
            // resolved in Assemble.
            _ReceivedBytes += fragLen;

            // Keep the list sorted by offset for gap detection. The new fragment is inserted behind the entries with
            // the same or a lower offset, found by scanning from the end: segments normally arrive in order, which makes
            // the insertion constant time, whereas a full sort on every addition would cost O(n log n) per segment for
            // a session with many fragments. Placing an equal offset behind the older entry also makes the result of
            // overlapping segments deterministic: the later segment overwrites the earlier one in Assemble.
            int insertAt = _Fragments.Count;
            while (insertAt > 0 && _Fragments[insertAt - 1].Offset > offset)
            {
                insertAt--;
            }

            _Fragments.Insert(insertAt, new SomeIpTpFragment(offset, data.ToArray()));

            return IsComplete();
        }

        /// <summary>
        /// Checks if all fragments cover the total size without gaps.
        /// </summary>
        private bool IsComplete()
        {
            if (_TotalSize is not uint total)
            {
                return false;
            }

            // Walk through sorted fragments checking for gaps. "covered" is the end of the contiguous prefix
            // [0, covered) known so far; because the list is sorted by offset, the first fragment that starts
            // beyond it proves a hole, and the session stays open.
            uint covered = 0; // bytes
            foreach (SomeIpTpFragment frag in _Fragments)
            {
                if (frag.Offset > covered)
                {
                    return false; // Gap detected
                }

                // Guard against uint overflow from crafted fragment length.
                uint dataLen = (uint)frag.Data.Length;
                if (dataLen > uint.MaxValue - frag.Offset)
                {
                    // Malformed fragment: offset+length overflows; treat session as incomplete.
                    return false;
                }

                // Handle overlapping fragments
                uint fragEnd = frag.Offset + dataLen;
                if (fragEnd > covered)
                {
                    covered = fragEnd;
                }
            }

            return covered >= total;
        }

        /// <summary>
        /// Assembles all fragments into a single contiguous buffer.
        /// Must only be called when IsComplete() returns true.
        /// </summary>
        /// <returns>
        /// A new buffer of exactly the total message size. Overlapping fragments overwrite each other in offset order;
        /// bytes beyond the declared total are cut off.
        /// </returns>
        internal byte[] Assemble()
        {
            // Allocation size: IsComplete() succeeded, so the fragments cover [0, total) contiguously and the covered
            // range cannot exceed the received bytes, which are capped by the size limit. The (int) cast therefore
            // cannot overflow and the allocation is bounded by that limit.
            int total = (int)(_TotalSize ?? 0);
            byte[] buffer = new byte[total];

            // Copy in offset order, clamping each copy to the declared total so a fragment that extends past the end
            // (a malformed last segment, or a later fragment overlapping it) cannot write out of bounds. A fragment
            // starting at or after the total gives a non-positive length and is skipped.
            foreach (SomeIpTpFragment frag in _Fragments)
            {
                int start = (int)frag.Offset;
                int copyLen = Math.Min(frag.Data.Length, total - start);
                if (copyLen > 0)
                {
                    frag.Data.AsSpan(0, copyLen).CopyTo(buffer.AsSpan(start, copyLen));
                }
            }

            return buffer;
        }
    }
}
