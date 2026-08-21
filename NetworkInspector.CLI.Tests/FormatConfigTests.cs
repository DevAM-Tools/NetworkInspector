// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="ExportFormatConfig"/> and <see cref="ConvertOutputConfig"/>.
/// </summary>
internal sealed class FormatConfigTests
{
    [Test]
    [Arguments("json", typeof(JsonFormatConfig))]
    [Arguments("json:style=pretty", typeof(JsonFormatConfig))]
    [Arguments("pbf:format=columnar,compressed", typeof(PbfFormatConfig))]
    [Arguments("text:level=summary", typeof(TextFormatConfig))]
    [Arguments("parquet", typeof(ParquetFormatConfig))]
    [Arguments("duckdb", typeof(DuckDbFormatConfig))]
    public async Task ExportFormatConfig_Parse_ReturnsExpectedType(string spec, Type expectedType)
    {
        ExportFormatConfig config = ExportFormatConfig.Parse(spec);

        await Assert.That(config.GetType()).IsEqualTo(expectedType);
    }

    [Test]
    public async Task ExportFormatConfig_Parse_UnknownFormat_Throws()
    {
        await Assert.That(() => ExportFormatConfig.Parse("xml"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExportFormatConfig_FromExtension_Json_ReturnsCompactJson()
    {
        ExportFormatConfig config = ExportFormatConfig.FromExtension(".json");

        await Assert.That(config).IsTypeOf<JsonFormatConfig>();
    }

    [Test]
    public async Task ExportFormatConfig_FromExtension_DuckDb_ReturnsDuckDb()
    {
        ExportFormatConfig config = ExportFormatConfig.FromExtension(".duckdb");

        await Assert.That(config).IsTypeOf<DuckDbFormatConfig>();
    }

    [Test]
    public async Task ParquetFormatConfig_IsDirectoryOutput_True()
    {
        ExportFormatConfig config = ExportFormatConfig.Parse("parquet");

        await Assert.That(config.IsDirectoryOutput).IsTrue();
    }

    [Test]
    public async Task DuckDbFormatConfig_IsDirectoryOutput_False()
    {
        ExportFormatConfig config = ExportFormatConfig.Parse("duckdb");

        await Assert.That(config.IsDirectoryOutput).IsFalse();
    }

    [Test]
    public async Task ExportFormatConfig_FromExtension_Unknown_Throws()
    {
        await Assert.That(() => ExportFormatConfig.FromExtension(".xyz"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ConvertOutputConfig_Parse_Pcapng_Succeeds()
    {
        ConvertOutputConfig config = ConvertOutputConfig.Parse("pcapng");

        await Assert.That(config).IsTypeOf<PcapngOutputConfig>();
    }

    [Test]
    public async Task ConvertOutputConfig_Parse_BlfBest_Succeeds()
    {
        ConvertOutputConfig config = ConvertOutputConfig.Parse("blf:compression=best");

        await Assert.That(config).IsTypeOf<BlfOutputConfig>();
    }

    [Test]
    public async Task ConvertOutputConfig_Parse_Asc_Succeeds()
    {
        ConvertOutputConfig config = ConvertOutputConfig.Parse("asc");

        await Assert.That(config).IsTypeOf<AscOutputConfig>();
    }

    [Test]
    public async Task ConvertOutputConfig_FromExtension_Empty_DefaultsToPcapng()
    {
        ConvertOutputConfig config = ConvertOutputConfig.FromExtension("");

        await Assert.That(config).IsTypeOf<PcapngOutputConfig>();
    }

    [Test]
    public async Task ConvertOutputConfig_FromExtension_Unknown_DefaultsToPcapng()
    {
        ConvertOutputConfig config = ConvertOutputConfig.FromExtension(".out");

        await Assert.That(config).IsTypeOf<PcapngOutputConfig>();
    }

    [Test]
    public async Task ConvertOutputConfig_Parse_UnknownCompression_Throws()
    {
        await Assert.That(() => ConvertOutputConfig.Parse("blf:compression=ultra"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExportFormatConfig_Parse_InvalidJsonStyle_Throws()
    {
        await Assert.That(() => ExportFormatConfig.Parse("json:style=weird"))
            .Throws<ArgumentException>();
    }

}
