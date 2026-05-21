<!-- Copyright © 2026 DevAM. All rights reserved. -->

# ASC Exporter

Vector CANalyzer-compatible ASCII (`.asc`) frame exporter.

## What This Is

The `AscExporter` writes supported bus frames as human-readable ASC text via `IFrameListener`.

## Why Use It

- Readable text output for debugging, triage, and exchange with ASC-capable tooling.
- Useful for CAN-focused analysis workflows where text logs are preferred.
- Frame-level conversion path with consistent exporter lifecycle.

## Quick Start

```csharp
using NetworkInspector.Exporters.Asc;

using AscExporter exporter = AscExporter.CreateBuilder()
    .ToFile("capture.asc")
    .WithTargetFrameCount(100_000)
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

## Supported Frame Families

- CAN classic and CAN FD
- LIN
- FlexRay

Unsupported link types (for example Ethernet and CAN XL) are skipped and counted in exporter statistics.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithTargetFrameCount(count)` | Stop after N frames (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Common Tasks

### Generate Readable Logs

Use ASC output when operators or reviewers need plain-text frame traces.

### Control Batch Size

Use `WithTargetFrameCount(...)` in automation pipelines to cap output volume.

### Integrate With Stream Pipelines

Use `ToStdout()` for command chaining where downstream tools consume text streams.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()`/`OnFinish()` sequentially.
- Output is ASC dialect-oriented and optimized for supported frame families.
- Lazy initialization delays file creation until first write (or explicit finish for empty outputs).

## Links

- [Exporters hub](../README.md)
- [Asc source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Asc)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
