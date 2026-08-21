// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Exit-point coverage for <c>WithTargetFrameCount</c> / <c>WithTargetPacketCount</c>
/// MaxCount overflow validation shared by all exporters.
/// </summary>
internal sealed class TargetCountMaxCountTests
{
    [Test]
    public async Task Asc_WithTargetFrameCount_AboveMaxCount_Throws()
    {
        AscExporter.Builder builder = AscExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Blf_WithTargetFrameCount_AboveMaxCount_Throws()
    {
        BlfExporter.Builder builder = BlfExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Pcapng_WithTargetFrameCount_AboveMaxCount_Throws()
    {
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Json_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Csv_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Text_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Pbf_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Parquet_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        ParquetExporter.Builder builder = ParquetExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DuckDb_WithTargetPacketCount_AboveMaxCount_Throws()
    {
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }
}
