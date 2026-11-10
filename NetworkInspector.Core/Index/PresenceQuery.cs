// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Fluent query builder for PacketIndex presence queries.
/// Supports selecting by protocol, group, or field and combining with AND/OR.
/// <para>
/// <b>Initial-state semantics:</b> The <c>And*</c> methods (<see cref="AndProtocol"/>,
/// <see cref="AndGroup"/>, <see cref="AndField"/>) behave as <c>Select*</c> when called as the
/// very first operation on a fresh query (i.e. before any <c>Select*</c>/<c>Or*</c>). This makes
/// <c>index.Query().AndProtocol(p).AndField(f)</c> behave the same as
/// <c>index.Query().SelectProtocol(p).AndField(f)</c>. After the first call a result exists
/// (it may still be empty) and subsequent <c>And*</c> calls perform a real intersection. A <c>Select*</c> call always starts
/// the query again and discards the result built so far, so <c>Select*(a).Select*(b)</c> is the same as <c>Select*(b)</c>.
/// </para>
/// <para>
/// <c>OrField</c>/<c>OrGroup</c>/<c>OrProtocol</c> always perform a union with the current
/// result, treating an unset result as the empty bitmap. <c>AndNot*</c> against an unset result
/// yields an empty result (nothing to remove from), and <c>Xor*</c> against an unset result is
/// equivalent to <c>Select*</c>. In both cases a result exists afterwards.
/// </para>
/// <para>
/// <b>Allocation model:</b> The first set operation stores a live view of the index-owned
/// bitmap without copying. <see cref="Contains"/> on that view sees packets committed later.
/// A second operation clones that bitmap once into a private buffer that no longer grows
/// with the index — chaining is therefore a snapshot and should be used only when the
/// index is no longer growing. Subsequent operations mutate the buffer in place.
/// Prefer per-packet <see cref="Contains"/> on <see cref="PacketIndex"/> views while capture
/// is live. <see cref="ToBitmap"/> always returns a detached copy.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe; a query is a short-lived value owned by one thread. It is a
/// <c>ref struct</c> so the query itself never allocates and cannot be stored in a field or kept across an
/// <c>await</c>. The fluent methods mutate the query in place and return a copy of the struct; all copies share the
/// same private mutable bitmap, so continue with the returned value and do not branch from an earlier copy.
/// The in-place operations read the index bitmaps without the seqlock (see <see cref="RoaringBitmap.AndWith"/>), which
/// is why chaining requires an index that is no longer growing.
/// </para>
/// </summary>
public ref struct PresenceQuery
{
    #region Configuration and state

    /// <summary>
    /// The index whose bitmaps are queried. Concrete <see cref="PacketIndex"/> (not <see cref="IPacketIndexReader"/>) so bitmap
    /// lookups can inline. Partial / mock readers that implement <see cref="IPacketIndexReader"/> still call through
    /// <see cref="PacketIndex.Query"/> when they wrap a real index.
    /// </summary>
    private readonly PacketIndex _Index;

    // Two-phase result storage: see "Allocation model" in the type-level docs.
    //
    //   Phase 0 (no op yet):       !_InitialResult.HasValue && _MutableResult == null
    //   Phase 1 (one op done):     _InitialResult.HasValue && _MutableResult == null
    //                              -> Holds a view over an index-owned bitmap (or Empty after
    //                                 AndNot with no prior result). Read-only.
    //   Phase 2 (>= two ops done): !_InitialResult.HasValue && _MutableResult != null
    //                              -> Owns a mutable clone. Subsequent ops mutate in place.
    //
    // Nullable distinguishes "unset" from Empty (default struct); do not use IsEmpty for phase.

    /// <summary>
    /// The result of phase 1: a live view over an index-owned bitmap (or <see cref="ReadOnlyRoaringBitmap.Empty"/> after an
    /// ANDNOT without a prior result). <see langword="null"/> in phase 0 and in phase 2.
    /// </summary>
    private ReadOnlyRoaringBitmap? _InitialResult;

    /// <summary>
    /// The result of phase 2: a private clone that later operations mutate in place. <see langword="null"/> before the
    /// second operation. Never aliases an index-owned bitmap.
    /// </summary>
    private RoaringBitmap? _MutableResult;

    /// <summary>Whether a first operation ran (phase 1 or 2). Distinguishes "no selection yet" from an empty result.</summary>
    private bool _HasResult;

    /// <summary>Creates a presence query against the given packet index. Created through <see cref="PacketIndex.Query"/>.</summary>
    /// <param name="index">The index whose bitmaps are queried.</param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is <see langword="null"/>.</exception>
    internal PresenceQuery(PacketIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _Index = index;
        _InitialResult = null;
        _MutableResult = null;
        _HasResult = false;
    }

    #endregion

    #region Select (initial set)

    /// <summary>Starts a new result with the packets that contain a specific protocol; a result built so far is discarded.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    public PresenceQuery SelectProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplySelect(bitmap);
        return this;
    }

    /// <summary>Starts a new result with the packets that contain a specific index group; a result built so far is discarded.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    public PresenceQuery SelectGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplySelect(bitmap);
        return this;
    }

    /// <summary>
    /// Starts a new result with the packets that contain a specific field (resolved via index group); a result built so far is
    /// discarded.
    /// </summary>
    /// <param name="fieldId">Field ID of the index's own stack. A field without an index group selects nothing.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    public PresenceQuery SelectField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplySelect(bitmap);
        return this;
    }

    #endregion

    #region AND

    /// <summary>ANDs with packets containing a specific protocol. The first operation of a query acts as a select.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    public PresenceQuery AndProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific index group. The first operation of a query acts as a select.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    public PresenceQuery AndGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific field. The first operation of a query acts as a select.</summary>
    /// <param name="fieldId">Field ID of the index's own stack. A field without an index group matches nothing.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    public PresenceQuery AndField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    #endregion

    #region OR

    /// <summary>ORs with packets containing a specific protocol.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    public PresenceQuery OrProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific index group.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    public PresenceQuery OrGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific field.</summary>
    /// <param name="fieldId">Field ID of the index's own stack. A field without an index group adds nothing.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    public PresenceQuery OrField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyOr(bitmap);
        return this;
    }

    #endregion

    #region ANDNOT (difference)

    /// <summary>Removes packets containing a specific protocol from the result. Without a prior result the result is empty.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    public PresenceQuery AndNotProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific index group from the result. Without a prior result the result is empty.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    public PresenceQuery AndNotGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific field from the result. Without a prior result the result is empty.</summary>
    /// <param name="fieldId">Field ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    public PresenceQuery AndNotField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyAndNot(bitmap);
        return this;
    }

    #endregion

    #region XOR (symmetric difference)

    /// <summary>XORs with packets containing a specific protocol. Without a prior result this acts as a select.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    public PresenceQuery XorProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific index group. Without a prior result this acts as a select.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    public PresenceQuery XorGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific field. Without a prior result this acts as a select.</summary>
    /// <param name="fieldId">Field ID of the index's own stack.</param>
    /// <returns>This query, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    public PresenceQuery XorField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyXor(bitmap);
        return this;
    }

    #endregion

    #region Terminal operations

    /// <summary>
    /// Returns the number of matching packets. After a single operation this reads the live index bitmap and can grow
    /// while capture continues; after chained operations it is the cardinality of the private snapshot.
    /// </summary>
    /// <returns>The number of packets in the result; 0 when no selection was made.</returns>
    public readonly long Count()
    {
        if (_MutableResult is not null)
        {
            return _MutableResult.Cardinality;
        }

        if (_InitialResult is ReadOnlyRoaringBitmap initial)
        {
            return initial.Cardinality;
        }

        return 0;
    }

    /// <summary>
    /// Checks whether a specific packet matches. Zero-allocation. After a single operation this is a live view and sees
    /// packets committed later (preferred while capture is running); after chained operations it is a snapshot.
    /// </summary>
    /// <param name="packetId">Packet id to test.</param>
    /// <returns><see langword="true"/> when the packet is in the result; <see langword="false"/> when no selection was made.</returns>
    public readonly bool Contains(uint packetId)
    {
        if (_MutableResult is not null)
        {
            return _MutableResult.Contains(packetId);
        }

        if (_InitialResult is ReadOnlyRoaringBitmap initial)
        {
            return initial.Contains(packetId);
        }

        return false;
    }

    /// <summary>
    /// Returns the result as a new mutable bitmap (or empty if no selection was made).
    /// The returned bitmap is always a detached copy — mutations to it do not affect the
    /// query's internal state, and further chaining on the query does not affect the result.
    /// </summary>
    /// <returns>A new bitmap that the caller owns; empty when no selection was made.</returns>
    public readonly RoaringBitmap ToBitmap()
    {
        if (_MutableResult is not null)
        {
            // Detach so further query chaining cannot mutate the returned bitmap underneath the caller.
            return _MutableResult.Clone();
        }

        if (_InitialResult is ReadOnlyRoaringBitmap initial)
        {
            return initial.ToBitmap();
        }

        return new();
    }

    /// <summary>
    /// Returns the result as a read-only bitmap (or <see cref="ReadOnlyRoaringBitmap.Empty"/> if
    /// no selection was made).
    /// Phase 1 (single selection): returns a zero-allocation view over the index-owned bitmap;
    /// further index writes may change the underlying bitmap. Use <see cref="ToBitmap"/> for a
    /// detached copy. Phase 2 (chained ops): returns a detached clone; further chaining on the
    /// query does not mutate the returned bitmap.
    /// </summary>
    /// <returns>
    /// A live view of the index-owned bitmap after a single operation, a detached snapshot after chained operations, or
    /// <see cref="ReadOnlyRoaringBitmap.Empty"/> when no selection was made.
    /// </returns>
    public readonly ReadOnlyRoaringBitmap ToReadOnlyBitmap()
    {
        if (_MutableResult is not null)
        {
            // Detach: caller-visible view must not change as further chained ops mutate _MutableResult.
            return _MutableResult.Clone().AsReadOnly();
        }

        if (_InitialResult is ReadOnlyRoaringBitmap initial)
        {
            return initial;
        }

        return ReadOnlyRoaringBitmap.Empty;
    }

    #endregion

    #region Index lookup

    /// <summary>Looks up the bitmap of a protocol in the index.</summary>
    /// <param name="protocolId">Protocol ID of the index's own stack.</param>
    /// <returns>The live index bitmap of the protocol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> is out of range for the index.</exception>
    private ReadOnlyRoaringBitmap _ResolveProtocolBitmap(ProtocolId protocolId)
    {
        if (_Index.TryGetProtocolBitmap(protocolId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }
        throw new ArgumentOutOfRangeException(
            nameof(protocolId),
            protocolId.Value,
            Invariant($"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_Index.ProtocolCount}). ") +
            "Ensure the ID was obtained from this index's own Stack.");
    }

    /// <summary>Looks up the bitmap of an index group in the index.</summary>
    /// <param name="groupId">Index group ID of the index's own stack.</param>
    /// <returns>The live index bitmap of the group.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groupId"/> is out of range for the index.</exception>
    private ReadOnlyRoaringBitmap _ResolveGroupBitmap(IndexGroupId groupId)
    {
        if (_Index.TryGetGroupBitmap(groupId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }
        throw new ArgumentOutOfRangeException(
            nameof(groupId),
            groupId.Value,
            Invariant($"Index group ID {groupId.Value} is out of range for this index (GroupCount={_Index.GroupCount}). ") +
            "Ensure the ID was obtained from this index's own Stack.");
    }

    /// <summary>Looks up the bitmap of a field in the index through the field's index group.</summary>
    /// <param name="fieldId">Field ID of the index's own stack.</param>
    /// <returns>The live index bitmap of the field's group; <see cref="ReadOnlyRoaringBitmap.Empty"/> for a field without a group.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldId"/> is out of range for the index's stack.</exception>
    private ReadOnlyRoaringBitmap _ResolveFieldBitmap(FieldId fieldId)
    {
        if (_Index.TryGetFieldBitmap(fieldId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }

        // TryGetFieldBitmap reports false both for a field ID out of range and for a valid field without an index group.
        // GetFieldBitmap tells them apart: it returns the empty bitmap for the second case and throws the descriptive
        // ArgumentOutOfRangeException for the first.
        return _Index.GetFieldBitmap(fieldId);
    }

    #endregion

    #region Result phases

    /// <summary>
    /// Promotes from phase 1 (immutable index-owned reference) to phase 2 (private mutable clone).
    /// After this call, <c>_MutableResult</c> is non-null and <c>_InitialResult</c> is null.
    /// </summary>
    /// <returns>The private mutable result, the same instance that <c>_MutableResult</c> holds afterwards.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RoaringBitmap _EnsureMutable()
    {
        if (_MutableResult is not null)
        {
            return _MutableResult;
        }

        // Clone the index-owned bitmap exactly once. From now on, in-place ops mutate this clone
        // instead of allocating a fresh result bitmap on every chain step.
        RoaringBitmap clone = _InitialResult is ReadOnlyRoaringBitmap initial
            ? initial.ToBitmap()
            : new RoaringBitmap();
        _MutableResult = clone;
        _InitialResult = null;
        return clone;
    }

    // The four _Apply* methods share one shape: the first operation only stores a reference (no allocation, no copy);
    // the second operation pays for the single clone in _EnsureMutable; later operations mutate the clone in place.
    // The in-place operations read `bitmap.Inner` without the seqlock, which is only safe while that bitmap is not
    // appended to (see the type remarks).

    /// <summary>
    /// Applies a select: the result becomes <paramref name="bitmap"/> whatever the query held before. A result built so far,
    /// including its private mutable copy, is dropped, so a select always starts the query again.
    /// </summary>
    /// <param name="bitmap">The index bitmap that becomes the result; held by reference, no copy.</param>
    private void _ApplySelect(ReadOnlyRoaringBitmap bitmap)
    {
        _MutableResult = null;
        _InitialResult = bitmap;
        _HasResult = true;
    }

    /// <summary>
    /// Applies an AND: the first operation of a fresh query stores <paramref name="bitmap"/> as the result (it acts as a select), every
    /// later one intersects the result with it.
    /// </summary>
    /// <param name="bitmap">The index bitmap to select or intersect with.</param>
    private void _ApplyInitialOrAnd(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // Phase 0 -> 1: hold the index-owned bitmap by reference, no allocation.
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        // Second-or-later op: clone once into the mutable result, then in-place AND.
        RoaringBitmap mutableResult = _EnsureMutable();
        mutableResult.AndWith(bitmap.Inner);
    }

    /// <summary>
    /// Applies an OR: the first operation of the query stores <paramref name="bitmap"/> as the result (a union with the
    /// unset result), every later one unites the result with it.
    /// </summary>
    /// <param name="bitmap">The index bitmap to unite with.</param>
    private void _ApplyOr(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        RoaringBitmap mutableResult = _EnsureMutable();
        mutableResult.OrWith(bitmap.Inner);
    }

    /// <summary>
    /// Applies an ANDNOT: without a prior result the result becomes empty (nothing to remove from), otherwise the values of
    /// <paramref name="bitmap"/> are removed from the result.
    /// </summary>
    /// <param name="bitmap">The index bitmap whose values are removed.</param>
    private void _ApplyAndNot(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // ANDNOT with no existing result yields empty.
            _InitialResult = ReadOnlyRoaringBitmap.Empty;
            _HasResult = true;
            return;
        }

        RoaringBitmap mutableResult = _EnsureMutable();
        mutableResult.AndNotWith(bitmap.Inner);
    }

    /// <summary>
    /// Applies an XOR: the first operation of the query stores <paramref name="bitmap"/> as the result (a symmetric
    /// difference with the unset result), every later one XORs the result with it.
    /// </summary>
    /// <param name="bitmap">The index bitmap to combine with.</param>
    private void _ApplyXor(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        RoaringBitmap mutableResult = _EnsureMutable();
        mutableResult.XorWith(bitmap.Inner);
    }

    #endregion
}
