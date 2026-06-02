// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Cross-packet presence index for protocols and index groups.
/// Protocols record their presence during parsing via <see cref="RecordGroupPresence"/>
/// and <see cref="RecordProtocolPresence"/>. The index is optionally attached to a
/// <see cref="Packet"/> before parsing — when absent, recording is a no-op (zero overhead).
/// Uses pre-allocated <see cref="RoaringBitmap"/> arrays and bit-vector dedup.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. The owning <see cref="Packet"/> /
/// parser thread is the only writer (<see cref="BeginPacket"/>, <see cref="EndPacket"/>,
/// <see cref="RecordGroupPresence"/>, <see cref="RecordProtocolPresence"/>); concurrent
/// readers querying <see cref="GetGroupBitmap"/>/<see cref="GetProtocolBitmap"/> after the
/// per-packet write is complete is supported. Concurrent writes are not.
/// </para>
/// </summary>
public sealed class PacketIndex : IPacketIndexReader
{
    private readonly Stack _Stack;
    private readonly RoaringBitmap[] _GroupBitmaps;
    private readonly RoaringBitmap[] _ProtocolBitmaps;

    // Bit-vector dedup: prevents duplicate bitmap inserts within the same packet
    private readonly ulong[] _GroupDedup;
    private readonly ulong[] _ProtocolDedup;

    // Starts at -1 ("no active packet") so the < 0 guard in RecordGroupPresence /
    // RecordProtocolPresence catches calls made before the very first BeginPacket.
    private int _CurrentPacketId = -1;

    /// <summary>
    /// Creates a packet index for the given stack, allocating bitmaps for all groups and protocols.
    /// </summary>
    /// <param name="stack">The protocol stack this index belongs to.</param>
    public PacketIndex(Stack stack)
    {
        _Stack = stack;
        int groupCount = stack.IndexGroupCount;
        int protoCount = stack.ProtocolCount;

        _GroupBitmaps = new RoaringBitmap[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            _GroupBitmaps[i] = new RoaringBitmap();
        }

        _ProtocolBitmaps = new RoaringBitmap[protoCount];
        for (int i = 0; i < protoCount; i++)
        {
            _ProtocolBitmaps[i] = new RoaringBitmap();
        }

        _GroupDedup = new ulong[(groupCount + 63) >> 6];
        _ProtocolDedup = new ulong[(protoCount + 63) >> 6];
    }

    /// <summary>Number of index groups tracked.</summary>
    public int GroupCount => _GroupBitmaps.Length;

    /// <summary>Number of protocols tracked.</summary>
    public int ProtocolCount => _ProtocolBitmaps.Length;

    /// <summary>The stack this index was created for.</summary>
    public Stack Stack => _Stack;

    /// <summary>
    /// Begins indexing a new packet. Must be called before parsing.
    /// Clears per-packet dedup state. Uses direct word zeroing for typical small bit-vectors
    /// (most stacks have fewer than 64 groups/protocols) to avoid Array.Clear overhead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginPacket(int packetId)
    {
        _CurrentPacketId = packetId;

        // Direct zeroing for typical 1-2 word case avoids Array.Clear method call overhead
        if (_GroupDedup.Length <= 2)
        {
            _GroupDedup[0] = 0;
            if (_GroupDedup.Length == 2)
            {
                _GroupDedup[1] = 0;
            }
        }
        else
        {
            Array.Clear(_GroupDedup);
        }

        if (_ProtocolDedup.Length <= 2)
        {
            _ProtocolDedup[0] = 0;
            if (_ProtocolDedup.Length == 2)
            {
                _ProtocolDedup[1] = 0;
            }
        }
        else
        {
            Array.Clear(_ProtocolDedup);
        }
    }

    /// <summary>
    /// Ends indexing for the current packet. Called after parsing completes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndPacket() =>
        // Reset current packet ID to catch misuse
        _CurrentPacketId = -1;

    /// <summary>
    /// Records that the current packet contains the given index group.
    /// Called by protocols during <see cref="Protocols.IProtocol.Parse"/>.
    /// Duplicate calls for the same group within one packet are deduplicated via bit-vector.
    /// </summary>
    /// <param name="groupId">Index group ID. Must originate from this index's own <see cref="Stack"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when called outside a <see cref="BeginPacket"/>/<see cref="EndPacket"/> pair.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range — typically because it was obtained from a different stack
    /// or is the sentinel <see cref="IndexGroupId.Invalid"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordGroupPresence(IndexGroupId groupId)
    {
        // Guard against off-lifecycle calls: _CurrentPacketId is set to -1 by EndPacket.
        // Without this check, (uint)(-1) = 4294967295 would be silently inserted into bitmaps.
        if (_CurrentPacketId < 0)
        {
            ThrowNoActivePacket();
        }

        int id = groupId.Value;
        if ((uint)id >= (uint)_GroupBitmaps.Length)
        {
            ThrowGroupIdOutOfRange(id);
        }
        int word = id >> 6;
        ulong bit = 1UL << (id & 63);

        ref ulong dedupWord = ref _GroupDedup[word];
        if ((dedupWord & bit) != 0)
        {
            return;
        }
        dedupWord |= bit;

        _GroupBitmaps[id].Add((uint)_CurrentPacketId);
    }

    /// <summary>
    /// Records that the current packet contains the given protocol.
    /// Called by protocols during <see cref="Protocols.IProtocol.Parse"/>.
    /// Duplicate calls for the same protocol within one packet are deduplicated via bit-vector.
    /// </summary>
    /// <param name="protocolId">Protocol ID. Must originate from this index's own <see cref="Stack"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when called outside a <see cref="BeginPacket"/>/<see cref="EndPacket"/> pair.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range — typically because it was obtained from a different stack
    /// or is the sentinel <see cref="ProtocolId.Invalid"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordProtocolPresence(ProtocolId protocolId)
    {
        // Guard against off-lifecycle calls: same rationale as RecordGroupPresence.
        if (_CurrentPacketId < 0)
        {
            ThrowNoActivePacket();
        }

        int id = protocolId.Value;
        if ((uint)id >= (uint)_ProtocolBitmaps.Length)
        {
            ThrowProtocolIdOutOfRange(id);
        }
        int word = id >> 6;
        ulong bit = 1UL << (id & 63);

        ref ulong dedupWord = ref _ProtocolDedup[word];
        if ((dedupWord & bit) != 0)
        {
            return;
        }
        dedupWord |= bit;

        _ProtocolBitmaps[id].Add((uint)_CurrentPacketId);
    }

    /// <summary>Cold-path helper: throws <see cref="InvalidOperationException"/> when a record method is called outside a Begin/EndPacket pair.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoActivePacket() =>
        throw new InvalidOperationException(
            "RecordGroupPresence/RecordProtocolPresence must be called between BeginPacket and EndPacket.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad group ID.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowGroupIdOutOfRange(int id) =>
        throw new ArgumentOutOfRangeException(
            nameof(id),
            id,
            $"Index group ID {id} is out of range for this index (GroupCount={_GroupBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad protocol ID.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowProtocolIdOutOfRange(int id) =>
        throw new ArgumentOutOfRangeException(
            nameof(id),
            id,
            $"Protocol ID {id} is out of range for this index (ProtocolCount={_ProtocolBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range for this index.
    /// </exception>
    public ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId)
    {
        // Validate before array access to produce a meaningful error instead of
        // an IndexOutOfRangeException with no context.
        if ((uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(groupId),
                groupId.Value,
                $"Index group ID {groupId.Value} is out of range for this index (GroupCount={_GroupBitmaps.Length}). " +
                "Ensure the ID was obtained from this index's own Stack.");
        }
        return _GroupBitmaps[groupId.Value].AsReadOnly();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    public ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId)
    {
        if ((uint)protocolId.Value >= (uint)_ProtocolBitmaps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocolId),
                protocolId.Value,
                $"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_ProtocolBitmaps.Length}). " +
                "Ensure the ID was obtained from this index's own Stack.");
        }
        return _ProtocolBitmaps[protocolId.Value].AsReadOnly();
    }

    /// <summary>
    /// Gets the bitmap of packets containing a specific field by resolving
    /// the field's index group via the stack metadata.
    /// Returns an empty bitmap if the field has no index group.
    /// </summary>
    public ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId)
    {
        IndexGroupId groupId = _Stack.GetFieldIndexGroup(fieldId);
        if (!groupId.IsValid)
        {
            // Field has no index group — return a shared empty bitmap, zero-allocation path.
            return ReadOnlyRoaringBitmap.Empty;
        }
        // Bounds-check the resolved group ID for consistency with GetGroupBitmap / TryGetFieldBitmap:
        // a valid-but-out-of-range ID (e.g. a field ID obtained from a different stack) must surface
        // a descriptive ArgumentOutOfRangeException, not a context-free IndexOutOfRangeException.
        if ((uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldId),
                groupId.Value,
                $"Field {fieldId.Value} resolves to index group ID {groupId.Value}, which is out of range for this index " +
                $"(GroupCount={_GroupBitmaps.Length}). Ensure the field ID was obtained from this index's own Stack.");
        }
        return _GroupBitmaps[groupId.Value].AsReadOnly();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range for this index.
    /// </exception>
    public long GroupCardinality(IndexGroupId groupId)
    {
        if ((uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(groupId),
                groupId.Value,
                $"Index group ID {groupId.Value} is out of range for this index (GroupCount={_GroupBitmaps.Length}).");
        }
        return _GroupBitmaps[groupId.Value].Cardinality;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    public long ProtocolCardinality(ProtocolId protocolId)
    {
        if ((uint)protocolId.Value >= (uint)_ProtocolBitmaps.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocolId),
                protocolId.Value,
                $"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_ProtocolBitmaps.Length}).");
        }
        return _ProtocolBitmaps[protocolId.Value].Cardinality;
    }

    /// <summary>Creates a presence query builder.</summary>
    public PresenceQuery Query() => new(this);

    // ── Try-variants ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap)
    {
        if ((uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }
        bitmap = _GroupBitmaps[groupId.Value].AsReadOnly();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap)
    {
        if ((uint)protocolId.Value >= (uint)_ProtocolBitmaps.Length)
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }
        bitmap = _ProtocolBitmaps[protocolId.Value].AsReadOnly();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap)
    {
        IndexGroupId groupId = _Stack.GetFieldIndexGroup(fieldId);
        if (!groupId.IsValid || (uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }
        bitmap = _GroupBitmaps[groupId.Value].AsReadOnly();
        return true;
    }

    /// <inheritdoc/>
    public bool TryGroupCardinality(IndexGroupId groupId, out long cardinality)
    {
        if ((uint)groupId.Value >= (uint)_GroupBitmaps.Length)
        {
            cardinality = 0;
            return false;
        }
        cardinality = _GroupBitmaps[groupId.Value].Cardinality;
        return true;
    }

    /// <inheritdoc/>
    public bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality)
    {
        if ((uint)protocolId.Value >= (uint)_ProtocolBitmaps.Length)
        {
            cardinality = 0;
            return false;
        }
        cardinality = _ProtocolBitmaps[protocolId.Value].Cardinality;
        return true;
    }
}

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
/// <b>Allocation model (in-place chaining):</b> The first set operation stores a reference to
/// the index-owned bitmap without copying. The second operation clones that bitmap once into a
/// private mutable buffer; all subsequent operations mutate the buffer in place via SIMD
/// <c>AndWith</c>/<c>OrWith</c>/<c>AndNotWith</c>/<c>XorWith</c>. This means an N-step query
/// allocates one bitmap clone instead of N. Index-owned bitmaps are never mutated. Terminal
/// methods (<see cref="ToBitmap"/>, <see cref="ToReadOnlyBitmap"/>) return detached copies so
/// the caller's view is not affected by any further chaining on the query.
/// </para>
/// </summary>
public ref struct PresenceQuery
{
    private readonly IPacketIndexReader _Index;

    // Two-phase result storage: see "Allocation model" in the type-level docs.
    //
    //   Phase 0 (no op yet):       _InitialResult == null && _MutableResult == null
    //   Phase 1 (one op done):     _InitialResult != null && _MutableResult == null
    //                              -> Holds a reference to an index-owned bitmap. Read-only.
    //   Phase 2 (>= two ops done): _InitialResult == null && _MutableResult != null
    //                              -> Owns a mutable clone. Subsequent ops mutate in place.
    private ReadOnlyRoaringBitmap? _InitialResult;
    private RoaringBitmap? _MutableResult;
    private bool _HasResult;

    /// <summary>Creates a presence query against the given packet index reader.</summary>
    internal PresenceQuery(IPacketIndexReader index)
    {
        _Index = index;
        _InitialResult = null;
        _MutableResult = null;
        _HasResult = false;
    }

    #region Select (initial set)

    /// <summary>Selects packets containing a specific protocol.</summary>
    public PresenceQuery SelectProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetProtocolBitmap(protocolId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>Selects packets containing a specific index group.</summary>
    public PresenceQuery SelectGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetGroupBitmap(groupId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>Selects packets containing a specific field (resolved via index group).</summary>
    public PresenceQuery SelectField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetFieldBitmap(fieldId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    #endregion

    #region AND

    /// <summary>ANDs with packets containing a specific protocol.</summary>
    public PresenceQuery AndProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetProtocolBitmap(protocolId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific index group.</summary>
    public PresenceQuery AndGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetGroupBitmap(groupId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    /// <summary>ANDs with packets containing a specific field.</summary>
    public PresenceQuery AndField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetFieldBitmap(fieldId);
        ApplyInitialOrAnd(bitmap);
        return this;
    }

    #endregion

    #region OR

    /// <summary>ORs with packets containing a specific protocol.</summary>
    public PresenceQuery OrProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetProtocolBitmap(protocolId);
        ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific index group.</summary>
    public PresenceQuery OrGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetGroupBitmap(groupId);
        ApplyOr(bitmap);
        return this;
    }

    /// <summary>ORs with packets containing a specific field.</summary>
    public PresenceQuery OrField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetFieldBitmap(fieldId);
        ApplyOr(bitmap);
        return this;
    }

    #endregion

    #region ANDNOT (difference)

    /// <summary>Removes packets containing a specific protocol from the result.</summary>
    public PresenceQuery AndNotProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetProtocolBitmap(protocolId);
        ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific index group from the result.</summary>
    public PresenceQuery AndNotGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetGroupBitmap(groupId);
        ApplyAndNot(bitmap);
        return this;
    }

    /// <summary>Removes packets containing a specific field from the result.</summary>
    public PresenceQuery AndNotField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetFieldBitmap(fieldId);
        ApplyAndNot(bitmap);
        return this;
    }

    #endregion

    #region XOR (symmetric difference)

    /// <summary>XORs with packets containing a specific protocol.</summary>
    public PresenceQuery XorProtocol(ProtocolId protocolId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetProtocolBitmap(protocolId);
        ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific index group.</summary>
    public PresenceQuery XorGroup(IndexGroupId groupId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetGroupBitmap(groupId);
        ApplyXor(bitmap);
        return this;
    }

    /// <summary>XORs with packets containing a specific field.</summary>
    public PresenceQuery XorField(FieldId fieldId)
    {
        ReadOnlyRoaringBitmap bitmap = _Index.GetFieldBitmap(fieldId);
        ApplyXor(bitmap);
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
        return _InitialResult?.Cardinality ?? 0;
    }

    /// <summary>Checks whether a specific packet matches.</summary>
    public readonly bool Contains(uint packetId)
    {
        if (_MutableResult is not null)
        {
            return _MutableResult.Contains(packetId);
        }
        return _InitialResult?.Contains(packetId) ?? false;
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
        return _InitialResult?.ToBitmap() ?? new RoaringBitmap();
    }

    /// <summary>
    /// Returns the result as a read-only bitmap (or <see cref="ReadOnlyRoaringBitmap.Empty"/> if
    /// no selection was made). The returned view is always over a detached snapshot — further
    /// chaining on the query does not mutate the returned bitmap.
    /// </summary>
    public readonly ReadOnlyRoaringBitmap ToReadOnlyBitmap()
    {
        if (_MutableResult is not null)
        {
            // Detach: caller-visible view must not change as further chained ops mutate _MutableResult.
            return _MutableResult.Clone().AsReadOnly();
        }
        return _InitialResult ?? ReadOnlyRoaringBitmap.Empty;
    }

    #endregion

    #region Internal helpers

    /// <summary>
    /// Promotes from phase 1 (immutable index-owned reference) to phase 2 (private mutable clone).
    /// After this call, <c>_MutableResult</c> is non-null and <c>_InitialResult</c> is null.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMutable()
    {
        if (_MutableResult is not null)
        {
            return;
        }

        // Clone the index-owned bitmap exactly once. From now on, in-place ops mutate this clone
        // instead of allocating a fresh result bitmap on every chain step.
        _MutableResult = _InitialResult is null ? new RoaringBitmap() : _InitialResult.ToBitmap();
        _InitialResult = null;
    }

    private void ApplyInitialOrAnd(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // Phase 0 -> 1: hold the index-owned bitmap by reference, no allocation.
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        // Second-or-later op: clone once into _MutableResult, then in-place AND.
        EnsureMutable();
        _MutableResult!.AndWith(bitmap.Inner);
    }

    private void ApplyOr(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        EnsureMutable();
        _MutableResult!.OrWith(bitmap.Inner);
    }

    private void ApplyAndNot(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            // ANDNOT with no existing result yields empty.
            _InitialResult = ReadOnlyRoaringBitmap.Empty;
            _HasResult = true;
            return;
        }

        EnsureMutable();
        _MutableResult!.AndNotWith(bitmap.Inner);
    }

    private void ApplyXor(ReadOnlyRoaringBitmap bitmap)
    {
        if (!_HasResult)
        {
            _InitialResult = bitmap;
            _HasResult = true;
            return;
        }

        EnsureMutable();
        _MutableResult!.XorWith(bitmap.Inner);
    }
    #endregion
}
