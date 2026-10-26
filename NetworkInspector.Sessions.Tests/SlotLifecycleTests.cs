// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Cancellation and disposal of <see cref="ListenerSlot"/> and <see cref="ValueCacheSlot"/>: the thread drains the flags
/// that were raised before the cancellation, and a late notifier or canceller never trips over a disposed slot.
/// </summary>
internal sealed class SlotLifecycleTests
{
    #region Helpers

    /// <summary>How long a test waits for a background thread before it gives up instead of hanging.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Session listener that records the dispatch thread, counts shutdown callbacks and can hold a callback.</summary>
    private sealed class DrainListener : ISessionListener
    {
        private int _ShuttingDown;
        private int _Unsubscribed;

        public string UiName => "DrainListener";

        /// <summary>Set inside <see cref="OnJobsChanged"/>, on the slot thread.</summary>
        internal ManualResetEventSlim JobsChanged { get; } = new(false);

        /// <summary>When set, <see cref="OnJobsChanged"/> blocks on it after signalling <see cref="JobsChanged"/>.</summary>
        internal ManualResetEventSlim? Hold { get; init; }

        internal Thread? SlotThread { get; private set; }

        internal int ShuttingDownCount => Volatile.Read(ref _ShuttingDown);

        internal int UnsubscribedCount => Volatile.Read(ref _Unsubscribed);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }

        public void OnJobsChanged(ISessionReader session)
        {
            SlotThread = Thread.CurrentThread;
            JobsChanged.Set();
            Hold?.Wait(_Timeout);
        }

        public void OnShuttingDown() => Interlocked.Increment(ref _ShuttingDown);

        public void OnUnsubscribed() => Interlocked.Increment(ref _Unsubscribed);
    }

    /// <summary>Value-cache listener with the same recording as <see cref="DrainListener"/>.</summary>
    private sealed class DrainCacheListener : IValueCacheListener
    {
        private int _ShuttingDown;

        public string UiName => "DrainCacheListener";

        internal ManualResetEventSlim JobsChanged { get; } = new(false);

        internal Thread? SlotThread { get; private set; }

        internal int ShuttingDownCount => Volatile.Read(ref _ShuttingDown);

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
        }

        public void OnJobsChanged(ISessionReader session)
        {
            SlotThread = Thread.CurrentThread;
            JobsChanged.Set();
        }

        public void OnShuttingDown() => Interlocked.Increment(ref _ShuttingDown);
    }

    /// <summary>Waits (spinning, no sleep) until the thread is blocked in a kernel wait, i.e. idle in the wake event.</summary>
    private static void _WaitUntilBlocked(Thread thread) =>
        WaitHelper.WaitUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0);

    private static ValueCache _CreateWriter(Stack stack) =>
        new(stack, [new ValueCacheFieldConfig(stack.GetFieldId("udp.srcport")!.Value)]);

    #endregion

    #region Drain after cancellation

    [Test]
    public async Task ListenerSlotCancelledWhileIdleStillDeliversTheFlagsRaisedBeforeTheCancel()
    {
        // Interleaving: the slot thread sleeps in the wake event; a producer raises ShuttingDown, then the session cancels.
        // The wait is interrupted by the cancellation; the flag raised before it must still be dispatched.
        const int trials = 50;
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        int lost = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            DrainListener listener = new();
            ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });
            slot.Start();
            slot.Notify(NotifyFlags.JobAdded);
            await Assert.That(listener.JobsChanged.Wait(_Timeout)).IsTrue();
            _WaitUntilBlocked(listener.SlotThread!);

            slot.Notify(NotifyFlags.ShuttingDown);
            slot.Cancel();
            slot.Join();

            lost += 1 - listener.ShuttingDownCount;
            slot.Dispose();
            listener.JobsChanged.Dispose();
        }

        await Assert.That(lost).IsEqualTo(0);
    }

    [Test]
    public async Task ValueCacheSlotCancelledWhileIdleStillDeliversTheFlagsRaisedBeforeTheCancel()
    {
        const int trials = 50;
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        ValueCacheRequest request = new() { FieldNames = ["udp.srcport"] };
        int lost = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            DrainCacheListener listener = new();
            ValueCache writer = _CreateWriter(stack);
            ValueCacheSlot slot = new(
                new JobId(0),
                listener,
                session,
                request,
                writer,
                ValueCacheFillMode.NotifyOnly,
                static _ => throw new InvalidOperationException("No rebuild expected."),
                () => writer,
                static (out StackLease lease) =>
                {
                    // NotifyOnly never parses, so it never asks for a lease.
                    lease = default;
                    return false;
                },
                static (_, _) => { });
            slot.Start();
            slot.Notify(NotifyFlags.JobAdded);
            await Assert.That(listener.JobsChanged.Wait(_Timeout)).IsTrue();
            _WaitUntilBlocked(listener.SlotThread!);

            slot.Notify(NotifyFlags.ShuttingDown);
            slot.Cancel();
            slot.Join();

            lost += 1 - listener.ShuttingDownCount;
            slot.Dispose();
            listener.JobsChanged.Dispose();
        }

        await Assert.That(lost).IsEqualTo(0);
    }

    [Test]
    public async Task ListenerSlotThatDrainedAfterCancelEndsCancelledLikeOneThatWasInterrupted()
    {
        // The slot thread is inside a callback when the cancel and a new flag arrive; it leaves the loop at the loop
        // condition and drains. The job must end Cancelled in that case too, not Completed.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        using ManualResetEventSlim hold = new(false);
        DrainListener listener = new() { Hold = hold };
        ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });
        slot.Start();
        slot.Notify(NotifyFlags.JobAdded);
        await Assert.That(listener.JobsChanged.Wait(_Timeout)).IsTrue();

        slot.Notify(NotifyFlags.ShuttingDown);
        slot.Cancel();
        hold.Set();
        slot.Join();

        await Assert.That(listener.ShuttingDownCount).IsEqualTo(1);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
        await Assert.That(slot.Status).IsEqualTo(JobStatus.Cancelled);
        slot.Dispose();
        listener.JobsChanged.Dispose();
    }

    #endregion

    #region Use after dispose

    [Test]
    public async Task NotifyOnDisposedListenerSlotDoesNotThrow()
    {
        // A source thread may still hold a snapshot with a slot that was just unsubscribed and disposed.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        DrainListener listener = new();
        ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });
        slot.Dispose();

        Exception? thrown = null;
        try
        {
            slot.Notify(NotifyFlags.NewPackets);
            slot.Notify(NotifyFlags.NewPackets | NotifyFlags.JobAdded);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
        listener.JobsChanged.Dispose();
    }

    [Test]
    public async Task NotifyOnDisposedValueCacheSlotDoesNotThrow()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        DrainCacheListener listener = new();
        ValueCache writer = _CreateWriter(stack);
        ValueCacheSlot slot = new(
            new JobId(0),
            listener,
            session,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            writer,
            ValueCacheFillMode.NotifyOnly,
            static _ => throw new InvalidOperationException("No rebuild expected."),
            () => writer,
            static (out StackLease lease) =>
            {
                // NotifyOnly never parses, so it never asks for a lease.
                lease = default;
                return false;
            },
            static (_, _) => { });
        slot.Dispose();

        Exception? thrown = null;
        try
        {
            slot.Notify(NotifyFlags.NewPackets);
            slot.Notify(NotifyFlags.NewPackets | NotifyFlags.JobAdded);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
        listener.JobsChanged.Dispose();
    }

    [Test]
    public async Task CancelOnDisposedListenerSlotDoesNotThrow()
    {
        // Interleaving: Shutdown snapshots the slots, a concurrent TryUnsubscribe cancels, joins and disposes one of
        // them, then Shutdown cancels the snapshot entry.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        DrainListener listener = new();
        ListenerSlot slot = new(new JobId(0), listener, session, static (_, _) => { });
        slot.Dispose();

        Exception? thrown = null;
        try
        {
            slot.Cancel();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
        listener.JobsChanged.Dispose();
    }

    #endregion
}
