// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Random;

/// <summary>
/// Ranges of the random source options (frame size, TCP payload size, timestamp interval), the CAN FD length byte, and the
/// seed namespaces of the TCP stream generator.
/// </summary>
internal sealed class RandomOptionsRangeTests
{
    #region Helpers

    private static Frame _FirstFrame(RandomSourceOptions options)
    {
        using RandomFrameSource source = new(options);
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();
        return frame
            ?? throw new InvalidOperationException("The source produced no frame.");
    }

    #endregion

    #region Frame size per mode

    [Test]
    [Arguments(RandomFrameMode.IPv4, 65549)]
    [Arguments(RandomFrameMode.UdpIPv4, 65549)]
    [Arguments(RandomFrameMode.TcpIPv4, 65549)]
    [Arguments(RandomFrameMode.IPv6, 65589)]
    [Arguments(RandomFrameMode.UdpIPv6, 65589)]
    [Arguments(RandomFrameMode.TcpIPv6, 65589)]
    public async Task MaxFrameSizeAboveTheLengthFieldIsRejectedWithItsRange(RandomFrameMode mode, int limit)
    {
        RandomSourceOptions tooBig = new() { Mode = mode, MaxFrameSize = limit + 1, FrameCount = 1 };
        RandomSourceOptions atLimit = new() { Mode = mode, MaxFrameSize = limit, FrameCount = 1 };

        ArgumentOutOfRangeException? error = await Assert.That(() => new RandomFrameSource(tooBig))
            .Throws<ArgumentOutOfRangeException>();
        using RandomFrameSource source = new(atLimit);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Message).Contains(limit.ToString(CultureInfo.InvariantCulture));
        await Assert.That(error.ParamName).IsEqualTo("MaxFrameSize");
    }

    [Test]
    [Arguments(RandomFrameMode.FullRandom)]
    [Arguments(RandomFrameMode.Ethernet)]
    public async Task ModesWithoutALengthFieldAreBoundedByTheLargestArray(RandomFrameMode mode)
    {
        RandomSourceOptions tooBig = new() { Mode = mode, MaxFrameSize = int.MaxValue, FrameCount = 1 };
        RandomSourceOptions atLimit = new() { Mode = mode, MaxFrameSize = Array.MaxLength, FrameCount = 1 };

        ArgumentOutOfRangeException? error = await Assert.That(() => new RandomFrameSource(tooBig))
            .Throws<ArgumentOutOfRangeException>();
        using RandomFrameSource source = new(atLimit);

        await Assert.That(error!.Message).Contains(Array.MaxLength.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments(RandomFrameMode.Can)]
    [Arguments(RandomFrameMode.CanFd)]
    [Arguments(RandomFrameMode.TcpStreamIPv4)]
    [Arguments(RandomFrameMode.TcpStreamIPv6)]
    public async Task ModesThatIgnoreTheFrameSizeAcceptAnyMaxFrameSize(RandomFrameMode mode)
    {
        using RandomFrameSource source = new(new RandomSourceOptions { Mode = mode, MaxFrameSize = int.MaxValue, FrameCount = 1 });

        await Assert.That(source.UiName).IsNotNull();
    }

    [Test]
    public async Task UdpIPv4FrameAtTheLargestSizeKeepsEveryLengthFieldExact()
    {
        Frame frame = _FirstFrame(new RandomSourceOptions
        {
            Mode = RandomFrameMode.UdpIPv4,
            MinFrameSize = 65549,
            MaxFrameSize = 65549,
            FrameCount = 1,
            Seed = 1,
        });

        int length = frame.Data.Length;
        ushort totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Data.Span[16..]);
        ushort udpLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Data.Span[38..]);

        await Assert.That(length).IsEqualTo(65549);
        await Assert.That(totalLength).IsEqualTo((ushort)65535);
        await Assert.That(udpLength).IsEqualTo((ushort)(65549 - 34));
    }

    [Test]
    public async Task TcpIPv6FrameAtTheLargestSizeKeepsThePayloadLengthExact()
    {
        Frame frame = _FirstFrame(new RandomSourceOptions
        {
            Mode = RandomFrameMode.TcpIPv6,
            MinFrameSize = 65589,
            MaxFrameSize = 65589,
            FrameCount = 1,
            Seed = 1,
        });

        await Assert.That(frame.Data.Length).IsEqualTo(65589);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.Data.Span[18..])).IsEqualTo((ushort)65535);
    }

    #endregion

    #region TCP stream payload size

    [Test]
    [Arguments(RandomFrameMode.TcpStreamIPv4, 65495)]
    [Arguments(RandomFrameMode.TcpStreamIPv6, 65515)]
    public async Task TcpStreamPayloadAboveTheIpLengthFieldIsRejectedWithItsRange(RandomFrameMode mode, int limit)
    {
        RandomSourceOptions tooBig = new()
        {
            Mode = mode,
            TcpStreamOptions = new TcpStreamOptions { MinPayloadSize = 0, MaxPayloadSize = limit + 1 },
        };

        ArgumentOutOfRangeException? error = await Assert.That(() => new RandomFrameSource(tooBig))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(error!.Message).Contains(limit.ToString(CultureInfo.InvariantCulture));
        await Assert.That(error.ParamName).IsEqualTo("MaxPayloadSize");
    }

    [Test]
    public async Task TcpStreamIPv4PayloadAtTheLimitKeepsTheTotalLengthExact()
    {
        Frame frame = _FirstFrame(new RandomSourceOptions
        {
            Mode = RandomFrameMode.TcpStreamIPv4,
            Seed = 5,
            TcpStreamOptions = new TcpStreamOptions
            {
                StreamCount = 1,
                SegmentsPerStream = 1,
                MinPayloadSize = 65495,
                MaxPayloadSize = 65495,
                IncludeHandshake = false,
                IncludeTeardown = false,
            },
        });

        await Assert.That(frame.Data.Length).IsEqualTo(14 + 20 + 20 + 65495);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.Data.Span[16..])).IsEqualTo((ushort)65535);
    }

    [Test]
    public async Task TcpStreamIPv6PayloadAtTheLimitKeepsThePayloadLengthExact()
    {
        Frame frame = _FirstFrame(new RandomSourceOptions
        {
            Mode = RandomFrameMode.TcpStreamIPv6,
            Seed = 5,
            TcpStreamOptions = new TcpStreamOptions
            {
                StreamCount = 1,
                SegmentsPerStream = 1,
                MinPayloadSize = 65515,
                MaxPayloadSize = 65515,
                IncludeHandshake = false,
                IncludeTeardown = false,
            },
        });

        await Assert.That(frame.Data.Length).IsEqualTo(14 + 40 + 20 + 65515);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.Data.Span[18..])).IsEqualTo((ushort)65535);
    }

    #endregion

    #region Timestamp interval

    [Test]
    public async Task TimestampIntervalAboveTheProductLimitIsRejectedWithTheMaximum()
    {
        // 1000 frames: the largest index is 999, so the interval may be at most long.MaxValue / 999.
        long maxInterval = long.MaxValue / 999;
        RandomSourceOptions tooBig = new() { FrameCount = 1000, TimestampInterval = maxInterval + 1 };
        RandomSourceOptions atLimit = new() { FrameCount = 1000, TimestampInterval = maxInterval };

        ArgumentOutOfRangeException? error = await Assert.That(() => new RandomFrameSource(tooBig))
            .Throws<ArgumentOutOfRangeException>();
        using RandomFrameSource source = new(atLimit);

        await Assert.That(error!.Message).Contains(maxInterval.ToString(CultureInfo.InvariantCulture));
        await Assert.That(error.ParamName).IsEqualTo("TimestampInterval");
    }

    [Test]
    public async Task UnlimitedSourceBoundsTheIntervalByTheLargestFrameId()
    {
        long maxInterval = long.MaxValue / ArrayIndexIdRange.MaxValue;
        RandomSourceOptions tooBig = new() { FrameCount = 0, TimestampInterval = maxInterval + 1 };
        RandomSourceOptions atLimit = new() { FrameCount = 0, TimestampInterval = maxInterval };

        await Assert.That(() => new RandomFrameSource(tooBig)).Throws<ArgumentOutOfRangeException>();
        using RandomFrameSource source = new(atLimit);

        await Assert.That(source.UiName).IsNotNull();
    }

    [Test]
    public async Task TimestampsAtTheLargestIntervalDoNotWrap()
    {
        long maxInterval = long.MaxValue / 99;
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 100,
            TimestampInterval = maxInterval,
            BaseTimestamp = new Timestamp(0),
            Seed = 3,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? last = null;
        for (int i = 0; i < 100; i++)
        {
            last = source.NextFrame();
        }

        await Assert.That(last).IsNotNull();
        await Assert.That(last!.Value.Timestamp.AsNanos).IsEqualTo(maxInterval * 99);
    }

    [Test]
    public async Task TimestampSaturatesWhenTheBaseLeavesNoRoomInsteadOfWrapping()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 3,
            TimestampInterval = 1_000,
            BaseTimestamp = new Timestamp(long.MaxValue - 1_500),
            Seed = 3,
        });
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? first = source.NextFrame();
        Frame? second = source.NextFrame();
        Frame? third = source.NextFrame();
        Frame? byId = source.FrameById(new FrameId(2));

        await Assert.That(first!.Value.Timestamp.AsNanos).IsEqualTo(long.MaxValue - 1_500);
        await Assert.That(second!.Value.Timestamp.AsNanos).IsEqualTo(long.MaxValue - 500);
        await Assert.That(third!.Value.Timestamp.AsNanos).IsEqualTo(long.MaxValue);
        await Assert.That(byId!.Value.Timestamp.AsNanos).IsEqualTo(long.MaxValue);
    }

    #endregion

    #region CAN FD length byte

    [Test]
    public async Task CanFdStoresThePayloadByteCountNotTheDlcCode()
    {
        using RandomFrameSource source = new(new RandomSourceOptions { Mode = RandomFrameMode.CanFd, FrameCount = 400, Seed = 11 });
        SourceTestFixture.InitializeAndStartSource(source);
        HashSet<int> lengthsSeen = [];

        for (int i = 0; i < 400; i++)
        {
            Frame frame = source.NextFrame()!.Value;
            int count = frame.Data.Length - 8;

            await Assert.That((int)frame.Data.Span[4]).IsEqualTo(count);
            _ = lengthsSeen.Add(count);
        }

        // Every length of the FD table shows up in 400 draws (16 values), including the ones above 8 that differ from the code.
        await Assert.That(lengthsSeen.Contains(12)).IsTrue();
        await Assert.That(lengthsSeen.Contains(64)).IsTrue();
    }

    [Test]
    public async Task CanFdFrameByIdMatchesTheSequentialFrame()
    {
        using RandomFrameSource source = new(new RandomSourceOptions { Mode = RandomFrameMode.CanFd, FrameCount = 50, Seed = 11 });
        SourceTestFixture.InitializeAndStartSource(source);

        for (int i = 0; i < 50; i++)
        {
            Frame sequential = source.NextFrame()!.Value;
            Frame random = source.FrameById(new FrameId(i))!.Value;

            await Assert.That(random.Data.Span.SequenceEqual(sequential.Data.Span)).IsTrue();
        }
    }

    #endregion

    #region Registry snapshot

    [Test]
    public async Task NextFrameAfterDisposeThrowsObjectDisposed()
    {
        RandomFrameSource source = new(new RandomSourceOptions { FrameCount = 3, Seed = 1 });
        SourceTestFixture.InitializeAndStartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<ObjectDisposedException>();
    }

    #endregion

    #region TCP seed namespaces

    [Test]
    [Arguments(0, 0)]
    [Arguments(3, 7)]
    [Arguments(65535, 65535)]
    public async Task SeedIdsOfSmallIndexesKeepTheirHistoricalValues(int stream, int segment)
    {
        ulong size = TcpStreamFrameBuilder.SegmentSeedId(TcpStreamFrameBuilder.PayloadSizeNamespace, stream, segment);
        ulong bytes = TcpStreamFrameBuilder.SegmentSeedId(TcpStreamFrameBuilder.PayloadBytesNamespace, stream, segment);

        await Assert.That(size).IsEqualTo(0x2_0000_0000UL + ((ulong)stream << 16) + (ulong)segment);
        await Assert.That(bytes).IsEqualTo(0x3_0000_0000UL + ((ulong)stream << 16) + (ulong)segment);
    }

    [Test]
    public async Task SeedIdsOfLargeIndexesNeverCollide()
    {
        // The pairs that collided in the historical layout: the stream bits overlap the segment bits from 65 536 on, and
        // the size namespace of stream s + 65 536 equals the bytes namespace of stream s.
        (int Stream, int Segment)[] pairs =
        [
            (0, 0), (0, 1), (1, 0), (0, 65535), (0, 65536), (1, 0), (1, 65536), (65535, 0), (65536, 0), (65536, 1),
            (65537, 0), (0, 131072), (2, 0), (70000, 70000), (int.MaxValue, 0), (0, int.MaxValue), (int.MaxValue, int.MaxValue),
        ];
        HashSet<ulong> ids = [];
        int expected = 0;

        foreach ((int stream, int segment) in pairs.Distinct())
        {
            _ = ids.Add(TcpStreamFrameBuilder.SegmentSeedId(TcpStreamFrameBuilder.PayloadSizeNamespace, stream, segment));
            _ = ids.Add(TcpStreamFrameBuilder.SegmentSeedId(TcpStreamFrameBuilder.PayloadBytesNamespace, stream, segment));
            expected += 2;
        }

        await Assert.That(ids.Count).IsEqualTo(expected);
    }

    [Test]
    public async Task LargeStreamAndSegmentIndexesStayOutOfEveryHistoricalRange()
    {
        ulong id = TcpStreamFrameBuilder.SegmentSeedId(TcpStreamFrameBuilder.PayloadSizeNamespace, 65536, 0);

        // Historical ids (frames, endpoints, sizes, bytes) are all below 4 x 2^32.
        await Assert.That(id).IsGreaterThan(0x4_0000_0000UL);
    }

    #endregion
}
