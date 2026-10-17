<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Exporters

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Exporters)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Exporters?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Exporters)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Write captured frames back to capture files, or parsed packets to analysis formats. Eight formats share one
builder pattern and one lifecycle:

- **Frame exporters** (`IFrameListener`) convert captures without parsing: PCAPNG, BLF, ASC.
- **Packet exporters** (`IPacketListener`) serialize parsed field trees: JSON, CSV, Text, PBF, Parquet.
- **DuckDB** lives in the add-on package
  [`NetworkInspector.Exporters.DuckDb`](../NetworkInspector.Exporters.DuckDb/README.md).

## Highlights

- **One API for every format.** `CreateBuilder()` → `To…()` → `With…()` → `Build()` → `OnFrame` / `OnPacket` →
  `OnFinish()`. Switching formats is a one-line change.
- **Round trips you can trust.** PCAPNG interface options, BLF channel names, CAN DLC 9–15, the LIN protected id,
  and the LIN checksum model survive read → write → read. The round-trip tests are cross-validated with `tshark` 4.6.
- **Analytics-ready columnar output.** Parquet, DuckDB, and PBF Columnar share one batch model with native
  unsigned 64-bit columns and a field catalog.
- **No stray files.** Output is opened lazily on the first item, so a filter that matches nothing writes nothing.
- **Errors are reported, not swallowed.** Tolerant mode (default) skips bad items and raises `ItemSkipped`;
  strict mode stops at the first one. Every exporter counts written, skipped, and failed items.
- **Safe cleanup.** `OnFinish()` always releases every buffer and the output, even after a failing trailer, and
  reports release failures as one `AggregateException`. `Dispose()` is an idempotent `OnFinish()`.
- **Size-based splitting.** `EstimatedOutputBytes` reports the live output size, which the `ni` CLI uses for
  `--split-size`.

## Install

```bash
dotnet add package NetworkInspector.Exporters
```

To read captures and parse packets you also need:

```bash
dotnet add package NetworkInspector.Sources
dotnet add package NetworkInspector.Protocols
```

`NetworkInspector.Core` comes in as a dependency.

## Quick Start

### Parse A Capture And Export JSON

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Settings;
using NetworkInspector.Exporters.Json;
using NetworkInspector.Protocols;
using NetworkInspector.Sources.Pcapng;

// One registry for the stack and the source; a different registry makes TryParse fail.
FrameInterfaceRegistry registry = new();
StackBuilder builder = new(new SettingsManager(), registry);
builder.RegisterStandardProtocols();
using Stack stack = builder.Build();

using PcapngSource source = PcapngSource.Open("capture.pcapng");
source.Start(registry.RegisterSource(source), registry);

using JsonExporter exporter = JsonExporter.CreateBuilder()
    .ToFile("packets.json")
    .WithFormat(JsonExportFormat.Pretty)
    .Build();
exporter.ItemSkipped += (_, e) => Console.Error.WriteLine($"#{e.ItemIndex} {e.Kind}: {e.Message}");

// Packet exporters need a field tree: the default ParseOptions builds one.
// First-parse ids must be dense (0, 1, 2, ...): advance only on success.
ParseOptions options = new();
int nextId = 0;
while (source.NextFrame() is { } frame)
{
    if (!Packet.TryParse(new PacketId(nextId), stack, frame, in options, out Packet? packet, out ParseFailure failure)
        || packet is null)
    {
        Console.Error.WriteLine($"frame {frame.Id.Value}: {failure}");
        continue;
    }

    nextId++;
    if (!exporter.OnPacket(packet))
    {
        break;
    }
}

exporter.OnFinish();
Console.WriteLine($"{exporter.WrittenCount} packets written, {exporter.SkippedCount} skipped");
```

Replace `JsonExporter` with `CsvExporter`, `TextExporter`, `PbfExporter`, or `ParquetExporter` to change the
format; the loop stays the same.

### Convert Frames Without Parsing (PCAPNG → BLF)

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Blf;
using NetworkInspector.Sources.Pcapng;

FrameInterfaceRegistry registry = new();
using PcapngSource source = PcapngSource.Open("capture.pcapng");
source.Start(registry.RegisterSource(source), registry);

using BlfExporter exporter = BlfExporter.CreateBuilder()
    .ToFile("capture.blf")
    .WithCompressionLevel(BlfCompressionLevel.Default)
    .Build();

while (source.NextFrame() is { } frame)
{
    if (!exporter.OnFrame(frame))
    {
        break;
    }
}

exporter.OnFinish();
```

## Choose A Format

| Exporter | Kind | Output | Best for |
| --- | --- | --- | --- |
| [`PcapngExporter`](Pcapng/README.md) | Frames | `.pcapng` | Wireshark and capture exchange |
| [`BlfExporter`](Blf/README.md) | Frames | `.blf` | Vector and automotive toolchains |
| [`AscExporter`](Asc/README.md) | Frames | `.asc` | Readable bus traces |
| [`JsonExporter`](Json/README.md) | Packets | `.json` | Scripts and log pipelines |
| [`CsvExporter`](Csv/README.md) | Packets | `.csv` | Spreadsheets and SQL imports |
| [`TextExporter`](Text/README.md) | Packets | `.txt` | Protocol trees for people |
| [`PbfExporter`](Pbf/README.md) | Packets | `.pbf` | Compact binary at scale |
| [`ParquetExporter`](Parquet/README.md) | Packets | directory | DuckDB, pandas, Polars, Spark |
| [`DuckDbExporter`](../NetworkInspector.Exporters.DuckDb/README.md) | Packets | `.duckdb` | SQL in one file |

Output targets: every exporter supports `ToFile`. All except Parquet and DuckDB also support `ToStream` (the caller
keeps the stream open and owns it) and `ToStdout` (the exporter closes stdout when it finishes).

## Lifecycle And Errors

| Member | Meaning |
| --- | --- |
| `OnFrame` / `OnPacket` | Write one item; `false` means stop (limit, cancellation, strict error) |
| `OnFinish()` | Write trailers, flush, release; idempotent |
| `ErrorTolerance` | `ErrorToleranceMode.Tolerant` (default) or `Strict` |
| `ItemSkipped` | Event with `ItemIndex`, `Kind` (`ExportErrorKind`), `Message` |
| `WrittenCount`, `SkippedCount`, `ErrorCount`, `HasErrors` | Statistics |
| `EstimatedOutputBytes` | Live output size estimate |

- Call `OnFinish()` before disposal to write final metadata. A failing trailer or flush is reported through
  `ItemSkipped` and `HasErrors`; every buffer and the output are still released. If releasing fails,
  `OnFinish()` throws one `AggregateException` after every step ran.
- Each builder also offers `WithTargetFrameCount` / `WithTargetPacketCount` and `WithCancellationToken`.
- Packet exporters skip packets parsed with `FieldTreeMode.Skip`.

## Round Trips

Frames read by `NetworkInspector.Sources` and written back keep their bus fields and interface metadata:

- Classic CAN bus DLC 9–15 (`len8_dlc`), remote-frame DLC, the LIN protected id, and the LIN checksum model
  survive BLF and ASC.
- BLF and ASC take the channel from `blf.channel`, `asc.channel`, or a trailing number in the interface name.
  BLF writes interface names back as AppText channel names.
- PCAPNG writes `if_name`, `if_description`, and the `if.*` interface options back, and passes unknown DLT
  values through.
- BLF start dates use UTC on both sides by default (`BlfExporter.Builder.WithTimestampTimeZone`). Use
  `TimeZoneInfo.Local` for files that Vector tools or `tshark` will read.
- ASC timestamps carry 6 to 9 fraction digits (`AscExporter.Builder.WithTimestampDecimals`, default 6, rounded).

## Limits And Thread-Safety Notes

- Exporter instances are single-threaded: call `OnFrame` / `OnPacket` / `OnFinish` from one thread.
- File targets do not create missing parent directories (Parquet creates its own directory) and overwrite
  existing files.
- Exporters do not validate upstream trust assumptions; validate and bound input at the source and parse boundary.
- Format details (compression, schemas, dialects) are documented in each format README.

## Safe Usage (STRIDE)

- **Spoofing**: Keep source provenance outside the export when chain of custody matters.
- **Tampering**: Log `ItemSkipped` decisions in calling code.
- **Repudiation**: Store the exporter configuration next to the output for reproducibility.
- **Information disclosure**: JSON, CSV, Text, and columnar outputs can contain payload data. Protect them.
- **Denial of service**: Use target counts, cancellation, and split limits in high-volume runs.
- **Elevation of privilege**: Run exports with least-privilege write access.

## Learn More

- [Exporter guide](EXPORTER_GUIDE.md): finish sequence, shared bus-field representation, BLF timestamps, test layout
- [Sources](../NetworkInspector.Sources/README.md) · [Core](../NetworkInspector.Core/README.md) ·
  [CLI](../NetworkInspector.CLI/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
