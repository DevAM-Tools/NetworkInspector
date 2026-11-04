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
- **Round trips verified against a reference reader** in the exporter test suite.

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
        .WithTimestampTimeZone(TimeZoneInfo.Local) // only when Vector tools or other local-time readers read the file
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
| `WithWriteMode(ExportWriteMode)` | `Background` writer thread (zlib + write) or `Synchronous` on the caller thread | `Background` |
| `WithMaxContainerSize(long)` | Uncompressed size of a log container, 64 KiB – 64 MiB | 10 MiB |
| `WithTargetFrameCount(int)` | Stop after N frames (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

**Write mode and cores.** The `Background` mode compresses and writes a container on a second thread while the caller fills the
next one. It pays off when a second core is free (measured +3 % frames/s on an idle machine); on a machine whose cores are busy
the second thread brings no overlap and the hand-off costs up to 7 % against the `Synchronous` mode. Choose `Synchronous` (`-f "blf:writeMode=sync"`) for a busy machine or when the stream must stay on the calling thread.

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
  (default UTC, the same default as `BlfSourceOptions.TimestampTimeZone`). Vector tools and other common readers read it as
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

CAN FD (Type 101) and CAN XL (Type 139) objects carry channels 0–255; a frame of those types on a higher channel is
skipped with `ExportErrorKind.MalformedData` and a message that names the limit, because writing the low byte would
put the frame on another bus. A CAN FD frame that declares more than 64 data bytes is skipped the same way instead of
being cut to 64 bytes.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()` and `OnFinish()` from one thread. In the default `Background` write mode one writer
  thread ("BLF export writer") compresses (zlib) and writes each full container while the caller fills the next one. With
  `ToStream` the caller must not use the stream until `OnFinish` returned; `OnFinish` waits for every container before it
  patches the header. A container that fails to write is reported one container later (at the latest by `OnFinish`);
  its frames count as skipped. The container size is a memory limit (two containers in background mode, plus one buffer of
  the same size for the compressed output unless compression is `None`); a value outside 64 KiB – 64 MiB is refused with a
  message that names the range.
- `WithCompressionLevel` accepts only the four defined `BlfCompressionLevel` values; any other value throws
  `ArgumentOutOfRangeException`.
- Link types without a BLF mapping (for example raw IP or loopback) are skipped and counted in `SkippedCount`.
- On a non-seekable target (stdout, a pipe) the header statistics (object count, end date) cannot be patched.
- On a seekable stream that already holds data, the BLF file starts at the stream position when the first frame
  arrives, and the header patch goes back to that position (bytes before it stay untouched).
- The file is created lazily on the first frame. A file that cannot be created is reported through `ItemSkipped` and
  counted as an error; `OnFrame()` returns `false` and does not throw.
- LIN data frames: the DLC is written as the number of data bytes (at most 8, and at most the bytes present in a
  truncated capture). A frame that states no checksum model is written as model 0 (classic), whereas the ASC exporter
  writes `enhanced` for it.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [BLF reader](../../NetworkInspector.Sources/README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
