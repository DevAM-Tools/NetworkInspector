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
    // ── Source management ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to register a frame source. Must be called while the session is
    /// in the <see cref="SessionPhase.Idle"/> phase.
    /// Returns <see langword="false"/> if the session is not in the correct phase.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddFrameSource(IFrameSource source, [NotNullWhen(true)] out FrameSourceInfo? info);

    // ── Listener management ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to register a session listener. May be called while the session is
    /// <see cref="SessionPhase.Idle"/>, <see cref="SessionPhase.Running"/>, or
    /// <see cref="SessionPhase.Restarting"/>.
    /// Returns <see langword="false"/> if the session is shutting down or stopped.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/> when <see cref="ISessionListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ListenerIdExhausted"/> when the listener ID limit is reached.
    /// </exception>
    bool TryAddListener(ISessionListener listener, [NotNullWhen(true)] out ListenerInfo? info);

    /// <summary>
    /// Registers a session listener together with the filter it pulls matching packets with.
    ///
    /// <para>
    /// The filter is used only by <see cref="ISessionReader.TryReadPackets"/> in
    /// <see cref="PacketReadMode.Matching"/> mode. Notifications stay unfiltered:
    /// <see cref="ISessionListener.OnNewPackets"/> always reports the full id window of newly
    /// stored packets, so a listener is never starved of wake-ups by its own filter.
    /// </para>
    ///
    /// <para>
    /// A <see langword="null"/> filter means "no filter" and is equivalent to
    /// <see cref="NetworkInspector.Filter.Filter.AlwaysMatch"/>.
    /// The filter must have been compiled against the session's current stack; a
    /// <see cref="Restart"/> re-binds it automatically via
    /// <see cref="IFilter.TryDerive"/>.
    /// </para>
    ///
    /// <para>
    /// A filter instance is single-threaded and must not be shared between listeners; each
    /// listener evaluates its own filter on its own thread.
    /// </para>
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/> when <see cref="ISessionListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ListenerIdExhausted"/> when the listener ID limit is reached.
    /// </exception>
    bool TryAddListener(ISessionListener listener, IFilter? filter, [NotNullWhen(true)] out ListenerInfo? info);

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
    /// <paramref name="filterFailure"/> describes the problem and no listener is registered — or
    /// when the session is shutting down or stopped, in which case both out parameters are
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
        out FilterError? filterFailure);

    // ── Value-cache management ───────────────────────────────────────────────

    /// <summary>
    /// Registers a dedicated value cache filled from packet id 0 by a pull slot,
    /// analog to <see cref="TryAddListener(ISessionListener, out ListenerInfo?)"/>.
    /// Always constructs a new cache; existing caches are never reused as a cover.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the session is shutting down or stopped.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="listener"/> or <paramref name="request"/> is null.</exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ValueCacheUiNameEmpty"/> when <see cref="IValueCacheListener.UiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.ValueCacheIdExhausted"/> when the value-cache ID limit is reached.
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/> when a field or group name is not a valid identifier.
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/> when a well-formed field or group name is not on the current stack.
    /// </exception>
    bool TryAddValueCache(IValueCacheListener listener, ValueCacheRequest request, [NotNullWhen(true)] out ValueCacheInfo? info);

    // ── Job management ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to add a user-defined background job. The job starts immediately
    /// on a dedicated thread if successful.
    /// Returns <see langword="false"/> if the session is shutting down or stopped.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobUiNameEmpty"/> when <paramref name="uiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddJob(string uiName, string description, Action<CancellationToken> work,
        [NotNullWhen(true)] out JobInfo? info);

    /// <summary>
    /// Removes a completed, cancelled, or failed job from the job list.
    /// Returns <see langword="false"/> if <paramref name="job"/> is not registered in this session
    /// or was already removed.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobStillRunning"/> when the job is still pending or running.
    /// </exception>
    bool TryRemoveJob(JobInfo job);

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
    /// <see cref="SessionPhase.Idle"/> or <see cref="SessionPhase.ShuttingDown"/> phase.
    /// </para>
    ///
    /// <para>Thread-safe. Multiple concurrent calls for different jobs are safe.
    /// Calling for the same job concurrently is safe (one succeeds, others return false).</para>
    /// </summary>
    bool TryUnsubscribe(JobInfo job);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the session stores sealed packets for later lock-free reads.
    /// When <see langword="false"/>, <see cref="ISessionReader.TryGetPacket(PacketId, out Packet)"/>
    /// re-parses from the frame source; stateful protocols replay the state recorded during the first
    /// parse.
    /// </summary>
    bool StoreParsedPackets { get; }

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
    bool TryStart();

    /// <summary>
    /// Waits for all source jobs to finish. Blocks the calling thread.
    /// Returns <see langword="true"/> if all source jobs completed,
    /// <see langword="false"/> if the <paramref name="timeout"/> elapsed first.
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
    /// (0 … N-1). Sources that do not support random access are skipped; their
    /// past frames cannot be retrieved.
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
    /// The session is not in the <see cref="SessionPhase.Running"/> or
    /// <see cref="SessionPhase.Stopped"/> phase.
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
    /// graceful completion. If a <see cref="TimeSpan"/> is provided, source jobs are
    /// given that long to finish before shutdown teardown continues; jobs that are still
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
}
