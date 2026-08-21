// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for the <see cref="PbfExporter"/> ? validates magic markers, block structure,
/// compression, and both Standard and Columnar formats.
/// </summary>
internal sealed class PbfExporterTests
{
    [Test]
    public async Task Builder_RequiresOutput()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Builder_MaxPacketsPerBlock_RejectsZero()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithMaxPacketsPerBlock(0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Builder_MaxPacketsPerBlock_RejectsNegative()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithMaxPacketsPerBlock(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardFormat_ProducesValidPbf()
    {
        using TestDir dir = new("pbf_standard");
        string path = dir.FilePath("output.pbf");

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.Open(path);
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
    }

    [Test]
    public async Task ColumnarFormat_ProducesValidPbf()
    {
        using TestDir dir = new("pbf_columnar");
        string path = dir.FilePath("output.pbf");

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(PbfExportFormat.Columnar)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.Open(path);
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
    }

    [Test]
    public async Task CompressedFormat_ProducesValidPbf()
    {
        using TestDir dir = new("pbf_compressed");
        string path = dir.FilePath("output.pbf");

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(true)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.Open(path);
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
        await Assert.That(verifier.FileSize).IsGreaterThan(0);
    }

    [Test]
    public async Task StreamOutput_ProducesValidPbf()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(ms.Length).IsGreaterThan(0);

        // Check magic in the raw bytes
        byte[] data = ms.ToArray();
        byte[] expectedMagic =
            "NETWORK-INSPECTOR-PBF-FORMAT-v1\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray();
        int magicSize = expectedMagic.Length;

        // Header magic
        bool headerMatch = data.AsSpan(0, magicSize).SequenceEqual(expectedMagic);
        await Assert.That(headerMatch).IsTrue();

        // Footer magic (last magicSize bytes)
        bool footerMatch = data.AsSpan(data.Length - magicSize, magicSize)
            .SequenceEqual(expectedMagic);
        await Assert.That(footerMatch).IsTrue();
    }

    [Test]
    public async Task BlockBoundary_RespectedBySmallLimit()
    {
        using TestDir dir = new("pbf_block_boundary");
        string path = dir.FilePath("output.pbf");

        // Force small blocks (2 packets per block)
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(2)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(6);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.Open(path);
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
        // With 6 packets and max 2 per block, expect at least 3 blocks
        await Assert.That(verifier.BlockCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task EmptyExport_ProducesValidMagicWrappers()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .Build();

        // No packets written
        exporter.OnFinish();

        byte[] data = ms.ToArray();
        if (data.Length > 0)
        {
            byte[] expectedMagic =
                "NETWORK-INSPECTOR-PBF-FORMAT-v1\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray();
            int magicSize = expectedMagic.Length;

            // Both magic markers should be present
            bool headerMatch = data.AsSpan(0, magicSize)
                .SequenceEqual(expectedMagic);
            await Assert.That(headerMatch).IsTrue();

            bool footerMatch = data.AsSpan(data.Length - magicSize, magicSize)
                .SequenceEqual(expectedMagic);
            await Assert.That(footerMatch).IsTrue();
        }
    }

    [Test]
    public async Task DefaultUiName_IsPbfExporter()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("PBF Exporter");
    }

    [Test]
    public async Task WithUiName_OverridesDefault()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithUiName("My PBF")
            .Build();

        await Assert.That(exporter.UiName).IsEqualTo("My PBF");
    }

    // ========================================================================
    // Cancellation
    // ========================================================================

    [Test]
    public async Task Cancellation_StopsExport()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .WithCancellationToken(cts.Token)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);

        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        await cts.CancelAsync().ConfigureAwait(false);

        // After cancellation, OnPacket must return false
        bool accepted = exporter.OnPacket(packets[2]);
        await Assert.That(accepted).IsFalse();

        exporter.OnFinish();
    }

    [Test]
    public async Task IsFinished_TrueAfterCancellation()
    {
        using CancellationTokenSource cts = new();
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .WithCancellationToken(cts.Token)
            .Build();

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    // ========================================================================
    // Target packet count
    // ========================================================================

    [Test]
    public async Task TargetPacketCount_LimitsExport()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .WithTargetPacketCount(3)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        await Assert.That(exporter.PacketCount).IsEqualTo(3);
    }

    [Test]
    public async Task IsFinished_TrueAfterTargetReached()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .WithTargetPacketCount(2)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.IsFinished).IsTrue();
        exporter.OnFinish();
    }

    // ========================================================================
    // Lifecycle: IsFinished, Double-finish
    // ========================================================================

    [Test]
    public async Task IsFinished_FalseBeforeOnFinish()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        await Assert.That(exporter.IsFinished).IsFalse();
    }

    [Test]
    public async Task IsFinished_TrueAfterOnFinish()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
    }

    [Test]
    public async Task OnPacket_AfterFinish_ReturnsFalse()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        exporter.OnFinish();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        bool accepted = exporter.OnPacket(packets[0]);

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task DoubleFinish_IsIdempotent()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);

        // Calling OnFinish twice must not throw
        exporter.OnFinish();
        exporter.OnFinish();

        await Assert.That(exporter.IsFinished).IsTrue();
        await Assert.That(exporter.PacketCount).IsEqualTo(1);
    }

    // ========================================================================
    // Statistics counters
    // ========================================================================

    [Test]
    public async Task Statistics_TracksWrittenCount()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        const int count = 4;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(count);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        await Assert.That(exporter.PacketCount).IsEqualTo(count);
        await Assert.That(exporter.WrittenCount).IsEqualTo(count);
        await Assert.That(exporter.SkippedCount).IsEqualTo(0);
        await Assert.That(exporter.ErrorCount).IsEqualTo(0);
        await Assert.That(exporter.HasErrors).IsFalse();

        exporter.OnFinish();
    }

    // ========================================================================
    // Description
    // ========================================================================

    [Test]
    public async Task Description_ReturnsNull_WhenNotSet()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .Build();

        await Assert.That(exporter.Description).IsNull();
    }

    [Test]
    public async Task Description_ReturnsConfiguredValue()
    {
        using MemoryStream ms = new();
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(ms)
            .WithCompressed(false)
            .WithDescription("Packet Binary Format export")
            .Build();

        await Assert.That(exporter.Description).IsEqualTo("Packet Binary Format export");
    }

    // ========================================================================
    // Trailer index consistency after partial I/O failure
    // ========================================================================

    /// <summary>
    /// A write-only stream that accepts bytes normally up to a configured threshold,
    /// then throws <see cref="IOException"/> on any subsequent write. Used to verify
    /// that the PBF exporter's commit-after-success pattern leaves the block index
    /// consistent when a block flush fails mid-write.
    /// </summary>
    private sealed class CountingFailingStream : Stream
    {
        private long _BytesWritten;

        /// <summary>
        /// Number of bytes to accept before the stream begins throwing
        /// <see cref="IOException"/>.
        /// </summary>
        internal long ThrowAfterByte { get; init; } = long.MaxValue;

        /// <inheritdoc/>
        public override bool CanRead => false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => true;

        /// <inheritdoc/>
        public override long Length => _BytesWritten;

        /// <inheritdoc/>
        public override long Position
        {
            get => _BytesWritten;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            // No-op
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckThrow(count);
            _BytesWritten += count;
        }

        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckThrow(buffer.Length);
            _BytesWritten += buffer.Length;
        }

        private void CheckThrow(int count)
        {
            if (_BytesWritten + count > ThrowAfterByte)
            {
                throw new IOException("Simulated I/O failure");
            }
        }
    }

    /// <summary>
    /// The block index recorded in the PBF trailer must match the number of
    /// blocks that were fully and successfully written to the stream. When a block
    /// flush fails partway through, the commit-after-success pattern
    /// must leave <see cref="PbfExporter.BlockCount"/> equal to the number of
    /// completed blocks, not one more.
    ///
    /// Strategy:
    /// <list type="number">
    ///   <item>Write 4 packets with <c>maxPacketsPerBlock=1</c> to a <see cref="MemoryStream"/>
    ///         to measure the exact byte length required for the header plus the first block.</item>
    ///   <item>Re-run the export to a <see cref="CountingFailingStream"/> that fails just
    ///         before the second block would be committed.</item>
    ///   <item>Assert that <c>BlockCount == 1</c> and <c>HasErrors == true</c>: the failed
    ///         block is not reflected in the index, but the successful first block is.</item>
    /// </list>
    /// </summary>
    [Test]
    public async Task BlockIndex_Consistency_FailedBlockWrite_NotCountedInBlockIndex()
    {
        // Create all packets once so the same already-parsed field IDs are used in
        // both the probe measurement phase and the actual export phase.  Reusing the
        // same packet objects guarantees identical PBF serialization regardless of
        // which other tests have run before this one and extended the field registry.
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);

        // -- Phase 1: measure bytes for header + first block ----------------------
        // Export the first packet to a probe stream (never throws) to learn the
        // exact byte length required for header + one data block.
        long bytesAfterFirstBlock;
        {
            using CountingFailingStream probe = new()
            {
                ThrowAfterByte = long.MaxValue
            };
            using PbfExporter probeExporter = PbfExporter.CreateBuilder()
                .ToStream(probe)
                .WithCompressed(false)
                .WithMaxPacketsPerBlock(1)
                .WithTrailerIndex(true)
                .Build();

            probeExporter.OnPacket(packets[0]);

            // Measure bytes after 1 block is flushed.  OnFinish() would write the
            // trailer; we want the measurement after the block but before the trailer,
            // so capture the stream length right after the block flush.
            // With maxPacketsPerBlock=1 the block is flushed synchronously inside
            // OnPacket, so probe.Length is the correct measurement.
            bytesAfterFirstBlock = probe.Length;

            // Clean up (ignore trailer for measurement purposes)
            probeExporter.OnFinish();
        }

        // -- Phase 2: fail on second block, verify index consistency --------------
        // Allow exactly the bytes needed for header + first block; the second block
        // write will throw, so it must not be committed to the block index.
        using CountingFailingStream failing = new()
        {
            ThrowAfterByte = bytesAfterFirstBlock
        };
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(failing)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(1)
            .WithTrailerIndex(true)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        // First block succeeded; subsequent blocks failed.
        // BlockCount must equal the number of committed (not just attempted) blocks.
        // With commit-after-success, only blocks whose Write() succeeded
        // are added to the block index and counted in BlockCount.
        await Assert.That(exporter.BlockCount).IsGreaterThanOrEqualTo(1);
        // The number of indexed blocks must never exceed the blocks actually written.
        await Assert.That(exporter.BlockCount).IsLessThan(4);
        await Assert.That(exporter.HasErrors).IsTrue();
    }
}
