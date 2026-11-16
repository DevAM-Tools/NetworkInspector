// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Targets;

/// <summary>
/// Tests for <see cref="StreamExportTarget"/> and its non-closing entry: a caller stream survives the export, an owned stream is
/// closed, and the entry forwards seeking so the BLF header patch works on a seekable caller stream.
/// </summary>
internal sealed class StreamExportTargetTests
{
    [Test]
    public async Task ANonOwningEntryLeavesTheCallerStreamOpenAfterDispose()
    {
        using MemoryStream caller = new();
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);

        stream!.Write([1, 2]); // Invariant: stream is set when TryOpenWrite returns true
        stream.Dispose();
        caller.WriteByte(3);

        await Assert.That(caller.CanWrite).IsTrue();
        await Assert.That(caller.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task AnOwningEntryClosesTheStreamOnDispose()
    {
        MemoryStream caller = new();
        StreamExportTarget target = new(caller, ownsStream: true);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);

        stream!.Dispose(); // Invariant: stream is set when TryOpenWrite returns true

        await Assert.That(caller.CanWrite).IsFalse();
    }

    [Test]
    public async Task ANonOwningEntryForwardsSeekAndPositionOfAStreamThatStartedLater()
    {
        using MemoryStream caller = new();
        caller.Write([0, 0, 0, 0, 0]);
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        using Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true

        long start = entry.Position;
        entry.Write([7, 7]);
        entry.Seek(start, SeekOrigin.Begin);
        entry.Write([9]);

        await Assert.That(entry.CanSeek).IsTrue();
        await Assert.That(start).IsEqualTo(5L);
        await Assert.That(entry.Length).IsEqualTo(7L);
        await Assert.That(caller.ToArray()).IsEquivalentTo(new byte[] { 0, 0, 0, 0, 0, 9, 7 });
    }

    [Test]
    public async Task ANonOwningEntryOverANonSeekableStreamReportsNoSeek()
    {
        ForwardOnlyStream caller = new();
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        using Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true

        entry.Write([5]);

        await Assert.That(entry.CanSeek).IsFalse();
        await Assert.That(entry.CanRead).IsFalse();
        await Assert.That(caller.ToArray()).IsEquivalentTo(new byte[] { 5 });
    }

    [Test]
    public async Task DisposingANonOwningEntryFlushesTheCallerStreamOnceAndKeepsItOpen()
    {
        using FlushCountingStream caller = new();
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true
        entry.Write([1]);

        entry.Dispose();
        entry.Dispose();

        await Assert.That(caller.FlushCount).IsEqualTo(1);
        await Assert.That(caller.CanWrite).IsTrue();
    }

    /// <summary>A memory stream that counts its <see cref="Flush"/> calls.</summary>
    private sealed class FlushCountingStream : MemoryStream
    {
        /// <summary>How many times <see cref="Flush"/> was called.</summary>
        internal int FlushCount { get; private set; }

        /// <inheritdoc/>
        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }

    [Test]
    public async Task AUsedEntryRefusesCallsAfterDisposeAndASecondDisposeIsHarmless()
    {
        using MemoryStream caller = new();
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true

        entry.Dispose();
        entry.Dispose();

        await Assert.That(entry.CanWrite).IsFalse();
        await Assert.That(() => entry.WriteByte(1)).Throws<ObjectDisposedException>();
        await Assert.That(() => entry.Flush()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task AsyncWritesAndFlushReachTheCallerStream()
    {
        using MemoryStream caller = new();
        StreamExportTarget target = new(caller);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        using Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true

        await entry.WriteAsync(new ReadOnlyMemory<byte>([1, 2, 3]), CancellationToken.None);
        entry.Write([4]);
        await entry.FlushAsync(CancellationToken.None);
        entry.SetLength(3);

        await Assert.That(caller.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task ASecondOpenAndANamedEntryAreRefused()
    {
        using MemoryStream caller = new();
        StreamExportTarget target = new(caller);
        ExportEntryName.TryCreate("fields/field_1.parquet", out ExportEntryName named, out _);

        bool namedOpened = target.TryOpenWrite(named, out _, out string namedFailure);
        target.TryOpenWrite(ExportEntryName.Main, out Stream? first, out _);
        using Stream firstEntry = first!; // Invariant: stream is set when TryOpenWrite returns true
        bool second = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);

        await Assert.That(namedOpened).IsFalse();
        await Assert.That(namedFailure).Contains("DirectoryExportTarget");
        await Assert.That(second).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("already opened");
    }

    [Test]
    public async Task TheConstructorRejectsNullAndAStreamThatCannotBeWritten()
    {
        using MemoryStream readOnly = new([1], writable: false);

        await Assert.That(() => new StreamExportTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new StreamExportTarget(readOnly)).Throws<ArgumentException>();
    }
}
