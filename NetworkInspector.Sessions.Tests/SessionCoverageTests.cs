// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FieldInfo = System.Reflection.FieldInfo;

namespace NetworkInspector.Sessions.Tests;

/// <summary>Additional session coverage for uncovered exit points.</summary>
internal sealed class SessionCoverageTests
{
    [Test]
    public async Task TryStart_WhenNotIdle_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert.That(session.TryStart()).IsTrue();
        await Assert.That(session.TryStart()).IsFalse();

        session.Shutdown();
    }

    [Test]
    public async Task TryAddListener_AfterStopped_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool added = session.TryAddListener(listener, out ListenerInfo? info);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();

        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_MappingMiss_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool found = session.TryGetPacket(new PacketId(100), out Packet? packet);

        await Assert.That(found).IsFalse();
        await Assert.That(packet).IsNull();

        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_NonRandomAccessSource_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using ForwardOnlyFrameSource source = new(2);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        _GetPacketStore(session).Clear();

        bool found = session.TryGetPacket(new PacketId(0), out Packet? packet);

        await Assert.That(found).IsFalse();
        await Assert.That(packet).IsNull();

        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_WithoutPacketIndex_UsesNonIndexedParse()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        _GetPacketStore(session).Clear();
        _SetPacketIndex(session, null);

        bool found = session.TryGetPacket(new PacketId(0), out Packet? packet);

        await Assert.That(found).IsTrue();
        await Assert.That(packet).IsNotNull();
        await Assert.That(session.PacketIndex).IsNull();

        session.Shutdown();
    }

    [Test]
    public async Task Restart_WrongRegistry_ThrowsArgumentException()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(5);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        try
        {
            session.Restart(_ =>
            {
                SettingsManager settings = new();
                try
                {
                    FrameInterfaceRegistry wrongRegistry = new();
                    StackBuilder builder = new(settings, wrongRegistry);
                    ProtocolRegistration.RegisterStandardProtocols(builder);
                    return builder.Build();
                }
                finally
                {
                    settings.Dispose();
                }
            });
            throw new InvalidOperationException("Expected ArgumentException was not thrown.");
        }
        catch (ArgumentException ex)
        {
            await Assert.That(ex.ParamName).IsEqualTo("stackFactory");
        }

        session.Shutdown();
    }

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        using Stack stack = TestHarness.CreateStack();
        Session session = new(stack);
        session.Dispose();
        session.Dispose();

        await Assert.That(session.ShutdownErrors).IsNull();
    }

    [Test]
    public async Task ListenerSlot_ExposesIdAndUiName()
    {
        using Stack stack = TestHarness.CreateStack();
        TestSessionListener listener = new();

        using Session session = new(stack);
        session.TryAddListener(listener, out ListenerInfo? info);
        session.TryStart();

        ListenerSlot slot = _GetListenerSlots(session)[0];

        await Assert.That(slot.Id.IsValid).IsTrue();
        await Assert.That(slot.UiName).IsEqualTo(listener.UiName);
        await Assert.That(slot.ListenerInfo).IsSameReferenceAs(info);

        session.Shutdown();
    }

    [Test]
    public async Task WaitForCompletion_WithTimeout_ReturnsTrueWhenSourcesComplete()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool completed = session.WaitForCompletion(TimeSpan.FromSeconds(5));

        await Assert.That(completed).IsTrue();

        session.Shutdown();
    }

    private static PacketStore _GetPacketStore(Session session)
    {
        FieldInfo field = typeof(Session).GetField(
            "_PacketStore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (PacketStore)field.GetValue(session)!;
    }

    private static void _SetPacketIndex(Session session, PacketIndex? index)
    {
        FieldInfo field = typeof(Session).GetField(
            "_PacketIndex",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(session, index);
    }

    private static ListenerSlot[] _GetListenerSlots(Session session)
    {
        FieldInfo field = typeof(Session).GetField(
            "_ListenerSlots",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        SnapshotList<ListenerSlot> slots = (SnapshotList<ListenerSlot>)field.GetValue(session)!;
        return slots.CurrentSnapshot;
    }

    /// <summary>Frame source without random-access support.</summary>
    private sealed class ForwardOnlyFrameSource : IFrameSource
    {
        private readonly int _Count;
        private int _Next;
        private FrameInterfaceId _InterfaceId;
        private FrameInterfaceRegistry? _Registry;

        internal ForwardOnlyFrameSource(int count) => _Count = count;

        public string UiName => "ForwardOnly";

        public string? Description => null;

        public int? EstimatedFrameCount => _Count;

        public bool IsRunning => _Registry is not null;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
            _Registry = registry;
            _InterfaceId = registry.Register(sourceId, "fwd", null, LinkType.Ethernet);
            _Next = 0;
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            if (_Next >= _Count)
            {
                return null;
            }

            int idx = _Next++;
            byte[] data = TestHarness.GenerateUdpFrame();
            return Frame.Create(
                new FrameId(idx),
                Timestamp.FromNanos(idx),
                data,
                LinkType.Ethernet,
                _InterfaceId,
                _Registry!).Value;
        }

        public void Dispose() => _Registry = null;
    }
}
