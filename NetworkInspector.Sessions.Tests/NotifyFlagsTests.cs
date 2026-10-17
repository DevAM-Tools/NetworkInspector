// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Tests for <see cref="NotifyFlags"/> — atomic flag-based notification semantics.
/// Verifies flag OR, Exchange-clearing, and coalescing behaviour.
/// </summary>
internal sealed class NotifyFlagsTests
{
    [Test]
    public async Task Flags_IndividualBitsAreDistinct()
    {
        // Ensure all flags are powers of two (no overlap).
        NotifyFlags[] allFlags =
        [
            NotifyFlags.NewPackets,
            NotifyFlags.SourceAdded,
            NotifyFlags.SourceCompleted,
            NotifyFlags.AllSourcesCompleted,
            NotifyFlags.JobAdded,
            NotifyFlags.JobStatusChanged,
            NotifyFlags.JobRemoved,
            NotifyFlags.PhaseChanged,
            NotifyFlags.ShuttingDown,
            NotifyFlags.StackChanged,
        ];

        for (int i = 0; i < allFlags.Length; i++)
        {
            for (int j = i + 1; j < allFlags.Length; j++)
            {
                NotifyFlags combined = allFlags[i] & allFlags[j];
                await Assert.That(combined).IsEqualTo(NotifyFlags.None);
            }
        }
    }

    [Test]
    public async Task InterlockedOr_CoalesceMultipleFlags()
    {
        int field = 0;
        Interlocked.Or(ref field, (int)NotifyFlags.NewPackets);
        Interlocked.Or(ref field, (int)NotifyFlags.SourceCompleted);
        Interlocked.Or(ref field, (int)NotifyFlags.NewPackets); // Duplicate — idempotent

        NotifyFlags result = (NotifyFlags)Interlocked.Exchange(ref field, 0);

        // Both flags should be set, duplicate was idempotent.
        await Assert.That(result.HasFlag(NotifyFlags.NewPackets)).IsTrue();
        await Assert.That(result.HasFlag(NotifyFlags.SourceCompleted)).IsTrue();

        // Field should be cleared after Exchange.
        await Assert.That(field).IsEqualTo(0);
    }

    [Test]
    public async Task Exchange_ClearsFieldAtomically()
    {
        int field = (int)(NotifyFlags.NewPackets | NotifyFlags.PhaseChanged);

        int read = Interlocked.Exchange(ref field, 0);
        NotifyFlags flags = (NotifyFlags)read;

        await Assert.That(flags.HasFlag(NotifyFlags.NewPackets)).IsTrue();
        await Assert.That(flags.HasFlag(NotifyFlags.PhaseChanged)).IsTrue();

        // Second exchange reads 0 — nothing pending.
        int second = Interlocked.Exchange(ref field, 0);
        await Assert.That(second).IsEqualTo(0);
    }

    [Test]
    public async Task None_HasNoFlagsSet()
    {
        NotifyFlags none = NotifyFlags.None;
        await Assert.That((int)none).IsEqualTo(0);
    }

    /// <summary>
    /// Many producers notify a slot that has not started yet. No flag may be lost, and the slot must deliver
    /// everything in one coalesced dispatch: each callback exactly once, however many notifications arrived.
    /// </summary>
    [Test]
    public async Task ListenerSlotCoalescesConcurrentNotifiesIntoOneDispatch()
    {
        NotifyFlags[] kinds =
        [
            NotifyFlags.SourceAdded,
            NotifyFlags.AllSourcesCompleted,
            NotifyFlags.JobAdded,
            NotifyFlags.PhaseChanged,
        ];
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        CountingListener listener = new();
        using ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });

        _RunProducers(8, 1_000, producer => slot.Notify(kinds[producer % kinds.Length]));
        int pending = slot.Flags;
        slot.Start();
        WaitHelper.WaitUntil(() => listener.PhaseChanged >= 1 && listener.JobsChanged >= 1
            && listener.SourcesChanged >= 1 && listener.AllSourcesCompleted >= 1);
        slot.Cancel();
        slot.Join();

        int expected = (int)(NotifyFlags.SourceAdded | NotifyFlags.AllSourcesCompleted | NotifyFlags.JobAdded | NotifyFlags.PhaseChanged);
        await Assert.That(pending).IsEqualTo(expected);
        await Assert.That(listener.SourcesChanged).IsEqualTo(1);
        await Assert.That(listener.AllSourcesCompleted).IsEqualTo(1);
        await Assert.That(listener.JobsChanged).IsEqualTo(1);
        await Assert.That(listener.PhaseChanged).IsEqualTo(1);
    }

    /// <summary>
    /// Producers hammer a running slot while it dispatches. The flag protocol (OR, reset before wait, re-check)
    /// must never lose the last wake-up: the final notification of each producer is always delivered.
    /// </summary>
    [Test]
    public async Task ListenerSlotNeverLosesTheLastWakeUnderConcurrentProducers()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        CountingListener listener = new();
        using ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });
        slot.Start();

        _RunProducers(8, 2_000, _ => slot.Notify(NotifyFlags.JobStatusChanged));
        _RunProducers(8, 1, _ => slot.Notify(NotifyFlags.SourceCompleted));
        WaitHelper.WaitUntil(() => listener.SourcesChanged >= 1);
        slot.Cancel();
        slot.Join();

        await Assert.That(listener.SourcesChanged).IsGreaterThanOrEqualTo(1);
        await Assert.That(listener.JobsChanged).IsGreaterThanOrEqualTo(1);
        await Assert.That(listener.JobsChanged).IsLessThanOrEqualTo(8 * 2_000);
    }

    /// <summary>Starts <paramref name="threadCount"/> threads at once, each calling <paramref name="notify"/> <paramref name="perThread"/> times.</summary>
    private static void _RunProducers(int threadCount, int perThread, Action<int> notify)
    {
        using Barrier start = new(threadCount);
        Thread[] threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int producer = t;
            threads[t] = new Thread(() =>
            {
                start.SignalAndWait();
                for (int i = 0; i < perThread; i++)
                {
                    notify(producer);
                }
            })
            {
                Name = "notify-producer",
                IsBackground = true,
                CurrentCulture = CultureInfo.InvariantCulture,
            };
        }

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }
    }

    /// <summary>Counts callbacks; thread-safe through <see cref="Interlocked"/>.</summary>
    private sealed class CountingListener : ISessionListener
    {
        private int _SourcesChanged;
        private int _AllSourcesCompleted;
        private int _JobsChanged;
        private int _PhaseChanged;

        public string UiName => "counting";

        internal int SourcesChanged => Volatile.Read(ref _SourcesChanged);

        internal int AllSourcesCompleted => Volatile.Read(ref _AllSourcesCompleted);

        internal int JobsChanged => Volatile.Read(ref _JobsChanged);

        internal int PhaseChanged => Volatile.Read(ref _PhaseChanged);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }

        public void OnSourcesChanged(ISessionReader session) => Interlocked.Increment(ref _SourcesChanged);

        public void OnAllSourcesCompleted(ISessionReader session) => Interlocked.Increment(ref _AllSourcesCompleted);

        public void OnJobsChanged(ISessionReader session) => Interlocked.Increment(ref _JobsChanged);

        public void OnPhaseChanged(SessionPhase phase) => Interlocked.Increment(ref _PhaseChanged);
    }
}
