// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// <see cref="BlfSource.FramesById"/> returns, for every id, the frame <see cref="BlfSource.FrameById"/> returns: over the
/// equivalence corpus (uncompressed, zlib, and LZ4 containers, raw objects, split objects, a container that fails to
/// decompress), with and without the container cache, in every id order and length, for every backend and scan mode.
/// Misuse is rejected and altered bytes fail closed like the single read.
/// </summary>
internal sealed class BlfFramesByIdTests
{
    private const int _FileHeaderSize = 144;

    #region Identical

    [Test]
    [MethodDataSource(typeof(BlfRandomAccessEquivalenceTests), nameof(BlfRandomAccessEquivalenceTests.Corpus))]
    public async Task BatchEqualsPerIdReadsForEveryBackendScanModeCacheAndOrder(string name, byte[] data)
    {
        List<string> mismatches = [];
        foreach (ScanMode mode in new[] { ScanMode.Lazy, ScanMode.Full })
        {
            foreach (int cache in new[] { 0, new BlfSourceOptions().CacheBudget })
            {
                // In-memory data.
                BlfSourceOptions memoryOptions = new() { ScanMode = mode, CacheBudget = cache };
                using (BlfSource memory = BlfSource.FromData(data, name, memoryOptions))
                {
                    List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(memory);
                    _CollectMismatches(memory, frames.Count, $"{name}/data/{mode}/{cache}", mismatches);
                }

                // File backends: preloaded and memory-mapped.
                using TempCaptureFile file = new(data, ".blf");
                foreach (long? budget in new long?[] { long.MaxValue, null })
                {
                    BlfSourceOptions options = new() { ScanMode = mode, CacheBudget = cache, PreloadBudget = budget };
                    using BlfSource source = BlfSource.Open(file.Path, options);
                    List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
                    _CollectMismatches(source, frames.Count, $"{name}/{(budget is null ? "mapped" : "preload")}/{mode}/{cache}", mismatches);
                }
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task BeforeStartTheBatchThrowsInvalidOperationExceptionLikeFrameById()
    {
        using BlfSource source = BlfSource.FromData(_Build(containerCount: 1, framesPerContainer: 4, _CompressionNone), "before-start");
        Frame?[] frames = new Frame?[2];

        await Assert.That(() => source.FramesById(new FrameId(0), frames)).Throws<InvalidOperationException>();
        await Assert.That(() => source.FrameById(new FrameId(0))).Throws<InvalidOperationException>();
    }

    #endregion

    #region Misuse

    [Test]
    public async Task RangeAcrossTheEndOfTheSourceAndTheIdRangeReadsNullEntries()
    {
        using BlfSource source = BlfSource.FromData(_Build(1, 4, _CompressionNone), "past-the-end");
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(source);
        Frame?[] tail = new Frame?[5];
        Frame?[] beyond = new Frame?[3];

        int tailFound = source.FramesById(new FrameId(2), tail);
        int beyondFound = source.FramesById(new FrameId(ArrayIndexIdRange.MaxValue), beyond);

        await Assert.That(tailFound).IsEqualTo(2);
        await Assert.That(RandomAccessEquivalence.SameFrame(sequential[2], tail[0]!.Value)).IsTrue();
        await Assert.That(RandomAccessEquivalence.SameFrame(sequential[3], tail[1]!.Value)).IsTrue();
        await Assert.That(tail.Skip(2).All(static frame => frame is null)).IsTrue();
        await Assert.That(beyondFound).IsEqualTo(0);
        await Assert.That(beyond.All(static frame => frame is null)).IsTrue();
    }

    [Test]
    public async Task ACancelledTokenThrowsAndWritesNothing()
    {
        using BlfSource source = BlfSource.FromData(_Build(1, 4, _CompressionNone), "cancelled");
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(source);
        Frame sentinel = sequential[0];
        Frame?[] frames = [sentinel, sentinel];
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException? thrown = null;
        try
        {
            _ = source.FramesById(new FrameId(1), frames, cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            thrown = exception;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(frames[0]!.Value.Id.Value).IsEqualTo(sentinel.Id.Value);
        await Assert.That(frames[1]!.Value.Id.Value).IsEqualTo(sentinel.Id.Value);
    }

    [Test]
    public async Task AfterDisposeTheBatchThrowsObjectDisposedException()
    {
        BlfSource source = BlfSource.FromData(_Build(1, 4, _CompressionNone), "disposed");
        _ = RandomAccessEquivalence.StartAndReadAll(source);
        source.Dispose();
        Frame?[] frames = new Frame?[2];

        await Assert.That(() => source.FramesById(new FrameId(1), frames)).Throws<ObjectDisposedException>();
    }

    #endregion

    #region Fail closed

    [Test]
    public async Task AlteredObjectSignatureGivesANullEntryInABatch()
    {
        // Arrange: uncompressed containers and no cache, so the batch re-reads the container bytes from the data.
        byte[] data = _Build(containerCount: 3, framesPerContainer: 4, _CompressionNone);
        using BlfSource source = BlfSource.FromData(data, "fail-closed", new BlfSourceOptions { ScanMode = ScanMode.Full, CacheBudget = 0 });
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        byte[][] originals = [.. frames.Select(static f => f.Data.ToArray())];

        // Act: break the LOBJ signature of the object of frame 7.
        data[_NthInnerObjectSignature(data, 7)] = (byte)'X';
        Frame?[] batch = new Frame?[frames.Count];
        int found = source.FramesById(new FrameId(0), batch);

        // Assert
        await Assert.That(found).IsEqualTo(frames.Count - 1);
        await Assert.That(batch[7]).IsNull();
        List<int> wrong = [];
        for (int id = 0; id < frames.Count; id++)
        {
            if (id != 7 && (batch[id] is not { } frame || !frame.Data.Span.SequenceEqual(originals[id])))
            {
                wrong.Add(id);
            }
        }

        await Assert.That(wrong).IsEmpty();
    }

    #endregion

    #region Helpers

    private const ushort _CompressionNone = 0;

    private static void _CollectMismatches(BlfSource source, int frameCount, string label, List<string> mismatches)
    {
        foreach ((string name, FrameId first, int length) in RandomAccessEquivalence.BatchRanges(frameCount))
        {
            string? mismatch = RandomAccessEquivalence.FindBatchMismatch(source, first, length, $"{label}/{name}");
            if (mismatch is not null)
            {
                mismatches.Add(mismatch);
            }
        }
    }

    private static byte[] _Build(int containerCount, int framesPerContainer, ushort method)
    {
        BlfCaptureBuilder outer = new();
        int index = 0;
        for (int container = 0; container < containerCount; container++)
        {
            BlfCaptureBuilder inner = new();
            for (int k = 0; k < framesPerContainer; k++)
            {
                _ = inner.AddCanFrame(1, TestFrames.BuildSocketCanClassic(0x100u + (uint)index, [(byte)index, 2, 3]), (index + 1) * 1_000L);
                index++;
            }

            _ = outer.AddLogContainer(method, inner);
        }

        return outer.Build();
    }

    private static int _NthInnerObjectSignature(byte[] data, int n)
    {
        int found = -1;
        for (int offset = _FileHeaderSize; offset + 16 <= data.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != 0x4A42_4F4C
                || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12)) != 1)
            {
                continue;
            }

            found++;
            if (found == n)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("Inner object not found.");
    }

    #endregion
}
