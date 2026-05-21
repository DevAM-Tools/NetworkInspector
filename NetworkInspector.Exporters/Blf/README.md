<!-- Copyright © 2026 DevAM. All rights reserved. -->

# BLF Exporter

Binary Logging Format (`.blf`) frame exporter for automotive capture workflows.

## What This Is

The `BlfExporter` writes raw frames to BLF output via `IFrameListener`.
It supports common automotive capture paths, including CAN/CAN FD and additional bus formats accepted by the current implementation.

## Why Use It

- Designed for BLF-based toolchains (for example Vector ecosystems).
- Configurable compression trade-off (`None`, `Fast`, `Default`, `Best`).
- Frame-level export path for large conversions without protocol parsing.

## Quick Start

```csharp
using NetworkInspector.Exporters.Blf;

using BlfExporter exporter = BlfExporter.CreateBuilder()
    .ToFile("capture.blf")
    .WithCompressionLevel(BlfCompressionLevel.Default)
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

- Ethernet
- CAN classic (`CanSocketcan`, `Can20B`)
- CAN FD (detected from SocketCAN FD flags)
- FlexRay
- LIN

Unsupported link types (for example CAN XL and loopback/raw IP variants) are skipped and counted in exporter statistics.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithCompressionLevel(level)` | Choose compression/throughput trade-off |
| `WithTargetFrameCount(count)` | Stop after N frames (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Common Tasks

### Optimize For Throughput

Use `WithCompressionLevel(BlfCompressionLevel.Fast)` for faster writes when CPU is constrained.

### Optimize For Size

Use `WithCompressionLevel(BlfCompressionLevel.Best)` when storage is the primary constraint.

### Keep Batch Runs Bounded

Combine target frame count and cancellation tokens for predictable runtime.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()`/`OnFinish()` sequentially.
- Some link types are intentionally skipped when no valid BLF object mapping exists.
- Lazy initialization delays file creation until first write (or explicit finish for empty outputs).

## Links

- [Exporters hub](../README.md)
- [BLF source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Blf)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
