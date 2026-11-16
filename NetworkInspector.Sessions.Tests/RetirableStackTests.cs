// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// The lease counter and the exactly-once disposal of <see cref="RetirableStack"/>, in isolation: the dispose action is a
/// counting probe, so the tests see how often and when the disposal ran without building protocol stacks.
/// </summary>
internal sealed class RetirableStackTests
{
    #region Fixtures

    /// <summary>Upper bound of a wait; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Counts the disposals and the reports of one <see cref="RetirableStack"/> and notes a disposal that overlaps a lease.
    /// Thread-safety: all members are interlocked/volatile.
    /// </summary>
    private sealed class Spy
    {
        private int _Disposals;
        private int _Reports;
        private int _ActiveLeases;
        private int _DisposedUnderLease;
        private Exception[] _LastErrors = [];

        internal int Disposals => Volatile.Read(ref _Disposals);

        internal int Reports => Volatile.Read(ref _Reports);

        internal int DisposedUnderLease => Volatile.Read(ref _DisposedUnderLease);

        internal Exception[] LastErrors => Volatile.Read(ref _LastErrors);

        internal void LeaseEntered() => _ = Interlocked.Increment(ref _ActiveLeases);

        internal void LeaseLeaving() => _ = Interlocked.Decrement(ref _ActiveLeases);

        internal void Dispose(Stack stack)
        {
            // A disposal while a counted lease is inside is exactly the defect the counter prevents.
            if (Volatile.Read(ref _ActiveLeases) != 0)
            {
                _ = Interlocked.Increment(ref _DisposedUnderLease);
            }

            _ = Interlocked.Increment(ref _Disposals);
        }

        internal void Report(RetirableStack stack, Exception[] errors)
        {
            Volatile.Write(ref _LastErrors, errors);
            _ = Interlocked.Increment(ref _Reports);
        }
    }

    /// <summary>One shared stack for the tests that never let the real disposal run (the spy replaces it).</summary>
    private static Stack _NewStack() => TestHarness.CreateStack();

    private static RetirableStack _Guard(Stack stack, Spy spy, bool owned = true) =>
        new(stack, generation: 0, owned, spy.Report, spy.Dispose);

    #endregion

    #region Single-threaded behaviour

    [Test]
    public async Task LeaseCountsAcquiresAndReleasesWithoutDisposing()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);

        bool first = guard.TryAcquire();
        bool second = guard.TryAcquire();
        int whileHeld = guard.LeaseCount;
        guard.Release();
        guard.Release();

        await Assert.That(first && second).IsTrue();
        await Assert.That(whileHeld).IsEqualTo(2);
        await Assert.That(guard.LeaseCount).IsEqualTo(0);
        await Assert.That(guard.IsRetired).IsFalse();
        await Assert.That(spy.Disposals).IsEqualTo(0);
    }

    [Test]
    public async Task RetireWithoutLeasesDisposesAtOnceAndReportsOnce()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);

        guard.Retire();
        guard.Retire();

        await Assert.That(guard.IsDisposed).IsTrue();
        await Assert.That(spy.Disposals).IsEqualTo(1);
        await Assert.That(spy.Reports).IsEqualTo(1);
        await Assert.That(guard.WaitUntilDisposed(TimeSpan.Zero)).IsTrue();
    }

    [Test]
    public async Task RetireWithALeaseDefersTheDisposalToTheLastRelease()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        _ = guard.TryAcquire();
        _ = guard.TryAcquire();

        guard.Retire();
        int disposalsAfterRetire = spy.Disposals;
        bool waitedWhileHeld = guard.WaitUntilDisposed(TimeSpan.Zero);
        guard.Release();
        int disposalsAfterFirstRelease = spy.Disposals;
        guard.Release();

        await Assert.That(disposalsAfterRetire).IsEqualTo(0);
        await Assert.That(waitedWhileHeld).IsFalse();
        await Assert.That(disposalsAfterFirstRelease).IsEqualTo(0);
        await Assert.That(spy.Disposals).IsEqualTo(1);
        await Assert.That(spy.Reports).IsEqualTo(1);
        await Assert.That(guard.WaitUntilDisposed(TimeSpan.Zero)).IsTrue();
    }

    [Test]
    public async Task AcquireAfterRetireIsRefusedAndLeavesTheCountAtZero()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        _ = guard.TryAcquire();
        guard.Retire();

        bool refused = guard.TryAcquire(out StackLease lease);
        lease.Dispose();
        int countWhileFirstStillHeld = guard.LeaseCount;
        guard.Release();

        await Assert.That(refused).IsFalse();
        await Assert.That(countWhileFirstStillHeld).IsEqualTo(1);
        await Assert.That(guard.LeaseCount).IsEqualTo(0);
        await Assert.That(spy.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task AcquireAfterTheDisposalIsRefusedWithoutDisposingAgain()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        guard.Retire();

        bool refused = guard.TryAcquire();

        await Assert.That(refused).IsFalse();
        await Assert.That(guard.LeaseCount).IsEqualTo(0);
        await Assert.That(spy.Disposals).IsEqualTo(1);
        await Assert.That(spy.Reports).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeNowDisposesDespiteALeaseAndALaterReleaseDoesNotDisposeAgain()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        _ = guard.TryAcquire();

        guard.DisposeNow();
        int disposalsWhileLeased = spy.Disposals;
        guard.Release();
        guard.DisposeNow();

        await Assert.That(disposalsWhileLeased).IsEqualTo(1);
        await Assert.That(spy.Disposals).IsEqualTo(1);
        await Assert.That(spy.Reports).IsEqualTo(1);
    }

    [Test]
    public async Task StackTheSessionDoesNotOwnIsRetiredButNeverDisposedNorReported()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy, owned: false);
        _ = guard.TryAcquire();

        guard.Retire();
        bool waitedWhileHeld = guard.WaitUntilDisposed(TimeSpan.Zero);
        guard.Release();

        await Assert.That(guard.IsRetired).IsTrue();
        await Assert.That(waitedWhileHeld).IsFalse();
        await Assert.That(guard.WaitUntilDisposed(TimeSpan.Zero)).IsTrue();
        await Assert.That(spy.Disposals).IsEqualTo(0);
        await Assert.That(spy.Reports).IsEqualTo(0);
    }

    [Test]
    public async Task ExceptionFromTheDisposalIsReportedOnceAndTheWaiterIsStillReleased()
    {
        using Stack stack = _NewStack();
        InvalidOperationException failure = new("dispose failed");
        List<Exception[]> reports = [];
        RetirableStack guard = new(
            stack,
            generation: 0,
            ownedBySession: true,
            (_, errors) => reports.Add(errors),
            _ => throw failure);

        guard.Retire();
        guard.Retire();

        await Assert.That(reports.Count).IsEqualTo(1);
        await Assert.That(reports[0].Length).IsEqualTo(1);
        await Assert.That(reports[0][0]).IsSameReferenceAs(failure);
        await Assert.That(guard.WaitUntilDisposed(TimeSpan.Zero)).IsTrue();
    }

    [Test]
    public async Task ProtocolShutdownErrorsOfTheStackAreReportedWithTheDisposal()
    {
        using Stack stack = ProbeStack.Create(new(), out ProbeProtocol probe, throwOnShutdown: true);
        List<Exception[]> reports = [];
        RetirableStack guard = new(stack, generation: 0, ownedBySession: true, (_, errors) => reports.Add(errors));

        guard.Retire();

        await Assert.That(probe.ShutdownCount).IsEqualTo(1);
        await Assert.That(reports.Count).IsEqualTo(1);
        await Assert.That(reports[0].Length).IsEqualTo(1);
        await Assert.That(reports[0][0]).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task WaitUntilDisposedReturnsFalseAfterItsTimeoutWhileALeaseIsHeld()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        _ = guard.TryAcquire();
        guard.Retire();

        bool disposed = guard.WaitUntilDisposed(TimeSpan.FromMilliseconds(20));
        guard.Release();

        await Assert.That(disposed).IsFalse();
        await Assert.That(spy.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task WaitUntilDisposedIsReleasedByTheLastRelease()
    {
        using Stack stack = _NewStack();
        Spy spy = new();
        RetirableStack guard = _Guard(stack, spy);
        _ = guard.TryAcquire();
        guard.Retire();

        // The waiter parks on the monitor; the release below (this thread) wakes it. No timing assumption: the waiter
        // returns true whether it started waiting before or after the release.
        Task<bool> waiter = Task.Factory.StartNew(
            () => guard.WaitUntilDisposed(Timeout.InfiniteTimeSpan),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        guard.Release();

        await Assert.That(await waiter.WaitAsync(_Timeout)).IsTrue();
    }

    #endregion

    #region Races

    [Test]
    [Category("Slow")]
    public async Task ConcurrentReadersAndRetireDisposeExactlyOnceAndNeverUnderALease()
    {
        // Repeated: each round races four readers (acquire, hold, release in a loop) against one retire, starting on a
        // barrier. Whoever makes the last transition disposes; the spy fails the round if a disposal overlapped a lease.
        using Stack stack = _NewStack();
        int rounds = 400;
        int violations = 0;
        for (int round = 0; round < rounds; round++)
        {
            Spy spy = new();
            RetirableStack guard = _Guard(stack, spy);
            ConcurrencyHelper.RunTogether(5, index =>
            {
                if (index == 4)
                {
                    guard.Retire();
                    return;
                }

                for (int i = 0; i < 200; i++)
                {
                    if (!guard.TryAcquire(out StackLease lease))
                    {
                        continue;
                    }

                    spy.LeaseEntered();
                    Thread.SpinWait(20);
                    spy.LeaseLeaving();
                    lease.Dispose();
                }
            });

            if (spy.Disposals != 1 || spy.Reports != 1 || spy.DisposedUnderLease != 0 || guard.LeaseCount != 0 || !guard.IsDisposed)
            {
                violations++;
            }
        }

        await Assert.That(violations).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentRetireDisposeNowAndReleasesStillDisposeExactlyOnce()
    {
        using Stack stack = _NewStack();
        int violations = 0;
        for (int round = 0; round < 300; round++)
        {
            Spy spy = new();
            RetirableStack guard = _Guard(stack, spy);
            _ = guard.TryAcquire();
            _ = guard.TryAcquire();
            ConcurrencyHelper.RunTogether(4, index =>
            {
                switch (index)
                {
                    case 0:
                        guard.Retire();
                        break;
                    case 1:
                        guard.DisposeNow();
                        break;
                    default:
                        guard.Release();
                        break;
                }
            });

            if (spy.Disposals != 1 || spy.Reports != 1 || guard.LeaseCount != 0)
            {
                violations++;
            }
        }

        await Assert.That(violations).IsEqualTo(0);
    }

    #endregion

    #region Index selection

    [Test]
    public async Task IndexOfAnotherStackIsNotUsedForAParse()
    {
        using Stack own = _NewStack();
        using Stack other = _NewStack();
        PacketIndex ownIndex = new(own);
        PacketIndex otherIndex = new(other);

        await Assert.That(Session.SelectIndexFor(ownIndex, own)).IsSameReferenceAs(ownIndex);
        await Assert.That(Session.SelectIndexFor(otherIndex, own)).IsNull();
        await Assert.That(Session.SelectIndexFor(null, own)).IsNull();
    }

    #endregion
}
