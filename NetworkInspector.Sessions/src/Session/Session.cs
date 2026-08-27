// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Orchestrates frame sources, the protocol stack, listeners,
/// and background jobs using a pull-based notification model.
///
/// <para>
/// <b>Source-thread model:</b>
/// Each <see cref="IFrameSource"/> receives a dedicated job thread that pulls frames via
/// <see cref="IFrameSource.NextFrame"/>, parses them under a <see cref="SpinLock"/>
/// (shared across all source threads), stores the resulting packet in the
/// <see cref="PacketStore"/>, and sets <see cref="NotifyFlags.NewPackets"/> on all
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
/// <b>Why SpinLock for parsing:</b>
/// <see cref="Stack"/> modifies protocol-instance state during parsing
/// (lazy field arrays, reassembly buffers). A single shared SpinLock serialises all
/// concurrent source threads. The contention window is short (one frame parse), so
/// SpinLock outperforms a Monitor here.
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><c>TryAddFrameSource</c> -- safe before <c>TryStart</c> on any thread.</item>
///   <item><c>TryAddListener</c> -- safe before or after <c>TryStart</c>.</item>
///   <item><c>TryStart</c> -- safe once; transitions phase to Running.</item>
///   <item><c>PacketCount</c>, <c>FrameCount</c> -- volatile reads, always current.</item>
///   <item><c>TryGetPacket</c> -- safe from any thread (PacketStore + re-parse).</item>
///   <item><c>TryAddJob</c> -- safe from any thread.</item>
///   <item><c>Shutdown</c> / <c>Dispose</c> -- safe to call from any thread.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>Creates a new session bound to <paramref name="stack"/> with default options.</remarks>
public sealed class Session(Stack stack, SessionOptions? options = null) : ISession, ISessionReader
{
    // -- Configuration --

    // Non-readonly: Restart() swaps the stack for a new one.
    private Stack _Stack = stack ?? throw new ArgumentNullException(nameof(stack));

    // True when the session created the stack via factory (Restart). False for the
    // initial stack passed to the constructor (caller manages its lifetime).
    private bool _OwnsStack;

    // Session-owned registry: shared across all stacks. Extracted from the
    // initial stack so that source and interface IDs remain stable across restarts.
    private readonly FrameInterfaceRegistry _FrameInterfaceRegistry = stack.FrameInterfaceRegistry;

    // -- State --

    private readonly SessionState _State = new();

    // Global atomic counters readable from any thread.
    private volatile int _PacketCount;
    private volatile int _FrameCount;

    // Number of source jobs that have not yet finished.
    // Transitions to 0 means all sources are done.
    private volatile int _ActiveSourceCount;

    // -- Parse lock --

    // SpinLock protects Stack parsing which mutates protocol-instance state.
    // enableThreadOwnerTracking=false removes re-entrancy overhead on hot path.
    private SpinLock _ParseLock = new(enableThreadOwnerTracking: false);

    // Globally unique PacketId counter (shared across all source threads).
    // Allocated INSIDE the SpinLock to guarantee correctness after a reparse
    // (which resets the counter under the lock).
    private volatile int _NextPacketId;

    // Kernel-level gate that blocks source threads during a stack-swap reparse.
    // Initially signalled (open): Wait() returns immediately during normal parsing.
    // During Restart(), the gate is Reset() (closed) to park source threads,
    // then Set() (opened) after all frames have been re-parsed.
    private readonly ManualResetEventSlim _ParseGate = new(initialState: true);

    // Guards against concurrent Restart() calls.
    // 0 = idle, 1 = restart in progress.
    private volatile int _RestartInProgress;

    // -- Shared stores --

    // All parsed packets -- single store, read by all listeners.
    private readonly PacketStore _PacketStore = new();

    // PacketId -> (FrameId, SourceId) for random-access re-parse.
    private readonly PacketToFrameMap _Mapping = new();

    // Roaring Bitmap index populated during parsing (protocol presence, field groups).
    // Created by _StartInternal(), set to null by Restart().
    private PacketIndex? _PacketIndex;

    // -- Source registry --

    // Sources registered before Start(). Re-used on Restart().
    private readonly SnapshotList<FrameSourceEntry> _SourceEntries = new();

    // Public read-only views of frame sources for GetFrameSources().
    private readonly SnapshotList<FrameSourceInfo> _SourceInfos = new();

    // Running source jobs (populated at Start() time, replaced on Restart()).
    // Non-readonly: reassigned by _StartInternal() on each start/restart cycle.
    private Job[] _SourceJobs = [];

    // Random-access capable sources keyed by FrameSourceId for GetPacket().
    // Copy-on-write: written only during _AddFrameSourceInternal (rare), read during TryGetPacket (hot).
    // Volatile reference swap replaces the previous lock(object) pattern for lock-free reads.
    private volatile Dictionary<FrameSourceId, IRandomAccessFrameSource> _RandomAccessSources = new();

    // -- Listener registry --

    // Active listener slots. SnapshotList for lock-free iteration by source threads.
    private readonly SnapshotList<ListenerSlot> _ListenerSlots = new();

    // Public read-only views of listener subscriptions for GetListeners().
    private readonly SnapshotList<ListenerInfo> _ListenerInfos = new();

    // -- Unified job list --

    // All jobs: source, listener, and user jobs are all registered here.
    // Lock-free: reads return the current snapshot, writes use CAS retry loop.
    private readonly SnapshotList<JobInfo> _AllJobs = new();

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
    public bool StoreParsedPackets { get; } = (options ?? SessionOptions.Default).StoreParsedPackets;

    /// <inheritdoc/>
    public bool IndexPackets { get; } = (options ?? SessionOptions.Default).IndexPackets;

    // -- ISession: Source management --

    /// <inheritdoc/>
    public bool TryAddFrameSource(IFrameSource source, [NotNullWhen(true)] out FrameSourceInfo? info)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ThrowIfDisposed();

        if (_State.Phase != SessionPhase.Idle)
        {
            info = null;
            return false;
        }

        info = _AddFrameSourceInternal(source);
        return true;
    }

    // -- ISessionReader: Source info --

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<FrameSourceInfo> GetFrameSources() => _SourceInfos.CurrentSnapshot;

    // -- ISession: Listener management --

    /// <inheritdoc/>
    public bool TryAddListener(ISessionListener listener, [NotNullWhen(true)] out ListenerInfo? info) =>
        TryAddListener(listener, filter: null, out info);

    /// <inheritdoc/>
    public bool TryAddListener(
        ISessionListener listener,
        string? filterExpression,
        [NotNullWhen(true)] out ListenerInfo? info,
        out FilterError? filterFailure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _ThrowIfDisposed();

        // Compile before allocating any listener state so a bad expression leaves the session
        // exactly as it was.
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(filterExpression ?? string.Empty, _Stack);
        if (!compiled.TryGetValue(out PacketFilter? filter))
        {
            info = null;
            filterFailure = compiled.Error;
            return false;
        }

        filterFailure = null;
        return TryAddListener(listener, filter, out info);
    }

    /// <inheritdoc/>
    public bool TryAddListener(ISessionListener listener, IFilter? filter, [NotNullWhen(true)] out ListenerInfo? info)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(listener.UiName))
        {
            throw new SessionException(
                SessionErrorCode.ListenerUiNameEmpty,
                "Listener UiName cannot be null or whitespace.");
        }

        // Cannot add listeners during shutdown or after stop.
        if (_State.Phase is SessionPhase.ShuttingDown or SessionPhase.Stopped)
        {
            info = null;
            return false;
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
        info.UnsubscribeCallback = () => TryUnsubscribe(slotJobInfo);

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

        return true;
    }

    // -- ISessionReader: Listener info --

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<ListenerInfo> GetListeners() => _ListenerInfos.CurrentSnapshot;

    // -- ISessionReader: Packet access --

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacket(PacketId id, [NotNullWhen(true)] out Packet? packet) =>
        TryGetPacket(id, recycle: null, out packet);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPacket(PacketId id, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        if (_QueriesDisabled || !id.IsValid)
        {
            packet = null;
            return false;
        }

        // Step 1: Try the PacketStore first -- O(1), lock-free, no re-parse needed.
        Packet? stored = _PacketStore.Get(id);
        if (stored is not null)
        {
            packet = stored;
            return true;
        }

        // Step 2: PacketStore miss (cleared or not yet stored).
        // Fall back to mapping -> random-access source -> re-parse.
        if (!_Mapping.TryGet(id, out FrameId frameId, out FrameSourceId sourceId))
        {
            packet = null;
            return false;
        }

        // Lock-free read: volatile reference ensures we see the latest dictionary snapshot.
        _RandomAccessSources.TryGetValue(sourceId, out IRandomAccessFrameSource? raSource);

        if (raSource is not null)
        {
            Frame? raFrame = raSource.FrameById(frameId);
            if (raFrame is not null)
            {
                if (!_TryReparseFrame(raFrame.Value, id, recycle, out packet))
                {
                    return false;
                }

                _TryStorePacket(id, packet);
                return true;
            }
        }

        // Frame not reachable (source does not support random access).
        packet = null;
        return false;
    }

    /// <inheritdoc/>
    public int ReadPackets(int fromIndex, Span<Packet?> buffer)
    {
        if (_QueriesDisabled)
        {
            return 0;
        }
        return _PacketStore.ReadRange(fromIndex, buffer);
    }

    /// <inheritdoc/>
    public int ReadPackets(int startId, Span<PacketRef> destination, out PacketIdLayout idLayout)
    {
        idLayout = PacketIdLayout.Contiguous;
        if (_QueriesDisabled)
        {
            return 0;
        }

        return _PacketStore.ReadRange(startId, destination);
    }

    /// <inheritdoc/>
    public bool TryReadPackets(
        ListenerId listenerId,
        int startId,
        Span<PacketRef> destination,
        PacketReadMode mode,
        out int count,
        out PacketIdLayout idLayout,
        [NotNullWhen(false)] out FilterError? failure)
    {
        count = 0;
        idLayout = PacketIdLayout.Contiguous;
        failure = null;

        ListenerSlot slot = _FindListenerSlot(listenerId)
            ?? throw new SessionException(
                SessionErrorCode.ListenerNotFound,
                $"No listener is registered with id {listenerId.Value.ToString(CultureInfo.InvariantCulture)}.");

        if (_QueriesDisabled)
        {
            return true;
        }

        if (mode == PacketReadMode.All)
        {
            count = _PacketStore.ReadRange(startId, destination);
            return true;
        }

        // A failed re-bind after a stack swap must not degrade into "match everything":
        // the caller has to learn that its filter is gone.
        if (slot.FilterFault is FilterError fault)
        {
            failure = fault;
            return false;
        }

        PacketFilter? filter = slot.Filter;
        if (filter is null || filter.IsAlwaysMatch)
        {
            count = _PacketStore.ReadRange(startId, destination);
            return true;
        }

        return _TryReadMatching(filter, startId, destination, out count, out idLayout, out failure);
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

            PacketId packetId = new((int)id);
            Packet? packet = _PacketStore.Get(packetId);
            if (packet is null)
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

    // -- ISession: Job management --

    /// <inheritdoc/>
    public bool TryAddJob(string uiName, string description, Action<CancellationToken> work,
        [NotNullWhen(true)] out JobInfo? info)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(uiName))
        {
            throw new SessionException(
                SessionErrorCode.JobUiNameEmpty,
                "Job UiName cannot be null or whitespace.");
        }

        // Cannot add jobs during shutdown or after stop.
        if (_State.Phase is SessionPhase.ShuttingDown or SessionPhase.Stopped)
        {
            info = null;
            return false;
        }

        JobId jobId = _State.AllocateJobId();
        Job job = new(jobId, uiName, description, work, _OnJobStatusChanged);
        info = new JobInfo(job);

        _AllJobs.Add(info);

        // Notify listeners about the new job via flags.
        _NotifyAllListeners(NotifyFlags.JobAdded);

        job.Start();
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Returns the current immutable snapshot array; no per-call allocation copy.</remarks>
    public IReadOnlyList<JobInfo> GetJobs() => _AllJobs.CurrentSnapshot;

    /// <inheritdoc/>
    public bool TryRemoveJob(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ThrowIfDisposed();

        if (job.Status is JobStatus.Pending or JobStatus.Running)
        {
            throw new SessionException(
                SessionErrorCode.JobStillRunning,
                "Cannot remove a job that is still pending or running. Cancel it and wait for completion first.");
        }

        // Remove from the unified list and notify listeners.
        if (!_AllJobs.Remove(job))
        {
            return false;
        }

        _NotifyAllListeners(NotifyFlags.JobRemoved);
        return true;
    }

    /// <inheritdoc/>
    public bool TryUnsubscribe(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ThrowIfDisposed();

        // Phase guard: unsubscribe only makes sense during Running/Stopped/Restarting.
        // During Idle nothing is running yet; during ShuttingDown the session handles cleanup.
        SessionPhase phase = _State.Phase;
        if (phase is SessionPhase.Idle or SessionPhase.ShuttingDown)
        {
            return false;
        }

        // Terminal guard: job is already done — nothing to cancel.
        if (job.Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)
        {
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

        // Must be a user job — cancel only if owned by this session.
        if (!_ContainsJob(job))
        {
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
    /// which is set during <see cref="TryAddListener(ISessionListener, IFilter?, out ListenerInfo?)"/>.
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
    private bool _TryUnsubscribeSource(FrameSourceEntry entry)
    {
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

    // -- ISession: Lifecycle --

    /// <inheritdoc/>
    public bool TryStart()
    {
        _ThrowIfDisposed();

        if (_State.Phase != SessionPhase.Idle)
        {
            return false;
        }

        _StartInternal();
        return true;
    }

    /// <inheritdoc/>
    public bool WaitForCompletion(TimeSpan? timeout = null)
    {
        if (timeout is null)
        {
            foreach (Job job in _SourceJobs)
            {
                job.Join();
            }
            return true;
        }

        TimeSpan limit = timeout.Value;
        foreach (Job job in _SourceJobs)
        {
            if (!job.Join(limit))
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

        if (_State.Phase is SessionPhase.ShuttingDown or SessionPhase.Idle)
        {
            throw new SessionException(
                SessionErrorCode.InvalidPhase,
                $"Restart() requires a Running or Stopped phase. " +
                $"Current phase: {_State.Phase}.");
        }

        // Prevent concurrent Restart() calls.
        if (Interlocked.CompareExchange(ref _RestartInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException("A restart is already in progress.");
        }

        try
        {
            _RestartCore(stackFactory);
        }
        finally
        {
            _RestartInProgress = 0;
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
    /// (0 … N-1) via the <see cref="PacketToFrameMap"/>. Non-random-access
    /// sources are skipped (their past frames cannot be retrieved).
    /// </para>
    /// </summary>
    private void _RestartCore(Func<FrameInterfaceRegistry, Stack> stackFactory)
    {
        // ── Phase 0: Build the new stack (outside any lock) ──────────────────
        Stack newStack = stackFactory(_FrameInterfaceRegistry)
            ?? throw new InvalidOperationException("The stack factory returned null.");

        if (!ReferenceEquals(newStack.FrameInterfaceRegistry, _FrameInterfaceRegistry))
        {
            throw new ArgumentException(
                "The stack returned by the factory must use the FrameInterfaceRegistry " +
                "that was passed to the factory. Do not create a new registry.",
                nameof(stackFactory));
        }

        // ── Phase 1: Gate source threads and swap the stack ──────────────────
        _State.SetPhase(SessionPhase.Restarting);
        _NotifyAllListeners(NotifyFlags.PhaseChanged);

        // Close the gate. Source threads that finish their current NextFrame()
        // call block at _ParseGate.Wait(ct) until we Set() below.
        _ParseGate.Reset();

        // Disable queries while the store is being rebuilt.
        _QueriesDisabled = true;

        int totalToReparse;
        bool lockTaken = false;
        try
        {
            // Wait for any in-flight parse to finish — once we own the lock
            // no source thread is inside the parse section (they are either
            // still in NextFrame or blocked on the gate).
            _ParseLock.Enter(ref lockTaken);

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

            // Clear the packet store (old parse results reference the old stack).
            // Do NOT clear the mapping — it stores frame order needed for re-parse
            // and the PacketId → (FrameId, SourceId) data remains valid.
            _PacketStore.Clear();

            // Create a fresh packet index for the new stack's field definitions.
            _PacketIndex = _TryCreatePacketIndex(_Stack);

            // Reset counters so re-parse fills them from 0.
            Interlocked.Exchange(ref _PacketCount, 0);
            Interlocked.Exchange(ref _FrameCount, 0);
            Interlocked.Exchange(ref _NextPacketId, 0);
        }
        finally
        {
            if (lockTaken)
            {
                _ParseLock.Exit(useMemoryBarrier: false);
            }
        }

        // ── Phase 2 + 3: Re-parse and resume ─────────────────────────────────
        // Wrapped in try/finally to guarantee the parse gate is reopened even
        // if _ReparseAllFrames throws (OOM, etc.). Without this, source threads
        // would remain parked on the closed gate indefinitely — a deadlock.
        try
        {
            _ReparseAllFrames(totalToReparse);
        }
        finally
        {
            // Re-bind every listener filter to the new stack while pull queries are still
            // disabled. A filter carries field ids resolved against the retired stack, so it must
            // be replaced before any listener can pull again.
            _DeriveListenerFilters();

            // Re-enable queries — the store contains all (or partially) re-parsed packets.
            _QueriesDisabled = false;

            // Determine the post-reparse phase based on source activity.
            // If all sources have already finished, transition directly to Stopped
            // instead of Running (mirrors the natural transition in _RunSourceLoop).
            bool sourcesStillActive = _ActiveSourceCount > 0;
            SessionPhase finalPhase = sourcesStillActive
                ? SessionPhase.Running
                : SessionPhase.Stopped;

            _State.SetPhase(finalPhase);

            // Build the combined notification flags.
            NotifyFlags flags = NotifyFlags.StackChanged | NotifyFlags.NewPackets | NotifyFlags.PhaseChanged;
            if (!sourcesStillActive)
            {
                flags |= NotifyFlags.AllSourcesCompleted;
            }

            // Reset all listener cursors to 0 so OnNewPackets delivers the full
            // re-parsed range, then notify StackChanged + NewPackets + PhaseChanged.
            _ResetAllListenerCursors();
            _NotifyAllListeners(flags);

            // Open the gate — source threads resume and parse with the new stack.
            // New frames receive PacketIds starting from totalToReparse.
            _ParseGate.Set();
        }
    }

    /// <summary>
    /// Re-parses frames for PacketIds 0 … <paramref name="count"/>-1 using the
    /// current <see cref="_Stack"/>. Reads frame data from random-access sources
    /// via the <see cref="_Mapping"/>. Non-random-access sources are silently
    /// skipped (their past frames cannot be retrieved).
    ///
    /// <para>
    /// Called while the <see cref="_ParseGate"/> is closed, so no source thread
    /// is parsing concurrently. Each frame is still parsed under the
    /// Each frame is still parsed under the ingest lock during stack-swap reparse.
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
            if (!_Mapping.TryGet(originalId, out FrameId frameId, out FrameSourceId sourceId))
            {
                continue;
            }

            if (!raSources.TryGetValue(sourceId, out IRandomAccessFrameSource? raSource))
            {
                continue;
            }

            Frame? frame = raSource.FrameById(frameId);
            if (frame is null)
            {
                continue;
            }

            Packet packet = _ParseFrameUnderLock(frame.Value, packetId: null);

            _TryStorePacket(packet.Id, packet);

            // The mapping entry is unchanged (same PacketId → same frame).
            // No need to re-record.

            // Update counters.
            Interlocked.Increment(ref _PacketCount);
            Interlocked.Increment(ref _FrameCount);
        }
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

    /// <inheritdoc/>
    public void Shutdown(TimeSpan? timeout = null)
    {
        // CAS gate: only one thread executes shutdown. Concurrent callers wait
        // for the executing thread to finish (spin on _ListenersTornDown).
        if (Interlocked.CompareExchange(ref _ShutdownStarted, 1, 0) != 0)
        {
            // Another thread is performing or has completed shutdown — wait for it.
            SpinWait spinner = new();
            while (_ListenersTornDown == 0)
            {
                spinner.SpinOnce();
            }
            return;
        }

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
        foreach (Job job in _SourceJobs)
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

        _QueriesDisabled = true;

        // Step 5: Transition to final state.
        _State.SetPhase(SessionPhase.Stopped);

        // Step 6: Dispose all jobs and listener slots.
        // Try-catch per item ensures one failed dispose does not prevent
        // cleanup of subsequent items. All exceptions are collected and
        // thrown as an AggregateException after all cleanup completes.
        List<Exception>? cleanupErrors = null;

        foreach (Job job in _SourceJobs)
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
            _Stack.Dispose();
            _OwnsStack = false;
        }

        // Dispose the parse gate. Safe because all source threads have finished
        // (they were cancelled and waited for above) and no new Wait() calls
        // can occur after this point.
        _ParseGate.Dispose();

        // Signal completion so concurrent callers waiting on the CAS gate can proceed.
        _ListenersTornDown = 1;

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

    // -- Internal helpers: Source registration --

    /// <summary>
    /// Registers a new frame source in the registry and creates its job.
    /// Used by <see cref="TryAddFrameSource"/> for initial source registration.
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
        info.RegisterStopCallback(() => TryUnsubscribe(entryJobInfo));

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
        // Create a fresh packet index for this run when indexing is enabled.
        _PacketIndex = _TryCreatePacketIndex(_Stack);

        // Re-enable queries (may have been disabled by a previous Restart or Shutdown attempt).
        _QueriesDisabled = false;

        ReadOnlySpan<FrameSourceEntry> entries = _SourceEntries.Current;
        _SourceJobs = new Job[entries.Length];
        _ActiveSourceCount = entries.Length;

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

        // Start all source jobs. Each start is individually guarded so that
        // a thread-creation failure (e.g. OOM) for one source does not prevent
        // the remaining sources from starting.
        for (int i = 0; i < entries.Length; i++)
        {
            _SourceJobs[i] = entries[i].Job;
            try
            {
                _SourceJobs[i].Start();
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
    ///   <item>Parse each frame under the shared <see cref="_ParseLock"/>.</item>
    ///   <item>Record the PacketId -> FrameId mapping.</item>
    ///   <item>Store the packet in the <see cref="PacketStore"/>.</item>
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

            // When the packet store is off the ingest Packet is discarded after mapping;
            // recycle it on this source thread instead of allocating every frame.
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

                Packet? recycle = null;
                if (!StoreParsedPackets)
                {
                    recycle = ingestRecycle;
                }

                Packet packet = _ParseFrameUnderLock(capturedFrame, packetId: null, recycle);

                // Record PacketId -> FrameId mapping for random access re-parse.
                // Failure means the PacketId is invalid (should not happen after allocation).
                if (!_Mapping.Record(packet.Id, capturedFrame.Id, sourceInfo.Id))
                {
                    ThrowMappingRecordFailed(packet.Id);
                }

                _TryStorePacket(packet.Id, packet);

                if (!StoreParsedPackets)
                {
                    ingestRecycle = packet;
                }

                // Update global atomic counters.
                Interlocked.Increment(ref _PacketCount);
                Interlocked.Increment(ref _FrameCount);

                // Set NewPackets flag on all listener slots — non-blocking, no copy.
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
    /// Allocates the next sequential <see cref="PacketId"/> under <see cref="_ParseLock"/>.
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
        if (_PacketIndex is not null)
        {
            return Packet.ParseFrameIndexed(id, _Stack, frame, _PacketIndex);
        }

        return Packet.ParseFrame(id, _Stack, frame);
    }

    /// <summary>
    /// First parse of a frame under <see cref="_ParseLock"/>. The lock plus the monotonic ids from
    /// <see cref="_AllocateNextPacketId"/> establish exactly the contract stateful protocols rely on:
    /// every packet id is parsed for the first time exactly once, ordered and single-threaded. That
    /// is what lets those protocols record their replay state here and serve lock-free re-parses
    /// afterwards, without the session having to pass any parse mode.
    /// </summary>
    private Packet _ParseFrameUnderLock(Frame frame, PacketId? packetId, Packet? recycle = null)
    {
        bool lockTaken = false;
        try
        {
            _ParseLock.Enter(ref lockTaken);
            PacketId id = packetId ?? _AllocateNextPacketId();
            if (recycle is not null)
            {
                RecycleError? error = _PacketIndex is not null
                    ? Packet.TryParseFrameIndexed(recycle, id, _Stack, frame, _PacketIndex)
                    : Packet.TryParseFrame(recycle, id, _Stack, frame);
                if (error is null)
                {
                    return recycle;
                }
            }

            return _ParseFrameCore(id, frame);
        }
        finally
        {
            if (lockTaken)
            {
                _ParseLock.Exit(useMemoryBarrier: false);
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="packet"/> when the session is configured to cache ingest results.
    /// Skipped when <see cref="StoreParsedPackets"/> is <see langword="false"/> so listeners
    /// always redissect.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _TryStorePacket(PacketId id, Packet packet)
    {
        if (!StoreParsedPackets)
        {
            return;
        }

        _PacketStore.Store(id, packet);
    }

    /// <summary>
    /// Re-parses an already announced packet id. Runs lock-free on any thread: because the id was
    /// announced, its first parse completed, so stateful protocols detect the re-parse themselves and
    /// replay their recorded state instead of mutating it. Passing an id that was never announced
    /// would be a first parse on an arbitrary thread and is therefore not allowed.
    /// <para>
    /// A non-<see langword="null"/> <paramref name="recycle"/> is reused in place. A rejected recycle
    /// (see <see cref="RecycleError"/>) is not an error for the caller: the re-parse repeats into a
    /// fresh packet.
    /// </para>
    /// </summary>
    private bool _TryReparseFrame(
        Frame frame, PacketId packetId, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        try
        {
            if (recycle is not null)
            {
                RecycleError? error = _PacketIndex is not null
                    ? Packet.TryParseFrameIndexed(recycle, packetId, _Stack, frame, _PacketIndex)
                    : Packet.TryParseFrame(recycle, packetId, _Stack, frame);
                if (error is null)
                {
                    packet = recycle;
                    return true;
                }
            }

            packet = _PacketIndex is not null
                ? Packet.ParseFrameIndexed(packetId, _Stack, frame, _PacketIndex)
                : Packet.ParseFrame(packetId, _Stack, frame);
            return true;
        }
        catch
        {
            packet = null;
            return false;
        }
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
    }

    // -- Job status callback --

    /// <summary>
    /// Called by each <see cref="Job"/> when its status changes.
    /// Sets <see cref="NotifyFlags.JobStatusChanged"/> on all listener slots.
    /// </summary>
    private void _OnJobStatusChanged(Job job, JobStatus status)
        => _NotifyAllListeners(NotifyFlags.JobStatusChanged);

    // -- Helper --

    private void _ThrowIfDisposed()
    {
        if (_Disposed)
        {
            throw new SessionException(SessionErrorCode.Disposed, "The session has been disposed.");
        }
    }
}
