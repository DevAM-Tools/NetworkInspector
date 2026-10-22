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

        bool added = session.TryAddListener(listener, filter, out ListenerInfo? info, out _);

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

        bool added = session.TryAddListener(listener, out ListenerInfo? info, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(_ListenerFilters(session)[0]).IsNull();
    }

    [Test]
    public async Task TryAddListener_WithNullFilter_LeavesSlotUnfiltered()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(new TestSessionListener(), filter: null, out ListenerInfo? info, out _);

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
            new TestSessionListener(), "udp.dstport == 53", out ListenerInfo? info, out FilterError? filterFailure, out _);

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

        bool added = session.TryAddListener(new TestSessionListener(), "   ", out ListenerInfo? _, out FilterError? filterFailure, out _);

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
            new TestSessionListener(), filterExpression: null, out ListenerInfo? _, out FilterError? filterFailure, out _);

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
            new TestSessionListener(), "nosuch.field == 1", out ListenerInfo? info, out FilterError? filterFailure, out _);

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
        session.TryStart(out _);
        session.WaitForCompletion();
        session.Shutdown();

        bool added = session.TryAddListener(
            new TestSessionListener(), "udp", out ListenerInfo? info, out FilterError? filterFailure, out _);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(filterFailure).IsNull();
    }

    [Test]
    public async Task TryAddListener_WithExpressionAndNullListener_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(() => session.TryAddListener(null!, "udp", out _, out _, out _)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryAddListener_WithFilterAndNullListener_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(() => session.TryAddListener(null!, PacketFilter.AlwaysMatch, out _, out _))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// A filter compiled against another stack carries that stack's field ids. The session must install a
    /// copy re-bound to its own stack, leave the caller's instance untouched, and match correctly.
    /// </summary>
    [Test]
    public async Task TryAddListenerFilterFromOtherStackIsRebound()
    {
        using Stack other = TestHarness.CreateStack();
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(4, filterExpression: null);
        PacketFilter foreign = SessionFixture.CompileOrThrow(other, "udp.dstport == 53");

        bool added = fixture.Session.TryAddListener(new TestSessionListener(), foreign, out ListenerInfo? info, out _);
        PacketFilter? installed = _ListenerFilters(fixture.Session)[1];
        PacketRef[] buffer = new PacketRef[4];
        bool read = fixture.Session.TryReadPackets(
            info!.Id, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(installed).IsNotSameReferenceAs(foreign);
        await Assert.That(installed!.Stack).IsSameReferenceAs(fixture.Session.Stack);
        await Assert.That(foreign.Stack).IsSameReferenceAs(other);
        await Assert.That(read).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(buffer[0].Id.Value).IsEqualTo(0);
        await Assert.That(buffer[1].Id.Value).IsEqualTo(2);
    }

    /// <summary>
    /// The retired-stack case: a filter compiled before a Restart is handed in afterwards. It must be
    /// re-bound to the stack the session uses now, not evaluated against the disposed one.
    /// </summary>
    [Test]
    public async Task TryAddListenerFilterFromBeforeRestartIsRebound()
    {
        using SessionFixture fixture = SessionFixture.WithAlternatingPorts(6, filterExpression: null);
        Stack before = fixture.Session.Stack;
        PacketFilter stale = SessionFixture.CompileOrThrow(before, "udp.dstport == 53");
        fixture.Restart();

        bool added = fixture.Session.TryAddListener(new TestSessionListener(), stale, out ListenerInfo? info, out _);
        PacketFilter? installed = _ListenerFilters(fixture.Session)[1];
        PacketRef[] buffer = new PacketRef[6];
        bool read = fixture.Session.TryReadPackets(
            info!.Id, 0, buffer, PacketReadMode.Matching, out int count, out _, out _, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(fixture.Session.Stack).IsNotSameReferenceAs(before);
        await Assert.That(installed!.Stack).IsSameReferenceAs(fixture.Session.Stack);
        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(3);
    }

    /// <summary>
    /// A foreign filter that names a field the session stack does not define is a caller error: the call
    /// throws and registers nothing, instead of installing a filter that compares the wrong field ids.
    /// </summary>
    [Test]
    public async Task TryAddListenerFilterThatCannotBeReboundThrowsAndRegistersNothing()
    {
        using Stack full = TestHarness.CreateStack();
        using Stack ethernetOnly = SessionFixture.BuildEthernetOnlyStack(new());
        using Session session = new(ethernetOnly);
        PacketFilter foreign = SessionFixture.CompileOrThrow(full, "udp.dstport == 53");

        ArgumentException? thrown = null;
        try
        {
            _ = session.TryAddListener(new TestSessionListener(), foreign, out _, out _);
        }
        catch (ArgumentException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.ParamName).IsEqualTo("filter");
        await Assert.That(session.GetListeners().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
    }

    /// <summary>The always-match singleton has no stack and is installed as is on any session.</summary>
    [Test]
    public async Task TryAddListenerAlwaysMatchIsInstalledAsIs()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        bool added = session.TryAddListener(new TestSessionListener(), PacketFilter.AlwaysMatch, out _, out _);

        await Assert.That(added).IsTrue();
        await Assert.That(_ListenerFilters(session)[0]).IsSameReferenceAs(PacketFilter.AlwaysMatch);
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
    public async Task ReadPackets_PacketRefOverload_BeyondPacketCount_Stops()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2);

        PacketRef[] buffer = new PacketRef[4];
        int read = fixture.Session.ReadPackets(0, buffer, out _);

        await Assert.That(read).IsEqualTo(2);
        await Assert.That(buffer[1].Packet).IsNotNull();
        await Assert.That(buffer[2].Packet).IsNull();
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
            out FilterError? failure, out _);

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
            out FilterError? failure, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out _, out _);

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
            fixture.ListenerId, -5, buffer, PacketReadMode.Matching, out int count, out _, out _, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _, out _);

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
            fixture.ListenerId, 10, buffer, PacketReadMode.Matching, out int count, out PacketIdLayout idLayout, out _, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(idLayout).IsEqualTo(PacketIdLayout.Contiguous);
    }

    [Test]
    public async Task TryReadPacketsUnknownListenerReturnsListenerNotFound()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(1);
        PacketRef[] buffer = new PacketRef[1];

        bool read = fixture.Session.TryReadPackets(
            new ListenerId(4242), 0, buffer, PacketReadMode.All, out _, out _, out FilterError? failure, out SessionFailure? sessionFailure);

        await Assert.That(read).IsFalse();
        await Assert.That(failure).IsNull();
        await Assert.That(sessionFailure).IsNotNull();
        await Assert.That(sessionFailure.Value.Code).IsEqualTo(SessionErrorCode.ListenerNotFound);
    }

    [Test]
    public async Task TryReadPacketsWhenQueriesDisabledReturnsQueriesDisabled()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(2, "udp.dstport == 53");
        ListenerId listenerId = fixture.ListenerId;
        fixture.Session.Shutdown();

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            listenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out SessionFailure? sessionFailure);

        await Assert.That(read).IsFalse();
        await Assert.That(count).IsEqualTo(0);
        await Assert.That(failure).IsNull();
        await Assert.That(sessionFailure).IsNotNull();
        await Assert.That(sessionFailure.Value.Code).IsEqualTo(SessionErrorCode.QueriesDisabled);
    }

    [Test]
    public async Task TryReadPackets_MatchingMode_PoisonedFilter_Fails()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPortsAndFilter(2, _CompilePoisoned);

        PacketRef[] buffer = new PacketRef[2];
        bool read = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out _);

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
            fixture.ListenerId, 2, buffer, PacketReadMode.Matching, out _, out _, out _, out _);
        bool replay = fixture.Session.TryReadPackets(
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out _);

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
        PacketFilter original = _ListenerFilters(fixture.Session)[0]!;

        fixture.Restart();

        PacketFilter? rebound = _ListenerFilters(fixture.Session)[0];
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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out _, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.Matching, out int count, out _, out FilterError? failure, out _);

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
            fixture.ListenerId, 0, buffer, PacketReadMode.All, out int count, out _, out FilterError? failure, out _);

        await Assert.That(read).IsTrue();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task MatchingReadsAreSerializedPerListener()
    {
        using SessionFixture fixture = SessionFixture.WithDnsPorts(4, "udp.dstport == 53");
        PacketRef[] left = new PacketRef[4];
        PacketRef[] right = new PacketRef[4];
        Task<bool> first = Task.Run(() =>
        {
            bool ok = true;
            for (int i = 0; i < 50; i++)
            {
                ok &= fixture.Session.TryReadPackets(
                    fixture.ListenerId,
                    0,
                    left,
                    PacketReadMode.Matching,
                    out _,
                    out _,
                    out _,
                    out _);
            }

            return ok;
        });
        Task<bool> second = Task.Run(() =>
        {
            bool ok = true;
            for (int i = 0; i < 50; i++)
            {
                ok &= fixture.Session.TryReadPackets(
                    fixture.ListenerId,
                    0,
                    right,
                    PacketReadMode.Matching,
                    out _,
                    out _,
                    out _,
                    out _);
            }

            return ok;
        });

        await Assert.That(await first).IsTrue();
        await Assert.That(await second).IsTrue();
    }

    #endregion

    #region Matching pre-check

    /// <summary>2 000 mixed frames: every fourth TCP (alternating ports 80/443), the rest UDP with patterned ports and TTLs.</summary>
    private static byte[][] _PrecheckFrames(int count)
    {
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            frames[i] = i % 4 == 0
                ? TestHarness.BuildTcpFrame(i % 8 == 0 ? (ushort)80 : (ushort)443)
                : TestHarness.BuildUdpFrame(i % 9 == 0 ? (ushort)53 : (ushort)(1_000 + (i % 400)), (byte)(20 + (i % 50)));
        }

        return frames;
    }

    /// <summary>Pages a matching read through the whole capture in buffers of <paramref name="bufferLength"/>.</summary>
    private static List<int> _ReadAllMatching(Session session, ListenerId listenerId, int bufferLength)
    {
        PacketRef[] buffer = new PacketRef[bufferLength];
        List<int> returned = [];
        int start = 0;
        int limit = session.PacketCount;
        while (start < limit)
        {
            if (!session.TryReadPackets(listenerId, start, buffer, PacketReadMode.Matching, out int count, out _, out _, out _))
            {
                throw new InvalidOperationException("The matching read failed.");
            }

            for (int i = 0; i < count; i++)
            {
                returned.Add(buffer[i].Id.Value);
            }

            start = count == bufferLength ? buffer[count - 1].Id.Value + 1 : limit;
        }

        return returned;
    }

    /// <summary>Ids for which an independent filter instance evaluates true on standalone parses.</summary>
    private static List<int> _ReferenceMatches(Stack stack, byte[][] frames, string expression)
    {
        PacketFilter reference = SessionFixture.CompileOrThrow(stack, expression);
        List<int> expected = [];
        for (int i = 0; i < frames.Length; i++)
        {
            _ = reference.TryIsMatch(TestHarness.ParseStandalone(stack, frames[i], i), out bool matched, out _);
            if (matched)
            {
                expected.Add(i);
            }
        }

        return expected;
    }

    [Test]
    [Arguments("udp.dstport >= 1200")]
    [Arguments("udp.port in {53, 1024}")]
    [Arguments("tcp.dstport == 443 || udp.dstport == 1007")]
    [Arguments("ip.ttl < 40 && !tcp")]
    [Arguments("udp.dstport != 53")]
    [Arguments("!(udp.dstport == 53)")]
    [Arguments("!(tcp.dstport == 443 || udp.dstport == 53)")]
    [Arguments("tcp.dstport != 80 && udp.dstport != 53")]
    [Arguments("!(udp.dstport == 53) && ip.ttl > 30")]
    public async Task PrecheckedMatchingReadEqualsTheFieldTreeFilter(string expression)
    {
        const int frameCount = 2_000;
        byte[][] frames = _PrecheckFrames(frameCount);
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = new(frames);
        using Session session = new(stack, new SessionOptions { IndexPackets = true });
        PacketFilter listenerFilter = SessionFixture.CompileOrThrow(stack, expression);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(new TestSessionListener(), listenerFilter, out ListenerInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        List<int> returned = _ReadAllMatching(session, info!.Id, 64);
        List<int> expected = _ReferenceMatches(stack, frames, expression);

        // A third instance evaluates every id the presence index does not prune, so its cache is the full bookkeeping.
        PacketFilter direct = SessionFixture.CompileOrThrow(stack, expression);
        PacketIndexReaderView index = session.PacketIndex!.Value;
        for (int i = 0; i < frameCount; i++)
        {
            if (direct.TryIsPresenceCandidate(index, (uint)i, out bool candidate) && !candidate)
            {
                continue;
            }

            _ = direct.TryIsMatch(TestHarness.ParseStandalone(stack, frames[i], i), out _, out _);
        }

        // The slot filter is the instance the session evaluates (the caller's, or a re-bound copy of it).
        PacketFilter slotFilter = _ListenerSlots(session).Single().Filter!;
        long evaluated = slotFilter.EvaluatedCount;
        long matchedCount = slotFilter.MatchedPackets.Cardinality;
        session.Shutdown();

        await Assert.That(returned).IsEquivalentTo(expected);
        await Assert.That(evaluated).IsEqualTo(direct.EvaluatedCount);
        await Assert.That(matchedCount).IsEqualTo(direct.MatchedPackets.Cardinality);
    }

    [Test]
    public async Task MatchAllFilterOverManyPacketsReturnsEveryId()
    {
        // "udp" matches every packet: the adaptive switch turns the pre-check off, verdicts must not change.
        const int frameCount = 10_000;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        using Session session = new(stack, new SessionOptions { IndexPackets = true });
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(new TestSessionListener(), SessionFixture.CompileOrThrow(stack, "udp.dstport != 1"), out ListenerInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        List<int> returned = _ReadAllMatching(session, info!.Id, 1_000);
        session.Shutdown();

        await Assert.That(returned.Count).IsEqualTo(frameCount);
        await Assert.That(returned.SequenceEqual(Enumerable.Range(0, frameCount))).IsTrue();
    }

    [Test]
    public async Task PrecheckFollowsTheDerivedFilterAfterRestart()
    {
        const int frameCount = 500;
        const string expression = "udp.dstport >= 1200";
        byte[][] frames = _PrecheckFrames(frameCount);
        using TestFrameSource source = new(frames);
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack, new SessionOptions { IndexPackets = true });
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(new TestSessionListener(), SessionFixture.CompileOrThrow(stack, expression), out ListenerInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        List<int> before = _ReadAllMatching(session, info!.Id, 64);
        ListenerSlot slot = _ListenerSlots(session).Single();
        PacketFilter? ownerBefore = slot.PrecheckOwner;
        session.Restart(registry => TestHarness.CreateStack(registry));
        List<int> after = _ReadAllMatching(session, info.Id, 64);
        PacketFilter? ownerAfter = slot.PrecheckOwner;
        PacketFilter? filterAfter = slot.Filter;
        bool hasPrecheck = slot.Precheck is not null;
        session.Shutdown();

        await Assert.That(before).IsEquivalentTo(_ReferenceMatches(stack, frames, expression));
        await Assert.That(after).IsEquivalentTo(before);
        await Assert.That(ownerBefore).IsNotNull();
        await Assert.That(ReferenceEquals(ownerAfter, filterAfter)).IsTrue();
        await Assert.That(ReferenceEquals(ownerAfter, ownerBefore)).IsFalse();
        await Assert.That(hasPrecheck).IsTrue();
    }

    [Test]
    public async Task SecondMatchingReadRereadsOnlyTheMatches()
    {
        const int frameCount = 400;
        byte[][] frames = _PrecheckFrames(frameCount);
        using CountingRandomAccessSource source = new(new TestFrameSource(frames));
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack, new SessionOptions { IndexPackets = false });
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(new TestSessionListener(), SessionFixture.CompileOrThrow(stack, "udp.dstport == 53"), out ListenerInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        List<int> first = _ReadAllMatching(session, info!.Id, frameCount);
        int afterFirst = source.FrameByIdCalls;
        List<int> second = _ReadAllMatching(session, info.Id, frameCount);
        int secondReads = source.FrameByIdCalls - afterFirst;
        session.Shutdown();

        // Cached non-matches need no re-read; cached matches are re-parsed once each to be returned.
        await Assert.That(second).IsEquivalentTo(first);
        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(secondReads).IsEqualTo(first.Count);
    }

    [Test]
    public async Task MatchingReadReadsEachFrameOnce()
    {
        const int frameCount = 400;
        byte[][] frames = _PrecheckFrames(frameCount);
        using CountingRandomAccessSource source = new(new TestFrameSource(frames));
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack, new SessionOptions { IndexPackets = false });
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(new TestSessionListener(), SessionFixture.CompileOrThrow(stack, "udp.dstport == 53"), out ListenerInfo? info, out _);
        session.TryStart(out _);
        session.WaitForCompletion();

        int before = source.FrameByIdCalls;
        List<int> matches = _ReadAllMatching(session, info!.Id, frameCount);
        int reads = source.FrameByIdCalls - before;
        session.Shutdown();

        // One source read per id: a pre-check survivor is re-parsed from the frame the pre-check already read.
        await Assert.That(matches.Count).IsGreaterThan(0);
        await Assert.That(reads).IsEqualTo(frameCount);
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
    private static PacketFilter?[] _ListenerFilters(Session session)
    {
        System.Reflection.FieldInfo field = typeof(Session)
            .GetField("_ListenerSlots", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Session no longer has a _ListenerSlots field.");

        SnapshotList<ListenerSlot> slots = (SnapshotList<ListenerSlot>)field.GetValue(session)!;
        ListenerSlot[] snapshot = slots.CurrentSnapshot;

        PacketFilter?[] filters = new PacketFilter?[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            filters[i] = snapshot[i].Filter;
        }

        return filters;
    }

    /// <summary>The registered listener slots (private session state; the pre-check owner is not observable otherwise).</summary>
    private static ListenerSlot[] _ListenerSlots(Session session)
    {
        System.Reflection.FieldInfo field = typeof(Session)
            .GetField("_ListenerSlots", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Session no longer has a _ListenerSlots field.");
        return ((SnapshotList<ListenerSlot>)field.GetValue(session)!).CurrentSnapshot;
    }

    #endregion
}
