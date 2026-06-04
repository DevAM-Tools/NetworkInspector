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

    /// <summary>Total packets parsed so far. Interlocked read.</summary>
    long PacketCount
    {
        get;
    }

    /// <summary>Total frames read so far. Interlocked read.</summary>
    long FrameCount
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
    /// Reads a contiguous range of packets into <paramref name="buffer"/>.
    /// <paramref name="fromIndex"/> is the first <see cref="PacketId"/> value (inclusive).
    /// Returns the number of slots actually filled. Entries may be <see langword="null"/>
    /// if the slot was cleared (e.g. after restart) or not yet stored. Returns 0 when queries are disabled.
    /// </summary>
    int ReadPackets(long fromIndex, Span<Packet?> buffer);

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
    /// Read-only view of the packet index built during parsing. Contains
    /// Roaring Bitmap presence information for protocols and field groups.
    /// Returns <see langword="null"/> if the session has not started yet.
    /// </summary>
    IPacketIndexReader? PacketIndex
    {
        get;
    }
}
