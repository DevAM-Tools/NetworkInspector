// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Unit tests for <see cref="PacketToFrameMap"/>.</summary>
internal sealed class PacketToFrameMapTests
{
    [Test]
    public async Task Record_InvalidPacketId_ReturnsFalse()
    {
        PacketToFrameMap map = new();

        bool recorded = map.Record(PacketId.Invalid, new FrameId(1), new FrameSourceId(0));

        await Assert.That(recorded).IsFalse();
    }

    [Test]
    public async Task Record_OutOfRangePacketId_Throws()
    {
        PacketToFrameMap map = new();
        int overflow = Array.MaxLength;

        await Assert
            .That(() => map.Record(new PacketId(overflow), new FrameId(1), new FrameSourceId(0)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Record_AndTryGet_Roundtrip()
    {
        PacketToFrameMap map = new();
        PacketId packetId = new(5);
        FrameId frameId = new(10);
        FrameSourceId sourceId = new(2);

        await Assert.That(map.Record(packetId, frameId, sourceId)).IsTrue();

        bool found = map.TryGet(packetId, out FrameId gotFrame, out FrameSourceId gotSource);

        await Assert.That(found).IsTrue();
        await Assert.That(gotFrame).IsEqualTo(frameId);
        await Assert.That(gotSource).IsEqualTo(sourceId);
    }

    [Test]
    public async Task TryGet_InvalidPacketId_ReturnsFalse()
    {
        PacketToFrameMap map = new();

        bool found = map.TryGet(PacketId.Invalid, out FrameId frameId, out FrameSourceId sourceId);

        await Assert.That(found).IsFalse();
        await Assert.That(frameId).IsEqualTo(FrameId.Invalid);
        await Assert.That(sourceId).IsEqualTo(FrameSourceId.Invalid);
    }

    [Test]
    public async Task TryGet_UnallocatedChunk_ReturnsFalse()
    {
        PacketToFrameMap map = new();
        PacketId packetId = new(Array.MaxLength - 1);

        bool found = map.TryGet(packetId, out FrameId frameId, out FrameSourceId sourceId);

        await Assert.That(found).IsFalse();
        await Assert.That(frameId).IsEqualTo(FrameId.Invalid);
        await Assert.That(sourceId).IsEqualTo(FrameSourceId.Invalid);
    }

    [Test]
    public async Task TryGet_UnsetSlot_ReturnsFalse()
    {
        PacketToFrameMap map = new();
        PacketId packetId = new(0);
        map.Record(packetId, new FrameId(1), new FrameSourceId(0));
        map.Clear();

        bool found = map.TryGet(packetId, out FrameId frameId, out FrameSourceId sourceId);

        await Assert.That(found).IsFalse();
        await Assert.That(frameId).IsEqualTo(FrameId.Invalid);
        await Assert.That(sourceId).IsEqualTo(FrameSourceId.Invalid);
    }

    [Test]
    public async Task Clear_DropsAllMappings()
    {
        PacketToFrameMap map = new();
        map.Record(new PacketId(0), new FrameId(1), new FrameSourceId(0));
        map.Record(new PacketId(1), new FrameId(2), new FrameSourceId(0));

        map.Clear();

        await Assert.That(map.TryGet(new PacketId(0), out _, out _)).IsFalse();
        await Assert.That(map.TryGet(new PacketId(1), out _, out _)).IsFalse();
    }
}
