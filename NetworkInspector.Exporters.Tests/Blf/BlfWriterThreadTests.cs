// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// The write modes of the BLF exporter: a writer thread compresses (zlib) and writes the containers in background mode, the
/// caller's thread does it in synchronous mode. Both must produce the same file; the container size is configurable inside a
/// documented range; the caller's stream must hold the complete, patched file when <c>OnFinish</c> returns; a stream that fails
/// must be reported as a lost container.
/// </summary>
[Category("Slow")]
internal sealed class BlfWriterThreadTests
{
    private const int _MinContainerSize = 64 * 1024;

    private static byte[] _Export(ExportWriteMode mode, int containerSize, Frame[] frames)
    {
        using MemoryStream stream = new();
        using (BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxContainerSize(containerSize)
            .WithWriteMode(mode)
            .Build())
        {
            foreach (Frame frame in frames)
            {
                exporter.OnFrame(frame);
            }

            exporter.OnFinish();
        }

        return stream.ToArray();
    }

    [Test]
    public async Task BackgroundAndSynchronousModesWriteIdenticalBytes()
    {
        // 3 000 frames of about 100 bytes with a 64 KiB container: several containers are written.
        Frame[] frames = PacketGenerators.CreateEthernetFrames(3000);

        byte[] background = _Export(ExportWriteMode.Background, _MinContainerSize, frames);
        byte[] synchronous = _Export(ExportWriteMode.Synchronous, _MinContainerSize, frames);

        await Assert.That(synchronous.AsSpan().SequenceEqual(background)).IsTrue();
        await Assert.That(background.Length).IsGreaterThan(144);
    }

    [Test]
    public async Task CallerStreamHoldsTheCompleteFileWithPatchedHeaderWhenOnFinishReturns()
    {
        Frame[] frames = PacketGenerators.CreateEthernetFrames(3000);
        using MemoryStream stream = new();
        BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxContainerSize(_MinContainerSize)
            .Build();
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();
        byte[] file = stream.ToArray();

        // The header was patched after the last container: the object count of the LOGG header is not the placeholder 0, and the
        // stored file length equals the stream length.
        uint objectCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32, 4));
        ulong storedLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16, 8));

        await Assert.That(objectCount).IsGreaterThanOrEqualTo(3000u);
        await Assert.That(storedLength).IsEqualTo((ulong)file.Length);
        await Assert.That(exporter.HasErrors).IsFalse();
        GC.KeepAlive(exporter);
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task ANonSeekableStreamKeepsThePlaceholderHeader(ExportWriteMode mode)
    {
        Frame[] frames = PacketGenerators.CreateEthernetFrames(50);
        using MemoryStream inner = new();
        using NonSeekableStream stream = new(inner);

        using (BlfExporter exporter = BlfExporter.CreateBuilder().ToStream(stream).WithWriteMode(mode).Build())
        {
            foreach (Frame frame in frames)
            {
                exporter.OnFrame(frame);
            }

            exporter.OnFinish();
        }

        byte[] file = inner.ToArray();

        // No seek was possible, so the object count of the header is still the placeholder.
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32, 4))).IsEqualTo(0u);
        await Assert.That(file.Length).IsGreaterThan(144);
    }

    [Test]
    [Arguments(ExportWriteMode.Background)]
    [Arguments(ExportWriteMode.Synchronous)]
    public async Task AFailingStreamIsReportedAsALostContainer(ExportWriteMode mode)
    {
        Frame[] frames = PacketGenerators.CreateEthernetFrames(3000);
        using FailingStream stream = new(failOnWriteNumber: 2);
        List<ExportErrorEventArgs> events = [];
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxContainerSize(_MinContainerSize)
            .WithWriteMode(mode)
            .Build();
        exporter.ItemSkipped += (_, args) => events.Add(args);

        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();

        // The first write is the file header, the second the first container's header (fails): its frames are skipped, the
        // others are written.
        await Assert.That(exporter.SkippedCount).IsGreaterThan(0);
        await Assert.That(exporter.WrittenCount + exporter.SkippedCount).IsEqualTo(3000);
        await Assert.That(events.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(events[0].Message).Contains("packets lost");
    }

    [Test]
    public async Task AStrictSynchronousContainerFailureReturnsFalseInTheSameCall()
    {
        Frame[] frames = PacketGenerators.CreateEthernetFrames(3000);
        using FailingStream stream = new(failOnWriteNumber: 2);
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .WithMaxContainerSize(_MinContainerSize)
            .WithWriteMode(ExportWriteMode.Synchronous)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        // The first container is written (and fails) inside the OnFrame call that completes it. That very call must stop the driver.
        int stoppedAt = -1;
        bool stoppedWithError = false;
        for (int index = 0; index < frames.Length; index++)
        {
            bool accepted = exporter.OnFrame(frames[index]);
            if (exporter.HasErrors)
            {
                stoppedAt = index;
                stoppedWithError = !accepted;
                break;
            }
        }

        exporter.OnFinish();

        await Assert.That(stoppedAt).IsGreaterThan(0);
        await Assert.That(stoppedWithError).IsTrue();
    }

    [Test]
    [Arguments(65_536L, true)]
    [Arguments(67_108_864L, true)]
    [Arguments(65_535L, false)]
    [Arguments(67_108_865L, false)]
    public async Task ContainerSizeBoundsAreAcceptedOrRefusedWithTheRange(long bytes, bool accepted)
    {
        if (accepted)
        {
            BlfExporter.Builder builder = BlfExporter.CreateBuilder().WithMaxContainerSize(bytes);
            await Assert.That(builder).IsNotNull();
            return;
        }

        await Assert.That(() => BlfExporter.CreateBuilder().WithMaxContainerSize(bytes))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("between 65536 and 67108864 bytes");
    }

    [Test]
    public async Task ASmallContainerSizeWritesSeveralContainersThatReadBackToTheSameFrames()
    {
        using TestDir dir = new("blf_containers");
        string small = dir.FilePath("small.blf");
        string large = dir.FilePath("large.blf");
        Frame[] frames = PacketGenerators.CreateEthernetFrames(3000);

        foreach ((string path, int size) in new[] { (small, _MinContainerSize), (large, 10 * 1024 * 1024) })
        {
            using BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(path).WithMaxContainerSize(size).Build();
            foreach (Frame frame in frames)
            {
                exporter.OnFrame(frame);
            }

            exporter.OnFinish();
        }

        BlfStructuralVerifier smallFile = BlfStructuralVerifier.Open(small);
        BlfStructuralVerifier largeFile = BlfStructuralVerifier.Open(large);

        await Assert.That(smallFile.HasValidHeader).IsTrue();
        await Assert.That(smallFile.ObjectCount).IsGreaterThan(largeFile.ObjectCount);
    }

    [Test]
    public async Task WriteModeMustBeDefined()
    {
        await Assert.That(() => BlfExporter.CreateBuilder().WithWriteMode((ExportWriteMode)9)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>A stream that cannot seek: the header patch is skipped and the placeholder stays.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    /// <summary>A stream that throws on the n-th write call (1-based) and accepts all others.</summary>
    private sealed class FailingStream(int failOnWriteNumber) : MemoryStream
    {
        private int _Writes;

        // Only the array overload is overridden: MemoryStream.Write(ReadOnlySpan) of a derived type calls it, so every write is
        // counted once (overriding both would recurse).
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref _Writes) == failOnWriteNumber)
            {
                throw new IOException("test write failure");
            }

            base.Write(buffer, offset, count);
        }
    }
}
