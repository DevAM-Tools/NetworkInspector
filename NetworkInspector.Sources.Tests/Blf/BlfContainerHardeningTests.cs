// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Hostile file-header and container-header values: the readers must reject or count them, never misread the stream.
/// </summary>
internal sealed class BlfContainerHardeningTests
{
    private static readonly byte[] _CanFrame = TestFrames.BuildSocketCanClassic(0x123, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);

    private static BlfCaptureBuilder _InnerCanFrames(int count)
    {
        BlfCaptureBuilder inner = new();
        for (int i = 0; i < count; i++)
        {
            inner.AddCanFrame(1, _CanFrame, (i + 1) * 1_000_000L);
        }

        return inner;
    }

    [Test]
    [Arguments(0x7FFFFFFFu)]
    [Arguments(0x80000000u)]
    [Arguments(0x80000090u)]
    [Arguments(0xFFFFFFF0u)]
    [Arguments(0xFFFFFFFFu)]
    public async Task StreamSourceRejectsHeaderSizeThatCannotBeABuffer(uint headerSize)
    {
        byte[] data = new BlfCaptureBuilder().AddCanFrame(1, _CanFrame, 1_000_000).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), headerSize);

        using BlfStreamSource source = BlfStreamSource.FromStream(new MemoryStream(data), "huge-header.blf");
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(() => source.NextFrame()).Throws<BlfException>();
    }

    [Test]
    public async Task StreamSourceStillSkipsAnOrdinaryExtendedHeader()
    {
        // A 160-byte header: 16 extra bytes that are read and discarded before the first object.
        byte[] plain = new BlfCaptureBuilder().AddCanFrame(1, _CanFrame, 1_000_000).Build();
        byte[] data = new byte[plain.Length + 16];
        plain.AsSpan(0, 144).CopyTo(data);
        plain.AsSpan(144).CopyTo(data.AsSpan(160));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 160);

        using BlfStreamSource source = BlfStreamSource.FromStream(new MemoryStream(data), "extended-header.blf");
        SourceTestFixture.InitializeAndStartSource(source);
        Frame? frame = source.NextFrame();

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Value.Data.Span.SequenceEqual(_CanFrame)).IsTrue();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(7u)]
    [Arguments(9u)]
    public async Task UncompressedContainerDeclaringTheWrongSizeIsASizeMismatch(uint declared)
    {
        byte[] payload = new byte[8];

        BlfException? ex = await Assert.That(() => BlfContainer.Decompress(payload, BlfConstants.CompressionNone, declared))
            .Throws<BlfException>();

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task UncompressedContainerDeclaringTheExactSizeIsCopied()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        byte[] copy = BlfContainer.Decompress(payload, BlfConstants.CompressionNone, 8);

        await Assert.That(copy.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task StreamSourceCountsAnUncompressedContainerWithWrongSizeAsSkipped()
    {
        byte[] data = new BlfCaptureBuilder()
            .AddLogContainerWithWrongSize(BlfConstants.CompressionNone, _InnerCanFrames(3), 5)
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(2))
            .Build();

        using BlfStreamSource source = BlfStreamSource.FromStream(new MemoryStream(data), "wrong-size.blf");
        SourceTestFixture.InitializeAndStartSource(source);
        int frames = 0;
        while (source.NextFrame() is not null)
        {
            frames++;
        }

        await Assert.That(frames).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount).IsEqualTo(1);
        await Assert.That(source.HasErrors).IsTrue();
    }

    [Test]
    public async Task FileSourceCountsAnUncompressedContainerWithWrongSizeAsSkipped()
    {
        byte[] data = new BlfCaptureBuilder()
            .AddLogContainerWithWrongSize(BlfConstants.CompressionNone, _InnerCanFrames(3), 5)
            .AddLogContainer(BlfConstants.CompressionNone, _InnerCanFrames(2))
            .Build();

        using BlfSource source = BlfSource.FromData(data, "wrong-size.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        SourceTestFixture.InitializeAndStartSource(source);
        int frames = 0;
        while (source.NextFrame() is not null)
        {
            frames++;
        }

        await Assert.That(frames).IsEqualTo(2);
        await Assert.That(source.SkippedFrameCount).IsGreaterThan(0);
    }
}
