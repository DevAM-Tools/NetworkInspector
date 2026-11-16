// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <c>SessionExport.TryStart</c>: an export of a session's packet range as a stack-bound job. The outputs are compared with the
/// library pipeline (a parse plan, the filter, and the same exporter fed directly from the same frames), which is the code the
/// command line runs, so equality here is equality with the command line without starting a process. Also covers the refusals
/// (range, filter syntax), the exporter finishing exactly once in every outcome, and ingest continuing while a job runs.
/// </summary>
internal sealed class SessionExportTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Frames of 60 sizes: the UDP length is the frame size minus 34, so <c>udp.length >= 56</c> keeps the 30 larger ones.</summary>
    private const int _FrameCount = 60;

    private const string _Filter = "udp.length >= 56";

    private const int _MatchingFrames = 30;

    private static byte[][] _VariedFrames()
    {
        byte[][] frames = new byte[_FrameCount][];
        for (int i = 0; i < _FrameCount; i++)
        {
            frames[i] = TestHarness.GenerateUdpFrame(60 + i);
        }

        return frames;
    }

    /// <summary>A session over the varied capture, ingested to the end.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly TestFrameSource _Source;
        private readonly string _Directory = Path.Combine(Path.GetTempPath(), "ni-session-export-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        internal Rig(Func<TestFrameSource, IFrameSource>? wrapSource = null, byte[][]? frames = null)
        {
            _ = Directory.CreateDirectory(_Directory);
            Stack = TestHarness.CreateStack();
            byte[][] capture = frames ?? _VariedFrames();
            _Source = new TestFrameSource(capture);
            Session = new Session(Stack);
            IFrameSource registered = _Source;
            if (wrapSource is not null)
            {
                registered = wrapSource(_Source);
            }

            _ = Session.TryAddFrameSource(registered, out _, out _);
            _ = Session.TryStart(out _);
            Session.WaitForCompletion();
            WaitHelper.WaitUntil(() => Session.PacketCount == capture.Length);
        }

        internal Session Session { get; }

        internal Stack Stack { get; }

        internal string PathOf(string name) => Path.Combine(_Directory, name);

        public void Dispose()
        {
            Session.Dispose();
            Stack.Dispose();
            _Source.Dispose();
            try
            {
                Directory.Delete(_Directory, recursive: true);
            }
            catch (IOException)
            {
                // The temporary directory of a test; a file still open here is cleaned up by the operating system's temp cleanup.
            }
        }
    }

    private static ExportContent _SelectedContent(Stack stack, params string[] names)
    {
        FieldRequest[] requests = [.. (names.Length == 0 ? ["ip.src", "udp.length"] : names).Select(static name => new FieldRequest(name))];
        if (!FieldSelection.TryCreate(stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        return new ExportContent { Shape = ExportShape.SelectedFields, Fields = selection, Info = false, FrameBytes = false };
    }

    private static CsvExporter _Csv(string path, ExportContent content) =>
        CsvExporter.CreateBuilder().ToFile(path).WithContent(content).Build();

    private static DuckDbExporter _DuckDb(string path, ExportContent content) =>
        DuckDbExporter.CreateBuilder().ToFile(path).WithContent(content).Build();

    /// <summary>
    /// The library pipeline for the same frames: the plan of filter and exporter demand, one parse per frame with the plan's
    /// options, the verdict, the commit. This is the loop the command line runs.
    /// </summary>
    private static int _RunLibraryPipeline(Session session, StackView view, IPacketListener exporter, string? filterExpression)
    {
        FilterObserver? filter = null;
        ParseDemand filterDemand = ParseDemand.FramesOnly;
        if (filterExpression is not null)
        {
            FilterObserverOptions observerOptions = FilterObserverOptions.Default;
            if (!PacketFilter.TryCompile(filterExpression, view.Stack, in observerOptions, out filter, out FilterError? error))
            {
                throw new InvalidOperationException(error.Message);
            }

            filterDemand = filter.ParseDemand;
        }

        ParseDemand exporterDemand = ParseDemand.FieldTree;
        if (exporter is IParseDemandSource source)
        {
            exporterDemand = source.ParseDemand;
        }

        ParseDemand[] demands = [filterDemand, exporterDemand];
        if (!ParsePlan.TryCreate(view.Stack, demands, out ParsePlan? plan, out ParsePlanError planError))
        {
            throw new InvalidOperationException(planError.Message);
        }

        ParseOptions options = plan.Options;
        int exported = 0;
        for (int id = 0; id < session.PacketCount; id++)
        {
            if (!session.TryGetFrame(id, out Frame frame, out SessionFailure? frameFailure))
            {
                throw new InvalidOperationException(frameFailure!.Value.Message);
            }

            if (!Packet.TryParse(id, view.Stack, frame, in options, out Packet? packet, out ParseFailure parseFailure))
            {
                throw new InvalidOperationException(parseFailure.ToString());
            }

            bool matched = true;
            if (filter is not null && !filter.TryReadMatch(out matched, out FilterError? verdictFailure))
            {
                throw new InvalidOperationException(verdictFailure!.Message);
            }

            if (matched)
            {
                _ = exporter.OnPacket(packet!);
                exported++;
            }
        }

        exporter.OnFinish();
        return exported;
    }

    private static void _Dispose(IPacketListener listener)
    {
        if (listener is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static SessionExportJob _StartAndJoin(Session session, Func<StackView, IPacketListener> create, string? filter, int to = _FrameCount)
    {
        if (!SessionExport.TryStart(
            session, "export", create, 0, to, filter, out SessionExportJob? export, out SessionFailure? failure, out FilterError? filterError))
        {
            string reason = "TryStart failed.";
            if (failure is not null)
            {
                reason = failure.Value.Message;
            }
            else if (filterError is not null)
            {
                reason = filterError.Message;
            }

            throw new InvalidOperationException(reason);
        }

        export.Job.Join();
        return export;
    }

    /// <summary>A listener that counts what it is given; demands a packet-depth parse without a tree, like a tree-free exporter.</summary>
    private sealed class CountingListener(int stopAfter = 0, Action? onFirstPacket = null) : IPacketListener, IParseDemandSource
    {
        private int _Finished;
        private int _Packets;
        private int _WithTree;

        public string UiName => "counting";

        public string? Description => null;

        public ParseDemand ParseDemand => new() { Depth = ParseDepth.Packet };

        internal int Finished => Volatile.Read(ref _Finished);

        internal int Packets => Volatile.Read(ref _Packets);

        /// <summary>Packets that arrived with a field tree (the plan built one for them).</summary>
        internal int PacketsWithTree => Volatile.Read(ref _WithTree);

        public bool OnPacket(Packet packet)
        {
            if (packet.HasFieldTree)
            {
                _ = Interlocked.Increment(ref _WithTree);
            }

            int count = Interlocked.Increment(ref _Packets);
            if (count == 1)
            {
                onFirstPacket?.Invoke();
            }

            return stopAfter == 0 || count < stopAfter;
        }

        public void OnFinish() => Interlocked.Increment(ref _Finished);
    }

    #endregion

    #region Equality with the library pipeline

    [Test]
    public async Task SelectedFieldsWithAFilterWriteTheSameCsvAsTheLibraryPipelineAndCountTheMatches()
    {
        using Rig rig = new();
        StackView view = rig.Session.CurrentStack;
        string expectedPath = rig.PathOf("expected.csv");
        using CsvExporter reference = _Csv(expectedPath, _SelectedContent(rig.Stack));
        int expectedCount = _RunLibraryPipeline(rig.Session, view, reference, _Filter);

        SessionExportJob export = _StartAndJoin(rig.Session, v => _Csv(rig.PathOf("actual.csv"), _SelectedContent(v.Stack)), _Filter);
        _Dispose(export.Exporter);

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(export.Failure).IsNull();
        await Assert.That(export.VisitedCount).IsEqualTo(_FrameCount);
        await Assert.That(export.ExportedCount).IsEqualTo(_MatchingFrames);
        await Assert.That(expectedCount).IsEqualTo(_MatchingFrames);
        await Assert.That(File.ReadAllText(rig.PathOf("actual.csv"))).IsEqualTo(File.ReadAllText(expectedPath));
    }

    [Test]
    public async Task AllFieldsAndTheHierarchyWriteTheSameCsvAsTheLibraryPipeline()
    {
        using Rig rig = new();
        StackView view = rig.Session.CurrentStack;
        foreach ((ExportShape shape, string name) in new[] { (ExportShape.AllFields, "all"), (ExportShape.Hierarchy, "tree") })
        {
            ExportContent content = CsvExporter.ContentTemplate with { Shape = shape };
            string expectedPath = rig.PathOf(name + "-expected.csv");
            string actualPath = rig.PathOf(name + "-actual.csv");
            using CsvExporter reference = _Csv(expectedPath, content);
            _ = _RunLibraryPipeline(rig.Session, view, reference, null);

            SessionExportJob export = _StartAndJoin(rig.Session, _ => _Csv(actualPath, content), null);
            _Dispose(export.Exporter);

            await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
            await Assert.That(export.ExportedCount).IsEqualTo(_FrameCount);
            await Assert.That(File.ReadAllText(actualPath)).IsEqualTo(File.ReadAllText(expectedPath));
        }
    }

    [Test]
    [Category("Slow")]
    public async Task SelectedFieldsWithAFilterWriteTheSameDuckDbRowsAsTheLibraryPipeline()
    {
        using Rig rig = new();
        StackView view = rig.Session.CurrentStack;
        string expectedPath = rig.PathOf("expected.duckdb");
        string actualPath = rig.PathOf("actual.duckdb");
        using DuckDbExporter reference = _DuckDb(expectedPath, _SelectedContent(rig.Stack));
        _ = _RunLibraryPipeline(rig.Session, view, reference, _Filter);

        SessionExportJob export = _StartAndJoin(rig.Session, v => _DuckDb(actualPath, _SelectedContent(v.Stack)), _Filter);
        _Dispose(export.Exporter);

        string expected = _LengthRows(expectedPath, rig.Stack);
        string actual = _LengthRows(actualPath, rig.Stack);

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(export.ExportedCount).IsEqualTo(_MatchingFrames);
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(actual.Split(',').Length).IsEqualTo(_MatchingFrames);
    }

    private static string _LengthRows(string path, Stack stack)
    {
        int fieldId = stack.GetFieldId("udp.length")!.Value.Value; // Non-null: the standard stack defines it.
        using DuckDBConnection connection = new(Invariant($"Data Source={path}"));
        connection.Open();
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT packet_id, value FROM field_{fieldId.ToString(CultureInfo.InvariantCulture)} ORDER BY packet_id");
        using DuckDBDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt32(0)}={Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)}"));
        }

        return string.Join(',', rows);
    }

    [Test]
    public async Task AFilterTheObserverFormCannotExpressFallsBackToTheFieldTreeAndExportsTheSamePackets()
    {
        using Rig rig = new();

        // Two flank() terms are beyond the tree-free form (one flank tracker per observer); the job falls back to the tree filter,
        // as the command line does. The expected set comes from the tree filter fed directly with the same packets in id order.
        const string flankPair =
            "flank(udp.length, from: < 56, to: >= 56, within: 3packets) || flank(udp.length, from: < 70, to: >= 70, within: 3packets)";
        StackView view = rig.Session.CurrentStack;
        FilterObserverOptions observerOptions = FilterObserverOptions.Default;
        bool observerCompiles = PacketFilter.TryCompile(flankPair, view.Stack, in observerOptions, out _, out _);
        await Assert.That(PacketFilter.TryCompile(flankPair, view.Stack, out PacketFilter? reference, out _)).IsTrue();
        PacketIndex index = new(view.Stack);
        ParseOptions options = new(FieldTreeMode.Build, index: index);
        int expected = 0;
        for (int id = 0; id < _FrameCount; id++)
        {
            _ = rig.Session.TryGetPacket(id, in options, view, null, out Packet? packet, out _);
            if (reference!.TryIsMatch(packet!, index, out bool matched, out _) && matched)
            {
                expected++;
            }
        }

        CountingListener tree = new();
        SessionExportJob scoped = _StartAndJoin(rig.Session, _ => tree, flankPair);
        CountingListener plain = new();
        _ = _StartAndJoin(rig.Session, _ => plain, _Filter);

        await Assert.That(observerCompiles).IsFalse();
        await Assert.That(scoped.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(scoped.Failure).IsNull();
        await Assert.That(expected).IsGreaterThan(0);
        await Assert.That(scoped.ExportedCount).IsEqualTo(expected);
        await Assert.That(tree.Finished).IsEqualTo(1);

        // The fallback built the field tree for every packet; the observer form did not build one at all.
        await Assert.That(tree.PacketsWithTree).IsEqualTo(tree.Packets);
        await Assert.That(plain.PacketsWithTree).IsEqualTo(0);
    }

    [Test]
    public async Task AProtocolScopeFilterThatNeedsTheFieldTreeExportsEveryMatchingPacket()
    {
        using Rig rig = new();
        CountingListener listener = new();

        // The scope needs the field tree. The export's parse replays ids that ingest parsed first, so it fills no index of its own;
        // the filter must read the session's index (a private empty one made every protocol look absent and exported nothing).
        SessionExportJob export = _StartAndJoin(rig.Session, _ => listener, "$udp { udp.length >= 0 }");

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(export.Failure).IsNull();
        await Assert.That(export.ExportedCount).IsEqualTo(_FrameCount);
        await Assert.That(listener.PacketsWithTree).IsEqualTo(listener.Packets);
    }

    #endregion

    #region Guide sample

    /// <summary>The field selection the guide leaves out: names resolved on the view's stack (see the exporter guide, section 14).</summary>
    internal static ExportContent SelectedFields(Stack stack, params string[] names) =>
        _SelectedContent(stack, names);

    [Test]
    public async Task TheSessionGuideSampleExportsAFilteredRangeToCsv()
    {
        using Rig rig = new();
        Session session = rig.Session;
        string path = rig.PathOf("udp.csv");
        long exportedCount = -1;

        // --- sample start ---
        if (SessionExport.TryStart(
                session,
                "export udp",
                view => CsvExporter.CreateBuilder()
                    .ToFile(path)
                    .WithContent(SelectedFields(view.Stack, "ip.src", "udp.length"))
                    .Build(),
                0,
                session.PacketCount,
                "udp.length >= 56",
                out SessionExportJob? export,
                out SessionFailure? failure,
                out FilterError? filterError))
        {
            export.Job.Join();                                  // or poll export.VisitedCount for a progress display
            exportedCount = export.ExportedCount;
            (export.Exporter as IDisposable)?.Dispose();        // the job finished the exporter; the caller releases it
        }
        else if (filterError is not null)
        {
            Console.Error.WriteLine("filter: " + filterError.Message);
        }
        else
        {
            Console.Error.WriteLine("export refused: " + failure!.Value.Message);
        }

        // --- sample end ---
        await Assert.That(exportedCount).IsEqualTo(_MatchingFrames);
        await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(_MatchingFrames + 1);
    }

    #endregion

    #region Refusals

    [Test]
    public async Task ARangePastThePacketCountIsRefusedSynchronouslyAndNoExporterIsCreated()
    {
        using Rig rig = new();
        int created = 0;

        bool started = SessionExport.TryStart(
            rig.Session,
            "export",
            _ =>
            {
                created++;
                return new CountingListener();
            },
            0,
            _FrameCount + 1,
            null,
            out SessionExportJob? export,
            out SessionFailure? failure,
            out FilterError? filterError);

        await Assert.That(started).IsFalse();
        await Assert.That(export).IsNull();
        await Assert.That(filterError).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.PacketNotFound);
        await Assert.That(failure.Value.Message).Contains("61");
        await Assert.That(failure.Value.Message).Contains("60 announced");
        await Assert.That(created).IsEqualTo(0);
    }

    [Test]
    public async Task ASyntaxErrorInTheFilterIsReturnedAsAFilterErrorAndNoExporterIsCreated()
    {
        using Rig rig = new();
        int created = 0;

        bool started = SessionExport.TryStart(
            rig.Session,
            "export",
            _ =>
            {
                created++;
                return new CountingListener();
            },
            0,
            _FrameCount,
            "udp.length >=",
            out SessionExportJob? export,
            out SessionFailure? failure,
            out FilterError? filterError);

        await Assert.That(started).IsFalse();
        await Assert.That(export).IsNull();
        await Assert.That(failure).IsNull();
        await Assert.That(filterError).IsNotNull();
        await Assert.That(created).IsEqualTo(0);
    }

    #endregion

    #region The exporter finishes once

    [Test]
    public async Task ACompletedExportFinishesTheExporterExactlyOnce()
    {
        using Rig rig = new();
        CountingListener listener = new();

        SessionExportJob export = _StartAndJoin(rig.Session, _ => listener, null);

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(listener.Packets).IsEqualTo(_FrameCount);
        await Assert.That(listener.Finished).IsEqualTo(1);
    }

    [Test]
    public async Task AnExporterThatStopsItselfEndsTheReadAndIsFinishedOnce()
    {
        using Rig rig = new();
        CountingListener listener = new(stopAfter: 7);

        SessionExportJob export = _StartAndJoin(rig.Session, _ => listener, null);

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(listener.Packets).IsEqualTo(7);
        await Assert.That(export.ExportedCount).IsEqualTo(7);
        await Assert.That(listener.Finished).IsEqualTo(1);
    }

    [Test]
    public async Task AFrameThatCannotBeReReadEndsTheExportWithAFailureAndFinishesTheExporterOnce()
    {
        using Rig rig = new(source => new HoleRandomAccessSource(source, holeFrameIndex: 20));
        CountingListener listener = new();

        SessionExportJob export = _StartAndJoin(rig.Session, _ => listener, null);

        await Assert.That(export.Failure).IsNotNull();
        await Assert.That(export.Failure!.Value.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(export.Failure.Value.Message).Contains("packet 20");
        await Assert.That(listener.Packets).IsEqualTo(20);
        await Assert.That(listener.Finished).IsEqualTo(1);
    }

    /// <summary>A UDP frame to port 53 that carries a DNS query whose single label is 60 times <c>a</c>.</summary>
    private static byte[] _DnsQueryFrameWithLongLabel()
    {
        const int label = 60;
        byte[] query =
        [
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            label,
            .. new string('a', label).Select(static c => (byte)c),
            0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01,
        ];
        byte[] frame = TestHarness.GenerateUdpFrame(42 + query.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 53);
        query.CopyTo(frame, 42);
        return frame;
    }

    [Test]
    [Category("Slow")]
    public async Task AFilterThatCannotDecideEndsTheExportWithFilterFailedAndFinishesTheExporterOnce()
    {
        // The lookahead forces the backtracking engine, and 60 identical characters make the nested quantifier exponential, so the
        // regular expression runs into its one second timeout on the first packet. A filter that cannot decide must neither keep
        // nor drop the packet: the export ends with the reason instead, and the exporter is still finished. The test waits the
        // timeout once, which is why it is marked slow.
        using Rig rig = new(frames: [_DnsQueryFrameWithLongLabel(), _DnsQueryFrameWithLongLabel()]);
        CountingListener listener = new();

        SessionExportJob export = _StartAndJoin(rig.Session, _ => listener, "dns.qry.name matches \"(?=^(a+)+$)\"", to: 2);

        await Assert.That(export.Failure).IsNotNull();
        await Assert.That(export.Failure!.Value.Code).IsEqualTo(SessionErrorCode.FilterFailed);
        await Assert.That(export.Failure.Value.Message).Contains("timed out");
        await Assert.That(export.ExportedCount).IsEqualTo(0);
        await Assert.That(listener.Packets).IsEqualTo(0);
        await Assert.That(listener.Finished).IsEqualTo(1);
    }

    [Test]
    public async Task ACancelledExportFinishesTheExporterOnceWithTheCommittedPackets()
    {
        using Rig rig = new();
        using ManualResetEventSlim firstPacket = new(false);
        using ManualResetEventSlim release = new(false);
        CountingListener listener = new(onFirstPacket: () =>
        {
            firstPacket.Set();
            _ = release.Wait(_Timeout);
        });
        _ = SessionExport.TryStart(
            rig.Session, "export", _ => listener, 0, _FrameCount, null, out SessionExportJob? export, out _, out _);

        await Assert.That(firstPacket.Wait(_Timeout)).IsTrue();
        export!.Job.Cancel();
        release.Set();
        export.Job.Join();

        await Assert.That(listener.Finished).IsEqualTo(1);
        await Assert.That(export.Failure).IsNull();
        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Cancelled);
        await Assert.That(listener.Packets).IsEqualTo(1);
    }

    [Test]
    public async Task ARestartEndsTheJobAsStackReplacedAndFinishesTheExporterOnce()
    {
        using Rig rig = new();
        using ManualResetEventSlim firstPacket = new(false);
        using ManualResetEventSlim release = new(false);
        CountingListener listener = new(onFirstPacket: () =>
        {
            firstPacket.Set();
            _ = release.Wait(_Timeout);
        });
        _ = SessionExport.TryStart(
            rig.Session, "export", _ => listener, 0, _FrameCount, null, out SessionExportJob? export, out _, out _);
        await Assert.That(firstPacket.Wait(_Timeout)).IsTrue();

        // The restart cancels the stack-bound job and waits for it; the exporter is released only once the restart has begun, so the
        // job meets the cancellation after its first packet.
        Task<RestartReport> restart = RestartRig.OnDedicatedThread(() => rig.Session.RestartOrThrow(registry => TestHarness.CreateStack(registry)));
        WaitHelper.WaitUntil(() => export!.Job.EndReason != JobEndReason.None);
        release.Set();
        _ = await restart.WaitAsync(_Timeout);
        export!.Job.Join();

        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Cancelled);
        await Assert.That(export.Job.EndReason).IsEqualTo(JobEndReason.StackReplaced);
        await Assert.That(export.Failure).IsNull();
        await Assert.That(listener.Finished).IsEqualTo(1);
        await Assert.That(listener.Packets).IsEqualTo(1);
    }

    #endregion

    #region Ingest

    [Test]
    public async Task IngestKeepsRunningWhileAnExportJobIsBusyAndNoIngestParseCarriesTheExportObserver()
    {
        // The source delivers 30 frames, then waits for the gate; the export of the first 30 blocks inside its first packet.
        using ManualResetEventSlim sourceGate = new(false);
        using ManualResetEventSlim exporterEntered = new(false);
        using ManualResetEventSlim exporterRelease = new(false);
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource inner = new(_VariedFrames());
        GatedIngestSource source = new(inner, gateAt: 30, sourceGate);
        using Session session = new(stack);
        _ = session.TryAddFrameSource(source, out _, out _);
        _ = session.TryStart(out _);
        WaitHelper.WaitUntil(() => session.PacketCount == 30);

        RecordingFieldObserver observer = new();
        ObservingListener listener = new(stack, observer, () =>
        {
            exporterEntered.Set();
            _ = exporterRelease.Wait(_Timeout);
        });
        bool started = SessionExport.TryStart(
            session, "export", _ => listener, 0, 30, null, out SessionExportJob? export, out SessionFailure? failure, out _);
        await Assert.That(started).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(exporterEntered.Wait(_Timeout)).IsTrue();

        // The job is busy inside the exporter; release the rest of the capture and wait until ingest published all of it.
        sourceGate.Set();
        WaitHelper.WaitUntil(() => session.PacketCount == _FrameCount);
        int ingestedWhileJobRan = session.PacketCount;
        bool jobStillRunning = export!.Job.Status == JobStatus.Running;
        exporterRelease.Set();
        export.Job.Join();

        await Assert.That(ingestedWhileJobRan).IsEqualTo(_FrameCount);
        await Assert.That(jobStillRunning).IsTrue();
        await Assert.That(export.Job.Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(export.ExportedCount).IsEqualTo(30);

        // Only the 30 export parses began a packet on the observer; the 60 ingest parses never saw it.
        await Assert.That(observer.BeganPacket).IsEqualTo(30);
    }

    /// <summary>Delivers frames from <paramref name="inner"/> until <paramref name="gateAt"/>, then waits for the gate.</summary>
    private sealed class GatedIngestSource(TestFrameSource inner, int gateAt, ManualResetEventSlim gate) : IRandomAccessFrameSource
    {
        private int _Delivered;

        public string UiName => inner.UiName;

        public string? Description => inner.Description;

        public int? EstimatedFrameCount => inner.EstimatedFrameCount;

        public bool IsRunning => inner.IsRunning;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => inner.Start(sourceId, registry);

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            if (_Delivered == gateAt)
            {
                gate.Wait(cancellationToken);
            }

            _Delivered++;
            return inner.NextFrame(cancellationToken);
        }

        public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) => inner.FrameById(id, cancellationToken);

        public void Dispose()
        {
            // The test owns and disposes the inner source.
        }
    }

    /// <summary>A listener whose parse demand carries a field observer; blocks inside its first packet when asked.</summary>
    private sealed class ObservingListener(Stack stack, RecordingFieldObserver observer, Action onFirstPacket) : IPacketListener, IParseDemandSource
    {
        private int _Packets;

        public string UiName => "observing";

        public string? Description => null;

        public ParseDemand ParseDemand
        {
            get
            {
                FieldId ttl = stack.GetFieldId("ip.ttl")!.Value; // Non-null: the standard stack defines it.
                return ParseDemand.Fields(new ParseObserver { Observer = observer, Watch = FieldWatch.Only(stack, ttl) });
            }
        }

        public bool OnPacket(Packet packet)
        {
            if (Interlocked.Increment(ref _Packets) == 1)
            {
                onFirstPacket();
            }

            return true;
        }

        public void OnFinish()
        {
        }
    }

    #endregion
}
