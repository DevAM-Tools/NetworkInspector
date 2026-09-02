// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.ValueCaches;

/// <summary>
/// How a <see cref="ValueCacheSlot"/> obtains rows for <see cref="IValueCacheListener.OnNewRows"/>.
/// </summary>
internal enum ValueCacheFillMode
{
    /// <summary>
    /// Slot thread calls <c>TryGetPacket</c> then <c>RecordPacket</c> for each id in the window,
    /// then notifies. Used by <c>TryAddValueCache</c>.
    /// </summary>
    PullFill = 0,

    /// <summary>
    /// Parse thread already teed the ingest writer. Slot notifies without recording.
    /// Used when both <see cref="SessionOptions.ValueCache"/> and
    /// <see cref="SessionOptions.ValueCacheListener"/> are set.
    /// </summary>
    NotifyOnly = 1,
}

/// <summary>
/// Session-internal bridge: holds the notification flags and pull cursor
/// for a single <see cref="IValueCacheListener"/>.
///
/// <para>
/// Flag protocol matches <see cref="ListenerSlot"/>: producers
/// <c>Interlocked.Or</c> flags and signal <see cref="ManualResetEventSlim"/>;
/// the slot thread <c>Interlocked.Exchange</c>s flags and pulls from
/// <see cref="ISessionReader"/>.
/// </para>
///
/// <para>
/// Thread-safety: flag field and wake event are safe for concurrent producers and one consumer.
/// The writer is single-threaded on this slot (PullFill) or on the parse thread (NotifyOnly).
/// </para>
/// </summary>
internal sealed class ValueCacheSlot : IDisposable
{
    #region Fields

    internal volatile int Flags;
    private IValueCacheListener _Listener { get; }
    private ISessionReader _SessionReader { get; }
    private ValueCacheRequest _Request { get; }
    private ValueCacheFillMode _FillMode { get; }
    private Func<Stack, ValueCache> _Rebuild { get; }
    private Func<ValueCache?> _GetIngestWriter { get; }
    private bool _StoreParsedPackets { get; }
    private Job _Job { get; }
    private readonly ManualResetEventSlim _Wake = new(initialState: false);

    private volatile ValueCache _Writer;
    private volatile int _PacketCursor;
    private volatile int _OnUnsubscribedInvoked;
    private Packet? _Recycle;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates a value-cache slot. The slot is not started until <see cref="Start"/> is called.
    /// </summary>
    internal ValueCacheSlot(
        JobId jobId,
        IValueCacheListener listener,
        ISessionReader sessionReader,
        ValueCacheRequest request,
        ValueCache writer,
        ValueCacheFillMode fillMode,
        Func<Stack, ValueCache> rebuild,
        Func<ValueCache?> getIngestWriter,
        bool storeParsedPackets,
        Action<Job, JobStatus> onStatusChanged)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(sessionReader);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(getIngestWriter);

        _Listener = listener;
        _SessionReader = sessionReader;
        _Request = request;
        _Writer = writer;
        _FillMode = fillMode;
        _Rebuild = rebuild;
        _GetIngestWriter = getIngestWriter;
        _StoreParsedPackets = storeParsedPackets;
        _Job = new Job(
            jobId,
            listener.UiName,
            fillMode == ValueCacheFillMode.NotifyOnly
                ? $"ValueCache ingest: {listener.UiName}"
                : $"ValueCache: {listener.UiName}",
            _RunLoop,
            onStatusChanged);
        Info = new JobInfo(_Job);
    }

    #endregion

    #region Identity

    /// <summary>The underlying job's identifier.</summary>
    internal JobId Id => _Job.Id;

    /// <summary>Public job view registered in the unified session job list.</summary>
    internal JobInfo Info
    {
        get;
    }

    /// <summary>
    /// Public subscription view. Set by the session after construction so
    /// <see cref="Session.TryUnsubscribe"/> can locate the matching info.
    /// </summary>
    internal ValueCacheInfo? ValueCacheInfo
    {
        get; set;
    }

    /// <summary>Fill strategy for <see cref="NotifyFlags.NewPackets"/>.</summary>
    internal ValueCacheFillMode FillMode => _FillMode;

    /// <summary>Current writer. Session may bind a new ingest writer on Restart.</summary>
    internal ValueCache Writer => _Writer;

    /// <summary>Name-based request used to rebuild the writer after a stack swap.</summary>
    internal ValueCacheRequest Request => _Request;

    #endregion

    #region Public API

    /// <summary>Starts the slot's background thread.</summary>
    internal void Start()
    {
        try
        {
            _Job.Start();
        }
        catch
        {
            EnsureOnUnsubscribed();
            throw;
        }
    }

    /// <summary>
    /// Invokes <see cref="IValueCacheListener.OnUnsubscribed"/> at most once.
    /// Used from <see cref="_RunLoop"/> and from the session coordinator when <see cref="Job.Start"/> fails.
    /// </summary>
    internal void EnsureOnUnsubscribed()
    {
        if (Interlocked.CompareExchange(ref _OnUnsubscribedInvoked, 1, 0) == 0)
        {
            _Listener.OnUnsubscribed();
        }
    }

    /// <summary>Requests cancellation of the slot and wakes the run loop.</summary>
    internal void Cancel()
    {
        _Job.Cancel();
        _Wake.Set();
    }

    /// <summary>Current job status.</summary>
    internal JobStatus Status => _Job.Status;

    /// <summary>Blocks until the job reaches a terminal state.</summary>
    internal void Join() => _Job.Join();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Wake.Dispose();
        _Job.Dispose();
    }

    /// <summary>
    /// Sets the given flag(s) on this slot and wakes the listener thread. Non-blocking, thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Notify(NotifyFlags flags)
    {
        int prior = Interlocked.Or(ref Flags, (int)flags);
        if ((prior & (int)flags) == 0)
        {
            _Wake.Set();
        }
    }

    /// <summary>
    /// Resets the packet cursor to zero so the next <see cref="NotifyFlags.NewPackets"/>
    /// dispatch delivers all packets from the beginning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetPacketCursor()
        => _PacketCursor = 0;

    /// <summary>
    /// Replaces the writer this NotifyOnly slot aliases (new ingest cache after Restart).
    /// PullFill slots ignore this; they rebuild from <see cref="_Request"/> on StackChanged.
    /// </summary>
    internal void BindWriter(ValueCache writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _Writer = writer;
        ValueCacheInfo?.SetWriter(writer);
    }

    /// <summary>Marks the current writer abandoned. Session Restart calls this for PullFill writers.</summary>
    internal void AbandonWriter() => _Writer.Abandon();

    #endregion

    #region Private helpers

    /// <summary>
    /// Wait-loop matching <see cref="ListenerSlot"/>: drain flags, wait on <see cref="_Wake"/>,
    /// dispatch. Callback exceptions propagate to <see cref="Job"/> and fail only this slot.
    /// </summary>
    private void _RunLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                _Wake.Reset();
                flags = Interlocked.Exchange(ref Flags, 0);
                if (flags != 0)
                {
                    _DispatchFlags((NotifyFlags)flags);
                    continue;
                }

                _Wake.Wait(ct);
            }

            int remaining = Interlocked.Exchange(ref Flags, 0);
            if (remaining != 0)
            {
                _DispatchFlags((NotifyFlags)remaining);
            }
        }
        finally
        {
            EnsureOnUnsubscribed();
        }
    }

    /// <summary>
    /// Dispatches flags in the same order as <see cref="ListenerSlot"/>:
    /// StackChanged, packets, sources, jobs, lifecycle.
    /// </summary>
    private void _DispatchFlags(NotifyFlags notify)
    {
        if ((notify & NotifyFlags.StackChanged) != 0)
        {
            _RebindAfterStackChange();
            _PacketCursor = 0;
            _Listener.OnStackChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.NewPackets) != 0)
        {
            int current = _SessionReader.PacketCount;
            int cursor = _PacketCursor;
            if (current > cursor)
            {
                if (_FillMode == ValueCacheFillMode.PullFill)
                {
                    _FillWindow(cursor, current);
                }

                _Listener.OnNewRows(_SessionReader, _Writer.AsReadOnlyView(), cursor, current);
                _PacketCursor = current;
            }
        }

        if ((notify & (NotifyFlags.SourceAdded | NotifyFlags.SourceCompleted)) != 0)
        {
            _Listener.OnSourcesChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.AllSourcesCompleted) != 0)
        {
            _Listener.OnAllSourcesCompleted(_SessionReader);
        }

        if ((notify & (NotifyFlags.JobAdded | NotifyFlags.JobStatusChanged | NotifyFlags.JobRemoved)) != 0)
        {
            _Listener.OnJobsChanged(_SessionReader);
        }

        if ((notify & NotifyFlags.PhaseChanged) != 0)
        {
            _Listener.OnPhaseChanged(_SessionReader.Phase);
        }

        if ((notify & NotifyFlags.ShuttingDown) != 0)
        {
            _Listener.OnShuttingDown();
        }
    }

    /// <summary>
    /// PullFill: abandon the evicted writer and construct a new cache from the stored request.
    /// NotifyOnly: alias the session's new ingest writer (already created under the parse lock).
    /// </summary>
    private void _RebindAfterStackChange()
    {
        if (_FillMode == ValueCacheFillMode.NotifyOnly)
        {
            ValueCache? ingest = _GetIngestWriter();
            if (ingest is not null)
            {
                _Writer = ingest;
                ValueCacheInfo?.SetWriter(ingest);
            }

            return;
        }

        _Writer.Abandon();
        _Writer = _Rebuild(_SessionReader.Stack);
        ValueCacheInfo?.SetWriter(_Writer);
    }

    /// <summary>
    /// Records packets <paramref name="fromId"/> .. <paramref name="toIdExclusive"/>-1 via
    /// <see cref="ISessionReader.TryGetPacket(PacketId, Packet?, out Packet?)"/> only (never
    /// <c>ReadPackets</c>). Redissect uses a slot-private recycle instance that is never published.
    /// </summary>
    private void _FillWindow(int fromId, int toIdExclusive)
    {
        for (int id = fromId; id < toIdExclusive; id++)
        {
            Packet? recycle = _StoreParsedPackets ? null : _Recycle;
            if (!_SessionReader.TryGetPacket(new PacketId(id), recycle, out Packet? packet) || packet is null)
            {
                continue;
            }

            if (_Writer.IsAbandoned)
            {
                return;
            }

            try
            {
                _Writer.RecordPacket(packet);
            }
            catch (InvalidOperationException) when (_Writer.IsAbandoned)
            {
                return;
            }
            if (!_StoreParsedPackets)
            {
                _Recycle = packet;
            }
        }
    }

    #endregion
}
