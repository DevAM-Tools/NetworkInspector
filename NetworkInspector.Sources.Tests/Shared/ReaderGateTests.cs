// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// Tests for <see cref="ReaderGate"/> and <see cref="ReaderSlot"/>: the close protocol, rejected entries, nesting, the
/// self-close guard, and the release of slots of finished threads. Threads are named, joined with a bound, and
/// synchronised with events, <see cref="SpinWait"/>, and <see cref="Interlocked"/>; no test sleeps.
/// </summary>
internal sealed class ReaderGateTests
{
    #region Constants

    private static readonly TimeSpan _JoinTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Constraint key of the tests that spin on several threads. Reason: with every repetition in parallel, dozens of
    /// spinning threads oversubscribe the cores, and a thread that has to see another thread's write waits for a time
    /// slice (about 40 ms per round instead of microseconds). Serialising them keeps the run fast.
    /// </summary>
    private const string _SpinningTestsKey = "ReaderGateSpinning";

    #endregion

    #region Close waits

    [Test]
    [Repeat(20)]
    [NotInParallel(_SpinningTestsKey)]
    public async Task CloseWaitsForASectionOnAnotherThread()
    {
        int insideAfterClose = 1;
        int exitsAfterClose = 0;
        for (int round = 0; round < 200; round++)
        {
            ReaderGate gate = new();
            SharedState state = new();
            using ManualResetEventSlim entered = new(false);
            Worker reader = new("gate-reader", () =>
            {
                if (!gate.TryEnter(out ReaderGateToken token))
                {
                    throw new InvalidOperationException("The reader could not enter an open gate.");
                }

                Volatile.Write(ref state.Inside, 1);
                entered.Set();

                // Stays inside until Close started: the closer must then wait for the exit below.
                SpinWait spin = default;
                while (!gate.IsClosed)
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }

                Volatile.Write(ref state.Inside, 0);
                _ = Interlocked.Increment(ref state.Exits);
                gate.Exit(in token);
            });

            await Assert.That(entered.Wait(_JoinTimeout)).IsTrue();
            gate.Close();
            insideAfterClose = Volatile.Read(ref state.Inside);
            exitsAfterClose = Volatile.Read(ref state.Exits);
            reader.Join();

            await Assert.That(insideAfterClose).IsEqualTo(0);
            await Assert.That(exitsAfterClose).IsEqualTo(1);
        }
    }

    #endregion

    #region Rejected entries

    [Test]
    public async Task EnterAfterCloseFailsAndLeavesNothingHeld()
    {
        ReaderGate gate = new();
        ReaderGate other = new();
        gate.Close();

        bool enteredHere = gate.TryEnter(out _);
        bool holdsHere = ReaderSlot.CurrentHolds(gate);

        bool enteredThere = true;
        bool holdsThere = true;
        bool otherWorksThere = false;
        Worker worker = new("gate-rejected", () =>
        {
            enteredThere = gate.TryEnter(out _);
            holdsThere = ReaderSlot.CurrentHolds(gate);
            if (other.TryEnter(out ReaderGateToken token))
            {
                otherWorksThere = ReaderSlot.CurrentHolds(other);
                other.Exit(in token);
            }
        });
        worker.Join();

        await Assert.That(enteredHere).IsFalse();
        await Assert.That(holdsHere).IsFalse();
        await Assert.That(enteredThere).IsFalse();
        await Assert.That(holdsThere).IsFalse();
        await Assert.That(otherWorksThere).IsTrue();

        // A second gate on the closing thread still enters and exits normally. No await between enter and exit: a
        // section belongs to one thread, and an await may resume on another.
        bool otherEntered = other.TryEnter(out ReaderGateToken here);
        bool otherIsOverflow = here.IsOverflow;
        other.Exit(in here);
        bool otherHeldAfterExit = ReaderSlot.CurrentHolds(other);

        await Assert.That(otherEntered).IsTrue();
        await Assert.That(otherIsOverflow).IsFalse();
        await Assert.That(otherHeldAfterExit).IsFalse();
    }

    [Test]
    public async Task NeverEnteredGateClosesAtOnce()
    {
        ReaderGate gate = new();

        gate.Close();

        await Assert.That(gate.IsClosed).IsTrue();
    }

    #endregion

    #region Nesting

    [Test]
    [Repeat(20)]
    [NotInParallel(_SpinningTestsKey)]
    public async Task NestedSectionsUseTheOverflowPathAndCloseWaitsForThem()
    {
        ReaderGate first = new();
        ReaderGate second = new();
        ReaderGate third = new();
        SharedState state = new();
        using ManualResetEventSlim entered = new(false);
        bool holdsBoth = false;
        bool secondIsOverflow = false;
        bool secondAgainIsOverflow = false;
        bool slotFreeAgain = false;
        bool holdsNone = true;

        Worker reader = new("gate-nested", () =>
        {
            // The first section takes the slot; the next two sections of the second gate find it busy.
            if (!first.TryEnter(out ReaderGateToken t1) || !second.TryEnter(out ReaderGateToken t2) || !second.TryEnter(out ReaderGateToken t3))
            {
                throw new InvalidOperationException("A nested section could not be entered.");
            }

            secondIsOverflow = t2.IsOverflow;
            secondAgainIsOverflow = t3.IsOverflow;
            holdsBoth = ReaderSlot.CurrentHolds(first) && ReaderSlot.CurrentHolds(second);
            entered.Set();

            // Leave the second gate only after its Close started; count both exits before they happen.
            SpinWait spin = default;
            while (!second.IsClosed)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }

            _ = Interlocked.Increment(ref state.Exits);
            second.Exit(in t3);
            _ = Interlocked.Increment(ref state.Exits);
            second.Exit(in t2);

            // The first section is still held until its own Close started.
            spin = default;
            while (!first.IsClosed)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }

            _ = Interlocked.Increment(ref state.Inside);
            first.Exit(in t1);

            // LIFO exits leave the slot and the overflow list empty: another gate takes the slot again.
            holdsNone = !ReaderSlot.CurrentHolds(first) && !ReaderSlot.CurrentHolds(second);
            slotFreeAgain = third.TryEnter(out ReaderGateToken t4) && !t4.IsOverflow;
            third.Exit(in t4);
        });

        await Assert.That(entered.Wait(_JoinTimeout)).IsTrue();

        int exitsWhenSecondClosed = -1;
        Worker secondCloser = new("gate-close-second", () =>
        {
            second.Close();
            exitsWhenSecondClosed = Volatile.Read(ref state.Exits);
        });
        secondCloser.Join();

        int exitsWhenFirstClosed = -1;
        int insideWhenFirstClosed = -1;
        Worker firstCloser = new("gate-close-first", () =>
        {
            first.Close();
            insideWhenFirstClosed = Volatile.Read(ref state.Inside);
            exitsWhenFirstClosed = Volatile.Read(ref state.Exits);
        });
        firstCloser.Join();
        reader.Join();

        await Assert.That(holdsBoth).IsTrue();
        await Assert.That(secondIsOverflow).IsTrue();
        await Assert.That(secondAgainIsOverflow).IsTrue();
        await Assert.That(exitsWhenSecondClosed).IsEqualTo(2);
        await Assert.That(exitsWhenFirstClosed).IsEqualTo(2);
        await Assert.That(insideWhenFirstClosed).IsEqualTo(1);
        await Assert.That(holdsNone).IsTrue();
        await Assert.That(slotFreeAgain).IsTrue();
    }

    #endregion

    #region Self close

    [Test]
    public async Task CloseFromInsideASectionThrowsAndTheSectionStillExits()
    {
        // The section stays on this thread: no await between enter and exit (an await may resume on another thread).
        ReaderGate gate = new();
        bool entered = gate.TryEnter(out ReaderGateToken token);
        bool firstCloseThrew = _Throws(() => gate.Close());

        // The failed close left the gate closed: other threads are rejected, and this section's own Exit clears its record.
        bool enteredThere = true;
        new Worker("gate-after-self-close", () => enteredThere = gate.TryEnter(out _)).Join();
        bool closed = gate.IsClosed;
        gate.Exit(in token);
        bool heldAfterExit = ReaderSlot.CurrentHolds(gate);
        bool secondCloseThrew = _Throws(() => gate.Close());

        await Assert.That(entered).IsTrue();
        await Assert.That(firstCloseThrew).IsTrue();
        await Assert.That(enteredThere).IsFalse();
        await Assert.That(closed).IsTrue();
        await Assert.That(heldAfterExit).IsFalse();
        await Assert.That(secondCloseThrew).IsTrue();
    }

    [Test]
    public async Task CloseFromInsideAnOverflowSectionThrows()
    {
        ReaderGate outer = new();
        ReaderGate gate = new();
        bool outerEntered = outer.TryEnter(out ReaderGateToken outerToken);
        bool entered = gate.TryEnter(out ReaderGateToken token);
        bool isOverflow = token.IsOverflow;
        bool closeThrew = _Throws(() => gate.Close());
        gate.Exit(in token);
        outer.Exit(in outerToken);
        bool gateHeld = ReaderSlot.CurrentHolds(gate);
        bool outerHeld = ReaderSlot.CurrentHolds(outer);

        await Assert.That(outerEntered).IsTrue();
        await Assert.That(entered).IsTrue();
        await Assert.That(isOverflow).IsTrue();
        await Assert.That(closeThrew).IsTrue();
        await Assert.That(gateHeld).IsFalse();
        await Assert.That(outerHeld).IsFalse();
    }

    [Test]
    public async Task SecondCloseThrows()
    {
        ReaderGate gate = new();
        gate.Close();

        await Assert.That(() => gate.Close()).Throws<InvalidOperationException>();
    }

    #endregion

    #region Concurrency

    [Test]
    [Repeat(20)]
    [NotInParallel(_SpinningTestsKey)]
    [Timeout(120_000)]
    public async Task NobodyIsInsideAfterCloseWithFourLoopingReaders(CancellationToken cancellationToken)
    {
        const int readerCount = 4;
        const int minimumEntries = 1_000;
        for (int round = 0; round < 50; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReaderGate gate = new();
            int inside = 0;
            int rejected = 0;
            int[] entries = new int[readerCount];
            Worker[] readers = new Worker[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                int index = r;
                readers[r] = new Worker(Invariant($"gate-concurrent-{r}"), () =>
                {
                    while (gate.TryEnter(out ReaderGateToken token))
                    {
                        _ = Interlocked.Increment(ref inside);
                        _ = Interlocked.Decrement(ref inside);
                        gate.Exit(in token);
                        _ = Interlocked.Increment(ref entries[index]);
                    }

                    // Closed: one more attempt is rejected too and leaves nothing held.
                    if (!gate.TryEnter(out _))
                    {
                        _ = Interlocked.Increment(ref rejected);
                    }
                });
            }

            // Close only when every reader has entered many times, so Close meets readers in the middle of their loops.
            SpinWait spin = default;
            while (!_AllReached(entries, minimumEntries))
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }

            gate.Close();
            int insideAfterClose = Volatile.Read(ref inside);
            foreach (Worker reader in readers)
            {
                reader.Join();
            }

            await Assert.That(insideAfterClose).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref inside)).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref rejected)).IsEqualTo(readerCount);
        }
    }

    #endregion

    #region Slot lifetime

    [Test]
    public async Task SlotsOfFinishedThreadsAreReleased()
    {
        const int finishedCount = 64;
        ReaderGate gate = new();
        Thread[] finished = new Thread[finishedCount];
        for (int i = 0; i < finishedCount; i++)
        {
            Worker worker = new(Invariant($"gate-prune-{i}"), () =>
            {
                if (gate.TryEnter(out ReaderGateToken token))
                {
                    gate.Exit(in token);
                }
            });
            finished[i] = worker.Thread;
            worker.Join();
        }

        // A new thread registers its slot, which unlinks the slots of the finished threads.
        using ManualResetEventSlim registered = new(false);
        using ManualResetEventSlim release = new(false);
        Worker live = new("gate-prune-live", () =>
        {
            if (gate.TryEnter(out ReaderGateToken token))
            {
                gate.Exit(in token);
            }

            registered.Set();
            _ = release.Wait(_JoinTimeout);
        });
        await Assert.That(registered.Wait(_JoinTimeout)).IsTrue();

        int stillRegistered = 0;
        foreach (Thread thread in finished)
        {
            if (ReaderSlot.IsRegistered(thread))
            {
                stillRegistered++;
            }
        }

        bool liveRegistered = ReaderSlot.IsRegistered(live.Thread);
        release.Set();
        live.Join();

        await Assert.That(stillRegistered).IsEqualTo(0);
        await Assert.That(liveRegistered).IsTrue();
    }

    [Test]
    public async Task SlotStaysWithinTheAgreedBound()
    {
        int instanceFields = typeof(ReaderSlot)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Length;

        // Header (16 B) plus one 8-byte reference per field on 64-bit.
        int computedBytes = 16 + (8 * instanceFields);
        int declaredBytes = ReaderSlot.SizeInBytes;

        await Assert.That(instanceFields).IsEqualTo(3);
        await Assert.That(declaredBytes).IsEqualTo(computedBytes);
        await Assert.That(declaredBytes).IsLessThanOrEqualTo(64);
    }

    #endregion

    #region Private helpers

    /// <summary>True when <paramref name="action"/> throws <see cref="InvalidOperationException"/>.</summary>
    private static bool _Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool _AllReached(int[] counts, int minimum)
    {
        for (int i = 0; i < counts.Length; i++)
        {
            if (Volatile.Read(ref counts[i]) < minimum)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Counters shared between a test and its worker threads; fields so that they can be passed by reference.</summary>
    private sealed class SharedState
    {
        public int Inside;
        public int Exits;
    }

    /// <summary>A named background thread that records its failure and rethrows it from <see cref="Join"/>.</summary>
    private sealed class Worker
    {
        private readonly Thread _Thread;
        private Exception? _Failure;

        public Worker(string name, Action body)
        {
            _Thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception exception)
                {
                    _Failure = exception;
                }
            })
            {
                Name = name,
                IsBackground = true,
            };
            _Thread.Start();
        }

        public Thread Thread => _Thread;

        public void Join()
        {
            if (!_Thread.Join(_JoinTimeout))
            {
                throw new TimeoutException(Invariant($"Thread '{_Thread.Name}' did not finish."));
            }

            if (_Failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_Failure).Throw();
            }
        }
    }

    #endregion
}
