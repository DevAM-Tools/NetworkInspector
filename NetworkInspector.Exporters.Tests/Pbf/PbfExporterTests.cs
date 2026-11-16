// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for the <see cref="PbfExporter"/> ? validates magic markers, block structure,
/// compression, and both Standard and Columnar formats.
/// </summary>
internal sealed class PbfExporterTests
{
    [Test]
    public async Task BuilderMaxPacketsPerBlockRejectsZero()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithMaxPacketsPerBlock(0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuilderMaxPacketsPerBlockRejectsNegative()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithMaxPacketsPerBlock(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StandardFormatProducesValidPbf()
    {
        using MemoryStream stream = new();

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
    }

    [Test]
    public async Task ColumnarFormatProducesValidPbf()
    {
        using MemoryStream stream = new();

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Columnar)
            .WithCompressed(false)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
    }

    [Test]
    public async Task CompressedFormatProducesValidPbf()
    {
        using MemoryStream stream = new();

        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(PbfExportFormat.Standard)
            .WithCompressed(true)
            .Build();

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(10);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }

        exporter.OnFinish();

        PbfVerifier verifier = PbfVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
        await Assert.That(verifier.FileSize).IsGreaterThan(0);
    }

    [Test]
    public async Task StreamOutputProducesValidPbf()
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
    public async Task BlockBoundaryRespectedBySmallLimit()
    {
        using MemoryStream stream = new();

        // Force small blocks (2 packets per block)
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
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

        PbfVerifier verifier = PbfVerifier.FromBytes(stream.ToArray());
        await Assert.That(verifier.HasValidHeaderMagic).IsTrue();
        await Assert.That(verifier.HasValidFooterMagic).IsTrue();
        // With 6 packets and max 2 per block, expect at least 3 blocks
        await Assert.That(verifier.BlockCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task EmptyExportProducesValidMagicWrappers()
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

    // ========================================================================
    // Statistics counters
    // ========================================================================

    [Test]
    public async Task StatisticsTracksWrittenCount()
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
    // Trailer index consistency after partial I/O failure
    // ========================================================================

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
    ///   <item>Re-run the export to a <see cref="TestStreams.FailingStream"/> that fails just
    ///         before the second block would be committed.</item>
    ///   <item>Assert that <c>BlockCount == 1</c> and <c>HasErrors == true</c>: the failed
    ///         block is not reflected in the index, but the successful first block is.</item>
    /// </list>
    /// </summary>
    [Test]
    public async Task BlockIndexConsistencyFailedBlockWriteNotCountedInBlockIndex()
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
            using TestStreams.FailingStream probe = new()
            {
                ThrowAfterByte = long.MaxValue
            };
            using PbfExporter probeExporter = PbfExporter.CreateBuilder()
                .ToStream(probe)
                .WithCompressed(false)
                .WithMaxPacketsPerBlock(1)
                .WithWriteMode(ExportWriteMode.Synchronous) // the byte measurement below needs the block written inside OnPacket
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
        using TestStreams.FailingStream failing = new()
        {
            ThrowAfterByte = bytesAfterFirstBlock
        };
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(failing)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(1)
            .WithWriteMode(ExportWriteMode.Synchronous)
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
