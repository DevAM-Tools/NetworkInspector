// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// The generation of a session: the number every consumer compares to notice that the stack was replaced, the immutable
/// <see cref="StackView"/> pair, the three pull states of a restart, and the promise that one read call parses on one
/// generation. Every interleaving is forced with gates; there are no sleeps. The only waits are bounded by
/// <see cref="_Timeout"/> so a broken implementation fails instead of hanging.
/// </summary>
internal sealed class SessionGenerationTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The generations the parking listener sees: the restart it parked in, then the two merged ones.</summary>
    private static readonly int[] _ExpectedOneThree = [1, 3];

    /// <summary>Starts <paramref name="work"/> on its own thread so a gate inside it cannot starve the thread pool.</summary>
    private static Task<T> _OnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Listener that parks its first <see cref="OnStackChanged"/> on a gate and records the generation of every stack
    /// change together with the stack of every packet window it reads. Thread-safety: the lists are guarded by a lock; the
    /// gates are events.
    /// </summary>
    private sealed class ParkingListener : ISessionListener, IDisposable
    {
        private readonly Lock _Gate = new();
        private readonly List<int> _StackChangedGenerations = [];
        private readonly List<bool> _WindowsOnCurrentStack = [];
        private int _Parked;

        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        public string UiName => "ParkingListener";

        internal int[] StackChangedGenerations
        {
            get
            {
                lock (_Gate)
                {
                    return [.. _StackChangedGenerations];
                }
            }
        }

        internal bool[] WindowsOnCurrentStack
        {
            get
            {
                lock (_Gate)
                {
                    return [.. _WindowsOnCurrentStack];
                }
            }
        }

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            // Read the announced window and check that every packet was parsed on the stack that is current now.
            Packet?[] buffer = new Packet?[toIndexExclusive - fromIndex];
            int read = session.ReadPackets(fromIndex, buffer);
            Stack current = session.CurrentStack.Stack;
            bool allCurrent = read == buffer.Length;
            for (int i = 0; i < read; i++)
            {
                allCurrent &= buffer[i] is not null && ReferenceEquals(buffer[i]!.Stack, current); // Non-null: checked first.
            }

            lock (_Gate)
            {
                _WindowsOnCurrentStack.Add(allCurrent);
            }
        }

        public void OnStackChanged(ISessionReader session)
        {
            lock (_Gate)
            {
                _StackChangedGenerations.Add(session.Generation);
            }

            if (Interlocked.Exchange(ref _Parked, 1) == 0)
            {
                Entered.Set();
                _ = Release.Wait(_Timeout);
            }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    #endregion

    #region Number and view

    [Test]
    public async Task GenerationStartsAtZeroAndRisesByOnePerRestart()
    {
        using RestartRig rig = new(frameCount: 20);
        StackView initial = rig.Session.CurrentStack;
        int generationBefore = rig.Session.Generation;

        Stack second = rig.RestartOntoStandard();
        StackView afterOne = rig.Session.CurrentStack;
        int generationAfterOne = rig.Session.Generation;
        _ = rig.RestartOntoStandard();
        Stack fourth = rig.RestartOntoStandard();

        await Assert.That(generationBefore).IsEqualTo(0);
        await Assert.That(initial.Generation).IsEqualTo(0);
        await Assert.That(ReferenceEquals(initial.Stack, rig.InitialStack)).IsTrue();
        await Assert.That(generationAfterOne).IsEqualTo(1);
        await Assert.That(afterOne.Generation).IsEqualTo(1);
        await Assert.That(ReferenceEquals(afterOne.Stack, second)).IsTrue();
        await Assert.That(rig.Session.Generation).IsEqualTo(3);
        await Assert.That(rig.Session.CurrentStack.Generation).IsEqualTo(3);
        await Assert.That(ReferenceEquals(rig.Session.CurrentStack.Stack, fourth)).IsTrue();
    }

    [Test]
    public async Task ViewTakenBeforeARestartKeepsNamingTheOldStackAndStillAnswersLookups()
    {
        using RestartRig rig = new(frameCount: 10);

        // The first restart makes the session own the stack, the second retires it: with no reader inside, it is disposed at once.
        _ = rig.RestartOntoStandard();
        StackView before = rig.Session.CurrentStack;
        _ = rig.RestartOntoStandard();
        StackView after = rig.Session.CurrentStack;

        await Assert.That(before.Generation).IsEqualTo(1);
        await Assert.That(after.Generation).IsEqualTo(2);
        await Assert.That(ReferenceEquals(before.Stack, after.Stack)).IsFalse();
        await Assert.That(before.IsCurrent(rig.Session)).IsFalse();
        await Assert.That(after.IsCurrent(rig.Session)).IsTrue();
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
        await Assert.That(before.Stack.GetFieldId("udp.dstport")).IsNotNull();
    }

    [Test]
    public async Task ADefaultViewAndAViewOfAnotherSessionAreNeverCurrent()
    {
        using RestartRig rig = new(frameCount: 4);
        using RestartRig other = new(frameCount: 4);

        // Both sessions are at generation 0, so only the stack tells the views apart.
        StackView foreign = other.Session.CurrentStack;

        await Assert.That(foreign.Generation).IsEqualTo(rig.Session.Generation);
        await Assert.That(foreign.IsCurrent(rig.Session)).IsFalse();
        await Assert.That(foreign.IsCurrent(other.Session)).IsTrue();
        await Assert.That(default(StackView).IsCurrent(rig.Session)).IsFalse();
    }

    [Test]
    public async Task IsCurrentRejectsANullSession()
    {
        using Stack stack = TestHarness.CreateStack();
        StackView view = new(stack, 0);

        await Assert.That(() => view.IsCurrent(null!)).Throws<ArgumentNullException>();
    }

    #endregion

    #region Pull states and order

    [Test]
    public async Task APollerThatReadsOnlyGenerationAndQueriesFlagSeesStableInProgressAndDone()
    {
        using RestartRig rig = new(frameCount: 8);
        using ManualResetEventSlim factoryEntered = new(false);
        using ManualResetEventSlim factoryRelease = new(false);
        using ManualResetEventSlim parseEntered = new(false);
        using ManualResetEventSlim parseRelease = new(false);

        // The factory parks first; the re-parse then parks inside the new stack's protocol, i.e. after the swap.
        Task<bool> restart = _OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                factoryEntered.Set();
                _ = factoryRelease.Wait(_Timeout);
                Stack stack = ProbeStack.Create(registry, out ProbeProtocol probe);
                probe.SetParseHook(() =>
                {
                    parseEntered.Set();
                    _ = parseRelease.Wait(_Timeout);
                });
                return stack;
            },
            out _,
            out _));
        await Assert.That(factoryEntered.Wait(_Timeout)).IsTrue();
        (int Generation, bool Enabled) stable = (rig.Session.Generation, rig.Session.QueriesEnabled);

        factoryRelease.Set();
        await Assert.That(parseEntered.Wait(_Timeout)).IsTrue();
        (int Generation, bool Enabled) inProgress = (rig.Session.Generation, rig.Session.QueriesEnabled);

        parseRelease.Set();
        bool restarted = await restart.WaitAsync(_Timeout);
        (int Generation, bool Enabled) done = (rig.Session.Generation, rig.Session.QueriesEnabled);

        await Assert.That(restarted).IsTrue();
        await Assert.That(stable).IsEqualTo((0, true));
        await Assert.That(inProgress).IsEqualTo((1, false));
        await Assert.That(done).IsEqualTo((1, true));
    }

    [Test]
    public async Task TwoRestartsThatASlowListenerObservesInOneWakeUpRaiseTheGenerationByTwo()
    {
        using RestartRig rig = new(frameCount: 20);
        using ParkingListener listener = new();
        _ = rig.Session.TryAddListener(listener, out _, out _);
        WaitHelper.WaitUntil(() => listener.WindowsOnCurrentStack.Length == 1);

        // The first restart wakes the listener, which parks inside OnStackChanged with generation 1. The next two restarts
        // run while it is parked, so their wake-ups merge into one flag word.
        _ = rig.RestartOntoStandard();
        await Assert.That(listener.Entered.Wait(_Timeout)).IsTrue();
        _ = rig.RestartOntoStandard();
        _ = rig.RestartOntoStandard();
        listener.Release.Set();
        WaitHelper.WaitUntil(() => listener.StackChangedGenerations.Length == 2 && listener.WindowsOnCurrentStack.Length == 3);

        await Assert.That(listener.StackChangedGenerations).IsEquivalentTo(_ExpectedOneThree);
        await Assert.That(listener.WindowsOnCurrentStack.Skip(1).All(static onCurrent => onCurrent)).IsTrue();
    }

    /// <summary>
    /// Records, at the start of <see cref="OnStackChanged"/>, the generation, the queries flag and the packet count, and
    /// reads with the listener's own filter there; at <see cref="OnNewPackets"/> it checks the stack of every packet.
    /// </summary>
    private sealed class OrderListener : ISessionListener
    {
        private readonly Lock _Gate = new();

        internal ListenerId Id { get; set; }

        internal (int Generation, bool Enabled, int Count)? AtStackChanged { get; private set; }

        internal bool? MatchingReadSucceededInStackChanged { get; private set; }

        internal int MatchingCountInStackChanged { get; private set; }

        internal bool? AllPacketsOnNewStack { get; private set; }

        public string UiName => "OrderListener";

        public void OnStackChanged(ISessionReader session)
        {
            // The first call (generation 0 never raises StackChanged) is the one of the restart. The matching read must use
            // the filter the restart re-bound before it enabled reads, otherwise it fails with the old stack's ids.
            PacketRef[] destination = new PacketRef[session.PacketCount];
            bool read = session.TryReadPackets(
                Id, 0, destination, PacketReadMode.Matching, out int count, out _, out _, out _);
            lock (_Gate)
            {
                AtStackChanged = (session.Generation, session.QueriesEnabled, session.PacketCount);
                MatchingReadSucceededInStackChanged = read;
                MatchingCountInStackChanged = count;
            }
        }

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            Packet?[] buffer = new Packet?[toIndexExclusive - fromIndex];
            int read = session.ReadPackets(fromIndex, buffer);
            Stack current = session.CurrentStack.Stack;
            bool all = read == buffer.Length;
            for (int i = 0; i < read; i++)
            {
                all &= buffer[i] is not null && ReferenceEquals(buffer[i]!.Stack, current); // Non-null: checked first.
            }

            lock (_Gate)
            {
                // Only the window after the restart counts; the initial backfill ran before OnStackChanged.
                if (AtStackChanged is not null)
                {
                    AllPacketsOnNewStack = all;
                }
            }
        }
    }

    [Test]
    public async Task ListenerSeesTheNewGenerationWithReadsEnabledAndItsFilterReboundInOnStackChanged()
    {
        using RestartRig rig = new(frameCount: 12);
        OrderListener listener = new();
        PacketFilter filter = SessionFixture.CompileOrThrow(rig.InitialStack, "udp.dstport == 53");
        _ = rig.Session.TryAddListener(listener, filter, out ListenerInfo? info, out _);
        listener.Id = info!.Id; // Non-null: the add succeeded on an idle-free finished session.

        _ = rig.RestartOntoStandard();
        WaitHelper.WaitUntil(() => listener.AllPacketsOnNewStack is not null);

        await Assert.That(listener.AtStackChanged).IsEqualTo((1, true, 12));
        await Assert.That(listener.MatchingReadSucceededInStackChanged).IsTrue();
        await Assert.That(listener.MatchingCountInStackChanged).IsEqualTo(12);
        await Assert.That(listener.AllPacketsOnNewStack).IsTrue();
    }

    #endregion

    #region Reads

    [Test]
    public async Task AChunkedReadDuringTwoRestartsNeverReturnsPacketsOfTwoStacks()
    {
        using RestartRig rig = new(frameCount: 10_000);
        List<Stack> stacks = [rig.InitialStack];
        using CancellationTokenSource stop = new();
        int calls = 0;
        int violations = 0;

        // The reader loops over the whole capture in 256-slot calls. For every call all non-null packets must share one stack
        // that belongs to this run, and a null slot inside the filled range is only allowed while a restart overtakes it.
        Task reader = _OnDedicatedThread(() =>
        {
            Packet?[] buffer = new Packet?[256];
            while (!stop.IsCancellationRequested)
            {
                for (int start = 0; start < rig.FrameCount && !stop.IsCancellationRequested; start += buffer.Length)
                {
                    int generationBefore = rig.Session.Generation;
                    int filled = rig.Session.ReadPackets(start, buffer);
                    bool enabledAfter = rig.Session.QueriesEnabled;
                    int generationAfter = rig.Session.Generation;
                    _ = Interlocked.Increment(ref calls);

                    Stack? common = null;
                    bool hasNull = false;
                    for (int i = 0; i < filled; i++)
                    {
                        if (buffer[i] is not Packet packet)
                        {
                            hasNull = true;
                            continue;
                        }

                        common ??= packet.Stack;
                        if (!ReferenceEquals(common, packet.Stack))
                        {
                            _ = Interlocked.Increment(ref violations);
                        }
                    }

                    // Queries are re-enabled only after the generation moved, so "enabled again and same generation" proves no
                    // restart touched this call; a null slot then has no explanation.
                    if (hasNull && enabledAfter && generationAfter == generationBefore)
                    {
                        _ = Interlocked.Increment(ref violations);
                    }

                    Stack[] known;
                    lock (stacks)
                    {
                        known = [.. stacks];
                    }

                    if (common is not null && !known.Any(stack => ReferenceEquals(stack, common)))
                    {
                        _ = Interlocked.Increment(ref violations);
                    }
                }
            }

            return 0;
        });

        for (int i = 0; i < 2; i++)
        {
            _ = rig.Session.RestartOrThrow(registry =>
            {
                Stack created = TestHarness.CreateStack(registry);
                lock (stacks)
                {
                    stacks.Add(created);
                }

                return created;
            });
        }

        // One more full pass on the final stack before the reader stops.
        int callsAtEnd = Volatile.Read(ref calls);
        WaitHelper.WaitUntil(() => Volatile.Read(ref calls) > callsAtEnd + (rig.FrameCount / 256) + 1, timeoutMs: 30_000);
        await stop.CancelAsync();
        await reader.WaitAsync(_Timeout);

        await Assert.That(Volatile.Read(ref violations)).IsEqualTo(0);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
        await Assert.That(rig.Session.Generation).IsEqualTo(2);
    }

    [Test]
    public async Task OneReadCallTakesExactlyOneLeaseForEveryPathAndGivesItBack()
    {
        using RestartRig rig = new(frameCount: 300);
        (Stack _, ProbeProtocol probe) = rig.RestartOntoProbe();
        RetirableStack holder = rig.Session.StackHolder;
        List<int> leaseCounts = [];
        probe.SetParseHook(() =>
        {
            lock (leaseCounts)
            {
                leaseCounts.Add(holder.LeaseCount);
            }
        });
        TestSessionListener listener = new();
        _ = rig.Session.TryAddListener(listener, "probe.eager == 64", out ListenerInfo? info, out _, out _);

        // A 256-slot range read: four chunks, one lease.
        PacketRef[] destination = new PacketRef[256];
        int rangeCount = rig.Session.ReadPackets(0, destination, out _);
        int[] rangeLeases = _Drain(leaseCounts);
        int leasesAfterRange = holder.LeaseCount;

        // A matching read with a filter that forces the pre-check and the build parse: still one lease for the call.
        bool matched = rig.Session.TryReadPackets(
            info!.Id, 0, destination, PacketReadMode.Matching, out int matchingCount, out _, out _, out _); // Non-null: the add succeeded.
        int[] matchingLeases = _Drain(leaseCounts);
        int leasesAfterMatching = holder.LeaseCount;

        await Assert.That(rangeCount).IsEqualTo(256);
        await Assert.That(rangeLeases.Length).IsGreaterThanOrEqualTo(256);
        await Assert.That(rangeLeases.All(static count => count == 1)).IsTrue();
        await Assert.That(leasesAfterRange).IsEqualTo(0);
        await Assert.That(matched).IsTrue();
        await Assert.That(matchingCount).IsEqualTo(256);
        await Assert.That(matchingLeases.Length).IsGreaterThanOrEqualTo(256);
        await Assert.That(matchingLeases.All(static count => count == 1)).IsTrue();
        await Assert.That(leasesAfterMatching).IsEqualTo(0);
    }

    private static int[] _Drain(List<int> list)
    {
        lock (list)
        {
            int[] copy = [.. list];
            list.Clear();
            return copy;
        }
    }

    [Test]
    public async Task ReadingGenerationAndCurrentStackAllocatesNothing()
    {
        using RestartRig rig = new(frameCount: 4);
        _ = rig.RestartOntoStandard();
        StackView view = rig.Session.CurrentStack;
        bool current = false;
        int generation = 0;

        // Warm up (JIT, tiering), then measure on this thread only.
        for (int i = 0; i < 2000; i++)
        {
            generation += rig.Session.Generation;
            current |= rig.Session.CurrentStack.IsCurrent(rig.Session) & view.IsCurrent(rig.Session);
        }

        // Up to five measured rounds of 1000 reads; the smallest allocation counts. A one-off allocation inflates only the
        // round it lands in, while a real per-read allocation shows in every round. A round at zero ends the measuring.
        long allocated = long.MaxValue;
        for (int round = 0; round < 5 && allocated > 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                generation += rig.Session.Generation;
                current |= rig.Session.CurrentStack.IsCurrent(rig.Session) & view.IsCurrent(rig.Session);
            }

            allocated = long.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(current).IsTrue();
        await Assert.That(generation).IsGreaterThan(0);
    }

    #endregion
}
