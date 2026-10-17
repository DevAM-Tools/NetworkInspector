// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Compiles and runs the samples of <c>SESSION_GUIDE.md</c> (sections 2, 8.1, 8.2 and 8.4), so the guide
/// cannot drift from the API. A test source replaces the capture file of the quick start.
/// </summary>
internal sealed class SessionGuideSamplesTests
{
    #region Section 2 — quick start

    [Test]
    public async Task QuickStartCountsEveryPacket()
    {
        const int frameCount = 12;
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        CountingListener listener = new();

        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        session.TryAddFrameSource(source, out _, out _);
        bool added = session.TryAddListener(listener, out ListenerInfo? info, out SessionFailure? failure);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.Seen >= frameCount);
        session.Shutdown();

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(failure).IsNull();
        await Assert.That(listener.Seen).IsEqualTo(frameCount);
    }

    #endregion

    #region Section 8.1 — packet list

    [Test]
    public async Task PacketListListenerBuildsOneRowPerPacketAndRebuildsAfterRestart()
    {
        const int frameCount = 300;
        using Stack stack = TestHarness.CreateStack();
        PacketListListener listener = new();
        using Session session = new(stack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);
        session.TryAddFrameSource(source, out _, out _);
        session.TryAddListener(listener, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => listener.RowCount >= frameCount);
        int before = listener.RowCount;

        session.Restart(registry => TestHarness.CreateStack(registry));
        WaitHelper.WaitUntil(() => listener.StackChanges >= 1 && listener.RowCount >= frameCount);
        int after = listener.RowCount;
        session.Shutdown();

        await Assert.That(before).IsEqualTo(frameCount);
        await Assert.That(after).IsEqualTo(frameCount);
        await Assert.That(listener.FirstRowId).IsEqualTo(0);
    }

    #endregion

    #region Section 8.2 — filtered list registration

    [Test]
    public async Task FilteredListRegistrationShapesCompileAndRegister()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        PacketFilter myFilter = SessionFixture.CompileOrThrow(stack, "udp");

        bool byExpression = session.TryAddListener(
            new CountingListener(), "tcp.port == 443", out ListenerInfo? info, out FilterError? filterFailure, out SessionFailure? failure);
        bool byFilter = session.TryAddListener(new CountingListener(), myFilter, out info, out failure);
        bool unfiltered = session.TryAddListener(new CountingListener(), out info, out failure);

        await Assert.That(byExpression).IsTrue();
        await Assert.That(filterFailure).IsNull();
        await Assert.That(byFilter).IsTrue();
        await Assert.That(unfiltered).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(session.GetListeners().Count).IsEqualTo(3);
    }

    #endregion

    #region Section 8.4 — one packet with a field observer

    [Test]
    public async Task OnePacketWithFieldObserverSeesTheWatchedField()
    {
        using Stack sessionStack = TestHarness.CreateStack();
        using Session session = new(sessionStack);
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        session.TryAddFrameSource(source, out _, out _);
        session.TryStart(out _);
        session.WaitForCompletion();
        RecordingObserver myObserver = new();
        PacketId id = new(1);

        bool parsed = false;
        FieldId ipTtlFieldId = session.Stack.GetFieldId("ip.ttl")!.Value;
        if (session.TryGetFrame(id, out Frame frame, out SessionFailure? failure))
        {
            Stack stack = session.Stack;
            ParseObserver[] observers =
            [
                new ParseObserver { Observer = myObserver, Watch = FieldWatch.Only(stack, ipTtlFieldId) },
            ];
            ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
            parsed = Packet.TryParse(id, stack, frame, in options, out Packet? _, out ParseFailure _);
        }

        await Assert.That(failure).IsNull();
        await Assert.That(parsed).IsTrue();
        await Assert.That(myObserver.BeganPacket).IsEqualTo(1);
        await Assert.That(myObserver.Fields.All(field => field == ipTtlFieldId)).IsTrue();
        await Assert.That(myObserver.Fields.Count).IsEqualTo(1);
        session.Shutdown();
    }

    #endregion

    #region Sample types

    /// <summary>Quick-start listener of the guide.</summary>
    private sealed class CountingListener : ISessionListener
    {
        private int _Seen;

        public string UiName => "counter";

        internal int Seen => Volatile.Read(ref _Seen);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
            => Interlocked.Add(ref _Seen, toIndexExclusive - fromIndex);
    }

    /// <summary>Packet-list listener of the guide, with counters a test can read from another thread.</summary>
    private sealed class PacketListListener : ISessionListener
    {
        private readonly PacketRef[] _Buffer = new PacketRef[256];
        private int _RowCount;
        private int _StackChanges;
        private int _FirstRowId = -1;

        // Touched only on this listener's thread; a UI copies rows out by posting them.
        private List<(int Id, Timestamp Time)> Rows { get; } = [];

        public string UiName => "packet list";

        internal int RowCount => Volatile.Read(ref _RowCount);

        internal int StackChanges => Volatile.Read(ref _StackChanges);

        internal int FirstRowId => Volatile.Read(ref _FirstRowId);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
            for (int start = fromIndex; start < toIndexExclusive; start += _Buffer.Length)
            {
                int filled = session.ReadPackets(start, _Buffer, out PacketIdLayout _);
                for (int i = 0; i < filled && _Buffer[i].Id.Value < toIndexExclusive; i++)
                {
                    Packet? packet = _Buffer[i].Packet;
                    if (packet is not null)
                    {
                        Rows.Add((packet.Id.Value, packet.Timestamp));
                    }
                }
            }

            if (Rows.Count > 0)
            {
                Volatile.Write(ref _FirstRowId, Rows[0].Id);
            }

            Volatile.Write(ref _RowCount, Rows.Count);
        }

        public void OnStackChanged(ISessionReader session)
        {
            Rows.Clear();
            Volatile.Write(ref _RowCount, 0);
            Interlocked.Increment(ref _StackChanges);
        }
    }

    /// <summary>Observer of the guide's single-packet recipe; records what the parse reported.</summary>
    private sealed class RecordingObserver : IFieldObserver
    {
        internal int BeganPacket { get; private set; }

        internal List<FieldId> Fields { get; } = [];

        public void BeginPacket(PacketId id, Timestamp timestamp) => BeganPacket++;

        public void OnField(in FieldVisit visit) => Fields.Add(visit.FieldId);
    }

    #endregion
}
