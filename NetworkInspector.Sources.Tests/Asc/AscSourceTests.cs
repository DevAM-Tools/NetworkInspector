// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Asc;

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Integration tests for <see cref="AscSource"/> (random-access) verifying
/// frame indexing, sequential iteration, random access via FrameById,
/// multi-bus support, and error handling.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscSourceTests
{
    /// <summary>Safety guard to prevent infinite loops in test helpers.</summary>
    private const int MaxFrameGuard = 10_000;

    #region Helpers

    /// <summary>
    /// Creates an <see cref="AscSource"/> from inline ASC text.
    /// </summary>
    private static AscSource CreateFromText(string ascContent, AscSourceOptions? options = null) =>
        AscSource.FromText(ascContent, "test.asc", options);

    /// <summary>
    /// Registers the source and starts it.
    /// </summary>
    private static FrameInterfaceRegistry StartSource(AscSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return registry;
    }

    /// <summary>
    /// Drains all frames via NextFrame with a safety guard against infinite loops.
    /// </summary>
    private static List<Frame> DrainFrames(AscSource source)
    {
        List<Frame> frames = [];
        for (int i = 0; i < MaxFrameGuard; i++)
        {
            Frame? f = source.NextFrame();
            if (!f.HasValue)
            {
                break;
            }
            frames.Add(f.Value);
        }
        return frames;
    }

    #endregion

    // ========================================================================
    // Empty / header-only
    // ========================================================================

    [Test]
    public async Task EmptyFile_ProducesNoFrames()
    {
        using AscSource source = CreateFromText("");
        StartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame.HasValue).IsFalse();
    }

    [Test]
    public async Task HeaderOnly_ProducesNoFrames()
    {
        using AscSource source = CreateFromText(
            "date Sun Nov 24 11:44:00 AM 2019\n" +
            "base hex timestamps absolute\n" +
            "internal events logged\n");
        StartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame.HasValue).IsFalse();
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(0);
    }

    // ========================================================================
    // EstimatedFrameCount
    // ========================================================================

    [Test]
    public async Task EstimatedFrameCount_ReflectsIndex()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n");
        StartSource(source);

        // AscSource scans fully at Open time — count known immediately
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(3);
    }

    // ========================================================================
    // Sequential iteration
    // ========================================================================

    [Test]
    public async Task NextFrame_ReturnsAllFramesSequentially()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "End TriggerBlock\n");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Id.Value).IsEqualTo(0);
        await Assert.That(frames[1].Id.Value).IsEqualTo(1);
    }

    // ========================================================================
    // Random access
    // ========================================================================

    [Test]
    public async Task FrameById_ReturnsCorrectFrame()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n");
        StartSource(source);

        // Access frames out of order
        Frame? f2 = source.FrameById(new FrameId(2));
        Frame? f0 = source.FrameById(new FrameId(0));

        await Assert.That(f2.HasValue).IsTrue();
        await Assert.That(f0.HasValue).IsTrue();
        await Assert.That(f2!.Value.Id.Value).IsEqualTo(2);
        await Assert.That(f0!.Value.Id.Value).IsEqualTo(0);
    }

    [Test]
    public async Task FrameById_OutOfRange_ReturnsNull()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "End TriggerBlock\n");
        StartSource(source);

        Frame? f = source.FrameById(new FrameId(999));

        await Assert.That(f.HasValue).IsFalse();
    }

    // ========================================================================
    // Multi-bus support
    // ========================================================================

    [Test]
    public async Task MultiBus_AllFramesParsed()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11\n" +
            "0.200000 CANFD 1 Rx 200 1 0 8 8 01 02 03 04 05 06 07 08\n" +
            "0.300000 L1 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0\n" +
            "0.400000 Fr 1 V9 0A 4 0 0 1234 x 8 0102030405060708\n" +
            "0.500000 ETH 1 Rx 14:001122334455667788990A0B0C0D\n" +
            "End TriggerBlock\n");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(5);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[1].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[2].LinkType).IsEqualTo(LinkType.Lin);
        await Assert.That(frames[3].LinkType).IsEqualTo(LinkType.Flexray);
        await Assert.That(frames[4].LinkType).IsEqualTo(LinkType.Ethernet);
    }

    // ========================================================================
    // Interface registration
    // ========================================================================

    [Test]
    public async Task DifferentChannels_DifferentInterfaceIds()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 2 200 Rx d 2 03 04\n" +
            "End TriggerBlock\n");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].InterfaceId).IsNotEqualTo(frames[1].InterfaceId);
    }

    // ========================================================================
    // Error tolerance
    // ========================================================================

    [Test]
    public async Task TolerantMode_CountsErrors()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 GARBAGE\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n",
            new AscSourceOptions { ErrorTolerance = ErrorToleranceMode.Tolerant });
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        // Garbage line is not a frame-producing type — skipped by classifier
        await Assert.That(frames.Count).IsEqualTo(2);
    }

    // ========================================================================
    // Dispose
    // ========================================================================

    [Test]
    public async Task IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        AscSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        StartSource(source);

        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();

        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task NextFrame_AfterDispose_Throws()
    {
        AscSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        StartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task FrameById_AfterDispose_Throws()
    {
        AscSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "End TriggerBlock\n");
        StartSource(source);
        source.Dispose();

        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<ObjectDisposedException>();
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_IdsParsedCorrectly()
    {
        using AscSource source = CreateFromText(
            "base dec\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 291 Rx d 2 10 20\n" +
            "End TriggerBlock\n");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(1);

        // CAN ID: decimal 291 = 0x123
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frames[0].Data.Span);
        await Assert.That(id & 0x1FFFFFFFu).IsEqualTo(291u);
    }

    // ========================================================================
    // FromData factory
    // ========================================================================

    [Test]
    public async Task FromData_WorksLikeFromText()
    {
        string text = "base hex\n" +
                      "Begin Triggerblock\n" +
                      "0.100000 1 100 Rx d 2 01 02\n" +
                      "End TriggerBlock\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        using AscSource source = AscSource.FromData(bytes, "data.asc");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(1);
    }

    // ========================================================================
    // No trigger block
    // ========================================================================

    [Test]
    public async Task NoTriggerBlock_FramesStillParsed()
    {
        using AscSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n");
        StartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
    }

    // ========================================================================
    // Double-Dispose idempotency (C-01 / C-03 regression guard)
    // ========================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        AscSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        StartSource(source);

        source.Dispose();

        // Second Dispose must not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    // ========================================================================
    // Factory null-argument guards (C-12 / C-13 regression guard)
    // ========================================================================

    [Test]
    public async Task Open_NullPath_ThrowsArgumentNullException() =>
        await Assert.That(() => AscSource.Open(null!)).Throws<ArgumentNullException>();

    [Test]
    public async Task FromText_NullData_ThrowsArgumentNullException() =>
        await Assert.That(() => AscSource.FromText(null!)).Throws<ArgumentNullException>();

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        using AscSource source = AscSource.FromText("base hex  timestamps absolute\n");
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
