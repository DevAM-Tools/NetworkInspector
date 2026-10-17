// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Central session object that orchestrates frame sources, the protocol stack,
/// pull-based notification to listeners, and background jobs.
/// All public methods are thread-safe.
///
/// <para>
/// Extends <see cref="ISessionReader"/> so that every <see cref="ISession"/> is also
/// a read-only data source. Consumers that only need to query session state should
/// accept <see cref="ISessionReader"/> instead.
/// </para>
/// </summary>
public interface ISession : ISessionReader, IDisposable
{
    #region API

    // ── Source management ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to register a frame source. Must be called while the session is
    /// in the <see cref="SessionPhase.Idle"/> phase.
    /// Returns <see langword="false"/> if the session is not in the correct phase.
    /// After a successful add the session owns <paramref name="source"/> (or a
    /// <see cref="CachedFrameSource"/> wrapper that owns it). The caller must not dispose it.
    /// Non-random-access sources are always wrapped so frames remain readable after ingest.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddFrameSource(IFrameSource source, [NotNullWhen(true)] out FrameSourceInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Registers a frame source with per-source cache options.
    /// <see cref="FrameSourceAddOptions.CacheRandomAccess"/> wraps an
    /// <see cref="IRandomAccessFrameSource"/> in <see cref="CachedFrameSource"/>
    /// (holds the inner <see cref="Frame"/>; payload memory is not copied).
    /// Stream sources are always wrapped.
    /// </summary>
    bool TryAddFrameSource(
        IFrameSource source,
        FrameSourceAddOptions addOptions,
        [NotNullWhen(true)] out FrameSourceInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure);

    // ── Listener management ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to register a session listener. May be called while the session is
    /// <see cref="SessionPhase.Idle"/>, <see cref="SessionPhase.Running"/>, or
    /// <see cref="SessionPhase.Stopped"/>. A listener added after packets exist is notified for the
    /// current id range.
    /// Returns <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> while the session is
    /// shutting down, and with <see cref="SessionErrorCode.QueriesDisabled"/> while packet queries are disabled
    /// (a restart is in progress or failed closed, or shutdown has finished).
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/> when <see cref="ISessionListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ListenerIdExhausted"/> when the listener ID limit is reached.
    /// </exception>
    bool TryAddListener(ISessionListener listener, [NotNullWhen(true)] out ListenerInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Registers a session listener together with the filter it pulls matching packets with.
    ///
    /// <para>
    /// The filter is used only by <see cref="ISessionReader.TryReadPackets"/> in
    /// <see cref="PacketReadMode.Matching"/> mode. Notifications stay unfiltered:
    /// <see cref="ISessionListener.OnNewPackets"/> always reports the full id window of newly
    /// announced packets, so a listener is never starved of wake-ups by its own filter.
    /// </para>
    ///
    /// <para>
    /// A <see langword="null"/> filter means "no filter" and is equivalent to
    /// <see cref="NetworkInspector.Filter.Filter.AlwaysMatch"/>.
    /// A filter compiled against the session's current <see cref="ISessionReader.Stack"/> is installed as is.
    /// A filter compiled against any other stack (another session, or this session before a
    /// <see cref="Restart"/>) is never evaluated against the wrong field ids: the session installs a re-bound
    /// copy from <see cref="NetworkInspector.Filter.Filter.TryDerive"/> and leaves the caller's instance
    /// untouched. A later <see cref="Restart"/> re-binds the installed filter the same way.
    /// </para>
    ///
    /// <para>
    /// A filter instance is single-threaded and must not be shared between listeners; each
    /// listener evaluates its own filter on its own thread.
    /// </para>
    ///
    /// <para>
    /// Phase rules and refusals are those of
    /// <see cref="TryAddListener(ISessionListener, out ListenerInfo?, out SessionFailure?)"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="filter"/> was compiled for another stack and cannot be re-bound to the session stack
    /// (it names a field the session stack does not define). No listener is registered.
    /// </exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/> when <see cref="ISessionListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ListenerIdExhausted"/> when the listener ID limit is reached.
    /// </exception>
    bool TryAddListener(
        ISessionListener listener,
        PacketFilter? filter,
        [NotNullWhen(true)] out ListenerInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Registers a session listener and compiles <paramref name="filterExpression"/> against the
    /// session's current stack.
    ///
    /// <para>
    /// A null, empty, or whitespace-only expression compiles to
    /// <see cref="NetworkInspector.Filter.Filter.AlwaysMatch"/> without touching the stack, so
    /// "no filter" costs nothing.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="false"/> when the expression does not compile — then
    /// <paramref name="filterFailure"/> describes the problem, <paramref name="failure"/> is
    /// <see langword="null"/>, and no listener is registered — or when the session refuses the
    /// subscription (shutting down, or packet queries disabled; see
    /// <see cref="TryAddListener(ISessionListener, out ListenerInfo?, out SessionFailure?)"/>), in which case
    /// <paramref name="failure"/> carries the reason and <paramref name="filterFailure"/> is
    /// <see langword="null"/>.
    /// </para>
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/> when <see cref="ISessionListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ListenerIdExhausted"/> when the listener ID limit is reached.
    /// </exception>
    bool TryAddListener(
        ISessionListener listener,
        string? filterExpression,
        [NotNullWhen(true)] out ListenerInfo? info,
        out FilterError? filterFailure,
        out SessionFailure? failure);

    // ── Value-cache management ───────────────────────────────────────────────

    /// <summary>
    /// Registers a dedicated value cache filled from packet id 0 by a pull slot,
    /// analog to <see cref="TryAddListener(ISessionListener, out ListenerInfo?, out SessionFailure?)"/>.
    /// Always constructs a new cache; existing caches are never reused as a cover.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> while the session is shutting
    /// down, and with <see cref="SessionErrorCode.QueriesDisabled"/> while packet queries are disabled
    /// (restart in progress or failed closed, or shutdown finished). Allowed in
    /// <see cref="SessionPhase.Idle"/>, <see cref="SessionPhase.Running"/>, and <see cref="SessionPhase.Stopped"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="listener"/> or <paramref name="request"/> is null.</exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ValueCacheUiNameEmpty"/> when <see cref="IValueCacheListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ValueCacheIdExhausted"/> when the value-cache ID limit is reached.
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/> when a field or group name is not a valid identifier.
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/> when a well-formed field or group name is not on the current stack.
    /// </exception>
    bool TryAddValueCache(
        IValueCacheListener listener,
        ValueCacheRequest request,
        [NotNullWhen(true)] out ValueCacheInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure);

    // ── Job management ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to add a user-defined background job. The job starts immediately
    /// on a dedicated thread if successful. Allowed in every phase until shutdown starts, including
    /// <see cref="SessionPhase.Stopped"/> after all sources finished (for example an export job).
    /// Returns <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> once
    /// <see cref="Shutdown"/> has started, also after it finished: nobody would cancel or join the job.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobUiNameEmpty"/> when <paramref name="uiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddJob(string uiName, string description, Action<CancellationToken> work,
        [NotNullWhen(true)] out JobInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Removes a completed, cancelled, or failed job from the job list.
    /// Returns <see langword="false"/> if <paramref name="job"/> is not registered in this session
    /// or was already removed.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobStillRunning"/> when the job is still pending or running.
    /// </exception>
    bool TryRemoveJob(JobInfo job, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Attempts to unsubscribe (stop) a job. The behaviour depends on the job type:
    ///
    /// <list type="bullet">
    ///   <item><b>Source job:</b> Cancels the source's frame-reading loop. The source thread
    ///         exits after the current frame. The source remains available for random access
    ///         (<see cref="IRandomAccessFrameSource.FrameById"/>) and reparse. Final disposal
    ///         happens during <see cref="Shutdown"/>. If this was the last active source,
    ///         the session transitions to <see cref="SessionPhase.Stopped"/>.</item>
    ///   <item><b>Listener job:</b> Cancels the listener slot. The listener's
    ///         <see cref="ISessionListener.OnUnsubscribed"/> callback is called before the
    ///         thread exits. The listener is removed from the active listener list.</item>
    ///   <item><b>User job:</b> Cancels the job via its <see cref="CancellationToken"/>.</item>
    /// </list>
    ///
    /// <para>
    /// Returns <see langword="false"/> if the job is not owned by this session, is already
    /// in a terminal state (<see cref="JobStatus.Completed"/>, <see cref="JobStatus.Cancelled"/>,
    /// or <see cref="JobStatus.Failed"/>), or if the session is in the
    /// <see cref="SessionPhase.ShuttingDown"/> phase. In <see cref="SessionPhase.Idle"/> a source that
    /// has not started is removed (and disposed) instead of cancelled.
    /// </para>
    ///
    /// <para>
    /// Do not unsubscribe a listener or value-cache job from its own callback: the call waits for that
    /// job's thread to exit, which is the calling thread.
    /// </para>
    ///
    /// <para>Thread-safe. Multiple concurrent calls for different jobs are safe.
    /// Calling for the same job concurrently is safe (one succeeds, others return false).</para>
    /// </summary>
    bool TryUnsubscribe(JobInfo job, [NotNullWhen(false)] out SessionFailure? failure);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the session populates the packet index during the first parse of each frame.
    /// When <see langword="false"/>, <see cref="ISessionReader.PacketIndex"/> stays
    /// <see langword="null"/> after start.
    /// </summary>
    bool IndexPackets { get; }

    /// <summary>
    /// Attempts to start all registered source jobs.
    /// Returns <see langword="true"/> if the session transitioned to
    /// <see cref="SessionPhase.Running"/>, <see langword="false"/> if the session
    /// was not in the <see cref="SessionPhase.Idle"/> phase.
    /// </summary>
    bool TryStart([NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Waits for all source jobs to finish. Blocks the calling thread.
    /// Returns <see langword="true"/> if all source jobs completed,
    /// <see langword="false"/> if the <paramref name="timeout"/> elapsed first.
    /// The timeout is one budget for the whole source set, not a fresh timeout per source.
    /// Pass <see langword="null"/> to wait indefinitely (default).
    /// </summary>
    bool WaitForCompletion(TimeSpan? timeout = null);

    /// <summary>
    /// Swaps the protocol stack and re-parses all existing frames in ascending
    /// order, without stopping running frame sources.
    ///
    /// <para>
    /// <b>Concurrency:</b>
    /// Source threads are temporarily gated (kernel-level wait, no CPU burn) so
    /// they continue capturing frames via <see cref="IFrameSource.NextFrame"/>
    /// but do not parse until the re-parse is complete. No data is lost.
    /// </para>
    ///
    /// <para>
    /// <b>Re-parse ordering:</b>
    /// All previously parsed frames are re-parsed in the original PacketId order
    /// (0 … N-1) from the bound source’s <see cref="IRandomAccessFrameSource.FrameById"/>.
    /// Stream sources are wrapped at add time, so their past frames remain readable.
    /// An <see cref="IRandomAccessFrameSource"/> added without
    /// <see cref="FrameSourceAddOptions.CacheRandomAccess"/> is re-parsed from that
    /// inner source. A mapping miss, a source that is not random-access, or a
    /// <see langword="null"/> <see cref="IRandomAccessFrameSource.FrameById"/> result
    /// throws <see cref="SessionException"/> with
    /// <see cref="SessionErrorCode.FrameUnavailable"/> (fail closed; PacketIds are not compressed).
    /// After that throw, pull queries stay disabled so
    /// <see cref="ISessionReader.TryGetPacket(PacketId, out Packet?, out SessionFailure?)"/>
    /// cannot read a partial rewrite; <see cref="ISessionReader.PacketCount"/> may be less than
    /// the pre-restart count. Call <see cref="Shutdown"/> to tear down.
    /// </para>
    ///
    /// <para>
    /// <b>Listener notification:</b>
    /// After re-parsing, listeners receive <see cref="NotifyFlags.StackChanged"/>
    /// followed by <see cref="NotifyFlags.NewPackets"/> (with the cursor reset to
    /// 0, so the full re-parsed range is delivered). Listeners should discard any
    /// cached protocol/field state when they receive
    /// <see cref="ISessionListener.OnStackChanged"/>.
    /// </para>
    ///
    /// <para>
    /// The factory receives the session's internal <see cref="FrameInterfaceRegistry"/>
    /// so the new stack can be built with the same registry. This keeps source and
    /// interface IDs stable without exposing the registry publicly.
    /// The factory must not call <see cref="Shutdown"/>, <see cref="Restart"/>,
    /// <see cref="IDisposable.Dispose"/>, or <see cref="TryStart"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Failure before any change:</b> the ingest value cache of <see cref="SessionOptions.ValueCache"/>
    /// and the packet index are built on the new stack before the phase, the parse gate, or the queries
    /// change. A request field the new stack does not define therefore throws
    /// (<see cref="SessionErrorCode.ValueCacheUnknownField"/> /
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>) and leaves the session exactly as it was:
    /// same stack, same phase, queries enabled. The session disposes the unused new stack.
    /// From the stack swap on, the session owns the new stack, also when a later step fails.
    /// </para>
    ///
    /// <para>
    /// <b>Runtime value caches:</b> slots added with <see cref="TryAddValueCache"/> rebuild their cache
    /// from the stored request on their own thread when they receive <see cref="NotifyFlags.StackChanged"/>.
    /// A request field the new stack does not define fails that slot (its job ends
    /// <see cref="JobStatus.Failed"/>), not the restart.
    /// </para>
    ///
    /// <para>
    /// <b>Blocking:</b> the call returns after every frame was re-parsed. Run it off a UI thread; while it
    /// runs, <see cref="ISessionReader.QueriesEnabled"/> is <see langword="false"/> and pull reads are refused.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// session.Restart(registry =>
    /// {
    ///     StackBuilder builder = new(newSettings, registry);
    ///     builder.RegisterStandardProtocols();
    ///     return builder.Build();
    /// });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="stackFactory">
    /// A factory that receives the session's <see cref="FrameInterfaceRegistry"/> and
    /// returns a new <see cref="Stack"/> built with that registry.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The stack returned by <paramref name="stackFactory"/> uses a different
    /// <see cref="FrameInterfaceRegistry"/> than the one passed to the factory.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A restart is already in progress, or the factory returned <see langword="null"/>.
    /// </exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.InvalidPhase"/> when the session is not in the <see cref="SessionPhase.Running"/>
    /// or <see cref="SessionPhase.Stopped"/> phase, or shutdown has started (also while the factory ran).
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/> or <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>
    /// when the new stack cannot satisfy <see cref="SessionOptions.ValueCache"/>; thrown before any state changes.
    /// <see cref="SessionErrorCode.FrameUnavailable"/> when a mapped frame cannot be re-read; queries then stay
    /// disabled (fail closed).
    /// </exception>
    void Restart(Func<FrameInterfaceRegistry, Stack> stackFactory);

    /// <summary>
    /// Shuts down the session. The shutdown procedure is:
    /// <list type="number">
    ///   <item>Transition to <see cref="SessionPhase.ShuttingDown"/>.</item>
    ///   <item>Cancel all source jobs.</item>
    ///   <item>Wait for source jobs to finish (up to <paramref name="timeout"/> if specified).</item>
    ///   <item>Cancel and drain all listener slots (queries stay enabled so redissect works).</item>
    ///   <item>Disable packet queries.</item>
    ///   <item>Transition to <see cref="SessionPhase.Stopped"/>.</item>
    ///   <item>Dispose all jobs and listener slots.</item>
    /// </list>
    ///
    /// <para>
    /// If <paramref name="timeout"/> is <see langword="null"/>, waits indefinitely for
    /// graceful completion. If a <see cref="TimeSpan"/> is provided, that duration is one
    /// budget for every source job together before shutdown teardown continues; jobs that are still
    /// running remain cancelled but may not have exited yet — inspect job status via
    /// <see cref="ISessionReader.GetJobs"/>. <c>Shutdown(TimeSpan.Zero)</c> skips waiting
    /// for source completion.
    /// </para>
    ///
    /// <para>Idempotent — safe to call multiple times.</para>
    ///
    /// <para>
    /// <b>Error handling:</b>
    /// If any job, listener slot, or frame source fails to dispose during cleanup,
    /// all remaining items are still disposed. After cleanup completes, all collected
    /// exceptions are thrown as a single <see cref="AggregateException"/>. When called
    /// via <see cref="IDisposable.Dispose"/>, the exception is captured in
    /// <c>Session.ShutdownErrors</c> instead (Dispose must not throw).
    /// </para>
    /// </summary>
    /// <exception cref="AggregateException">
    /// Thrown when one or more disposal operations failed during cleanup.
    /// </exception>
    void Shutdown(TimeSpan? timeout = null);

    #endregion
}
