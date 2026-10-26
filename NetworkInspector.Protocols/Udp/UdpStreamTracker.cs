// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Udp;

/// <summary>
/// Tracks UDP "streams" (conversations) by assigning a monotonically increasing
/// stream index to each unique (source address, destination address, source port,
/// destination port) 4-tuple. Unlike TCP, UDP is connectionless, so there is no
/// connection state — only a key → index mapping.
/// <para>
/// The key is normalized so that both directions of the same conversation
/// receive the same stream index.
/// </para>
/// <para>
/// A 4-entry inline LRU cache sits in front of the dictionary to exploit temporal
/// locality in network traffic (bursts from the same connection). Cache hits avoid
/// the full dictionary lookup (hash computation over the 128-bit addresses and a bucket probe).
/// </para>
/// <para>
/// Promote-to-front uses a swap with position 0 (O(1), avoids shifting the wide
/// <see cref="UdpConnectionKey"/> structs: two 128-bit addresses plus ports). Insert shifts entries only on cache miss.
/// </para>
/// <para>
/// <b>Thread safety:</b> not thread-safe. The tracker is mutated only by the ordered first-parse path of
/// <see cref="UdpProtocol"/> (serialized by the session); replays read the recorded stream index instead and never call it.
/// </para>
/// <para>
/// <b>Limit:</b> the dictionary gets one entry per distinct 4-tuple for the lifetime of the protocol instance, capped by the
/// caller through the <c>maxStreams</c> argument of <see cref="TryGetOrCreateStreamIndex"/> (the <c>udp.max_tracked_streams</c>
/// setting, like <c>tcp.max_tracked_streams</c> for TCP). Threat: a scan or flood of distinct UDP tuples would otherwise grow
/// this table, which lives as long as the protocol instance, without bound. A new tuple beyond the cap gets no stream; tuples
/// that are already tracked keep their index.
/// </para>
/// </summary>
internal sealed class UdpStreamTracker
{
    /// <summary>LRU cache capacity — 4 entries provide a good balance between
    /// hit rate and linear scan cost for typical network traffic patterns. A fixed constant of the cache design,
    /// not a limit on the number of streams.</summary>
    private const int _CacheSize = 4;

    /// <summary>Maps each unique conversation 4-tuple to its stream index.</summary>
    private readonly Dictionary<UdpConnectionKey, uint> _Streams = [];

    /// <summary>
    /// Monotonically increasing stream index counter. A <see cref="uint"/>: the caller caps the dictionary at
    /// <see cref="int.MaxValue"/> entries, so the counter cannot wrap.
    /// </summary>
    private uint _NextStreamIndex;

    /// <summary>Inline LRU cache — most recently used entry is at index 0.</summary>
    private readonly UdpConnectionKey[] _CacheKeys = new UdpConnectionKey[_CacheSize];

    /// <summary>Cached stream indices corresponding to <see cref="_CacheKeys"/>.</summary>
    private readonly uint[] _CacheValues = new uint[_CacheSize];

    /// <summary>Number of valid entries in the LRU cache (0.._CacheSize).</summary>
    private int _CacheCount;

    /// <summary>
    /// Returns the stream index for the given conversation key.
    /// If the key is seen for the first time, a new stream index is assigned, unless the table already holds
    /// <paramref name="maxStreams"/> conversations.
    /// Checks the inline LRU cache first; falls back to the dictionary on miss.
    /// </summary>
    /// <param name="key">Normalized UDP connection key.</param>
    /// <param name="maxStreams">
    /// Upper bound for the number of tracked conversations (trust-boundary limit against memory exhaustion by a flood of
    /// distinct tuples; set by the caller from the <c>udp.max_tracked_streams</c> setting). Reaching it is an expected failure
    /// that the caller reports, not an exception; tracked conversations keep working.
    /// </param>
    /// <param name="streamIndex">The stream index on success; 0 when the limit is reached.</param>
    /// <returns><see langword="true"/> when a stream index is available; <see langword="false"/> for a new key at the limit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetOrCreateStreamIndex(in UdpConnectionKey key, int maxStreams, out uint streamIndex)
    {
        // Linear probe — sequential access from MRU ([0]) toward LRU. With at most four entries a straight scan over
        // adjacent array slots is cheaper than hashing the key, and a burst of packets from one conversation
        // hits at index 0 on the first comparison.
        for (int i = 0; i < _CacheCount; i++)
        {
            if (_CacheKeys[i].Equals(key))
            {
                uint cachedIndex = _CacheValues[i];

                // Swap with MRU position [0] — O(1), avoids shifting all entries.
                // Approximate LRU: the old MRU moves to the hit position.
                if (i > 0)
                {
                    _CacheKeys[i] = _CacheKeys[0];
                    _CacheValues[i] = _CacheValues[0];
                    _CacheKeys[0] = key;
                    _CacheValues[0] = cachedIndex;
                }

                streamIndex = cachedIndex;
                return true;
            }
        }

        // Cache miss — fall through to dictionary. A key never seen before gets the next index; indices are never reused
        // or removed, so a stream index identifies one conversation for the whole session. A new key is refused beyond the
        // limit; the check sits after the lookups so conversations that are already tracked keep their index when the table
        // is full, and no index is consumed by a refusal.
        uint index;
        if (_Streams.TryGetValue(key, out uint existingIndex))
        {
            index = existingIndex;
        }
        else if (_Streams.Count >= maxStreams)
        {
            streamIndex = 0;
            return false;
        }
        else
        {
            index = _NextStreamIndex++;
            _Streams[key] = index;
        }

        // Insert at MRU position [0] — shift existing entries toward LRU; when the cache is full the LRU entry (last slot)
        // falls out, which is harmless because the dictionary still holds it.
        // Only runs on cache miss, so the shift cost is amortized.
        int shiftCount = Math.Min(_CacheCount, _CacheSize - 1);
        for (int j = shiftCount; j > 0; j--)
        {
            _CacheKeys[j] = _CacheKeys[j - 1];
            _CacheValues[j] = _CacheValues[j - 1];
        }

        _CacheKeys[0] = key;
        _CacheValues[0] = index;

        if (_CacheCount < _CacheSize)
        {
            _CacheCount++;
        }

        streamIndex = index;
        return true;
    }

    /// <summary>Number of tracked UDP streams.</summary>
    internal int Count => _Streams.Count;

}

/// <summary>
/// Bidirectional UDP connection key, normalized so the numerically lower
/// (address, port) pair is always first. This ensures the same conversation
/// maps to the same key regardless of packet direction.
/// <para>
/// IPv4 addresses are stored as IPv4-mapped IPv6 (::ffff:a.b.c.d)
/// so all addresses use a uniform 128-bit representation.
/// </para>
/// <para>
/// The hash is <see cref="HashCode"/>, which is seeded randomly per process. The keys come from captured, possibly
/// crafted packets: an unkeyed mix would let colliding conversations be computed offline and turn every dictionary
/// insert into a long probe (denial of service). Every field is added as 32-bit words, so no folding step creates
/// collisions of its own. Hash values are not persisted or shown.
/// </para>
/// </summary>
internal readonly struct UdpConnectionKey : IEquatable<UdpConnectionKey>
{
    // Immutable value type, hence safe to share between threads. The normalization in the constructor (lower pair first,
    // ports as tie-breaker) makes both directions of a conversation produce an identical key; the direction itself is
    // not part of the key.

    /// <summary>Normalized lower (address, port) pair — address component.</summary>
    private readonly UInt128 _Addr1;

    /// <summary>Normalized higher (address, port) pair — address component.</summary>
    private readonly UInt128 _Addr2;

    /// <summary>Normalized lower (address, port) pair — port component.</summary>
    private readonly ushort _Port1;

    /// <summary>Normalized higher (address, port) pair — port component.</summary>
    private readonly ushort _Port2;

    /// <summary>Creates a normalized connection key from two endpoints.</summary>
    /// <param name="srcAddr">Source IP address (as UInt128).</param>
    /// <param name="dstAddr">Destination IP address (as UInt128).</param>
    /// <param name="srcPort">Source UDP port.</param>
    /// <param name="dstPort">Destination UDP port.</param>
    internal UdpConnectionKey(UInt128 srcAddr, UInt128 dstAddr, ushort srcPort, ushort dstPort)
    {
        // Normalize: lower (addr, port) pair first for consistent hashing.
        // Compare addresses first, then ports as tiebreaker.
        if (srcAddr < dstAddr || (srcAddr == dstAddr && srcPort <= dstPort))
        {
            _Addr1 = srcAddr;
            _Port1 = srcPort;
            _Addr2 = dstAddr;
            _Port2 = dstPort;
        }
        else
        {
            _Addr1 = dstAddr;
            _Port1 = dstPort;
            _Addr2 = srcAddr;
            _Port2 = srcPort;
        }
    }

    /// <summary>Creates a connection key from raw IPv4 addresses (as 32-bit values).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static UdpConnectionKey FromIPv4(uint srcIp, uint dstIp, ushort srcPort, ushort dstPort)
    {
        // Map IPv4 to IPv4-mapped IPv6: ::ffff:a.b.c.d
        UInt128 srcAddr = new(0, 0x0000_FFFF_0000_0000UL | srcIp);
        UInt128 dstAddr = new(0, 0x0000_FFFF_0000_0000UL | dstIp);
        return new UdpConnectionKey(srcAddr, dstAddr, srcPort, dstPort);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(UdpConnectionKey other) =>
        _Addr1 == other._Addr1 && _Addr2 == other._Addr2 &&
        _Port1 == other._Port1 && _Port2 == other._Port2;

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is UdpConnectionKey other && Equals(other);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // Process-seeded hash over 32-bit words: injective input, unpredictable buckets for crafted keys.
        HashCode hash = default;
        ConnectionKeyHash.AddAddress(ref hash, _Addr1);
        ConnectionKeyHash.AddAddress(ref hash, _Addr2);
        hash.Add(((uint)_Port1 << 16) | _Port2);
        return hash.ToHashCode();
    }
}
