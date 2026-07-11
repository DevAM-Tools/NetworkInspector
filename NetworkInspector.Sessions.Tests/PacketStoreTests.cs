// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Tests for <see cref="PacketStore"/> — chunked append-only packet storage.
/// </summary>
internal sealed class PacketStoreTests
{
    [Test]
    public async Task Store_And_Get_ReturnsStoredPacket()
    {
        PacketStore store = new();
        using Stack stack = TestHarness.CreateStack();
        Packet packet = _ParseTestPacket(stack, 0);

        store.Store(packet.Id, packet);
        Packet? result = store.Get(packet.Id);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(packet.Id);
    }

    [Test]
    public async Task Get_InvalidId_ReturnsNull()
    {
        PacketStore store = new();
        Packet? result = store.Get(PacketId.Invalid);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Get_UnstaredId_ReturnsNull()
    {
        PacketStore store = new();
        Packet? result = store.Get(new PacketId(42));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ReadRange_ReturnsCorrectPackets()
    {
        PacketStore store = new();
        using Stack stack = TestHarness.CreateStack();

        // Store 10 packets
        Packet[] packets = new Packet[10];
        for (int i = 0; i < 10; i++)
        {
            packets[i] = _ParseTestPacket(stack, i);
            store.Store(packets[i].Id, packets[i]);
        }

        // Read range [3, 7)
        Packet?[] buffer = new Packet?[4];
        int read = store.ReadRange(3, buffer);

        await Assert.That(read).IsEqualTo(4);
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(buffer[i]).IsNotNull();
            await Assert.That(buffer[i]!.Id).IsEqualTo(new PacketId(3 + i));
        }
    }

    [Test]
    public async Task Clear_RemovesAllPackets()
    {
        PacketStore store = new();
        using Stack stack = TestHarness.CreateStack();
        Packet packet = _ParseTestPacket(stack, 0);

        store.Store(packet.Id, packet);
        store.Clear();

        Packet? result = store.Get(packet.Id);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ConcurrentStoreAndGet_NoCorruption()
    {
        // Store packets from multiple threads, read from main.
        PacketStore store = new();
        using Stack stack = TestHarness.CreateStack();
        const int count = 500;

        Packet[] packets = new Packet[count];
        for (int i = 0; i < count; i++)
        {
            packets[i] = _ParseTestPacket(stack, i);
        }

        // Store concurrently (each thread stores its own range)
        Task[] tasks =
        [
            Task.Run(() =>
            {
                for (int i = 0; i < count / 2; i++)
                {
                    store.Store(packets[i].Id, packets[i]);
                }
            }),
            Task.Run(() =>
            {
                for (int i = count / 2; i < count; i++)
                {
                    store.Store(packets[i].Id, packets[i]);
                }
            }),
        ];
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // All packets should be retrievable
        for (int i = 0; i < count; i++)
        {
            Packet? result = store.Get(new PacketId(i));
            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Id).IsEqualTo(new PacketId(i));
        }
    }

    /// <summary>Creates a test packet by parsing a synthetic UDP frame.</summary>
    private static Packet _ParseTestPacket(Stack stack, int id)
    {
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        if (registry.SourceCount == 0)
        {
            using TestFrameSource source = TestFrameSource.WithUdpFrames(0);
            registry.RegisterSource(source);
        }

        FrameSourceId sourceId = new(0);
        FrameInterfaceId ifId = registry.Register(sourceId, $"test_{id}", null, LinkType.Ethernet);
        byte[] data = TestHarness.GenerateUdpFrame();

        Frame frame = Frame.Create(
            new FrameId(id),
            Timestamp.FromNanos(id * 1_000_000L),
            data,
            LinkType.Ethernet,
            ifId,
            registry).Value;

        return Packet.ParseFrame(new PacketId(id), stack, frame);
    }
}
