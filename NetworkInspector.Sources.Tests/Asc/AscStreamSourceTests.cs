// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Integration tests for <see cref="AscStreamSource"/> verifying end-to-end
/// streaming from a text byte stream through to <see cref="Frame"/> objects.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscStreamSourceTests
{
    /// <summary>Safety guard to prevent infinite loops in test helpers.</summary>
    private const int MaxFrameGuard = 10_000;

    #region Helpers

    /// <summary>
    /// Creates an <see cref="AscStreamSource"/> from inline ASC text.
    /// Uses explicit newlines to avoid raw-string-literal indentation ambiguity.
    /// </summary>
    private static AscStreamSource CreateFromText(string ascContent, AscSourceOptions? options = null)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(ascContent);
        MemoryStream ms = new(bytes, writable: false);
        return AscStreamSource.FromStream(ms, "test.asc", leaveOpen: false, options);
    }



    /// <summary>
    /// Drains all frames from the source with a safety guard against infinite loops.
    /// </summary>
    private static List<Frame> DrainFrames(AscStreamSource source)
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
    // Empty / header-only streams
    // ========================================================================

    [Test]
    public async Task EmptyStream_ProducesNoFrames()
    {
        using AscStreamSource source = CreateFromText("");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame.HasValue).IsFalse();
    }

    [Test]
    public async Task HeaderOnly_ProducesNoFrames()
    {
        using AscStreamSource source = CreateFromText(
            "date Sun Nov 24 11:44:00 AM 2019\n" +
            "base hex timestamps absolute\n" +
            "internal events logged\n");
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();

        await Assert.That(frame.HasValue).IsFalse();
    }

    // ========================================================================
    // Single frame
    // ========================================================================

    [Test]
    public async Task SingleCanMessage_ProducesOneFrame()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
    }

    // ========================================================================
    // Multi-bus file (CAN + CAN FD + LIN + FlexRay + Ethernet)
    // ========================================================================

    [Test]
    public async Task MultiBus_AllFramesParsed()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 123 Rx d 8 AA BB CC DD EE FF 00 11\n" +
            "0.200000 CANFD 1 Rx 200 1 0 8 8 01 02 03 04 05 06 07 08\n" +
            "0.300000 L1 3C Rx 8 01 02 03 04 05 06 07 08 checksum = F0\n" +
            "0.400000 Fr 1 V9 0A 4 0 0 1234 x 8 0102030405060708\n" +
            "0.500000 ETH 1 Rx 14:001122334455667788990A0B0C0D\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(5);
        await Assert.That(frames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[1].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(frames[2].LinkType).IsEqualTo(LinkType.Lin);
        await Assert.That(frames[3].LinkType).IsEqualTo(LinkType.Flexray);
        await Assert.That(frames[4].LinkType).IsEqualTo(LinkType.Ethernet);
    }

    // ========================================================================
    // Sequential frame IDs
    // ========================================================================

    [Test]
    public async Task FrameIds_AreSequential()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames[0].Id.Value).IsEqualTo(0);
        await Assert.That(frames[1].Id.Value).IsEqualTo(1);
        await Assert.That(frames[2].Id.Value).IsEqualTo(2);
    }

    // ========================================================================
    // Interface registration
    // ========================================================================

    [Test]
    public async Task DifferentChannels_DifferentInterfaceIds()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 2 200 Rx d 2 03 04\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].InterfaceId).IsNotEqualTo(frames[1].InterfaceId);
    }

    [Test]
    public async Task SameChannel_SameInterfaceId()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].InterfaceId).IsEqualTo(frames[1].InterfaceId);
    }

    // ========================================================================
    // Comments and empty lines
    // ========================================================================

    [Test]
    public async Task CommentsAndEmptyLines_AreSkipped()
    {
        using AscStreamSource source = CreateFromText(
            "; comment line\n" +
            "// another comment\n" +
            "base hex\n" +
            "\n" +
            "Begin Triggerblock\n" +
            "; comment inside data\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(2);
    }

    // ========================================================================
    // Error tolerance
    // ========================================================================

    [Test]
    public async Task TolerantMode_SkipsMalformedLines()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 GARBAGE LINE CANNOT PARSE\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n",
            new AscSourceOptions { ErrorTolerance = ErrorToleranceMode.Tolerant });
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        // The garbage line is not a frame-producing type — it is skipped as unknown
        await Assert.That(frames.Count).IsEqualTo(2);
    }

    // ========================================================================
    // Read frame count
    // ========================================================================

    [Test]
    public async Task ReadFrameCount_TracksSuccessfulReads()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n" +
            "0.300000 1 300 Rx d 2 05 06\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);
        DrainFrames(source);

        await Assert.That(source.ReadFrameCount).IsEqualTo(3);
    }

    // ========================================================================
    // EstimatedFrameCount is always null for streams
    // ========================================================================

    [Test]
    public async Task EstimatedFrameCount_IsNull()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.EstimatedFrameCount).IsNull();
    }

    // ========================================================================
    // IsRunning state
    // ========================================================================

    [Test]
    public async Task IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        AscStreamSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();

        await Assert.That(source.IsRunning).IsFalse();
    }

    // ========================================================================
    // Disposed source throws
    // ========================================================================

    [Test]
    public async Task NextFrame_AfterDispose_Throws()
    {
        AscStreamSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        SourceTestFixture.InitializeAndStartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    // ========================================================================
    // Decimal base
    // ========================================================================

    [Test]
    public async Task DecimalBase_DataParsedCorrectly()
    {
        using AscStreamSource source = CreateFromText(
            "base dec\n" +
            "Begin Triggerblock\n" +
            "0.100000 1 291 Rx d 2 10 20\n" +
            "End TriggerBlock\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        await Assert.That(frames.Count).IsEqualTo(1);

        // CAN ID: decimal 291 = 0x123, stored in big-endian SocketCAN header
        uint id = BinaryPrimitives.ReadUInt32BigEndian(frames[0].Data.Span);
        await Assert.That(id & 0x1FFFFFFFu).IsEqualTo(291u);

        // Data bytes: decimal 10 and 20
        await Assert.That(frames[0].Data.Span[8]).IsEqualTo((byte)10);
        await Assert.That(frames[0].Data.Span[9]).IsEqualTo((byte)20);
    }

    // ========================================================================
    // Data without trigger block (immediate data after header)
    // ========================================================================

    [Test]
    public async Task NoTriggerBlock_FirstFrameCaptured()
    {
        using AscStreamSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n" +
            "0.200000 1 200 Rx d 2 03 04\n");
        SourceTestFixture.InitializeAndStartSource(source);

        List<Frame> frames = DrainFrames(source);

        // Both frames should be captured, including the first one
        // parsed during header scanning (stored as _PendingFirstFrame)
        await Assert.That(frames.Count).IsEqualTo(2);
    }

    // ========================================================================
    // UiName defaults
    // ========================================================================

    [Test]
    public async Task UiName_MatchesParameter()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("base hex\n");
        using MemoryStream ms = new(bytes, writable: false);
        using AscStreamSource source = AscStreamSource.FromStream(ms, "my-name.asc");

        await Assert.That(source.UiName).IsEqualTo("my-name.asc");
    }

    // ========================================================================
    // Double-Dispose idempotency (H-02 / C-06 regression guard)
    // ========================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        AscStreamSource source = CreateFromText(
            "base hex\n" +
            "0.100000 1 100 Rx d 2 01 02\n");
        SourceTestFixture.InitializeAndStartSource(source);

        source.Dispose();

        // Second Dispose must not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    [Test]
    public async Task IsRunning_FalseBeforeStart()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("base hex\n");
        using MemoryStream ms = new(bytes, writable: false);
        using AscStreamSource source = AscStreamSource.FromStream(ms);

        await Assert.That(source.IsRunning).IsFalse();
    }

    // ========================================================================
    // Lifecycle guards — pre-Start contract
    // ========================================================================

    [Test]
    public async Task NextFrame_BeforeStart_ThrowsInvalidOperationException()
    {
        using AscStreamSource source = CreateFromText("base hex\n0.100000 1 100 Rx d 2 01 02\n");

        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        using AscStreamSource source = CreateFromText("base hex\n");
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
