<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# PCAPNG Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`PcapngExporter` writes captured frames to `.pcapng` files that every PCAPNG
reader can open. It is a frame exporter (`IFrameListener`): no protocol parsing is needed.

## Highlights

- **Capture context survives a conversion.** Interface names, descriptions, and the IDB options the PCAPNG
  reader stores (`if.hardware`, `if.os`, `if.speed`, `if.txspeed`, `if.rxspeed`, `if.filter`, `if.fcs_length`)
  are written back. The section header falls back to the source file's `capture.*` values.
- **Any link type.** DLT values are passed through, also values outside the `LinkType` enum.
- **Multi-interface output.** One IDB is created on demand for each `(FrameInterfaceId, LinkType)` pair.
- **Nanosecond timestamps by default**, configurable from 10⁰ to 10⁻¹⁹ seconds; resolutions finer than 10⁻¹⁰ saturate for current dates (the 64-bit tick count overflows).

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Pcapng;

// frames: any IEnumerable<Frame>, for example from NetworkInspector.Sources.
static void WritePcapng(IEnumerable<Frame> frames)
{
    using PcapngExporter exporter = PcapngExporter.CreateBuilder()
        .ToFile("capture.pcapng")
        .WithApplication("my-tool 1.0")
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
| `ToFile` / `ToStream` / `ToStdout` / `ToTarget` | Output target (`ToTarget` takes a host `IExportTarget` and writes the one entry `Main`) | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithSnapLength(uint)` | Truncate stored bytes per frame (1 to `int.MaxValue`) | 262 144 |
| `WithTimestampResolution(byte)` | Power-of-ten exponent 0..19 | 9 (ns) |
| `WithHardware` / `WithOs` | SHB `shb_hardware` / `shb_os` | source file |
| `WithApplication` / `WithComment` | SHB `shb_userappl` / comment | source file |
| `WithTargetFrameCount(int)` | Stop after N frames (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`WithTimestampResolution` rejects bit 7 (binary resolution) and values above 19. The four SHB setters throw
`ArgumentException` for a text above 65 535 UTF-8 bytes (the 16-bit option length of the format).

## Format Notes

- `FrameInterfaceInfo.UiName` becomes IDB `if_name`, `Description` becomes `if_description`, a
  `if.fcs_length` byte becomes `if_fcslen`. Per-packet EPB flags are not written.
- Without a builder value, `shb_hardware`, `shb_os`, and `shb_userappl` come from the first frame's interface
  properties `capture.hardware`, `capture.os`, and `capture.application`. A PCAPNG-to-PCAPNG conversion therefore
  keeps its capture context.
- Snap truncation writes EPB `orig_len` from the frame's length. Sources keep only the captured bytes, so the
  original `orig_len` of an already truncated capture is not preserved. The first frame cut at the snap length raises one
  advisory `ItemSkipped` event (`SerializationError`, not counted as skipped) that names the snap length and
  `WithSnapLength`; `TruncatedFrameCount` counts all cut frames.
- An IDB option is 16-bit long: interface metadata above 65 535 UTF-8 bytes (`if_name`, `if_description`, a text
  option), a negative number, or a value of the wrong type cannot be stored.
  It is left out, never truncated, the interface's frames are still written, and one advisory `ItemSkipped` event
  (`SerializationError`, not counted as skipped) per interface names the options that were left out.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()` and `OnFinish()` from one thread.
- The file is created lazily on the first frame. An empty export still produces a valid file with a section header.
  A file that cannot be created is reported through `ItemSkipped` and counted as an error; `OnFrame()` returns `false`
  and does not throw.
- The interface description block stores the link type in 16 bits. A frame whose link type is larger is skipped
  (`SerializationError`) instead of being written with a wrong link type.
- The CLI value `pcap` is an alias: there is no classic PCAP writer, the output is always PCAPNG.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [PCAPNG reader](../../NetworkInspector.Sources/README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
