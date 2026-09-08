// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared session value-cache profiling loops. Construction lives in
/// <see cref="StartIngest"/> / <see cref="StartOndemand"/> so the runner can put it in
/// <c>PrepareIteration</c>. <see cref="WaitIngest"/> / <see cref="CompleteOndemand"/> are the timed work.
/// </summary>
internal static class SessionValueCacheHarness
{
    #region Constants

    /// <summary>Upper bound for ingest drain and on-demand backfill waits.</summary>
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    #endregion

    #region Public API

    /// <summary>
    /// Extra frame used to raise <c>NewPackets</c> after <see cref="ISession.TryAddValueCache"/>
    /// so a PullFill slot backfills from packet 0. Frame id is <c>frames.Length</c>.
    /// </summary>
    internal static Frame CreateTriggerFrame(Stack stack, Frame[] frames)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Length);

        ParseResult<Frame> trigger = Frame.Create(
            new FrameId(frames.Length),
            Timestamp.FromSecs(frames.Length),
            frames[0].Data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry);
        if (!trigger.TryGetValue(out Frame triggerFrame))
        {
            throw new InvalidOperationException("Failed to create the NewPackets trigger frame.");
        }

        return triggerFrame;
    }

    /// <summary>
    /// Constructs and starts a session that skip-ingests <paramref name="frames"/> through a
    /// sequential source (session wraps it). Caller must <see cref="WaitIngest"/> or dispose.
    /// </summary>
    internal static Session StartIngest(
        Stack stack,
        Frame[] frames,
        ValueCacheRequest request,
        SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(request);

        SessionOptions resolved = options ?? SessionOptions.Default;
        Session session = new(
            stack,
            new SessionOptions
            {
                IndexPackets = resolved.IndexPackets,
                ValueCache = request,
                ValueCacheListener = resolved.ValueCacheListener,
            });
        SequentialMemoryFrameSource source = new(frames);
        if (!session.TryAddFrameSource(source, out _))
        {
            session.Dispose();
            throw new InvalidOperationException("Failed to add sequential frame source.");
        }

        if (!session.TryStart())
        {
            session.Dispose();
            throw new InvalidOperationException("Failed to start session.");
        }

        return session;
    }

    /// <summary>
    /// Timed ingest wait: <see cref="ISession.WaitForCompletion"/> plus row poll, then shutdown.
    /// </summary>
    internal static void WaitIngest(Session session, int expectedUdpSrcPortRows)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUdpSrcPortRows);

        try
        {
            if (!session.WaitForCompletion(WaitTimeout))
            {
                throw new TimeoutException(
                    FormattableString.Invariant(
                        $"Session ingest WaitForCompletion timed out before PacketCount {session.PacketCount.ToString(CultureInfo.InvariantCulture)}."));
            }

            _WaitUntil(
                () => _UdpSrcPortRowCount(session.IngestValueCache) >= expectedUdpSrcPortRows,
                WaitTimeout,
                () => _IngestFillTimeoutMessage(session, expectedUdpSrcPortRows));
        }
        finally
        {
            session.Shutdown();
            session.Dispose();
        }
    }

    /// <summary>
    /// Starts a session, ingests the batch, waits until <see cref="ISessionReader.PacketCount"/> reaches
    /// the batch size. Does not add the on-demand cache. Caller must <see cref="CompleteOndemand"/>.
    /// </summary>
    internal static Session StartOndemand(
        Stack stack,
        Frame[] frames,
        Frame triggerFrame,
        SessionOptions? options,
        out TriggerFrameSource trigger)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(frames);

        SessionOptions resolved = options ?? SessionOptions.Default;
        Session session = new(
            stack,
            new SessionOptions
            {
                IndexPackets = resolved.IndexPackets,
            });
        SequentialMemoryFrameSource source = new(frames);
        trigger = new TriggerFrameSource(triggerFrame);
        bool triggerAdded = false;
        try
        {
            if (!session.TryAddFrameSource(source, out _))
            {
                throw new InvalidOperationException("Failed to add sequential frame source.");
            }

            if (!session.TryAddFrameSource(trigger, out _))
            {
                throw new InvalidOperationException("Failed to add trigger frame source.");
            }

            triggerAdded = true;

            if (!session.TryStart())
            {
                throw new InvalidOperationException("Failed to start session.");
            }

            _WaitUntil(
                () => session.PacketCount >= frames.Length,
                WaitTimeout,
                "session PacketCount did not reach the ingested batch.");
        }
        catch
        {
            session.Shutdown();
            session.Dispose();
            if (!triggerAdded)
            {
                trigger.Dispose();
            }

            throw;
        }

        return session;
    }

    /// <summary>
    /// Timed on-demand fill: <see cref="ISession.TryAddValueCache"/>, trigger release, wait for rows.
    /// </summary>
    internal static void CompleteOndemand(
        Session session,
        TriggerFrameSource trigger,
        ValueCacheRequest request,
        string listenerUiName,
        int expectedUdpSrcPortRows)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerUiName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUdpSrcPortRows);

        ValueCacheFillListener listener = new(listenerUiName, expectedUdpSrcPortRows);
        try
        {
            if (!session.TryAddValueCache(listener, request, out _))
            {
                throw new InvalidOperationException(
                    "TryAddValueCache returned false — session must stay Running for on-demand add.");
            }

            trigger.Release();
            if (!listener.Filled.Wait(WaitTimeout))
            {
                throw new TimeoutException(
                    FormattableString.Invariant(
                        $"On-demand value cache did not reach {expectedUdpSrcPortRows.ToString(CultureInfo.InvariantCulture)} udp.srcport rows."));
            }
        }
        finally
        {
            session.Shutdown();
            session.Dispose();
            listener.Filled.Dispose();
        }
    }

    #endregion

    #region Private helpers

    private static void _WaitUntil(Func<bool> condition, TimeSpan timeout, string message) =>
        _WaitUntil(condition, timeout, () => message);

    private static void _WaitUntil(Func<bool> condition, TimeSpan timeout, Func<string> message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        SpinWait spinner = default;
        while (!condition())
        {
            if (timer.Elapsed > timeout)
            {
                throw new TimeoutException(message());
            }

            spinner.SpinOnce();
        }
    }

    private static int _UdpSrcPortRowCount(ReadOnlyValueCache? view)
    {
        if (view is not ReadOnlyValueCache cache)
        {
            return 0;
        }

        if (cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series))
        {
            return series.Count;
        }

        return 0;
    }

    private static string _IngestFillTimeoutMessage(Session session, int expected)
    {
        ReadOnlyValueCache? view = session.IngestValueCache;
        int rows = _UdpSrcPortRowCount(view);
        int seriesCount = view is ReadOnlyValueCache cache ? cache.Series.Count : 0;
        return FormattableString.Invariant(
            $"Ingest value cache did not reach {expected} udp.srcport rows. PacketCount={session.PacketCount}, seriesCount={seriesCount}, udp.srcport rows={rows}, phase={session.Phase}.");
    }

    #endregion
}
