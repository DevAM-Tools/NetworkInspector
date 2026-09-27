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
- CAN FD (SocketCAN FDF on `CanSocketcan`, BLF object type 101)
- CAN XL (SocketCAN XLF on `CanSocketcan`, BLF object type 139)
- FlexRay
- LIN

Unsupported link types (for example loopback/raw IP variants) are skipped and counted in exporter statistics.

## Wire format

The exporter writes these BLF object types:

| Family | Object type |
| --- | --- |
| Ethernet | 120 (`ETHERNET_FRAME_EX`, raw frame after a 32-byte header) |
| CAN classic | 1 |
| CAN error | 2 |
| CAN FD | 101 |
| CAN XL | 139 |
| FlexRay | 50 |
| LIN data | 57 (`object_version` 1) |
| LIN send / CRC / receive error | 58 / 60 / 61 |
| LIN sleep / wakeup | 20 / 62 |

Compression is zlib inside LOG_CONTAINER objects (or none when `BlfCompressionLevel.None` is selected). Object timestamps are nanoseconds (flags word 2) relative to the millisecond `start_date` anchor. Direction is written as 0 (RX) because `Frame` does not carry direction — see [BlfObjectPayloads.cs](BlfObjectPayloads.cs).

Type 120 copies the Ethernet bytes as-is, so VLAN TCI 0, QinQ inner tags, and FCS trailers survive a BLF round-trip. The Type 71 builder remains for decomposed-header tests; it is not the exporter path.

FlexRay Type 50 stores Channel A/B in `channelMask` and writes the ISO header CRC into both `headerCrc1` and `headerCrc2`. LIN sleep (event byte 0x01 or 0x02), wakeup (0x04), and error bits 0x08 / 0x02 / 0x01 are written as Types 20, 62, 60, 61, and 58. They are not written as Type 57.

`blf.channel` is written as stored, including 0. When the property is absent the channel is 1. `blf.hw_channel` (`ushort`) is written at Type 120 offset 6 with flags bit 0x0002.

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
- [BLF reader](../../NetworkInspector.Sources/Blf)
- [BLF exporter implementation](.)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
