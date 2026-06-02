// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for LIN and FlexRay frame parsing via <see cref="BlfStreamSource"/>.
/// Verifies that BLF LIN Message (V1/V2), FlexRay frames, and AppText channel
/// names are correctly parsed and exposed through the frame source API.
/// </summary>
internal sealed class BlfLinFlexRayTests
{


    /// <summary>Creates a BlfStreamSource from raw bytes.</summary>
    private static BlfStreamSource CreateSource(byte[] data) =>
        BlfStreamSource.FromStream(new MemoryStream(data), "test.blf");

    // ========================================================================
    // LIN Message V1 (Type 11)
    // ========================================================================

    [Test]
    public async Task LinMessageV1_SingleFrame_Parsed()
    {
        byte[] linData = [0x01, 0x02, 0x03, 0x04];

        byte[] blfData = new BlfTestGenerator()
            .AddLinFrame(1, 0x3A, linData, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Lin);
        await Assert.That(frame.Value.Data.Length).IsGreaterThan(0);

        // Verify exhaustion
        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(1);
        await Assert.That(source.HasErrors).IsFalse();
    }

    [Test]
    public async Task LinMessageV1_MultipleFrames_AllParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLinFrame(1, 0x10, [0xAA, 0xBB], 1_000_000)
            .AddLinFrame(2, 0x20, [0xCC, 0xDD, 0xEE], 2_000_000)
            .AddLinFrame(1, 0x30, [0xFF], 3_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(source.ReadFrameCount).IsEqualTo(3);
    }

    // ========================================================================
    // LIN Message V2 (Type 57)
    // ========================================================================

    [Test]
    public async Task LinMessageV2_SingleFrame_Parsed()
    {
        byte[] linData = [0xDE, 0xAD, 0xBE, 0xEF];

        byte[] blfData = new BlfTestGenerator()
            .AddLinMessage2(1, 0x15, linData, 0xAB, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Lin);
        await Assert.That(frame.Value.Data.Length).IsGreaterThan(0);

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task LinMessageV2_WithChecksum_Parsed()
    {
        byte[] linData = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        byte[] blfData = new BlfTestGenerator()
            .AddLinMessage2(2, 0x3F, linData, 0xCC, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Lin);

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // FlexRay (Type 50 — RcvMessage)
    // ========================================================================

    [Test]
    public async Task FlexRay_SingleFrame_Parsed()
    {
        byte[] frData = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

        byte[] blfData = new BlfTestGenerator()
            .AddFlexRayFrame(1, 42, 7, 0x1234, frData, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Flexray);
        await Assert.That(frame.Value.Data.Length).IsGreaterThan(0);

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(1);
        await Assert.That(source.HasErrors).IsFalse();
    }

    [Test]
    public async Task FlexRay_MultipleFrames_AllParsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddFlexRayFrame(1, 10, 0, 0xAAAA, [0x01, 0x02], 1_000_000)
            .AddFlexRayFrame(2, 20, 1, 0xBBBB, [0x03, 0x04, 0x05], 2_000_000)
            .AddFlexRayFrame(1, 30, 2, 0xCCCC, [0x06], 3_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(source.ReadFrameCount).IsEqualTo(3);
    }

    // ========================================================================
    // Mixed LIN + FlexRay + Ethernet
    // ========================================================================

    [Test]
    public async Task MixedProtocols_AllFramesParsed()
    {
        byte[] ethFrame = FrameBuilders.BuildEthernetFrame(
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
            0x0800, [0xAA, 0xBB]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, ethFrame, 1_000_000)
            .AddLinFrame(1, 0x10, [0x01, 0x02], 2_000_000)
            .AddFlexRayFrame(1, 5, 0, 0x1111, [0x03, 0x04], 3_000_000)
            .AddLinMessage2(2, 0x20, [0x05, 0x06], 0xDD, 4_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);

        // First frame — Ethernet
        Frame? f1 = source.NextFrame();
        await Assert.That(f1).IsNotNull();
        await Assert.That(f1!.Value.LinkType).IsEqualTo(LinkType.Ethernet);

        // Second — LIN
        Frame? f2 = source.NextFrame();
        await Assert.That(f2).IsNotNull();
        await Assert.That(f2!.Value.LinkType).IsEqualTo(LinkType.Lin);

        // Third — FlexRay
        Frame? f3 = source.NextFrame();
        await Assert.That(f3).IsNotNull();
        await Assert.That(f3!.Value.LinkType).IsEqualTo(LinkType.Flexray);

        // Fourth — LIN V2
        Frame? f4 = source.NextFrame();
        await Assert.That(f4).IsNotNull();
        await Assert.That(f4!.Value.LinkType).IsEqualTo(LinkType.Lin);

        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.ReadFrameCount).IsEqualTo(4);
    }

    // ========================================================================
    // LIN — empty data
    // ========================================================================

    [Test]
    public async Task LinMessageV1_EmptyData_Parsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddLinFrame(1, 0x00, [], 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Lin);
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // FlexRay — empty payload
    // ========================================================================

    [Test]
    public async Task FlexRay_EmptyPayload_Parsed()
    {
        byte[] blfData = new BlfTestGenerator()
            .AddFlexRayFrame(1, 99, 0, 0x0000, [], 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Flexray);
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Channel names via AppText
    // ========================================================================

    [Test]
    public async Task AppText_ChannelName_AssignedToInterface()
    {
        byte[] ethFrame = FrameBuilders.BuildEthernetFrame(
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
            0x0800, [0xAA]);

        byte[] blfData = new BlfTestGenerator()
            // Channel name before any frame on that channel
            .AddAppTextChannel(1, 5, "ETH-Port1", 500_000) // busType=5 for Ethernet
            .AddEthernetFrame(1, ethFrame, 1_000_000)
            .Build();

        using BlfStreamSource source = CreateSource(blfData);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);

        // The interface should have been registered with a name from AppText
        FrameInterfaceId ifId = frame.Value.InterfaceId;
        FrameInterfaceInfo? iface = registry.Get(ifId);
        await Assert.That(iface).IsNotNull();
        await Assert.That(iface!.UiName).IsNotNull();
    }
}
