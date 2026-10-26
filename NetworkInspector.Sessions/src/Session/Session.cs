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
/// <b>First parse versus re-parse:</b>
/// Only the first parse of a packet id runs under the Monitor, with dense ascending ids: protocols that keep
/// cross-packet state record it then. Every later parse of an announced id (<c>TryGetPacket</c>, <c>ReadPackets</c>,
/// listener pulls) is lock-free, may run on any thread, and replays the recorded state. The first parse uses
/// <see cref="FieldTreeMode.Skip"/> and recycles one throwaway packet per source thread, because the session retains
/// no packets (only the packet id to frame map). A re-parse rebuilds the field tree from the captured frame.
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><c>TryAddFrameSource</c> -- safe before <c>TryStart</c> on any thread, also concurrently with other adds.</item>
///   <item><c>TryAddListener</c>, <c>TryAddValueCache</c> -- safe before or after <c>TryStart</c>.</item>
///   <item><c>TryStart</c> -- safe from any thread; exactly one caller wins, the others get <c>InvalidPhase</c>.</item>
///   <item><c>PacketCount</c>, <c>FrameCount</c> -- volatile reads, always current.</item>
///   <item><c>TryGetPacket</c> -- safe from any thread (re-parse from frame cache or RA source); holds a stack lease while it parses.</item>
///   <item><c>Restart</c> -- one at a time; does not wait for in-flight reads, the replaced stack is disposed by the last of them.</item>
///   <item><c>TryAddJob</c> -- safe from any thread.</item>
///   <item><c>Shutdown</c> / <c>Dispose</c> -- safe to call from any thread.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Stack lifetime (lease scheme):</b>
/// <see cref="Restart"/> swaps the stack under the parse mutex, but lock-free reads can still be re-parsing on the old one,
/// and <see cref="Stack.Dispose"/> runs every protocol's <c>OnShutdown</c> and releases the settings locks. Every
/// operation that parses outside the parse mutex (<c>TryGetPacket</c>, <c>ReadPackets</c>, the matching read, the listener
/// and value-cache pulls) therefore takes a lease on the stack it parses with (one interlocked increment, released with one
/// decrement; no lock, no allocation) and re-checks <c>QueriesEnabled</c> after taking it. A restart retires the replaced stack
/// right after the swap: it is disposed at once when no lease is held, otherwise by the thread that releases the last lease.
/// Rejected: keeping every retired stack until <see cref="Shutdown"/> (holds the memory of every old stack and delays
/// <c>OnShutdown</c> for the whole session), and disposing under the parse mutex (readers do not take it, so it excludes
/// nobody who matters and stalls ingest for the length of the protocol shutdowns). Disposal errors are collected and thrown by
/// <see cref="Shutdown"/>. <see cref="Shutdown"/> waits for outstanding leases within its time budget and disposes the stack
/// anyway afterwards; the residual risk then is a read that is still parsing on a disposed stack (settings reads throw
/// <see cref="ObjectDisposedException"/>, which the parser records as a parse error; a protocol's own released resource can
/// produce wrong content), and a source thread that outlived the budget is not waited for at all.
/// </para>
///
/// <para>
/// <b>Packets and the stack that made them:</b> a <see cref="Packet"/> handed out before a restart belongs to the old stack.
/// Its fields that were materialised stay valid for as long as the caller keeps the packet (they are plain data). Its
/// still unmaterialised lazy containers run on the old protocol instances, which are shut down once the restart has swapped
/// them out and no read is parsing on them any more: such a container then reports a failed population as an error child (the
/// packet never throws into the reader, but the content is not guaranteed). A caller that needs the complete packet across a
/// restart materialises it first (<c>Packet.MaterializeAll</c>) or, better, re-reads it after <c>OnStackChanged</c>. Why not
/// more: a lease per packet would cost an interlocked pair for every packet while no restart happens, and a packet has no
/// dispose, so a dropped packet could never give its lease back (the stack would live forever); tracking the handed-out
/// packets would retain them, which the session deliberately never does; materialising every packet before it is returned would
/// defeat the lazy design. <see cref="Stack"/> itself stays usable for lookups (names, ids, infos) after disposal; only a
/// parse or a population that reaches the protocol instances or the settings is unsafe.
/// </para>
///
/// <para>
/// <b>Registry lock:</b>
/// Every registry (sources, listener and value-cache slots, user jobs, the random-access table) is a copy-on-write
/// snapshot that the per-frame paths read without a lock. The rare writers (<c>TryAdd*</c>, the unsubscribe of a source,
/// the start and the shutdown flag) serialise on one short lock, <see cref="_RegistryLock"/>. Under it an adder re-checks
/// that the session has not started shutting down (for sources: has not started at all) and only then publishes its fully
/// built object; <c>Shutdown</c> publishes its flag under the same lock. Everything a registration published is therefore
/// visible to the shutdown snapshots, and everything later is refused, so no slot or job escapes the cancel-and-join.
/// </para>
/// </summary>
public sealed class Session : ISession, ISessionReader
{
    #region Construction

    /// <summary>Creates a new session bound to <paramref name="stack"/>.</summary>
    /// <param name="stack">Protocol stack used for the first parse of each frame.</param>
    /// <param name="options">Construction-time options. <see langword="null"/> uses <see cref="SessionOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>,
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/> or
    /// <see cref="SessionErrorCode.ValueCacheInvalidRequest"/> when
    /// <see cref="SessionOptions.ValueCache"/> names fail identifier validation or stack lookup, or the request cannot be built.
    /// <see cref="SessionErrorCode.ValueCacheListenerWithoutRequest"/> when a listener is set without a request.
    /// </exception>
    public Session(Stack stack, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);

        // The caller keeps ownership of the initial stack: the session never disposes it, but it still counts the leases.
        _Holder = new RetirableStack(stack, ownedBySession: false, _OnStackDisposed);

        // The registry outlives every stack of this session (Restart hands it to the stack factory), so source and
        // interface ids stay valid across a stack swap.
        _FrameInterfaceRegistry = stack.FrameInterfaceRegistry;
        SessionOptions resolved = options ?? SessionOptions.Default;
        IndexPackets = resolved.IndexPackets;
        _IngestRequest = resolved.ValueCache;
        _IngestListener = resolved.ValueCacheListener;

        // Must run after the fields above: it reads the ingest request and listener, and it throws a SessionException
        // when the request does not fit the stack, so a bad request fails the constructor.
        _InitializeValueCaches();
    }

    #endregion

    #region Fields

    // -- Configuration --

    // The current stack together with its lease counter (see RetirableStack). Non-readonly: Restart() swaps it for a new one.
    // Volatile so readers on other threads observe the swap without a lock. It also says who owns the stack: the session for
    // one a Restart factory built, the caller for the initial stack passed to the constructor.
    // Why a holder and not the bare stack: a stack that Restart replaced must not be disposed while a lock-free read still
    // parses on it, and the session does not want to keep it until Shutdown either. The holder counts the reads that use the
    // stack and disposes it when the last one ends (see the "Stack lifetime" remarks on the class).
    private volatile RetirableStack _Holder;

    // The current stack. Ingest and Restart read it under _ParseMutex (the swap happens under the same lock); a lock-free
    // read must NOT use it for a parse but take a lease (_TryLeaseStackForRead), otherwise the stack can be disposed under it.
    private Stack _Stack => _Holder.Stack;

    // Session-owned stacks that were replaced (or are being shut down) and not yet disposed: they wait for their last lease.
    // Normally empty; a stack is in it from before its retirement until its disposal ran. Copy-on-write like the other
    // registries, because the last releasing reader removes it without any lock (a lock here would be one a lease holder
    // could need). Shutdown waits for these within its time budget.
    private readonly SnapshotList<RetirableStack> _RetiredStacks = new();

    // Everything the disposal of a retired stack reported (an exception that escaped Stack.Dispose, the protocol shutdown
    // errors in Stack.ShutdownDiagnostics). Appended by whichever thread disposes, copy-on-write like _RetiredStacks, and
    // taken once by Shutdown, which throws it with the other cleanup errors. Never silently dropped.
    private volatile Exception[] _StackDisposeErrors = [];

    // Session-owned registry: shared across all stacks. Extracted from the
    // initial stack so that source and interface IDs remain stable across restarts.
    private readonly FrameInterfaceRegistry _FrameInterfaceRegistry;

    // -- State --

    private readonly SessionState _State = new();

    // Global atomic counters readable from any thread. Both advance together (one packet per frame) and are bumped
    // only AFTER the packet-id to frame mapping was recorded, so a reader that loads a count may look up every id
    // below it (see ReadPackets). Restart resets them to 0 and refills them while queries are disabled.
    private volatile int _PacketCount;
    private volatile int _FrameCount;

    // Number of source jobs that have not yet finished.
    // Transitions to 0 means all sources are done. Set to the full source count BEFORE any source job starts, so a
    // source that finishes immediately cannot drive it to 0 while others are still waiting to start.
    private volatile int _ActiveSourceCount;

    // -- Parse lock --

    // Monitor protects Stack parsing which mutates protocol-instance state.
    // One frame parse is long enough that waiters should kernel-wait, not spin.
    private readonly object _ParseMutex = new();

    // Globally unique PacketId counter (shared across all source threads).
    // Allocated INSIDE the parse mutex so that id order equals parse order (protocols rely on dense ascending first
    // parses) and so that a Restart, which resets the counter under the same lock, can never interleave with an
    // allocation. Outside Restart the next id equals the number of entries in _PacketFrames, which demands dense appends.
    private volatile int _NextPacketId;

    // Kernel-level gate that blocks source threads during a stack-swap reparse.
    // Initially signalled (open): Wait() returns immediately during normal parsing.
    // During Restart(), the gate is Reset() (closed) to park source threads,
    // then Set() (opened) after all frames have been re-parsed.
    // The ingest re-checks IsSet under _ParseMutex, because a source thread can pass Wait() just before
    // Restart closes the gate. Never disposed: a source thread that outlives Shutdown(timeout) may still
    // wait on it, and the event holds no kernel handle unless a thread actually blocked.
    private readonly ManualResetEventSlim _ParseGate = new(initialState: true);

    // Number of times a source thread found the gate closed under _ParseMutex and parked again instead of
    // allocating an id. Diagnostic only; tests use it to prove the re-check ran.
    private volatile int _IngestGateRefusals;

    // Set while no restart runs; Reset under _LifecycleLock when a restart begins, Set again in its finally.
    // Doubles as the "restart in progress" flag (read under _LifecycleLock) and as the event Shutdown waits on.
    // Never disposed, for the same reason as _ParseGate.
    private readonly ManualResetEventSlim _RestartIdle = new(initialState: true);

    // Set once the first Shutdown caller finished teardown. Later callers wait on it. Never disposed.
    private readonly ManualResetEventSlim _ShutdownDone = new(initialState: false);

    // Serialises Restart's swap with Shutdown. Not taken on the parse hot path.
    // Lock order: _LifecycleLock → _ParseMutex → ListenerSlot.FilterGate. Never acquire them in reverse.
    // _RegistryLock is a leaf below _LifecycleLock (see there). Subscriptions (TryAddListener, TryAddValueCache) take only
    // _RegistryLock, never this one: Shutdown joins listener threads while it holds this lock, so a listener callback that
    // subscribes must never block on it.
    private readonly object _LifecycleLock = new();

    // Serialises the writers of the registries and the publication of the shutdown flag and of the Idle -> started
    // transition. Held only for a few snapshot writes and flag reads: no user callback, no I/O, no thread start, no wait,
    // and no other lock is taken inside it (it is a leaf: _LifecycleLock -> _RegistryLock is the only nesting). Readers
    // never take it.
    // Threat avoided: an adder that checked "not shutting down" and then published after Shutdown took its snapshots would
    // leave a slot or job nobody cancels or joins (and a source added after Start would never run). Taking the check and
    // the publication under the same lock that publishes the flag closes that window.
    // Rejected alternative: _LifecycleLock itself (Shutdown holds it while joining listener threads, so a subscription made
    // from a listener callback would deadlock). Residual risk: none for the registries; an add that loses the race is
    // refused with InvalidPhase and drops its unpublished object.
    private readonly Lock _RegistryLock = new();

    // -- Shared stores --

    // PacketId → (FrameId, FrameSourceId), packed per group of 64 packets (≈ 0.25–0.5 B per packet for typical sessions,
    // 8 B at worst) instead of one 8-byte word per packet: the session keeps no packets, so this map is the only per-packet
    // memory that grows with the capture. Dense sequential appends under the parse mutex (single writer); readers are
    // lock-free; a miss is index >= Count. Its stores grow in 64 KiB chunks, below the large-object threshold, so growth
    // never copies a large array. Restart keeps it: the ids and their frames stay valid across a stack swap.
    private readonly PacketFrameMap _PacketFrames = new();

    // Roaring Bitmap index populated during the first parse (protocol presence, field groups). Null when IndexPackets
    // is false or before Start. Created by _StartInternal(); Restart replaces it with a fresh index built for the new
    // stack (the old one holds field ids of the retired stack). Volatile because listener threads read it while the
    // lifecycle thread swaps it.
    private volatile PacketIndex? _PacketIndex;

    // -- Source registry --

    // Every registered source with its job, in registration order. Registered while Idle, started by Start(), and
    // disposed by Shutdown(). Restart does not touch it: sources keep running and keep their entry.
    private readonly SnapshotList<FrameSourceEntry> _SourceEntries = new();

    // Public read-only views of frame sources for GetFrameSources().
    private readonly SnapshotList<FrameSourceInfo> _SourceInfos = new();


    // Source jobs started by Start(); empty before. Published only after every slot is filled, so a reader that sees
    // the array sees a complete one. A source stopped by TryUnsubscribe stays in it until Shutdown disposes it.
    private volatile Job[] _SourceJobs = [];

    // Random-access capable sources keyed by FrameSourceId, used by every re-parse (TryGetPacket, ReadPackets, Restart).
    // Copy-on-write: replaced as a whole by TryAddFrameSource and _RemoveRandomAccessSource (rare), read on every
    // packet read (hot). Readers take one snapshot per call and never lock; a lock around a dictionary would
    // put a lock on the path of every packet read. The writers do a read-modify-write of this field, so they run under
    // _RegistryLock: two concurrent adds would otherwise both start from the same table and the later store would drop the
    // other's source (its packets would become unreadable).
    private volatile RandomAccessSourceTable _RandomAccessSources = RandomAccessSourceTable.Empty;

    // -- Listener registry --

    // Active listener slots. SnapshotList for lock-free iteration by source threads.
    private readonly SnapshotList<ListenerSlot> _ListenerSlots = new();

    // Public read-only views of listener subscriptions for GetListeners().
    private readonly SnapshotList<ListenerInfo> _ListenerInfos = new();

    // -- Unified job list --

    // All jobs: source, listener, and user jobs are all registered here.
    // Lock-free: reads return the current snapshot, writes use CAS retry loop.
    private readonly SnapshotList<JobInfo> _AllJobs = new();

    // The user jobs of _AllJobs (TryAddJob) again as their own list, so Shutdown can cancel, join and dispose them without
    // guessing which entries of _AllJobs are not sources, listeners or value caches (those have registries of their own).
    // TryRemoveJob drops the entry together with the one in _AllJobs. Same publication rule as the other registries.
    private readonly SnapshotList<JobInfo> _UserJobs = new();

    // -- Value-cache registry --

    // Request and listener of SessionOptions; fixed for the session's lifetime. Restart rebuilds the cache from the request.
    private readonly ValueCacheRequest? _IngestRequest;
    private readonly IValueCacheListener? _IngestListener;

    // The ingest cache is written by the parse (source) thread during the first parse of each frame, under
    // _ParseMutex. Restart swaps it under the same lock, so a writer never sees a half-swapped cache. Volatile for readers.
    private volatile ValueCache? _IngestValueCache;

    // Public view of the ingest cache; created once and re-pointed at each new writer. Written under _ParseMutex
    // (Restart) or during construction.
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

    // Ensures only one thread executes the shutdown body. Others wait on _ShutdownDone.
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

    /// <summary>
    /// Ingest never retains a field tree. The first parse only has to advance protocol state and feed the packet index and
    /// the ingest value cache, which <see cref="FieldTreeMode.Skip"/> still does; building a field-body slab for a packet
    /// that is thrown away would cost allocation and time on the serialized parse path. Readers re-parse with
    /// <see cref="FieldTreeMode.Build"/> from the captured frame.
    /// </summary>
    private static FieldTreeMode _IngestFieldTreeMode => FieldTreeMode.Skip;

    /// <inheritdoc/>
    public bool IndexPackets { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Valid until the next <see cref="Restart"/>. A stack the session owns (built by a restart factory) is disposed as soon as
    /// a restart replaced it and the session's own in-flight reads ended; a caller that parses with this stack itself holds no
    /// lease and must read the property again after <c>OnStackChanged</c> instead of keeping the old reference.
    /// </remarks>
    public Stack Stack => _Stack;

    /// <inheritdoc/>
    public ReadOnlyValueCache? IngestValueCache
    {
        get
        {
            // One volatile load, so the null check and the view use the same writer even if Restart swaps it meanwhile.
            ValueCache? ingest = _IngestValueCache;
            if (ingest is null)
            {
                return null;
            }

            return ingest.AsReadOnlyView();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns a read-only view of the current immutable snapshot; the view is created once per snapshot, not per call.</remarks>
    public IReadOnlyList<ValueCacheInfo> GetValueCaches() => _ValueCacheInfos.ReadOnlyView;

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

        // Sources are only accepted before Start: the source list, the job array and the active-source count are fixed
        // when TryStart runs. This is the cheap early refusal; the registration below checks again under the registry
        // lock, because TryStart or Shutdown can run between this check and the publication.
        if (!_TryAcceptSource(out failure))
        {
            info = null;
            return false;
        }

        IFrameSource bound = _BindSource(source, addOptions);
        return _TryAddFrameSourceCore(bound, out info, out failure);
    }

    /// <summary>
    /// The phase rule for a new source: Idle, and shutdown not started. Only a snapshot, see
    /// <see cref="_TryAddFrameSourceCore"/> for the check that decides.
    /// </summary>
    private bool _TryAcceptSource([NotNullWhen(false)] out SessionFailure? failure)
    {
        if (_State.Phase != SessionPhase.Idle || _ShutdownStarted != 0)
        {
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Frame sources can only be added while the session is Idle.");
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Wraps stream sources (and RA sources when <see cref="FrameSourceAddOptions.CacheRandomAccess"/>)
    /// in <see cref="CachedFrameSource"/>. Does not wrap an existing cache.
    /// </summary>
    /// <remarks>
    /// Why: the session keeps no parsed packets, so every later read (TryGetPacket, ReadPackets, Restart) re-parses the
    /// captured frame and needs random access to it. A pure stream source cannot be re-read, so the wrapper retains
    /// its frames (references to the frame bytes, not copies). A random-access source can be re-read by itself, so by
    /// default it is not wrapped and costs no retained frames; the wrapper is opt-in through
    /// <see cref="FrameSourceAddOptions.CacheRandomAccess"/>.
    /// </remarks>
    private static IFrameSource _BindSource(IFrameSource source, FrameSourceAddOptions addOptions)
    {
        // An existing cache already provides random access; wrapping it again is rejected by CachedFrameSource.
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
        // Cancel is cooperative: the job's delegate must observe its token, otherwise Join waits for it to finish.
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
        // Same order as the listener path: publish the terminal status first so OnUnsubscribed, which runs on the
        // slot thread when the loop exits, observes Unsubscribed.
        info.SetStatus(SubscriptionStatus.Unsubscribed);
        slot.Cancel();

        // A never-started slot has no thread that could observe the cancellation; start it so the loop exits at once,
        // or, when the start itself fails, make sure the listener is still told. TryStart does the latter itself and
        // reports a failed start as a return value (the job is already Failed), so there is nothing left to catch.
        if (slot.Status == JobStatus.Pending)
        {
            _ = slot.TryStart(out _);
        }

        slot.Join();
        slot.EnsureOnUnsubscribed();

        // PullFill owns its writer and public info, so both go away. The NotifyOnly slot only observes the ingest
        // cache, which belongs to the session and stays: the ingest info remains registered, and only its
        // unsubscribe callback is cleared.
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

        // The Idle check is part of _StartInternal, made under the locks that also order the source and listener
        // registrations and Shutdown, so concurrent callers cannot both pass it: exactly one starts the session.
        return _StartInternal(out failure);
    }

    /// <inheritdoc/>
    public bool WaitForCompletion(TimeSpan? timeout = null)
    {
        // Snapshot of the started source jobs; empty before Start, which then completes immediately. The lock orders
        // this read after a TryStart that is publishing the array, and after a Restart that holds the lock for its whole
        // swap and re-parse. It is re-entrant, so Shutdown (which already holds it) can call this method.
        Job[] jobs;
        lock (_LifecycleLock)
        {
            jobs = _SourceJobs;
        }

        return _JoinWithinBudget(jobs, timeout, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Waits for every job to reach a terminal state within one time budget that started at
    /// <paramref name="startTimestamp"/>.
    /// </summary>
    /// <param name="jobs">The jobs to wait for, in order.</param>
    /// <param name="timeout">The whole budget; <see langword="null"/> waits without limit.</param>
    /// <param name="startTimestamp"><see cref="Stopwatch.GetTimestamp"/> value at which the budget began.</param>
    /// <returns><see langword="true"/> when every job finished within the budget.</returns>
    private static bool _JoinWithinBudget(ReadOnlySpan<Job> jobs, TimeSpan? timeout, long startTimestamp)
    {
        // No timeout: wait on each job's completion event (kernel wait, no polling).
        if (timeout is null)
        {
            foreach (Job job in jobs)
            {
                job.Join();
            }

            return true;
        }

        // One budget for the whole set (as documented on ISession): each Join gets what is left, so N slow jobs cannot
        // take N times the timeout. Shutdown passes the timestamp of its own start, so the sources and the user jobs it
        // waits for share one budget.
        TimeSpan budget = timeout.Value;
        foreach (Job job in jobs)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
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

        // Claim the restart under the lock: validate the phase, refuse a second concurrent restart, and reset
        // _RestartIdle so a Shutdown that arrives now waits for this restart instead of tearing the session down under it.
        lock (_LifecycleLock)
        {
            if (_ShutdownStarted != 0
                || _State.Phase is SessionPhase.ShuttingDown or SessionPhase.Idle)
            {
                throw new SessionException(
                    SessionErrorCode.InvalidPhase,
                    $"Restart() requires a Running or Stopped phase and no shutdown. Current phase: {_State.Phase}.");
            }

            if (!_RestartIdle.IsSet)
            {
                throw new InvalidOperationException("A restart is already in progress.");
            }

            _RestartIdle.Reset();
        }

        // The factory is user code, so it runs OUTSIDE _LifecycleLock: building a stack may be slow, and holding the lock
        // would stall every other lifecycle call. _RestartIdle (reset above) is what keeps Shutdown and a second Restart
        // out meanwhile; it is also why the factory must not call Shutdown, Restart, or Dispose (they would wait for this
        // restart). 'built' is owned here until _RestartCore hands it to the session.
        Stack? built = null;
        try
        {
            built = stackFactory(_FrameInterfaceRegistry)
                ?? throw new InvalidOperationException("The stack factory returned null.");

            // A different registry would invalidate the source and interface ids of every captured frame.
            if (!ReferenceEquals(built.FrameInterfaceRegistry, _FrameInterfaceRegistry))
            {
                throw new ArgumentException(
                    "The stack returned by the factory must use the FrameInterfaceRegistry that was passed to the factory. Do not create a new registry.",
                    nameof(stackFactory));
            }

            // Second critical section: the swap and the re-parse. Re-check shutdown, because Shutdown may have set its
            // flag while the factory ran (it then waits on _RestartIdle, which only this restart's finally sets).
            lock (_LifecycleLock)
            {
                if (_ShutdownStarted != 0)
                {
                    throw new SessionException(
                        SessionErrorCode.InvalidPhase,
                        "Restart was abandoned because shutdown has started.");
                }

                // Clears 'built' as soon as the session owns the stack, so the finally below disposes it
                // only when the restart failed before the swap.
                _RestartCore(ref built);
            }
        }
        finally
        {
            // Non-null only when the restart failed before the swap (see the ref parameter of _RestartCore).
            built?.Dispose();
            lock (_LifecycleLock)
            {
                // Wakes a Shutdown that waits for the restart to finish.
                _RestartIdle.Set();
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
    /// the thread simply waits before parsing. A source thread that passed the gate
    /// just before it closed finds it closed under <see cref="_ParseMutex"/>, allocates
    /// nothing, and parks again. After the re-parse finishes and the gate opens, source
    /// threads resume with the new stack and continue allocating sequential PacketIds
    /// from where the re-parse left off.
    /// </para>
    ///
    /// <para>
    /// <b>Failure before any change:</b>
    /// The ingest value cache and the packet index for the new stack are built before
    /// the phase, the gate, or the queries change. A request the new stack cannot satisfy
    /// (<see cref="SessionErrorCode.ValueCacheUnknownField"/>,
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>,
    /// <see cref="SessionErrorCode.ValueCacheInvalidRequest"/>) therefore throws with the
    /// session exactly as it was; the caller disposes the unused stack.
    /// </para>
    ///
    /// <para>
    /// <b>Frame ordering guarantee:</b>
    /// All previously parsed frames are re-parsed in ascending PacketId order
    /// (0 … N-1) via <see cref="_PacketFrames"/>. A mapping miss, a source
    /// that is not random-access, or a null <c>FrameById</c> throws
    /// <see cref="SessionException"/> with <see cref="SessionErrorCode.FrameUnavailable"/>.
    /// Queries stay disabled after that throw; listeners do not receive
    /// <see cref="NotifyFlags.NewPackets"/> for the partial rewrite.
    /// </para>
    /// </summary>
    /// <param name="built">
    /// The new stack, non-null on entry. Set to <see langword="null"/> in the same step that installs it
    /// as the session stack: from then on the session owns and disposes it, also when a later step throws.
    /// </param>
    private void _RestartCore(ref Stack? built)
    {
        // Caller holds _LifecycleLock and has already validated the registry and checked built for null.
        Stack newStack = built!; // Non-null: Restart checked the factory result before calling.

        // Build everything the new stack needs before touching session state, so a request the new
        // stack cannot satisfy leaves the session as it was (old stack, open gate, enabled queries).
        ValueCache? newIngest = _TryCreateIngestValueCache(newStack);
        PacketIndex? newIndex = _TryCreatePacketIndex(newStack);

        // ── Phase 1: Gate source threads and swap the stack ──────────────────
        _State.SetPhase(SessionPhase.Restarting);
        _NotifyAllListeners(NotifyFlags.PhaseChanged);

        // Close the gate. Source threads that finish their current NextFrame()
        // call block at _ParseGate.Wait(ct) until we Set() below.
        _ParseGate.Reset();

        // Disable queries while frames are re-parsed onto the new stack: the counters and the packet index are a partial
        // rewrite until the re-parse finishes, and a pull read would re-parse on a stack whose first parse is incomplete.
        _QueriesDisabled = true;

        // -1 until the swap block read the counter; the fail-closed path restores it only when it is known.
        int totalToReparse = -1;
        bool reparseCompleted = false;

        // The swap and the re-parse share one try/finally: whatever throws after the gate closed, the
        // finally reopens it, so source threads never stay parked.
        // The holder the swap replaced. Set inside the swap block and cleared by the retirement that follows it; the finally
        // below retires it when something throws between the two, so a replaced stack is never left without a retirement.
        RetirableStack? replaced = null;
        try
        {
            lock (_ParseMutex)
            {
                // Once we own the lock no source thread is inside the parse section. A source thread that
                // passed the gate before Reset() and waits for this lock re-checks the gate under the lock
                // and parks again (see _TryIngestFrameUnderLock), so it cannot allocate from the reset counter.
                totalToReparse = _NextPacketId;

                // Swap the protocol stack. Session owns factory-created stacks from here on.
                // The old stack is NOT disposed here. _QueriesDisabled (set above) only stops reads that start later: a
                // lock-free re-parse (TryGetPacket, ReadPackets, a listener or value-cache pull) that passed that check
                // before it was set may still be parsing on the old stack, and Stack.Dispose would shut its protocols
                // down and release the settings locks under it. Those reads hold a lease on the old holder, so the
                // retirement right after this block disposes the stack when the last of them leaves (at once if none is
                // inside). Rejected: disposing here (readers do not take this lock, so it only stalls ingest for the
                // length of the protocol shutdowns) and keeping the old stack until Shutdown (memory, delayed OnShutdown).
                replaced = _Holder;
                _Holder = new RetirableStack(newStack, ownedBySession: true, _OnStackDisposed);
                built = null;

                // Old parse results are discarded (the session does not retain packets).
                // Do NOT clear the mapping — it stores frame order needed for re-parse
                // and the PacketId → (FrameId, SourceId) data remains valid.

                // Fresh packet index for the new stack's field definitions (built above).
                _PacketIndex = newIndex;

                // Abandon previous writers, then install the ingest cache built above.
                // Runtime PullFill slots stay registered; they rebind on StackChanged.
                _IngestValueCache?.Abandon();
                foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
                {
                    if (slot.FillMode == ValueCacheFillMode.PullFill)
                    {
                        slot.AbandonWriter();
                    }
                }

                _IngestValueCache = newIngest;
                if (newIngest is not null)
                {
                    if (_IngestInfo is null)
                    {
                        _IngestInfo = _CreateIngestInfo(newIngest);
                        _ValueCacheInfos.Add(_IngestInfo);
                    }
                    else
                    {
                        _IngestInfo.SetWriter(newIngest);
                    }

                    foreach (ValueCacheSlot slot in _ValueCacheSlots.Current)
                    {
                        if (slot.FillMode == ValueCacheFillMode.NotifyOnly)
                        {
                            slot.BindWriter(newIngest);
                        }
                    }
                }

                // Reset counters so re-parse fills them from 0.
                Interlocked.Exchange(ref _PacketCount, 0);
                Interlocked.Exchange(ref _FrameCount, 0);
                Interlocked.Exchange(ref _NextPacketId, 0);
            }

            // Retire the replaced stack now, outside the parse mutex and before the (possibly long) re-parse: with no read in
            // flight it is disposed right here, otherwise by the last reader that leaves. Not under any lock a lease holder
            // could need: Stack.Dispose runs arbitrary protocol shutdown code.
            RetirableStack retiring = replaced!; // Non-null: the swap block above assigned it before it ended.
            replaced = null;
            _RetireStack(retiring);

            // ── Phase 2 + 3: Re-parse and resume ─────────────────────────────────
            _ReparseAllFrames(totalToReparse);
            reparseCompleted = true;
        }
        finally
        {
            // A throw inside the swap block (after the holder was replaced) skipped the retirement above.
            if (replaced is not null)
            {
                _RetireStack(replaced);
            }

            // Leave Restarting for the phase that matches the sources: Running while any source still ingests, Stopped
            // when they all ended during the restart (their completion could not set the phase while Restarting).
            // This holds on the failure path too; there only the queries stay disabled.
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
                // Fail closed: the old stack is already retired (disposed once its last read ended) and counters/mapping
                // are a partial rewrite. Do not re-enable queries or announce NewPackets.
                // Restore _NextPacketId so a still-running source cannot reuse 0 … N-1.
                if (totalToReparse >= 0)
                {
                    Interlocked.Exchange(ref _NextPacketId, totalToReparse);
                }

                NotifyFlags flags = NotifyFlags.PhaseChanged;
                if (!sourcesStillActive)
                {
                    flags |= NotifyFlags.AllSourcesCompleted;
                }

                _NotifyAllListeners(flags);
            }

            // Reopen last, on success and on failure alike, so no source thread stays parked. Reopening only after the
            // filter re-bind and the cursor reset means resumed ingest cannot announce packets to stale cursors.
            _ParseGate.Set();
        }
    }

    /// <summary>
    /// Re-parses frames for PacketIds 0 … <paramref name="count"/>-1 using the
    /// current <see cref="_Stack"/>. Reads frame data from random-access sources
    /// via <see cref="_PacketFrames"/> (stream sources are wrapped at add time).
    /// A mapping miss, non-random-access source, or null <c>FrameById</c> throws
    /// <see cref="SessionException"/> with <see cref="SessionErrorCode.FrameUnavailable"/>.
    ///
    /// <para>
    /// Called while the <see cref="_ParseGate"/> is closed, so no source thread
    /// is parsing concurrently. Each frame is still parsed under the ingest lock
    /// during stack-swap reparse: this is the first parse on the new stack, so it needs the same exclusive, ordered
    /// execution as ingest (protocols record their replay state now), and the lock also excludes a source thread that
    /// passed the gate just before it closed.
    /// </para>
    /// </summary>
    private void _ReparseAllFrames(int count)
    {
        // Snapshot the random-access sources once; frames are read 64 packets at a time, one batch call per source.
        // Reading in batches lets a file-backed source walk its index and file forward instead of paying one random
        // FrameById per id (the same batching ReadPackets uses). The batch buffers are on the stack: no allocation.
        RandomAccessLookup sources = new(_RandomAccessSources);
        FrameBatch frames = default;
        Span<FrameBatchMiss> misses = stackalloc FrameBatchMiss[_FrameBatchSize];

        // The re-parse drops every packet, so one packet is recycled through the whole loop instead of allocating one per
        // frame (restarting a capture of millions of frames would otherwise create millions of garbage packets). Same
        // reasoning as the ingest loop: the packet is private to this thread, never published, and a rejected recycle just
        // falls back to a fresh packet inside the parse.
        Packet? recycle = null;
        for (int start = 0; start < count; start += _FrameBatchSize)
        {
            int length = Math.Min(_FrameBatchSize, count - start);
            Span<Frame?> chunk = frames;
            chunk = chunk[..length];
            _ReadFrameBatch(ref sources, start, chunk, misses[..length]);

            // Parse the chunk strictly in ascending id order. Stateful protocols need dense ascending first parses, so
            // the batch read may be reordered internally but the parse below must not be.
            for (int lane = 0; lane < length; lane++)
            {
                PacketId originalId = new(start + lane);

                // Fail closed on the first missing frame: a gap cannot be skipped because ids are not compressed.
                if (chunk[lane] is not Frame frame)
                {
                    throw _RestartFrameError(originalId, misses[lane]);
                }

                // Keep the original PacketId so mapping, index, and TryGetPacket stay aligned. The packet is not kept: the
                // parse exists for its side effects (protocol state, packet index, ingest value cache), so the returned
                // packet only goes back in as the recycle packet of the next frame.
                recycle = _ParseFrameUnderLock(frame, packetId: originalId, recycle);

                // Counters grow as the re-parse proceeds; queries are disabled, so nobody observes the partial value.
                Interlocked.Increment(ref _PacketCount);
                Interlocked.Increment(ref _FrameCount);
            }
        }

        // Parses used original ids, so _AllocateNextPacketId never advanced. Resume new ingest at count.
        Interlocked.Exchange(ref _NextPacketId, count);
    }

    /// <summary>
    /// The <see cref="SessionErrorCode.FrameUnavailable"/> error <see cref="_ReparseAllFrames"/> throws for a packet
    /// whose frame could not be read; <paramref name="miss"/> selects the reason text.
    /// </summary>
    private static SessionException _RestartFrameError(PacketId id, FrameBatchMiss miss)
    {
        string reason = miss switch
        {
            FrameBatchMiss.Unmapped => "mapping slot is missing",
            FrameBatchMiss.NotRandomAccess => "source is not random-access",
            _ => "FrameById returned null",
        };
        return new SessionException(
            SessionErrorCode.FrameUnavailable,
            FormattableString.Invariant(
                $"Restart cannot re-parse PacketId {id.Value.ToString(CultureInfo.InvariantCulture)}: {reason}."));
    }

    /// <summary>
    /// Re-binds every listener filter to the current <see cref="_Stack"/> after a stack swap.
    ///
    /// <para>
    /// <see cref="PacketFilter.TryDerive"/> produces a new instance from the same parsed expression with
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
            // The filter gate keeps a listener thread that is mid-way through a matching read from seeing the filter
            // swapped under it (the read holds the same gate). Lock order: _LifecycleLock is held, FilterGate comes last.
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

    // -- Stack leases and retirement --

    /// <summary>
    /// Takes a lease on the current stack for a lock-free parse, or refuses when the session cannot serve a read: a restart
    /// is rewriting packets, a restart failed closed, or shutdown disabled the queries. The lease keeps the stack from being
    /// disposed until it is disposed itself.
    /// </summary>
    /// <remarks>
    /// Order matters. The lease is taken first and <see cref="_QueriesDisabled"/> is read after it. Restart sets
    /// <see cref="_QueriesDisabled"/> before it swaps and retires, and the retirement is an interlocked operation after that
    /// write. So either the lease was taken before the retirement (the retirement sees it and leaves the disposal to the
    /// release), or after (the lease is refused and the loop reads the newer holder). A reader that took a lease on the old
    /// stack and still sees the queries enabled started before the restart disabled them, and its parse on the old stack is
    /// consistent. A reader that sees them disabled gives the lease back at once. The loop ends after at most one pass per
    /// restart that overtook it: a refused holder was replaced before it was retired, except at shutdown, where the current
    /// holder is retired and the read must fail instead of looping.
    /// </remarks>
    /// <param name="lease">The lease; dispose it when the parse is finished. The default lease when refused.</param>
    /// <returns><see langword="false"/> when queries are disabled; the caller treats it exactly like a refused read.</returns>
    private bool _TryLeaseStackForRead(out StackLease lease)
    {
        while (true)
        {
            RetirableStack holder = _Holder;
            if (holder.TryAcquire(out lease))
            {
                if (!_QueriesDisabled)
                {
                    return true;
                }

                // Queries went off between the caller's early check and the lease: nothing may be parsed any more.
                lease.Dispose();
                lease = default;
                return false;
            }

            // Refused: the holder is retired. Replaced by a restart (look again), or the current one at shutdown (give up).
            if (ReferenceEquals(holder, _Holder))
            {
                lease = default;
                return false;
            }
        }
    }

    /// <summary>
    /// Registers a replaced stack and retires it: it is disposed now when no read uses it, otherwise by the last of them.
    /// </summary>
    private void _RetireStack(RetirableStack retiring)
    {
        // Registered before the retirement, because the disposal removes the entry and may run inside Retire (no lease held) or
        // on a reader thread at any moment after it: the removal must never come first, or the entry would stay forever.
        // A stack the caller owns is not registered: the session never disposes it, so nothing is waiting for it.
        if (retiring.OwnedBySession)
        {
            _RetiredStacks.Add(retiring);
        }

        retiring.Retire();
    }

    /// <summary>
    /// Called once by the thread that disposed a session-owned stack: forgets the stack and keeps what its disposal reported
    /// until <see cref="Shutdown"/> throws it. Must not block (it can run on a reader thread that just finished a read).
    /// </summary>
    private void _OnStackDisposed(RetirableStack disposed, Exception[] errors)
    {
        _ = _RetiredStacks.Remove(disposed);
        if (errors.Length == 0)
        {
            return;
        }

        // Copy-on-write append with a CAS retry, like SnapshotList: concurrent disposals (a reader and a restart) lose nothing.
        // A lock would be one more thing a lease holder could wait on; disposal errors are rare, so the copy costs nothing.
        Exception[] current;
        Exception[] updated;
        do
        {
            current = _StackDisposeErrors;
            updated = new Exception[current.Length + errors.Length];
            current.CopyTo(updated, 0);
            errors.CopyTo(updated, current.Length);
        }
        while (Interlocked.CompareExchange(ref _StackDisposeErrors, updated, current) != current);
    }

    /// <summary>
    /// Shutdown part: retires the current stack and waits for every retired stack to be disposed within the time budget that
    /// began at <paramref name="startTimestamp"/>, then disposes the ones whose last read did not end in time.
    /// </summary>
    /// <remarks>
    /// Why wait at all: a read of a user thread may still be inside a parse on a stack (shutdown disabled the queries, so no
    /// new one starts). Why only within the budget: a read that never ends (a protocol that hangs) would otherwise hang the
    /// shutdown, just like a source or a job that never ends would without a timeout (without a timeout the wait is
    /// unbounded here too, like the joins). Residual risk after the budget: the stack is disposed while such a read is still
    /// inside it; its settings reads throw <see cref="ObjectDisposedException"/> (recorded as a parse error of that packet),
    /// and a protocol that released its own state in <c>OnShutdown</c> may produce wrong content for that one packet. The
    /// caller chose the timeout; the alternative (leaving the stack undisposed) would leak protocol resources and settings
    /// locks. A source thread that outlived the budget is not waited for.
    /// </remarks>
    private void _RetireStacksForShutdown(TimeSpan? timeout, long startTimestamp)
    {
        // The current stack is retired like a replaced one; afterwards no lease can be taken (reads are disabled anyway).
        _RetireStack(_Holder);

        // The snapshot array is complete: a restart cannot run (Shutdown waited for it), so nothing is added to the list from
        // here on; entries only leave it, as their disposal runs.
        foreach (RetirableStack stack in _RetiredStacks.CurrentSnapshot)
        {
            TimeSpan remaining = Timeout.InfiniteTimeSpan;
            if (timeout is TimeSpan budget)
            {
                remaining = budget - Stopwatch.GetElapsedTime(startTimestamp);
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }
            }

            if (!stack.WaitUntilDisposed(remaining))
            {
                stack.DisposeNow();
            }
        }
    }

    /// <summary>
    /// Number of replaced session-owned stacks that wait for their last read to end. Diagnostic: zero when no read was inside
    /// a stack at the moment it was replaced; tests use it to prove retired stacks do not accumulate.
    /// </summary>
    internal int RetiredStackCount => _RetiredStacks.Current.Length;

    /// <summary>The holder of the current stack. Diagnostic; tests take a lease on it to pin a stack at a known point.</summary>
    internal RetirableStack StackHolder => _Holder;

    /// <inheritdoc/>
    public void Shutdown(TimeSpan? timeout = null)
    {
        // Exactly one caller runs the teardown. The flag is published under the registry lock, the lock every registration
        // holds while it re-checks the flag and publishes its slot or job: whatever a registration published before this
        // point is in the snapshots the teardown takes next, and whatever comes after is refused. A plain CAS would leave
        // an adder that already passed its check free to publish after the snapshots.
        // Later callers block until the teardown finished (kernel wait, no polling), ignore their own timeout, and do not
        // see the first caller's AggregateException.
        bool first;
        lock (_RegistryLock)
        {
            first = _ShutdownStarted == 0;
            if (first)
            {
                _ShutdownStarted = 1;
            }
        }

        if (!first)
        {
            _ShutdownDone.Wait();
            return;
        }

        try
        {
            // Do not hold _LifecycleLock while waiting. Restart sets the event in its own lock block after
            // it released the swap, so this wait cannot deadlock with it. A restart that has not begun yet
            // sees _ShutdownStarted under the lock and refuses.
            _RestartIdle.Wait();
            lock (_LifecycleLock)
            {
                _ShutdownUnderLock(timeout);
            }
        }
        finally
        {
            // Also when teardown throws, so concurrent Shutdown callers are never left waiting.
            _ShutdownDone.Set();
        }
    }

    /// <summary>
    /// Tears the session down. Caller holds <see cref="_LifecycleLock"/> and has waited
    /// until no restart is in progress.
    /// </summary>
    private void _ShutdownUnderLock(TimeSpan? timeout)
    {
        // One time budget for everything shutdown waits for under a timeout: the source jobs and the user jobs share it.
        long startTimestamp = Stopwatch.GetTimestamp();
        Job[] sourceJobs = _SourceJobs;

        // The snapshots of the user jobs and the slots are complete: the shutdown flag was published under _RegistryLock
        // before this method started, every registration re-checks it under that lock before it publishes, and a
        // registration that was past the check had finished publishing when the flag was set. Nothing can be added to these
        // registries from here on, so a single pass over each snapshot cancels and joins everything there is.
        ReadOnlySpan<JobInfo> userJobInfos = _UserJobs.Current;
        Job[] userJobs = new Job[userJobInfos.Length];
        for (int i = 0; i < userJobs.Length; i++)
        {
            userJobs[i] = userJobInfos[i].Job;
        }

        // Allow Shutdown() on Stopped phase so that listener slots are properly
        // cancelled and OnUnsubscribed is called. The session may transition to
        // Stopped automatically when all sources finish, but listener threads
        // still need explicit cancellation. The step into ShuttingDown is conditional so it cannot
        // overwrite a Stopped that the last source thread publishes at the same moment.
        bool alreadyStopped = !_State.TryEnterShuttingDown();

        if (!alreadyStopped)
        {
            _NotifyAllListeners(NotifyFlags.PhaseChanged | NotifyFlags.ShuttingDown);
        }
        else
        {
            // Notify ShuttingDown to any still-running listener slots.
            _NotifyAllListeners(NotifyFlags.ShuttingDown);
        }

        // Cancel all source jobs and user jobs. They observe this via their CancellationToken.
        foreach (Job job in sourceJobs)
        {
            job.Cancel();
        }

        foreach (Job job in userJobs)
        {
            job.Cancel();

            // A job TryAddJob registered but has not started yet would stay Pending, and a Pending job never reaches a
            // terminal state, so the join below would wait for a thread that does not exist. Start it (the start is
            // guarded, TryAddJob's own start becomes a no-op): its work runs with a token that is already cancelled.
            if (job.Status == JobStatus.Pending)
            {
                _ = job.TryStart(out _);
            }
        }

        // Wait for source jobs and then user jobs to finish (up to timeout if specified, one budget for both).
        // If timeout expired, sources may still be finishing their current frame parse.
        // Continue with teardown regardless — the caller chose this timeout and can
        // inspect job states via GetJobs() to see which jobs are still running.
        _ = _JoinWithinBudget(sourceJobs, timeout, startTimestamp);
        _ = _JoinWithinBudget(userJobs, timeout, startTimestamp);

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

        // Cancel all listener slots first, then join them one by one: the cancellations run in parallel, so the
        // slowest callback bounds the wait instead of the sum.
        ReadOnlySpan<ListenerSlot> listeners = _ListenerSlots.Current;
        foreach (ListenerSlot slot in listeners)
        {
            slot.Cancel();
        }
        foreach (ListenerSlot slot in listeners)
        {
            // If the slot is still Pending (a TryAddListener that published the slot but has not started it yet, or a
            // slot added in Idle that TryStart never saw), start it so RunLoop can observe cancellation and exit
            // cleanly. Job.Start uses a CAS guard against double-start, so this is safe even if TryAddListener's
            // Start races with us. A failed start has already told the listener (TryStart).
            if (slot.Status == JobStatus.Pending)
            {
                _ = slot.TryStart(out _);
            }

            slot.Join();
            slot.EnsureOnUnsubscribed();
        }

        // Value-cache slots follow the listeners: their threads must be gone before the writers are abandoned.
        _TearDownValueCacheSlots();

        // Abandon the writers: any later Record throws instead of writing (a source thread that outlived a Shutdown
        // timeout may still be parsing into the ingest cache).
        _IngestValueCache?.Abandon();
        foreach (ValueCacheSlot cacheSlot in _ValueCacheSlots.Current)
        {
            if (cacheSlot.FillMode == ValueCacheFillMode.PullFill)
            {
                cacheSlot.AbandonWriter();
            }
        }

        // Listeners have drained; from here on no packet reads are served (QueriesEnabled becomes false for good).
        _QueriesDisabled = true;

        // Transition to final state.
        _State.SetPhase(SessionPhase.Stopped);

        // Dispose all jobs and listener slots.
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
        foreach (Job job in userJobs)
        {
            try
            {
                job.Dispose();
            }
            catch (Exception ex) { (cleanupErrors ??= []).Add(ex); }
        }
        foreach (ListenerSlot slot in listeners)
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

        // Dispose all frame sources. The session owns every source added
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

        // Dispose the current stack if the session owns it (factory-created via Restart), and every replaced stack whose last
        // read has not ended yet: wait for those reads within the remaining budget, then dispose anyway. A stack the caller
        // owns (the initial one) is only marked retired.
        _RetireStacksForShutdown(timeout, startTimestamp);

        // Everything the disposals reported, from this shutdown and from the restarts before it (taken once, so a later
        // shutdown call, which does not run the teardown again, never repeats it). Each disposal appended before it signalled
        // that it was done, and this method waited for every signal.
        Exception[] stackDisposeErrors = Interlocked.Exchange(ref _StackDisposeErrors, []);
        if (stackDisposeErrors.Length > 0)
        {
            (cleanupErrors ??= []).AddRange(stackDisposeErrors);
        }

        // The parse gate is deliberately not disposed: with a timeout, a cancelled source thread may still
        // return from NextFrame after this point and wait on the gate. Its cancelled token then makes the
        // wait throw OperationCanceledException, so the job ends Cancelled instead of failing on a disposed event.

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
        // that handles true concurrent Dispose() calls safely. Setting the flag first makes the registration members
        // (TryAdd*, TryStart, Restart, TryUnsubscribe, ...) throw SessionErrorCode.Disposed while the shutdown below
        // runs; Shutdown itself and the read members do not check it.
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
    /// Registers a new frame source in the registry and creates its job, but only if the session is still Idle: the
    /// decision and the publication happen under <see cref="_RegistryLock"/>, which <see cref="TryStart"/> also takes to
    /// leave Idle. Used by both <see cref="TryAddFrameSource(IFrameSource, out FrameSourceInfo?, out SessionFailure?)"/>
    /// overloads after the source was bound to its cache wrapper.
    /// </summary>
    private bool _TryAddFrameSourceCore(
        IFrameSource source,
        [NotNullWhen(true)] out FrameSourceInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        // The name is user code (the source's property), so it is read before the lock is taken.
        string uiName = source.UiName;

        lock (_RegistryLock)
        {
            // The decision. TryStart snapshots the source list and leaves Idle under this same lock, so a source that passes
            // here is either in that snapshot (and started) or refused; it can never be added to a session that is already
            // running and then sit in the list without a thread. Shutdown's flag is published under the lock as well.
            if (!_TryAcceptSource(out failure))
            {
                info = null;
                return false;
            }

            // The job id is taken before the source is registered, so an exhausted id range leaves nothing behind.
            JobId jobId = _State.AllocateJobId();
            FrameSourceId sourceId = _Stack.FrameInterfaceRegistry.RegisterSource(source);
            FrameSourceInfo sourceInfo = _Stack.FrameInterfaceRegistry.GetSource(sourceId)!; // Non-null: the id was just registered above.

            // The job delegate captures the source and its info; the job thread runs _RunSourceLoop with them. The job is
            // only created here and started by _StartInternal.
            Job job = new(
                jobId,
                uiName,
                $"Source: {uiName}",
                ct => _RunSourceLoop(source, sourceInfo, ct),
                _OnJobStatusChanged);

            // Hold the entry reference so we can also register its JobInfo in the
            // unified job list without creating a second JobInfo wrapper.
            FrameSourceEntry entry = new(sourceInfo, source, job);

            // Wire the convenience API: FrameSourceInfo.Stop() → TryUnsubscribe(job). Captured reference is the entry's
            // JobInfo (same reference stored in _AllJobs). Done before the publication so a caller that finds the info in
            // GetFrameSources() never sees a source that cannot be stopped.
            JobInfo entryJobInfo = entry.JobInfo;
            sourceInfo.RegisterStopCallback(() => TryUnsubscribe(entryJobInfo, out _));

            // Register random-access capable sources for every re-parse (TryGetPacket, ReadPackets, Restart).
            // Copy-on-write: build a new table with the added entry and publish it with one volatile store. The
            // read-modify-write is safe because every writer of the table holds _RegistryLock.
            if (source is IRandomAccessFrameSource raSource)
            {
                _RandomAccessSources = _RandomAccessSources.With(sourceId, raSource);
            }

            _SourceEntries.Add(entry);
            _SourceInfos.Add(sourceInfo);
            _AllJobs.Add(entryJobInfo);
            info = sourceInfo;
        }

        // Notify existing listeners about the new source (outside the lock: it only raises flags, but nothing needs the lock).
        _NotifyAllListeners(NotifyFlags.SourceAdded);
        failure = null;
        return true;
    }

    /// <summary>
    /// Leaves Idle and launches all source and listener jobs. Called by <see cref="TryStart"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> when the session already started or
    /// started shutting down; exactly one concurrent caller gets <see langword="true"/>.
    /// </returns>
    private bool _StartInternal([NotNullWhen(false)] out SessionFailure? failure)
    {
        Job[] jobs;
        lock (_LifecycleLock)
        {
            // The decision, the snapshot of the registered sources and the step out of Idle form one unit under the
            // registry lock. Why: a second TryStart must not run the start sequence again (it would reset the active-source
            // count), a TryAddFrameSource must either be in this snapshot or be refused, and a listener added now must
            // see the new phase and start its own thread (one added before is started by _StartListenerSlots below).
            lock (_RegistryLock)
            {
                if (_ShutdownStarted != 0 || _State.Phase != SessionPhase.Idle)
                {
                    failure = new SessionFailure(SessionErrorCode.InvalidPhase, "TryStart requires the Idle phase.");
                    return false;
                }

                // Collect the job of every registered source into one array and publish it once complete. It is published
                // here, under the lock, because the unsubscribe of a source decides under the same lock whether a Pending
                // job is still free to be removed or already belongs to this start.
                ReadOnlySpan<FrameSourceEntry> entries = _SourceEntries.Current;
                jobs = new Job[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    jobs[i] = entries[i].Job;
                }

                _SourceJobs = jobs;

                // No sources: nothing will ever run, so the session is Stopped at once.
                SessionPhase started = jobs.Length == 0
                    ? SessionPhase.Stopped
                    : SessionPhase.Running;
                _State.SetPhase(started);
            }

            // Index and query switch are set up before any job exists, so the first parsed frame already finds them.
            _PacketIndex = _TryCreatePacketIndex(_Stack);
            _QueriesDisabled = false;

            // The full count is set before the first job starts: a source that ends at once must not see 0 while
            // others have not even started.
            Interlocked.Exchange(ref _ActiveSourceCount, jobs.Length);

            // Listeners still start without sources so they receive the AllSourcesCompleted and PhaseChanged
            // notifications.
            if (jobs.Length == 0)
            {
                _StartListenerSlots();
                _NotifyAllListeners(NotifyFlags.AllSourcesCompleted | NotifyFlags.PhaseChanged);
                failure = null;
                return true;
            }

            // Listeners start before sources (see _StartListenerSlots) so the first NewPackets flag finds a thread
            // that is ready to receive it.
            _NotifyAllListeners(NotifyFlags.PhaseChanged);
            _StartListenerSlots();
        }

        // Source threads start outside the lifecycle lock: the phase is Running, the job array is published, and the
        // lock is not needed by the source loop.
        for (int i = 0; i < jobs.Length; i++)
        {
            if (!jobs[i].TryStart(out _))
            {
                // The job is already Failed with its exception recorded (JobInfo.FailureException for GetJobs()) and its
                // thread never ran, so the source loop's finally block will not run. Account for the source here, exactly
                // as that finally block would, so MorePacketsExpected and the Stopped transition do not wait for a source
                // that will never run. The start failure itself is not rethrown.
                int remaining = Interlocked.Decrement(ref _ActiveSourceCount);
                if (remaining == 0)
                {
                    _NotifyAllListeners(NotifyFlags.AllSourcesCompleted);

                    // Conditional, like the source loop: a Shutdown or Restart that published its own phase meanwhile keeps it.
                    if (_State.TryTransition(SessionPhase.Running, SessionPhase.Stopped))
                    {
                        _NotifyAllListeners(NotifyFlags.PhaseChanged);
                    }
                }
            }
        }

        failure = null;
        return true;
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
                // A thread that cannot be started is reported by the job itself: the failure is recorded on the slot's job
                // (JobInfo.FailureException, status Failed, JobStatusChanged raised) and the listener was told it is
                // unsubscribed. That slot receives no notifications; other listeners and the sources continue unaffected,
                // so the result is not needed here.
                _ = slot.TryStart(out _);
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
                // Same contract as the listener slots: a failed start is recorded on the slot's job (status Failed,
                // JobInfo.FailureException) and the listener was told it is unsubscribed (OnUnsubscribed). Other slots and
                // the sources are unaffected, so the result is not needed here.
                _ = slot.TryStart(out _);
            }
        }
    }

    /// <summary>
    /// Creates a packet index for <paramref name="stack"/> when <see cref="IndexPackets"/> is enabled; otherwise null.
    /// </summary>
    private PacketIndex? _TryCreatePacketIndex(Stack stack)
    {
        if (!IndexPackets)
        {
            return null;
        }

        return new PacketIndex(stack);
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
    ///   <item>Wait on <see cref="_ParseGate"/> (closed only while Restart swaps the stack).</item>
    ///   <item>Parse each frame under the shared <see cref="_ParseMutex"/>.</item>
    ///   <item>Record the PacketId -> FrameId mapping, then increment the global counters (same lock section).</item>
    ///   <item>Keep the ingest packet to recycle it for the next frame on this source thread.</item>
    ///   <item>Set <see cref="NotifyFlags.NewPackets"/> on all listeners, outside the lock.</item>
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

            // Ingest skip-parses and does not retain the packet, and no listener ever sees it (listeners pull and
            // re-parse from the frame). So one Packet per source thread is reused for every frame instead of allocating
            // one per frame on the serialized parse path. It is thread-private: recycling overwrites the packet in place,
            // so it must belong to exactly one thread.
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
                // The gate may close between Wait and the parse lock (Restart resets it before it takes
                // the lock). The ingest then refuses without allocating an id, and this thread parks on
                // the gate again; the same frame is parsed with the new stack once the gate reopens.
                Packet? packet;
                do
                {
                    _ParseGate.Wait(ct);
                }
                while (!_TryIngestFrameUnderLock(capturedFrame, sourceInfo.Id, ingestRecycle, out packet));

                // Keep the packet for the next frame's recycle. Announce AFTER the id, its map entry and the counters
                // are published (done inside the ingest), so a listener that wakes finds every id below PacketCount.
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

                // Transition to Stopped only if the phase is still Running (Shutdown or Restart may have changed the phase;
                // they own it from then on). One compare-exchange instead of a check followed by a store: with the two
                // steps apart, a ShuttingDown or Restarting published in between would be overwritten by Stopped, and
                // listeners and TryAdd* members would see a phase that is wrong while that lifecycle step still runs.
                if (_State.TryTransition(SessionPhase.Running, SessionPhase.Stopped))
                {
                    _NotifyAllListeners(NotifyFlags.PhaseChanged);
                }
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="ValueCacheSlot"/> with its public view and publishes both, unless shutdown has started. A
    /// PullFill slot gets its own public <see cref="ValueCacheInfo"/> and <see cref="ValueCacheId"/>; a NotifyOnly slot
    /// attaches to the existing ingest info and allocates neither.
    /// </summary>
    /// <remarks>
    /// Everything the slot and its info need (job, writer, unsubscribe callback, the link between slot and info) is set
    /// before the first registry sees them, and the three registries are written together under
    /// <see cref="_RegistryLock"/> together with the shutdown check. A concurrent <c>TryUnsubscribe</c> or
    /// <c>GetValueCaches</c> therefore never finds a half-built subscription, and Shutdown never misses one.
    /// The NotifyOnly attach (construction time only, before the session is shared) changes the already registered ingest
    /// info before the check; it cannot be refused, because no shutdown can run during the constructor.
    /// </remarks>
    /// <returns><see langword="false"/> with <see cref="SessionErrorCode.InvalidPhase"/> when shutdown has started.</returns>
    private bool _TryRegisterValueCacheSlot(
        IValueCacheListener listener,
        ValueCacheRequest request,
        ValueCache writer,
        ValueCacheFillMode fillMode,
        [NotNullWhen(true)] out ValueCacheSlot? registered,
        out bool startNow,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        bool attachesToIngest = fillMode == ValueCacheFillMode.NotifyOnly && _IngestInfo is not null;
        startNow = false;

        // Allocate ids before any slot exists, so an exhausted id range leaves nothing behind.
        ValueCacheId cacheId = attachesToIngest ? ValueCacheId.Invalid : _State.AllocateValueCacheId();
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
            _TryLeaseStackForRead,
            _OnJobStatusChanged);
        Action unsubscribe = () =>
        {
            _ = TryUnsubscribe(slot.Info, out _);
        };

        ValueCacheInfo info;
        if (attachesToIngest)
        {
            // The ingest cache already has a public view (and an id); the listener job attaches to it.
            info = _IngestInfo!; // Non-null: attachesToIngest requires _IngestInfo is not null.
            info.Job = slot.Info;
            info.SetStatus(SubscriptionStatus.Active);
        }
        else
        {
            info = new ValueCacheInfo
            {
                Id = cacheId,
                UiName = listener.UiName,
                Job = slot.Info,
            };
            info.SetWriter(writer);
        }

        info.UnsubscribeCallback = unsubscribe;
        slot.ValueCacheInfo = info;

        lock (_RegistryLock)
        {
            // Shutdown publishes its flag under this lock before it snapshots the registries; a registration that is
            // refused here drops its unpublished slot, and one that is published here is in the snapshot.
            if (_ShutdownStarted != 0)
            {
                failure = new SessionFailure(
                    SessionErrorCode.InvalidPhase,
                    "Subscriptions cannot be added while the session is shutting down.");
                registered = null;
                slot.Dispose();
                return false;
            }

            if (!attachesToIngest)
            {
                _ValueCacheInfos.Add(info);
            }

            _ValueCacheSlots.Add(slot);
            _AllJobs.Add(slot.Info);

            // Read in the same critical section as the publication, like the listener registration: TryStart leaves Idle
            // under this lock too, so the slot is either in TryStart's snapshot (phase still Idle) or started by the caller
            // (phase already left Idle), never neither.
            startNow = _State.Phase != SessionPhase.Idle;
        }

        registered = slot;
        failure = null;
        return true;
    }

    /// <summary>
    /// Resolves a <see cref="ValueCacheRequest"/> against <paramref name="stack"/> and constructs
    /// the writer. Names that fail <see cref="NameValidation.IsValidName"/> throw
    /// <see cref="SessionErrorCode.ValueCacheInvalidFieldName"/>. Unknown names throw
    /// <see cref="SessionErrorCode.ValueCacheUnknownField"/>. A request the value cache itself rejects (empty, a field that
    /// records nothing, a duplicate series, <c>ChunkShift</c> out of range) throws
    /// <see cref="SessionErrorCode.ValueCacheInvalidRequest"/>.
    /// </summary>
    private static ValueCache _BuildValueCache(Stack stack, ValueCacheRequest request)
    {
        // Field entries are keyed by name so that an explicit Fields entry replaces the FieldNames shorthand for the
        // same field (the Fields loop below assigns after the shorthand loop). Names are validated before lookup, so
        // an invalid name and an unknown name report different error codes.
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

        // Groups carry only an id and the default capture mode; the ValueCache expands them to payload series itself.
        List<ValueCacheGroupConfig> groups = [];
        foreach (string groupName in request.GroupNames)
        {
            IndexGroupId groupId = _RequireGroupId(stack, groupName);
            groups.Add(new ValueCacheGroupConfig(groupId, request.DefaultCaptureMode));
        }

        ValueCacheFieldConfig[] fields = [.. byName.Values];
        ValueCacheGroupConfig[] groupConfigs = [.. groups];

        // The ValueCache constructor validates the configuration (empty request, duplicate series, a field no flag
        // records, ChunkShift range) and throws ArgumentException for it. Every name was already resolved above, so none
        // of these is an unknown field: they are reported as ValueCacheInvalidRequest, and the original message names the
        // real cause. The ChunkShift setter of the options initializer below throws ArgumentOutOfRangeException (an
        // ArgumentException) for an out-of-range request value, so it takes the same path.
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
                SessionErrorCode.ValueCacheInvalidRequest,
                ex.Message);
        }
    }

    /// <summary>
    /// Resolves a value-cache field name on <paramref name="stack"/>; throws a <see cref="SessionException"/>
    /// for an invalid or unknown name.
    /// </summary>
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

    /// <summary>
    /// Resolves a value-cache index-group name on <paramref name="stack"/>; throws a <see cref="SessionException"/>
    /// for an invalid or unknown name.
    /// </summary>
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

    /// <summary>
    /// Builds the ingest value cache for <paramref name="stack"/> from <c>SessionOptions.ValueCache</c>, or returns
    /// <see langword="null"/> when none was requested. Throws the <see cref="SessionException"/> of
    /// <see cref="_BuildValueCache"/> when the stack cannot satisfy the request; it changes no session state.
    /// </summary>
    private ValueCache? _TryCreateIngestValueCache(Stack stack)
    {
        if (_IngestRequest is null)
        {
            return null;
        }

        return _BuildValueCache(stack, _IngestRequest);
    }

    /// <summary>
    /// Creates the public view of the ingest cache. It is created once (a Restart re-points it at the new writer with
    /// <see cref="ValueCacheInfo"/>.SetWriter) so the id and the info a caller holds stay valid across restarts.
    /// </summary>
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

        // The ingest cache is built eagerly so the first parsed frame already records into it. A request that does not
        // fit the stack throws here and fails the constructor.
        _IngestValueCache = _TryCreateIngestValueCache(_Stack);
        if (_IngestValueCache is null)
        {
            return;
        }

        _IngestInfo = _CreateIngestInfo(_IngestValueCache);
        _ValueCacheInfos.Add(_IngestInfo);

        // The listener, when present, only observes the ingest cache (NotifyOnly): the parse thread fills it.
        if (_IngestListener is not null)
        {
            bool registered = _TryRegisterValueCacheSlot(
                _IngestListener,
                _IngestRequest!, // Non-null: a listener without a request was rejected above.
                _IngestValueCache,
                ValueCacheFillMode.NotifyOnly,
                out _,
                out _,
                out _);

            // The constructor runs before anyone else holds the session, so no shutdown can have started.
            Debug.Assert(registered, "Registering the ingest listener in the constructor cannot be refused.");
        }
    }

    /// <summary>
    /// Cancels and joins all value-cache slots. Same cancel-all-then-join-each procedure as the listener slots in
    /// <see cref="_ShutdownUnderLock"/>; one snapshot is enough because the shutdown flag was published under
    /// <see cref="_RegistryLock"/>, which every slot registration re-checks before it publishes.
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
            // A never-started slot would stay Pending forever (Cancel does not move it), so start it and let the loop
            // observe the cancellation; Join would otherwise wait for a thread that does not exist. A failed start has
            // already told the listener (TryStart).
            if (slot.Status == JobStatus.Pending)
            {
                _ = slot.TryStart(out _);
            }

            slot.Join();
            slot.EnsureOnUnsubscribed();
        }
    }

    // -- Parse helper --

    /// <summary>
    /// How often a source thread found the parse gate closed under <see cref="_ParseMutex"/> and parked
    /// again instead of allocating an id. Diagnostic; tests use it to prove the restart race was taken.
    /// </summary>
    internal int IngestGateRefusalCount => _IngestGateRefusals;

    /// <summary>
    /// Fail-closed path when a freshly allocated packet id cannot be recorded in the map.
    /// Extracted so ExitPointGaps can exercise the throw without corrupting a live source loop.
    /// </summary>
    internal static void ThrowMappingRecordFailed(PacketId packetId)
    {
        // An invalid id here means the id allocator and the packet-id range disagree: a broken invariant, so it throws.
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

        // Single writer: every caller holds _ParseMutex. The map publishes the entry before it publishes its count.
        _PacketFrames.Record(packetId.Value, frameId, sourceId);
    }

    /// <summary>
    /// Packs <paramref name="frameId"/> in bits 63..32 and <paramref name="sourceId"/> in bits 31..0.
    /// Casts through <see cref="uint"/> so <see cref="FrameId.Invalid"/> (-1) does not sign-extend.
    /// This is the per-packet word that <see cref="PacketFrameMap"/> stores raw or encodes as the base of a group.
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
    private bool _TryGetPacketFrame(PacketId packetId, out FrameId frameId, out FrameSourceId sourceId) =>
        _PacketFrames.TryGet(packetId.Value, out frameId, out sourceId);

    /// <summary>
    /// Allocates the next sequential <see cref="PacketId"/> under <see cref="_ParseMutex"/>.
    /// </summary>
    /// <exception cref="SessionException">
    /// Thrown when the next ID would exceed <see cref="Core.Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    private PacketId _AllocateNextPacketId()
    {
        // Increment first, validate second: the counter may already be past the last valid id when this throws. The
        // ingest caller puts the counter back (see _TryIngestFrameUnderLock), so the failure is not sticky past it.
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
    /// First parse of a frame into a freshly allocated packet, using the packet index when the session was configured
    /// with one. Called only from <see cref="_ParseAllocatedFrame"/> (when there is no accepted recycle packet), i.e.
    /// from the ordered first-parse path with <see cref="_ParseMutex"/> held. A rejected parse is a broken invariant
    /// (for example a jump in the dense id sequence), not an expected failure, hence the exception.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Packet _ParseFrameCore(PacketId id, Frame frame)
    {
        // Skip mode plus the ingest cache and the index: everything the first parse has to feed, no field tree.
        ParseOptions options = new(_IngestFieldTreeMode, _IngestValueCache, index: _PacketIndex);
        if (!Packet.TryParse(id, _Stack, frame, in options, out Packet? packet, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet!; // Non-null: TryParse returned true.
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
            // Restart passes the original id (the counter is not used); a null id allocates the next one, inside the lock.
            PacketId id = packetId ?? _AllocateNextPacketId();
            return _ParseAllocatedFrame(id, frame, recycle);
        }
    }

    /// <summary>
    /// First parse of one newly read frame. Returns <see langword="false"/> without allocating an id
    /// when the parse gate is closed: Restart resets the gate before it takes this lock, so a source
    /// thread that passed the gate earlier must wait again instead of allocating from the reset counter.
    /// Holds <see cref="_ParseMutex"/> from the gate check through the map append and the packet-count
    /// publish. Restart must not call this.
    /// </summary>
    /// <returns><see langword="true"/> when the frame was parsed and announced; <paramref name="packet"/> is then set.</returns>
    private bool _TryIngestFrameUnderLock(
        Frame frame,
        FrameSourceId sourceId,
        Packet? recycle,
        [NotNullWhen(true)] out Packet? packet)
    {
        lock (_ParseMutex)
        {
            // Restart closed the gate after this thread passed it. Parsing now would race the re-parse.
            if (!_ParseGate.IsSet)
            {
                Interlocked.Increment(ref _IngestGateRefusals);
                packet = null;
                return false;
            }

            // One critical section for allocate, parse, map append and count publish: a second source cannot observe
            // the next id before this frame is stored (ids, map and counts stay dense and in parse order).
            int nextBefore = _NextPacketId;
            try
            {
                PacketId id = _AllocateNextPacketId();
                packet = _ParseAllocatedFrame(id, frame, recycle);

                // Map entry before the counts: a reader that loads PacketCount may look up every id below it.
                RecordPacketFrame(id, frame.Id, sourceId);
                Interlocked.Increment(ref _PacketCount);
                Interlocked.Increment(ref _FrameCount);
                return true;
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
        // Reuse the caller's packet in place when the parser accepts it (same stack, no outstanding field reads). A
        // rejected recycle leaves the packet unchanged and falls through to a fresh allocation, so it is never an error.
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
    /// converted to <see langword="false"/>. Consequently the method returns <see langword="true"/> or throws
    /// <see cref="InvalidOperationException"/> when the parser refuses the id; it never returns
    /// <see langword="false"/> today, and the callers treat a <see langword="false"/> as a miss.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a restart or shutdown disabled the queries before the stack lease was granted; the caller
    /// treats it as a refused read. Never <see langword="false"/> for a parse failure.
    /// </returns>
    /// <exception cref="InvalidOperationException">The parser rejected <paramref name="packetId"/> or the frame.</exception>
    private bool _TryReparseFrame(
        Frame frame, PacketId packetId, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        // The lease covers exactly the parse: the stack must not be disposed while a protocol parses on it. The frame was
        // read before (a user frame source runs outside the lease, so a slow source never delays a disposal).
        if (!_TryLeaseStackForRead(out StackLease lease))
        {
            packet = null;
            return false;
        }

        using (lease)
        {
            return _ReparseOnStack(lease.Stack, frame, packetId, recycle, out packet);
        }
    }

    /// <summary>
    /// The parse of <see cref="_TryReparseFrame"/> on a stack the caller holds a lease on. A caller that re-parses a whole chunk
    /// takes one lease for it and calls this per packet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parser rejected <paramref name="packetId"/> or the frame.</exception>
    private bool _ReparseOnStack(
        Stack stack, Frame frame, PacketId packetId, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        // Build mode: a reader needs the full field tree. No value cache is passed, so a replay never writes one. The
        // packet index is passed along; it ignores an id it already holds (PacketIndex.TryBeginPacket), so a re-parse
        // cannot double-count presence bits.
        ParseOptions options = new(FieldTreeMode.Build, index: SelectIndexFor(_PacketIndex, stack));
        if (recycle is not null && Packet.TryParse(recycle, packetId, stack, frame, in options, out _))
        {
            packet = recycle;
            return true;
        }

        if (!Packet.TryParse(packetId, stack, frame, in options, out Packet? created, out ParseFailure failure) || created is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        packet = created;
        return true;
    }

    /// <summary>
    /// The packet index a parse on <paramref name="stack"/> may use: <paramref name="candidate"/> when it was built for that
    /// stack, otherwise <see langword="null"/> (no presence recording).
    /// </summary>
    /// <remarks>
    /// Why: a reader that holds a lease on the stack a restart just replaced can read the session's index after the swap, which
    /// is the new stack's index. The parser rejects an index of another stack (<see cref="ParseFailure.IndexStackMismatch"/>),
    /// which would turn a legitimate in-flight read into an exception. Skipping the index is correct: the old stack's packets
    /// are not part of the new index, and a re-parse never adds presence for an id the index already holds anyway.
    /// </remarks>
    internal static PacketIndex? SelectIndexFor(PacketIndex? candidate, Stack stack)
    {
        if (candidate is not null && ReferenceEquals(candidate.Stack, stack))
        {
            return candidate;
        }

        return null;
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
    /// <remarks>Returns a read-only view of the current immutable snapshot; the view is created once per snapshot, not per call.</remarks>
    public IReadOnlyList<FrameSourceInfo> GetFrameSources() => _SourceInfos.ReadOnlyView;

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

        // Refuse early so a session that cannot take the subscription does not pay for compiling the filter. The core
        // checks again, because the phase may change while the filter compiles.
        if (!_TryAcceptSubscription(out failure))
        {
            return false;
        }

        // Compile before allocating any listener state so a bad expression leaves the session
        // exactly as it was. A Restart that swaps the stack after this point is caught by the core:
        // it re-binds the filter, and a field the new stack lacks is reported here as a filter failure.
        FilterResult<PacketFilter> compiled = PacketFilter.Compile(filterExpression ?? string.Empty, _Stack);
        if (!compiled.TryGetValue(out PacketFilter? filter))
        {
            filterFailure = compiled.Error;
            return false;
        }

        return _TryAddListenerCore(listener, filter, out info, out filterFailure, out failure);
    }

    /// <inheritdoc/>
    public bool TryAddListener(
        ISessionListener listener,
        PacketFilter? filter,
        [NotNullWhen(true)] out ListenerInfo? info,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _ThrowIfDisposed();

        if (_TryAddListenerCore(listener, filter, out info, out FilterError? bindFailure, out failure))
        {
            return true;
        }

        // A caller-supplied filter that names fields this session does not have is a caller error.
        if (bindFailure is not null)
        {
            // The '!' below is safe: a null filter is returned as is by _TryBindFilterToStack and cannot fail to bind.
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The filter '{filter!.Expression}' was compiled for another stack and cannot be re-bound to the session stack: ")
                + bindFailure.Message,
                nameof(filter));
        }

        // Without a bind failure the core returns false only for a refused subscription, which sets failure.
        Debug.Assert(failure is not null, "A refused subscription always carries a SessionFailure.");
        return false;
    }

    /// <summary>
    /// Shared body of both filtered <c>TryAddListener</c> overloads. Returns <see langword="false"/> with
    /// <paramref name="failure"/> when the session refuses the subscription, or with
    /// <paramref name="bindFailure"/> when a filter compiled for another stack cannot be re-bound to the
    /// session stack. In both cases no slot or info is registered afterwards (a refusal after the ids were taken leaves
    /// those two ids unused).
    /// </summary>
    /// <exception cref="SessionException">
    /// <see cref="SessionErrorCode.ListenerUiNameEmpty"/>, <see cref="SessionErrorCode.ListenerIdExhausted"/>,
    /// <see cref="SessionErrorCode.JobIdExhausted"/>.
    /// </exception>
    private bool _TryAddListenerCore(
        ISessionListener listener,
        PacketFilter? filter,
        [NotNullWhen(true)] out ListenerInfo? info,
        out FilterError? bindFailure,
        out SessionFailure? failure)
    {
        info = null;
        bindFailure = null;

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

        // A filter carries field ids of the stack it was compiled for. Re-bind a foreign one before any
        // id or slot exists; a filter that cannot be re-bound names fields this session does not have.
        if (!_TryBindFilterToStack(filter, _Stack, out PacketFilter? bound, out bindFailure))
        {
            return false;
        }

        // Allocate both ids before any slot exists: an exhausted id range throws here and leaves nothing to undo.
        // (A registration that loses a race with Shutdown below does not give them back; ids are never reused anyway.)
        ListenerId listenerId = _State.AllocateListenerId();
        JobId jobId = _State.AllocateJobId();

        // Build the slot and its public view completely before either is visible to anyone: the filter, the link from the
        // slot to its ListenerInfo (TryUnsubscribe finds the listener through it), and the Unsubscribe convenience API.
        // Why: the job list is lock-free and readable at once, and a TryUnsubscribe that found the slot's job while the link
        // was still missing would take it for a user job and cancel it without releasing the subscription.
        ListenerSlot slot = new(jobId, listener, this, _OnJobStatusChanged);
        slot.SetFilter(bound);
        ListenerInfo listenerInfo = new()
        {
            Id = listenerId,
            UiName = listener.UiName,
        };
        slot.ListenerInfo = listenerInfo;

        // Wire the convenience API: ListenerInfo.Unsubscribe() → TryUnsubscribe(job).
        // Captured reference is the slot's JobInfo (same reference stored in _AllJobs).
        JobInfo slotJobInfo = slot.Info;
        listenerInfo.UnsubscribeCallback = () =>
        {
            _ = TryUnsubscribe(slotJobInfo, out _);
        };

        // Publish all three registries together, under the registry lock, after re-checking that shutdown has not started.
        // From the publication on, source threads and Restart can see the slot (lock-free snapshot lists).
        // Why the check is here and not only in _TryAcceptSubscription: Shutdown snapshots the slots after it published
        // its flag under this same lock, so a slot published here is cancelled and joined, and a slot that arrives later
        // is refused instead of running forever. startNow is read in the same critical section as the publication, and
        // TryStart leaves Idle under this lock too: the slot is either in TryStart's snapshot (phase still Idle) or
        // started by this call (phase already left Idle), never neither.
        bool startNow;
        lock (_RegistryLock)
        {
            if (_ShutdownStarted != 0)
            {
                failure = new SessionFailure(
                    SessionErrorCode.InvalidPhase,
                    "Subscriptions cannot be added while the session is shutting down.");
                info = null;
                slot.Dispose();
                return false;
            }

            _ListenerSlots.Add(slot);
            _AllJobs.Add(slot.Info);
            _ListenerInfos.Add(listenerInfo);
            startNow = _State.Phase != SessionPhase.Idle;
        }

        info = listenerInfo;

        // A Restart may have swapped the stack between the bind above and the registration. Restart
        // re-binds only the slots it can see, so check again now that this slot is visible to it.
        _RebindIfStackChanged(slot);

        // Start the slot in any non-Idle phase. During Idle, _StartInternal()
        // will start all pending slots. In all active phases (Running,
        // Restarting), immediate start ensures the slot's thread is ready for
        // notifications. Starting while a shutdown begins is safe — the slot is in the shutdown snapshot, which
        // starts a still-Pending slot itself, and Job.Start guards against a double start.
        // Un-started (Pending) slots would deadlock shutdown's wait loop
        // because Cancel() alone does not transition a never-started Job out of Pending.
        if (startNow)
        {
            slot.Start();
        }

        // A listener that joins after packets exist must still be told about them; the flag was missed otherwise.
        _BackfillListener(slot);
        failure = null;
        return true;
    }

    /// <summary>
    /// Yields <paramref name="filter"/> when it is null, always-match, or already bound to
    /// <paramref name="stack"/>; otherwise a re-bound copy from <see cref="PacketFilter.TryDerive"/>.
    /// The caller's instance is never mutated.
    /// </summary>
    /// <returns><see langword="false"/> with <paramref name="failure"/> when the filter cannot be re-bound to <paramref name="stack"/>.</returns>
    private static bool _TryBindFilterToStack(
        PacketFilter? filter,
        Stack stack,
        out PacketFilter? bound,
        [NotNullWhen(false)] out FilterError? failure)
    {
        if (filter is null || filter.IsAlwaysMatch || ReferenceEquals(filter.Stack, stack))
        {
            bound = filter;
            failure = null;
            return true;
        }

        if (!filter.TryDerive(stack, out PacketFilter? derived, out failure))
        {
            bound = null;
            return false;
        }

        bound = derived;
        return true;
    }

    /// <summary>
    /// Re-binds the filter of a freshly registered <paramref name="slot"/> when the session stack changed
    /// after the filter was bound. Mirrors <see cref="_DeriveListenerFilters"/>: a filter that cannot be
    /// re-bound is dropped and the failure is recorded on the slot. Runs under the slot's filter gate, so it
    /// serialises with a concurrent Restart re-binding the same slot; re-binding twice is harmless.
    /// </summary>
    private void _RebindIfStackChanged(ListenerSlot slot)
    {
        lock (slot.FilterGate)
        {
            PacketFilter? filter = slot.Filter;
            Stack current = _Stack;
            if (filter is null || filter.IsAlwaysMatch || ReferenceEquals(filter.Stack, current))
            {
                return;
            }

            if (filter.TryDerive(current, out PacketFilter? derived, out FilterError? failure))
            {
                slot.SetFilter(derived);
                return;
            }

            slot.SetFilterFault(failure);
        }
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

        // Build the writer before registering anything: a request the stack cannot satisfy throws a SessionException
        // with no slot, id or info left behind. The slot keeps the request so it can rebuild on a stack change.
        // The stack is read once: the cache is built on exactly this one, and the check after the registration compares
        // against it.
        Stack builtOn = _Stack;
        ValueCache writer = _BuildValueCache(builtOn, request);
        if (!_TryRegisterValueCacheSlot(
                listener,
                request,
                writer,
                ValueCacheFillMode.PullFill,
                out ValueCacheSlot? slot,
                out bool startNow,
                out failure))
        {
            return false;
        }

        info = slot.ValueCacheInfo!; // Non-null: _TryRegisterValueCacheSlot always assigns it before returning true.

        // Same start rule as listeners: in Idle, _StartInternal starts the pending slots; otherwise start now (a Pending
        // slot would never leave Pending on cancellation and would block shutdown's join).
        if (startNow)
        {
            slot.Start();
        }

        // A Restart may have swapped the stack while the cache was being built, before this slot was visible to it. That
        // Restart abandoned and rebound only the slots it could see, so this one would keep filling a cache of the retired
        // stack (the parser rejects it as a cache of another stack and nothing would ever be recorded). The slot rebuilds
        // its cache from the stored request when it receives StackChanged, which is the same path a Restart that did see it
        // takes. A swap after this point finds the slot registered and handles it itself.
        if (!ReferenceEquals(builtOn, _Stack))
        {
            slot.Notify(NotifyFlags.StackChanged);
        }

        // Fill from id 0: a late PullFill slot has the whole existing range to catch up on (cursor starts at 0).
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
    /// <remarks>Returns a read-only view of the current immutable snapshot; the view is created once per snapshot, not per call.</remarks>
    public IReadOnlyList<ListenerInfo> GetListeners() => _ListenerInfos.ReadOnlyView;

    #endregion

    #region Packet reads

    // -- ISessionReader: Packet access --

    /// <inheritdoc/>
    public bool TryGetPacket(
        PacketId id,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure) =>
        TryGetPacket(id, recycle: null, out packet, out failure);

    /// <inheritdoc/>
    public bool TryGetPacket(
        PacketId id,
        Packet? recycle,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        // One snapshot of the source table per call; lock-free, so any thread may call this while ingest continues.
        RandomAccessLookup sources = new(_RandomAccessSources);
        return _TryGetPacketCore(ref sources, id, recycle, out packet, out failure);
    }

    /// <summary>
    /// Per-id body shared by <see cref="TryGetPacket(PacketId, Packet?, out Packet?, out SessionFailure?)"/>
    /// and the read loops. <paramref name="sources"/> is a snapshot the caller takes once per call, so a loop
    /// neither re-reads the volatile dictionary nor hashes the source id again for a run of packets from one source.
    /// </summary>
    /// <remarks>
    /// The <see cref="_QueriesDisabled"/> check stays per id on purpose: it is one volatile load, and it is the
    /// guard that keeps a long read from re-parsing ids on a stack that a concurrent Restart has just swapped in.
    /// </remarks>
    private bool _TryGetPacketCore(
        ref RandomAccessLookup sources,
        PacketId id,
        Packet? recycle,
        [NotNullWhen(true)] out Packet? packet,
        [NotNullWhen(false)] out SessionFailure? failure)
    {
        // Guards first (cheap, no I/O): the restart/shutdown pause, then the id itself, then the map. An id that is
        // not in the map has not been announced (yet).
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

        // Re-read the captured frame from its source (the session keeps no packets), then re-parse it below.
        if (!sources.TryGet(sourceId, out IRandomAccessFrameSource? raSource))
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

        // The lease check inside the re-parse is the authoritative queries check: the early one above is only a cheap
        // short cut, a restart can disable the queries between the two.
        if (!_TryReparseFrame(raFrame.Value, id, recycle, out packet))
        {
            failure = new SessionFailure(SessionErrorCode.QueriesDisabled, "Packet queries are disabled.");
            return false;
        }

        failure = null;
        return true;
    }

    /// <inheritdoc/>
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

        // A missing source is reported through the null check below as FrameUnavailable, like an unreadable frame.
        _ = _RandomAccessSources.TryGet(sourceId, out IRandomAccessFrameSource? raSource);

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

        // Ids fromIndex … fromIndex + total − 1 exist; a start outside the recorded range reads nothing. The count is
        // loaded once: it is published after the map entry of every id below it (see _TryIngestFrameUnderLock), so
        // every id in [0, limit) can be looked up even while ingest appends more.
        int limit = _PacketCount;
        if (fromIndex < 0 || fromIndex >= limit)
        {
            return 0;
        }

        // Read in chunks of up to 64 ids (the size of one map group): one batched frame read per source and chunk instead of one random
        // FrameById per packet. The chunk buffers are stack arrays (see FrameBatch), so a read of any length allocates
        // nothing beyond the packets the caller did not supply for recycling.
        int total = Math.Min(buffer.Length, limit - fromIndex);
        RandomAccessLookup sources = new(_RandomAccessSources);
        for (int done = 0; done < total; done += _FrameBatchSize)
        {
            int length = Math.Min(_FrameBatchSize, total - done);
            _ReadPacketChunk(ref sources, fromIndex + done, buffer.Slice(done, length));
        }

        return total;
    }

    /// <inheritdoc/>
    public int ReadPackets(int startId, Span<PacketRef> destination, out PacketIdLayout idLayout)
    {
        idLayout = PacketIdLayout.Contiguous;
        if (_QueriesDisabled)
        {
            return 0;
        }

        // Same single count load as the Packet overload: ids below it are all in the map.
        int limit = _PacketCount;
        if (startId < 0 || startId >= limit)
        {
            return 0;
        }

        // The ids of a plain range read are consecutive, so the layout is always Contiguous (only the Matching mode of
        // TryReadPackets can skip ids).
        int total = Math.Min(destination.Length, limit - startId);
        RandomAccessLookup sources = new(_RandomAccessSources);
        PacketBatch batch = default;
        for (int done = 0; done < total; done += _FrameBatchSize)
        {
            int length = Math.Min(_FrameBatchSize, total - done);
            Span<Packet?> chunk = batch;
            chunk = chunk[..length];

            // Recycle the packets the caller's slots carry, read the chunk, and hand the results back with their ids.
            for (int i = 0; i < length; i++)
            {
                chunk[i] = destination[done + i].Packet;
            }

            _ReadPacketChunk(ref sources, startId + done, chunk);
            for (int i = 0; i < length; i++)
            {
                destination[done + i] = new PacketRef(new PacketId(startId + done + i), chunk[i]);
            }
        }

        return total;
    }

    /// <summary>
    /// Packets per batched frame read: one map group, and the chunk size of the sources' batch reads. Must not exceed 64:
    /// <see cref="_ReadFrameBatch"/> tracks the pending lanes of a chunk in the bits of one <see cref="ulong"/>.
    /// Constant (a tuning value tied to the 64-lane bitmask, not configurable).
    /// </summary>
    private const int _FrameBatchSize = 64;

    /// <summary>
    /// Frames of up to 64 packets on the stack (no allocation per chunk). A <see cref="Frame"/> holds a reference to its
    /// bytes, so <c>stackalloc</c> cannot hold it; an inline array gives a stack-allocated <see cref="Span{T}"/> of
    /// managed-reference elements instead of a heap array per chunk. Thread-safety: not applicable (stack-local value).
    /// </summary>
    [InlineArray(_FrameBatchSize)]
    private struct FrameBatch
    {
        private Frame? _Element0;
    }

    /// <summary>
    /// Packets of up to 64 slots on the stack, for the <see cref="PacketRef"/> overload; stack-allocated for the same
    /// reason as <see cref="FrameBatch"/>. Thread-safety: not applicable (stack-local value).
    /// </summary>
    [InlineArray(_FrameBatchSize)]
    private struct PacketBatch
    {
        private Packet? _Element0;
    }

    /// <summary>Why a packet of a batch has no frame.</summary>
    private enum FrameBatchMiss : byte
    {
        /// <summary>The frame was read.</summary>
        None,

        /// <summary>The packet id has no entry in the packet-to-frame map.</summary>
        Unmapped,

        /// <summary>The packet's source is not in the random-access table.</summary>
        NotRandomAccess,

        /// <summary>The source returned no frame for the id (not indexed, changed, or unreadable).</summary>
        Unreadable,
    }

    /// <summary>
    /// Resolves packets <paramref name="startId"/> … <paramref name="startId"/> + <paramref name="frames"/>.Length − 1
    /// (at most 64, all below the packet count) to their frames. Maps every packet, groups the lanes per source, reads
    /// each run of consecutive frame ids of a source with one range call (<see cref="IRandomAccessFrameSource.FramesById"/>,
    /// usually one call per source and chunk), and scatters the frames back. <paramref name="misses"/> tells why an entry is
    /// null.
    /// </summary>
    private void _ReadFrameBatch(ref RandomAccessLookup sources, int startId, Span<Frame?> frames, Span<FrameBatchMiss> misses)
    {
        // All scratch lives on the stack (value-type ids only, so stackalloc is allowed) and `pending` is a bitmask with
        // one bit per lane, which is why a chunk holds at most 64 packets (_FrameBatchSize). The whole routine allocates
        // nothing.
        int length = frames.Length;
        Span<FrameId> frameIds = stackalloc FrameId[_FrameBatchSize];
        Span<int> sourceIds = stackalloc int[_FrameBatchSize];
        ulong pending = 0;

        // Map: a packet without a mapping is a miss now; the others wait for their source's call.
        for (int lane = 0; lane < length; lane++)
        {
            frames[lane] = null;
            if (_TryGetPacketFrame(new PacketId(startId + lane), out FrameId frameId, out FrameSourceId sourceId))
            {
                frameIds[lane] = frameId;
                sourceIds[lane] = sourceId.Value;
                misses[lane] = FrameBatchMiss.None;
                pending |= 1UL << lane;
            }
            else
            {
                misses[lane] = FrameBatchMiss.Unmapped;
            }
        }

        // One FramesById per source: gather its lanes in packet order, read, scatter.
        Span<FrameId> runIds = stackalloc FrameId[_FrameBatchSize];
        Span<int> runLanes = stackalloc int[_FrameBatchSize];
        FrameBatch runFrames = default;
        while (pending != 0)
        {
            // Take the source of the lowest pending lane and collect all lanes of that source (in lane order, so the
            // frame ids stay in packet order); lanes of other sources stay pending for the next iteration.
            int source = sourceIds[BitOperations.TrailingZeroCount(pending)];
            int count = 0;
            for (ulong rest = pending; rest != 0; rest &= rest - 1)
            {
                int lane = BitOperations.TrailingZeroCount(rest);
                if (sourceIds[lane] == source)
                {
                    runLanes[count] = lane;
                    runIds[count] = frameIds[lane];
                    count++;
                    pending &= ~(1UL << lane);
                }
            }

            if (!sources.TryGet(new FrameSourceId(source), out IRandomAccessFrameSource? raSource))
            {
                for (int k = 0; k < count; k++)
                {
                    misses[runLanes[k]] = FrameBatchMiss.NotRandomAccess;
                }

                continue;
            }

            // A source's packets carry ascending frame ids; every stretch of consecutive ids is one range read, so the
            // source walks its frames forward. Gaps (frames the source skipped) start a new range. Ascending order is an
            // efficiency assumption only: ids that step back or repeat just end the run and start another range, so the
            // result stays correct for any order. The +1 below cannot overflow for valid frame ids (at most
            // ArrayIndexIdRange.MaxValue); an invalid id (-1) is only read as a null entry by FramesById.
            Span<Frame?> got = runFrames;
            got = got[..count];
            for (int runStart = 0; runStart < count;)
            {
                int runEnd = runStart + 1;
                while (runEnd < count && runIds[runEnd].Value == runIds[runEnd - 1].Value + 1)
                {
                    runEnd++;
                }

                // The returned found-count is not needed: every entry of the range is written, a null entry marks a
                // frame the source could not give, and the scatter below turns it into FrameBatchMiss.Unreadable.
                _ = raSource.FramesById(runIds[runStart], got[runStart..runEnd]);
                runStart = runEnd;
            }

            // Scatter: put each frame back into its packet lane and record why a lane stayed empty.
            for (int k = 0; k < count; k++)
            {
                int lane = runLanes[k];
                frames[lane] = got[k];
                misses[lane] = got[k] is null
                    ? FrameBatchMiss.Unreadable
                    : FrameBatchMiss.None;
            }
        }
    }

    /// <summary>
    /// Re-parses packets <paramref name="startId"/> … into <paramref name="packets"/> (≤ 64 slots; a non-null slot is
    /// that slot's recycle packet, a failed slot becomes null). The queries-disabled check stays per packet, as in
    /// <see cref="_TryGetPacketCore"/>.
    /// </summary>
    private void _ReadPacketChunk(ref RandomAccessLookup sources, int startId, Span<Packet?> packets)
    {
        // Read the whole chunk's frames first (batched per source), then re-parse them one by one on this thread.
        FrameBatch frames = default;
        Span<Frame?> chunk = frames;
        chunk = chunk[..packets.Length];
        Span<FrameBatchMiss> misses = stackalloc FrameBatchMiss[_FrameBatchSize];
        _ReadFrameBatch(ref sources, startId, chunk, misses[..packets.Length]);

        // One lease for the whole chunk instead of one per packet: an interlocked pair per 64 packets, and the chunk is short
        // (the per-packet queries check below ends it early when a restart starts). The frames were read before the lease, so
        // a slow source never holds a retired stack alive.
        if (!_TryLeaseStackForRead(out StackLease lease))
        {
            packets.Clear();
            return;
        }

        using (lease)
        {
            for (int i = 0; i < packets.Length; i++)
            {
                if (_QueriesDisabled || chunk[i] is not Frame frame)
                {
                    packets[i] = null;
                    continue;
                }

                packets[i] = _ReparseOnStack(lease.Stack, frame, new PacketId(startId + i), packets[i], out Packet? packet)
                    ? packet
                    : null;
            }
        }
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

        // All mode ignores the filter, so it needs neither the gate nor the filter state.
        if (mode == PacketReadMode.All)
        {
            count = ReadPackets(startId, destination, out idLayout);
            return true;
        }

        // Matching mode: a PacketFilter is single-threaded and keeps mutable state (flank state, match cache, poison),
        // and a Restart re-binds it under the same gate. The gate therefore serialises all matching reads of one
        // listener with each other and with the re-bind. Accepted cost: the gate is held across the frame reads and
        // re-parses of the whole read (not a short section), so a Restart waits for a running matching read.
        // Different listeners have different gates and do not block each other.
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

            return _TryReadMatching(slot, filter, startId, destination, out count, out idLayout, out failure);
        }
    }

    /// <summary>
    /// Scans <c>[startId, PacketCount)</c> and fills <paramref name="destination"/> with the packets
    /// that <paramref name="filter"/> accepts.
    ///
    /// <para>
    /// Presence pruning uses <see cref="PacketFilter.TryIsPresenceCandidate"/> on the live index:
    /// each packet id is tested with <see cref="ReadOnlyRoaringBitmap.Contains"/> so the index
    /// can keep growing without cloning bitmaps. Packets outside the prune set are skipped
    /// without touching the field tree. Survivors reach <see cref="PacketFilter.TryIsMatch"/>.
    /// </para>
    /// <para>
    /// Two cheaper stages run before the build re-parse: an id whose verdict the filter already cached as a non-match
    /// is skipped, and for an eligible filter (<see cref="MatchingPrecheck"/>) an unknown id is first re-parsed in skip
    /// mode with an equivalent observer, which may rule it out and records that verdict in the filter. The field-tree
    /// filter stays the authority for every returned packet: both stages only rule packets out.
    /// </para>
    /// <para>Caller holds <c>slot.FilterGate</c>.</para>
    /// </summary>
    private bool _TryReadMatching(
        ListenerSlot slot,
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

        // Setup: one view of the live index, one load of the count (every id below it is mapped), and the scan state.
        // `skipped` records whether any scanned id was left out, which is what makes the returned layout Gapped.
        PacketIndexReaderView? indexView = _PacketIndex?.AsReadOnlyView();
        int limit = _PacketCount;
        int first = Math.Max(startId, 0);
        bool skipped = false;
        int filled = 0;
        RandomAccessLookup sources = new(_RandomAccessSources);
        MatchingPrecheck? precheck = _GetPrecheck(slot, filter);

        // Per id, cheapest rejection first: presence index (no frame read), cached verdict, skip-parse pre-check, and
        // only then the authoritative build re-parse plus field-tree evaluation. Every stage can only rule an id OUT;
        // an id returned to the caller has always passed TryIsMatch.
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

            // Known verdicts first: a cached non-match needs no re-parse (TryIsMatch would return the same cached false).
            bool cachedMatch = false;
            if (filter.TryGetCachedVerdict(packetId, out bool cached))
            {
                if (!cached)
                {
                    skipped = true;
                    continue;
                }

                cachedMatch = true;
            }

            // Frame of this id when the pre-check read it; the authority re-parse reuses it instead of a second read.
            Frame? prechecked = null;

            // Unknown ids of an eligible filter: a skip-mode parse with the equivalent observer may rule the id out.
            if (precheck is not null && !cachedMatch)
            {
                if (precheck.IsActive)
                {
                    if (!_TryPrecheck(ref sources, precheck, packetId, out bool maybeMatch, out Frame read))
                    {
                        // Frame unavailable or queries disabled: exactly the outcome of the re-parse miss below.
                        skipped = true;
                        continue;
                    }

                    precheck.Record(maybeMatch);
                    if (!maybeMatch)
                    {
                        filter.RecordVerdict(packetId, matched: false);
                        skipped = true;
                        continue;
                    }

                    prechecked = read;
                }
                else
                {
                    precheck.OnSkippedId();
                }
            }

            // Authority: build re-parse and field-tree evaluation, unchanged. A frame the pre-check read is parsed as is.
            Packet? recycle = destination[filled].Packet;
            Packet? packet;
            if (prechecked is Frame ready)
            {
                if (!_TryReparsePacketCore(ready, packetId, recycle, out packet))
                {
                    skipped = true;
                    continue;
                }
            }
            else if (!_TryGetPacketCore(ref sources, packetId, recycle, out packet, out _))
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
    /// Returns the slot's pre-check for <paramref name="filter"/>, creating it once per filter instance.
    /// Caller holds <c>slot.FilterGate</c>.
    /// </summary>
    private static MatchingPrecheck? _GetPrecheck(ListenerSlot slot, PacketFilter filter)
    {
        // Built lazily and cached on the slot: creating the observer and parse options per read would repeat that work
        // on every call. The owner check rebuilds it when the filter instance changed (a Restart re-bind installs a new
        // filter); a null Precheck for the current owner means the filter is not eligible (see CreatePrecheckObserver).
        if (!ReferenceEquals(slot.PrecheckOwner, filter))
        {
            FilterObserver? observer = filter.CreatePrecheckObserver();
            slot.Precheck = observer is null ? null : new MatchingPrecheck(filter, observer);
            slot.PrecheckOwner = filter;
        }

        return slot.Precheck;
    }

    /// <summary>
    /// Skip-mode re-parse of <paramref name="id"/> with the pre-check observer. False when the frame cannot be read
    /// (the same per-id guards as the build path). <paramref name="maybeMatch"/> is true whenever the observer cannot
    /// rule the packet out, including a rejected parse, so the authority path decides. <paramref name="frame"/> is the
    /// frame that was read, so the caller re-parses it without a second source read.
    /// </summary>
    /// <remarks>
    /// <paramref name="sources"/> is the snapshot the matching read takes once per call, as in <see cref="_TryGetPacketCore"/>.
    /// </remarks>
    private bool _TryPrecheck(
        ref RandomAccessLookup sources, MatchingPrecheck precheck, PacketId id, out bool maybeMatch, out Frame frame)
    {
        maybeMatch = true;
        frame = default;

        // Same per-id guards as TryGetFrame and the build path: a miss here is the outcome of the re-parse miss.
        if (_QueriesDisabled
            || !id.IsValid
            || !_TryGetPacketFrame(id, out FrameId frameId, out FrameSourceId sourceId)
            || !sources.TryGet(sourceId, out IRandomAccessFrameSource? raSource)
            || raSource.FrameById(frameId) is not Frame read)
        {
            return false;
        }

        frame = read;

        // Stack lease per id: the same restart guard as the build re-parse, and it keeps the stack alive for the parse.
        if (!_TryLeaseStackForRead(out StackLease lease))
        {
            return false;
        }

        using (lease)
        {
            Stack stack = lease.Stack;

            // Skip-mode parse into the pre-check's own recycled packet (never the caller's, never published), so the
            // pre-check allocates only when the recycle is rejected. A rejected parse leaves maybeMatch true: only a
            // successful parse whose observer says "no match" may rule an id out.
            Packet? recycle = precheck.Recycle;
            if (recycle is null || !Packet.TryParse(recycle, id, stack, frame, in precheck.Options, out _))
            {
                if (!Packet.TryParse(id, stack, frame, in precheck.Options, out Packet? created, out _))
                {
                    return true;
                }

                precheck.Recycle = created;
            }

            // The observer cannot give a verdict (TryReadMatch false) -> treat as "maybe", never as a rejection.
            maybeMatch = !precheck.Observer.TryReadMatch(out bool matched, out _) || matched;
            return true;
        }
    }

    /// <summary>
    /// The tail of <see cref="_TryGetPacketCore"/> for a frame the caller already holds: the queries-disabled guard and
    /// the build re-parse. Shared so a pre-checked id is not read from its source twice.
    /// </summary>
    private bool _TryReparsePacketCore(Frame frame, PacketId id, Packet? recycle, [NotNullWhen(true)] out Packet? packet)
    {
        if (_QueriesDisabled)
        {
            packet = null;
            return false;
        }

        return _TryReparseFrame(frame, id, recycle, out packet);
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

        // Refuse once shutdown has started, also after it finished: nobody would cancel or join the job. This is the cheap
        // early refusal; the registration below decides under the registry lock.
        if (!_TryAcceptJob(out failure))
        {
            return false;
        }

        JobId jobId = _State.AllocateJobId();
        Job job = new(jobId, uiName, description, work, _OnJobStatusChanged);
        JobInfo jobInfo = new(job);

        // Register before start, so the job is visible in GetJobs() (and cancellable) from its first instruction. The
        // shutdown check and the publication share the registry lock that Shutdown publishes its flag under: a job
        // published here is in Shutdown's snapshot (cancelled, joined, disposed), a job that comes later is refused. Without
        // that, a job added in the instant shutdown starts would run on with nobody left to cancel it.
        lock (_RegistryLock)
        {
            if (!_TryAcceptJob(out failure))
            {
                job.Dispose();
                info = null;
                return false;
            }

            _AllJobs.Add(jobInfo);
            _UserJobs.Add(jobInfo);
        }

        info = jobInfo;

        // Notify listeners about the new job via flags.
        _NotifyAllListeners(NotifyFlags.JobAdded);

        // Shutdown may already have taken its snapshot with this job in it and started it itself; the start is guarded.
        job.Start();
        failure = null;
        return true;
    }

    /// <summary>The phase rule for a new user job: shutdown has not started. Decides only under the registry lock.</summary>
    private bool _TryAcceptJob([NotNullWhen(false)] out SessionFailure? failure)
    {
        if (_ShutdownStarted != 0 || _State.Phase == SessionPhase.ShuttingDown)
        {
            failure = new SessionFailure(
                SessionErrorCode.InvalidPhase,
                "Jobs cannot be added once the session has started shutting down.");
            return false;
        }

        failure = null;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Returns a read-only view of the current immutable snapshot; the view is created once per snapshot, not per call.</remarks>
    public IReadOnlyList<JobInfo> GetJobs() => _AllJobs.ReadOnlyView;

    /// <inheritdoc/>
    public bool TryRemoveJob(JobInfo job, [NotNullWhen(false)] out SessionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(job);
        _ThrowIfDisposed();
        failure = null;

        // Removing a pending or running job is refused (the caller must cancel and wait first, see ISession.TryRemoveJob).
        // It is an expected condition: the caller can poll for it while a job winds down, so it is returned as a failure
        // like the unknown or already removed job below, not thrown.
        if (job.Status is JobStatus.Pending or JobStatus.Running)
        {
            failure = new SessionFailure(
                SessionErrorCode.JobStillRunning,
                "Cannot remove a job that is still pending or running. Cancel it and wait for completion first.");
            return false;
        }

        if (!_AllJobs.Remove(job))
        {
            failure = new SessionFailure(SessionErrorCode.JobNotFound, "The job is not registered in this session.");
            return false;
        }

        // A user job also leaves the shutdown list: it is finished, so shutdown has nothing left to cancel or join.
        // (A source, listener or value-cache job is not in that list; the removal is then a no-op.)
        _ = _UserJobs.Remove(job);
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
            return _TryUnsubscribeSource(sourceEntry, out failure);
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
    /// which is set during <see cref="TryAddListener(ISessionListener, PacketFilter?, out ListenerInfo?, out SessionFailure?)"/>.
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
    /// Drops <paramref name="sourceId"/> from the copy-on-write random-access table (a no-op when absent). Like the
    /// add it is a read-modify-write of the volatile field, so it runs under <see cref="_RegistryLock"/> (re-entrant
    /// when the caller already holds it): a concurrent add would otherwise be undone by the later store.
    /// </summary>
    private void _RemoveRandomAccessSource(FrameSourceId sourceId)
    {
        lock (_RegistryLock)
        {
            _RandomAccessSources = _RandomAccessSources.Without(sourceId);
        }
    }

    /// <summary>
    /// Stops a source job. A source that was never started is removed from every registry and disposed. A running
    /// source is cancelled: its thread exits after the current frame, and the source stays in the registries and in
    /// the random-access table, so its already ingested frames remain readable for re-parse and Restart until
    /// <see cref="Shutdown"/> disposes it.
    /// </summary>
    private bool _TryUnsubscribeSource(FrameSourceEntry entry, [NotNullWhen(false)] out SessionFailure? failure)
    {
        // Never started: nothing ingested yet, so the source can be removed completely; no frame of it is referenced by the
        // packet-to-frame map. "Never started" means Pending and not part of the job array that TryStart published; that
        // array is published under the registry lock, which this decision holds too. A job that is merely Pending is not
        // enough: once TryStart published its array, every job in it is about to be started, so removing and disposing one
        // here would drop a source (and its random-access entry) that is then started anyway, or dispose a job that has
        // just started. Such a source takes the cancel path below. (A session that was shut down from Idle never
        // publishes an array, so its Pending sources are removed here as before.)
        bool removeNow = false;
        lock (_RegistryLock)
        {
            if (entry.Job.Status == JobStatus.Pending && Array.IndexOf(_SourceJobs, entry.Job) < 0)
            {
                // Removing the entry from the list is the claim: of several concurrent unsubscribes of the same source only
                // the one whose removal succeeds disposes it; the others find it gone.
                if (!_SourceEntries.Remove(entry))
                {
                    failure = new SessionFailure(SessionErrorCode.JobNotFound, "The job is not registered in this session.");
                    return false;
                }

                removeNow = true;
                entry.Info.ClearStopCallback();
                _ = _SourceInfos.Remove(entry.Info);
                _ = _AllJobs.Remove(entry.JobInfo);
                _RemoveRandomAccessSource(entry.Info.Id);
            }
        }

        failure = null;
        if (removeNow)
        {
            // Out of every registry, so nothing can reach the source or its job any more; dispose them outside the lock.
            entry.Source.Dispose();
            entry.Job.Dispose();
            _NotifyAllListeners(NotifyFlags.JobRemoved);
            return true;
        }

        // Clear the stop callback: IsStoppable becomes false and a later FrameSourceInfo.Stop() is a no-op.
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
        // double-start. A failed start (the job is already Failed, RunLoop never runs) has told the listener in TryStart.
        if (slot.Status == JobStatus.Pending)
        {
            _ = slot.TryStart(out _);
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

    /// <summary>Throws <see cref="SessionErrorCode.Disposed"/> once <see cref="Dispose"/> has begun.</summary>
    /// <exception cref="SessionException"><see cref="Dispose"/> was called.</exception>
    private void _ThrowIfDisposed()
    {
        if (_Disposed)
        {
            throw new SessionException(SessionErrorCode.Disposed, "The session has been disposed.");
        }
    }

    #endregion

    #region Nested types

    /// <summary>
    /// Snapshot of the random-access source table for one read call, plus a one-entry cache of the last hit.
    /// A read loop over packets of one source then does one table lookup instead of one per id.
    /// <para>
    /// Thread-safety: a mutable value that lives on the calling thread's stack for one call and is passed
    /// by <see langword="ref"/>; it is never shared. The table itself is an immutable copy-on-write snapshot.
    /// Callers must pass it by <see langword="ref"/>: a by-value copy would discard the cached hit after each call.
    /// Only hits are cached; a miss repeats the table search, which is cheap for the few sources a session has.
    /// </para>
    /// </summary>
    private struct RandomAccessLookup(RandomAccessSourceTable sources)
    {
        private readonly RandomAccessSourceTable _Sources = sources;
        private FrameSourceId _LastId = FrameSourceId.Invalid;
        private IRandomAccessFrameSource? _LastSource;

        /// <summary>Looks up the random-access source of <paramref name="sourceId"/>; false when it has none.</summary>
        internal bool TryGet(FrameSourceId sourceId, [NotNullWhen(true)] out IRandomAccessFrameSource? source)
        {
            if (_LastSource is not null && _LastId == sourceId)
            {
                source = _LastSource;
                return true;
            }

            if (!_Sources.TryGet(sourceId, out source))
            {
                return false;
            }

            _LastId = sourceId;
            _LastSource = source;
            return true;
        }
    }

    #endregion
}
