// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Tests for session infrastructure helpers and listener defaults.</summary>
internal sealed class InfrastructureTests
{
    [Test]
    public async Task SnapshotListAddAndRemove()
    {
        SnapshotList<string> list = new();
        int emptyLength = list.Current.Length;

        list.Add("a");
        list.Add("b");
        int afterAdd = list.Current.Length;
        string first = list.Current[0];
        bool removed = list.Remove("a");
        bool missing = list.Remove("missing");
        string[] remaining = list.CurrentSnapshot;

        await Assert.That(emptyLength).IsEqualTo(0);
        await Assert.That(afterAdd).IsEqualTo(2);
        await Assert.That(first).IsEqualTo("a");
        await Assert.That(removed).IsTrue();
        await Assert.That(missing).IsFalse();
        await Assert.That(remaining).IsEquivalentTo(["b"]);
    }

    /// <summary>Concurrent writers must not lose an element: every Add goes through the CAS retry loop.</summary>
    [Test]
    public async Task SnapshotListConcurrentAddsKeepEveryItem()
    {
        SnapshotList<int> list = new();
        const int threadCount = 8;
        const int perThread = 250;
        Thread[] threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int offset = t * perThread;
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    list.Add(offset + i);
                }
            })
            {
                Name = "snapshot-writer",
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

        int[] items = list.CurrentSnapshot;

        await Assert.That(items.Length).IsEqualTo(threadCount * perThread);
        await Assert.That(items.Distinct().Count()).IsEqualTo(threadCount * perThread);
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

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }
    }
}
