// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for CAN Classic and CAN FD frame reading from BLF sources.
/// Verifies ID encoding, DLC values, extended IDs, FD flags, and multi-channel handling.
/// </summary>
internal sealed class BlfCanTests
{
    private static BlfSource CreateSource(byte[] blfData) =>
        BlfSource.FromData(blfData, "test.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });

    private static void StartSource(BlfSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    // ========================================================================
    // CAN Classic
    // ========================================================================

    [Test]
    public async Task CanClassicSingleFrame_ParsedCorrectly()
    {
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x123, [1, 2, 3, 4, 5, 6, 7, 8]);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFrame(1, can, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(1);

        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frame.Value.Id.Value).IsEqualTo(0);

        // Verify SocketCAN format: id(4 BE)+dlc(1)+flags(1)+reserved(2)+data(8)
        await Assert.That(frame.Value.Data.Length >= 16).IsTrue();

        uint id = BinaryPrimitives.ReadUInt32BigEndian(frame.Value.Data.Span);
        await Assert.That(id & 0x1FFF_FFFF).IsEqualTo(0x123u);
    }

    [Test]
    public async Task CanClassicExtendedId_EffFlagSet()
    {
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x1234_5678, [0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA], extended: true);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFrame(1, can, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        ReadOnlySpan<byte> data = frame!.Value.Data.Span;
        uint id = BinaryPrimitives.ReadUInt32BigEndian(data);

        // EFF flag should be set
        await Assert.That(id & 0x8000_0000).IsNotEqualTo(0u);
        // 29-bit CAN ID should match
        await Assert.That(id & 0x1FFF_FFFF).IsEqualTo(0x1234_5678u);
    }

    [Test]
    public async Task CanClassicVariousDlcValues_ParsedCorrectly()
    {
        (uint CanId, byte[] Data)[] tests =
        [
            (0x100, []),                         // DLC=0
            (0x200, [1, 2, 3, 4]),               // DLC=4
            (0x300, [1, 2, 3, 4, 5, 6, 7, 8]),  // DLC=8
        ];

        foreach ((uint canId, byte[] canData) in tests)
        {
            byte[] can = FrameBuilders.BuildSocketCanClassic(canId, canData);
            byte[] blfData = new BlfTestGenerator()
                .AddCanFrame(1, can, 1_000_000)
                .Build();

            using BlfSource source = CreateSource(blfData);
            StartSource(source);
            Frame? frame = source.NextFrame();

            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
            await Assert.That(frame.Value.Data.Span[4]).IsEqualTo((byte)canData.Length);
        }
    }

    // ========================================================================
    // CAN FD
    // ========================================================================

    [Test]
    public async Task CanFdSingleFrame_ParsedCorrectly()
    {
        byte[] canFd = FrameBuilders.BuildSocketCanFd(0x200, Enumerable.Repeat((byte)0xBB, 32).ToArray(), brs: true);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFdFrame(1, canFd, 1_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);

        ReadOnlySpan<byte> data = frame.Value.Data.Span;
        uint id = BinaryPrimitives.ReadUInt32BigEndian(data);
        await Assert.That(id & 0x1FFF_FFFF).IsEqualTo(0x200u);
    }

    // ========================================================================
    // Mixed CAN Classic + FD
    // ========================================================================

    [Test]
    public async Task CanClassicAndFdMixed_AllFramesParsed()
    {
        byte[] classic1 = FrameBuilders.BuildSocketCanClassic(0x100, [1, 2, 3, 4]);
        byte[] fd = FrameBuilders.BuildSocketCanFd(0x200, Enumerable.Repeat((byte)0xAA, 24).ToArray(), brs: true);
        byte[] classic2 = FrameBuilders.BuildSocketCanClassic(0x300, [5, 6, 7, 8]);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFrame(1, classic1, 1_000_000)
            .AddCanFdFrame(1, fd, 2_000_000)
            .AddCanFrame(1, classic2, 3_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(3);

        StartSource(source);

        // All three frames should be CAN
        for (int i = 0; i < 3; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
            await Assert.That(frame.Value.Id.Value).IsEqualTo(i);
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Multi-channel
    // ========================================================================

    [Test]
    public async Task CanDifferentChannels_DifferentInterfaceIds()
    {
        byte[] can1 = FrameBuilders.BuildSocketCanClassic(0x100, [1, 2, 3, 4]);
        byte[] can2 = FrameBuilders.BuildSocketCanClassic(0x200, [5, 6, 7, 8]);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFrame(1, can1, 1_000_000)
            .AddCanFrame(2, can2, 2_000_000)
            .Build();

        using BlfSource source = CreateSource(blfData);
        StartSource(source);

        Frame? f1 = source.NextFrame();
        Frame? f2 = source.NextFrame();

        await Assert.That(f1).IsNotNull();
        await Assert.That(f2).IsNotNull();
        await Assert.That(f1!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(f2!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);

        // Different channels should produce different interface IDs
        await Assert.That(f1.Value.InterfaceId).IsNotEqualTo(f2.Value.InterfaceId);
    }
}
