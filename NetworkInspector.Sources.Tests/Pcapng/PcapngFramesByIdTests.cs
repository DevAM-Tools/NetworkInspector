// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// <see cref="PcapngSource.FramesById"/> returns, for every id, the frame <see cref="PcapngSource.FrameById"/> returns:
/// over the equivalence corpus, in every id order and length, for every backend and scan mode; it rejects misuse; and it
/// fails closed on altered bytes exactly like the single read.
/// </summary>
internal sealed class PcapngFramesByIdTests
{
    #region Identical

    [Test]
    [MethodDataSource(typeof(PcapngRandomAccessEquivalenceTests), nameof(PcapngRandomAccessEquivalenceTests.Corpus))]
    public async Task BatchEqualsPerIdReadsForEveryBackendScanModeAndOrder(string name, byte[] data)
    {
        List<string> mismatches = [];

        // In-memory data (always a full scan).
        using (PcapngSource fromData = PcapngSource.FromData(data, name))
        {
            List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(fromData);
            _CollectMismatches(fromData, frames.Count, $"{name}/data", mismatches);
        }

        // File backends: preloaded and memory-mapped, lazy (after the scan) and full.
        using TempCaptureFile file = new(data, ".pcapng");
        foreach (long? budget in new long?[] { long.MaxValue, null })
        {
            foreach (ScanMode mode in new[] { ScanMode.Lazy, ScanMode.Full })
            {
                PcapngSourceOptions options = new() { PreloadBudget = budget, ScanMode = mode };
                using PcapngSource source = PcapngSource.Open(file.Path, options);
                List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
                _CollectMismatches(source, frames.Count, $"{name}/{(budget is null ? "mapped" : "preload")}/{mode}", mismatches);
            }
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task BeforeStartEveryEntryIsNull()
    {
        using PcapngSource source = PcapngSource.FromData(_Capture(10), "before-start");
        Frame?[] frames = new Frame?[3];

        int found = source.FramesById(new FrameId(0), frames);

        await Assert.That(found).IsEqualTo(0);
        await Assert.That(frames.All(static frame => frame is null)).IsTrue();
    }

    #endregion

    #region Misuse

    [Test]
    public async Task RangeAcrossTheEndOfTheSourceAndTheIdRangeReadsNullEntries()
    {
        using PcapngSource source = PcapngSource.FromData(_Capture(10), "past-the-end");
        List<Frame> sequential = RandomAccessEquivalence.StartAndReadAll(source);
        Frame?[] tail = new Frame?[5];
        Frame?[] beyond = new Frame?[3];

        int tailFound = source.FramesById(new FrameId(8), tail);
        int beyondFound = source.FramesById(new FrameId(ArrayIndexIdRange.MaxValue), beyond);

        await Assert.That(tailFound).IsEqualTo(2);
        await Assert.That(RandomAccessEquivalence.SameFrame(sequential[8], tail[0]!.Value)).IsTrue();
        await Assert.That(RandomAccessEquivalence.SameFrame(sequential[9], tail[1]!.Value)).IsTrue();
        await Assert.That(tail.Skip(2).All(static frame => frame is null)).IsTrue();
        await Assert.That(beyondFound).IsEqualTo(0);
        await Assert.That(beyond.All(static frame => frame is null)).IsTrue();
    }

    [Test]
    public async Task ACancelledTokenThrowsAndWritesNothing()
    {
        using PcapngSource source = PcapngSource.FromData(_Capture(10), "cancelled");
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
        PcapngSource source = PcapngSource.FromData(_Capture(10), "disposed");
        _ = RandomAccessEquivalence.StartAndReadAll(source);
        source.Dispose();
        Frame?[] frames = new Frame?[2];

        await Assert.That(() => source.FramesById(new FrameId(1), frames)).Throws<ObjectDisposedException>();
    }

    #endregion

    #region Fail closed

    [Test]
    public async Task AlteredBytesGiveNullEntriesInABatch()
    {
        // Arrange: 100 frames from memory; keep a copy of every frame's bytes before the data changes.
        byte[] data = _Capture(100);
        using PcapngSource source = PcapngSource.FromData(data, "fail-closed");
        List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(source);
        byte[][] originals = [.. frames.Select(static f => f.Data.ToArray())];
        List<int> blocks = _EnhancedPacketBlockOffsets(data);

        // Act: frame 42 gets an unknown block type, frame 43 a cap_len larger than its block.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blocks[42]), 0x0BAD_0BAD);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blocks[43] + 20), 4096);
        Frame?[] batch = new Frame?[100];
        int found = source.FramesById(new FrameId(0), batch);

        // Assert
        await Assert.That(found).IsEqualTo(98);
        await Assert.That(batch[42]).IsNull();
        await Assert.That(batch[43]).IsNull();
        List<int> wrong = [];
        for (int id = 0; id < 100; id++)
        {
            if (id is 42 or 43)
            {
                continue;
            }

            if (batch[id] is not { } frame || !frame.Data.Span.SequenceEqual(originals[id]))
            {
                wrong.Add(id);
            }
        }

        await Assert.That(wrong).IsEmpty();
    }

    #endregion

    #region Helpers

    private static void _CollectMismatches(PcapngSource source, int frameCount, string label, List<string> mismatches)
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

    private static byte[] _Capture(int count)
    {
        byte[] dst = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] src = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        using PcapngCaptureBuilder writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < count; i++)
        {
            byte[] payload = new byte[20 + (i % 3)];
            BinaryPrimitives.WriteInt32LittleEndian(payload, i);
            writer.WriteFrame(0, 1_700_000_000_000_000_000L + (i * 731_017L), TestFrames.BuildEthernetFrame(dst, src, 0x0800, payload));
        }

        return writer.Build();
    }

    private static List<int> _EnhancedPacketBlockOffsets(byte[] data)
    {
        List<int> offsets = [];
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (type == 6)
            {
                offsets.Add(offset);
            }

            offset += length;
        }

        return offsets;
    }

    #endregion
}
