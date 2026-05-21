<!-- Copyright © 2026 DevAM. All rights reserved. -->

# JSON Exporter

Packet-level JSON exporter for analysis and integration workflows.

## What This Is

The `JsonExporter` serializes parsed packets through `IPacketListener` to JSON output.

## Why Use It

- Multiple output styles for different consumers.
- Compact mode reduces repeated payload through deduplication-oriented encoding.
- File, stream, and stdout targets for both batch and pipeline usage.

## Output Styles

| Style | Best For |
| --- | --- |
| `JsonExportFormat.Compact` | Smallest payload, machine-driven ingestion |
| `JsonExportFormat.Pretty` | Human inspection and debugging |
| `JsonExportFormat.Array` | Flat full-key array output for downstream tooling |

## Quick Start

```csharp
using NetworkInspector.Exporters.Json;

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
```

## Common Tasks

### Stream JSON To Stdout

Use `ToStdout()` for pipeline scenarios.

### Reduce Output Size

Use `WithFormat(JsonExportFormat.Compact)` for high-volume exports.

### Flush Per Packet

Use `WithFlushPerPacket(true)` when downstream consumers require immediate visibility.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithFormat(format)` | Choose JSON style |
| `WithFlushPerPacket(flush)` | Flush output after each packet |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- Output reflects current packet parse tree, including any sensitive payload-derived text.
- Use cancellation and packet limits for bounded long-running exports.

## Links

- [Exporters hub](../README.md)
- [JSON source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Json)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
