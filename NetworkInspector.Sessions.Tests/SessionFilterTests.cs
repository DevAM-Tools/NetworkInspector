// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Covers per-listener filter registration, the listener-bound pull API
/// (<see cref="Session.TryReadPackets"/>) and filter re-binding across
/// <see cref="Session.Restart"/>.
/// </summary>
internal sealed class SessionFilterTests
{
    #region Registration

    [Test]
    public async Task TryAddListener_WithFilter_StoresFilterOnSlot()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        TestSessionListener listener = new();
        PacketFilter filter = SessionFixture.CompileOrThrow(stack, "udp.dstport == 53");

        bool added = session.TryAddListener(listener, filter, out ListenerInfo? info);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(_ListenerFilters(session)[0]).IsSameReferenceAs(filter);
    }

    [Test]
    public async Task TryAddListener_WithoutFilter_LeavesSlotUnfiltered()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        TestSessionListener listener = new();

        bool added = session.TryAddListener(listener, out ListenerInfo? info);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(_ListenerFilters(session)[0]).IsNull();
    }

    [Test]
    public async Task TryAddListener_WithNullFilter_LeavesSlotUnfiltered()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(new TestSessionListener(), filter: null, out ListenerInfo? info);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(_ListenerFilters(session)[0]).IsNull();
    }

    [Test]
    public async Task TryAddListener_WithExpression_CompilesAgainstSessionStack()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(
            new TestSessionListener(), "udp.dstport == 53", out ListenerInfo? info, out FilterError? filterFailure);

        await Assert.That(added).IsTrue();
        await Assert.That(filterFailure).IsNull();
        await Assert.That(info).IsNotNull();
        await Assert.That(_ListenerFilters(session)[0]!.Expression).IsEqualTo("udp.dstport == 53");
    }

    [Test]
    public async Task TryAddListener_WithBlankExpression_UsesAlwaysMatch()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(new TestSessionListener(), "   ", out ListenerInfo? _, out FilterError? filterFailure);

        await Assert.That(added).IsTrue();
        await Assert.That(filterFailure).IsNull();
        await Assert.That(_ListenerFilters(session)[0]!.IsAlwaysMatch).IsTrue();
    }

    [Test]
    public async Task TryAddListener_WithNullExpression_UsesAlwaysMatch()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(
            new TestSessionListener(), filterExpression: null, out ListenerInfo? _, out FilterError? filterFailure);

        await Assert.That(added).IsTrue();
        await Assert.That(filterFailure).IsNull();
        await Assert.That(_ListenerFilters(session)[0]!.IsAlwaysMatch).IsTrue();
    }

    [Test]
    public async Task TryAddListener_WithUnknownField_FailsWithoutRegistering()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(
            new TestSessionListener(), "nosuch.field == 1", out ListenerInfo? info, out FilterError? filterFailure);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(filterFailure).IsNotNull();
        await Assert.That(session.GetListeners().Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryAddListener_WithExpressionAfterShutdown_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryStart();
        session.WaitForCompletion();
        session.Shutdown();

        bool added = session.TryAddListener(
            new TestSessionListener(), "udp", out ListenerInfo? info, out FilterError? filterFailure);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(filterFailure).IsNull();
    }

    [Test]
    public async Task TryAddListener_WithExpressionAndNullListener_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(() => session.TryAddListener(null!, "udp", out _, out _)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryAddListener_WithFilterAndNullListener_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(() => session.TryAddListener(null!, PacketFilter.AlwaysMatch, out _))
            .Throws<ArgumentNullException>();
    }

    #endregion

    #region ReadPackets with PacketRef

    [Test]
    public async Task ReadPackets_PacketRefOverload_ReturnsContiguousIds()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(4);

        PacketRef[] buffer = new PacketRef[4];
        int read = fixture.Session.ReadPackets(0, buffer, out PacketIdLayout idLayout);

        await Assert.That(read).IsEqualTo(4);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
        await Assert.That(buffer[2].Id.Value).IsEqualTo(2);
        await Assert.That(buffer[2].Packet).IsNotNull();
    }

    [Test]
    public async Task ReadPackets_PacketRefOverload_BeyondStoredRange_ReportsNullPackets()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2);

        PacketRef[] buffer = new PacketRef[4];
        int read = fixture.Session.ReadPackets(0, buffer, out _);

        await Assert.That(read).IsEqualTo(4);
        await Assert.That(buffer[3].Packet).IsNull();
    }

    [Test]
    public async Task ReadPackets_PacketRefOverload_WhenQueriesDisabled_ReturnsZero()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2);
        fixture.Session.Shutdown();

        PacketRef[] buffer = new PacketRef[2];
        int read = fixture.Session.ReadPackets(0, buffer, out _);

        await Assert.That(read).IsEqualTo(0);
    }

    #endregion

    #region TryReadPackets

    [Test]
    public async Task TryReadPackets_AllMode_IgnoresFilter()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(4, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId,
            0,
            buffer,
            PacketReadMode.All,
            out int count,
            out PacketIdLayout idLayout,
            out FilterError? failure);

        await Assert.That(read).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_ReturnsOnlyMatchesAsGapped()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(6, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[6];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId,
            0,
            buffer,
            PacketReadMode.Matching,
            out int count,
            out PacketIdLayout idLayout,
            out FilterError? failure);

        await Assert.That(read).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Gapped);
        await Assert.That(buffer[0].Id.Value).IsEqualTo(0);
        await Assert.That(buffer[1].Id.Value).IsEqualTo(2);
        await Assert.That(buffer[2].Id.Value).IsEqualTo(4);
        await Assert.That(buffer[1].Packet).IsNotNull();
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_AllPacketsMatch_ReportsContiguous()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(4, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_StopsAtDestinationCapacity()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(6, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(buffer[1].Id.Value).IsEqualTo(2);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_NegativeStartId_StartsAtZero()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(3, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[3];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, -5, buffer, PacketReadMode.Matching, out int count, out _, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(buffer[0].Id.Value).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_PrunedByCandidateBitmap()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingProtocols(4, "tcp");

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Gapped);
        await Assert.That(buffer[0].Id.Value).IsEqualTo(1);
        await Assert.That(buffer[1].Id.Value).IsEqualTo(3);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_NoFilter_BehavesLikeAll()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(4, filterExpression: null);

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_AlwaysMatchFilter_BehavesLikeAll()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(4, "");

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_StartBeyondLastPacket_ReturnsNothing()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2, "udp.dstport == 53");

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 10, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPackets_UnknownListener_Throws()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(1);
        PacketRef[] buffer = new PacketRef[1];

        try
        {
            _ = fixture.Session.TryReadPackets(
                new ListenerId(4242), 0, buffer, PacketReadMode.All, out _, out _, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException exception)
        {
            await Assert.That(exception.Code).IsEqualTo(SessionErrorCode.ListenerNotFound);
        }
    }

    [Test]
    public async Task TryReadPackets_WhenQueriesDisabled_ReturnsEmptySuccess()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2, "udp.dstport == 53");
        ListenerId listenerId = fixture.ListenerId;
        fixture.Session.Shutdown();

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            listenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_PoisonedFilter_Fails()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPortsAndFilter(2, _CompilePoisoned);

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure);

        await Assert.That(read).IsFalse();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.OutOfOrder);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_EvaluationFailureFailsTheRead()
    {
        // A stateful filter that already saw a later packet rejects the replay of an earlier one.
        // The listener must be told, rather than silently receiving an empty batch.
        using SessionFixture fixture = SessionFixture.WithDnsPorts(4, "flank(ip.ttl, changed, within: 1s)");
        PacketRef[] buffer = new PacketRef[4];

        _ = fixture.Session.TryReadPackets(
            fixture.ListenerId, 2, buffer, PacketReadMode.Matching, out _, out _, out _);
        bool replay = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure);

        await Assert.That(replay).IsFalse();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.OutOfOrder);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_ArmedFlank_CrossIntermediate()
    {
        using SessionFixture fixture = SessionFixture.WithTtlSequence(
            [1, 3, 3, 2],
            "flank(ip.ttl, from: 1, to: 2, within: 10packets)");

        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure);

        await Assert.That(read).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(buffer[0].Id.Value).IsEqualTo(3);
    }

    #endregion

    #region Restart

    [Test]
    public async Task Restart_DerivesNewFilterInstance()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(3, "udp.dstport == 53");
        IFilter original = _ListenerFilters(fixture.Session)[0]!;

        fixture.Restart();

        IFilter? rebound = _ListenerFilters(fixture.Session)[0];
        await Assert.That(rebound).IsNotNull();
        await Assert.That(rebound).IsNotSameReferenceAs(original);
        await Assert.That(rebound!.Expression).IsEqualTo(original.Expression);
    }

    [Test]
    public async Task Restart_ReboundFilterStillMatches()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(6, "udp.dstport == 53");
        fixture.Restart();

        PacketRef[] buffer = new PacketRef[6];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task Restart_ClearsPoisonOfDerivedFilter()
    {
        PacketFilter? poisoned = null;
        using SessionFixture fixture = SessionFixture.WithDnsPortsAndFilter(
            3,
            stack =>
            {
                poisoned = _CompilePoisoned(stack);
                return poisoned;
            });

        fixture.Restart();

        await Assert.That(_ListenerFilters(fixture.Session)[0]!.IsPoisoned).IsFalse();
        await Assert.That(poisoned!.IsPoisoned).IsTrue();
    }

    [Test]
    public async Task Restart_ListenerWithoutFilter_StaysUnfiltered()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2);

        fixture.Restart();

        await Assert.That(_ListenerFilters(fixture.Session)[0]).IsNull();
    }

    [Test]
    public async Task Restart_ToStackWithoutTheField_FailsMatchingReads()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(3, "udp.dstport == 53");

        fixture.RestartWithEthernetOnlyStack();

        PacketRef[] buffer = new PacketRef[3];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure);

        await Assert.That(read).IsFalse();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(failure).IsNotNull();
        await Assert.That(_ListenerFilters(fixture.Session)[0]).IsNull();
    }

    [Test]
    public async Task Restart_ToStackWithoutTheField_LeavesAllReadsWorking()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(3, "udp.dstport == 53");

        fixture.RestartWithEthernetOnlyStack();

        PacketRef[] buffer = new PacketRef[3];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.All, out int count, out _, out FilterError? failure);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(failure).IsNull();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Compiles a stateful filter and poisons it by evaluating a high packet id before a low one,
    /// which is the documented out-of-order failure.
    /// </summary>
    private static PacketFilter _CompilePoisoned(Stack stack)
    {
        PacketFilter filter = SessionFixture.CompileOrThrow(stack, "flank(ip.ttl, changed, within: 1s)");
        Packet first = TestHarness.ParseStandalone(stack, TestHarness.BuildUdpFrame(53, 64), 0);
        Packet second = TestHarness.ParseStandalone(stack, TestHarness.BuildUdpFrame(53, 63), 1);
        _ = filter.TryIsMatch(second, out _, out _);
        _ = filter.TryIsMatch(first, out _, out _);
        return filter;
    }

    /// <summary>
    /// Reads the filter of every registered listener slot. The slot registry is private session
    /// state; reading it directly is the only way to prove that <c>Restart</c> installs a new
    /// filter instance rather than merely producing the same verdicts.
    /// </summary>
    private static IFilter?[] _ListenerFilters(Session session)
    {
        System.Reflection.FieldInfo field = typeof(Session)
            .GetField("_ListenerSlots", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Session no longer has a _ListenerSlots field.");

        SnapshotList<ListenerSlot> slots = (SnapshotList<ListenerSlot>)field.GetValue(session)!;
        ListenerSlot[] snapshot = slots.CurrentSnapshot;

        IFilter?[] filters = new IFilter?[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            filters[i] = snapshot[i].Filter;
        }

        return filters;
    }

    #endregion
}
