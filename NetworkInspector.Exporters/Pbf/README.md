<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# PBF Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`PbfExporter` writes parsed packets to the NetworkInspector Packet Binary Format (`.pbf`, magic
`NETWORK-INSPECTOR-PBF-FORMAT-v1`): a compact, protobuf-based file for storing large parse results and reading
them back fast. It is a packet exporter (`IPacketListener`): packets must be parsed with a field tree
(`FieldTreeMode.Build`, the default).

## Highlights

- **Compact.** Typed binary values, LZ4 block compression, and delta-encoded ids and timestamps.
- **Two layouts.** `Standard` is packet-oriented; `Columnar` stores one typed column block per field for
  analytical post-processing.
- **Indexed.** The trailer holds a block index and per-block field-presence bitmaps, so readers can skip blocks.
- **Same columnar model as Parquet and DuckDB**, with the same detail flags.

## Layouts

| Layout | Purpose |
| --- | --- |
| `PbfExportFormat.Standard` (default) | Sequential packet records with typed payloads |
| `PbfExportFormat.Columnar` | Packet ids, timestamps, topology, and one field block per field id |

A `Columnar` block contains:

- packet ids and timestamps (delta `sint64`), optional info strings and frame bytes,
- topology rows (`packet_id`, `node_id`, `field_id`, `parent_node_id`) with `ColumnarDetailFlags.IncludeTopology`,
- one field block per distinct field id, with typed value columns and optional custom representation and text.

Field numbers are defined in [PbfFieldNumbers.cs](PbfFieldNumbers.cs).

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Pbf;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WritePbf(IEnumerable<Packet> packets)
{
    using PbfExporter exporter = PbfExporter.CreateBuilder()
        .ToFile("packets.pbf")
        .WithFormat(PbfExportFormat.Columnar)
        .Build(); // compressed, with trailer index

    foreach (Packet packet in packets)
    {
        if (!exporter.OnPacket(packet))
        {
            break;
        }
    }

    exporter.OnFinish();
}
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithFormat(format)` | `Standard` or `Columnar` | `Standard` |
| `WithCompressed(bool)` | LZ4 block compression | `true` |
| `WithMaxPacketsPerBlock(int)` | Flush a block after N packets | 50 000 |
| `WithMaxBlockSize(long)` | Flush a block at this estimated size | 16 MiB |
| `WithTrailerIndex(bool)` | Block index in the trailer | `true` |
| `WithDetailFlags(ColumnarDetailFlags)` | Optional columnar columns (`Columnar` only) | `All` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps (metadata) | `false` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`ColumnarDetailFlags` lives in `NetworkInspector.Exporters.Columnar`: `IncludeInfo`, `IncludeFrameBytes`,
`IncludeCustomRepresentation`, `IncludeCustomText`, `IncludeTopology`, `All`, `None`.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- Blocks of 64 bytes or less, and blocks that do not shrink, are stored uncompressed.
- The per-block field-presence bitmap covers up to 32 768 field ids.
- Packets parsed with `FieldTreeMode.Skip` are skipped.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
