<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# ASC Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`AscExporter` writes captured frames as Vector ASCII (`.asc`) trace text: readable in any editor and accepted by
ASC-capable tools such as CANalyzer. It is a frame exporter (`IFrameListener`): no protocol parsing is needed.

## Highlights

- **Broad frame coverage**, including Ethernet (`ETH`) and CAN XL (`CANXL` / `XLFF`).
- **Exact timestamps.** Times are computed in integers from the frame nanoseconds; with 9 fraction digits a
  round trip keeps every nanosecond.
- **Bus details survive.** CAN DLC 9–15, remote-frame DLC, the LIN checksum model (`CSM = classic|enhanced`), and
  the channel are written so that the ASC reader restores them.
- **Plain text** for reviews, diffs, and triage logs.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Asc;

// frames: any IEnumerable<Frame>, for example from NetworkInspector.Sources.
static void WriteAsc(IEnumerable<Frame> frames)
{
    using AscExporter exporter = AscExporter.CreateBuilder()
        .ToFile("capture.asc")
        .WithTimestampDecimals(AscExporter.MaxTimestampDecimals) // 9 digits: nanosecond round trip
        .Build();

    foreach (Frame frame in frames)
    {
        if (!exporter.OnFrame(frame))
        {
            break;
        }
    }

    exporter.OnFinish();
}
```

## Supported Frame Families

| Family | Notes |
| --- | --- |
| CAN classic, CAN FD | DLC as one hex digit (`base hex`) |
| CAN XL | `CANXL` / `XLFF`; at least 12 bytes, length 1..2048 |
| LIN | 6-bit id, checksum, `CSM` model |
| FlexRay | hex cycle and byte count |
| Ethernet | `ETH` line, hex length, no FCS, at most 1518 bytes |

Other link types, and frames outside these limits, are skipped and reported through `ItemSkipped`.

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithTimestampDecimals(int)` | Fraction digits, `MinTimestampDecimals` (6) to `MaxTimestampDecimals` (9) | 6 |
| `WithTargetFrameCount(int)` | Stop after N frames (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Line Format Details

- Timestamps are rounded half up to the configured digits; a carry moves into the seconds.
- Classic CAN: a bus DLC of 9–15 (SocketCAN `len8_dlc`, byte 7) is written as `9`–`F` with 8 data bytes.
  Remote frames write `r {dlc}` with the requested count from byte 4.
- LIN: the 6-bit id (`PID & 0x3F`), the checksum, and `CSM = classic` or `CSM = enhanced` from DLT_LIN byte 4
  bits 1–0 (enhanced when the bits do not state a model).
- The channel is resolved once per interface: `blf.channel`, else `asc.channel`, else a trailing number ≥ 1 in the
  interface name, else 1. An unusable channel property falls back to channel 1.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()` and `OnFinish()` from one thread.
- The file is created lazily on the first frame; an empty export still writes a valid header.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [ASC reader](../../NetworkInspector.Sources/README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
