// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Tests for session infrastructure helpers and listener defaults.</summary>
internal sealed class InfrastructureTests
{
    [Test]
    public async Task SnapshotList_AddRemoveClearAndCount()
    {
        SnapshotList<string> list = new();

        await Assert.That(list.Count).IsEqualTo(0);

        list.Add("a");
        list.Add("b");

        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list.Current[0]).IsEqualTo("a");

        bool removed = list.Remove("a");
        await Assert.That(removed).IsTrue();
        await Assert.That(list.Count).IsEqualTo(1);

        bool missing = list.Remove("missing");
        await Assert.That(missing).IsFalse();

        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadWaitHelper_WaitUntil_BlocksUntilConditionMet()
    {
        int counter = 0;
        Task waiter = Task.Run(() => ThreadWaitHelper.WaitUntil(() => Interlocked.CompareExchange(ref counter, 0, 0) >= 2));
        Thread.Sleep(20);
        Interlocked.Increment(ref counter);
        Interlocked.Increment(ref counter);
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ThreadWaitHelper_WaitUntilWithTimeout_ReturnsFalseOnTimeout()
    {
        bool result = ThreadWaitHelper.WaitUntil(static () => false, TimeSpan.FromMilliseconds(20));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ThreadWaitHelper_WaitUntilWithTimeout_ReturnsTrueWhenConditionMet()
    {
        int flag = 0;
        Task setter = Task.Run(async () =>
        {
            await Task.Delay(10);
            Interlocked.Exchange(ref flag, 1);
        });

        bool result = ThreadWaitHelper.WaitUntil(() => Volatile.Read(ref flag) == 1, TimeSpan.FromSeconds(5));

        await setter;
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task SessionListener_DefaultMethods_AreCallable()
    {
        ISessionListener listener = new DefaultOnlyListener();

        listener.OnSourcesChanged(null!);
        listener.OnAllSourcesCompleted(null!);
        listener.OnJobsChanged(null!);
        listener.OnStackChanged(null!);
        listener.OnPhaseChanged(SessionPhase.Running);
        listener.OnShuttingDown();
        listener.OnUnsubscribed();

        await Assert.That(listener.UiName).IsEqualTo("DefaultOnly");
    }

    private sealed class DefaultOnlyListener : ISessionListener
    {
        public string UiName => "DefaultOnly";

        public void OnNewPackets(ISessionReader session, long fromIndex, long toIndexExclusive)
        {
        }
    }
}
