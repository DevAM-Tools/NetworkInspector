// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The identity under which a response finds its request: service, method, client and session of the request id, the interface
/// version, and the capture interface (0 when the analysis is told to pair across interfaces).
/// </summary>
/// <param name="ServiceId">Service id.</param>
/// <param name="MethodId">Method id.</param>
/// <param name="ClientId">Client id.</param>
/// <param name="SessionId">Session id.</param>
/// <param name="InterfaceVersion">Interface version.</param>
/// <param name="Interface">The capture interface the message arrived on, or 0 when pairing ignores the interface.</param>
internal readonly record struct SomeIpTimingKey(
    ushort ServiceId, ushort MethodId, ushort ClientId, ushort SessionId, byte InterfaceVersion, int Interface);

/// <summary>
/// The pending requests of the method-timing analysis: requests that were seen and have not been answered yet.
/// <para>
/// <b>What it pairs:</b> a response takes the <em>latest</em> pending request of its key. Older requests of the same key stay
/// pending (a client that reuses a session id before the first answer arrives is answered in order), until a response takes them
/// or the pending limit forgets them.
/// </para>
/// <para>
/// <b>Limit <c>someip.timing_max_pending</c>:</b> range 1 to 10,000,000, default 65,536, a setting the end user may change.
/// Threat: requests that never get a response (a lost link, a flood of requests with distinct session ids) would fill the table
/// without bound. Posture: secure by default. At the limit the <em>oldest</em> pending request of the whole table is forgotten and its
/// key is remembered, so the response that arrives later says that its request was dropped by the limit instead of claiming that
/// none existed. The memory of forgotten keys is bounded by the same number: at most this many keys are remembered, and the oldest
/// memory is dropped first, so the whole structure holds at most twice the limit in entries.
/// </para>
/// <para>
/// <b>Structure:</b> every pending request is one node in two intrusive doubly linked lists, the global list in arrival order and the
/// list of its key. Taking the latest request of a key, forgetting the oldest request of the table and unlinking either from both
/// lists are all O(1), with no allocation beyond the node. Rejected alternative: a sorted structure by arrival serial, which would
/// cost O(log n) per request for an order that a list gives for free.
/// </para>
/// <para>
/// <b>Thread-safety:</b> none, by construction. Only the ordered first parse of a packet touches the tracker (the protocol's
/// <see cref="IngestGate"/> serializes it), a re-parse and a lazy populator never do: they read the pair that the first parse
/// stored in the response's effect. No lock and no volatile field, because there is exactly one thread at a time that can reach the
/// instance. Rejected alternative: a lock, which would protect against a caller that the gate already excludes.
/// </para>
/// </summary>
internal sealed class SomeIpTimingTracker
{
    #region Types

    /// <summary>One pending request, linked into the global arrival list and into the list of its key.</summary>
    private sealed class Node(in SomeIpTimingKey key, int packetId, long timestampNanos)
    {
        internal readonly SomeIpTimingKey Key = key;
        internal readonly int PacketId = packetId;
        internal readonly long TimestampNanos = timestampNanos;

        /// <summary>The next older request of the table.</summary>
        internal Node? GlobalOlder;

        /// <summary>The next newer request of the table.</summary>
        internal Node? GlobalNewer;

        /// <summary>The next older request of the same key.</summary>
        internal Node? KeyOlder;

        /// <summary>The next newer request of the same key.</summary>
        internal Node? KeyNewer;
    }

    /// <summary>The oldest and the latest pending request of one key.</summary>
    private sealed class Chain
    {
        internal Node? Oldest;
        internal Node? Latest;
    }

    #endregion

    #region Fields

    private readonly int _MaxPending;
    private readonly Dictionary<SomeIpTimingKey, Chain> _Chains = [];

    /// <summary>The keys whose request the limit forgot, newest memory last; a key can be listed again after a later eviction.</summary>
    private readonly Queue<SomeIpTimingKey> _ForgottenOrder = new();

    /// <summary>The same keys as a set, with the number of times each is listed in <see cref="_ForgottenOrder"/>.</summary>
    private readonly Dictionary<SomeIpTimingKey, int> _ForgottenCount = [];

    private Node? _Oldest;
    private Node? _Latest;

    #endregion

    #region Constructors

    /// <summary>Creates an empty tracker.</summary>
    /// <param name="maxPending">The pending limit (<c>someip.timing_max_pending</c>), at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPending"/> is below 1.</exception>
    internal SomeIpTimingTracker(int maxPending)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);
        _MaxPending = maxPending;
    }

    #endregion

    #region Properties

    /// <summary>The number of requests that wait for a response.</summary>
    internal int PendingCount { get; private set; }

    /// <summary>The number of forgotten keys that are remembered (bounded by the pending limit).</summary>
    internal int ForgottenCount => _ForgottenOrder.Count;

    #endregion

    #region Public API

    /// <summary>
    /// Registers a request as the latest pending request of its key, forgetting the oldest request of the table when the limit is
    /// reached.
    /// </summary>
    /// <param name="key">The key of the request.</param>
    /// <param name="packetId">The packet that holds the request.</param>
    /// <param name="timestampNanos">The capture time of the request in nanoseconds.</param>
    internal void AddRequest(in SomeIpTimingKey key, int packetId, long timestampNanos)
    {
        // Make room first, so that the table never holds more than the limit, not even for the duration of this call.
        if (PendingCount >= _MaxPending)
        {
            _ForgetOldest();
        }

        Node node = new(in key, packetId, timestampNanos);

        // Global list: the new request is the newest of the table.
        node.GlobalOlder = _Latest;
        if (_Latest is not null)
        {
            _Latest.GlobalNewer = node;
        }

        _Latest = node;
        _Oldest ??= node;

        // Key list: the new request is the latest of its key.
        if (!_Chains.TryGetValue(key, out Chain? chain))
        {
            chain = new();
            _Chains[key] = chain;
        }

        node.KeyOlder = chain.Latest;
        if (chain.Latest is not null)
        {
            chain.Latest.KeyNewer = node;
        }

        chain.Latest = node;
        chain.Oldest ??= node;
        PendingCount++;
    }

    /// <summary>
    /// Takes the latest pending request of a key.
    /// </summary>
    /// <param name="key">The key of the response.</param>
    /// <param name="requestPacket">The packet of the request on success.</param>
    /// <param name="delayNanos">Response time minus request time in nanoseconds on success.</param>
    /// <param name="responseNanos">The capture time of the response in nanoseconds.</param>
    /// <param name="forgotten">
    /// On failure: whether a request of this key was forgotten by the pending limit, so the caller can name the limit.
    /// </param>
    /// <returns>True when a request was pending.</returns>
    internal bool TryPairResponse(in SomeIpTimingKey key, long responseNanos, out int requestPacket, out long delayNanos, out bool forgotten)
    {
        requestPacket = -1;
        delayNanos = 0;
        forgotten = false;
        if (!_Chains.TryGetValue(key, out Chain? chain) || chain.Latest is not { } node)
        {
            forgotten = _ForgottenCount.ContainsKey(key);
            return false;
        }

        requestPacket = node.PacketId;

        // A response stamped earlier than its request (capture sources are not always ordered) pairs with a delay of zero rather than
        // a negative one: a negative round trip is not a measurement, and the order of the packets already shows that the capture is
        // out of order. Timestamps come from a capture file and are untrusted: the difference of two extreme values would wrap, so an
        // overflow saturates at the largest delay instead of turning into a small or negative one.
        long delay = 0;
        if (responseNanos >= node.TimestampNanos)
        {
            delay = unchecked(responseNanos - node.TimestampNanos);
            if (delay < 0)
            {
                delay = long.MaxValue;
            }
        }

        delayNanos = delay;
        _Unlink(node);
        return true;
    }

    #endregion

    #region Eviction

    /// <summary>Forgets the oldest request of the table and remembers its key, within the memory bound.</summary>
    private void _ForgetOldest()
    {
        Node? node = _Oldest;
        if (node is null)
        {
            return;
        }

        SomeIpTimingKey key = node.Key;
        _Unlink(node);

        // Remember the key; drop the oldest memory when the memory is as large as the pending limit.
        if (_ForgottenOrder.Count >= _MaxPending)
        {
            SomeIpTimingKey dropped = _ForgottenOrder.Dequeue();
            if (_ForgottenCount.TryGetValue(dropped, out int count))
            {
                if (count <= 1)
                {
                    _ = _ForgottenCount.Remove(dropped);
                }
                else
                {
                    _ForgottenCount[dropped] = count - 1;
                }
            }
        }

        _ForgottenOrder.Enqueue(key);
        _ForgottenCount[key] = _ForgottenCount.GetValueOrDefault(key) + 1;
    }

    /// <summary>Removes a node from the global list and from the list of its key.</summary>
    private void _Unlink(Node node)
    {
        if (node.GlobalOlder is not null)
        {
            node.GlobalOlder.GlobalNewer = node.GlobalNewer;
        }
        else
        {
            _Oldest = node.GlobalNewer;
        }

        if (node.GlobalNewer is not null)
        {
            node.GlobalNewer.GlobalOlder = node.GlobalOlder;
        }
        else
        {
            _Latest = node.GlobalOlder;
        }

        Chain chain = _Chains[node.Key];
        if (node.KeyOlder is not null)
        {
            node.KeyOlder.KeyNewer = node.KeyNewer;
        }
        else
        {
            chain.Oldest = node.KeyNewer;
        }

        if (node.KeyNewer is not null)
        {
            node.KeyNewer.KeyOlder = node.KeyOlder;
        }
        else
        {
            chain.Latest = node.KeyOlder;
        }

        if (chain.Latest is null)
        {
            _ = _Chains.Remove(node.Key);
        }

        PendingCount--;
    }

    #endregion
}
