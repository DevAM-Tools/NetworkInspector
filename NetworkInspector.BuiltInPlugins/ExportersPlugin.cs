// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Registers the nine export formats: three frame exporters (BLF, PCAPNG, ASC) and six packet exporters (CSV, JSON, Text,
/// PBF, Parquet, DuckDB). Stateless; thread-safe.
/// </summary>
public sealed class ExportersPlugin : IPlugin
{
    /// <inheritdoc/>
    public PluginInfo Info { get; } = BuiltInPluginInfo.Create("NetworkInspector.Exporters",
        "Writes frames as BLF, PCAPNG, or ASC and parsed packets as CSV, JSON, text, PBF, Parquet, or DuckDB.");

    /// <inheritdoc/>
    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Frame exporters first, then packet exporters; the order is the order the application lists the formats in.
        builder.AddFrameExporter(BlfExporterRegistration.Create());
        builder.AddFrameExporter(PcapngExporterRegistration.Create());
        builder.AddFrameExporter(AscExporterRegistration.Create());
        builder.AddPacketExporter(CsvExporterRegistration.Create());
        builder.AddPacketExporter(JsonExporterRegistration.Create());
        builder.AddPacketExporter(TextExporterRegistration.Create());
        builder.AddPacketExporter(PbfExporterRegistration.Create());
        builder.AddPacketExporter(ParquetExporterRegistration.Create());
        builder.AddPacketExporter(DuckDbExporterRegistration.Create());
    }
}
