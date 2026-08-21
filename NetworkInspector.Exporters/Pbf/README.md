<!-- Copyright © 2026 DevAM. All rights reserved. -->

# PBF Exporter

Packet binary exporter for compact and high-throughput packet persistence.

## What This Is

The `PbfExporter` serializes parsed packets through `IPacketListener` into the NetworkInspector PBF
format (magic `NETWORK-INSPECTOR-PBF-FORMAT-v1`).

## Why Use It

- More compact than text-oriented formats for large datasets.
- Supports row-like (`Standard`) and analytics-oriented (`Columnar`) layouts.
- Optional per-block compression and trailer indexing.
- `Columnar` blocks share the same `ColumnarPacketBatch` accumulator as the Parquet and DuckDB
  exporters (typed values, topology, detail flags).

## Layout Options

| Option | Purpose |
| --- | --- |
| `PbfExportFormat.Standard` | Sequential packet-oriented layout (typed payloads; same-as-previous without string formatting) |
| `PbfExportFormat.Columnar` | Columnar layout for analytical post-processing |

## Columnar Format

`Columnar` blocks wrap a `ColumnarPacketBatch` (see `NetworkInspector.Exporters/Columnar/`) and
serialize it to protobuf:

- Packet IDs (delta `sint64`) and timestamps (delta `sint64`), plus optional per-packet info
  strings and frame bytes.
- Topology rows (`packet_id`, `node_id`, `field_id`, `parent_node_id`) when
  `ColumnarDetailFlags.IncludeTopology` is set.
- One field block per distinct field ID, each carrying typed value columns and optional plain
  string custom representation/text columns.

Field-number layout is documented in `PbfFieldNumbers.cs`. The per-block field-presence trailer
(`TrailerFieldBitmap`) unions topology field IDs and field-column bag keys.

## Quick Start

```csharp
using NetworkInspector.Exporters.Pbf;

using PbfExporter exporter = PbfExporter.CreateBuilder()
    .ToFile("packets.pbf")
    .WithFormat(PbfExportFormat.Columnar)
    .WithCompressed(true)
    .WithTrailerIndex(true)
    .Build();

foreach (Packet packet in packets)
{
    if (!exporter.OnPacket(packet))
    {
        break;
    }
}

exporter.OnFinish();
```

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithFormat(format)` | Choose Standard or Columnar layout |
| `WithCompressed(compressed)` | Enable or disable block compression |
| `WithMaxPacketsPerBlock(count)` | Flush block after N packets |
| `WithMaxBlockSize(bytes)` | Flush block by size threshold |
| `WithTrailerIndex(include)` | Include block index in trailer |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |
| `WithDetailFlags(flags)` | Optional columnar columns (info, frame bytes, customs, topology) |
| `WithTimestampSorted(sorted)` | Declares packets arrive in non-decreasing timestamp order |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- Format is binary and optimized for tooling, not manual inspection.

## Links

- [Exporters hub](../README.md)
- [PBF source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Pbf)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
