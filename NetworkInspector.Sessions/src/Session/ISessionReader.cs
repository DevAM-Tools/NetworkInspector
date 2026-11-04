// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Read-only view of session data. Used by <see cref="Listeners.ISessionListener"/>
/// implementations to pull data in the notification callback.
///
/// <para>
/// All methods are thread-safe. Reads use <see cref="Volatile"/> /
/// <see cref="Interlocked"/> — no external locking required.
/// </para>
/// </summary>
public interface ISessionReader
{
    #region API

    // ── Counters ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Total packets parsed so far. Volatile read. The count is published after the frame mapping of every id below
    /// it, so each id in <c>[0, PacketCount)</c> can be read even while ingest keeps appending; a count read once
    /// is a safe upper bound for a following read loop.
    /// </summary>
    int PacketCount
    {
        get;
    }

    /// <summary>Total frames read so far. Volatile read.</summary>
    int FrameCount
    {
        get;
    }

    /// <summary>Current session lifecycle phase. Volatile read.</summary>
    SessionPhase Phase
    {
        get;
    }

    /// <summary>
    /// <see langword="true"/> while at least one source job is still active.
    /// Intended for progress UI — may be slightly stale.
    /// </summary>
    bool MorePacketsExpected
    {
        get;
    }

    /// <summary>
    /// False while restart is rewriting packets, after restart fails closed, and after shutdown disables reads.
    /// A false packet read, or a zero <c>ReadPackets</c> result, means "no row" only when this is true.
    /// </summary>
    bool QueriesEnabled
    {
        get;
    }

    /// <summary>
    /// The failure that left the session in <see cref="SessionPhase.Faulted"/> (a restart that failed after the stack was
    /// swapped), or <see langword="null"/> while the session is not faulted. Volatile read. While it is non-null, reads and
    /// new work are refused with <see cref="SessionErrorCode.SessionFaulted"/> and ingest is parked; another restart or the
    /// shutdown are the only ways out, and a successful restart clears it.
    /// </summary>
    SessionFailure? Fault
    {
        get;
    }

    // ── Packet access ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to retrieve a packet by its <see cref="PacketId"/> by re-parsing the captured frame.
    /// The session does not retain packet instances. Returns <see langword="true"/> if the frame
    /// can be loaded (cached stream source or random-access source). Returns <see langword="false"/>
    /// if the id is invalid, the frame cannot be re-read, or queries are disabled.
    /// </summary>
    bool TryGetPacket(PacketId id, [NotNullWhen(true)] out Packet? packet, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Like <see cref="TryGetPacket(PacketId, out Packet?, out SessionFailure?)"/>, but reuses the caller's
    /// <paramref name="recycle"/> packet when the id has to be re-parsed, which keeps the hot path
    /// free of packet allocations. Pass <see langword="null"/> to always allocate.
    ///
    /// <para>
    /// A store hit is not possible: the session never keeps packets. When
    /// <paramref name="recycle"/> is accepted, the returned instance is that object.
    /// If the recycle attempt is rejected (for example because the packet still has an active
    /// materialization or came from a stack of another frame registry), the re-parse falls back to a fresh allocation and
    /// still succeeds. A recycle packet of an earlier stack of the same registry is rebound to the current one, so a buffer of
    /// packets survives a restart without a single allocation.
    /// </para>
    ///
    /// <para>
    /// <b>Ownership:</b> <paramref name="recycle"/> belongs exclusively to the calling thread.
    /// Re-parsing into it overwrites its fields in place, so it must not be handed in while any
    /// other thread — or the caller itself, through an earlier <see cref="Field"/> or
    /// <see cref="MutField"/> reference — still reads it.
    /// </para>
    /// </summary>
    bool TryGetPacket(PacketId id, Packet? recycle, [NotNullWhen(true)] out Packet? packet, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Parses one packet with the caller's <see cref="ParseOptions"/> (observers, a value cache, a first-protocol override, the
    /// field-tree mode) under the session's own stack protection, and refuses options that were built for another stack
    /// generation. This is the path for a caller that watches fields while it parses: the raw <see cref="Stack"/> property
    /// parses without a lease (a restart can dispose the stack under the parse) and nothing validates the field ids in the
    /// observers (after a restart they name other fields, silently).
    ///
    /// <para>
    /// <b>Recipe:</b> take <c>StackView view = CurrentStack</c>, resolve the field ids on <c>view.Stack</c>, build the
    /// options from them, and pass <paramref name="builtFor"/> = <c>view</c>. One integer compare against the generation of the
    /// stack the call leases decides: a restart in between gives <see cref="SessionErrorCode.OptionsStackMismatch"/> (the
    /// message names both generations) and the observers are not called; take the view again and rebuild the options.
    /// </para>
    ///
    /// <para>
    /// <paramref name="options"/> carries ids and objects of one stack: <see cref="ParseOptions.Index"/> left <see langword="null"/>
    /// is replaced by the session's packet index of the leased stack; an index or value cache of another stack inside the
    /// options is answered with <see cref="SessionErrorCode.OptionsStackMismatch"/> as well (a defined failure, not an
    /// exception). <paramref name="recycle"/> follows the rules of
    /// <see cref="TryGetPacket(PacketId, Packet?, out Packet?, out SessionFailure?)"/>.
    /// </para>
    ///
    /// <para>
    /// Refusals: <see cref="SessionErrorCode.QueriesDisabled"/> while a restart runs or after shutdown,
    /// <see cref="SessionErrorCode.SessionFaulted"/>, <see cref="SessionErrorCode.OptionsStackMismatch"/>,
    /// <see cref="SessionErrorCode.PacketNotFound"/>, <see cref="SessionErrorCode.FrameUnavailable"/>.
    /// </para>
    /// </summary>
    /// <param name="id">The packet to parse.</param>
    /// <param name="options">The parse choices, built for <paramref name="builtFor"/>.</param>
    /// <param name="builtFor">The view the options were built on.</param>
    /// <param name="recycle">A packet to reuse, or <see langword="null"/> to allocate.</param>
    /// <param name="packet">The parsed packet; set when the call returns <see langword="true"/>.</param>
    /// <param name="failure">Why nothing was parsed; set when the call returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the packet was parsed with the options.</returns>
    bool TryGetPacket(
        PacketId id,
        in ParseOptions options,
        StackView builtFor,
        Packet? recycle,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Parses the packets <paramref name="fromId"/> … <paramref name="toIdExclusive"/> − 1 with the caller's
    /// <see cref="ParseOptions"/> and hands each to <paramref name="visitor"/> on the calling thread, in id order. This is the
    /// pull read for consumers that parse with observers (an export of selected fields, a statistic over a few fields): the frames
    /// are read in batches of 64 per source, the parse uses exactly the options given (a tree-free parse stays tree-free), and the
    /// session never pushes.
    ///
    /// <para>
    /// <b>Options and generation:</b> the same rules as
    /// <see cref="TryGetPacket(PacketId, in ParseOptions, StackView, Packet?, out Packet?, out SessionFailure?)"/>: the options are
    /// built on <paramref name="builtFor"/>; another generation gives <see cref="SessionErrorCode.OptionsStackMismatch"/> before the
    /// first parse; <see cref="ParseOptions.Index"/> left <see langword="null"/> is replaced by the session's index of the leased
    /// stack.
    /// </para>
    ///
    /// <para>
    /// <b>One call, one generation:</b> the call holds one stack lease for its whole range (as
    /// <see cref="ReadPackets(int, Span{Packet?})"/>), so a caller reads a large range in windows (an export job uses 4 096 ids per
    /// call) to release a retired stack soon after a restart. A restart that starts during the call ends it after the packet in
    /// progress with <see cref="SessionErrorCode.QueriesDisabled"/>; <paramref name="visitedCount"/> says how far it got.
    /// </para>
    ///
    /// <para>
    /// <b>Refusals:</b> <see cref="SessionErrorCode.QueriesDisabled"/>, <see cref="SessionErrorCode.SessionFaulted"/>,
    /// <see cref="SessionErrorCode.OptionsStackMismatch"/>, <see cref="SessionErrorCode.PacketNotFound"/> when
    /// <paramref name="toIdExclusive"/> exceeds the published packet count (the message names the range and the count),
    /// <see cref="SessionErrorCode.FrameUnavailable"/> when a frame cannot be re-read (the message names the id; the packets before
    /// it were visited). A visitor that returns <see langword="false"/> ends the call successfully.
    /// </para>
    /// </summary>
    /// <param name="fromId">First packet id (inclusive), ≥ 0.</param>
    /// <param name="toIdExclusive">End of the range (exclusive), ≥ <paramref name="fromId"/>. Equal values visit nothing.</param>
    /// <param name="options">The parse choices, built for <paramref name="builtFor"/>.</param>
    /// <param name="builtFor">The view the options were built on.</param>
    /// <param name="visitor">Receives each parsed packet.</param>
    /// <param name="visitedCount">Packets handed to <paramref name="visitor"/> (also on failure).</param>
    /// <param name="failure">Why the call ended early; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the whole range was visited or the visitor stopped; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="visitor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromId"/> is negative or <paramref name="toIdExclusive"/> is below it.</exception>
    bool TryVisitPackets(
        int fromId,
        int toIdExclusive,
        in ParseOptions options,
        StackView builtFor,
        IPacketVisitor visitor,
        out int visitedCount,
        [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Attempts to retrieve the captured frame for <paramref name="id"/> without parsing a packet.
    /// Returns <see langword="true"/> when the frame can be re-read from a cached stream source
    /// or a random-access source. Returns <see langword="false"/> when the id is invalid, the frame is
    /// unreachable, or queries are disabled. On failure <paramref name="frame"/> is
    /// <see cref="Frame.Invalid"/>.
    /// </summary>
    bool TryGetFrame(PacketId id, out Frame frame, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Reads a contiguous range of packets into <paramref name="buffer"/> by re-parsing each id.
    /// <paramref name="fromIndex"/> is the first <see cref="PacketId"/> value (inclusive).
    /// Each non-null <paramref name="buffer"/> slot is used as that slot’s recycle packet.
    /// Null slots allocate. The session does not retain the returned instances.
    /// Returns the number of slots actually filled. Returns 0 when queries are disabled.
    ///
    /// <para>
    /// <b>One call, one generation:</b> the call parses on exactly one stack generation. A restart that starts during the
    /// call leaves the remaining slots <see langword="null"/>; it never produces packets of another stack in the same buffer.
    /// A <see langword="null"/> slot inside the returned count therefore means "a restart overtook this call" (check
    /// <see cref="QueriesEnabled"/> and <see cref="Generation"/>), or that the frame could not be re-read.
    /// </para>
    /// </summary>
    int ReadPackets(int fromIndex, Span<Packet?> buffer);

    /// <summary>
    /// Reads a contiguous range of packets, paired with their ids, into <paramref name="destination"/>.
    ///
    /// <para>
    /// Every slot is an independent reparse. <see cref="PacketRef.Packet"/> on input is that slot’s recycle
    /// instance when non-null. The session does not retain returned packets.
    /// </para>
    ///
    /// <para>
    /// <b>One call, one generation:</b> see <see cref="ReadPackets(int, Span{Packet?})"/>; a restart that starts during the
    /// call leaves the remaining <see cref="PacketRef.Packet"/> values <see langword="null"/>.
    /// </para>
    /// </summary>
    /// <param name="startId">First <see cref="PacketId"/> value to read (inclusive).</param>
    /// <param name="destination">Caller-owned buffer; never allocated or resized by the session.</param>
    /// <param name="idLayout">Receives <see cref="PacketIdLayout.Contiguous"/>.</param>
    /// <returns>The number of slots filled.</returns>
    int ReadPackets(int startId, Span<PacketRef> destination, out PacketIdLayout idLayout);

    /// <summary>
    /// Reads packets on behalf of a registered listener, optionally restricted to the packets that
    /// match that listener's filter.
    ///
    /// <para>
    /// <b><see cref="PacketReadMode.All"/></b> ignores the filter and behaves exactly like
    /// <see cref="ReadPackets(int, Span{PacketRef}, out PacketIdLayout)"/>.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="PacketReadMode.Matching"/></b> scans ids from <paramref name="startId"/> up to
    /// the current <see cref="PacketCount"/> and fills only matching packets, so
    /// <paramref name="idLayout"/> becomes <see cref="PacketIdLayout.Gapped"/> as soon as any id in
    /// the scanned range is skipped. A listener without a filter, or one whose filter is
    /// <see cref="NetworkInspector.Filter.Filter.AlwaysMatch"/>, short-circuits to the
    /// <see cref="PacketReadMode.All"/> path and does no per-packet work. Otherwise the filter's
    /// presence-index candidate set prunes the range first and only the survivors are evaluated.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="false"/> when the listener's filter refuses to produce a verdict —
    /// because it is poisoned by an earlier evaluation failure, because a packet failed to
    /// evaluate now, or because it could not be re-bound after a stack swap. In that case
    /// <paramref name="failure"/> carries the reason, <paramref name="count"/> is 0, and the
    /// caller must repair the filter (<see cref="PacketFilter.ResetState"/>, or registering a new
    /// listener) before matching pulls succeed again.
    /// </para>
    ///
    /// <para>
    /// Filtering never applies to notifications: <see cref="Listeners.ISessionListener.OnNewPackets"/>
    /// keeps reporting the unfiltered id window.
    /// </para>
    ///
    /// <para>
    /// <b>One call, one generation:</b> both modes parse on exactly one stack generation per call, see
    /// <see cref="ReadPackets(int, Span{Packet?})"/>. A matching scan that a restart overtakes ends early with the packets
    /// found so far.
    /// </para>
    /// </summary>
    /// <param name="listenerId">Identifies the listener whose filter applies.</param>
    /// <param name="startId">First <see cref="PacketId"/> value to consider (inclusive).</param>
    /// <param name="destination">Caller-owned buffer; never allocated or resized by the session.</param>
    /// <param name="mode">Whether to return every packet or only matching ones.</param>
    /// <param name="count">Receives the number of slots filled.</param>
    /// <param name="idLayout">Receives whether the returned ids are consecutive.</param>
    /// <param name="failure">Receives the filter error when the filter refuses a verdict.</param>
    /// <param name="sessionFailure">
    /// Receives the session error when the listener is unknown or queries are disabled.
    /// Exactly one of <paramref name="failure"/> and <paramref name="sessionFailure"/> is non-null when the method returns false.
    /// </param>
    /// <returns><see langword="true"/> when the read completed.</returns>
    bool TryReadPackets(
        ListenerId listenerId,
        int startId,
        Span<PacketRef> destination,
        PacketReadMode mode,
        out int count,
        out PacketIdLayout idLayout,
        out FilterError? failure,
        out SessionFailure? sessionFailure);

    // ── Source info ──────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot of all registered frame sources.</summary>
    IReadOnlyList<FrameSourceInfo> GetFrameSources();

    // ── Listener info ────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot of all registered listener subscriptions.</summary>
    IReadOnlyList<ListenerInfo> GetListeners();

    // ── Job info ─────────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot of all registered jobs.</summary>
    IReadOnlyList<JobInfo> GetJobs();

    // ── Index ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zero-allocation read-only view of the packet index built during parsing. Contains
    /// Roaring Bitmap presence information for protocols and field groups.
    /// Returns <see langword="null"/> when the session has not started yet, or when
    /// <see cref="ISession.IndexPackets"/> is <see langword="false"/>.
    /// Keep the compile-time type as <see cref="PacketIndexReaderView"/> or pass it to a
    /// generic <c>where TIndex : IPacketIndexReader</c> API. Assigning this value to
    /// <see cref="IPacketIndexReader"/> boxes.
    /// </summary>
    PacketIndexReaderView? PacketIndex
    {
        get;
    }

    // ── Stack ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The protocol stack currently bound to this session. Replaced by a restart
    /// (<see cref="ISession.TryRestart(Func{FrameInterfaceRegistry, Stack}, out RestartReport?, out SessionFailure?)"/>). A caller that
    /// parses with it outside the session's reads holds no protection: a stack the session owns is disposed once a restart
    /// replaced it and the session's own reads ended, so read the property again after <c>OnStackChanged</c> and do not keep the
    /// old reference. Prefer <see cref="CurrentStack"/>, which pairs the stack with its generation.
    /// </summary>
    Stack Stack
    {
        get;
    }

    /// <summary>
    /// The number of the stack that serves reads now: 0 for the stack the session was constructed with, plus one for every
    /// stack swap. It moves at the swap, before reads are enabled again, so a caller that polls only this property and
    /// <see cref="QueriesEnabled"/> (no listener, no callback) can tell the three states of a restart apart:
    /// <list type="table">
    ///   <listheader><term>Generation / QueriesEnabled</term><description>Meaning</description></listheader>
    ///   <item><term>N / true</term><description>Stable: everything derived from generation N is valid.</description></item>
    ///   <item><term>N + 1 / false</term><description>A restart is in progress: the stack was swapped, packets are re-parsed,
    ///   reads are refused.</description></item>
    ///   <item><term>N + 1 / true</term><description>Done: reads serve generation N + 1; derived state of N must be rebuilt.</description></item>
    /// </list>
    /// Two restarts that a slow consumer observes in one wake-up show as a jump of two; compare for inequality with the
    /// number you hold, never assume "old + 1". Volatile read, no lock, no allocation.
    /// </summary>
    int Generation
    {
        get;
    }

    /// <summary>
    /// The current stack together with its generation, taken from one volatile load of the session's holder so the two can
    /// never disagree. The pair is immutable: after a restart a view taken before still names the old stack and the old
    /// number (<see cref="StackView.IsCurrent"/> then returns <see langword="false"/>). Resolve field names to ids on
    /// <see cref="StackView.Stack"/>, keep the view next to the ids, and compare it before you use them again. No allocation.
    /// </summary>
    StackView CurrentStack
    {
        get;
    }

    // ── Value caches ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only view of the construction-time ingest value cache, or
    /// <see langword="null"/> when <see cref="SessionOptions.ValueCache"/> was not set.
    /// After Restart this aliases the rebound writer.
    /// Keep the compile-time type as <see cref="ReadOnlyValueCache"/> or pass it to a
    /// generic <c>where TCache : IReadOnlyValueCache</c> API. Assigning this value to
    /// <see cref="IReadOnlyValueCache"/> boxes.
    /// </summary>
    ReadOnlyValueCache? IngestValueCache
    {
        get;
    }

    /// <summary>
    /// Snapshot of ingest and runtime value-cache subscriptions.
    /// Ingest without a listener is included with UiName <c>ingest</c>.
    /// </summary>
    IReadOnlyList<ValueCacheInfo> GetValueCaches();

    #endregion
}
