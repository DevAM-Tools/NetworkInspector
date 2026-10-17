<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# BLF Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`BlfExporter` writes captured frames to Vector Binary Logging Format (`.blf`) files for automotive toolchains
such as CANalyzer and CANoe. It is a frame exporter (`IFrameListener`): no protocol parsing is needed.

## Highlights

- **Automotive and Ethernet in one file:** CAN classic, CAN FD, CAN XL, LIN, FlexRay, and Ethernet.
- **Lossless Ethernet.** Type 120 copies the bytes as they are, so VLAN TCI 0, QinQ inner tags, and FCS
  trailers survive a BLF round trip.
- **Bus details survive.** CAN DLC 9–15 (`len8_dlc`), remote-frame DLC, the LIN protected identifier, and the
  LIN checksum model are written back. Interface names become AppText channel names, which the BLF reader turns
  back into interface names.
- **Nanosecond object timestamps** relative to the millisecond `start_date` anchor.
- **Selectable zlib compression** from `None` to `Best`.
- **Round trips verified against `tshark` 4.6** in the exporter test suite.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Blf;

// frames: any IEnumerable<Frame>, for example from NetworkInspector.Sources.
static void WriteBlf(IEnumerable<Frame> frames)
{
    using BlfExporter exporter = BlfExporter.CreateBuilder()
        .ToFile("capture.blf")
        .WithCompressionLevel(BlfCompressionLevel.Default)
        .WithTimestampTimeZone(TimeZoneInfo.Local) // only when Vector tools or tshark read the file
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

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithCompressionLevel` | `None`, `Fast`, `Default`, `Best` | `Default` |
| `WithTimestampTimeZone` | Zone of the `start_date` civil time | UTC |
| `WithTargetFrameCount(int)` | Stop after N frames (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Wire Format

| Family | BLF object type |
| --- | --- |
| Ethernet | 120 (`ETHERNET_FRAME_EX`) |
| CAN classic / CAN error | 1 / 2 |
| CAN FD | 101 |
| CAN XL | 139 |
| FlexRay | 50 |
| LIN data | 57 (`object_version` 1) |
| LIN send / CRC / receive error | 58 / 60 / 61 |
| LIN sleep / wakeup | 20 / 62 |
| Channel name | 65 (`APP_TEXT`) |

- Compression is zlib inside `LOG_CONTAINER` objects, or none with `BlfCompressionLevel.None`.
- Direction is always written as 0 (RX), because `Frame` carries no direction.
  See [BlfObjectPayloads.cs](BlfObjectPayloads.cs).
- `start_date` is a SYSTEMTIME without a zone. The exporter writes civil time in `WithTimestampTimeZone`
  (default UTC, the same default as `BlfSourceOptions.TimestampTimeZone`). Vector tools and `tshark` read it as
  local time, so pass `TimeZoneInfo.Local` for files they will open.
- CAN classic writes the bus DLC: `len8_dlc` (SocketCAN byte 7) when it holds 9–15, else byte 4.
  LIN writes the 6-bit id (`PID & 0x3F`) and the checksum model from DLT_LIN byte 4 bits 1–0.
- FlexRay Type 50 stores channel A/B in `channelMask` and writes the header CRC into both CRC fields.
- LIN sleep (event byte 0x01 or 0x02) and wakeup (0x04) become Types 20 and 62. The error bits 0x08, 0x02, and
  0x01 become Types 60, 61, and 58.
- A timestamp before the anchor is clamped. The frame is still written, and one advisory `ItemSkipped` event is
  raised; it is not counted as an error.

## Channel Mapping

The channel is resolved once per interface:

1. `blf.channel` as stored (including 0),
2. else `asc.channel` (interfaces read from ASC),
3. else a trailing number ≥ 1 in the interface name (`"CAN 3"`),
4. else 1.

A channel property that exists but is unusable (out of range, wrong type) skips that interface's frames with
`ItemSkipped`. `blf.hw_channel` is written at Type 120 offset 6 with flag 0x0002. An interface name other than the
default (`"CAN 2"`, `"Ethernet 1"`) is written as an AppText channel name.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()` and `OnFinish()` from one thread.
- Link types without a BLF mapping (for example raw IP or loopback) are skipped and counted in `SkippedCount`.
- On a non-seekable target (stdout, a pipe) the header statistics (object count, end date) cannot be patched.
- The file is created lazily on the first frame.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [BLF reader](../../NetworkInspector.Sources/README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
