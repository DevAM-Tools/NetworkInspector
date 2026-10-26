// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// The <c>Get*</c> list members hand out views of the session's copy-on-write snapshot arrays. A view must not expose the
/// array itself (a cast to <c>T[]</c> would let a caller overwrite what every other reader sees), and an unchanged
/// snapshot must give the same view back without a new allocation per call.
/// </summary>
internal sealed class SessionSnapshotViewTests
{
    #region Helpers

    /// <summary>True when a write through <c>IList&lt;T&gt;</c> is refused, which an array view would accept.</summary>
    private static bool _RefusesWritesThroughIList<T>(IReadOnlyList<T> view)
        where T : class
    {
        if (view is not IList<T> list || list.Count == 0)
        {
            return false;
        }

        try
        {
            list[0] = list[0];
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private sealed class RowsListener : IValueCacheListener
    {
        public string UiName => "rows";

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
        }
    }

    #endregion

    [Test]
    public async Task GetJobsReturnsViewThatIsNotTheSnapshotArray()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddJob("UserJob", "quick", static _ => { }, out _, out _);

        IReadOnlyList<JobInfo> jobs = session.GetJobs();

        await Assert.That(jobs).IsNotAssignableTo<JobInfo[]>();
        await Assert.That(_RefusesWritesThroughIList(jobs)).IsTrue();
        await Assert.That(jobs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetListenersReturnsViewThatIsNotTheSnapshotArray()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddListener(new TestSessionListener(), out _, out _);

        IReadOnlyList<ListenerInfo> listeners = session.GetListeners();

        await Assert.That(listeners).IsNotAssignableTo<ListenerInfo[]>();
        await Assert.That(_RefusesWritesThroughIList(listeners)).IsTrue();
        await Assert.That(listeners.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetFrameSourcesReturnsViewThatIsNotTheSnapshotArray()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddFrameSource(TestFrameSource.WithUdpFrames(1), out _, out _);

        IReadOnlyList<FrameSourceInfo> sources = session.GetFrameSources();

        await Assert.That(sources).IsNotAssignableTo<FrameSourceInfo[]>();
        await Assert.That(_RefusesWritesThroughIList(sources)).IsTrue();
        await Assert.That(sources.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetValueCachesReturnsViewThatIsNotTheSnapshotArray()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddValueCache(
            new RowsListener(),
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out _,
            out _);

        IReadOnlyList<ValueCacheInfo> caches = session.GetValueCaches();

        await Assert.That(caches).IsNotAssignableTo<ValueCacheInfo[]>();
        await Assert.That(_RefusesWritesThroughIList(caches)).IsTrue();
        await Assert.That(caches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnchangedSnapshotGivesTheSameViewAndAChangeGivesAFreshOne()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddJob("First", "quick", static _ => { }, out _, out _);

        IReadOnlyList<JobInfo> before = session.GetJobs();
        IReadOnlyList<JobInfo> beforeAgain = session.GetJobs();
        session.TryAddJob("Second", "quick", static _ => { }, out _, out _);
        IReadOnlyList<JobInfo> after = session.GetJobs();

        await Assert.That(beforeAgain).IsSameReferenceAs(before);
        await Assert.That(after).IsNotSameReferenceAs(before);
        await Assert.That(before.Count).IsEqualTo(1);
        await Assert.That(after.Count).IsEqualTo(2);
    }
}
