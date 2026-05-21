<!-- Copyright © 2026 DevAM. All rights reserved. -->

# Pcapng Exporter

PCAPNG frame exporter for capture-preserving workflows.

## What This Is

The `PcapngExporter` writes raw frames to `.pcapng` output via `IFrameListener`.
It is designed for format-preserving conversion and tool interoperability.

## Why Use It

- Broad ecosystem compatibility (Wireshark, tshark, tcpdump, Scapy, and other PCAPNG tools).
- On-demand interface registration for multi-interface captures.
- Configurable snap length and timestamp resolution.

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

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithSnapLength(length)` | Truncate stored frame bytes per packet |
| `WithTimestampResolution(resolution)` | Set timestamp precision exponent |
| `WithHardware(...)`, `WithOs(...)`, `WithApplication(...)`, `WithComment(...)` | Set section header metadata |
| `WithTargetFrameCount(count)` | Stop after N frames (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnFrame()`/`OnFinish()` sequentially.
- Unsupported or malformed inputs can be skipped and reported through exporter diagnostics.
- Lazy initialization delays file creation until first write (or explicit finish for empty outputs).

## Links

- [Exporters hub](../README.md)
- [Pcapng source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Pcapng)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
