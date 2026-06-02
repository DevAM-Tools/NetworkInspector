// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Random;

/// <summary>
/// Tests for <see cref="RandomFrameSource"/> — synthetic frame generation.
/// Verifies determinism (same seed → same data), all eight modes, frame count limits,
/// timestamp spacing, and lifecycle management.
/// </summary>
internal sealed class RandomFrameSourceTests
{


    // ========================================================================
    // Determinism — same seed produces identical frames
    // ========================================================================

    [Test]
    public async Task SameSeed_ProducesIdenticalFrames()
    {
        const ulong seed = 12345;
        const int count = 10;

        using RandomFrameSource source1 = new(count, seed);
        SourceTestFixture.InitializeAndStartSource(source1);

        using RandomFrameSource source2 = new(count, seed);
        SourceTestFixture.InitializeAndStartSource(source2);

        for (int i = 0; i < count; i++)
        {
            Frame? f1 = source1.NextFrame();
            Frame? f2 = source2.NextFrame();

            await Assert.That(f1).IsNotNull();
            await Assert.That(f2).IsNotNull();
            await Assert.That(f1!.Value.Data.Span.SequenceEqual(f2!.Value.Data.Span)).IsTrue();
        }
    }

    [Test]
    public async Task DifferentSeed_ProducesDifferentFrames()
    {
        using RandomFrameSource source1 = new(1, seed: 42);
        SourceTestFixture.InitializeAndStartSource(source1);

        using RandomFrameSource source2 = new(1, seed: 99);
        SourceTestFixture.InitializeAndStartSource(source2);

        Frame? f1 = source1.NextFrame();
        Frame? f2 = source2.NextFrame();

        await Assert.That(f1).IsNotNull();
        await Assert.That(f2).IsNotNull();
        // Different seeds should produce different data (with overwhelming probability)
        await Assert.That(f1!.Value.Data.Span.SequenceEqual(f2!.Value.Data.Span)).IsFalse();
    }

    // ========================================================================
    // Frame count limit
    // ========================================================================

    [Test]
    public async Task FrameCount_ReturnsExactCount()
    {
        const int count = 5;
        using RandomFrameSource source = new(count, seed: 42);
        SourceTestFixture.InitializeAndStartSource(source);

        for (int i = 0; i < count; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Id.Value).IsEqualTo(i);
        }

        // After count frames, should return null
        await Assert.That(source.NextFrame()).IsNull();
    }

    [Test]
    public async Task EstimatedFrameCount_ReflectsConfiguration()
    {
        using RandomFrameSource finite = new(100, seed: 42);
        await Assert.That(finite.EstimatedFrameCount).IsEqualTo(100);

        using RandomFrameSource unlimited = new(0, seed: 42);
        await Assert.That(unlimited.EstimatedFrameCount).IsNull();
    }

    // ========================================================================
    // Timestamp spacing
    // ========================================================================

    [Test]
    public async Task Timestamps_IncrementCorrectly()
    {
        Timestamp baseTs = new(1_000_000_000_000L);   // 1000 seconds
        long interval = 2_000_000L;                    // 2ms

        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 3,
            Seed = 42,
            BaseTimestamp = baseTs,
            TimestampInterval = interval,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? f0 = source.NextFrame();
        Frame? f1 = source.NextFrame();
        Frame? f2 = source.NextFrame();

        await Assert.That(f0).IsNotNull();
        await Assert.That(f1).IsNotNull();
        await Assert.That(f2).IsNotNull();

        await Assert.That(f0!.Value.Timestamp.AsNanos).IsEqualTo(baseTs.AsNanos);
        await Assert.That(f1!.Value.Timestamp.AsNanos).IsEqualTo(baseTs.AsNanos + interval);
        await Assert.That(f2!.Value.Timestamp.AsNanos).IsEqualTo(baseTs.AsNanos + 2 * interval);
    }

    // ========================================================================
    // All eight modes produce valid frames
    // ========================================================================

    [Test]
    [Arguments(RandomFrameMode.FullRandom)]
    [Arguments(RandomFrameMode.Ethernet)]
    [Arguments(RandomFrameMode.IPv4)]
    [Arguments(RandomFrameMode.IPv6)]
    [Arguments(RandomFrameMode.UdpIPv4)]
    [Arguments(RandomFrameMode.UdpIPv6)]
    [Arguments(RandomFrameMode.Can)]
    [Arguments(RandomFrameMode.CanFd)]
    public async Task AllModes_ProduceFrames(RandomFrameMode mode)
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 3,
            Seed = 42,
            Mode = mode,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        for (int i = 0; i < 3; i++)
        {
            Frame? frame = source.NextFrame();
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Value.Data.Length).IsGreaterThan(0);
        }

        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // CAN modes produce correct link type and frame size
    // ========================================================================

    [Test]
    public async Task CanMode_ProducesCorrectLinkTypeAndSize()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 1,
            Seed = 42,
            Mode = RandomFrameMode.Can,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        // Classic CAN: 8 header + 8 data = 16 bytes
        await Assert.That(frame.Value.Data.Length).IsEqualTo(16);
    }

    [Test]
    public async Task CanFdMode_ProducesCorrectLinkType()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 1,
            Seed = 42,
            Mode = RandomFrameMode.CanFd,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.CanSocketcan);
        // CAN FD: 8 header + variable data (0-64 bytes)
        await Assert.That(frame.Value.Data.Length).IsGreaterThanOrEqualTo(8);
        await Assert.That(frame.Value.Data.Length).IsLessThanOrEqualTo(72);
    }

    // ========================================================================
    // Ethernet modes produce correct link type
    // ========================================================================

    [Test]
    [Arguments(RandomFrameMode.Ethernet)]
    [Arguments(RandomFrameMode.IPv4)]
    [Arguments(RandomFrameMode.IPv6)]
    [Arguments(RandomFrameMode.UdpIPv4)]
    [Arguments(RandomFrameMode.UdpIPv6)]
    public async Task EthernetModes_ProduceEthernetLinkType(RandomFrameMode mode)
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 1,
            Seed = 42,
            Mode = mode,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? frame = source.NextFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.LinkType).IsEqualTo(LinkType.Ethernet);
    }

    // ========================================================================
    // Lifecycle
    // ========================================================================

    [Test]
    public async Task NextFrame_BeforeStart_Throws()
    {
        using RandomFrameSource source = new(1, seed: 42);
        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NextFrame_AfterDispose_Throws()
    {
        RandomFrameSource source = new(1, seed: 42);
        SourceTestFixture.InitializeAndStartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task IsRunning_LifecycleTransitions()
    {
        using RandomFrameSource source = new(1, seed: 42);
        await Assert.That(source.IsRunning).IsFalse();

        SourceTestFixture.InitializeAndStartSource(source);
        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        RandomFrameSource source = new(1, seed: 42);
        SourceTestFixture.InitializeAndStartSource(source);

        source.Dispose();

        // Second Dispose must be idempotent and not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    [Test]
    public async Task Start_AfterDispose_ThrowsObjectDisposedException()
    {
        RandomFrameSource source = new(1, seed: 42);
        source.Dispose();

        FrameInterfaceRegistry registry = new();
        FrameSourceId id = registry.RegisterSource(source);

        await Assert.That(() => source.Start(id, registry)).Throws<ObjectDisposedException>();
    }

    // ========================================================================
    // UiName
    // ========================================================================

    [Test]
    public async Task CustomUiName_IsUsed()
    {
        using RandomFrameSource source = new(1, seed: 42, uiName: "TestSource");
        await Assert.That(source.UiName).IsEqualTo("TestSource");
    }

    // ========================================================================
    // Lifecycle guards — null-registry contract
    // ========================================================================

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
        using RandomFrameSource source = new(1, seed: 42);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }
}
