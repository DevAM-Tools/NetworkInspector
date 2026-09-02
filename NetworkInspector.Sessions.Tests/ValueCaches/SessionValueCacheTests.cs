// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Session ingest value cache, runtime <see cref="IValueCacheListener"/> slots, Restart rebind,
/// and RedissectOnly fill.
/// </summary>
internal sealed class SessionValueCacheTests
{
    #region Helpers

    private static ValueCacheRequest _UdpPortRequest(ValueCacheLimits limits = default) =>
        new()
        {
            FieldNames = ["udp.srcport"],
            Limits = limits.MaxRowCount is null && limits.MaxBytes is null
                ? ValueCacheLimits.Unlimited
                : limits,
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

        public void OnNewRows(ISessionReader session, ValueCacheReaderView cache, int fromIndex, int toIndexExclusive)
        {
            if (!cache.TryGetSeries<ulong>("udp.srcport", out ValueCacheSeries<ulong>? series) || series is null)
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

        public void OnNewRows(ISessionReader session, ValueCacheReaderView cache, int fromIndex, int toIndexExclusive)
        {
            Volatile.Write(ref _CallbackThreadId, Environment.CurrentManagedThreadId);
            Interlocked.Add(ref _RowsSeen, toIndexExclusive - fromIndex);
            if (cache.TryGetSeries<ulong>("udp.srcport", out ValueCacheSeries<ulong>? series) && series is not null)
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

        public void OnNewRows(ISessionReader session, ValueCacheReaderView cache, int fromIndex, int toIndexExclusive)
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ValueCacheReaderView? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        ValueCacheSeries<ulong> series = ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value);
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= frameCount);

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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ValueCacheReaderView? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        await Assert.That(ingest!.Value.Series.Count).IsGreaterThan(0);
        session.Shutdown();
    }

    [Test]
    public async Task Ingest_MaxRowCount_StopsAtCapacity()
    {
        const int frameCount = 5;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(
            stack,
            new SessionOptions { ValueCache = _UdpPortRequest(new ValueCacheLimits(MaxRowCount: 2, MaxBytes: null)) });
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ValueCacheReaderView? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        ValueCacheSeries<ulong> series = ingest!.Value.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value);
        int count = series.Count;
        bool reached = ingest.Value.IsCapacityReached;
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(reached).IsTrue();
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        await Assert.That(session.PacketIndex.HasValue).IsTrue();
        ValueCacheReaderView? ingest = session.IngestValueCache;
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        WaitHelper.WaitUntil(() => session.PacketCount >= frameCount);

        bool added = session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= session.PacketCount);

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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        WaitHelper.WaitUntil(() => session.PacketCount >= 2);

        session.TryAddValueCache(listener, _UdpPortRequest(), out _);
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
        live.TryAddFrameSource(source2, out _);
        bool firstAdded = live.TryAddValueCache(first, _UdpPortRequest(), out ValueCacheInfo? info1);
        bool secondAdded = live.TryAddValueCache(second, _UdpPortRequest(), out ValueCacheInfo? info2);
        live.TryStart();
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
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(
            listener,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out ValueCacheInfo? info);
        session.TryStart();
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
            _ = session.TryAddValueCache(new EmptyNameListener(), _UdpPortRequest(), out _);
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
    public async Task TryAddValueCache_WhenStopped_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(2);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool added = session.TryAddValueCache(new RecordingValueCacheListener(), _UdpPortRequest(), out ValueCacheInfo? info);
        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
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
        session.TryAddFrameSource(source, out _);
        bool added = session.TryAddValueCache(runtime, _UdpPortRequest(), out ValueCacheInfo? runtimeInfo);
        session.TryStart();
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => runtime.RowsSeen >= frameCount);

        ValueCacheReaderView oldIngest = session.IngestValueCache!.Value;
        await Assert.That(added).IsTrue();
        await Assert.That(oldIngest.IsAbandoned).IsFalse();

        session.Restart(registry => TestHarness.CreateStack(registry));
        WaitHelper.WaitUntil(() => runtime.StackChangedCount >= 1 && runtime.RowsSeen >= frameCount * 2);

        await Assert.That(oldIngest.IsAbandoned).IsTrue();
        ValueCacheReaderView newIngest = session.IngestValueCache!.Value;
        await Assert.That(newIngest.IsAbandoned).IsFalse();
        await Assert.That(session.GetValueCaches().Contains(runtimeInfo!)).IsTrue();
        session.Shutdown();
    }

    [Test]
    public async Task RedissectOnly_RuntimeCacheStillFills()
    {
        const int frameCount = 5;
        RecordingValueCacheListener listener = new();
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, SessionOptions.RedissectOnly);
        session.TryAddFrameSource(source, out _);
        session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        session.TryStart();
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
        session.TryAddFrameSource(source, out _);
        session.TryAddValueCache(listener, _UdpPortRequest(), out ValueCacheInfo? info);
        session.TryStart();
        WaitHelper.WaitUntil(() => listener.RowsSeen >= 3);

        int countBefore = info!.Cache.GetSeries<ulong>(stack.GetFieldId("udp.srcport")!.Value).Count;
        info.Unsubscribe();
        WaitHelper.WaitUntil(() => listener.UnsubscribedCount >= 1);

        WaitHelper.WaitUntil(() => session.PacketCount > 3);
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
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        ValueCacheReaderView? ingest = session.IngestValueCache;
        await Assert.That(ingest.HasValue).IsTrue();
        ValueCacheStringSeries text = ingest!.Value.GetCustomTextSeries(proto.NumberId);
        bool gotText = text.TryGetAsString(0, out string first);
        bool hasPayload = ingest.Value.TryGetSeries<ulong>("vcx.text", out ValueCacheSeries<ulong>? payload);
        await Assert.That(text.Count).IsEqualTo(2);
        await Assert.That(gotText).IsTrue();
        await Assert.That(first).IsEqualTo("hello");
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
