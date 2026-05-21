// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for <see cref="BlfStreamSource"/> — stream-based BLF reading.
/// Verifies sequential reading of Ethernet, CAN, and CAN FD frames, timestamps,
/// and stream lifecycle management.
/// </summary>
internal sealed class BlfStreamSourceTests
{
    private static readonly byte[] SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Creates a BlfStreamSource from a MemoryStream backed by the given data.</summary>
    private static BlfStreamSource CreateSource(byte[] data, string uiName = "test.blf", bool leaveOpen = false) =>
        BlfStreamSource.FromStream(new MemoryStream(data), uiName, leaveOpen);

    /// <summary>Starts the source with a fresh registry.</summary>
    private static void StartSource(BlfStreamSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
    }

    // ========================================================================
    // Single Ethernet frame
    // ========================================================================

    [Test]
    public async Task SingleEthernetFrame_Parsed()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, [0xDE, 0xAD, 0xBE, 0xEF]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        await Assert.That(source.EstimatedFrameCount).IsNull();

        StartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(frame.Value.Id.Value).IsEqualTo(0);
        await Assert.That(frame.Value.Data.Span.SequenceEqual(eth)).IsTrue();

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Multiple frames
    // ========================================================================

    [Test]
    public async Task MultipleFrames_AllRead()
    {
        BlfTestGenerator gen = new();

        List<byte[]> expected = [];
        for (int i = 0; i < 5; i++)
        {
            byte[] payload = [(byte)i, (byte)(i + 1), (byte)(i + 2)];
            byte[] eth = FrameBuilders.BuildEthernetFrame(DstMac, SrcMac, 0x0800, payload);
            expected.Add(eth);
            gen.AddEthernetFrame(1, eth, (i + 1) * 1_000_000L);
        }

        byte[] blfData = gen.Build();
        using BlfStreamSource source = CreateSource(blfData);
        StartSource(source);

        for (int i = 0; i < 5; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(i);
            await Assert.That(frame.Value.Data.Span.SequenceEqual(expected[i])).IsTrue();
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // CAN Classic
    // ========================================================================

    [Test]
    public async Task CanClassicSingle_Parsed()
    {
        byte[] socketCanFrame = FrameBuilders.BuildSocketCanClassic(0x123, [0xDE, 0xAD, 0xBE, 0xEF]);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFrame(1, socketCanFrame, 500_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        StartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // CAN FD
    // ========================================================================

    [Test]
    public async Task CanFdSingle_Parsed()
    {
        byte[] socketCanFdFrame = FrameBuilders.BuildSocketCanFd(
            0x456, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                    0x09, 0x0A, 0x0B, 0x0C]);

        byte[] blfData = new BlfTestGenerator()
            .AddCanFdFrame(1, socketCanFdFrame, 750_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        StartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Invalid magic
    // ========================================================================

    [Test]
    public async Task InvalidMagic_ThrowsBlfException()
    {
        byte[] garbage = new byte[200];
        garbage[0] = 0xFF; // Not "LOGG"

        using BlfStreamSource source = CreateSource(garbage);
        StartSource(source);

        BlfException? caught = null;
        try
        {
            source.NextFrame();
        }
        catch (BlfException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }

    // ========================================================================
    // Empty/short data
    // ========================================================================

    [Test]
    public async Task EmptyData_ReturnsNull()
    {
        byte[] empty = [];

        using BlfStreamSource source = CreateSource(empty);
        StartSource(source);

        // Too short for header — should return null (not throw)
        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNull();
    }

    // ========================================================================
    // UiName
    // ========================================================================

    [Test]
    public async Task UiName_MatchesProvided()
    {
        byte[] blfData = new BlfTestGenerator().Build();

        using BlfStreamSource source = CreateSource(blfData, uiName: "MyBlf");
        await Assert.That(source.UiName).IsEqualTo("MyBlf");
    }

    // ========================================================================
    // Stream lifecycle — leaveOpen
    // ========================================================================

    [Test]
    public async Task StreamDisposedWhenNotLeaveOpen()
    {
        byte[] blfData = new BlfTestGenerator().Build();

        MemoryStream stream = new(blfData);
        BlfStreamSource source = BlfStreamSource.FromStream(stream, leaveOpen: false);
        source.Dispose();

        bool streamDisposed = false;
        try
        {
            stream.ReadByte();
        }
        catch (ObjectDisposedException)
        {
            streamDisposed = true;
        }

        await Assert.That(streamDisposed).IsTrue();
    }

    [Test]
    public async Task StreamNotDisposedWhenLeaveOpen()
    {
        byte[] blfData = new BlfTestGenerator().Build();

        using MemoryStream stream = new(blfData);
        BlfStreamSource source = BlfStreamSource.FromStream(stream, leaveOpen: true);
        source.Dispose();

        stream.Position = 0;
        bool canRead = stream.ReadByte() >= 0;
        await Assert.That(canRead).IsTrue();
    }

    // ========================================================================
    // Double-Dispose idempotency (C-05 / H-02 regression guard)
    // ========================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        BlfStreamSource source = CreateSource(blfData);
        StartSource(source);

        source.Dispose();

        // Second Dispose must be idempotent and not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    // ========================================================================
    // Lifecycle guards — pre-Start contract
    // ========================================================================

    [Test]
    public async Task NextFrame_BeforeStart_ThrowsInvalidOperationException()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        using BlfStreamSource source = CreateSource(blfData);
        // Calling NextFrame() without Start() must throw — not silently return null.
        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NextFrame_AfterDispose_ThrowsObjectDisposedException()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        BlfStreamSource source = CreateSource(blfData);
        StartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task IsRunning_FalseBeforeStart()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        using BlfStreamSource source = CreateSource(blfData);
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        BlfStreamSource source = CreateSource(blfData);
        StartSource(source);

        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();

        await Assert.That(source.IsRunning).IsFalse();
    }

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        byte[] blfData = new BlfTestGenerator().Build();
        using BlfStreamSource source = CreateSource(blfData);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
