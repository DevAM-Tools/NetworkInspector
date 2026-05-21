<!-- Copyright © 2026 DevAM. All rights reserved. -->

# Text Exporter

Human-readable packet tree exporter for debugging and operational inspection.

## What This Is

The `TextExporter` serializes parsed packets through `IPacketListener` into readable text output similar to protocol tree views.

## Why Use It

- Fast way to inspect parse results without custom viewers.
- Adjustable detail level for summary vs deep inspection.
- Useful for CI artifacts and triage logs.

## Detail Levels

| Level | Output |
| --- | --- |
| `TextDetailLevel.Summary` | Protocol containers only |
| `TextDetailLevel.Standard` | Default field tree with values |
| `TextDetailLevel.Full` | Adds full bytes-heavy detail |

## Quick Start

```csharp
using NetworkInspector.Exporters.Text;

using TextExporter exporter = TextExporter.CreateBuilder()
    .ToFile("packets.txt")
    .WithDetailLevel(TextDetailLevel.Standard)
    .WithMaxTextLength(256)
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

### Create Compact Triage Logs

Use `WithDetailLevel(TextDetailLevel.Summary)` for high-level packet overviews.

### Disable Value Truncation

Use `WithMaxTextLength(0)` when you need complete string/bytes values.

### Stream To Console Pipelines

Use `ToStdout()` for command chaining and live inspection.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithDetailLevel(level)` | Select summary/standard/full output |
| `WithMaxTextLength(maxLength)` | Truncate long text/bytes values (`0` = unlimited) |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- Full detail can produce very large outputs on byte-heavy traffic.
- Use packet count limits and cancellation in unattended runs.

## Links

- [Exporters hub](../README.md)
- [Text source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Text)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
