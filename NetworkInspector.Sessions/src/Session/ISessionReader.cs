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
    // ── Counters ─────────────────────────────────────────────────────────────

    /// <summary>Total packets parsed so far. Volatile read.</summary>
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

    // ── Packet access ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to retrieve a packet by its <see cref="PacketId"/>.
    /// Returns <see langword="true"/> if the packet was found (store or random-access re-parse fallback),
    /// <see langword="false"/> if the id is invalid, the packet is not in the store and cannot be
    /// re-read from its source, or queries are disabled (e.g. during shutdown).
    /// </summary>
    bool TryGetPacket(PacketId id, [NotNullWhen(true)] out Packet? packet);

    /// <summary>
    /// Like <see cref="TryGetPacket(PacketId, out Packet?)"/>, but reuses the caller's
    /// <paramref name="recycle"/> packet when the id has to be re-parsed, which keeps the hot path
    /// free of packet allocations. Pass <see langword="null"/> to always allocate.
    ///
    /// <para>
    /// A store hit returns the stored instance and leaves <paramref name="recycle"/> untouched, so a
    /// caller cannot rely on getting its own instance back — compare by reference if that matters.
    /// If the recycle attempt is rejected (for example because the packet still has an active
    /// materialization or came from another stack), the re-parse falls back to a fresh allocation and
    /// still succeeds.
    /// </para>
    ///
    /// <para>
    /// <b>Ownership:</b> <paramref name="recycle"/> belongs exclusively to the calling thread.
    /// Re-parsing into it overwrites its fields in place, so it must not be handed in while any
    /// other thread — or the caller itself, through an earlier <see cref="Field"/> or
    /// <see cref="MutField"/> reference — still reads it.
    /// </para>
    /// </summary>
    bool TryGetPacket(PacketId id, Packet? recycle, [NotNullWhen(true)] out Packet? packet);

    /// <summary>
    /// Reads a contiguous range of packets into <paramref name="buffer"/>.
    /// <paramref name="fromIndex"/> is the first <see cref="PacketId"/> value (inclusive).
    /// Returns the number of slots actually filled. Entries may be <see langword="null"/>
    /// if the slot was cleared (e.g. after restart) or not yet stored. Returns 0 when queries are disabled.
    /// </summary>
    int ReadPackets(int fromIndex, Span<Packet?> buffer);

    /// <summary>
    /// Reads a contiguous range of packets, paired with their ids, into <paramref name="destination"/>.
    ///
    /// <para>
    /// Equivalent to <see cref="ReadPackets(int, Span{Packet})"/> apart from carrying the id in
    /// every slot: <paramref name="idLayout"/> is always
    /// <see cref="PacketIdLayout.Contiguous"/> and <see cref="PacketRef.Packet"/> may be
    /// <see langword="null"/> for ids that hold nothing. Returns 0 when queries are disabled.
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
    /// caller must repair the filter (<see cref="IFilter.ResetState"/>, or registering a new
    /// listener) before matching pulls succeed again.
    /// </para>
    ///
    /// <para>
    /// Filtering never applies to notifications: <see cref="Listeners.ISessionListener.OnNewPackets"/>
    /// keeps reporting the unfiltered id window.
    /// </para>
    /// </summary>
    /// <param name="listenerId">Identifies the listener whose filter applies.</param>
    /// <param name="startId">First <see cref="PacketId"/> value to consider (inclusive).</param>
    /// <param name="destination">Caller-owned buffer; never allocated or resized by the session.</param>
    /// <param name="mode">Whether to return every packet or only matching ones.</param>
    /// <param name="count">Receives the number of slots filled.</param>
    /// <param name="idLayout">Receives whether the returned ids are consecutive.</param>
    /// <param name="failure">Receives the filter error when the read failed.</param>
    /// <returns><see langword="true"/> when the read completed.</returns>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerNotFound"/> when <paramref name="listenerId"/> does not
    /// identify a listener currently registered with this session.
    /// </exception>
    bool TryReadPackets(
        ListenerId listenerId,
        int startId,
        Span<PacketRef> destination,
        PacketReadMode mode,
        out int count,
        out PacketIdLayout idLayout,
        [NotNullWhen(false)] out FilterError? failure);

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

    /// <summary>The protocol stack currently bound to this session. Replaced by <see cref="ISession.Restart"/>.</summary>
    Stack Stack
    {
        get;
    }

    // ── Value caches ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only view of the construction-time ingest value cache, or
    /// <see langword="null"/> when <see cref="SessionOptions.ValueCache"/> was not set.
    /// After Restart this aliases the rebound writer.
    /// </summary>
    ValueCacheReaderView? IngestValueCache
    {
        get;
    }

    /// <summary>
    /// Snapshot of ingest and runtime value-cache subscriptions.
    /// Ingest without a listener is included with UiName <c>ingest</c>.
    /// </summary>
    IReadOnlyList<ValueCacheInfo> GetValueCaches();
}
