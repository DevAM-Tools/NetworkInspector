// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared session value-cache profiling loops. Each call constructs a new
/// <see cref="Session"/> so warmup and previous iterations cannot reuse a writer.
/// Ingest waits with <see cref="ISession.WaitForCompletion"/> plus a <see cref="SpinWait"/>
/// poll for published rows (not <see cref="Thread.Sleep(int)"/>, which is ~15 ms on Windows).
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
    /// Starts a session with <see cref="SessionOptions.ValueCache"/>, ingests <paramref name="frames"/>,
    /// waits until packet count and <c>udp.srcport</c> rows reach the batch size.
    /// </summary>
    internal static void RunIngest(
        Stack stack,
        Frame[] frames,
        ValueCacheRequest request,
        int expectedUdpSrcPortRows)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUdpSrcPortRows);

        using Session session = new(stack, new SessionOptions { ValueCache = request });
        using MemoryFrameSource source = new(frames);
        if (!session.TryAddFrameSource(source, out _))
        {
            throw new InvalidOperationException("Failed to add memory frame source.");
        }

        if (!session.TryStart())
        {
            throw new InvalidOperationException("Failed to start session.");
        }

        try
        {
            if (!session.WaitForCompletion(WaitTimeout))
            {
                throw new TimeoutException(
                    FormattableString.Invariant(
                        $"Session ingest WaitForCompletion timed out before PacketCount {frames.Length.ToString(CultureInfo.InvariantCulture)}."));
            }

            _WaitUntil(
                () => _UdpSrcPortRowCount(session.IngestValueCache) >= expectedUdpSrcPortRows,
                WaitTimeout,
                () => _IngestFillTimeoutMessage(session, expectedUdpSrcPortRows));
        }
        finally
        {
            session.Shutdown();
        }
    }

    /// <summary>
    /// Stores <paramref name="frames"/> with no ingest cache, then
    /// <see cref="ISession.TryAddValueCache"/> and waits for PullFill backfill.
    /// The trigger source stays open so the session remains Running during fill.
    /// </summary>
    internal static void RunOndemand(
        Stack stack,
        Frame[] frames,
        Frame triggerFrame,
        ValueCacheRequest request,
        string listenerUiName,
        int expectedUdpSrcPortRows)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerUiName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUdpSrcPortRows);

        using Session session = new(stack);
        using MemoryFrameSource source = new(frames);
        using TriggerFrameSource trigger = new(triggerFrame);
        ValueCacheFillListener listener = new(listenerUiName, expectedUdpSrcPortRows);
        try
        {
            if (!session.TryAddFrameSource(source, out _))
            {
                throw new InvalidOperationException("Failed to add memory frame source.");
            }

            if (!session.TryAddFrameSource(trigger, out _))
            {
                throw new InvalidOperationException("Failed to add trigger frame source.");
            }

            if (!session.TryStart())
            {
                throw new InvalidOperationException("Failed to start session.");
            }

            _WaitUntil(
                () => session.PacketCount >= frames.Length,
                WaitTimeout,
                "session PacketCount did not reach the ingested batch.");

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

    private static int _UdpSrcPortRowCount(ValueCacheReaderView? view)
    {
        if (view is not ValueCacheReaderView cache)
        {
            return 0;
        }

        FieldId? portId = cache.Stack.GetFieldId("udp.srcport");
        if (portId is null)
        {
            return 0;
        }

        IReadOnlyList<ValueCacheSeries> seriesList = cache.Series;
        for (int i = 0; i < seriesList.Count; i++)
        {
            ValueCacheSeries series = seriesList[i];
            if (series.FieldId == portId.Value)
            {
                return series.Count;
            }
        }

        return 0;
    }

    private static string _IngestFillTimeoutMessage(Session session, int expected)
    {
        ValueCacheReaderView? view = session.IngestValueCache;
        int rows = _UdpSrcPortRowCount(view);
        int seriesCount = view is ValueCacheReaderView cache ? cache.Series.Count : 0;
        return FormattableString.Invariant(
            $"Ingest value cache did not reach {expected} udp.srcport rows. PacketCount={session.PacketCount}, seriesCount={seriesCount}, udp.srcport rows={rows}, phase={session.Phase}.");
    }

    #endregion
}
