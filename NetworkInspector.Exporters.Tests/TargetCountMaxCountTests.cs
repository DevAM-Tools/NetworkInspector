// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Exit-point coverage for <c>WithTargetFrameCount</c> / <c>WithTargetPacketCount</c>
/// MaxCount overflow validation shared by all exporters.
/// </summary>
internal sealed class TargetCountMaxCountTests
{
    [Test]
    public async Task AscWithTargetFrameCountAboveMaxCountThrows()
    {
        AscExporter.Builder builder = AscExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BlfWithTargetFrameCountAboveMaxCountThrows()
    {
        BlfExporter.Builder builder = BlfExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PcapngWithTargetFrameCountAboveMaxCountThrows()
    {
        PcapngExporter.Builder builder = PcapngExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetFrameCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task JsonWithTargetPacketCountAboveMaxCountThrows()
    {
        JsonExporter.Builder builder = JsonExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CsvWithTargetPacketCountAboveMaxCountThrows()
    {
        CsvExporter.Builder builder = CsvExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TextWithTargetPacketCountAboveMaxCountThrows()
    {
        TextExporter.Builder builder = TextExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PbfWithTargetPacketCountAboveMaxCountThrows()
    {
        PbfExporter.Builder builder = PbfExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ParquetWithTargetPacketCountAboveMaxCountThrows()
    {
        ParquetExporter.Builder builder = ParquetExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DuckDbWithTargetPacketCountAboveMaxCountThrows()
    {
        DuckDbExporter.Builder builder = DuckDbExporter.CreateBuilder();
        await Assert.That(() => builder.WithTargetPacketCount(ArrayIndexIdRange.MaxCount + 1))
            .Throws<ArgumentOutOfRangeException>();
    }
}
