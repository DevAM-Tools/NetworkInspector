// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Manages TCP connection states and performs per-segment analysis (retransmission, out-of-order, duplicate ACK,
/// keep-alive, window checks, round trip times, connection phase).
/// <para>
/// This file holds the connection table. The analysis itself lives in the other parts of this partial class:
/// <c>TcpConnectionTracker.Analysis.cs</c> (the per-segment checks, in the order they must run) and
/// <c>TcpConnectionTracker.Phase.cs</c> (completeness and the connection state machine).
/// </para>
/// <para>
/// <b>Rules and known differences:</b> the classification of sequence anomalies follows the established rules of
/// passive TCP sequence analysis (the same order of checks and the same thresholds), and the result is cross-checked
/// against an independent dissector in the test suite, so the analysis flags carry the meaning users know from other
/// packet analyzers. It is a single pass over the packets: fields that need a later packet to be known
/// (<c>tcp.reassembled_in</c>, the final completeness value of a conversation on its early packets) are not reproduced,
/// because a packet that has been parsed is never amended.
/// </para>
/// <para>
/// A 4-entry inline LRU cache sits in front of the dictionary to exploit temporal
/// locality in TCP traffic (bursts from the same connection). Cache hits avoid the
/// full dictionary lookup. Promote-to-front uses a swap with position 0 (O(1),
/// avoids shifting the large key structs: two 128-bit addresses plus ports and discriminators).
/// Insert shifts entries only on cache miss. Because a hit only swaps with slot 0, slots 1..3 are
/// not kept in strict recency order: the cache is an approximation of LRU, which is enough for its purpose.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The tracker mutates connection state and is used only by the ordered,
/// single-threaded first parse of each packet; replays never touch it (they use the effects recorded at first parse).
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by TcpProtocol (field _ConnectionTracker)")]
internal sealed partial class TcpConnectionTracker
{
    #region Fields

    /// <summary>LRU cache capacity — 4 entries provide a good balance between
    /// hit rate and linear scan cost for typical network traffic patterns.</summary>
    private const int _CacheSize = 4;

    /// <summary>
    /// All tracked connections keyed by normalized connection key. The key's hash is seeded per process, so crafted
    /// address/port combinations cannot be chosen to collide. The number of entries is capped by the caller through
    /// the <c>maxStreams</c> argument of <see cref="TryGetOrCreate"/>.
    /// </summary>
    private readonly Dictionary<TcpConnectionKey, TcpConnectionState> _Connections = [];

    /// <summary>Monotonically increasing stream index counter.</summary>
    private uint _NextStreamIndex;

    /// <summary>Inline LRU cache — most recently used entry is at index 0.</summary>
    private readonly TcpConnectionKey[] _CacheKeys = new TcpConnectionKey[_CacheSize];

    /// <summary>Cached connection states corresponding to <see cref="_CacheKeys"/>.</summary>
    private readonly TcpConnectionState?[] _CacheValues = new TcpConnectionState?[_CacheSize];

    /// <summary>Number of valid entries in the LRU cache (0.._CacheSize).</summary>
    private int _CacheCount;

    #endregion

    #region Connection table

    /// <summary>
    /// Gets or creates the connection state for the given key. A SYN that cannot belong to the connection recorded for the
    /// key starts a new connection on the same address/port pair: this is how port reuse is recognized. The old state is
    /// dropped, the stream index advances, and <paramref name="reusedPorts"/> tells the caller to flag the segment and to drop
    /// the reassembly stream of the old connection.
    /// </summary>
    /// <remarks>
    /// A SYN or SYN-ACK starts a new connection when its sender already used a different initial sequence number on this
    /// pair, or when the recorded connection has already seen a FIN or RST (a finished connection cannot be continued by a
    /// SYN, and a retransmitted SYN with the same number before any close stays in the old connection). A replacement reuses
    /// the dictionary slot, so it does not consume a table slot and is also allowed when the table is full.
    /// </remarks>
    /// <param name="key">Normalized connection key of the segment.</param>
    /// <param name="maxStreams">
    /// Upper bound for the number of tracked connections (trust-boundary limit against memory exhaustion by a flood of
    /// distinct connections; set by the caller from the <c>tcp.max_tracked_streams</c> setting). Reaching it is an
    /// expected failure that the caller reports, not an exception; existing connections keep working.
    /// </param>
    /// <param name="isForward">Direction of the segment relative to the normalized key.</param>
    /// <param name="flags">TCP flags byte of the segment.</param>
    /// <param name="seqNum">Sequence number of the segment (the ISN when it is a SYN).</param>
    /// <param name="state">The connection state on success; <see langword="null"/> when the limit is reached.</param>
    /// <param name="reusedPorts"><see langword="true"/> when the segment started a new connection on a known pair.</param>
    /// <returns><see langword="true"/> when a state is available.</returns>
    internal bool TryGetOrCreate(
        in TcpConnectionKey key,
        int maxStreams,
        bool isForward,
        byte flags,
        uint seqNum,
        [NotNullWhen(true)] out TcpConnectionState? state,
        out bool reusedPorts)
    {
        reusedPorts = false;

        // New connection: refuse beyond the limit. The check sits after the lookups so connections that are already
        // tracked keep being analyzed when the table is full. No stream index is consumed on refusal.
        if (!_TryFind(in key, out state))
        {
            if (_Connections.Count >= maxStreams)
            {
                state = null;
                return false;
            }

            // Stream indices are handed out in order of first appearance and are what the user sees as tcp.stream.
            state = new TcpConnectionState { StreamIndex = _NextStreamIndex++ };
            _Connections[key] = state;
            _InsertCache(key, state);
            return true;
        }

        // Only a SYN can open a connection; every other segment belongs to the connection that is recorded.
        bool isSyn = (flags & 0x02) != 0;
        if (!isSyn)
        {
            return true;
        }

        // The sender's own flow holds the initial sequence number it used before. A different one, or any SYN after the
        // connection was closed or reset, means the old connection is over and this is a new one.
        TcpFlowState sender = isForward
            ? state.Forward
            : state.Reverse;
        bool closed = state.FinCount > 0 || state.ResetSeen;
        if (sender.IsnSet && (seqNum != sender.Isn || closed))
        {
            // Fresh state, next stream index, same dictionary slot: the old state becomes unreachable and is collected.
            // The cache entry is overwritten in place, not shifted.
            state = new TcpConnectionState { StreamIndex = _NextStreamIndex++ };
            _Connections[key] = state;
            _ReplaceCache(in key, state);
            reusedPorts = true;
        }

        return true;
    }

    /// <summary>Cache probe, then dictionary lookup; a dictionary hit is promoted into the cache.</summary>
    private bool _TryFind(in TcpConnectionKey key, [NotNullWhen(true)] out TcpConnectionState? state)
    {
        // Linear probe — sequential access from MRU ([0]) toward LRU.
        for (int i = 0; i < _CacheCount; i++)
        {
            if (_CacheKeys[i].Equals(key))
            {
                // Swap with MRU position [0] — O(1), avoids shifting all entries.
                if (i > 0)
                {
                    TcpConnectionState? hitValue = _CacheValues[i];
                    _CacheKeys[i] = _CacheKeys[0];
                    _CacheValues[i] = _CacheValues[0];
                    _CacheKeys[0] = key;
                    _CacheValues[0] = hitValue;
                }

                state = _CacheValues[0]!; // Invariant: slots 0.._CacheCount-1 hold a state, and a hit is inside that range.
                return true;
            }
        }

        // Cache miss: known connection, full dictionary lookup, then promote it into the cache.
        if (_Connections.TryGetValue(key, out state))
        {
            _InsertCache(key, state);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Inserts a connection at the front of the cache, shifting the others one slot back. The last slot falls out when
    /// the cache is full. Only runs on a cache miss, which is why the shift cost is acceptable.
    /// </summary>
    /// <param name="key">Connection key.</param>
    /// <param name="state">Connection state for <paramref name="key"/>.</param>
    private void _InsertCache(in TcpConnectionKey key, TcpConnectionState state)
    {
        int shiftCount = Math.Min(_CacheCount, _CacheSize - 1);
        for (int j = shiftCount; j > 0; j--)
        {
            _CacheKeys[j] = _CacheKeys[j - 1];
            _CacheValues[j] = _CacheValues[j - 1];
        }

        _CacheKeys[0] = key;
        _CacheValues[0] = state;

        if (_CacheCount < _CacheSize)
        {
            _CacheCount++;
        }
    }

    /// <summary>
    /// Points the cache slot that holds <paramref name="key"/> at a new state. The slot is found by key because the lookup
    /// that preceded the replacement already moved the entry to slot 0, but the search keeps the method correct on its own.
    /// A stale cache entry would resurrect the dropped connection on the next packet, which is why this must not be skipped.
    /// </summary>
    private void _ReplaceCache(in TcpConnectionKey key, TcpConnectionState state)
    {
        for (int i = 0; i < _CacheCount; i++)
        {
            if (_CacheKeys[i].Equals(key))
            {
                _CacheValues[i] = state;
                return;
            }
        }
    }

    #endregion
}
