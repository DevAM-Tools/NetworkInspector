// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Value-cache slot behaviour around a stack swap, a rejected parse, and invalid requests: the interleavings that left a
/// slot with a cache of a retired stack, delivered ids that were never recorded, or reported the wrong error code.
/// </summary>
internal sealed class SessionValueCacheRaceTests
{
    #region Helpers

    /// <summary>Name list that runs a callback once, when the session enumerates it for the first time (it is building the cache).</summary>
    private sealed class HookedNameList(string name, Action onFirstEnumerate) : IReadOnlyList<string>
    {
        private int _Enumerated;

        public int Count => 1;

        public string this[int index] => index == 0
            ? name
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            if (Interlocked.Exchange(ref _Enumerated, 1) == 0)
            {
                onFirstEnumerate();
            }

            yield return name;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Value-cache listener recording the rows window, stack changes and the unsubscribe callback.</summary>
    private sealed class RowsListener : IValueCacheListener
    {
        private int _RowsSeen;
        private int _StackChanged;

        public string UiName => "rows";

        internal int RowsSeen => Volatile.Read(ref _RowsSeen);

        internal int StackChangedCount => Volatile.Read(ref _StackChanged);

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive) =>
            Interlocked.Add(ref _RowsSeen, toIndexExclusive - fromIndex);

        public void OnStackChanged(ISessionReader session) => Interlocked.Increment(ref _StackChanged);
    }

    /// <summary>
    /// Random-access source that returns an invalid (default) frame for one id when it is re-read, so the pull fill gets a
    /// frame the parser rejects. Ingest reads through <c>NextFrame</c>, which stays valid.
    /// </summary>
    private sealed class InvalidFrameOnReadSource(TestFrameSource inner, int invalidId) : IRandomAccessFrameSource
    {
        public string UiName => inner.UiName;

        public string? Description => inner.Description;

        public int? EstimatedFrameCount => inner.EstimatedFrameCount;

        public bool IsRunning => inner.IsRunning;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => inner.Start(sourceId, registry);

        public Frame? NextFrame(CancellationToken cancellationToken = default) => inner.NextFrame(cancellationToken);

        public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) =>
            id.Value == invalidId
                ? default(Frame)
                : inner.FrameById(id, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private static SessionException? _Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (SessionException ex)
        {
            return ex;
        }
    }

    #endregion

    #region Stack swap between build and registration

    [Test]
    public async Task TryAddValueCacheBuiltBeforeARestartIsRebuiltOnTheNewStack()
    {
        // Interleaving: the cache is built on the old stack (the session reads the request names while it builds), a
        // Restart then swaps the stack and finishes, and only afterwards the slot is registered. Restart never saw the
        // slot, so nothing rebinds it; a fill with the stale cache is rejected by the parser (cache of another stack).
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => session.Phase == SessionPhase.Stopped);
        RowsListener listener = new();
        HookedNameList names = new("udp.srcport", () => session.Restart(registry => TestHarness.CreateStack(registry)));

        bool added = session.TryAddValueCache(
            listener,
            new ValueCacheRequest { FieldNames = names },
            out ValueCacheInfo? info,
            out _);

        await Assert.That(added).IsTrue();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);
        bool hasSeries = info!.Cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series);
        await Assert.That(hasSeries).IsTrue();
        await Assert.That(series.Count).IsEqualTo(frameCount);
        await Assert.That(info.Cache.IsAbandoned).IsFalse();
        await Assert.That(listener.StackChangedCount).IsGreaterThanOrEqualTo(1);
    }

    #endregion

    #region Rejected parse

    [Test]
    public async Task PullFillRejectedParseFailsTheSlotInsteadOfMarkingTheRestOfTheWindowDelivered()
    {
        // The parser rejects the re-read frame of id 5. Counting the whole window as delivered would report ids 5..9 as rows
        // although 6..9 were never recorded; the documented contract is that an unreadable frame fails the slot.
        const int frameCount = 10;
        const int badId = 5;
        using Stack stack = TestHarness.CreateStack();
        using InvalidFrameOnReadSource source = new(TestFrameSource.WithUdpFrames(frameCount), badId);
        RowsListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        bool added = session.TryAddValueCache(
            listener,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out ValueCacheInfo? info,
            out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        JobInfo cacheJob = info!.Job!;

        WaitHelper.WaitUntil(() => cacheJob.Status == JobStatus.Failed || listener.RowsSeen >= frameCount);

        await Assert.That(added).IsTrue();
        await Assert.That(cacheJob.Status).IsEqualTo(JobStatus.Failed);
        await Assert.That(listener.RowsSeen).IsLessThan(frameCount);
        bool hasSeries = info.Cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series);
        await Assert.That(hasSeries).IsTrue();
        await Assert.That(series.Count).IsEqualTo(badId);
    }

    #endregion

    #region Error codes

    [Test]
    public async Task OutOfRangeChunkShiftIsReportedAsInvalidRequest()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionException? thrown = _Capture(() => session.TryAddValueCache(
            new RowsListener(),
            new ValueCacheRequest { FieldNames = ["udp.srcport"], ChunkShift = 2 },
            out _,
            out _));

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidRequest);
    }

    [Test]
    public async Task EmptyRequestIsReportedAsInvalidRequest()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionException? thrown = _Capture(() => session.TryAddValueCache(
            new RowsListener(),
            new(),
            out _,
            out _));

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidRequest);
    }

    [Test]
    public async Task FieldThatRecordsNothingIsReportedAsInvalidRequest()
    {
        using Stack stack = TestHarness.CreateStack();

        SessionException? thrown = _Capture(() => _ = new Session(
            stack,
            new SessionOptions
            {
                ValueCache = new ValueCacheRequest
                {
                    Fields = [new ValueCacheFieldRequest { FieldName = "udp.srcport", RecordValue = false }],
                },
            }));

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidRequest);
    }

    [Test]
    public async Task UnknownFieldNameStillReportsUnknownField()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        SessionException? thrown = _Capture(() => session.TryAddValueCache(
            new RowsListener(),
            new ValueCacheRequest { FieldNames = ["no.such.field"] },
            out _,
            out _));

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
    }

    #endregion
}
