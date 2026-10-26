// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Stateful;

/// <summary>
/// Remembers the verdict of every packet a filter instance has already evaluated.
/// <para>
/// Two bitmaps are needed rather than one: <c>evaluated</c> records that a decision exists and
/// <c>matched</c> records what it was. A single bitmap could not distinguish "evaluated to false"
/// from "not evaluated yet", and that distinction is what lets a stateful filter answer repeated
/// queries for an already-seen packet without replaying its state machine.
/// </para>
/// <para>
/// Roaring bitmaps keep this compact even for very large captures (dense id ranges collapse into a few
/// containers; a <see cref="HashSet{T}"/> of ids or a <c>bool[]</c> per packet would cost memory per packet).
/// </para>
/// <para>
/// Why it matters for correctness, not just speed: a stateful filter (<c>flank</c>) must feed each packet to its
/// tracker exactly once, so the cache is mandatory there and re-queries are answered from it. For stateless filters
/// the cache can be switched off (<see cref="FilterCompileOptions.EnableMatchCache"/>) so forward-only runs do not
/// grow it with every packet id.
/// </para>
/// <para>
/// Thread safety: not thread-safe for writers. The owning <c>Filter</c> calls <see cref="Store"/> from one
/// thread. The read-only views returned by <see cref="Matched"/> and <see cref="Evaluated"/> wrap the live bitmaps
/// and are safe to read from other threads while that single writer appends (a property of the bitmap type).
/// </para>
/// </summary>
internal sealed class MatchCache
{
    #region Fields

    /// <summary>Ids that have a cached verdict. Written first by <see cref="Store"/>.</summary>
    private RoaringBitmap _Evaluated = new();

    /// <summary>Ids whose cached verdict is "match". A subset of <see cref="_Evaluated"/>.</summary>
    private RoaringBitmap _Matched = new();

    #endregion

    #region Properties

    /// <summary>Number of packets with a cached verdict.</summary>
    public long EvaluatedCount => _Evaluated.Cardinality;

    /// <summary>
    /// Packets that matched so far, as a live view (later stores are visible through it, no copy is made). A view
    /// obtained before <see cref="Clear"/> keeps describing the discarded bitmap.
    /// </summary>
    public ReadOnlyRoaringBitmap Matched => _Matched.AsReadOnly();

    /// <summary>
    /// Packets that have been evaluated so far, as a live view (see <see cref="Matched"/>).
    /// </summary>
    public ReadOnlyRoaringBitmap Evaluated => _Evaluated.AsReadOnly();

    #endregion

    #region Access

    /// <summary>
    /// Reads a cached verdict. Returns <see langword="false"/> when the packet has not been evaluated (or its id is
    /// negative, which is never cached); <paramref name="matched"/> is then <see langword="false"/> too.
    /// </summary>
    public bool TryGet(PacketId packetId, out bool matched)
    {
        // Evaluated first: a missing evaluated bit means "unknown", which must not be confused with "no match".
        int value = packetId.Value;
        if (value < 0 || !_Evaluated.Contains((uint)value))
        {
            matched = false;
            return false;
        }

        matched = _Matched.Contains((uint)value);
        return true;
    }

    /// <summary>
    /// Stores a verdict. A negative id (an invalid packet id) is ignored. Storing the same id again with a different
    /// verdict does not clear an earlier "matched" bit; callers store each id once.
    /// </summary>
    public void Store(PacketId packetId, bool matched)
    {
        int value = packetId.Value;
        if (value < 0)
        {
            return;
        }

        // Evaluated is added before Matched, and the two additions are not atomic as a pair: a concurrent reader of
        // the views can briefly see a packet as evaluated but not yet matched.
        _Evaluated.Add((uint)value);
        if (matched)
        {
            _Matched.Add((uint)value);
        }
    }

    /// <summary>
    /// Forgets every cached verdict by replacing both bitmaps (this also releases their memory, unlike removing ids
    /// one by one). Must not run concurrently with readers of the views or with <see cref="Store"/>.
    /// </summary>
    public void Clear()
    {
        _Evaluated = new();
        _Matched = new();
    }

    #endregion
}
