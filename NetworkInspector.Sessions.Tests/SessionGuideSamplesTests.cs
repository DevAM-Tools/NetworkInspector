// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Compiles and runs the samples of <c>SESSION_GUIDE.md</c> (sections 2, 8.1, 8.2, 8.4, 8.5 and 9), so the guide
/// cannot drift from the API. A test source replaces the capture file of the quick start. Each sample asserts one observable
/// from the guide text.
/// </summary>
internal sealed class SessionGuideSamplesTests
{
    #region Section 2 — quick start

    [Test]
    public async Task QuickStartCountsEveryPacket()
    {
        const int frameCount = 12;
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        CountingListener listener = new();

        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        session.TryAddFrameSource(source, out _, out _);
        bool added = session.TryAddListener(listener, out ListenerInfo? info, out SessionFailure? failure);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.Seen >= frameCount);
        session.Shutdown();

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(failure).IsNull();
        await Assert.That(listener.Seen).IsEqualTo(frameCount);
    }

    #endregion

    #region Section 8.1 — packet list with generation-aware columns

    [Test]
    public async Task ColumnListenerReadsCellsByNameAndRebuildsItsRowsAndIdsAfterARestart()
    {
        const int frameCount = 300;
        using Stack stack = TestHarness.CreateStack();
        ColumnListener listener = new();
        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(listener, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowCount >= frameCount);
        int before = listener.RowCount;
        int generationBefore = listener.ViewGeneration;
        (ulong? Ttl, ulong? DstPort) firstCells = listener.FirstCells;

        Stack newStack = TestHarness.CreateStack(session.Stack.FrameInterfaceRegistry);
        session.RestartOrThrow(_ => newStack);
        WaitHelper.WaitUntil(() => listener.StackChanges >= 1 && listener.RowCount >= frameCount && listener.ViewGeneration == 1);
        int after = listener.RowCount;
        session.Shutdown();

        await Assert.That(before).IsEqualTo(frameCount);
        await Assert.That(generationBefore).IsEqualTo(0);
        await Assert.That(firstCells.Ttl).IsEqualTo(64UL);
        await Assert.That(firstCells.DstPort).IsEqualTo(53UL);
        await Assert.That(after).IsEqualTo(frameCount);
        await Assert.That(listener.FirstRowId).IsEqualTo(0);
        await Assert.That(ReferenceEquals(listener.ViewStack, newStack)).IsTrue();
        await Assert.That(listener.FirstCells).IsEqualTo(firstCells);
    }

    #endregion

    #region Section 8.2 — filtered list registration

    [Test]
    public async Task FilteredListRegistrationShapesCompileAndRegister()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        PacketFilter myFilter = SessionFixture.CompileOrThrow(stack, "udp");

        bool byExpression = session.TryAddListener(
            new CountingListener(), "tcp.port == 443", out ListenerInfo? info, out FilterError? filterFailure, out SessionFailure? failure);
        bool byFilter = session.TryAddListener(new CountingListener(), myFilter, out info, out failure);
        bool unfiltered = session.TryAddListener(new CountingListener(), out info, out failure);

        await Assert.That(byExpression).IsTrue();
        await Assert.That(filterFailure).IsNull();
        await Assert.That(byFilter).IsTrue();
        await Assert.That(unfiltered).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(session.GetListeners().Count).IsEqualTo(3);
    }

    #endregion

    #region Section 8.4 — one packet with field observers

    [Test]
    public async Task OnePacketWithFieldObserverSeesTheWatchedFieldAndAStaleViewIsRefused()
    {
        using Stack sessionStack = TestHarness.CreateStack();
        using Session session = new(sessionStack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        RecordingFieldObserver myObserver = new();
        PacketId id = 1;

        // The sample of the guide.
        StackView view = session.CurrentStack;
        FieldId ipTtlFieldId = view.Stack.GetFieldId("ip.ttl")!.Value;
        ParseObserver[] observers =
        [
            new ParseObserver { Observer = myObserver, Watch = FieldWatch.Only(view.Stack, ipTtlFieldId) },
        ];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
        bool parsed = session.TryGetPacket(id, in options, view, recycle: null, out Packet? packet, out SessionFailure? failure);

        // After a restart the same view is refused by code.
        session.RestartOrThrow(registry => TestHarness.CreateStack(registry));
        bool stale = session.TryGetPacket(id, in options, view, recycle: null, out _, out SessionFailure? staleFailure);

        await Assert.That(parsed).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(packet).IsNotNull();
        await Assert.That(myObserver.BeganPacket).IsEqualTo(1);
        await Assert.That(myObserver.Fields.All(field => field == ipTtlFieldId)).IsTrue();
        await Assert.That(myObserver.Fields.Count).IsEqualTo(1);
        await Assert.That(stale).IsFalse();
        await Assert.That(staleFailure!.Value.Code).IsEqualTo(SessionErrorCode.OptionsStackMismatch);
        session.Shutdown();
    }

    #endregion

    #region Section 8.5 — a job that declares its binding

    [Test]
    public async Task ABoundExportJobEndsWithStackReplacedWhenTheSessionRestarts()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        using ManualResetEventSlim running = new(false);
        using ManualResetEventSlim gate = new(false);
        StackView startedIn = default;

        // The sample of the guide: the work receives the view it started in.
        session.TryAddJob("export rows", "rows to out.csv", JobStackBinding.StackBound,
            context =>
            {
                startedIn = context.Stack;
                running.Set();
                _ = gate.Wait(TimeSpan.FromSeconds(30), context.CancellationToken);
            },
            out JobInfo? job, out _);
        await Assert.That(running.Wait(TimeSpan.FromSeconds(30))).IsTrue();

        session.RestartOrThrow(registry => TestHarness.CreateStack(registry));
        job!.Join();

        await Assert.That(ReferenceEquals(startedIn.Stack, stack)).IsTrue();
        await Assert.That(startedIn.Generation).IsEqualTo(0);
        await Assert.That((job.Status, job.EndReason)).IsEqualTo((JobStatus.Cancelled, JobEndReason.StackReplaced));
        session.Shutdown();
    }

    #endregion

    #region Section 9 — restart with options

    [Test]
    public async Task TheRestartCallOfTheGuideReturnsTheReportAndAFailureCarriesTheReason()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(7);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        // The sample of the guide.
        RestartOptions options = new()
        {
            JobPolicy = RestartJobPolicy.WaitThenCancel,
            JobWaitTimeout = TimeSpan.FromSeconds(10),
        };
        bool restarted = session.TryRestart(
            registry =>
            {
                StackBuilder builder = new(new SettingsManager(), registry);
                builder.RegisterStandardProtocols();
                return builder.Build();
            },
            options,
            out RestartReport? report,
            out SessionFailure? failure);

        // The refusal side of the sample: a bound that is out of range comes back as a result.
        bool refused = session.TryRestart(
            registry => TestHarness.CreateStack(registry),
            new RestartOptions { JobWaitTimeout = TimeSpan.Zero },
            out _,
            out SessionFailure? refusal);
        session.Shutdown();

        await Assert.That(restarted).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(report!.Generation).IsEqualTo(1);
        await Assert.That(report.JobsCancelled.Count).IsEqualTo(0);
        await Assert.That(refused).IsFalse();
        await Assert.That(refusal!.Value.Code).IsEqualTo(SessionErrorCode.RestartOptionOutOfRange);
    }

    #endregion

    #region Sample types

    /// <summary>Quick-start listener of the guide.</summary>
    private sealed class CountingListener : ISessionListener
    {
        private int _Seen;

        public string UiName => "counter";

        internal int Seen => Volatile.Read(ref _Seen);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
            => Interlocked.Add(ref _Seen, toIndexExclusive - fromIndex);
    }

    /// <summary>
    /// The generation-aware column listener of guide section 8.1, with counters a test can read from another thread.
    /// </summary>
    private sealed class ColumnListener : ISessionListener
    {
        private readonly string[] _ColumnNames = ["ip.ttl", "udp.dstport"];   // names survive a restart, ids do not
        private readonly PacketRef[] _Buffer = new PacketRef[256];            // recycled by every pull
        private StackView _View;                                              // the generation the ids below belong to
        private FieldId?[] _ColumnIds = [];
        private int _RowCount;
        private int _StackChanges;
        private int _FirstRowId = -1;
        private volatile int _ViewGeneration = -1;
        private volatile Stack? _ViewStack;
        private long _FirstTtl = -1;
        private long _FirstDstPort = -1;

        // Touched only on this listener's thread; a UI copies rows out by posting them.
        private List<(int Id, ulong?[] Cells)> Rows { get; } = [];

        public string UiName => "packet list";

        internal int RowCount => Volatile.Read(ref _RowCount);

        internal int StackChanges => Volatile.Read(ref _StackChanges);

        internal int FirstRowId => Volatile.Read(ref _FirstRowId);

        internal int ViewGeneration => _ViewGeneration;

        internal Stack? ViewStack => _ViewStack;

        internal (ulong? Ttl, ulong? DstPort) FirstCells
        {
            get
            {
                long ttl = Volatile.Read(ref _FirstTtl);
                long dstPort = Volatile.Read(ref _FirstDstPort);
                return (ttl < 0 ? null : (ulong)ttl, dstPort < 0 ? null : (ulong)dstPort);
            }
        }

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            _EnsureColumns(session);                                          // re-resolve when the generation moved
            for (int start = fromIndex; start < toIndexExclusive; start += _Buffer.Length)
            {
                int filled = session.ReadPackets(start, _Buffer, out PacketIdLayout _);
                for (int i = 0; i < filled && _Buffer[i].Id.Value < toIndexExclusive; i++)
                {
                    Packet? packet = _Buffer[i].Packet;                       // null: a restart overtook this call
                    if (packet is not null)
                    {
                        Rows.Add((packet.Id.Value, _ReadCells(packet)));
                    }
                }
            }

            if (Rows.Count > 0)
            {
                Volatile.Write(ref _FirstRowId, Rows[0].Id);
                ulong?[] cells = Rows[0].Cells;
                Volatile.Write(ref _FirstTtl, cells[0] is ulong ttl ? (long)ttl : -1);
                Volatile.Write(ref _FirstDstPort, cells[1] is ulong dstPort ? (long)dstPort : -1);
            }

            Volatile.Write(ref _RowCount, Rows.Count);
        }

        public void OnStackChanged(ISessionReader session)
        {
            Rows.Clear();                                                     // the next OnNewPackets starts at 0
            Volatile.Write(ref _RowCount, 0);
            _EnsureColumns(session);
            Interlocked.Increment(ref _StackChanges);
        }

        private void _EnsureColumns(ISessionReader session)
        {
            StackView current = session.CurrentStack;                         // stack and generation from one load
            if (_ColumnIds.Length != 0 && _View.Generation == current.Generation)
            {
                return;
            }

            _View = current;
            _ColumnIds = [.. _ColumnNames.Select(name => current.Stack.GetFieldId(name))];   // null: the new stack lacks it
            _ViewStack = current.Stack;
            _ViewGeneration = current.Generation;
        }

        private ulong?[] _ReadCells(Packet packet)
        {
            ulong?[] cells = new ulong?[_ColumnIds.Length];
            for (int column = 0; column < cells.Length; column++)
            {
                if (_ColumnIds[column] is FieldId id
                    && packet.TryGetFieldValue(id, out FieldValue value, materialize: true)
                    && value.Data.TryGetAsU64(out ulong cell))
                {
                    cells[column] = cell;
                }
            }

            return cells;
        }
    }

    #endregion
}
