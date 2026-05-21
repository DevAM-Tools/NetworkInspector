<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Exporters

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Exporters)](https://www.nuget.org/packages/NetworkInspector.Exporters)

User-focused export package for writing either raw frames or parsed packets.

## What This Is

`NetworkInspector.Exporters` contains two exporter families:

- Frame exporters (`IFrameListener`): preserve capture frame structure.
- Packet exporters (`IPacketListener`): serialize parsed packet data for analysis workflows.

## Why It Stands Out

- One package covers conversion-oriented and analysis-oriented outputs.
- Consistent builder pattern across formats.
- Practical output targets (file, stream, and stdout depending on exporter).

## Install

```bash
dotnet add package NetworkInspector.Exporters
```

Typical packet-export stacks also include:

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
dotnet add package NetworkInspector.Sources
```

## Choose Your Export Path

| Goal | Input Type | Recommended Path | Typical Formats |
| --- | --- | --- | --- |
| Repackage or transform captures | Frames | Frame exporter | PCAPNG, BLF, ASC |
| Produce analysis-ready records | Parsed packets | Packet exporter | JSON, PBF, CSV, Text |

## Quick Start

### Frame Export Quick Start (PCAPNG)

```csharp
using NetworkInspector.Exporters.Pcapng;

using PcapngExporter exporter = PcapngExporter.CreateBuilder()
    .ToFile("capture.pcapng")
    .Build();

foreach (Frame frame in frames)
{
    if (!exporter.OnFrame(frame))
    {
        break;
    }
}

exporter.OnFinish();
```

### Packet Export Quick Start (JSON)

```csharp
using NetworkInspector.Exporters.Json;

using JsonExporter exporter = JsonExporter.CreateBuilder()
    .ToFile("packets.json")
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

## Export Format Map

| Exporter | Output | Use When | Guide |
| --- | --- | --- | --- |
| `PcapngExporter` | `.pcapng` | Broad tool compatibility and standard capture exchange | [Pcapng/README.md](Pcapng/README.md) |
| `BlfExporter` | `.blf` | Automotive and CAN-focused pipelines | [Blf/README.md](Blf/README.md) |
| `AscExporter` | `.asc` | Human-readable CAN trace files | [Asc/README.md](Asc/README.md) |
| `JsonExporter` | `.json` | General analytics and downstream processing | [Json/README.md](Json/README.md) |
| `PbfExporter` | `.pbf` | Compact binary output at scale | [Pbf/README.md](Pbf/README.md) |
| `CsvExporter` | `.csv` | Spreadsheet and tabular workflows | [Csv/README.md](Csv/README.md) |
| `TextExporter` | `.txt` | Readable protocol-tree output | [Text/README.md](Text/README.md) |

## Common Tasks

### Export To Stdout

Use builders with stdout targets for pipeline-first workflows.

### Stop Early On Limits

Pair exporters with upstream count/time/cancellation controls to avoid unbounded runs.

### Finalize Correctly

Call `OnFinish()` before disposal to flush final metadata and trailing structures.

## Limits And Thread-Safety Notes

- Treat exporter instances as single-threaded unless a format README states otherwise.
- Exporters do not validate upstream trust assumptions; validate and bound input at source/parse boundaries.
- Feature details (compression, schema variants, dialect nuances) are format-specific and documented in each sub-README.

## Safe Usage (STRIDE)

- **Spoofing**: Preserve source provenance metadata outside the exporter output when chain-of-custody matters.
- **Tampering**: Surface and log malformed input handling decisions in calling code.
- **Repudiation**: Persist exporter configuration with output artifacts for reproducibility.
- **Information disclosure**: Treat JSON/CSV/Text outputs as potentially sensitive and protect destinations accordingly.
- **Denial of service**: Enforce cancellation and bounded processing in high-volume runs.
- **Elevation of privilege**: Run exporter processes with least-privilege write access.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)
- [Pcapng exporter](Pcapng/README.md)
- [Blf exporter](Blf/README.md)
- [Asc exporter](Asc/README.md)
- [Json exporter](Json/README.md)
- [Pbf exporter](Pbf/README.md)
- [Csv exporter](Csv/README.md)
- [Text exporter](Text/README.md)
- [Root overview](../README.md)

## License

[MIT License](../LICENSE)
