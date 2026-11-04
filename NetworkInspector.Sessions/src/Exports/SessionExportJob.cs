// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// A running or finished session export started by <see cref="SessionExport.TryStart"/>: the job, the exporter, and progress.
/// <para>
/// <b>Why counters here:</b> <see cref="JobInfo"/> has no progress members (jobs are generic work items); an export has a known
/// total, so it publishes its own counts. A UI computes the rate from two samples of <see cref="VisitedCount"/> and their times.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the job thread writes the counters with <see cref="Interlocked"/> / <see cref="Volatile"/>; any thread
/// may read them. <see cref="Failure"/> is published before the job's terminal status, so a reader that sees the job ended sees it.
/// </para>
/// </summary>
public sealed class SessionExportJob
{
    #region Fields

    // Counters and failure are written by the one job thread and read by any thread. A plain Volatile read/write pair would do for
    // a single writer; Interlocked.Increment is used for the counters because it costs the same and stays correct if a second
    // writer ever appears. Rejected: a lock (a read for a progress display must never wait for the export).
    private long _VisitedCount;
    private long _ExportedCount;
    private object? _Failure; // A boxed SessionFailure: a struct cannot go through Volatile, a reference can (published atomically).

    #endregion

    #region Constructors

    internal SessionExportJob(int fromId, int toIdExclusive, IPacketListener exporter)
    {
        FromId = fromId;
        ToIdExclusive = toIdExclusive;
        Exporter = exporter;
    }

    #endregion

    #region Properties

    /// <summary>The session job; set by <see cref="SessionExport.TryStart"/> before the handle is handed out.</summary>
    public JobInfo Job { get; internal set; } = null!; // Invariant: TryStart sets it before handing the handle out.

    /// <summary>First packet id of the export (inclusive).</summary>
    public int FromId { get; }

    /// <summary>End of the export range (exclusive). Total = <see cref="ToIdExclusive"/> − <see cref="FromId"/>.</summary>
    public int ToIdExclusive { get; }

    /// <summary>The exporter; read its statistics after the job ended.</summary>
    public IPacketListener Exporter { get; }

    /// <summary>Packets parsed so far (matching or not).</summary>
    public long VisitedCount => Volatile.Read(ref _VisitedCount);

    /// <summary>Packets handed to the exporter so far (matching the filter).</summary>
    public long ExportedCount => Volatile.Read(ref _ExportedCount);

    /// <summary>
    /// Why the export ended early (read failure, filter failure, a job started on another generation); <see langword="null"/>
    /// when it completed or was cancelled.
    /// </summary>
    public SessionFailure? Failure => Volatile.Read(ref _Failure) as SessionFailure?;

    #endregion

    #region Internal API

    internal void AddVisited() => Interlocked.Increment(ref _VisitedCount);

    internal void AddExported() => Interlocked.Increment(ref _ExportedCount);

    internal void SetFailure(SessionFailure failure) => Volatile.Write(ref _Failure, failure);

    #endregion
}
