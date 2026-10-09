// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Orchestrates frame sources, the protocol stack, listeners,
/// and background jobs using a pull-based notification model.
///
/// <para>
/// <b>Source-thread model:</b>
/// Each <see cref="IFrameSource"/> receives a dedicated job thread that pulls frames via
/// <see cref="IFrameSource.NextFrame"/>, parses them under a shared Monitor
/// (<see cref="_ParseMutex"/>), announces the packet id, and sets <see cref="NotifyFlags.NewPackets"/> on all
/// listener slots via <c>Interlocked.Or</c>.
/// </para>
///
/// <para>
/// <b>Listener-thread model (pull-based):</b>
/// Each <see cref="ISessionListener"/> subscription receives a dedicated
/// <see cref="ListenerSlot"/> with an event-gated wait loop backed by
/// <see cref="ManualResetEventSlim"/>. Producers set atomic flags and signal the
/// wake event; the listener thread reads and clears flags via <c>Interlocked.Exchange</c>,
/// then pulls data from the shared <see cref="ISessionReader"/>. No queues, no batch
/// copies. Natural coalescing: multiple events between two wake cycles merge into a
/// single flag read.
/// </para>
///
/// <para>
/// <b>Why Monitor for parsing:</b>
/// <see cref="Stack"/> modifies protocol-instance state during parsing
/// (lazy field arrays, reassembly buffers). A single shared Monitor serialises all
/// concurrent source threads. One frame parse is long enough that waiting threads
/// should kernel-wait rather than spin.
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><c>TryAddFrameSource</c> -- safe before <c>TryStart</c> on any thread.</item>
///   <item><c>TryAddListener</c> -- safe before or after <c>TryStart</c>.</item>
///   <item><c>TryStart</c> -- safe once; transitions phase to Running.</item>
///   <item><c>PacketCount</c>, <c>FrameCount</c> -- volatile reads, always current.</item>
///   <item><c>TryGetPacket</c> -- safe from any thread (re-parse from frame cache or RA source).</item>
///   <item><c>TryAddJob</c> -- safe from any thread.</item>
///   <item><c>Shutdown</c> / <c>Dispose</c> -- safe to call from any thread.</item>
/// </list>
/// </para>
/// </summary>
public sealed class Session : ISession, ISessionReader
{
    #region Construction

    /// <summary>Creates a new session bound to <paramref name="stack"/>.</summary>
    /// <param name="stack">Protocol stack used for the first parse of each frame.</param>
    /// <param name="options">Construction-time options. <see langword="null"/> uses <see cref="SessionOptions.Default"/>.</param>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/> or
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/> when
    /// <see cref="SessionOptions.ValueCache"/> names fail identifier validation or stack lookup.
    /// <see cref="SessionErrorCode.ValueCacheListenerWithoutRequest"/> when a listener is set without a request.
    /// </exception>
    public Session(Stack stack, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _Stack = stack;
        _FrameInterfaceRegistry = stack.FrameInterfaceRegistry;
        SessionOptions resolved = options ?? SessionOptions.Default;
        IndexPackets = resolved.IndexPackets;
        _IngestRequest = resolved.ValueCache;
        _IngestListener = resolved.ValueCacheListener;
        _InitializeValueCaches();
    }

    #endregion

    #region Fields

    // -- Configuration --

    // Non-readonly: Restart() swaps the stack for a new one.
    // Volatile so readers on other threads observe the swap without a lock.
    private volatile Stack _Stack;

    // True when the session created the stack via factory (Restart). False for the
    // initial stack passed to the constructor (caller manages its lifetime).
    private volatile bool _OwnsStack;

    // Session-owned registry: shared across all stacks. Extracted from the
    // initial stack so that source and interface IDs remain stable across restarts.
    private readonly FrameInterfaceRegistry _FrameInterfaceRegistry;

    // -- State --

    private readonly SessionState _State = new();

    // Global atomic counters readable from any thread.
    private volatile int _PacketCount;
    private volatile int _FrameCount;

    // Number of source jobs that have not yet finished.
    // Transitions to 0 means all sources are done.
    private volatile int _ActiveSourceCount;

    // -- Parse lock --

    // Monitor protects Stack parsing which mutates protocol-instance state.
    // One frame parse is long enough that waiters should kernel-wait, not spin.
    private readonly object _ParseMutex = new();

    // Globally unique PacketId counter (shared across all source threads).
    // Allocated INSIDE the parse mutex to guarantee correctness after a reparse
    // (which resets the counter under the lock).
    private volatile int _NextPacketId;

    // Kernel-level gate that blocks source threads during a stack-swap reparse.
    // Initially signalled (open): Wait() returns immediately during normal parsing.
    // During Restart(), the gate is Reset() (closed) to park source threads,
    // then Set() (opened) after all frames have been re-parsed.
    private readonly ManualResetEventSlim _ParseGate = new(initialState: true);

    // Guards against concurrent Restart() calls.
    // 0 = idle, 1 = restart in progress. Shutdown waits for 0 before disposing the parse gate.
    private volatile int _RestartInProgress;

    // Serialises Restart's swap with Shutdown. Not taken on the parse hot path.
    private readonly object _LifecycleLock = new();

    // -- Shared stores --

    // PacketId → packed (FrameId, FrameSourceId). Dense sequential appends; miss = index >= Count.
    // 8192 longs = 64 KB, under the ~85 KB large-object threshold.
    // Shift 16 would be 512 KB and would be collected with generation 2.
    private const int _PacketToFrameChunkShift = 13;
    private readonly ChunkedGrowOnlyStore<long> _PacketToFrame = new(_PacketToFrameChunkShift);

    // Roaring Bitmap index populated during parsing (protocol presence, field groups).
    // Created by _StartInternal(), set to null by Restart().
    private volatile PacketIndex? _PacketIndex;

    // -- Source registry --

    // Sources registered before Start(). Re-used on Restart().
    private readonly SnapshotList<FrameSourceEntry> _SourceEntries = new();

    // Public read-only views of frame sources for GetFrameSources().
    private readonly SnapshotList<FrameSourceInfo> _SourceInfos = new();


    // Running source jobs (populated at Start() time). Published only after every slot is filled.
    private volatile Job[] _SourceJobs = [];

    // Random-access capable sources keyed by FrameSourceId for GetPacket().
    // Copy-on-write: written only during _AddFrameSourceInternal (rare), read during TryGetPacket (hot).
    // Volatile reference swap replaces the previous lock(object) pattern for lock-free reads.
    private volatile Dictionary<FrameSourceId, IRandomAccessFrameSource> _RandomAccessSources = [];

    // -- Listener registry --

    // Active listener slots. SnapshotList for lock-free iteration by source threads.
    private readonly SnapshotList<ListenerSlot> _ListenerSlots = new();

    // Public read-only views of listener subscriptions for GetListeners().
    private readonly SnapshotList<ListenerInfo> _ListenerInfos = new();

    // -- Unified job list --

    // All jobs: source, listener, and user jobs are all registered here.
    // Lock-free: reads return the current snapshot, writes use CAS retry loop.
    private readonly SnapshotList<JobInfo> _AllJobs = new();

    // -- Value-cache registry --

    private readonly ValueCacheRequest? _IngestRequest;
    private readonly IValueCacheListener? _IngestListener;
    private volatile ValueCache? _IngestValueCache;
    private ValueCacheInfo? _IngestInfo;
    private readonly SnapshotList<ValueCacheSlot> _ValueCacheSlots = new();
    private readonly SnapshotList<ValueCacheInfo> _ValueCacheInfos = new();

    // -- Disposal --

    // Volatile: read by _ThrowIfDisposed (any thread), written by Dispose (any thread).
    private volatile bool _Disposed;

    // Exceptions that occurred during disposal in Shutdown(). Populated by Dispose()
    // when Shutdown() throws an AggregateException. Callers can inspect this after
    // Dispose() returns to detect cleanup failures without Dispose() throwing.
    private volatile AggregateException? _ShutdownErrors;

    // Guards against double shutdown (Shutdown() called explicitly then via Dispose()).
    // Accessed atomically via Interlocked — multiple threads may call Shutdown() concurrently.
    private volatile int _ListenersTornDown; // 0 = false, 1 = true

    // Ensures only one thread executes the shutdown body. Others wait for completion.
    private volatile int _ShutdownStarted; // 0 = not started, 1 = started

    // When true, TryGetPacket and ReadPackets return immediately without data.
    // Set during shutdown after source jobs finish, cleared on restart.
    private volatile bool _QueriesDisabled;

    #endregion

    #region ISessionReader

    // -- ISessionReader: Status --

    /// <inheritdoc/>
    public SessionPhase Phase => _State.Phase;

    /// <inheritdoc/>
    public int PacketCount => _PacketCount;

    /// <inheritdoc/>
    public int FrameCount => _FrameCount;

    /// <inheritdoc/>
    public bool MorePacketsExpected => _ActiveSourceCount > 0;

    /// <inheritdoc/>
    public bool QueriesEnabled => !_QueriesDisabled;

    /// <summary>Ingest never retains a field tree.</summary>
    private static FieldTreeMode _IngestFieldTreeMode => FieldTreeMode.Skip;

    /// <inheritdoc/>
    public bool IndexPackets { get; }

    /// <inheritdoc/>
    public Stack Stack => _Stack;

    /// <inheritdoc/>
    public ReadOnlyValueCache? IngestValueCache
    {
        get
        {
            ValueCache? ingest = _IngestValueCache;
            if (ingest is null)
            {
                return null;
            }

            return ingest.AsReadOnlyView();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<ValueCacheInfo> GetValueCaches() => _ValueCacheInfos.CurrentSnapshot;

    #endregion

    #region Sources

    // -- ISession: Source management --

    /// <inheritdoc/>
    public bool TryAddFrameSource(
        IFrameSource source,
        [NotNullWhen(true)] out FrameSourceInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure) =>
        TryAddFrameSource(source, FrameSourceAddOptions.Default, out info, out failure);

    /// <inheritdoc/>
    public bool TryAddFrameSource(
        IFrameSource source,
        FrameSourceAddOptions addOptions,
        [NotNullWhen(true)] out FrameSourceInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ThrowIfDisposed();

        if (_State.Phase != SessionPhase.Idle)
        {
            info = null;
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Frame sources can only be added while the session is Idle.");
            return false;
        }

        IFrameSource bound = _BindSource(source, addOptions);
        info = _AddFrameSourceInternal(bound);
        failure = null;
        return true;
    }

    /// <summary>
    /// Wraps stream sources (and RA sources when <see cref="FrameSourceAddOptions.CacheRandomAccess"/>)
    /// in <see cref="CachedFrameSource"/>. Does not wrap an existing cache.
    /// </summary>
    private static IFrameSource _BindSource(IFrameSource source, FrameSourceAddOptions addOptions)
    {
        if (source is CachedFrameSource)
        {
            return source;
        }

        bool isRandomAccess = source is IRandomAccessFrameSource;
        if (isRandomAccess && !addOptions.CacheRandomAccess)
        {
            return source;
        }

        return new CachedFrameSource(source, allowRandomAccessInner: isRandomAccess);
    }

    /// <summary>
    /// Cancels a user job and waits for it to reach a terminal state.
    /// The job remains in <see cref="_AllJobs"/> for diagnostic inspection.
    /// </summary>
    private bool _TryUnsubscribeUserJob(JobInfo job)
    {
        job.Cancel();
        job.Join();
        _NotifyAllListeners(NotifyFlags.JobStatusChanged);
        return true;
    }

    /// <summary>
    /// Finds the <see cref="ValueCacheSlot"/> whose job matches <paramref name="job"/>.
    /// </summary>
    private (ValueCacheSlot? Slot, ValueCacheInfo? Info) _FindValueCacheSlotAndInfo(JobInfo job)
    {
        foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
        {
            if (ReferenceEquals(slot.Info, job))
            {
                return (slot, slot.ValueCacheInfo);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Unsubscribes a value-cache listener. PullFill abandons the runtime writer and removes
    /// the slot. NotifyOnly ingest cancel does not abandon the ingest writer.
    /// </summary>
    private bool _TryUnsubscribeValueCache(ValueCacheSlot slot, ValueCacheInfo info)
    {
        info.SetStatus(SubscriptionStatus.Unsubscribed);
        slot.Cancel();
        if (slot.Status == JobStatus.Pending)
        {
            try
            {
                slot.Start();
            }
            catch
            {
                slot.EnsureOnUnsubscribed();
            }
        }

        slot.Join();
        slot.EnsureOnUnsubscribed();

        if (slot.FillMode == ValueCacheFillMode.PullFill)
        {
            slot.AbandonWriter();
            _ValueCacheSlots.Remove(slot);
            _ValueCacheInfos.Remove(info);
        }
        else
        {
            _ValueCacheSlots.Remove(slot);
            info.UnsubscribeCallback = null;
        }

        slot.Dispose();
        _NotifyAllListeners(NotifyFlags.JobStatusChanged);
        return true;
    }

    #endregion

    #region Lifecycle

    // -- ISession: Lifecycle --

    /// <inheritdoc/>
    public bool TryStart([NotNullWhen(false)] out SessionFailure? failure)
    {
        _ThrowIfDisposed();

        if (_State.Phase != SessionPhase.Idle)
        {
            failure = new SessionFailure(SessionErrorCode.InvalidPhase, "TryStart requires the Idle phase.");
            return false;
        }

        _StartInternal();
        failure = null;
        return true;
    }

    /// <inheritdoc/>
    public bool WaitForCompletion(TimeSpan? timeout = null)
    {
        Job[] jobs;
        lock (_LifecycleLock)
        {
            jobs = _SourceJobs;
        }

        if (timeout is null)
        {
            foreach (Job job in jobs)
            {
                job.Join();
            }

            return true;
        }

        TimeSpan budget = timeout.Value;
        long start = Stopwatch.GetTimestamp();
        foreach (Job job in jobs)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= budget)
            {
                return false;
            }

            if (!job.Join(budget - elapsed))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public void Restart(Func<FrameInterfaceRegistry, Stack> stackFactory)
    {
        ArgumentNullException.ThrowIfNull(stackFactory);
        _ThrowIfDisposed();

        lock (_LifecycleLock)
        {
            if (_ShutdownStarted != 0
                || _State.Phase is SessionPhase.ShuttingDown or SessionPhase.Idle)
            {
                throw new SessionException(
                    SessionErrorCode.InvalidPhase,
                    $"Restart() requires a Running or Stopped phase and no shutdown. Current phase: {_State.Phase}.");
            }

            if (_RestartInProgress != 0)
            {
                throw new InvalidOperationException("A restart is already in progress.");
            }

            _RestartInProgress = 1;
        }

        Stack? built = null;
        try
        {
            built = stackFactory(_FrameInterfaceRegistry)
                ?? throw new InvalidOperationException("The stack factory returned null.");
            if (!ReferenceEquals(built.FrameInterfaceRegistry, _FrameInterfaceRegistry))
            {
                throw new ArgumentException(
                    "The stack returned by the factory must use the FrameInterfaceRegistry that was passed to the factory. Do not create a new registry.",
                    nameof(stackFactory));
            }

            lock (_LifecycleLock)
            {
                if (_ShutdownStarted != 0)
                {
                    throw new SessionException(
                        SessionErrorCode.InvalidPhase,
                        "Restart was abandoned because shutdown has started.");
                }

                _RestartCore(built);
                built = null;
            }
        }
        finally
        {
            built?.Dispose();
            lock (_LifecycleLock)
            {
                _RestartInProgress = 0;
            }
        }
    }

    /// <summary>
    /// Core restart logic: builds a new stack, gates source threads, swaps the
    /// stack, re-parses all frames in original order, then resumes source threads.
    ///
    /// <para>
    /// <b>Concurrency model:</b>
    /// Source threads are blocked on <see cref="_ParseGate"/> (kernel-level wait,
    /// no CPU burn) after their current <see cref="IFrameSource.NextFrame"/> call
    /// returns. This ensures no frame is lost: the source keeps capturing while
    /// the thread simply waits before parsing. After the re-parse finishes and
    /// the gate opens, source threads resume with the new stack and continue
    /// allocating sequential PacketIds from where the re-parse left off.
    /// </para>
    ///
    /// <para>
    /// <b>Frame ordering guarantee:</b>
    /// All previously parsed frames are re-parsed in ascending PacketId order
    /// (0 … N-1) via <see cref="_PacketToFrame"/>. A mapping miss, a source
    /// that is not random-access, or a null <c>FrameById</c> throws
    /// <see cref="SessionException"/> with <see cref="SessionErrorCode.FrameUnavailable"/>.
    /// Queries stay disabled after that throw; listeners do not receive
    /// <see cref="NotifyFlags.NewPackets"/> for the partial rewrite.
    /// </para>
    /// </summary>
    private void _RestartCore(Stack newStack)
    {
        // Caller holds _LifecycleLock and has already validated the registry.
        // ── Phase 1: Gate source threads and swap the stack ──────────────────
        _State.SetPhase(SessionPhase.Restarting);
        _NotifyAllListeners(NotifyFlags.PhaseChanged);

        // Close the gate. Source threads that finish their current NextFrame()
        // call block at _ParseGate.Wait(ct) until we Set() below.
        _ParseGate.Reset();

        // Disable queries while frames are re-parsed onto the new stack.
        _QueriesDisabled = true;

        int totalToReparse;
        lock (_ParseMutex)
        {
            // Wait for any in-flight parse to finish — once we own the lock
            // no source thread is inside the parse section (they are either
            // still in NextFrame or blocked on the gate).
            totalToReparse = _NextPacketId;

            // Dispose the old stack if the session owns it (factory-created).
            Stack oldStack = _Stack;
            if (_OwnsStack)
            {
                oldStack.Dispose();
            }

            // Swap the protocol stack. Session owns factory-created stacks.
            _Stack = newStack;
            _OwnsStack = true;

            // Old parse results are discarded (the session does not retain packets).
            // Do NOT clear the mapping — it stores frame order needed for re-parse
            // and the PacketId → (FrameId, SourceId) data remains valid.

            // Create a fresh packet index for the new stack's field definitions.
            _PacketIndex = _TryCreatePacketIndex(_Stack);

            // Abandon previous writers, then rebuild ingest from the stored request.
            // Runtime PullFill slots stay registered (C18); they rebind on StackChanged.
            _IngestValueCache?.Abandon();
            foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
            {
                if (slot.FillMode == ValueCacheFillMode.PullFill)
                {
                    slot.AbandonWriter();
                }
            }

            _IngestValueCache = _TryCreateIngestValueCache(_Stack);
            if (_IngestValueCache is not null)
            {
                if (_IngestInfo is null)
                {
                    _IngestInfo = _CreateIngestInfo(_IngestValueCache);
                    _ValueCacheInfos.Add(_IngestInfo);
                }
                else
                {
                    _IngestInfo.SetWriter(_IngestValueCache);
                }

                foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
                {
                    if (slot.FillMode == ValueCacheFillMode.NotifyOnly)
                    {
                        slot.BindWriter(_IngestValueCache);
                    }
                }
            }

            // Reset counters so re-parse fills them from 0.
            Interlocked.Exchange(ref _PacketCount, 0);
            Interlocked.Exchange(ref _FrameCount, 0);
            Interlocked.Exchange(ref _NextPacketId, 0);
        }

        // ── Phase 2 + 3: Re-parse and resume ─────────────────────────────────
        // Wrapped in try/finally to guarantee the parse gate is reopened even
        // if _ReparseAllFrames throws (OOM, FrameUnavailable, etc.). Without this,
        // source threads would remain parked on the closed gate indefinitely.
        bool reparseCompleted = false;
        try
        {
            _ReparseAllFrames(totalToReparse);
            reparseCompleted = true;
        }
        finally
        {
            bool sourcesStillActive = _ActiveSourceCount > 0;
            SessionPhase finalPhase = sourcesStillActive
                ? SessionPhase.Running
                : SessionPhase.Stopped;
            _State.SetPhase(finalPhase);

            if (reparseCompleted)
            {
                // Re-bind every listener filter to the new stack while pull queries are still
                // disabled. A filter carries field ids resolved against the retired stack, so it must
                // be replaced before any listener can pull again.
                _DeriveListenerFilters();

                _QueriesDisabled = false;

                NotifyFlags flags = NotifyFlags.StackChanged | NotifyFlags.NewPackets | NotifyFlags.PhaseChanged;
                if (!sourcesStillActive)
                {
                    flags |= NotifyFlags.AllSourcesCompleted;
                }

                _ResetAllListenerCursors();
                _ResetAllValueCacheCursors();
                _NotifyAllListeners(flags);
            }
            else
            {
                // Fail closed: the old stack is already disposed and counters/mapping
                // are a partial rewrite. Do not re-enable queries or announce NewPackets.
                // Restore _NextPacketId so a still-running source cannot reuse 0 … N-1.
                Interlocked.Exchange(ref _NextPacketId, totalToReparse);

                NotifyFlags flags = NotifyFlags.PhaseChanged;
                if (!sourcesStillActive)
                {
                    flags |= NotifyFlags.AllSourcesCompleted;
                }

                _NotifyAllListeners(flags);
            }

            _ParseGate.Set();
        }
    }

    /// <summary>
    /// Re-parses frames for PacketIds 0 … <paramref name="count"/>-1 using the
    /// current <see cref="_Stack"/>. Reads frame data from random-access sources
    /// via <see cref="_PacketToFrame"/> (stream sources are wrapped at add time).
    /// A mapping miss, non-random-access source, or null <c>FrameById</c> throws
    /// <see cref="SessionException"/> with <see cref="SessionErrorCode.FrameUnavailable"/>.
    ///
    /// <para>
    /// Called while the <see cref="_ParseGate"/> is closed, so no source thread
    /// is parsing concurrently. Each frame is still parsed under the ingest lock
    /// during stack-swap reparse.
    /// </para>
    /// </summary>
    private void _ReparseAllFrames(int count)
    {
        // Snapshot the random-access sources once.
        Dictionary<FrameSourceId, IRandomAccessFrameSource> raSources = _RandomAccessSources;

        for (int i = 0; i < count; i++)
        {
            PacketId originalId = new(i);

            // Look up which frame and source this PacketId mapped to.
            if (!_TryGetPacketFrame(originalId, out FrameId frameId, out FrameSourceId sourceId))
            {
                throw new SessionException(
                    SessionErrorCode.FrameUnavailable,
                    FormattableString.Invariant(
                        $"Restart cannot re-parse PacketId {originalId.Value.ToString(CultureInfo.InvariantCulture)}: mapping slot is missing."));
            }

            if (!raSources.TryGetValue(sourceId, out IRandomAccessFrameSource? raSource))
            {
                throw new SessionException(
                    SessionErrorCode.FrameUnavailable,
                    FormattableString.Invariant(
                        $"Restart cannot re-parse PacketId {originalId.Value.ToString(CultureInfo.InvariantCulture)}: source is not random-access."));
            }

            Frame? frame = raSource.FrameById(frameId);
            if (frame is null)
            {
                throw new SessionException(
                    SessionErrorCode.FrameUnavailable,
                    FormattableString.Invariant(
                        $"Restart cannot re-parse PacketId {originalId.Value.ToString(CultureInfo.InvariantCulture)}: FrameById returned null."));
            }

            // Keep the original PacketId so mapping, index, and TryGetPacket stay aligned.
            _ = _ParseFrameUnderLock(frame.Value, packetId: originalId);

            // Update counters.
            Interlocked.Increment(ref _PacketCount);
            Interlocked.Increment(ref _FrameCount);
        }

        // Parses used original ids, so _AllocateNextPacketId never advanced. Resume new ingest at count.
        Interlocked.Exchange(ref _NextPacketId, count);
    }

    /// <summary>
    /// Re-binds every listener filter to the current <see cref="_Stack"/> after a stack swap.
    ///
    /// <para>
    /// <see cref="IFilter.TryDerive"/> produces a new instance from the same parsed expression with
    /// empty flank state, an empty match cache, and no poison — the old instance is left untouched
    /// and simply dropped. A filter that cannot be re-bound (for example because the new stack no
    /// longer defines a referenced field) is removed and the failure is recorded on the slot, so
    /// the next <see cref="PacketReadMode.Matching"/> pull reports the error instead of silently
    /// returning every packet.
    /// </para>
    /// </summary>
    private void _DeriveListenerFilters()
    {
        ReadOnlySpan<ListenerSlot> slots = _ListenerSlots.Current;
        foreach (ListenerSlot slot in slots)
        {
            lock (slot.FilterGate)
            {
                PacketFilter? filter = slot.Filter;
                if (filter is null)
                {
                    continue;
                }

                if (filter.TryDerive(_Stack, out PacketFilter? derived, out FilterError? failure))
                {
                    slot.SetFilter(derived);
                    continue;
                }

                slot.SetFilterFault(failure);
            }
        }
    }

    /// <summary>
    /// Resets the packet cursor of all active listener slots to 0.
    /// Called during a stack-swap reparse so that the subsequent
    /// <see cref="NotifyFlags.NewPackets"/> dispatch delivers all re-parsed
    /// packets from the beginning.
    /// </summary>
    private void _ResetAllListenerCursors()
    {
        ReadOnlySpan<ListenerSlot> slots = _ListenerSlots.Current;
        foreach (ListenerSlot slot in slots)
        {
            slot.ResetPacketCursor();
        }
    }

    /// <summary>
    /// Resets value-cache slot cursors to 0 so the subsequent NewPackets window
    /// covers the full re-parsed id range.
    /// </summary>
    private void _ResetAllValueCacheCursors()
    {
        ReadOnlySpan<ValueCacheSlot> slots = _ValueCacheSlots.Current;
        foreach (ValueCacheSlot slot in slots)
        {
            slot.ResetPacketCursor();
        }
    }

    /// <inheritdoc/>
    public void Shutdown(TimeSpan? timeout = null)
    {
        if (Interlocked.CompareExchange(ref _ShutdownStarted, 1, 0) != 0)
        {
            ThreadWaitHelper.WaitUntil(() => _ListenersTornDown != 0);
            return;
        }

        try
        {
            // Do not hold _LifecycleLock while waiting. Restart drops the flag only after it
            // releases the lock, so this wait cannot deadlock with the swap.
            ThreadWaitHelper.WaitUntil(() => _RestartInProgress == 0);
            lock (_LifecycleLock)
            {
                _ShutdownUnderLock(timeout);
            }
        }
        finally
        {
            _ListenersTornDown = 1;
        }
    }

    /// <summary>
    /// Tears the session down. Caller holds <see cref="_LifecycleLock"/> and has waited
    /// until no restart is in progress.
    /// </summary>
    private void _ShutdownUnderLock(TimeSpan? timeout)
    {
        Job[] sourceJobs = _SourceJobs;

        // Allow Shutdown() on Stopped phase so that listener slots are properly
        // cancelled and OnUnsubscribed is called. The session may transition to
        // Stopped automatically when all sources finish, but listener threads
        // still need explicit cancellation.
        bool alreadyStopped = _State.Phase == SessionPhase.Stopped;

        if (!alreadyStopped)
        {
            _State.SetPhase(SessionPhase.ShuttingDown);
            _NotifyAllListeners(NotifyFlags.PhaseChanged | NotifyFlags.ShuttingDown);
        }
        else
        {
            // Notify ShuttingDown to any still-running listener slots.
            _NotifyAllListeners(NotifyFlags.ShuttingDown);
        }

        // Step 1: Cancel all source jobs. Sources observe this via CancellationToken.
        foreach (Job job in sourceJobs)
        {
            job.Cancel();
        }

        // Step 2: Wait for source jobs to finish (up to timeout if specified).
        // If timeout expired, sources may still be finishing their current frame parse.
        // Continue with teardown regardless — the caller chose this timeout and can
        // inspect job states via GetJobs() to see which sources are still running.
        WaitForCompletion(timeout);

        // Listeners must still TryGetPacket (including lock-free redissect) while they
        // drain the last NewPackets window. Disable queries only after they exit.

        // Mark all active listeners as SessionEnded BEFORE cancelling them, so that
        // OnUnsubscribed (called in each slot's finally block) reads the correct
        // terminal status.
        foreach (ListenerInfo listenerInfo in _ListenerInfos.Current)
        {
            if (listenerInfo.Status == SubscriptionStatus.Active)
            {
                listenerInfo.SetStatus(SubscriptionStatus.SessionEnded);
            }
        }

        foreach (ValueCacheInfo valueCacheInfo in _ValueCacheInfos.Current)
        {
            if (valueCacheInfo.Status == SubscriptionStatus.Active)
            {
                valueCacheInfo.SetStatus(SubscriptionStatus.SessionEnded);
            }
        }

        // Step 4: Cancel and wait for all listener slots.
        ReadOnlySpan<ListenerSlot> listeners = _ListenerSlots.Current;
        foreach (ListenerSlot slot in listeners)
        {
            slot.Cancel();
        }
        foreach (ListenerSlot slot in listeners)
        {
            // If the slot is still Pending (never started — TryAddListener
            // TOCTOU between Add and Start), start it so RunLoop can observe
            // cancellation and exit cleanly. Job.Start uses a CAS guard
            // against double-start, so this is safe even if TryAddListener's
            // Start races with us.
            if (slot.Status == JobStatus.Pending)
            {
                try
                {
                    slot.Start();
                }
                catch
                {
                    slot.EnsureOnUnsubscribed();
                }
            }

            slot.Join();
            slot.EnsureOnUnsubscribed();
        }

        // Step 4b: Handle listener slots added during the narrow TOCTOU window
        // between TryAddListener's phase guard and the snapshot read above.
        // Since the phase is now ShuttingDown, no new additions can pass the
        // guard, so one extra pass is sufficient.
        ReadOnlySpan<ListenerSlot> allListeners = _ListenerSlots.Current;
        for (int i = listeners.Length; i < allListeners.Length; i++)
        {
            ListenerSlot lateSlot = allListeners[i];
            lateSlot.Cancel();
            if (lateSlot.Status == JobStatus.Pending)
            {
                try
                {
                    lateSlot.Start();
                }
                catch
                {
                    lateSlot.EnsureOnUnsubscribed();
                }
            }

            lateSlot.Join();
            lateSlot.EnsureOnUnsubscribed();
        }

        _TearDownValueCacheSlots();

        _IngestValueCache?.Abandon();
        foreach (ValueCacheSlot cacheSlot in _ValueCacheSlots.Current)
        {
            if (cacheSlot.FillMode == ValueCacheFillMode.PullFill)
            {
                cacheSlot.AbandonWriter();
            }
        }

        _QueriesDisabled = true;

        // Step 5: Transition to final state.
        _State.SetPhase(SessionPhase.Stopped);

        // Step 6: Dispose all jobs and listener slots.
        // Try-catch per item ensures one failed dispose does not prevent
        // cleanup of subsequent items. All exceptions are collected and
        // thrown as an AggregateException after all cleanup completes.
        List<Exception>? cleanupErrors = null;

        foreach (Job job in sourceJobs)
        {
            try
            {
                job.Dispose();
            }
            catch (Exception ex) { (cleanupErrors ??= []).Add(ex); }
        }
        foreach (ListenerSlot slot in allListeners)
        {
            try
            {
                slot.Dispose();
            }
            catch (Exception ex) { (cleanupErrors ??= []).Add(ex); }
        }

        foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
        {
            try
            {
                slot.Dispose();
            }
            catch (Exception ex) { (cleanupErrors ??= []).Add(ex); }
        }

        // Step 7: Dispose all frame sources. The session owns every source added
        // via TryAddFrameSource. Source disposal is deferred entirely to this point
        // so that sources remain available for random access (FrameById) and reparse
        // after their read loop finishes or after being stopped via TryUnsubscribe.
        foreach (FrameSourceEntry entry in _SourceEntries.Current)
        {
            try
            {
                entry.Source.Dispose();
            }
            catch (Exception ex)
            {
                (cleanupErrors ??= []).Add(ex);
            }
        }

        // Dispose the current stack if the session owns it (factory-created via Restart).
        if (_OwnsStack)
        {
            try
            {
                _Stack.Dispose();
                _OwnsStack = false;
            }
            catch (Exception ex)
            {
                (cleanupErrors ??= []).Add(ex);
            }
        }

        // Dispose the parse gate. Safe because all source threads have finished
        // (they were cancelled and waited for above) and no new Wait() calls
        // can occur after this point.
        try
        {
            _ParseGate.Dispose();
        }
        catch (Exception ex)
        {
            (cleanupErrors ??= []).Add(ex);
        }

        // Surface all cleanup failures as a single AggregateException.
        // This ensures no disposal error is silently swallowed.
        if (cleanupErrors is not null)
        {
            throw new AggregateException(
                "One or more errors occurred during session shutdown cleanup.",
                cleanupErrors);
        }
    }

    // -- IDisposable --

    /// <inheritdoc/>
    public void Dispose()
    {
        // Volatile field: single check is sufficient — Shutdown() has its own CAS guard
        // that handles true concurrent Dispose() calls safely.
        if (_Disposed)
        {
            return;
        }
        _Disposed = true;

        // Graceful shutdown with no timeout — wait indefinitely for completion.
        // Dispose must not throw (standard .NET pattern). Shutdown() may throw an
        // AggregateException if cleanup failures occur — capture it so callers
        // can inspect ShutdownErrors after Dispose() returns.
        try
        {
            Shutdown();
        }
        catch (AggregateException ex)
        {
    // Publish shutdown errors with a release fence so post-Dispose readers observe them.
            _ShutdownErrors = ex;
        }
    }

    /// <summary>
    /// Returns cleanup exceptions that occurred during <see cref="Dispose"/>.
    /// <see langword="null" /> if no errors occurred or <see cref="Dispose"/> has not been called.
    /// When <see cref="Shutdown"/> is called directly (not via Dispose), cleanup failures
    /// are thrown as an <see cref="AggregateException"/> instead.
    /// </summary>
    public AggregateException? ShutdownErrors => _ShutdownErrors;

    #endregion

    #region Private helpers

    // -- Internal helpers: Source registration --

    /// <summary>
    /// Registers a new frame source in the registry and creates its job.
    /// Used by <see cref="TryAddFrameSource(IFrameSource, out FrameSourceInfo?, out SessionFailure?)"/> for initial source registration.
    /// </summary>
    private FrameSourceInfo _AddFrameSourceInternal(IFrameSource source)
    {
        FrameSourceId sourceId = _Stack.FrameInterfaceRegistry.RegisterSource(source);
        FrameSourceInfo info = _Stack.FrameInterfaceRegistry.GetSource(sourceId)!;

        // Build the job delegate (captured variables are stack-local copies).
        FrameSourceInfo capturedInfo = info;
        Job job = new(
            _State.AllocateJobId(),
            source.UiName,
            $"Source: {source.UiName}",
            ct => _RunSourceLoop(source, capturedInfo, ct),
            _OnJobStatusChanged);

        // Hold the entry reference so we can also register its JobInfo in the
        // unified job list without creating a second JobInfo wrapper.
        FrameSourceEntry entry = new(info, source, job);
        _SourceEntries.Add(entry);
        _SourceInfos.Add(info);
        _AllJobs.Add(entry.JobInfo);

        // Wire the convenience API: FrameSourceInfo.Stop() → TryUnsubscribe(job).
        // Captured reference is the entry's JobInfo (same reference stored in _AllJobs).
        JobInfo entryJobInfo = entry.JobInfo;
        info.RegisterStopCallback(() => TryUnsubscribe(entryJobInfo, out _));

        // Register random-access capable sources for GetPacket().
        // Copy-on-write: create a new dictionary with the added entry and publish atomically.
        // _AddFrameSourceInternal is only called during Idle or Restart (single-threaded),
        // so no CAS retry loop is needed.
        if (source is IRandomAccessFrameSource raSource)
        {
            Dictionary<FrameSourceId, IRandomAccessFrameSource> next = new(_RandomAccessSources)
            {
                [sourceId] = raSource,
            };
            _RandomAccessSources = next;
        }

        // Notify existing listeners about the new source.
        _NotifyAllListeners(NotifyFlags.SourceAdded);

        return info;
    }

    /// <summary>
    /// Transitions to Running and launches all source and listener jobs.
    /// Called by <see cref="TryStart"/> during initial start.
    /// </summary>
    private void _StartInternal()
    {
        Job[] jobs;
        lock (_LifecycleLock)
        {
            _PacketIndex = _TryCreatePacketIndex(_Stack);
            _QueriesDisabled = false;

            ReadOnlySpan<FrameSourceEntry> entries = _SourceEntries.Current;
            jobs = new Job[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                jobs[i] = entries[i].Job;
            }

            _SourceJobs = jobs;
            Interlocked.Exchange(ref _ActiveSourceCount, entries.Length);

            if (entries.Length == 0)
            {
                _StartListenerSlots();
                _State.SetPhase(SessionPhase.Stopped);
                _NotifyAllListeners(NotifyFlags.AllSourcesCompleted | NotifyFlags.PhaseChanged);
                return;
            }

            _State.SetPhase(SessionPhase.Running);
            _NotifyAllListeners(NotifyFlags.PhaseChanged);
            _StartListenerSlots();
        }

        for (int i = 0; i < jobs.Length; i++)
        {
            try
            {
                jobs[i].Start();
            }
            catch
            {
                int remaining = Interlocked.Decrement(ref _ActiveSourceCount);
                if (remaining == 0)
                {
                    _NotifyAllListeners(NotifyFlags.AllSourcesCompleted);
                    _State.SetPhase(SessionPhase.Stopped);
                    _NotifyAllListeners(NotifyFlags.PhaseChanged);
                }
            }
        }
    }

    /// <summary>Starts all pending listener slots (shared by normal start and zero-source start).</summary>
    private void _StartListenerSlots()
    {
        // Start all listener slots first so they are ready to receive flags.
        // Each Start is individually guarded: a failed listener start does
        // not prevent other listeners or source jobs from starting.
        ReadOnlySpan<ListenerSlot> listeners = _ListenerSlots.Current;
        foreach (ListenerSlot slot in listeners)
        {
            // Only start if not already running (listeners survive restart).
            if (slot.Status is JobStatus.Pending)
            {
                try
                {
                    slot.Start();
                }
                catch
                {
                    // Listener's Job.Start() already transitioned to Failed.
                    // The slot will not receive notifications but other
                    // listeners and sources continue unaffected.
                }
            }
        }

        _StartValueCacheSlots();
    }

    /// <summary>
    /// Starts pending value-cache slots (ingest NotifyOnly and runtime PullFill).
    /// </summary>
    private void _StartValueCacheSlots()
    {
        ReadOnlySpan<ValueCacheSlot> slots = _ValueCacheSlots.Current;
        foreach (ValueCacheSlot slot in slots)
        {
            if (slot.Status is JobStatus.Pending)
            {
                try
                {
                    slot.Start();
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="NetworkInspector.Core.Index.PacketIndex"/> for the given stack.
    /// </summary>
    private static NetworkInspector.Core.Index.PacketIndex _CreatePacketIndex(Stack stack) =>
        new(stack);

    /// <summary>
    /// Creates a packet index when <see cref="IndexPackets"/> is enabled; otherwise null.
    /// </summary>
    private PacketIndex? _TryCreatePacketIndex(Stack stack)
    {
        if (!IndexPackets)
        {
            return null;
        }

        return _CreatePacketIndex(stack);
    }

    // -- Source job loop --

    /// <summary>
    /// The work delegate executed by each source's <see cref="Job"/> thread.
    ///
    /// <para>
    /// <b>Loop structure (pull-based):</b>
    /// <list type="number">
    ///   <item>Start the source and register its capture interface(s).</item>
    ///   <item>Pull frames one by one via <see cref="IFrameSource.NextFrame"/>.</item>
    ///   <item>Parse each frame under the shared <see cref="_ParseMutex"/>.</item>
    ///   <item>Record the PacketId -> FrameId mapping.</item>
    ///   <item>Recycle the ingest packet on this source thread.</item>
    ///   <item>Increment global counters and set <see cref="NotifyFlags.NewPackets"/>.</item>
    ///   <item>On source exhaustion: set SourceCompleted and (if last) AllSourcesCompleted flags.</item>
    /// </list>
    /// No batch buffer. No flush timer. No per-listener array copy.
    /// The flags coalesce naturally -- a fast listener sees small batches,
    /// a slow listener sees all accumulated packets in one read.
    /// </para>
    /// </summary>
    private void _RunSourceLoop(
        IFrameSource source,
        FrameSourceInfo sourceInfo,
        CancellationToken ct)
    {
        try
        {
            source.Start(sourceInfo.Id, _Stack.FrameInterfaceRegistry);

            // Ingest skip-parses and does not retain the packet; recycle it on this source thread.
            Packet? ingestRecycle = null;

            while (!ct.IsCancellationRequested)
            {
                Frame? frame = source.NextFrame(ct);
                if (frame is null)
                {
                    break;
                }

                Frame capturedFrame = frame.Value;

                // If a reparse is in progress the gate is closed. Source threads
                // park here (kernel wait — no CPU burn) until the reparse finishes.
                // CancellationToken ensures Shutdown can still interrupt.
                // Throws OperationCanceledException on cancellation → caught by Job.RunCore
                // which recognises it via token comparison and transitions to Cancelled.
                _ParseGate.Wait(ct);

                // Allocate, parse, and publish the map under one lock so a second source
                // cannot observe the next id before this frame is stored.
                Packet packet = _IngestFrameUnderLock(capturedFrame, sourceInfo.Id, ingestRecycle);
                ingestRecycle = packet;
                _NotifyAllListeners(NotifyFlags.NewPackets);
            }

            // Source completed. Set SourceCompleted flag on all listeners.
            _NotifyAllListeners(NotifyFlags.SourceCompleted);
        }
        finally
        {
            // Critical: always decrement active source counter, even on failure.
            // Source disposal is deferred to Shutdown() so sources remain available
            // for random access (IRandomAccessFrameSource.FrameById) and reparse
            // after their read loop finishes.
            int remaining = Interlocked.Decrement(ref _ActiveSourceCount);

            if (remaining == 0)
            {
                // AllSourcesCompleted: this is the last source thread.
                _NotifyAllListeners(NotifyFlags.AllSourcesCompleted);

                // Transition to Stopped if still Running (Shutdown may have
                // already changed the phase).
                if (_State.Phase == SessionPhase.Running)
                {
                    _State.SetPhase(SessionPhase.Stopped);
                    _NotifyAllListeners(NotifyFlags.PhaseChanged);
                }
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="ListenerSlot"/> and registers it in session registries.
    /// Ownership transfers to <see cref="_ListenerSlots"/> before return so disposal is session-managed.
    /// </summary>
    private ListenerSlot _RegisterListenerSlot(JobId jobId, ISessionListener listener)
    {
        ListenerSlot slot = new(jobId, listener, this, _OnJobStatusChanged);
        _ListenerSlots.Add(slot);
        _AllJobs.Add(slot.Info);
        return slot;
    }

    /// <summary>
    /// Creates a <see cref="ValueCacheSlot"/>, public info, and registers both.
    /// </summary>
    private ValueCacheSlot _RegisterValueCacheSlot(
        IValueCacheListener listener,
        ValueCacheRequest request,
        ValueCache writer,
        ValueCacheFillMode fillMode)
    {
        ValueCacheId cacheId = _State.AllocateValueCacheId();
        JobId jobId = _State.AllocateJobId();
        ValueCacheSlot slot = new(
            jobId,
            listener,
            this,
            request,
            writer,
            fillMode,
            stack => _BuildValueCache(stack, request),
            () => _IngestValueCache,
            _OnJobStatusChanged);

        ValueCacheInfo info = new()
        {
            Id = cacheId,
            UiName = listener.UiName,
            Job = slot.Info,
        };
        info.SetWriter(writer);
        info.UnsubscribeCallback = () =>
        {
            _ = TryUnsubscribe(slot.Info, out _);
        };
        slot.ValueCacheInfo = info;

        _ValueCacheSlots.Add(slot);
        if (fillMode == ValueCacheFillMode.NotifyOnly && _IngestInfo is not null)
        {
            _IngestInfo.UnsubscribeCallback = info.UnsubscribeCallback;
            _IngestInfo.Job = slot.Info;
            _IngestInfo.SetStatus(SubscriptionStatus.Active);
            slot.ValueCacheInfo = _IngestInfo;
        }
        else
        {
            _ValueCacheInfos.Add(info);
        }

        _AllJobs.Add(slot.Info);
        return slot;
    }

    /// <summary>
    /// Resolves a <see cref="ValueCacheRequest"/> against <paramref name="stack"/> and constructs
    /// the writer. Names that fail <see cref="NameValidation.IsValidName"/> throw
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>. Unknown names throw
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/>.
    /// </summary>
    private static ValueCache _BuildValueCache(Stack stack, ValueCacheRequest request)
    {
        Dictionary<string, ValueCacheFieldConfig> byName = new(StringComparer.Ordinal);
        foreach (string name in request.FieldNames)
        {
            FieldId fieldId = _RequireFieldId(stack, name);
            byName[name] = new ValueCacheFieldConfig(fieldId, request.DefaultCaptureMode);
        }

        foreach (ValueCacheFieldRequest field in request.Fields)
        {
            FieldId fieldId = _RequireFieldId(stack, field.FieldName);
            byName[field.FieldName] = new ValueCacheFieldConfig(
                fieldId,
                field.CaptureMode,
                field.RecordValue,
                field.RecordCustomText,
                field.RecordCustomRepresentation);
        }

        List<ValueCacheGroupConfig> groups = [];
        foreach (string groupName in request.GroupNames)
        {
            IndexGroupId groupId = _RequireGroupId(stack, groupName);
            groups.Add(new ValueCacheGroupConfig(groupId, request.DefaultCaptureMode));
        }

        ValueCacheFieldConfig[] fields = [.. byName.Values];
        ValueCacheGroupConfig[] groupConfigs = [.. groups];
        try
        {
            return new ValueCache(
                stack,
                fields,
                groupConfigs,
                new ValueCacheBuildOptions
                {
                    RecordAllFields = request.RecordAllFields,
                    DefaultCaptureMode = request.DefaultCaptureMode,
                    RecordContainerPresence = request.RecordContainerPresence,
                    ChunkShift = request.ChunkShift,
                });
        }
        catch (ArgumentException ex)
        {
            throw new SessionException(
                SessionErrorCode.ValueCacheUnknownField,
                ex.Message);
        }
    }

    private static FieldId _RequireFieldId(Stack stack, string name)
    {
        _RequireValidCacheName(name, "field");

        FieldId? id = stack.GetFieldId(name);
        if (id is null)
        {
            throw new SessionException(
                SessionErrorCode.ValueCacheUnknownField,
                string.Format(CultureInfo.InvariantCulture, "Unknown value-cache field '{0}'.", name));
        }

        return id.Value;
    }

    private static IndexGroupId _RequireGroupId(Stack stack, string name)
    {
        _RequireValidCacheName(name, "group");

        IndexGroupId? id = stack.GetIndexGroupId(name);
        if (id is null)
        {
            throw new SessionException(
                SessionErrorCode.ValueCacheUnknownField,
                string.Format(CultureInfo.InvariantCulture, "Unknown value-cache group '{0}'.", name));
        }

        return id.Value;
    }

    /// <summary>
    /// Same identifier rule as <see cref="StackBuilder"/> field registration:
    /// <see cref="NameValidation.IsValidName"/>.
    /// </summary>
    private static void _RequireValidCacheName(string name, string kind)
    {
        if (name is not null && NameValidation.IsValidName(name))
        {
            return;
        }

        string display = name ?? "null";
        throw new SessionException(
            SessionErrorCode.ValueCacheInvalidFieldName,
            string.Format(
                CultureInfo.InvariantCulture,
                "Invalid value-cache {0} name '{1}'. Names must be dot-separated C-style identifiers (e.g. \"ip.src\").",
                kind,
                display));
    }

    private ValueCache? _TryCreateIngestValueCache(Stack stack)
    {
        if (_IngestRequest is null)
        {
            return null;
        }

        return _BuildValueCache(stack, _IngestRequest);
    }

    private ValueCacheInfo _CreateIngestInfo(ValueCache writer)
    {
        ValueCacheInfo info = new()
        {
            Id = _State.AllocateValueCacheId(),
            UiName = _IngestListener?.UiName ?? "ingest",
            Job = null,
        };
        info.SetWriter(writer);
        return info;
    }

    /// <summary>
    /// Ctor-time ingest cache and optional NotifyOnly slot. Throws when a listener is set without a request.
    /// </summary>
    private void _InitializeValueCaches()
    {
        if (_IngestListener is not null && _IngestRequest is null)
        {
            throw new SessionException(
                SessionErrorCode.ValueCacheListenerWithoutRequest,
                "SessionOptions.ValueCacheListener requires SessionOptions.ValueCache.");
        }

        _IngestValueCache = _TryCreateIngestValueCache(_Stack);
        if (_IngestValueCache is null)
        {
            return;
        }

        _IngestInfo = _CreateIngestInfo(_IngestValueCache);
        _ValueCacheInfos.Add(_IngestInfo);
        if (_IngestListener is not null)
        {
            _ = _RegisterValueCacheSlot(
                _IngestListener,
                _IngestRequest!,
                _IngestValueCache,
                ValueCacheFillMode.NotifyOnly);
        }
    }

    /// <summary>
    /// Cancels and joins value-cache slots, including any added in the shutdown TOCTOU window.
    /// </summary>
    private void _TearDownValueCacheSlots()
    {
        ReadOnlySpan<ValueCacheSlot> slots = _ValueCacheSlots.Current;
        foreach (ValueCacheSlot slot in slots)
        {
            slot.Cancel();
        }

        foreach (ValueCacheSlot slot in slots)
        {
            if (slot.Status == JobStatus.Pending)
            {
                try
                {
                    slot.Start();
                }
                catch
                {
                    slot.EnsureOnUnsubscribed();
                }
            }

            slot.Join();
            slot.EnsureOnUnsubscribed();
        }

        ReadOnlySpan<ValueCacheSlot> allSlots = _ValueCacheSlots.Current;
        for (int i = slots.Length; i < allSlots.Length; i++)
        {
            ValueCacheSlot lateSlot = allSlots[i];
            lateSlot.Cancel();
            if (lateSlot.Status == JobStatus.Pending)
            {
                try
                {
                    lateSlot.Start();
                }
                catch
                {
                    lateSlot.EnsureOnUnsubscribed();
                }
            }

            lateSlot.Join();
            lateSlot.EnsureOnUnsubscribed();
        }
    }

    // -- Parse helper --

    /// <summary>
    /// Fail-closed path when a freshly allocated packet id cannot be recorded in the map.
    /// Extracted so ExitPointGaps can exercise the throw without corrupting a live source loop.
    /// </summary>
    internal static void ThrowMappingRecordFailed(PacketId packetId)
    {
        throw new InvalidOperationException(
            $"Failed to record mapping for PacketId {packetId.Value.ToString(CultureInfo.InvariantCulture)}. " +
            "The packet ID is invalid.");
    }

    /// <summary>
    /// Appends the next dense <paramref name="packetId"/> → (<paramref name="frameId"/>, <paramref name="sourceId"/>) slot.
    /// <paramref name="packetId"/> must equal the current store count.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="packetId"/> is invalid, or not the next sequential index.
    /// </exception>
    internal void RecordPacketFrame(PacketId packetId, FrameId frameId, FrameSourceId sourceId)
    {
        if (!packetId.IsValid)
        {
            ThrowMappingRecordFailed(packetId);
        }

        if (packetId.Value != _PacketToFrame.Count)
        {
            throw new InvalidOperationException("Packet-to-frame mapping requires dense sequential PacketId appends.");
        }

        _PacketToFrame.Append(PackFrameMapping(frameId, sourceId));
    }

    /// <summary>
    /// Packs <paramref name="frameId"/> in bits 63..32 and <paramref name="sourceId"/> in bits 31..0.
    /// Casts through <see cref="uint"/> so <see cref="FrameId.Invalid"/> (-1) does not sign-extend.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackFrameMapping(FrameId frameId, FrameSourceId sourceId) =>
        (long)(uint)frameId.Value << 32 | (uint)sourceId.Value;

    /// <summary>Unpacks a value from <see cref="PackFrameMapping"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void UnpackFrameMapping(long packed, out FrameId frameId, out FrameSourceId sourceId)
    {
        frameId = new FrameId((int)(packed >> 32));
        sourceId = new FrameSourceId((int)(packed & 0xFFFF_FFFFL));
    }

    /// <summary>
    /// Looks up the frame that produced <paramref name="packetId"/>.
    /// Returns <see langword="false"/> when the packet has not been recorded.
    /// Caller must pass a valid id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetPacketFrame(PacketId packetId, out FrameId frameId, out FrameSourceId sourceId)
    {
        if (!_PacketToFrame.TryGet(packetId.Value, out long packed))
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        UnpackFrameMapping(packed, out frameId, out sourceId);
        return true;
    }

    /// <summary>
    /// Allocates the next sequential <see cref="PacketId"/> under <see cref="_ParseMutex"/>.
    /// </summary>
    /// <exception cref="SessionException">
    /// Thrown when the next ID would exceed <see cref="Core.Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    private PacketId _AllocateNextPacketId()
    {
        int next = Interlocked.Increment(ref _NextPacketId);
        int idValue = next - 1;
        if (!Core.Ids.ArrayIndexIdRange.IsValidIndex(idValue))
        {
            throw new SessionException(
                SessionErrorCode.PacketIdExhausted,
                $"Maximum packet ID count exceeded. Valid packet IDs are 0..{Core.Ids.ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)} " +
                $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");
        }

        return new PacketId(idValue);
    }

    /// <summary>
    /// Parses a frame, using the packet index when the session was configured with one.
    /// Called only from <see cref="_ParseFrameUnderLock"/>, i.e. from the ordered first-parse path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Packet _ParseFrameCore(PacketId id, Frame frame)
    {
        ParseOptions options = new(_IngestFieldTreeMode, _IngestValueCache, index: _PacketIndex);
        if (!Packet.TryParse(id, _Stack, frame, in options, out Packet? packet, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet!;
    }

    /// <summary>
    /// First parse of a frame under <see cref="_ParseMutex"/>. The lock plus the monotonic ids from
    /// <see cref="_AllocateNextPacketId"/> establish exactly the contract stateful protocols rely on:
    /// every packet id is parsed for the first time exactly once, ordered and single-threaded. That
    /// is what lets those protocols record their replay state here and serve lock-free re-parses
    /// afterwards, without the session having to pass any parse mode.
    /// </summary>
    private Packet _ParseFrameUnderLock(Frame frame, PacketId? packetId, Packet? recycle = null)
    {
        lock (_ParseMutex)
        {
            PacketId id = packetId ?? _AllocateNextPacketId();
            return _ParseAllocatedFrame(id, frame, recycle);
        }
    }

    /// <summary>
    /// First parse of one newly read frame. Holds <see cref="_ParseMutex"/> from id allocation
    /// through the map append and the packet-count publish. Restart must not call this.
    /// </summary>
    private Packet _IngestFrameUnderLock(Frame frame, FrameSourceId sourceId, Packet? recycle)
    {
        lock (_ParseMutex)
        {
            int nextBefore = _NextPacketId;
            try
            {
                PacketId id = _AllocateNextPacketId();
                Packet packet = _ParseAllocatedFrame(id, frame, recycle);
                RecordPacketFrame(id, frame.Id, sourceId);
                Interlocked.Increment(ref _PacketCount);
                Interlocked.Increment(ref _FrameCount);
                return packet;
            }
            catch
            {
                // No other source can have allocated inside this lock. Putting the counter
                // back keeps the next id equal to the map count.
                _NextPacketId = nextBefore;
                throw;
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="id"/> with the ingest field-tree mode. Caller holds <see cref="_ParseMutex"/>.
    /// </summary>
    private Packet _ParseAllocatedFrame(PacketId id, Frame frame, Packet? recycle)
    {
        if (recycle is not null)
        {
            ParseOptions options = new(_IngestFieldTreeMode, _IngestValueCache, index: _PacketIndex);
            if (Packet.TryParse(recycle, id, _Stack, frame, in options, out _))
            {
                return recycle;
            }
        }

        return _ParseFrameCore(id, frame);
    }

    /// <summary>
    /// Re-parses an already announced packet id. Runs lock-free on any thread: because the id was
    /// announced, its first parse completed, so stateful protocols detect the re-parse themselves and
    /// replay their recorded state instead of mutating it. Passing an id that was never announced
    /// would be a first parse on an arbitrary thread and is therefore not allowed.
    /// <para>
    /// A non-<see langword="null"/> <paramref name="recycle"/> is reused in place. A rejected recycle
    /// is not an error for the caller: the re-parse repeats into a fresh packet.
    /// </para>
    /// <para>
    /// Truncated or malformed frames become error packets. Unexpected failures
    /// (for example <see cref="OutOfMemoryException"/>) propagate; they are not
    /// converted to <see langword="false"/>.
    /// </para>
    /// </summary>
    private bool _TryReparseFrame(
        Frame frame, PacketId packetId, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        ParseOptions options = new(FieldTreeMode.Build, index: _PacketIndex);
        if (recycle is not null && Packet.TryParse(recycle, packetId, _Stack, frame, in options, out _))
        {
            packet = recycle;
            return true;
        }

        if (!Packet.TryParse(packetId, _Stack, frame, in options, out Packet? created, out ParseFailure failure) || created is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        packet = created;
        return true;
    }

    // -- Flag delivery helpers --

    /// <summary>
    /// Sets the given flag(s) on all active listener slots.
    /// Non-blocking, lock-free. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// <see cref="NotifyFlags.NewPackets"/> is set once per parsed frame
    /// (O(frames × listeners) atomic ORs). Listener slots coalesce duplicate
    /// flags between wake cycles; each frame still issues one OR per listener.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _NotifyAllListeners(NotifyFlags flags)
    {
        ReadOnlySpan<ListenerSlot> slots = _ListenerSlots.Current;
        foreach (ListenerSlot slot in slots)
        {
            slot.Notify(flags);
        }

        ReadOnlySpan<ValueCacheSlot> valueCacheSlots = _ValueCacheSlots.Current;
        foreach (ValueCacheSlot slot in valueCacheSlots)
        {
            slot.Notify(flags);
        }
    }

    // -- Job status callback --

    /// <summary>
    /// Called by each <see cref="Job"/> when its status changes.
    /// Sets <see cref="NotifyFlags.JobStatusChanged"/> on all listener slots.
    /// </summary>
    private void _OnJobStatusChanged(Job job, JobStatus status)
        => _NotifyAllListeners(NotifyFlags.JobStatusChanged);

    #endregion

    #region Listeners and jobs

    // -- ISessionReader: Source info --

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<FrameSourceInfo> GetFrameSources() => _SourceInfos.CurrentSnapshot;

    // -- ISession: Listener management --

    /// <inheritdoc/>
    public bool TryAddListener(
        ISessionListener listener,
        [NotNullWhen(true)] out ListenerInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure) =>
        TryAddListener(listener, filter: null, out info, out failure);

    /// <inheritdoc/>
    public bool TryAddListener(
        ISessionListener listener,
        string? filterExpression,
        [NotNullWhen(true)] out ListenerInfo? info,
        out FilterError? filterFailure,
        out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _ThrowIfDisposed();
        info = null;
        filterFailure = null;
        failure = null;

        if (!_TryAcceptSubscription(out failure))
        {
            return false;
        }

        // Compile before allocating any listener state so a bad expression leaves the session
        // exactly as it was.
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(filterExpression ?? string.Empty, _Stack);
        if (!compiled.TryGetValue(out PacketFilter? filter))
        {
            filterFailure = compiled.Error;
            return false;
        }

        return TryAddListener(listener, filter, out info, out failure);
    }

    /// <inheritdoc/>
    public bool TryAddListener(
        ISessionListener listener,
        IFilter? filter,
        [NotNullWhen(true)] out ListenerInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _ThrowIfDisposed();
        info = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(listener.UiName))
        {
            throw new SessionException(
                SessionErrorCode.ListenerUiNameEmpty,
                "Listener UiName cannot be null or whitespace.");
        }

        if (!_TryAcceptSubscription(out failure))
        {
            return false;
        }

        // Reject a foreign filter before any id or slot is stored.
        if (filter is not null and not PacketFilter)
        {
            throw new ArgumentException(
                "ListenerSlot only accepts NetworkInspector.Filter.Filter instances.",
                nameof(filter));
        }

        ListenerId listenerId = _State.AllocateListenerId();
        JobId jobId = _State.AllocateJobId();

        ListenerSlot slot = _RegisterListenerSlot(jobId, listener);
        slot.SetFilter(filter);

        info = new ListenerInfo()
        {
            Id = listenerId,
            UiName = listener.UiName,
        };

        // Link the slot to its public view for TryUnsubscribe correlation.
        slot.ListenerInfo = info;

        // Wire the convenience API: ListenerInfo.Unsubscribe() → TryUnsubscribe(job).
        // Captured reference is the slot's JobInfo (same reference stored in _AllJobs).
        JobInfo slotJobInfo = slot.Info;
        info.UnsubscribeCallback = () =>
        {
            _ = TryUnsubscribe(slotJobInfo, out _);
        };

        // Track the public view for GetListeners().
        _ListenerInfos.Add(info);

        // Start the slot in any non-Idle phase. During Idle, _StartInternal()
        // will start all pending slots. In all active phases (Running,
        // Restarting), immediate start ensures the slot's thread is ready for
        // notifications. Starting during the narrow TOCTOU window where the
        // phase transitions to ShuttingDown is safe — the slot will observe
        // cancellation and exit cleanly. Un-started (Pending) slots would
        // deadlock shutdown's wait loop because Cancel() alone does not
        // transition a never-started Job out of Pending.
        if (_State.Phase != SessionPhase.Idle)
        {
            slot.Start();
        }

        _BackfillListener(slot);
        failure = null;
        return true;
    }

    /// <summary>
    /// Shared phase and query gate for listeners and value caches.
    /// <see cref="SessionPhase.Stopped"/> is allowed so a finished capture can still gain a view.
    /// </summary>
    private bool _TryAcceptSubscription([NotNullWhen(false)] out SessionFailure? failure)
    {
        if (_State.Phase == SessionPhase.ShuttingDown)
        {
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Subscriptions cannot be added while the session is shutting down.");
            return false;
        }

        if (_QueriesDisabled)
        {
            failure = new SessionFailure(SessionErrorCode.QueriesDisabled, "Packet queries are disabled.");
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Wakes one new listener for packets that already exist. Idle has none yet.
    /// </summary>
    private void _BackfillListener(ListenerSlot slot)
    {
        if (_State.Phase == SessionPhase.Idle || _QueriesDisabled)
        {
            return;
        }

        NotifyFlags flags = NotifyFlags.NewPackets;
        if (_State.Phase == SessionPhase.Stopped)
        {
            flags |= NotifyFlags.AllSourcesCompleted;
        }

        slot.Notify(flags);
    }

    /// <inheritdoc/>
    public bool TryAddValueCache(
        IValueCacheListener listener,
        ValueCacheRequest request,
        [NotNullWhen(true)] out ValueCacheInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(request);
        _ThrowIfDisposed();
        info = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(listener.UiName))
        {
            throw new SessionException(
                SessionErrorCode.ValueCacheUiNameEmpty,
                "Value-cache listener UiName cannot be null or whitespace.");
        }

        if (!_TryAcceptSubscription(out failure))
        {
            return false;
        }

        ValueCache writer = _BuildValueCache(_Stack, request);
        ValueCacheSlot slot = _RegisterValueCacheSlot(
            listener,
            request,
            writer,
            ValueCacheFillMode.PullFill);

        info = slot.ValueCacheInfo!;
        if (_State.Phase != SessionPhase.Idle)
        {
            slot.Start();
        }

        if (_State.Phase != SessionPhase.Idle && !_QueriesDisabled)
        {
            NotifyFlags flags = NotifyFlags.NewPackets;
            if (_State.Phase == SessionPhase.Stopped)
            {
                flags |= NotifyFlags.AllSourcesCompleted;
            }

            slot.Notify(flags);
        }

        failure = null;
        return true;
    }

    // -- ISessionReader: Listener info --

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<ListenerInfo> GetListeners() => _ListenerInfos.CurrentSnapshot;

    #endregion

    #region Packet reads

    // -- ISessionReader: Packet access --

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacket(
        PacketId id,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure) =>
        TryGetPacket(id, recycle: null, out packet, out failure);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacket(
        PacketId id,
        Packet? recycle,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        if (_QueriesDisabled)
        {
            packet = null;
            failure = new SessionFailure(SessionErrorCode.QueriesDisabled, "Packet queries are disabled.");
            return false;
        }

        if (!id.IsValid)
        {
            packet = null;
            failure = new SessionFailure(SessionErrorCode.PacketNotFound, "Packet id is not valid.");
            return false;
        }

        if (!_TryGetPacketFrame(id, out FrameId frameId, out FrameSourceId sourceId))
        {
            packet = null;
            failure = new SessionFailure(
                SessionErrorCode.PacketNotFound,
                string.Format(CultureInfo.InvariantCulture, "Packet id {0} is not in the announced range.", id.Value));
            return false;
        }

        _RandomAccessSources.TryGetValue(sourceId, out IRandomAccessFrameSource? raSource);
        if (raSource is null)
        {
            packet = null;
            failure = new SessionFailure(SessionErrorCode.FrameUnavailable, "The frame source for this packet is not available.");
            return false;
        }

        Frame? raFrame = raSource.FrameById(frameId);
        if (raFrame is null)
        {
            packet = null;
            failure = new SessionFailure(SessionErrorCode.FrameUnavailable, "The captured frame could not be re-read.");
            return false;
        }

        failure = null;
        return _TryReparseFrame(raFrame.Value, id, recycle, out packet);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetFrame(PacketId id, out Frame frame, [NotNullWhen(false)] out SessionFailure? failure)
    {
        if (_QueriesDisabled)
        {
            frame = default;
            failure = new SessionFailure(SessionErrorCode.QueriesDisabled, "Packet queries are disabled.");
            return false;
        }

        if (!id.IsValid)
        {
            frame = default;
            failure = new SessionFailure(SessionErrorCode.PacketNotFound, "Packet id is not valid.");
            return false;
        }

        if (!_TryGetPacketFrame(id, out FrameId frameId, out FrameSourceId sourceId))
        {
            frame = default;
            failure = new SessionFailure(
                SessionErrorCode.PacketNotFound,
                string.Format(CultureInfo.InvariantCulture, "Packet id {0} is not in the announced range.", id.Value));
            return false;
        }

        _RandomAccessSources.TryGetValue(sourceId, out IRandomAccessFrameSource? raSource);

        if (raSource is not null)
        {
            Frame? raFrame = raSource.FrameById(frameId);
            if (raFrame is not null)
            {
                frame = raFrame.Value;
                failure = null;
                return true;
            }
        }

        frame = default;
        failure = new SessionFailure(SessionErrorCode.FrameUnavailable, "The captured frame could not be re-read.");
        return false;
    }

    /// <inheritdoc/>
    public int ReadPackets(int fromIndex, Span<Packet?> buffer)
    {
        if (_QueriesDisabled)
        {
            return 0;
        }

        int filled = 0;
        int limit = _PacketCount;
        for (int i = 0; i < buffer.Length; i++)
        {
            int id = fromIndex + i;
            if (id < 0 || id >= limit)
            {
                break;
            }

            Packet? recycle = buffer[i];
            if (!TryGetPacket(new PacketId(id), recycle, out Packet? packet, out _))
            {
                buffer[i] = null;
                filled++;
                continue;
            }

            buffer[i] = packet;
            filled++;
        }

        return filled;
    }

    /// <inheritdoc/>
    public int ReadPackets(int startId, Span<PacketRef> destination, out PacketIdLayout idLayout)
    {
        idLayout = PacketIdLayout.Contiguous;
        if (_QueriesDisabled)
        {
            return 0;
        }

        int filled = 0;
        int limit = _PacketCount;
        for (int i = 0; i < destination.Length; i++)
        {
            int id = startId + i;
            if (id < 0 || id >= limit)
            {
                break;
            }

            PacketId packetId = new(id);
            Packet? recycle = destination[i].Packet;
            TryGetPacket(packetId, recycle, out Packet? packet, out _);
            destination[i] = new PacketRef(packetId, packet);
            filled++;
        }

        return filled;
    }

    /// <inheritdoc/>
    public bool TryReadPackets(
        ListenerId listenerId,
        int startId,
        Span<PacketRef> destination,
        PacketReadMode mode,
        out int count,
        out PacketIdLayout idLayout,
        out FilterError? failure,
        out SessionFailure? sessionFailure)
    {
        count = 0;
        idLayout = PacketIdLayout.Contiguous;
        failure = null;
        sessionFailure = null;

        ListenerSlot? slot = _FindListenerSlot(listenerId);
        if (slot is null)
        {
            sessionFailure = new SessionFailure(
                SessionErrorCode.ListenerNotFound,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "No listener is registered with id {0}.",
                    listenerId.Value));
            return false;
        }

        if (_QueriesDisabled)
        {
            sessionFailure = new SessionFailure(SessionErrorCode.QueriesDisabled, "Packet queries are disabled.");
            return false;
        }

        if (mode == PacketReadMode.All)
        {
            count = ReadPackets(startId, destination, out idLayout);
            return true;
        }

        lock (slot.FilterGate)
        {
            if (slot.FilterFault is FilterError fault)
            {
                failure = fault;
                sessionFailure = null;
                return false;
            }

            PacketFilter? filter = slot.Filter;
            if (filter is null || filter.IsAlwaysMatch)
            {
                count = ReadPackets(startId, destination, out idLayout);
                sessionFailure = null;
                return true;
            }

            return _TryReadMatching(filter, startId, destination, out count, out idLayout, out failure);
        }
    }

    /// <summary>
    /// Scans <c>[startId, PacketCount)</c> and fills <paramref name="destination"/> with the packets
    /// that <paramref name="filter"/> accepts.
    ///
    /// <para>
    /// Presence pruning uses <see cref="IFilter.TryIsPresenceCandidate"/> on the live index:
    /// each packet id is tested with <see cref="ReadOnlyRoaringBitmap.Contains"/> so the index
    /// can keep growing without cloning bitmaps. Packets outside the prune set are skipped
    /// without touching the field tree. Survivors reach <see cref="IFilter.TryIsMatch"/>.
    /// </para>
    /// </summary>
    private bool _TryReadMatching(
        PacketFilter filter,
        int startId,
        Span<PacketRef> destination,
        out int count,
        out PacketIdLayout idLayout,
        [NotNullWhen(false)] out FilterError? failure)
    {
        count = 0;
        idLayout = PacketIdLayout.Contiguous;
        failure = null;

        if (filter.PoisonError is FilterError poison)
        {
            failure = poison;
            return false;
        }

        PacketIndexReaderView? indexView = _PacketIndex?.AsReadOnlyView();
        int limit = _PacketCount;
        int first = Math.Max(startId, 0);
        bool skipped = false;
        int filled = 0;

        for (int id = first; id < limit && filled < destination.Length; id++)
        {
            if (indexView is not null
                && filter.TryIsPresenceCandidate(indexView.Value, (uint)id, out bool isCandidate)
                && !isCandidate)
            {
                skipped = true;
                continue;
            }

            PacketId packetId = new(id);
            Packet? recycle = destination[filled].Packet;
            if (!TryGetPacket(packetId, recycle, out Packet? packet, out _) || packet is null)
            {
                skipped = true;
                continue;
            }

            if (!filter.TryIsMatch(packet, _PacketIndex, out bool matched, out FilterError? evalFailure))
            {
                count = 0;
                failure = evalFailure;
                return false;
            }

            if (!matched)
            {
                skipped = true;
                continue;
            }

            destination[filled] = new PacketRef(packetId, packet);
            filled++;
        }

        count = filled;
        idLayout = skipped ? PacketIdLayout.Gapped : PacketIdLayout.Contiguous;
        return true;
    }

    /// <summary>
    /// Finds the slot of a registered listener, or <see langword="null"/> when the id is unknown.
    /// Linear over the listener snapshot — listener counts are small and the scan is lock-free.
    /// </summary>
    private ListenerSlot? _FindListenerSlot(ListenerId listenerId)
    {
        foreach (ListenerSlot slot in _ListenerSlots.Current)
        {
            if (slot.ListenerInfo is ListenerInfo info && info.Id == listenerId)
            {
                return slot;
            }
        }

        return null;
    }

    // -- ISessionReader: Index --

    /// <inheritdoc/>
    public PacketIndexReaderView? PacketIndex => _PacketIndex?.AsReadOnlyView();

    #endregion

    #region Listeners and jobs

    // -- ISession: Job management --

    /// <inheritdoc/>
    public bool TryAddJob(
        string uiName,
        string description,
        Action<CancellationToken> work,
        [NotNullWhen(true)] out JobInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ThrowIfDisposed();
        info = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(uiName))
        {
            throw new SessionException(
                SessionErrorCode.JobUiNameEmpty,
                "Job UiName cannot be null or whitespace.");
        }

        if (_State.Phase == SessionPhase.ShuttingDown)
        {
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Jobs cannot be added while the session is shutting down.");
            return false;
        }

        JobId jobId = _State.AllocateJobId();
        Job job = new(jobId, uiName, description, work, _OnJobStatusChanged);
        info = new JobInfo(job);

        _AllJobs.Add(info);

        // Notify listeners about the new job via flags.
        _NotifyAllListeners(NotifyFlags.JobAdded);

        job.Start();
        failure = null;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<JobInfo> GetJobs() => _AllJobs.CurrentSnapshot;

    /// <inheritdoc/>
    public bool TryRemoveJob(JobInfo job, [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ThrowIfDisposed();
        failure = null;

        if (job.Status is JobStatus.Pending or JobStatus.Running)
        {
            throw new SessionException(
                SessionErrorCode.JobStillRunning,
                "Cannot remove a job that is still pending or running. Cancel it and wait for completion first.");
        }

        if (!_AllJobs.Remove(job))
        {
            failure = new SessionFailure(SessionErrorCode.JobNotFound, "The job is not registered in this session.");
            return false;
        }

        _NotifyAllListeners(NotifyFlags.JobRemoved);
        return true;
    }

    /// <inheritdoc/>
    public bool TryUnsubscribe(JobInfo job, [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ThrowIfDisposed();
        failure = null;

        if (_State.Phase == SessionPhase.ShuttingDown)
        {
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Unsubscribe is not available while the session is shutting down.");
            return false;
        }

        if (job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)
        {
            failure = new SessionFailure(SessionErrorCode.JobTerminal, "The job is already in a terminal state.");
            return false;
        }

        // Determine job type by searching registries.
        // Check source entries first (small list, O(n) scan).
        FrameSourceEntry? sourceEntry = _FindSourceEntry(job);
        if (sourceEntry is not null)
        {
            return _TryUnsubscribeSource(sourceEntry);
        }

        // Check listener slots next.
        (ListenerSlot? slot, ListenerInfo? info) = _FindListenerSlotAndInfo(job);
        if (slot is not null && info is not null)
        {
            return _TryUnsubscribeListener(slot, info);
        }

        (ValueCacheSlot? valueCacheSlot, ValueCacheInfo? valueCacheInfo) = _FindValueCacheSlotAndInfo(job);
        if (valueCacheSlot is not null && valueCacheInfo is not null)
        {
            return _TryUnsubscribeValueCache(valueCacheSlot, valueCacheInfo);
        }

        // Must be a user job — cancel only if owned by this session.
        if (!_ContainsJob(job))
        {
            failure = new SessionFailure(SessionErrorCode.JobNotFound, "The job is not registered in this session.");
            return false;
        }

        return _TryUnsubscribeUserJob(job);
    }

    // ── Unsubscribe helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="job"/> is registered in
    /// <see cref="_AllJobs"/> (reference identity).
    /// </summary>
    private bool _ContainsJob(JobInfo job)
    {
        foreach (JobInfo entry in _AllJobs.Current)
        {
            if (ReferenceEquals(entry, job))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the <see cref="FrameSourceEntry"/> whose <see cref="FrameSourceEntry.JobInfo"/>
    /// matches the given <paramref name="job"/>. Returns <see langword="null"/> if not found.
    /// </summary>
    private FrameSourceEntry? _FindSourceEntry(JobInfo job)
    {
        foreach (FrameSourceEntry entry in _SourceEntries.Current)
        {
            if (ReferenceEquals(entry.JobInfo, job))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the <see cref="ListenerSlot"/> and its corresponding <see cref="ListenerInfo"/>
    /// whose job matches the given <paramref name="job"/>. Returns nulls if not found.
    /// The <see cref="ListenerInfo"/> is retrieved from <see cref="ListenerSlot.ListenerInfo"/>
    /// which is set during <see cref="TryAddListener(ISessionListener, IFilter?, out ListenerInfo?, out SessionFailure?)"/>.
    /// </summary>
    private (ListenerSlot? Slot, ListenerInfo? Info) _FindListenerSlotAndInfo(JobInfo job)
    {
        foreach (ListenerSlot slot in _ListenerSlots.Current)
        {
            if (ReferenceEquals(slot.Info, job))
            {
                return (slot, slot.ListenerInfo);
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Stops a source job. The source thread exits after the current frame.
    /// The source remains available for random access and reparse.
    /// </summary>
    private void _RemoveRandomAccessSource(FrameSourceId sourceId)
    {
        Dictionary<FrameSourceId, IRandomAccessFrameSource> current = _RandomAccessSources;
        if (!current.ContainsKey(sourceId))
        {
            return;
        }

        Dictionary<FrameSourceId, IRandomAccessFrameSource> next = new(current);
        next.Remove(sourceId);
        _RandomAccessSources = next;
    }

    private bool _TryUnsubscribeSource(FrameSourceEntry entry)
    {
        if (entry.Job.Status == JobStatus.Pending)
        {
            entry.Info.ClearStopCallback();
            _SourceEntries.Remove(entry);
            _SourceInfos.Remove(entry.Info);
            _AllJobs.Remove(entry.JobInfo);
            _RemoveRandomAccessSource(entry.Info.Id);
            entry.Source.Dispose();
            entry.Job.Dispose();
            _NotifyAllListeners(NotifyFlags.JobRemoved);
            return true;
        }

        entry.Info.ClearStopCallback();
        // Cancel the source job — sets the CancellationToken that _RunSourceLoop observes.
        entry.Job.Cancel();

        // Wait for the source thread to reach a terminal state.
        // _RunSourceLoop's finally block decrements _ActiveSourceCount and
        // notifies listeners (SourceCompleted, AllSourcesCompleted, PhaseChanged).
        entry.Job.Join();

        // Notify listeners that a job changed status.
        _NotifyAllListeners(NotifyFlags.JobStatusChanged);
        return true;
    }

    /// <summary>
    /// Unsubscribes a listener. Sets the subscription status, cancels the slot,
    /// waits for the thread to exit (which calls OnUnsubscribed), then removes
    /// the slot from registries and disposes it.
    /// </summary>
    private bool _TryUnsubscribeListener(ListenerSlot slot, ListenerInfo info)
    {
        // Set status BEFORE cancel so that OnUnsubscribed (called in RunLoop's
        // finally block) reads the correct status.
        info.SetStatus(SubscriptionStatus.Unsubscribed);

        // Cancel the slot — sets the CancellationToken observed by RunLoop.
        slot.Cancel();

        // If the slot was never started (Pending), start it so RunLoop can
        // observe cancellation and exit cleanly. The Job CAS guard prevents
        // double-start.
        if (slot.Status == JobStatus.Pending)
        {
            try
            {
                slot.Start();
            }
            catch
            {
                // Job.Start already transitioned to Failed — RunLoop never runs.
                slot.EnsureOnUnsubscribed();
            }
        }

        // Wait for the listener thread to reach a terminal state.
        slot.Join();
        slot.EnsureOnUnsubscribed();

        // Remove from registries.
        _ListenerSlots.Remove(slot);
        if (info is not null)
        {
            _ListenerInfos.Remove(info);
        }

        // Dispose the slot (disposes the underlying Job + CTS).
        slot.Dispose();

        // Notify remaining listeners that a job changed status.
        _NotifyAllListeners(NotifyFlags.JobStatusChanged);
        return true;
    }

    private void _ThrowIfDisposed()
    {
        if (_Disposed)
        {
            throw new SessionException(SessionErrorCode.Disposed, "The session has been disposed.");
        }
    }

    #endregion
}
