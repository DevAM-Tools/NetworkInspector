// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <c>TryVisitPackets</c>: the pull read for consumers that parse with observers. Covers the order and the exactly-once parse of a
/// range, the guards (empty range, range past the count, argument errors), the generation check before the first parse, the stop
/// by the visitor, and the one stack lease per call. No sleeps; no timing.
/// </summary>
internal sealed class SessionVisitPacketsTests
{
    #region Fixtures

    private static ParseOptions _TtlOptions(StackView view, RecordingFieldObserver observer)
    {
        FieldId ttl = view.Stack.GetFieldId("ip.ttl")!.Value; // Non-null: the standard stack defines it.
        ParseObserver[] observers = [new ParseObserver { Observer = observer, Watch = FieldWatch.Only(view.Stack, ttl) }];
        return new ParseOptions(FieldTreeMode.Skip, observers: observers);
    }

    /// <summary>Records the ids it receives; stops after <c>stopAfter</c> packets when that is positive.</summary>
    private sealed class CountingVisitor(int stopAfter = 0, Func<int>? leaseProbe = null) : IPacketVisitor
    {
        internal List<int> Ids { get; } = [];

        internal List<int> LeaseCounts { get; } = [];

        internal List<bool> HadFieldTree { get; } = [];

        public bool Visit(Packet packet)
        {
            Ids.Add(packet.Id.Value);
            HadFieldTree.Add(packet.HasFieldTree);
            if (leaseProbe is not null)
            {
                LeaseCounts.Add(leaseProbe());
            }

            return stopAfter == 0 || Ids.Count < stopAfter;
        }
    }

    #endregion

    #region Range

    [Test]
    public async Task EveryIdOfTheRangeIsParsedOnceInOrderAndObservedOncePerPacket()
    {
        using RestartRig rig = new(frameCount: 150);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(view, observer);
        CountingVisitor visitor = new();

        bool ok = rig.Session.TryVisitPackets(10, 140, in options, view, visitor, out int visited, out SessionFailure? failure);

        await Assert.That(ok).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(visited).IsEqualTo(130);
        await Assert.That(visitor.Ids).IsEquivalentTo(Enumerable.Range(10, 130));
        await Assert.That(visitor.Ids.SequenceEqual(Enumerable.Range(10, 130))).IsTrue();
        await Assert.That(observer.BeganPacket).IsEqualTo(130);
        await Assert.That(observer.Fields.Count).IsEqualTo(130);

        // The caller asked for no field tree, and the session honors it.
        await Assert.That(visitor.HadFieldTree.Any(static tree => tree)).IsFalse();
    }

    [Test]
    public async Task AnEmptyRangeVisitsNothingAndSucceeds()
    {
        using RestartRig rig = new(frameCount: 5);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(view, observer);
        CountingVisitor visitor = new();

        bool ok = rig.Session.TryVisitPackets(3, 3, in options, view, visitor, out int visited, out SessionFailure? failure);

        await Assert.That(ok).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(visited).IsEqualTo(0);
        await Assert.That(observer.BeganPacket).IsEqualTo(0);
    }

    [Test]
    public async Task ARangePastThePacketCountIsRefusedByNameOfRangeAndCountBeforeAnyParse()
    {
        using RestartRig rig = new(frameCount: 5);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(view, observer);
        CountingVisitor visitor = new();

        bool ok = rig.Session.TryVisitPackets(2, 9, in options, view, visitor, out int visited, out SessionFailure? failure);

        await Assert.That(ok).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.PacketNotFound);
        await Assert.That(failure.Value.Message).Contains("2 to 9");
        await Assert.That(failure.Value.Message).Contains("5 announced");
        await Assert.That(visited).IsEqualTo(0);
        await Assert.That(observer.BeganPacket).IsEqualTo(0);
    }

    [Test]
    public async Task ABadArgumentThrowsInsteadOfReturningAFailure()
    {
        using RestartRig rig = new(frameCount: 5);
        StackView view = rig.Session.CurrentStack;
        ParseOptions options = _TtlOptions(view, new());
        CountingVisitor visitor = new();

        await Assert.That(() => rig.Session.TryVisitPackets(-1, 2, in options, view, visitor, out _, out _))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => rig.Session.TryVisitPackets(3, 2, in options, view, visitor, out _, out _))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => rig.Session.TryVisitPackets(0, 2, in options, view, null!, out _, out _))
            .ThrowsExactly<ArgumentNullException>();
    }

    #endregion

    #region Generation

    [Test]
    public async Task OptionsBuiltForARetiredViewAreRefusedBeforeTheFirstParse()
    {
        using RestartRig rig = new(frameCount: 6);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions stale = _TtlOptions(view, observer);
        _ = rig.RestartOntoStandard();
        CountingVisitor visitor = new();

        bool ok = rig.Session.TryVisitPackets(0, 6, in stale, view, visitor, out int visited, out SessionFailure? failure);

        await Assert.That(ok).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.OptionsStackMismatch);
        await Assert.That(failure.Value.Message).Contains("generation 0");
        await Assert.That(failure.Value.Message).Contains("generation 1");
        await Assert.That(visited).IsEqualTo(0);
        await Assert.That(observer.BeganPacket).IsEqualTo(0);
        await Assert.That(visitor.Ids.Count).IsEqualTo(0);
    }

    #endregion

    #region Visitor and lease

    [Test]
    public async Task AVisitorReturningFalseStopsAfterThatPacketAndTheCallSucceeds()
    {
        using RestartRig rig = new(frameCount: 200);
        StackView view = rig.Session.CurrentStack;
        ParseOptions options = _TtlOptions(view, new());
        CountingVisitor visitor = new(stopAfter: 70);

        bool ok = rig.Session.TryVisitPackets(0, 200, in options, view, visitor, out int visited, out SessionFailure? failure);

        await Assert.That(ok).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(visited).IsEqualTo(70);
        await Assert.That(visitor.Ids.SequenceEqual(Enumerable.Range(0, 70))).IsTrue();
    }

    [Test]
    public async Task AWholeCallHoldsExactlyOneStackLeaseAndReleasesItAtTheEnd()
    {
        using RestartRig rig = new(frameCount: 200);
        StackView view = rig.Session.CurrentStack;
        ParseOptions options = _TtlOptions(view, new());
        CountingVisitor visitor = new(leaseProbe: () => rig.Session.StackHolder.LeaseCount);
        int before = rig.Session.StackHolder.LeaseCount;

        bool ok = rig.Session.TryVisitPackets(0, 200, in options, view, visitor, out _, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(before).IsEqualTo(0);
        await Assert.That(visitor.LeaseCounts.Count).IsEqualTo(200);
        await Assert.That(visitor.LeaseCounts.All(static count => count == 1)).IsTrue();
        await Assert.That(rig.Session.StackHolder.LeaseCount).IsEqualTo(0);
    }

    #endregion
}
