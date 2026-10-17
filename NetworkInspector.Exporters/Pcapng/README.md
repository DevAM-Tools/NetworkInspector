<!-- Copyright © 2026 DevAM. All rights reserved. -->

# Pcapng Exporter

PCAPNG frame exporter for capture-preserving workflows.

## What This Is

The `PcapngExporter` writes raw frames to `.pcapng` output via `IFrameListener`.
It is designed for format-preserving conversion and tool interoperability.

## Why Use It

- Broad ecosystem compatibility with other PCAPNG readers.
- On-demand interface registration for multi-interface captures. `FrameInterfaceInfo.UiName` is written as IDB `if_name`, `Description` as `if_description`. A `if.fcs_length` byte is written as `if_fcslen`. The interface options the PCAPNG reader stores (`if.hardware`, `if.os`, `if.speed`, `if.txspeed`, `if.rxspeed`, `if.filter`) are written back as IDB options through the same table the reader uses. Per-packet EPB flags are not written.
- Configurable snap length and timestamp resolution. Snap truncation writes EPB `orig_len` from the pre-snap `Frame.Length`. Readers later expose captured bytes only.

## Quick Start

```csharp
using NetworkInspector.Exporters.Pcapng;

using PcapngExporter exporter = PcapngExporter.CreateBuilder()
    .ToFile("capture.pcapng")
    .WithSnapLength(65535)
    .WithTimestampResolution(9)
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

## Common Tasks

### Capture-Preserving Conversion

Use `PcapngExporter` when you need frame-level output without protocol parsing.

### Multi-Interface Output

The exporter creates interface descriptions on demand for each `(FrameInterfaceId, LinkType)` combination.

### Add File Metadata

Use SHB metadata options to include capture context:

- `WithHardware(...)`
- `WithOs(...)`
- `WithApplication(...)`
- `WithComment(...)`

Without a builder value, `shb_hardware`, `shb_os`, and `shb_userappl` come from the first frame's interface
properties `capture.hardware`, `capture.os`, `capture.application` (what the PCAPNG reader stores from the source
file's SHB), so a PCAPNG-to-PCAPNG conversion keeps the capture context.

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithSnapLength(length)` | Truncate stored frame bytes per packet |
| `WithTimestampResolution(resolution)` | Decimal power-of-ten exponent 0..19 (9 = nanoseconds). Bit 7 and values above 19 throw. |
| `WithHardware(...)`, `WithOs(...)`, `WithApplication(...)`, `WithComment(...)` | Set section header metadata |
| `WithTargetFrameCount(count)` | Stop after N frames (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()`/`OnFinish()` sequentially.
- Link types are written through as PCAPNG DLT values (no exporter-side DLT whitelist), also values outside the `LinkType` enum, which the PCAPNG reader passes through. Malformed write failures can be skipped and reported through exporter diagnostics.
- Lazy initialization delays file creation until first write (or explicit finish for empty outputs).

## Links

- [Exporters hub](../README.md)
- [Pcapng source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Pcapng)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
