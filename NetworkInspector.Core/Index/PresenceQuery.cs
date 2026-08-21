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
/// <c>index.Query().SelectProtocol(p).AndField(f)</c>. After the first call the result becomes
/// non-empty and subsequent <c>And*</c> calls perform a real intersection.
/// </para>
/// <para>
/// <c>OrField</c>/<c>OrGroup</c>/<c>OrProtocol</c> always perform a union with the current
/// result, treating an unset result as the empty bitmap. <c>AndNot*</c> and <c>Xor*</c> against
/// an unset result are no-ops in the AndNot case and equivalent to <c>Select*</c> in the Xor
/// case.
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
/// </summary>
public ref struct PresenceQuery
{
    // Concrete PacketIndex (not IPacketIndexReader) so bitmap lookups can inline.
    // Partial / mock readers that implement IPacketIndexReader still call through
    // PacketIndex.Query() when they wrap a real index (see PartialPacketIndexReader).
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
    private ReadOnlyRoaringBitmap? _InitialResult;
    private RoaringBitmap? _MutableResult;
    private bool _HasResult;

    /// <summary>Creates a presence query against the given packet index.</summary>
    internal PresenceQuery(PacketIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _Index = index;
        _InitialResult = null;
        _MutableResult = null;
        _HasResult = false;
    }

    #region Select (initial set)

    /// <summary>Selects packets containing a specific protocol.</summary>
    public PresenceQuery SelectProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>Selects packets containing a specific index group.</summary>
    public PresenceQuery SelectGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>Selects packets containing a specific field (resolved via index group).</summary>
    public PresenceQuery SelectField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    #endregion

    #region AND

    /// <summary>ANDs with packets containing a specific protocol.</summary>
    public PresenceQuery AndProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific index group.</summary>
    public PresenceQuery AndGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific field.</summary>
    public PresenceQuery AndField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyInitialOrAnd(bitmap);
        return this;
    }

    #endregion

    #region OR

    /// <summary>ORs with packets containing a specific protocol.</summary>
    public PresenceQuery OrProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific index group.</summary>
    public PresenceQuery OrGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific field.</summary>
    public PresenceQuery OrField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyOr(bitmap);
        return this;
    }

    #endregion

    #region ANDNOT (difference)

    /// <summary>Removes packets containing a specific protocol from the result.</summary>
    public PresenceQuery AndNotProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific index group from the result.</summary>
    public PresenceQuery AndNotGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific field from the result.</summary>
    public PresenceQuery AndNotField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyAndNot(bitmap);
        return this;
    }

    #endregion

    #region XOR (symmetric difference)

    /// <summary>XORs with packets containing a specific protocol.</summary>
    public PresenceQuery XorProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveProtocolBitmap(protocolId);
        _ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific index group.</summary>
    public PresenceQuery XorGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveGroupBitmap(groupId);
        _ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific field.</summary>
    public PresenceQuery XorField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _ResolveFieldBitmap(fieldId);
        _ApplyXor(bitmap);
        return this;
    }

    #endregion

    #region Terminal operations

    /// <summary>Returns the number of matching packets.</summary>
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

    /// <summary>Checks whether a specific packet matches.</summary>
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

    #region Internal helpers

    private ReadOnlyRoaringBitmap _ResolveProtocolBitmap(ProtocolId protocolId)
    {
        if (_Index.TryGetProtocolBitmap(protocolId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }
        throw new ArgumentOutOfRangeException(
            nameof(protocolId),
            protocolId.Value,
            $"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_Index.ProtocolCount}). " +
            "Ensure the ID was obtained from this index's own Stack.");
    }

    private ReadOnlyRoaringBitmap _ResolveGroupBitmap(IndexGroupId groupId)
    {
        if (_Index.TryGetGroupBitmap(groupId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }
        throw new ArgumentOutOfRangeException(
            nameof(groupId),
            groupId.Value,
            $"Index group ID {groupId.Value} is out of range for this index (GroupCount={_Index.GroupCount}). " +
            "Ensure the ID was obtained from this index's own Stack.");
    }

    private ReadOnlyRoaringBitmap _ResolveFieldBitmap(FieldId fieldId)
    {
        if (_Index.TryGetFieldBitmap(fieldId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap;
        }
        return _Index.GetFieldBitmap(fieldId);
    }

    /// <summary>
    /// Promotes from phase 1 (immutable index-owned reference) to phase 2 (private mutable clone).
    /// After this call, <c>_MutableResult</c> is non-null and <c>_InitialResult</c> is null.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EnsureMutable()
    {
        if (_MutableResult is not null)
        {
            return;
        }

        // Clone the index-owned bitmap exactly once. From now on, in-place ops mutate this clone
        // instead of allocating a fresh result bitmap on every chain step.
        _MutableResult = _InitialResult is ReadOnlyRoaringBitmap initial
            ? initial.ToBitmap()
            : new RoaringBitmap();
        _InitialResult = null;
    }

    private void _ApplyInitialOrAnd(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // Phase 0 -> 1: hold the index-owned bitmap by reference, no allocation.
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        // Second-or-later op: clone once into _MutableResult, then in-place AND.
        _EnsureMutable();
        _MutableResult!.AndWith(bitmap.Inner);
    }

    private void _ApplyOr(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        _EnsureMutable();
        _MutableResult!.OrWith(bitmap.Inner);
    }

    private void _ApplyAndNot(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // ANDNOT with no existing result yields empty.
            _InitialResult = ReadOnlyRoaringBitmap.Empty;
            _HasResult = true;
            return;
        }

        _EnsureMutable();
        _MutableResult!.AndNotWith(bitmap.Inner);
    }

    private void _ApplyXor(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        _EnsureMutable();
        _MutableResult!.XorWith(bitmap.Inner);
    }

    #endregion
}
