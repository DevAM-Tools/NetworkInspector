// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Life cycle of the stacks a <see cref="Session.Restart"/> retires: a retired stack must stay usable for every read that
/// already runs on it and must be disposed as soon as the last of those reads ends (immediately when there is none).
/// Every interleaving is forced with gates; there are no sleeps. The only waits are bounded by <see cref="_Timeout"/> so a
/// broken implementation fails instead of hanging.
/// </summary>
internal sealed class SessionStackRetirementTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A finished capture on a session that starts with a stack the session does not own. The first
    /// <see cref="RestartOnto"/> makes the session own a probe stack, every later one retires an owned stack.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly Stack _InitialStack;
        private readonly TestFrameSource _Source;

        internal Rig(int frameCount, SessionOptions? options = null, Func<TestFrameSource, IFrameSource>? wrapSource = null)
        {
            FrameCount = frameCount;
            _InitialStack = TestHarness.CreateStack();
            _Source = TestFrameSource.WithUdpFrames(frameCount);
            Session = new Session(_InitialStack, options);
            IFrameSource registered = _Source;
            if (wrapSource is not null)
            {
                registered = wrapSource(_Source);
            }

            _ = Session.TryAddFrameSource(registered, out _, out _);
            _ = Session.TryStart(out _);
            Session.WaitForCompletion();
            WaitHelper.WaitUntil(() => Session.PacketCount == frameCount && Session.Phase == SessionPhase.Stopped);
        }

        internal Session Session { get; }

        internal int FrameCount { get; }

        /// <summary>Restarts onto a fresh probe stack and returns that stack's probe.</summary>
        internal ProbeProtocol RestartOnto(bool throwOnShutdown = false)
        {
            ProbeProtocol? created = null;
            Session.Restart(registry => ProbeStack.Create(registry, out created, throwOnShutdown));
            return created!; // Non-null: the factory ran, otherwise Restart threw.
        }

        public void Dispose()
        {
            // Errors of the shutdown are the subject of dedicated tests; a rig only has to clean up.
            Session.Dispose();
            _InitialStack.Dispose();
            _Source.Dispose();
        }
    }

    /// <summary>Starts <paramref name="work"/> on its own thread so a gate inside it cannot starve the thread pool.</summary>
    private static Task<T> _OnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>A parse hook that reports its arrival and then waits for the release (bounded).</summary>
    private static Action _ParkOn(ManualResetEventSlim entered, ManualResetEventSlim release) =>
        () =>
        {
            entered.Set();
            _ = release.Wait(_Timeout);
        };

    #endregion

    #region In-flight reads

    [Test]
    public async Task RetiredStackIsDisposedOnlyAfterTheInFlightReparseEnds()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol retiring = rig.RestartOnto();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        retiring.SetParseHook(_ParkOn(entered, release));

        // The reader passed every check and is parked inside the protocol's parse, on the stack that is about to retire.
        Task<bool> reader = _OnDedicatedThread(() => rig.Session.TryGetPacket(new PacketId(0), out _, out _));
        await Assert.That(entered.Wait(_Timeout)).IsTrue();

        _ = rig.RestartOnto();
        int shutdownsWhileReading = retiring.ShutdownCount;
        int retiredWhileReading = rig.Session.RetiredStackCount;
        release.Set();
        bool readSucceeded = await reader.WaitAsync(_Timeout);

        await Assert.That(shutdownsWhileReading).IsEqualTo(0);
        await Assert.That(retiredWhileReading).IsEqualTo(1);
        await Assert.That(readSucceeded).IsTrue();
        await Assert.That(retiring.ShutdownCount).IsEqualTo(1);
        await Assert.That(retiring.ShutdownDuringParseCount).IsEqualTo(0);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
    }

    [Test]
    public async Task RestartWithoutReadersDisposesTheRetiredStackAtOnce()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol retiring = rig.RestartOnto();

        ProbeProtocol current = rig.RestartOnto();

        await Assert.That(retiring.ShutdownCount).IsEqualTo(1);
        await Assert.That(current.ShutdownCount).IsEqualTo(0);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
    }

    [Test]
    public async Task RepeatedRestartsUnderReadLoadDisposeEveryRetiredStackOnceAndNeverDuringAParse()
    {
        using Rig rig = new(frameCount: 32);
        List<ProbeProtocol> probes = [rig.RestartOnto()];
        using CancellationTokenSource stop = new();
        int successfulReads = 0;

        // Two readers hammer single and range reads while the main thread swaps the stack again and again. A read that hits a
        // restart is refused (QueriesDisabled); one that got in must finish on a live stack.
        Task[] readers = new Task[2];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = _OnDedicatedThread(() =>
            {
                Packet?[] buffer = new Packet?[8];
                for (int i = seed; !stop.IsCancellationRequested; i++)
                {
                    if (rig.Session.TryGetPacket(new PacketId(i % rig.FrameCount), out _, out _))
                    {
                        _ = Interlocked.Increment(ref successfulReads);
                    }

                    if (rig.Session.ReadPackets(i % 16, buffer) > 0)
                    {
                        _ = Interlocked.Increment(ref successfulReads);
                    }
                }

                return 0;
            });
        }

        for (int i = 0; i < 25; i++)
        {
            probes.Add(rig.RestartOnto());
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers).WaitAsync(_Timeout);

        for (int i = 0; i < probes.Count - 1; i++)
        {
            await Assert.That(probes[i].ShutdownCount).IsEqualTo(1);
            await Assert.That(probes[i].ShutdownDuringParseCount).IsEqualTo(0);
        }

        await Assert.That(probes[^1].ShutdownCount).IsEqualTo(0);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
        await Assert.That(Volatile.Read(ref successfulReads)).IsGreaterThan(0);
    }

    #endregion

    #region Packets handed out before the restart

    [Test]
    public async Task PacketFromBeforeTheRestartKeepsEagerFieldsAndFailsLazyPartsWithAnErrorChild()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol retiring = rig.RestartOnto();
        bool gotPending = rig.Session.TryGetPacket(new PacketId(0), out Packet? pending, out _);
        bool gotMaterialized = rig.Session.TryGetPacket(new PacketId(1), out Packet? materialized, out _);
        materialized!.MaterializeAll(); // Non-null: gotMaterialized is asserted below.

        _ = rig.RestartOnto();
        await Assert.That(retiring.ShutdownCount).IsEqualTo(1);

        // A packet that was fully materialized before the restart is a complete snapshot.
        int eagerValueOfPending = -1;
        foreach (Core.Fields.Field field in pending!.IterFieldsFlat(materialize: false)) // Non-null: gotPending is asserted below.
        {
            if (field.FieldId == retiring.EagerFieldId)
            {
                eagerValueOfPending = field.Value.Data.TryGetAsU64(out ulong eager) ? (int)eager : -2;
            }
        }

        bool materializedHasChild = false;
        foreach (Core.Fields.Field field in materialized.IterFieldsFlat(materialize: false))
        {
            materializedHasChild |= field.FieldId == retiring.ChildFieldId;
        }

        // The lazy part of the other packet runs on the retired protocol: it must not throw into the reader, and it must not
        // pretend to have the data; the failure shows up as an error child.
        bool pendingHasChild = false;
        foreach (Core.Fields.Field field in pending.IterFieldsFlat(materialize: true))
        {
            pendingHasChild |= field.FieldId == retiring.ChildFieldId;
        }

        await Assert.That(gotPending && gotMaterialized).IsTrue();
        await Assert.That(eagerValueOfPending).IsEqualTo(64);
        await Assert.That(materializedHasChild).IsTrue();
        await Assert.That(pendingHasChild).IsFalse();
    }

    #endregion

    #region Restart that fails after the swap

    [Test]
    public async Task RestartThatFailsAfterTheSwapStillRetiresTheReplacedStack()
    {
        // The hole makes the re-parse of frame 2 fail, after the new stack was installed and the old one retired.
        using Rig rig = new(frameCount: 4, wrapSource: static inner => new HoleRandomAccessSource(inner, holeFrameIndex: 2));
        ProbeProtocol? first = null;
        ProbeProtocol? second = null;
        SessionException? firstFailure = null;
        SessionException? secondFailure = null;
        try
        {
            rig.Session.Restart(registry => ProbeStack.Create(registry, out first));
        }
        catch (SessionException ex)
        {
            firstFailure = ex;
        }

        try
        {
            rig.Session.Restart(registry => ProbeStack.Create(registry, out second));
        }
        catch (SessionException ex)
        {
            secondFailure = ex;
        }

        await Assert.That(firstFailure?.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(secondFailure?.Code).IsEqualTo(SessionErrorCode.FrameUnavailable);
        await Assert.That(first!.ShutdownCount).IsEqualTo(1);
        await Assert.That(second!.ShutdownCount).IsEqualTo(0);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
    }

    #endregion

    #region Shutdown

    [Test]
    public async Task ShutdownDisposesAStackWhoseLeaseLeakedAfterTheBudget()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol current = rig.RestartOnto();
        RetirableStack holder = rig.Session.StackHolder;
        bool leased = holder.TryAcquire();

        // The lease never ends within the budget: the shutdown gives up waiting and disposes anyway.
        rig.Session.Shutdown(TimeSpan.FromMilliseconds(50));
        int disposalsAfterShutdown = current.ShutdownCount;
        holder.Release();

        await Assert.That(leased).IsTrue();
        await Assert.That(disposalsAfterShutdown).IsEqualTo(1);
        await Assert.That(current.ShutdownCount).IsEqualTo(1);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
    }

    [Test]
    public async Task ShutdownWaitsForALeaseThatEndsWithinTheBudget()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol current = rig.RestartOnto();
        RetirableStack holder = rig.Session.StackHolder;
        bool leased = holder.TryAcquire();

        // No timeout: the shutdown can only finish after the release below. Queries are disabled right before it starts to
        // wait for the stacks, so waiting for that flag is the deterministic "shutdown is on its way" point.
        Task shutdown = _OnDedicatedThread(() =>
        {
            rig.Session.Shutdown();
            return 0;
        });
        WaitHelper.WaitUntil(() => !rig.Session.QueriesEnabled);
        int disposalsWhileLeased = current.ShutdownCount;
        holder.Release();
        await shutdown.WaitAsync(_Timeout);

        await Assert.That(leased).IsTrue();
        await Assert.That(disposalsWhileLeased).IsEqualTo(0);
        await Assert.That(current.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task ShutdownDisposesCurrentAndLeasedRetiredStackAndReleasesNeitherTwice()
    {
        using Rig rig = new(frameCount: 4);
        ProbeProtocol retired = rig.RestartOnto();
        RetirableStack retiredHolder = rig.Session.StackHolder;
        bool leased = retiredHolder.TryAcquire();
        ProbeProtocol current = rig.RestartOnto();
        int retiredBeforeShutdown = rig.Session.RetiredStackCount;
        int retiredDisposalsBeforeShutdown = retired.ShutdownCount;

        rig.Session.Shutdown(TimeSpan.FromMilliseconds(50));
        retiredHolder.Release();

        await Assert.That(leased).IsTrue();
        await Assert.That(retiredBeforeShutdown).IsEqualTo(1);
        await Assert.That(retiredDisposalsBeforeShutdown).IsEqualTo(0);
        await Assert.That(retired.ShutdownCount).IsEqualTo(1);
        await Assert.That(current.ShutdownCount).IsEqualTo(1);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
    }

    [Test]
    public async Task ShutdownNeverDisposesTheStackTheCallerOwns()
    {
        using Stack initial = ProbeStack.Create(new(), out ProbeProtocol probe);
        using Session session = new(initial);

        session.Shutdown();

        await Assert.That(probe.ShutdownCount).IsEqualTo(0);
        await Assert.That(session.RetiredStackCount).IsEqualTo(0);
    }

    #endregion

    #region Reporting

    [Test]
    public async Task ShutdownReportsAProtocolShutdownErrorOfARetiredStackExactlyOnce()
    {
        using Rig rig = new(frameCount: 2);
        _ = rig.RestartOnto(throwOnShutdown: true);
        _ = rig.RestartOnto();

        AggregateException? first = null;
        try
        {
            rig.Session.Shutdown();
        }
        catch (AggregateException ex)
        {
            first = ex;
        }

        // A second call waits for the first and does not repeat its report.
        rig.Session.Shutdown();

        await Assert.That(first).IsNotNull();
        await Assert.That(first!.InnerExceptions.Count(static e => e is InvalidOperationException)).IsEqualTo(1);
    }

    #endregion
}
