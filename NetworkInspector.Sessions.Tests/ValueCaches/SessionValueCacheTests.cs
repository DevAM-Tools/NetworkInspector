// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Session ingest value cache, runtime <see cref="IValueCacheListener"/> slots, Restart rebind,
/// and RedissectOnly fill.
/// </summary>
internal sealed class SessionValueCacheTests
{
    #region Helpers

    private static ValueCacheRequest _UdpPortRequest() =>
        new()
        {
            FieldNames = ["udp.srcport"],
        };

    private static (Stack Stack, CustomTextSessionProtocol Proto) _CreateCustomTextStack()
    {
        SettingsManager? settingsManager = new();
        try
        {
            StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
            FrameProtocol frame = new();
            ProtocolId frameId = builder.RegisterProtocol(frame);
            frame.RegisterFields(builder, frameId);
            CustomTextSessionProtocol proto = new();
            ProtocolId protocolId = builder.RegisterProtocol(proto);
            proto.RegisterFields(builder, protocolId);
            builder.RegisterParserInU64TableByName(
                FrameProtocol.LinkTypeTableName,
                (ulong)LinkType.Ethernet,
                protocolId);
            Stack stack = builder.Build();
            settingsManager = null;
            return (stack, proto);
        }
        finally
        {
            settingsManager?.Dispose();
        }
    }

    #endregion

    #region First-caller sample

    /// <summary>Plan first-caller sample: udp.srcport pull from OnNewRows.</summary>
    private sealed class UdpPortCacheListener : IValueCacheListener
    {
        public string UiName => "udp src ports";

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
            if (!cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series))
            {
                return;
            }

            int count = series.Count;
            for (int i = _Seen; i < count; i++)
            {
                _ = series[i].PacketId;
            }

            _Seen = count;
            _ = fromIndex;
            _ = toIndexExclusive;
            _ = session;
        }

        internal int Seen => _Seen;

        private int _Seen;
    }

    #endregion

    #region Recording listener

    private sealed class RecordingValueCacheListener(string uiName = "vc-listener") : IValueCacheListener
    {
        private readonly List<(int From, int To)> _Windows = [];
        private readonly object _Lock = new();
        private int _RowsSeen;
        private int _StackChanged;
        private int _Unsubscribed;
        private int _CallbackThreadId;
        private int _SeriesCount;

        public string UiName { get; } = uiName;

        internal int RowsSeen => Volatile.Read(ref _RowsSeen);
        internal int StackChangedCount => Volatile.Read(ref _StackChanged);
        internal int UnsubscribedCount => Volatile.Read(ref _Unsubscribed);
        internal int CallbackThreadId => Volatile.Read(ref _CallbackThreadId);
        internal int SeriesCount => Volatile.Read(ref _SeriesCount);

        internal (int From, int To)[] Windows
        {
            get
            {
                lock (_Lock)
                {
                    return [.. _Windows];
                }
            }
        }

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
            Volatile.Write(ref _CallbackThreadId, Environment.CurrentManagedThreadId);
            Interlocked.Add(ref _RowsSeen, toIndexExclusive - fromIndex);
            if (cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series))
            {
                Volatile.Write(ref _SeriesCount, series.Count);
            }

            lock (_Lock)
            {
                _Windows.Add((fromIndex, toIndexExclusive));
            }

            _ = session;
        }

        public void OnStackChanged(ISessionReader session)
            => Interlocked.Increment(ref _StackChanged);

        public void OnUnsubscribed()
            => Interlocked.Increment(ref _Unsubscribed);
    }

    private sealed class EmptyNameListener : IValueCacheListener
    {
        public string UiName => "   ";

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
        }
    }

    #endregion

    #region Ingest

    [Test]
    public async Task Ingest_UdpSrcPort_RecordsDensePacketIds()
    {
        const int frameCount = 8;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { ValueCache = _UdpPortRequest() });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        ReadOnlyValueCacheSeries<ulong> series = ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value);
        int count = series.Count;
        bool increasing = ingest.Value.PacketIdsStrictlyIncreasing;
        await Assert.That(count).IsEqualTo(frameCount);
        await Assert.That(increasing).IsTrue();
        for (int i = 0; i < count; i++)
        {
            await Assert.That(series[i].PacketId).IsEqualTo(i);
        }
    }

    [Test]
    public async Task Ingest_WithListener_OnNewRowsCoversPacketRange()
    {
        const int frameCount = 10;
        RecordingValueCacheListener listener = new();
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = _UdpPortRequest(),
                ValueCacheListener = listener,
            });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        // RowsSeen is published before the window list; wait for both so the assert cannot race.
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount && listener.Windows.Length > 0);

        (int From, int To)[] windows = listener.Windows;
        int covered = 0;
        foreach ((int from, int to) in windows)
        {
            await Assert.That(from).IsEqualTo(covered);
            covered = to;
        }

        await Assert.That(covered).IsEqualTo(frameCount);
    }

    [Test]
    public async Task Ingest_RecordAllFields_CompletesOnUdpFixture()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions { ValueCache = new ValueCacheRequest { RecordAllFields = true } });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        await Assert.That(ingest!.Value.Series.Count).IsGreaterThan(0);
        session.Shutdown();
    }

    [Test]
    public async Task Ingest_WithIndex_PacketIndexAndSeriesPopulated()
    {
        const int frameCount = 4;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions { ValueCache = _UdpPortRequest(), IndexPackets = true });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        await Assert.That(session.PacketIndex.HasValue).IsTrue();
        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        await Assert.That(ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count)
            .IsEqualTo(frameCount);
    }

    #endregion

    #region Runtime

    [Test]
    public async Task TryAddValueCache_AfterHistory_FillsFromZero()
    {
        const int frameCount = 6;
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(frameCount);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        WaitHelper.WaitUntil(() => session.PacketCount >= frameCount);

        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info, out _);
        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= session.PacketCount && listener.Windows.Length > 0);

        int packetCount = session.PacketCount;
        int lastTo = listener.Windows[^1].To;
        int seriesCount = listener.SeriesCount;
        int callbackThread = listener.CallbackThreadId;
        await Assert.That(lastTo).IsEqualTo(packetCount);
        await Assert.That(seriesCount).IsEqualTo(packetCount);
        await Assert.That(callbackThread).IsNotEqualTo(Environment.CurrentManagedThreadId);
        source.Release();
        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_WhileRunning_CatchesLiveTail()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(2);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        WaitHelper.WaitUntil(() => session.PacketCount >= 2);

        session.TryAddValueCache(listener, _UdpPortRequest(), out _, out _);
        WaitHelper.WaitUntil(() => session.PacketCount >= 4 && listener.RowsSeen >= 4);

        int seen = listener.RowsSeen;
        await Assert.That(seen).IsGreaterThanOrEqualTo(4);
        source.Release();
        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task TryAddValueCache_SameFieldsTwice_TwoIndependentCaches()
    {
        const int frameCount = 4;
        RecordingValueCacheListener first = new("a");
        RecordingValueCacheListener second = new("b");
        using Stack stack2 = TestHarness.CreateStack();
        using TestFrameSource source2 = TestFrameSource.WithUdpFrames(frameCount);
        using Session live = new(stack2);
        live.TryAddFrameSource(source2, out _, out _);
        bool firstAdded = live.TryAddValueCache(first, _UdpPortRequest(), out ValueCacheInfo? info1, out _);
        bool secondAdded = live.TryAddValueCache(second, _UdpPortRequest(), out ValueCacheInfo? info2, out _);
        live.TryStart(out _);
        live.WaitForCompletion();
        WaitHelper.WaitUntil(() => first.RowsSeen >= frameCount && second.RowsSeen >= frameCount);

        await Assert.That(firstAdded).IsTrue();
        await Assert.That(secondAdded).IsTrue();
        await Assert.That(ReferenceEquals(info1, info2)).IsFalse();
        int count1 = info1!.Cache.GetSeries<ulong>(stack2.GetFieldId("udp.srcport")!.Value).Count;
        int count2 = info2!.Cache.GetSeries<ulong>(stack2.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(count1).IsEqualTo(frameCount);
        await Assert.That(count2).IsEqualTo(frameCount);
        await Assert.That(live.GetValueCaches().Count).IsEqualTo(2);
        live.Shutdown();
    }

    [Test]
    public async Task FirstCallerSample_CompilesAndRecords()
    {
        const int frameCount = 3;
        UdpPortCacheListener listener = new();
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        bool added = session.TryAddValueCache(
            listener,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out ValueCacheInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.Seen >= frameCount);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        _ = info!.Cache;
        session.Shutdown();
    }

    #endregion

    #region Errors and phases

    [Test]
    public async Task Ctor_UnknownField_ThrowsAndDoesNotStart()
    {
        using Stack stack = TestHarness.CreateStack();
        try
        {
            _ = new Session(
                stack,
                new SessionOptions { ValueCache = new ValueCacheRequest { FieldNames = ["no.such.field"] } });
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("eth.")]
    [Arguments("1bad")]
    [Arguments("udp src")]
    public async Task Ctor_InvalidFieldName_Throws(string name)
    {
        using Stack stack = TestHarness.CreateStack();
        try
        {
            _ = new Session(
                stack,
                new SessionOptions { ValueCache = new ValueCacheRequest { FieldNames = [name] } });
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }
    }

    [Test]
    public async Task TryAddValueCache_UnknownField_DoesNotMutateSession()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        RecordingValueCacheListener listener = new();
        try
        {
            _ = session.TryAddValueCache(
                listener,
                new ValueCacheRequest { FieldNames = ["no.such.field"] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        }

        await Assert.That(session.GetValueCaches().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("eth.")]
    [Arguments("1bad")]
    [Arguments("udp src")]
    public async Task TryAddValueCache_InvalidFieldName_DoesNotMutateSession(string name)
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        RecordingValueCacheListener listener = new();
        try
        {
            _ = session.TryAddValueCache(
                listener,
                new ValueCacheRequest { FieldNames = [name] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }

        await Assert.That(session.GetValueCaches().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryAddValueCache_InvalidFieldName_OnFieldsEntry_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(
                new RecordingValueCacheListener(),
                new ValueCacheRequest
                {
                    Fields = [new ValueCacheFieldRequest { FieldName = "eth." }],
                },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }
    }

    [Test]
    public async Task TryAddValueCache_InvalidGroupName_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(
                new RecordingValueCacheListener(),
                new ValueCacheRequest { GroupNames = ["eth."] },
                out _,
                out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheInvalidFieldName);
        }
    }

    [Test]
    public async Task TryAddValueCache_EmptyUiName_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        try
        {
            _ = session.TryAddValueCache(new EmptyNameListener(), _UdpPortRequest(), out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheUiNameEmpty);
        }
    }

    [Test]
    public async Task Ctor_ListenerWithoutRequest_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        try
        {
            _ = new Session(
                stack,
                new SessionOptions { ValueCacheListener = new RecordingValueCacheListener() });
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ValueCacheListenerWithoutRequest);
        }
    }

    [Test]
    public async Task TryAddValueCacheAfterStoppedFillsRows()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(2);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info, out SessionFailure? failure);
        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(failure).IsNull();
        WaitHelper.WaitUntil(() => listener.SeriesCount == 2);
        session.Shutdown();
    }

    #endregion

    #region Restart, redissect, unsubscribe, custom text

    [Test]
    public async Task Restart_AbandonsOldWriter_KeepsRuntimeSlot()
    {
        const int frameCount = 4;
        RecordingValueCacheListener runtime = new("runtime");
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { ValueCache = _UdpPortRequest() });
        session.TryAddFrameSource(source, out _, out _);
        bool added = session.TryAddValueCache(runtime, _UdpPortRequest(), out ValueCacheInfo? runtimeInfo, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => runtime.RowsSeen >= frameCount);

        ReadOnlyValueCache oldIngest = session.IngestValueCache!.Value;
        await Assert.That(added).IsTrue();
        await Assert.That(oldIngest.IsAbandoned).IsFalse();

        session.Restart(registry => TestHarness.CreateStack(registry));
        WaitHelper.WaitUntil(() => runtime.StackChangedCount >= 1 && runtime.RowsSeen >= frameCount * 2);

        await Assert.That(oldIngest.IsAbandoned).IsTrue();
        ReadOnlyValueCache newIngest = session.IngestValueCache!.Value;
        await Assert.That(newIngest.IsAbandoned).IsFalse();
        await Assert.That(session.GetValueCaches().Contains(runtimeInfo!)).IsTrue();
        session.Shutdown();
    }

    /// <summary>
    /// The new stack cannot satisfy the ingest request. Restart must throw before it changes anything: same
    /// stack, same phase, queries enabled, old ingest cache intact, and a later restart still possible.
    /// </summary>
    [Test]
    public async Task RestartIngestFieldMissingOnNewStackLeavesSessionUsable()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        using Session session = new(stack, new SessionOptions { ValueCache = _UdpPortRequest() });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        ReadOnlyValueCache ingestBefore = session.IngestValueCache!.Value;

        SessionException? thrown = null;
        try
        {
            session.Restart(SessionFixture.BuildEthernetOnlyStack);
        }
        catch (SessionException ex)
        {
            thrown = ex;
        }

        SessionPhase phaseAfterFailure = session.Phase;
        bool queriesAfterFailure = session.QueriesEnabled;
        Stack stackAfterFailure = session.Stack;
        bool readAfterFailure = session.TryGetPacket(new PacketId(0), out Packet? packet, out _);
        bool ingestAbandoned = ingestBefore.IsAbandoned;

        // The gate and the restart flag were reset, so a valid restart still works.
        session.Restart(registry => TestHarness.CreateStack(registry));

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Code).IsEqualTo(SessionErrorCode.ValueCacheUnknownField);
        await Assert.That(phaseAfterFailure).IsEqualTo(SessionPhase.Stopped);
        await Assert.That(queriesAfterFailure).IsTrue();
        await Assert.That(stackAfterFailure).IsSameReferenceAs(stack);
        await Assert.That(readAfterFailure).IsTrue();
        await Assert.That(packet).IsNotNull();
        await Assert.That(ingestAbandoned).IsFalse();
        await Assert.That(session.PacketCount).IsEqualTo(3);
        await Assert.That(session.QueriesEnabled).IsTrue();
        await Assert.That(session.Stack).IsNotSameReferenceAs(stack);
        session.Shutdown();
    }

    /// <summary>
    /// A restart disables queries while a PullFill slot is in the middle of a window. The slot must stop at the
    /// refused id (a paused session is not a missing frame), survive, and refill everything after the restart.
    /// </summary>
    [Test]
    public async Task RestartWhilePullFillRunsKeepsTheSlot()
    {
        const int frameCount = 6;
        using Stack stack = TestHarness.CreateStack();
        using HookRandomAccessSource source = new(TestFrameSource.WithUdpFrames(frameCount));
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        (ValueCacheInfo info, (int From, int To)[] beforeRestart, Exception? restartError) =
            _RestartWhileSlotFills(session, source, listener);
        WaitHelper.WaitUntil(
            () => (listener.StackChangedCount >= 1 && listener.SeriesCount == frameCount) || listener.UnsubscribedCount > 0);

        (int From, int To)[] windows = listener.Windows;

        await Assert.That(restartError).IsNull();
        await Assert.That(beforeRestart.Length).IsEqualTo(1);
        await Assert.That(beforeRestart[0]).IsEqualTo((0, 3));
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(0);
        await Assert.That(info.Job!.Status).IsEqualTo(JobStatus.Running);
        await Assert.That(listener.StackChangedCount).IsEqualTo(1);
        await Assert.That(windows[^1]).IsEqualTo((0, frameCount));
        await Assert.That(listener.SeriesCount).IsEqualTo(frameCount);
        session.Shutdown();
    }

    /// <summary>
    /// Pins the slot inside id 2 of its first window, starts a restart that disables queries but cannot take
    /// the parse lock held here (so it cannot abandon the writer yet), then lets the slot run into the refused
    /// read at id 3. Runs on one thread without <c>await</c>: the parse lock is a monitor.
    /// </summary>
    private static (ValueCacheInfo Info, (int From, int To)[] BeforeRestart, Exception? RestartError) _RestartWhileSlotFills(
        Session session,
        HookRandomAccessSource source,
        RecordingValueCacheListener listener)
    {
        using ManualResetEventSlim slotEntered = new(false);
        using ManualResetEventSlim releaseSlot = new(false);
        int armed = 1;
        source.OnFrameById = id =>
        {
            // Only the first read of id 2 blocks; the restart's re-parse reads it again later.
            if (id.Value == 2 && Interlocked.Exchange(ref armed, 0) == 1)
            {
                slotEntered.Set();
                releaseSlot.Wait();
            }
        };

        Exception? restartError = null;
        Thread restart = new(() =>
        {
            try
            {
                session.Restart(registry => TestHarness.CreateStack(registry));
            }
            catch (Exception ex)
            {
                restartError = ex;
            }
        })
        {
            Name = "restart-during-fill",
            IsBackground = true,
            CurrentCulture = CultureInfo.InvariantCulture,
        };

        object parseMutex = TestHarness.ParseMutexOf(session);
        ValueCacheInfo? info;
        (int From, int To)[] beforeRestart;
        try
        {
            if (!session.TryAddValueCache(listener, _UdpPortRequest(), out info, out _))
            {
                throw new InvalidOperationException("The value cache was rejected.");
            }

            slotEntered.Wait();
            Monitor.Enter(parseMutex);
            try
            {
                // Restart disables queries, then blocks on the parse lock held here.
                restart.Start();
                WaitHelper.WaitUntil(() => !session.QueriesEnabled);

                // The slot finishes id 2, then finds queries disabled at id 3.
                releaseSlot.Set();
                WaitHelper.WaitUntil(() => listener.Windows.Length > 0 || listener.UnsubscribedCount > 0);
                beforeRestart = listener.Windows;
            }
            finally
            {
                Monitor.Exit(parseMutex);
            }
        }
        finally
        {
            releaseSlot.Set();
            source.OnFrameById = null;
            if ((restart.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            {
                restart.Join();
            }
        }

        return (info, beforeRestart, restartError);
    }

    [Test]
    public async Task RedissectOnly_RuntimeCacheStillFills()
    {
        const int frameCount = 5;
        RecordingValueCacheListener listener = new();
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);

        int count = info!.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(count).IsEqualTo(frameCount);
        session.Shutdown();
    }

    [Test]
    public async Task Unsubscribe_RuntimeCache_StopsRecording()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(3);
        RecordingValueCacheListener listener = new();
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info, out _);
        session.TryStart(out _);
        WaitHelper.WaitUntil(() => listener.RowsSeen >= 3);

        // Unsubscribe joins the slot, which may still finish a window that was in flight. Count only after it
        // returned: from then on nothing may be recorded, however many packets arrive.
        info!.Unsubscribe();
        WaitHelper.WaitUntil(() => listener.UnsubscribedCount >= 1);
        int countBefore = info.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        int packetsAtUnsubscribe = session.PacketCount;

        WaitHelper.WaitUntil(() => session.PacketCount > packetsAtUnsubscribe + 1);
        int countAfter = info.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        await Assert.That(countAfter).IsEqualTo(countBefore);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
        source.Release();
        session.WaitForCompletion();
        session.Shutdown();
    }

    [Test]
    public async Task CustomTextRequest_RecordsTextNotPayload()
    {
        (Stack stack, CustomTextSessionProtocol proto) = _CreateCustomTextStack();
        using Stack owned = stack;
        using TestFrameSource source = new([new byte[16], new byte[16]]);
        using Session session = new(
            stack,
            new SessionOptions
            {
                ValueCache = new ValueCacheRequest
                {
                    Fields =
                    [
                        new ValueCacheFieldRequest
                        {
                            FieldName = "vcx.text",
                            RecordValue = false,
                            RecordCustomText = true,
                        },
                    ],
                },
            });
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        ReadOnlyValueCache? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        ReadOnlyValueCacheSeries<string> text = ingest!.Value.GetCustomTextSeries(proto.NumberId);
        await Assert.That(text.Count).IsEqualTo(2);
        await Assert.That(text[0].Value).IsEqualTo("hello");
        bool hasPayload = ingest.Value.TryGetSeries<ulong>("vcx.text", out ReadOnlyValueCacheSeries<ulong> payload);
        await Assert.That(hasPayload).IsFalse();
        _ = payload;
        session.Shutdown();
    }

    #endregion
}

/// <summary>Minimal protocol that appends a U64 with custom text for session ingest tests.</summary>
internal sealed class CustomTextSessionProtocol : IProtocol
{
    #region Fields

    internal FieldId NumberId;
    private int _Resets;

    #endregion

    #region Public API

    public string Name => "vcxtext";
    public string UiName => "CustomText Session";

    public void ResetParseState() => _Resets++;

    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        NumberId = builder.RegisterField(protocolId, "vcx.text", "Number", FieldType.U64);
    }

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        _ = parentField.AppendWithCustomText(NumberId, FieldValue.NewU64(1), new LazyString("hello"));
        return data.Length;
    }

    #endregion
}
