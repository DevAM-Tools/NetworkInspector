<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# PCAPNG Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`PcapngExporter` writes captured frames to `.pcapng` files that Wireshark, `tshark`, and every other PCAPNG
reader can open. It is a frame exporter (`IFrameListener`): no protocol parsing is needed.

## Highlights

- **Capture context survives a conversion.** Interface names, descriptions, and the IDB options the PCAPNG
  reader stores (`if.hardware`, `if.os`, `if.speed`, `if.txspeed`, `if.rxspeed`, `if.filter`, `if.fcs_length`)
  are written back. The section header falls back to the source file's `capture.*` values.
- **Any link type.** DLT values are passed through, also values outside the `LinkType` enum.
- **Multi-interface output.** One IDB is created on demand for each `(FrameInterfaceId, LinkType)` pair.
- **Nanosecond timestamps by default**, configurable from 10⁰ to 10⁻¹⁹ seconds.

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
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithSnapLength(uint)` | Truncate stored bytes per frame | 262 144 |
| `WithTimestampResolution(byte)` | Power-of-ten exponent 0..19 | 9 (ns) |
| `WithHardware` / `WithOs` | SHB `shb_hardware` / `shb_os` | source file |
| `WithApplication` / `WithComment` | SHB `shb_userappl` / comment | source file |
| `WithTargetFrameCount(int)` | Stop after N frames (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`WithTimestampResolution` rejects bit 7 (binary resolution) and values above 19.

## Format Notes

- `FrameInterfaceInfo.UiName` becomes IDB `if_name`, `Description` becomes `if_description`, a
  `if.fcs_length` byte becomes `if_fcslen`. Per-packet EPB flags are not written.
- Without a builder value, `shb_hardware`, `shb_os`, and `shb_userappl` come from the first frame's interface
  properties `capture.hardware`, `capture.os`, and `capture.application`. A PCAPNG-to-PCAPNG conversion therefore
  keeps its capture context.
- Snap truncation writes EPB `orig_len` from the frame's length. Sources keep only the captured bytes, so the
  original `orig_len` of an already truncated capture is not preserved.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()` and `OnFinish()` from one thread.
- The file is created lazily on the first frame. An empty export still produces a valid file with a section header.
- The CLI value `pcap` is an alias: there is no classic PCAP writer, the output is always PCAPNG.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [PCAPNG reader](../../NetworkInspector.Sources/README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
