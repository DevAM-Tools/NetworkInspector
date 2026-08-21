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

    /// <summary>Session was rejected or evicted (cap limit, size overflow, or LRU eviction);
    /// the caller should emit a diagnostic.</summary>
    Dropped
}

/// <summary>
/// Result of a single <see cref="SomeIpTpReassembler.AddSegment"/> call.
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
/// <param name="Data">Fragment payload data.</param>
internal readonly record struct SomeIpTpFragment(uint Offset, byte[] Data);

/// <summary>
/// Tracks SOME/IP-TP fragment reassembly sessions.
/// Segments are identified by (ServiceID, MethodID, ClientID, SessionID) and
/// reassembled based on byte offsets from the TP header.
/// </summary>
internal sealed class SomeIpTpReassembler
{
    /// <summary>Maximum number of concurrent reassembly sessions.</summary>
    private const int _MaxSessions = 1024;

    /// <summary>Maximum total reassembled message size (1 MiB).</summary>
    private const int _MaxReassembledSize = 1024 * 1024; // bytes

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
        uint serial = ++_PacketSerial;
        bool lruEvicted = false;

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

        if (state.AddFragment(byteOffset, data, moreSegments, _MaxReassembledSize))
        {
            // Reassembly complete — extract the assembled payload and remove session.
            byte[] assembled = state.Assemble();
            _Sessions.Remove(key);
            return new SomeIpTpReassemblyResult { Outcome = SomeIpTpOutcome.Complete, Payload = assembled, LruEvicted = lruEvicted };
        }

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
        /// Sets <see cref="IsDropped"/> if the fragment would exceed <paramref name="maxSize"/>.
        /// </summary>
        internal bool AddFragment(uint offset, ReadOnlySpan<byte> data, bool moreSegments, int maxSize)
        {
            uint fragLen = (uint)data.Length;

            // Record total size when we see the last segment; guard against offset+fragLen overflow.
            if (!moreSegments)
            {
                if (fragLen > uint.MaxValue - offset)
                {
                    // Malformed: offset + length wraps uint32 — treat as a dropped session.
                    IsDropped = true;
                    return false;
                }
                _TotalSize = offset + fragLen;
            }

            // Overflow protection — mark dropped if received bytes would exceed size limit.
            // Guard uint addition to prevent wrap-around on crafted data.
            if (fragLen > uint.MaxValue - _ReceivedBytes || _ReceivedBytes + fragLen > (uint)maxSize)
            {
                IsDropped = true;
                return false;
            }

            _ReceivedBytes += fragLen;
            _Fragments.Add(new SomeIpTpFragment(offset, data.ToArray()));

            // Sort fragments by offset for gap detection
            _Fragments.Sort((a, b) => a.Offset.CompareTo(b.Offset));

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

            // Walk through sorted fragments checking for gaps
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
        internal byte[] Assemble()
        {
            int total = (int)(_TotalSize ?? 0);
            byte[] buffer = new byte[total];

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
