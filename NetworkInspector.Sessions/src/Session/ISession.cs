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
    /// Returns <see langword="false"/> if the session is not in the correct phase, also when <see cref="TryStart"/> or
    /// <see cref="Shutdown"/> wins a race with the add (a source is either started or refused, never left unstarted).
    /// Concurrent adds are safe and lose no source.
    /// After a successful add the session owns <paramref name="source"/> (or a
    /// <see cref="CachedFrameSource"/> wrapper that owns it). The caller must not dispose it.
    /// Non-random-access sources are always wrapped so frames remain readable after ingest.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached;
    /// <see cref="SessionErrorCode.Disposed"/> when the session was disposed.
    /// </exception>
    bool TryAddFrameSource(IFrameSource source, [NotNullWhen(true)] out FrameSourceInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Registers a frame source with per-source cache options.
    /// <see cref="FrameSourceAddOptions.CacheRandomAccess"/> wraps an
    /// <see cref="IRandomAccessFrameSource"/> in <see cref="CachedFrameSource"/>
    /// (holds the inner <see cref="Frame"/>; payload memory is not copied).
    /// Stream sources are always wrapped. Phase rule, ownership and failures are those of
    /// <see cref="TryAddFrameSource(IFrameSource, out FrameSourceInfo?, out SessionFailure?)"/>, which is this
    /// overload with <see cref="FrameSourceAddOptions.Default"/>.
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
    /// restart (<see cref="TryRestart(Func{FrameInterfaceRegistry, Stack}, RestartOptions, out RestartReport?, out SessionFailure?)"/>))
    /// is never evaluated against the wrong field ids: the session installs a re-bound
    /// copy from <see cref="NetworkInspector.Filter.Filter.TryDerive"/> and leaves the caller's instance
    /// untouched. A later restart re-binds the installed filter the same way.
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
    /// <see cref="SessionErrorCode.ValueCacheInvalidRequest"/> when the request cannot be built (empty, a field that records
    /// nothing, a duplicate series, <c>ChunkShift</c> out of range).
    /// </exception>
    bool TryAddValueCache(
        IValueCacheListener listener,
        ValueCacheRequest request,
        [NotNullWhen(true)] out ValueCacheInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure);

    // ── Job management ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to add a user-defined background job that depends on the stack
    /// (<see cref="JobStackBinding.StackBound"/>: this overload is the secure default, see
    /// <see cref="TryAddJob(string, string, JobStackBinding, Action{JobContext}, out JobInfo?, out SessionFailure?)"/>). The
    /// job starts immediately on a dedicated thread if successful. Allowed in every phase until shutdown starts, including
    /// <see cref="SessionPhase.Stopped"/> after all sources finished (for example an export job).
    /// Returns <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> once
    /// <see cref="Shutdown"/> has started, also after it finished: nobody would cancel or join the job. Returns
    /// <see langword="false"/> with <see cref="SessionErrorCode.RestartPending"/> while a restart is between its job check and
    /// its end (a stack-bound job added then would extend the restart's wait or run on into the swap), and with
    /// <see cref="SessionErrorCode.SessionFaulted"/> while the session is <see cref="SessionPhase.Faulted"/>.
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobUiNameEmpty"/> when <paramref name="uiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddJob(string uiName, string description, Action<CancellationToken> work,
        [NotNullWhen(true)] out JobInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Attempts to add a user-defined background job that declares whether it depends on the stack, and hands its work a
    /// <see cref="JobContext"/> (token, the <see cref="StackView"/> it started in, the reader).
    ///
    /// <para>
    /// A <see cref="JobStackBinding.StackBound"/> job reads session data or holds field ids, filters, caches or catalogs; a
    /// restart cancels it with <see cref="JobEndReason.StackReplaced"/>, waits for it, or is refused while it runs, as the
    /// caller of <c>TryRestart</c> chose. Its <see cref="JobInfo.Generation"/> is the generation current when it was added.
    /// An <see cref="JobStackBinding.Independent"/> job (generation -1) is never touched by a restart. The owner of a job
    /// that was ended by a restart sees <see cref="JobEndReason.StackReplaced"/> in the same snapshot as the terminal
    /// status and can re-create the job on the new stack.
    /// </para>
    ///
    /// <para>
    /// Phase rules and refusals are those of
    /// <see cref="TryAddJob(string, string, Action{CancellationToken}, out JobInfo?, out SessionFailure?)"/>; the
    /// <see cref="SessionErrorCode.RestartPending"/> refusal applies to <see cref="JobStackBinding.StackBound"/> jobs only.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> is not a defined value.</exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.JobUiNameEmpty"/> when <paramref name="uiName"/> is null or whitespace.
    /// <see cref="SessionErrorCode.JobIdExhausted"/> when the job ID limit is reached.
    /// </exception>
    bool TryAddJob(string uiName, string description, JobStackBinding binding, Action<JobContext> work,
        [NotNullWhen(true)] out JobInfo? info, [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Removes a completed, cancelled, or failed job from the job list.
    /// Returns <see langword="false"/> with <see cref="SessionErrorCode.JobNotFound"/> if <paramref name="job"/> is not
    /// registered in this session or was already removed, and with <see cref="SessionErrorCode.JobStillRunning"/> if the
    /// job is still pending or running (cancel it and wait for it first); the job then stays registered.
    /// </summary>
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
    /// was not in the <see cref="SessionPhase.Idle"/> phase (or had started shutting down).
    /// Of several concurrent callers exactly one succeeds; the others get
    /// <see cref="SessionErrorCode.InvalidPhase"/>.
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
    /// Swaps the protocol stack and re-parses all existing frames in ascending order, without stopping running frame
    /// sources, with <see cref="RestartOptions.Default"/> (cancel stack-bound jobs now). See
    /// <see cref="TryRestart(Func{FrameInterfaceRegistry, Stack}, RestartOptions, out RestartReport?, out SessionFailure?)"/>
    /// for everything this call does and every way it can be refused.
    /// </summary>
    /// <param name="stackFactory">
    /// A factory that receives the session's <see cref="FrameInterfaceRegistry"/> and returns a new <see cref="Stack"/> built
    /// with that registry.
    /// </param>
    /// <param name="report">What the restart did; set exactly when the call returns <see langword="true"/>.</param>
    /// <param name="failure">Why the restart did not happen or did not finish; set exactly when the call returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the new stack serves reads.</returns>
    bool TryRestart(
        Func<FrameInterfaceRegistry, Stack> stackFactory,
        [NotNullWhen(true)] out RestartReport? report,
        [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Swaps the protocol stack and re-parses all existing frames in ascending order, without stopping running frame
    /// sources. This is the one restart call; every expected refusal is a result (<see langword="false"/> plus a
    /// <see cref="SessionFailure"/>), never an exception, so a host can show it to the user and an MCP tool can return it.
    ///
    /// <para>
    /// <b>Generation:</b> the swap raises <see cref="ISessionReader.Generation"/> by one while reads are still disabled;
    /// reads are enabled again only after listener filters and cursors were rebound. A caller that polls only the
    /// generation and <see cref="ISessionReader.QueriesEnabled"/> sees stable (N, enabled), in progress (N + 1, disabled),
    /// done (N + 1, enabled). A <see cref="StackView"/> taken before the call keeps naming the old stack.
    /// </para>
    ///
    /// <para>
    /// <b>Order of one restart:</b>
    /// <list type="number">
    ///   <item>Claim: phase check (<see cref="SessionPhase.Running"/>, <see cref="SessionPhase.Stopped"/> or
    ///         <see cref="SessionPhase.Faulted"/>), no shutdown, no restart running.</item>
    ///   <item>Job check: the <see cref="JobStackBinding.StackBound"/> jobs that are pending or running are snapshotted;
    ///         under <see cref="RestartJobPolicy.Refuse"/> any of them refuses the restart (nothing changed). From here on no
    ///         stack-bound job can be added (<see cref="SessionErrorCode.RestartPending"/>), so the snapshot stays complete.</item>
    ///   <item>The factory runs, outside every lock. A factory that throws costs no job: ending a job is the only step that
    ///         cannot be undone, so it comes after the step that most often fails.</item>
    ///   <item>Job phase: <see cref="RestartJobPolicy.CancelNow"/> cancels the snapshotted jobs with
    ///         <see cref="JobEndReason.StackReplaced"/>; <see cref="RestartJobPolicy.WaitThenCancel"/> waits up to
    ///         <see cref="RestartOptions.JobWaitTimeout"/> (observing <see cref="RestartOptions.WaitCancellation"/> and a
    ///         starting shutdown) and cancels the rest.</item>
    ///   <item>Swap, re-parse of every packet from id 0, listener filters re-bound, queries enabled, listeners woken with
    ///         <see cref="NotifyFlags.StackChanged"/> followed by <see cref="NotifyFlags.NewPackets"/> (cursor reset to 0), the
    ///         <see cref="Restarted"/> notification posted.</item>
    /// </list>
    /// The policies, in one table (what you lose):
    /// <list type="table">
    ///   <listheader><term>Policy</term><description>Effect and what you lose</description></listheader>
    ///   <item><term>CancelNow</term><description>Stack-bound jobs are cancelled at once. You lose their unfinished work.</description></item>
    ///   <item><term>WaitThenCancel</term><description>The call blocks up to the bound; jobs that end in time keep their result,
    ///   the rest are cancelled. You lose time (and the unfinished work of jobs that outlast the bound).</description></item>
    ///   <item><term>Refuse</term><description>No restart while a stack-bound job runs; the failure names the jobs.
    ///   You lose the restart until the jobs end.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Concurrency:</b> source threads are temporarily gated (kernel-level wait, no CPU burn) so they continue capturing
    /// frames via <see cref="IFrameSource.NextFrame"/> but do not parse until the re-parse is complete. No data is lost.
    /// A read that started before the swap finishes on the stack it leased (one read call parses on one generation); a read
    /// that starts after it is refused until the re-parse ended.
    /// </para>
    ///
    /// <para>
    /// <b>Re-parse ordering and failure:</b> all previously parsed frames are re-parsed in the original <see cref="PacketId"/>
    /// order (0 … N-1) from the bound source's <see cref="IRandomAccessFrameSource.FrameById"/>. A mapping miss, a source
    /// that is not random-access, or a <see langword="null"/> <c>FrameById</c> result fails the restart with
    /// <see cref="SessionErrorCode.FrameUnavailable"/> after the swap: the call returns <see langword="false"/>, the session
    /// is <see cref="SessionPhase.Faulted"/> (<see cref="ISessionReader.Fault"/> keeps the failure), reads and new work are
    /// refused with <see cref="SessionErrorCode.SessionFaulted"/>, and ingest stays parked. Another <c>TryRestart</c> (which
    /// re-parses from 0 and makes the counters agree again) or <see cref="Shutdown"/> are the only ways out.
    /// </para>
    ///
    /// <para>
    /// <b>Jobs ended before a later failure:</b> the job phase runs after the factory succeeded and after every refusal that
    /// depends only on the new stack (the ingest value-cache request, see below), so a refused restart never costs a running
    /// job. If the restart is abandoned later (shutdown started during the wait) or faulted in the re-parse, the jobs
    /// already ended stay ended (<see cref="JobEndReason.StackReplaced"/> in the job list); no report is returned.
    /// </para>
    ///
    /// <para>
    /// <b>Listener notification:</b> listeners receive <see cref="NotifyFlags.StackChanged"/> followed by
    /// <see cref="NotifyFlags.NewPackets"/> (cursor reset to 0, so the full re-parsed range is delivered). Listeners should
    /// discard any cached protocol/field state when they receive <see cref="ISessionListener.OnStackChanged"/>. Two restarts
    /// that a slow listener observes in one wake-up show as one callback with a generation two higher.
    /// </para>
    ///
    /// <para>
    /// The factory receives the session's internal <see cref="FrameInterfaceRegistry"/> so the new stack can be built with
    /// the same registry. This keeps source and interface IDs stable without exposing the registry publicly. The factory must
    /// not call <see cref="Shutdown"/>, <c>TryRestart</c>, <see cref="IDisposable.Dispose"/>, or <see cref="TryStart"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Failure before any change:</b> the ingest value cache of <see cref="SessionOptions.ValueCache"/> and the packet
    /// index are built on the new stack before the job phase, the phase, the parse gate, or the queries change. A request
    /// field the new stack does not define therefore refuses the restart (<see cref="SessionErrorCode.ValueCacheUnknownField"/>,
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>, <see cref="SessionErrorCode.ValueCacheInvalidRequest"/>)
    /// and leaves the session as it was: same stack, same phase, queries enabled, every job running. The session disposes
    /// the unused new stack.
    /// From the stack swap on, the session owns the new stack, also when a later step fails.
    /// </para>
    ///
    /// <para>
    /// <b>Replaced stack:</b> a stack the session created itself (an earlier restart factory) is disposed as soon as it can
    /// safely be: right after the swap when no session read is parsing on it, otherwise by the thread whose read is the last
    /// one that ends. The call does not wait for those reads. The stack passed to the <see cref="Session"/> constructor
    /// stays the caller's and is never disposed by the session. A <see cref="Packet"/> handed out before the restart keeps
    /// its materialised fields; its still unmaterialised lazy containers run on the replaced protocol instances and may
    /// report a failed population (an error child) once the stack was disposed, so materialise what you need before the
    /// restart or re-read after <see cref="ISessionListener.OnStackChanged"/>. <see cref="Shutdown"/> disposes the current
    /// stack and any replaced one that is still in use, waiting for those reads within its timeout budget.
    /// </para>
    ///
    /// <para>
    /// <b>Runtime value caches:</b> slots added with <see cref="TryAddValueCache"/> rebuild their cache from the stored
    /// request on their own thread when they receive <see cref="NotifyFlags.StackChanged"/>. A request field the new stack
    /// does not define fails that slot (its job ends <see cref="JobStatus.Failed"/>), not the restart.
    /// </para>
    ///
    /// <para>
    /// <b>Blocking:</b> the call returns after every frame was re-parsed (and, under
    /// <see cref="RestartJobPolicy.WaitThenCancel"/>, after the wait). Run it off a UI thread; while it runs,
    /// <see cref="ISessionReader.QueriesEnabled"/> is <see langword="false"/> and pull reads are refused. It may be called
    /// from a listener callback (listeners are not waited for), but a worker thread is preferred.
    /// </para>
    ///
    /// <para>
    /// <b>Refusals</b> (all returned as <paramref name="failure"/>): <see cref="SessionErrorCode.InvalidPhase"/> (Idle, shutting
    /// down, or abandoned because shutdown started), <see cref="SessionErrorCode.RestartInProgress"/>,
    /// <see cref="SessionErrorCode.RestartOptionOutOfRange"/>, <see cref="SessionErrorCode.JobsRunning"/>,
    /// <see cref="SessionErrorCode.RestartFromJob"/> (the calling thread is the thread of a running stack-bound job: it would
    /// wait for itself or cancel itself), the ingest value-cache codes above (nothing changed), and
    /// <see cref="SessionErrorCode.FrameUnavailable"/> (the session is now faulted).
    /// </para>
    ///
    /// <para>
    /// <b>What still throws</b> (caller bugs and the factory's own failure): <see cref="ArgumentNullException"/> for a null
    /// argument, <see cref="SessionException"/> with <see cref="SessionErrorCode.Disposed"/> on a disposed session,
    /// <see cref="InvalidOperationException"/> when the factory returns <see langword="null"/>,
    /// <see cref="ArgumentException"/> when the factory's stack uses another <see cref="FrameInterfaceRegistry"/>, and any
    /// exception the factory itself throws, unchanged. A bug in a parser during the re-parse faults the session and then
    /// propagates.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// RestartOptions options = new() { JobPolicy = RestartJobPolicy.WaitThenCancel, JobWaitTimeout = TimeSpan.FromSeconds(10) };
    /// if (!session.TryRestart(
    ///         registry =>
    ///         {
    ///             StackBuilder builder = new(newSettings, registry);
    ///             builder.RegisterStandardProtocols();
    ///             return builder.Build();
    ///         },
    ///         options,
    ///         out RestartReport? report,
    ///         out SessionFailure? failure))
    /// {
    ///     ShowError(failure.Message);
    ///     return;
    /// }
    ///
    /// ShowInfo($"Restarted to generation {report.Generation}; cancelled {report.JobsCancelled.Count} jobs.");
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="stackFactory">
    /// A factory that receives the session's <see cref="FrameInterfaceRegistry"/> and returns a new <see cref="Stack"/> built
    /// with that registry.
    /// </param>
    /// <param name="options">The job policy, the wait bound and the wait cancellation. Not <see langword="null"/>.</param>
    /// <param name="report">What the restart did; set exactly when the call returns <see langword="true"/>.</param>
    /// <param name="failure">Why the restart did not happen or did not finish; set exactly when the call returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the new stack serves reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stackFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The stack returned by <paramref name="stackFactory"/> uses a different <see cref="FrameInterfaceRegistry"/> than the one
    /// passed to the factory.
    /// </exception>
    /// <exception cref="InvalidOperationException">The factory returned <see langword="null"/>.</exception>
    /// <exception cref="SessionException"><see cref="SessionErrorCode.Disposed"/> when the session was disposed.</exception>
    bool TryRestart(
        Func<FrameInterfaceRegistry, Stack> stackFactory,
        RestartOptions options,
        [NotNullWhen(true)] out RestartReport? report,
        [NotNullWhen(false)] out SessionFailure? failure);

    /// <summary>
    /// Raised after every restart that completed or faulted, on a thread of the session and never on the restarting thread.
    ///
    /// <para>
    /// <b>One slot per handler:</b> each handler added has its own queue, its own thread, and an
    /// <see cref="JobStackBinding.Independent"/> job named <c>restart-event</c> in <see cref="ISessionReader.GetJobs"/>. A
    /// handler that blocks delays only its own notifications; a handler that throws ends only its own job
    /// (<see cref="JobStatus.Failed"/> with <see cref="JobInfo.FailureException"/>, <see cref="NotifyFlags.JobStatusChanged"/>
    /// raised) and receives nothing more; the restart, the session's reads and the other handlers are unaffected.
    /// Notifications are never merged: a handler sees one per restart, in generation order. When the handler runs, the
    /// session already reads the generation of the notification with reads enabled (after a successful restart).
    /// </para>
    ///
    /// <para>
    /// <b>Cost:</b> one idle thread per subscription. <b>Do not</b> call <see cref="Shutdown"/> or <see cref="IDisposable.Dispose"/>
    /// from a handler (they join every subscription, including the calling one). A handler that removes itself from inside
    /// its own callback (<c>-=</c>, or <see cref="TryUnsubscribe"/> of its job) is refused, because the removal would join
    /// the calling thread: <c>-=</c> throws <see cref="InvalidOperationException"/>, <see cref="TryUnsubscribe"/> returns
    /// <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/>. Remove a handler from another thread.
    /// <see cref="Shutdown"/> delivers what is queued within its time budget and reports the undelivered count in its
    /// aggregate message.
    /// </para>
    /// </summary>
    /// <exception cref="SessionException">
    /// On <c>add</c>: <see cref="SessionErrorCode.InvalidPhase"/> once shutdown has started (an event accessor cannot return
    /// a result), <see cref="SessionErrorCode.Disposed"/> on a disposed session.
    /// </exception>
    /// <exception cref="InvalidOperationException">On <c>remove</c>: the handler removes itself from inside its own callback.</exception>
    event Action<RestartNotification>? Restarted;

    /// <summary>
    /// Shuts down the session. The shutdown procedure is:
    /// <list type="number">
    ///   <item>Transition to <see cref="SessionPhase.ShuttingDown"/>.</item>
    ///   <item>
    ///     Cancel all source jobs and user jobs
    ///     (<see cref="TryAddJob(string, string, Action{CancellationToken}, out JobInfo?, out SessionFailure?)"/>).
    ///   </item>
    ///   <item>Wait for source jobs and then user jobs to finish (up to <paramref name="timeout"/> if specified), then
    ///         let every <see cref="Restarted"/> subscription deliver what is queued and exit (within the same budget; the
    ///         notifications it could not deliver are counted in the aggregate message). A restart that is running is
    ///         waited for first, and a wait for stack-bound jobs inside it ends at once; a session in
    ///         <see cref="SessionPhase.Faulted"/> shuts down like any other, its parked sources end Cancelled.</item>
    ///   <item>Cancel and drain all listener slots (queries stay enabled so redissect works). A flag raised before the
    ///         cancellation is still delivered.</item>
    ///   <item>Disable packet queries.</item>
    ///   <item>Transition to <see cref="SessionPhase.Stopped"/>.</item>
    ///   <item>Dispose all jobs and listener slots.</item>
    /// </list>
    ///
    /// <para>
    /// If <paramref name="timeout"/> is <see langword="null"/>, waits indefinitely for
    /// graceful completion. If a <see cref="TimeSpan"/> is provided, that duration is one
    /// budget for every source job and user job together before shutdown teardown continues; jobs that are still
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
