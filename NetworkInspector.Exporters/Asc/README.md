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

- CAN classic, CAN FD, and CAN XL
- LIN
- FlexRay
- Ethernet (Vector ASC 1.3.1 section 4.2, `base hex` length, no FCS)

AFDX data frames are read as Ethernet. AFDX status, AFDX bus statistics, `ETH STAT`, and `ETH RxEr` are not read or written. Other link types are skipped and counted in exporter statistics.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithTargetFrameCount(count)` | Stop after N frames (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |
| `WithTimestampDecimals(digits)` | Fraction digits of the line timestamps, `AscExporter.MinTimestampDecimals` (6) to `MaxTimestampDecimals` (9); default 6 |

## Line Format Details

- Timestamps are computed in integers from the frame nanoseconds and rounded to the configured digits (a carry
  moves into the seconds), so 9 digits round-trip nanoseconds exactly.
- Classic CAN: the DLC is one hex digit (`base hex`). A bus DLC of 9–15 (SocketCAN `len8_dlc`, byte 7) is written as
  `9`–`F` with 8 data bytes. Remote frames write `r {dlc}` with the requested count from byte 4.
- LIN: the 6-bit id (`PID & 0x3F`), the checksum, and `CSM = classic` or `CSM = enhanced` from DLT_LIN byte 4
  bits 1–0 (enhanced when the bits do not state a model).
- The channel comes from the interface, resolved once per interface: `blf.channel`, else `asc.channel`, else a trailing
  number ≥ 1 in the interface name, else 1. An unusable channel property falls back to channel 1.

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
