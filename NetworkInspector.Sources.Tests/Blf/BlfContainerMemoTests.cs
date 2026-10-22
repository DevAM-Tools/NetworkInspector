// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// <see cref="BlfSource.FrameById"/> remembers, per reading thread, the last container it used so that ids of one container
/// do not queue on the container cache lock. These tests prove that the memo never serves bytes of another source or of
/// another container, that a cache budget of 0 still works, and that the memo survives the disposal of a source.
/// </summary>
internal sealed class BlfContainerMemoTests
{
    #region Constants

    private const int _FramesPerContainer = 200;
    private const int _ContainerCount = 3;

    #endregion

    #region Tests

    [Test]
    [Arguments(0)]
    [Arguments(64 * 1024)]
    public async Task AlternatingReadsOfTwoSourcesWithEqualContainerOffsetsNeverMixTheirBytes(int cacheBudget)
    {
        // Arrange: two files with the same structure (so the same container offsets) and different payload bytes.
        byte[] firstBytes = _Build(variant: 1);
        byte[] secondBytes = _Build(variant: 2);
        using TempCaptureFile firstFile = new(firstBytes, ".blf");
        using TempCaptureFile secondFile = new(secondBytes, ".blf");
        BlfSourceOptions options = new() { PreloadBudget = null, CacheBudget = cacheBudget };
        using BlfSource first = BlfSource.Open(firstFile.Path, options);
        using BlfSource second = BlfSource.Open(secondFile.Path, options);
        List<Frame> firstFrames = RandomAccessEquivalence.StartAndReadAll(first);
        List<Frame> secondFrames = RandomAccessEquivalence.StartAndReadAll(second);
        List<string> mismatches = [];

        // Act: one thread alternates between the sources id by id, and walks the containers back and forth.
        for (int round = 0; round < 3; round++)
        {
            for (int n = 0; n < firstFrames.Count; n++)
            {
                int id = round % 2 == 0 ? n : firstFrames.Count - 1 - n;
                Frame? a = first.FrameById(new FrameId(id));
                Frame? b = second.FrameById(new FrameId(id));
                if (a is null || !RandomAccessEquivalence.SameFrame(firstFrames[id], a.Value))
                {
                    mismatches.Add(Invariant($"first source, id {id}"));
                }

                if (b is null || !RandomAccessEquivalence.SameFrame(secondFrames[id], b.Value))
                {
                    mismatches.Add(Invariant($"second source, id {id}"));
                }
            }
        }

        // Assert: the two sources do differ, so a mixed-up memo would have been caught above.
        await Assert.That(firstFrames[5].Data.Span.SequenceEqual(secondFrames[5].Data.Span)).IsFalse();
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task ReadsAcrossContainersAndBackStayExact()
    {
        using TempCaptureFile file = new(_Build(variant: 3), ".blf");
        using BlfSource source = BlfSource.Open(file.Path, new BlfSourceOptions { PreloadBudget = null });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        System.Random random = new(5);
        List<string> mismatches = [];

        // Random ids jump between containers (every jump replaces the memo), repeated ids hit it.
        for (int n = 0; n < 5_000; n++)
        {
            int id = random.Next(frames.Count);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                Frame? frame = source.FrameById(new FrameId(id));
                if (frame is null || !RandomAccessEquivalence.SameFrame(frames[id], frame.Value))
                {
                    mismatches.Add(Invariant($"id {id}"));
                }
            }
        }

        await Assert.That(frames.Count).IsEqualTo(_FramesPerContainer * _ContainerCount);
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task DisposedSourceStillThrowsAndANewSourceOnTheSameThreadReadsItsOwnBytes()
    {
        using TempCaptureFile file = new(_Build(variant: 4), ".blf");
        BlfSource first = BlfSource.Open(file.Path, new BlfSourceOptions { PreloadBudget = null });
        List<Frame> firstFrames = RandomAccessEquivalence.StartAndReadAll(first);
        _ = first.FrameById(new FrameId(3));
        first.Dispose();
        using TempCaptureFile otherFile = new(_Build(variant: 5), ".blf");
        using BlfSource second = BlfSource.Open(otherFile.Path, new BlfSourceOptions { PreloadBudget = null });
        List<Frame> secondFrames = RandomAccessEquivalence.StartAndReadAll(second);

        Frame? frame = second.FrameById(new FrameId(3));

        await Assert.That(() => first.FrameById(new FrameId(3))).Throws<ObjectDisposedException>();
        await Assert.That(frame is not null && RandomAccessEquivalence.SameFrame(secondFrames[3], frame.Value)).IsTrue();
        await Assert.That(RandomAccessEquivalence.SameFrame(firstFrames[3], secondFrames[3])).IsFalse();
    }

    #endregion

    #region Helpers

    /// <summary>Three LZ4 containers of 200 CAN frames each; the payload bytes depend on <paramref name="variant"/>.</summary>
    private static byte[] _Build(byte variant)
    {
        BlfCaptureBuilder outer = new();
        for (int container = 0; container < _ContainerCount; container++)
        {
            BlfCaptureBuilder inner = new();
            for (int i = 0; i < _FramesPerContainer; i++)
            {
                int index = (container * _FramesPerContainer) + i;
                _ = inner.AddCanFrame(
                    1,
                    TestFrames.BuildSocketCanClassic(0x100u + (uint)(index & 0xFF), [variant, (byte)index, (byte)(index >> 8)]),
                    (index + 1) * 1_000L);
            }

            _ = outer.AddLogContainer(BlfConstants.CompressionLz4, inner);
        }

        return outer.Build();
    }

    #endregion
}
