// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Cross-packet presence index for protocols and index groups.
/// Protocols record their presence during parsing via <see cref="RecordGroupPresence"/>
/// and <see cref="RecordProtocolPresence"/>. The index is optionally attached to a
/// <see cref="Packet"/> before parsing — when absent, recording is a no-op (zero overhead).
/// Uses pre-allocated <see cref="RoaringBitmap"/> arrays and bit-vector dedup.
/// <para>
/// <b>Thread-safety:</b> Single-writer / multi-reader. The parser thread is the only writer
/// (<see cref="BeginPacket"/>, <see cref="EndPacket"/>, <see cref="RecordGroupPresence"/>,
/// <see cref="RecordProtocolPresence"/>). Concurrent readers may hold
/// <see cref="ReadOnlyRoaringBitmap"/> views returned by <see cref="GetGroupBitmap"/>,
/// <see cref="GetProtocolBitmap"/> and related APIs and query them at their own pace while
/// the writer continues to append packets. Those views alias the live index bitmaps: new
/// packet IDs become visible on the same view without obtaining another one. Per-bitmap
/// seqlocks in <see cref="RoaringBitmap"/> retry a reader that overlaps an in-flight
/// <see cref="RoaringBitmap.Add"/>. Concurrent writes of a <i>new</i> packet are not supported.
/// A later parse of an already indexed packet — including <c>Packet.TryParse</c> when
/// <see cref="ParseOptions.Index"/> is set, from any thread — is a no-op for this index:
/// <see cref="TryBeginPacket"/> returns
/// <see langword="false"/> and no bitmap is mutated.
/// </para>
/// </summary>
public sealed class PacketIndex : IPacketIndexReader
{
    #region Fields

    private readonly RoaringBitmap[] _GroupBitmaps;
    private readonly RoaringBitmap[] _ProtocolBitmaps;

    // Bit-vector dedup: one bit per group / protocol id, set while the current packet is parsed. Parsers may report the
    // same presence many times per packet; a bit test costs a few instructions, whereas a bitmap Add would take the
    // bitmap's write section (two fences) plus a container lookup. The bitmaps are therefore written once per set bit in
    // EndPacket (_CommitDedupToBitmaps), in ascending id order, and RollbackCurrentPacket can discard a failed parse by
    // clearing the vectors. Single writer only; not shared with readers, so the words are plain fields.
    private readonly ulong[] _GroupDedup;
    private readonly ulong[] _ProtocolDedup;

    // Starts at -1 ("no active packet") so the < 0 guard in RecordGroupPresence /
    // RecordProtocolPresence catches calls made before the very first BeginPacket.
    // A plain field on purpose: only the single writer thread reads or writes it. Replaying threads leave
    // TryBeginPacket before they would read it (see _MaxIndexedPacketId).
    private int _CurrentPacketId = -1;

    /// <summary>
    /// When <see langword="true"/>, the current Begin/End pair is a replay of an already indexed
    /// packet: <see cref="RecordGroupPresence"/> / <see cref="RecordProtocolPresence"/> return
    /// without writing, and <see cref="EndPacket"/> does not commit. Used by the void
    /// <see cref="BeginPacket"/> API so tests can pair EndPacket after a no-op Begin.
    /// Single-writer only; <see cref="Packet"/> uses <see cref="TryBeginPacket"/> on the hot path
    /// and never opens this dummy session.
    /// </summary>
    private bool _SuppressCommit;

    /// <summary>
    /// Packet ids that have completed <see cref="EndPacket"/> at least once.
    /// <see cref="RoaringBitmap.Contains"/> is safe for concurrent readers (seqlock) so
    /// <see cref="TryBeginPacket"/> can reject a replay without taking the writer path.
    /// </summary>
    private readonly RoaringBitmap _IndexedPackets = new();

    /// <summary>
    /// Highest packet id that <see cref="EndPacket"/> committed, or -1. An id above it cannot be in
    /// <see cref="_IndexedPackets"/>, so <see cref="TryBeginPacket"/> skips the seqlock read for every first parse of
    /// a monotonic ingest. Written only by the single writer, read by any thread that replays a packet: volatile.
    /// <see cref="EndPacket"/> stores it before <see cref="RoaringBitmap.Add"/>, so a reader that sees an old value
    /// sees a moment at which the id was not yet in the bitmap either — the same answer the bitmap would give.
    /// </summary>
    private volatile int _MaxIndexedPacketId = -1;

    /// <summary>
    /// First 65 536-id chunk (high 16 bits of the packet id) not yet run-optimized. Packets are indexed mostly in
    /// order, so once a packet of a higher chunk commits, every lower chunk is complete and its containers are
    /// converted to run containers where that is smaller (<see cref="RoaringBitmap.RunOptimizeKeys"/>). Written and
    /// read only by the single writer in <see cref="EndPacket"/>.
    /// </summary>
    private int _NextChunkToOptimize;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates a packet index for the given stack, allocating bitmaps for all groups and protocols.
    /// </summary>
    /// <param name="stack">The protocol stack this index belongs to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    public PacketIndex(Stack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        Stack = stack;
        int groupCount = stack.IndexGroupCount;
        int protoCount = stack.ProtocolCount;

        _GroupBitmaps = new RoaringBitmap[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            _GroupBitmaps[i] = new();
        }

        _ProtocolBitmaps = new RoaringBitmap[protoCount];
        for (int i = 0; i < protoCount; i++)
        {
            _ProtocolBitmaps[i] = new();
        }

        // One bit per id, rounded up to whole 64-bit words. Stacks with up to 128 groups or protocols need at most two
        // words, which is the case _ClearDedup special-cases.
        _GroupDedup = new ulong[(groupCount + 63) >> 6];
        _ProtocolDedup = new ulong[(protoCount + 63) >> 6];
    }

    /// <summary>
    /// Begins indexing a new packet. Must be called before parsing.
    /// A second call for a packet id that already completed <see cref="EndPacket"/> is a no-op
    /// session: subsequent record calls and the matching <see cref="EndPacket"/> do nothing, so
    /// bitmaps are not mutated twice. Nested begin (a second call before EndPacket) still throws.
    /// </summary>
    /// <param name="packetId">Packet identifier in the range 0 … <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called while a packet is already being indexed (missing <see cref="EndPacket"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="packetId"/> is negative or exceeds <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginPacket(int packetId)
    {
        if (TryBeginPacket(packetId))
        {
            return;
        }

        // Already indexed. A dummy session is only legal on an idle index; opening one while
        // another id is in flight would overwrite _CurrentPacketId and suppress that commit.
        if (_CurrentPacketId >= 0)
        {
            _ThrowNestedBeginPacket();
        }

        _CurrentPacketId = packetId;
        _SuppressCommit = true;
    }

    /// <summary>
    /// Attempts to begin indexing <paramref name="packetId"/>.
    /// Returns <see langword="true"/> when this is the first index session for that id and the
    /// caller must invoke <see cref="EndPacket"/> (and may record presence).
    /// Returns <see langword="false"/> when the id was already indexed — even while another id's
    /// session is open — so concurrent replays only read <see cref="_IndexedPackets"/>.
    /// Opening a <i>new</i> id still throws until the in-flight session calls
    /// <see cref="EndPacket"/>.
    /// </summary>
    /// <param name="packetId">Packet identifier in the range 0 … <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.</param>
    /// <returns><see langword="true"/> when a live index session was opened.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="packetId"/> is not yet indexed and a packet is already being
    /// indexed (missing <see cref="EndPacket"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="packetId"/> is negative or exceeds <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryBeginPacket(int packetId)
    {
        ArrayIndexIdRange.ValidateIndexOrThrow(packetId, nameof(packetId));

        // Replay check. Ids above the highest committed id cannot be indexed yet, so the common first parse of a
        // monotonic ingest skips the seqlock read of _IndexedPackets entirely; only ids at or below it need the bitmap.
        // This path runs on any thread (replays) and touches no writer-only state.
        if (packetId <= _MaxIndexedPacketId && _IndexedPackets.Contains((uint)packetId))
        {
            return false;
        }

        // From here on this is the single writer opening a new session. A second open session is a caller bug.
        if (_CurrentPacketId >= 0)
        {
            _ThrowNestedBeginPacket();
        }

        // Open the session on clean dedup vectors. EndPacket already leaves them clear, so this is a cheap safeguard that
        // a new packet can never inherit presence bits from an earlier one.
        _CurrentPacketId = packetId;
        _SuppressCommit = false;
        _ClearDedup(_GroupDedup);
        _ClearDedup(_ProtocolDedup);
        return true;
    }

    /// <summary>
    /// Ends indexing for the current packet. Commits all deduped group/protocol presence
    /// recorded since <see cref="BeginPacket"/> into the persistent bitmaps.
    /// A no-op session opened for an already indexed packet commits nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called without a matching <see cref="BeginPacket"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndPacket()
    {
        if (_CurrentPacketId < 0)
        {
            _ThrowNoActivePacketEnd();
        }

        if (!_SuppressCommit)
        {
            // Write the deduplicated presence bits: one bitmap Add per distinct group / protocol of this packet.
            _CommitDedupToBitmaps(_GroupDedup, _GroupBitmaps);
            _CommitDedupToBitmaps(_ProtocolDedup, _ProtocolBitmaps);

            // Mark the id as indexed only after its presence bits are in the bitmaps, so a replay that sees the id as
            // indexed never overlaps a half-committed packet. The shortcut bound must be raised before the bitmap add
            // (see _MaxIndexedPacketId). A rolled-back packet is marked as well: it was parsed once, and a replay must
            // not index it a second time.
            if (_CurrentPacketId > _MaxIndexedPacketId)
            {
                _MaxIndexedPacketId = _CurrentPacketId;
            }

            _IndexedPackets.Add((uint)_CurrentPacketId);
            _ClearDedup(_GroupDedup);
            _ClearDedup(_ProtocolDedup);

            // The high 16 bits of the id are the roaring chunk key; chunks below the current one are complete and get
            // converted to run containers (cheap no-op unless a new chunk was entered).
            _RunOptimizeCompletedChunks((int)((uint)_CurrentPacketId >> 16));
        }

        _SuppressCommit = false;
        _CurrentPacketId = -1;
    }

    /// <summary>
    /// Rolls back all index contributions for the current packet without committing them.
    /// Clears per-packet dedup state so a subsequent <see cref="EndPacket"/> does not insert
    /// the current packet ID into any group or protocol bitmap. The session stays open:
    /// <see cref="EndPacket"/> must still be called, and it still marks the packet id as indexed, so a later
    /// parse of the same id is a no-op replay and is not indexed again. Does nothing when no session is open.
    /// Used when parsing a packet failed after some presence had already been recorded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RollbackCurrentPacket()
    {
        if (_CurrentPacketId < 0)
        {
            return;
        }

        _ClearDedup(_GroupDedup);
        _ClearDedup(_ProtocolDedup);
    }

    #endregion

    #region Properties

    /// <summary>Number of index groups tracked.</summary>
    public int GroupCount => _GroupBitmaps.Length;

    /// <summary>Number of protocols tracked.</summary>
    public int ProtocolCount => _ProtocolBitmaps.Length;

    /// <summary>The stack this index was created for.</summary>
    public Stack Stack { get; }

    #endregion

    #region Recording

    /// <summary>
    /// Records that the current packet contains the given index group.
    /// Called by protocols during <see cref="Protocols.IProtocol.Parse"/>.
    /// Duplicate calls for the same group within one packet are deduplicated via bit-vector.
    /// Bitmap inserts are deferred until <see cref="EndPacket"/>.
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
        // Hot path: called from protocol parsers for every group they touch. Everything below is flag checks, one bounds
        // check and one bit test-and-set on a stack-sized word; no bitmap is written and nothing is allocated. The
        // throw paths are separate NoInlining helpers so this method stays small enough to inline into the parsers.
        // A replayed packet (dummy session) records nothing.
        if (_SuppressCommit)
        {
            return;
        }

        // Guard against off-lifecycle calls: _CurrentPacketId is set to -1 by EndPacket.
        // Without this check, (uint)(-1) = 4294967295 would be silently inserted into bitmaps.
        if (_CurrentPacketId < 0)
        {
            _ThrowNoActivePacket();
        }

        if (!_IsValidGroupId(groupId))
        {
            _ThrowGroupIdOutOfRange(groupId);
        }

        // Word index = id / 64, bit index = id % 64. A set bit means "already recorded for this packet".
        int id = groupId.Value;
        int word = id >> 6;
        ulong bit = 1UL << (id & 63);

        ref ulong dedupWord = ref _GroupDedup[word];
        if ((dedupWord & bit) != 0)
        {
            return;
        }
        dedupWord |= bit;
    }

    /// <summary>
    /// Records that the current packet contains the given protocol.
    /// Called by protocols during <see cref="Protocols.IProtocol.Parse"/>.
    /// Duplicate calls for the same protocol within one packet are deduplicated via bit-vector.
    /// Bitmap inserts are deferred until <see cref="EndPacket"/>.
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
        // Hot path, same shape and rationale as RecordGroupPresence.
        if (_SuppressCommit)
        {
            return;
        }

        // Guard against off-lifecycle calls: same rationale as RecordGroupPresence.
        if (_CurrentPacketId < 0)
        {
            _ThrowNoActivePacket();
        }

        if (!_IsValidProtocolId(protocolId))
        {
            _ThrowProtocolIdOutOfRange(protocolId);
        }

        // Word index = id / 64, bit index = id % 64 (see RecordGroupPresence).
        int id = protocolId.Value;
        int word = id >> 6;
        ulong bit = 1UL << (id & 63);

        ref ulong dedupWord = ref _ProtocolDedup[word];
        if ((dedupWord & bit) != 0)
        {
            return;
        }
        dedupWord |= bit;
    }

    #endregion

    #region Query API

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range for this index.
    /// </exception>
    public ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId)
    {
        if (!_IsValidGroupId(groupId))
        {
            _ThrowGetGroupOutOfRange(groupId);
        }
        return _GroupBitmaps[groupId.Value].AsReadOnly();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    public ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId)
    {
        if (!_IsValidProtocolId(protocolId))
        {
            _ThrowGetProtocolOutOfRange(protocolId);
        }
        return _ProtocolBitmaps[protocolId.Value].AsReadOnly();
    }

    /// <summary>
    /// Gets the bitmap of packets containing a specific field by resolving
    /// the field's index group via the stack metadata.
    /// Returns an empty bitmap if the field has no index group.
    /// The returned view is live, like <see cref="GetGroupBitmap"/>.
    /// </summary>
    /// <param name="fieldId">Field ID. Must originate from this index's own <see cref="Stack"/>.</param>
    /// <returns>A live read-only view of the field's index-group bitmap, or <see cref="ReadOnlyRoaringBitmap.Empty"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="fieldId"/> is out of range for this index's stack, or the field resolves to an index
    /// group that this index does not have.
    /// </exception>
    public ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId)
    {
        if (!_IsValidFieldId(fieldId))
        {
            _ThrowGetFieldOutOfRange(fieldId);
        }

        IndexGroupId groupId = Stack.GetFieldIndexGroup(fieldId);
        if (!groupId.IsValid)
        {
            // Valid field with no index group — default Empty struct, zero-allocation path.
            return ReadOnlyRoaringBitmap.Empty;
        }

        if (!_IsValidGroupId(groupId))
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
        if (!_IsValidGroupId(groupId))
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
        if (!_IsValidProtocolId(protocolId))
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

    /// <summary>
    /// Gets a zero-allocation struct view of this index for inlinable call sites.
    /// Pass the returned struct (or this <see cref="PacketIndex"/>) to generic
    /// <c>where TIndex : IPacketIndexReader</c> APIs. Do not assign the result to
    /// <see cref="IPacketIndexReader"/> — that boxes.
    /// </summary>
    public PacketIndexReaderView AsReadOnlyView() => new(this);

    #endregion

    #region Try variants

    /// <inheritdoc/>
    public bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap)
    {
        if (!_IsValidGroupId(groupId))
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
        if (!_IsValidProtocolId(protocolId))
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
        if (!_IsValidFieldId(fieldId))
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }

        IndexGroupId groupId = Stack.GetFieldIndexGroup(fieldId);
        if (!groupId.IsValid || !_IsValidGroupId(groupId))
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
        if (!_IsValidGroupId(groupId))
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
        if (!_IsValidProtocolId(protocolId))
        {
            cardinality = 0;
            return false;
        }
        cardinality = _ProtocolBitmaps[protocolId.Value].Cardinality;
        return true;
    }

    #endregion

    #region Private helpers

    // The three validators use one unsigned comparison: a negative id (for example the Invalid sentinel) wraps to a huge
    // uint and fails the same check, so no separate "< 0" test is needed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _IsValidGroupId(IndexGroupId groupId) => (uint)groupId.Value < (uint)_GroupBitmaps.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _IsValidProtocolId(ProtocolId protocolId) => (uint)protocolId.Value < (uint)_ProtocolBitmaps.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _IsValidFieldId(FieldId fieldId) => (uint)fieldId.Value < (uint)Stack.FieldCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ClearDedup(ulong[] dedup)
    {
        // Called four times per packet (group and protocol vector, once at begin and once at end). Direct zeroing for the
        // typical 1-2 word case avoids Array.Clear method call overhead; larger stacks fall back to Array.Clear.
        if (dedup.Length <= 2)
        {
            dedup[0] = 0;
            if (dedup.Length == 2)
            {
                dedup[1] = 0;
            }
        }
        else
        {
            Array.Clear(dedup);
        }
    }

    /// <summary>
    /// Run-optimizes every chunk below <paramref name="chunk"/> that was not optimized yet, in all group and protocol
    /// bitmaps and in the indexed-packet set. Each chunk is visited once over the life of the index; a gap of absent
    /// chunks costs one binary search per bitmap. Rare path (once per 65 536 packets in order).
    /// <para>
    /// Why: ids are appended in ascending order, so a finished chunk is long runs for protocols present in most
    /// packets (a dense chunk shrinks from an 8 KiB bitmap container to a few bytes as runs). Only conversions that are
    /// smaller are made (<see cref="RoaringBitmap.RunOptimizeKeys"/>). Why per finished chunk and not per add: a
    /// conversion scans the whole container, and a chunk that is still growing would be converted again and again.
    /// An id of an already optimized chunk that arrives later (out-of-order ingest) is still added correctly, because a
    /// run container accepts adds.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _RunOptimizeCompletedChunks(int chunk)
    {
        if (chunk <= _NextChunkToOptimize)
        {
            return;
        }

        foreach (RoaringBitmap bitmap in _GroupBitmaps)
        {
            _ = bitmap.RunOptimizeKeys(_NextChunkToOptimize, chunk);
        }

        foreach (RoaringBitmap bitmap in _ProtocolBitmaps)
        {
            _ = bitmap.RunOptimizeKeys(_NextChunkToOptimize, chunk);
        }

        _ = _IndexedPackets.RunOptimizeKeys(_NextChunkToOptimize, chunk);
        _NextChunkToOptimize = chunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _CommitDedupToBitmaps(ulong[] dedup, RoaringBitmap[] bitmaps)
    {
        // Walk only the set bits of each word: skip empty words, take the lowest set bit with TrailingZeroCount, clear it
        // with bits &= bits - 1. Cost is proportional to the number of distinct ids of this packet, not to the id count of
        // the stack. The bitmaps[id] access needs no bounds reasoning: a bit is only set after the Record method validated
        // the id against the same array length.
        uint packetId = (uint)_CurrentPacketId;
        for (int word = 0; word < dedup.Length; word++)
        {
            ulong bits = dedup[word];
            if (bits == 0)
            {
                continue;
            }

            int baseId = word << 6;
            do
            {
                int bit = BitOperations.TrailingZeroCount(bits);
                int id = baseId + bit;
                bitmaps[id].Add(packetId);
                bits &= bits - 1;
            }
            while (bits != 0);
        }
    }

    /// <summary>Cold-path helper: throws <see cref="InvalidOperationException"/> when a record method is called outside a Begin/EndPacket pair.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowNoActivePacket() =>
        throw new InvalidOperationException(
            "RecordGroupPresence/RecordProtocolPresence must be called between BeginPacket and EndPacket.");

    /// <summary>Cold-path helper: throws when <see cref="EndPacket"/> is called without a matching <see cref="BeginPacket"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowNoActivePacketEnd() =>
        throw new InvalidOperationException(
            "EndPacket called without a matching BeginPacket.");

    /// <summary>Cold-path helper: throws when <see cref="BeginPacket"/> is called while a packet is already active.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowNestedBeginPacket() =>
        throw new InvalidOperationException(
            "BeginPacket called while a packet is already being indexed. Call EndPacket first.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad group ID during recording.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowGroupIdOutOfRange(IndexGroupId groupId) =>
        throw new ArgumentOutOfRangeException(
            nameof(groupId),
            groupId.Value,
            $"Index group ID {groupId.Value} is out of range for this index (GroupCount={_GroupBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad protocol ID during recording.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowProtocolIdOutOfRange(ProtocolId protocolId) =>
        throw new ArgumentOutOfRangeException(
            nameof(protocolId),
            protocolId.Value,
            $"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_ProtocolBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad group ID during lookup.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowGetGroupOutOfRange(IndexGroupId groupId) =>
        throw new ArgumentOutOfRangeException(
            nameof(groupId),
            groupId.Value,
            $"Index group ID {groupId.Value} is out of range for this index (GroupCount={_GroupBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad protocol ID during lookup.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowGetProtocolOutOfRange(ProtocolId protocolId) =>
        throw new ArgumentOutOfRangeException(
            nameof(protocolId),
            protocolId.Value,
            $"Protocol ID {protocolId.Value} is out of range for this index (ProtocolCount={_ProtocolBitmaps.Length}). " +
            "Ensure the ID was obtained from this index's own Stack.");

    /// <summary>Cold-path helper: throws a descriptive <see cref="ArgumentOutOfRangeException"/> for a bad field ID during lookup.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _ThrowGetFieldOutOfRange(FieldId fieldId) =>
        throw new ArgumentOutOfRangeException(
            nameof(fieldId),
            fieldId.Value,
            $"Field ID {fieldId.Value} is out of range for this index (FieldCount={Stack.FieldCount}). " +
            "Ensure the field ID was obtained from this index's own Stack.");

    #endregion
}
