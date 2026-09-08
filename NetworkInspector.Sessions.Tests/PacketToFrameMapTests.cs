// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Unit tests for Session packet-to-frame packing and dense appends.</summary>
internal sealed class PacketToFrameMapTests
{
    [Test]
    public async Task PackFrameMapping_Roundtrip_PreservesIds()
    {
        FrameId frameId = new(10);
        FrameSourceId sourceId = new(2);

        long packed = Session.PackFrameMapping(frameId, sourceId);
        Session.UnpackFrameMapping(packed, out FrameId gotFrame, out FrameSourceId gotSource);

        await Assert.That(gotFrame).IsEqualTo(frameId);
        await Assert.That(gotSource).IsEqualTo(sourceId);
    }

    [Test]
    public async Task PackFrameMapping_InvalidIds_DoesNotSignExtend()
    {
        long packed = Session.PackFrameMapping(FrameId.Invalid, FrameSourceId.Invalid);
        Session.UnpackFrameMapping(packed, out FrameId gotFrame, out FrameSourceId gotSource);

        await Assert.That(gotFrame).IsEqualTo(FrameId.Invalid);
        await Assert.That(gotSource).IsEqualTo(FrameSourceId.Invalid);
    }

    [Test]
    public async Task RecordPacketFrame_InvalidPacketId_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert
            .That(() => session.RecordPacketFrame(PacketId.Invalid, new FrameId(1), new FrameSourceId(0)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RecordPacketFrame_NonSequentialPacketId_Throws()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        await Assert
            .That(() => session.RecordPacketFrame(new PacketId(5), new FrameId(1), new FrameSourceId(0)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RecordPacketFrame_SequentialIds_RoundtripThroughTryGetFrame()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        await Assert.That(session.TryGetFrame(new PacketId(0), out Frame first)).IsTrue();
        await Assert.That(session.TryGetFrame(new PacketId(2), out Frame last)).IsTrue();
        await Assert.That(first.IsValid).IsTrue();
        await Assert.That(last.IsValid).IsTrue();
        await Assert.That(last.Id).IsEqualTo(new FrameId(2));

        session.Shutdown();
    }
}
