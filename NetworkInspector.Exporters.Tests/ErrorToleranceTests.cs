// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FailingStream = NetworkInspector.Exporters.Tests.Helpers.TestStreams.FailingStream;

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Cross-exporter tests that validate <see cref="ErrorToleranceMode"/> behaviour:
/// in <see cref="ErrorToleranceMode.Tolerant"/> mode an I/O failure must skip the
/// item and raise <c>ItemSkipped</c>; in <see cref="ErrorToleranceMode.Strict"/>
/// mode the next call must return <c>false</c> and <c>HasErrors</c> must become
/// <c>true</c>. These tests exist for every exporter (CSV, JSON, Text, BLF,
/// PCAPNG, PBF).
/// </summary>
internal sealed class ErrorToleranceTests
{
    #region CSV

    [Test]
    public async Task Csv_TolerantMode_RaisesItemSkippedAndContinues()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(stream)
            .WithBom(false)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        // The very first byte cannot be written, so the header / first row will fail
        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task Csv_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using CsvExporter exporter = CsvExporter.CreateBuilder()
            .ToStream(stream)
            .WithBom(false)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        bool first = exporter.OnPacket(packets[0]);
        bool second = exporter.OnPacket(packets[1]);

        // Either the first call already returned false, or the second call must.
        await Assert.That(first && second).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region JSON

    [Test]
    public async Task Json_TolerantMode_RaisesItemSkippedAndContinues()
    {
        // Allow the opening "[\n" through (2 bytes) so that Start succeeds and the
        // failure happens on the actual packet write.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 2
        };
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(JsonExportFormat.Array)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task Json_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 2
        };
        using JsonExporter exporter = JsonExporter.CreateBuilder()
            .ToStream(stream)
            .WithFormat(JsonExportFormat.Array)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);
        bool second = exporter.OnPacket(packets[1]);

        await Assert.That(second).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region Text

    [Test]
    public async Task Text_TolerantMode_RaisesItemSkippedAndContinues()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);
        exporter.OnPacket(packets[1]);
        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task Text_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 0
        };
        using TextExporter exporter = TextExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(2);
        exporter.OnPacket(packets[0]);
        bool second = exporter.OnPacket(packets[1]);

        await Assert.That(second).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region PCAPNG

    [Test]
    public async Task Pcapng_TolerantMode_RaisesItemSkippedAndContinues()
    {
        // Allow the SHB through but fail on subsequent IDB/EPB writes.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 256
        };
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Frame[] frames = PacketGenerators.CreateEthernetFrames(4);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }
        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task Pcapng_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 256
        };
        using PcapngExporter exporter = PcapngExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        Frame[] frames = PacketGenerators.CreateEthernetFrames(4);
        bool anySucceededAfterFailure = false;
        bool sawFailure = false;
        foreach (Frame frame in frames)
        {
            bool ok = exporter.OnFrame(frame);
            if (sawFailure && ok)
            {
                anySucceededAfterFailure = true;
            }
            if (!ok)
            {
                sawFailure = true;
            }
        }

        await Assert.That(sawFailure).IsTrue();
        await Assert.That(anySucceededAfterFailure).IsFalse();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region BLF

    [Test]
    public async Task Blf_TolerantMode_RaisesItemSkippedAndContinues()
    {
        // Allow the BLF header through (~144 bytes) but fail on object writes.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 256
        };
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Frame[] frames = PacketGenerators.CreateEthernetFrames(8);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }
        exporter.OnFinish();

        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
        await Assert.That(exporter.HasErrors).IsTrue();
    }

    [Test]
    public async Task Blf_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 256
        };
        using BlfExporter exporter = BlfExporter.CreateBuilder()
            .ToStream(stream)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        // BLF buffers writes inside an 8 MiB container, so individual OnFrame
        // calls do not surface I/O failures — the failure manifests on flush.
        // OnFinish flushes the container and updates the header; that is where
        // the broken stream throws and HasErrors must become true.
        Frame[] frames = PacketGenerators.CreateEthernetFrames(8);
        foreach (Frame frame in frames)
        {
            exporter.OnFrame(frame);
        }
        exporter.OnFinish();

        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion

    #region PBF

    [Test]
    public async Task Pbf_TolerantMode_RaisesItemSkippedAndContinues()
    {
        // PBF only writes to the underlying stream when a block is flushed; force
        // a flush by limiting block size aggressively.
        using FailingStream stream = new()
        {
            ThrowAfterByte = 64
        };
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(1)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Tolerant;

        int skippedRaised = 0;
        exporter.ItemSkipped += (_, _) => Interlocked.Increment(ref skippedRaised);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        foreach (Packet packet in packets)
        {
            exporter.OnPacket(packet);
        }
        exporter.OnFinish();

        // Either init failed (skippedRaised on header write) or a block flush failed,
        // either way HasErrors must be set.
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(skippedRaised).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Pbf_StrictMode_AbortsOnFailure()
    {
        using FailingStream stream = new()
        {
            ThrowAfterByte = 64
        };
        using PbfExporter exporter = PbfExporter.CreateBuilder()
            .ToStream(stream)
            .WithCompressed(false)
            .WithMaxPacketsPerBlock(1)
            .Build();
        exporter.ErrorTolerance = ErrorToleranceMode.Strict;

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(4);
        bool sawFailure = false;
        foreach (Packet packet in packets)
        {
            if (!exporter.OnPacket(packet))
            {
                sawFailure = true;
                break;
            }
        }

        await Assert.That(sawFailure).IsTrue();
        await Assert.That(exporter.HasErrors).IsTrue();
        await Assert.That(exporter.IsFinished).IsTrue();
    }

    #endregion
}
