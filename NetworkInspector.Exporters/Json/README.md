<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# JSON Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`JsonExporter` writes the full field tree of every parsed packet as JSON, for scripts, log pipelines, and any
tool that reads JSON. It is a packet exporter (`IPacketListener`): packets must be parsed with a field tree
(`FieldTreeMode.Build`, the default).

## Highlights

- **Three styles:** compact for volume, pretty for people, a flat array for downstream tools.
- **Compact by default.** Repeated field metadata is deduplicated, which keeps large exports small.
- **SIMD string escaping** on the hot path.
- **Valid JSON always.** Every style writes one JSON array; an empty export is an empty array.

## Output Styles

| Style | Best For |
| --- | --- |
| `JsonExportFormat.Compact` (default) | Smallest output, machine ingestion |
| `JsonExportFormat.Pretty` | Human inspection and debugging |
| `JsonExportFormat.Array` | Flat full-key records for downstream tooling |

The field metadata key `type` / `TY` holds the numeric `FieldType` value in **Compact** and the enum member
name (for example `"MacAddress"`, `"U64"`) in **Pretty** and **Array**.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Json;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteJson(IEnumerable<Packet> packets)
{
    using JsonExporter exporter = JsonExporter.CreateBuilder()
        .ToFile("packets.json")
        .WithFormat(JsonExportFormat.Pretty)
        .Build();

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
| `WithUiName` / `WithDescription` | User-facing metadata (not written to the output) | — |
| `WithFormat(format)` | JSON style | `Compact` |
| `WithFlushPerPacket(bool)` | Flush after each packet for live consumers | `false` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- The output is one JSON array, not newline-delimited JSON.
- Packets parsed with `FieldTreeMode.Skip` are skipped.
- The output contains every decoded field, including payload-derived text. Treat it as sensitive.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
