<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Sources

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Sources?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Sources)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Sources?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Sources)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Read capture files fast and safely: PCAP, PCAPNG, Vector BLF, and Vector ASC, from a file with random access or
from any stream. Every source produces the same `Frame` values (bytes, timestamp, link type, interface) for the
NetworkInspector parser, exporters, and sessions.

## Highlights

- **Four formats, one interface.** `IFrameSource.NextFrame()` works the same for PCAP, PCAPNG, BLF, and ASC.
- **Millions of frames per second.** Up to 7.5 M frames/s from a stream and 3.7 M frames/s from a memory-mapped
  file (measurements below).
- **Opens instantly.** For PCAPNG and BLF a lazy scan reads only the header on `Open`; the frame index grows
  while you read.
- **Random access while reading.** `FrameById` is thread-safe and works during the lazy scan.
- **Large files.** Small files are loaded into memory and served as zero-copy slices; larger files are memory
  mapped; files above 2 GiB are scanned in windows.
- **Hardened against hostile input.** Malformed records are skipped or stop the read (your choice) and are always
  reported. BLF containers are checked against a decompression-bomb limit before allocation.
- **Bus details preserved.** CAN DLC 9–15, the LIN protected identifier, PCAPNG interface options, and unknown
  link types survive a read → write round trip with `NetworkInspector.Exporters`.
- **No native dependencies.** Fully managed; the parser stack (`NetworkInspector.Protocols`) is not required.

## Install

```bash
dotnet add package NetworkInspector.Sources
```

To parse the frames into packets, add the dissectors:

```bash
dotnet add package NetworkInspector.Protocols
```

## Quick Start

### Read Frames

```csharp
using NetworkInspector.Core;
using NetworkInspector.Sources.Pcapng;

using PcapngSource source = PcapngSource.Open("capture.pcapng"); // PCAPNG and legacy PCAP

// Every source registers its interfaces in a registry before the first frame.
FrameInterfaceRegistry registry = new();
source.Start(registry.RegisterSource(source), registry);

while (source.NextFrame() is { } frame)
{
    Console.WriteLine($"{frame.Timestamp} {frame.LinkType} {frame.Data.Length} bytes");
}
```

`Open` throws for a missing file (`FileNotFoundException`), an unreadable file (`IOException`), or an invalid
header (`PcapException`, `BlfException`). Problems inside the file are handled by the error tolerance mode.

### Read And Parse Every Frame

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols;
using NetworkInspector.Sources.Pcapng;

// The source must be started with the registry the stack was built with.
FrameInterfaceRegistry registry = new();
StackBuilder builder = new(new SettingsManager(), registry);
builder.RegisterStandardProtocols();
using Stack stack = builder.Build();

using PcapngSource source = PcapngSource.Open("capture.pcapng");
source.Start(registry.RegisterSource(source), registry);

// First-parse packet ids must be dense (0, 1, 2, ...): advance only on success.
ParseOptions options = new();
int nextId = 0;
while (source.NextFrame() is { } frame)
{
    if (!Packet.TryParse(new PacketId(nextId), stack, frame, in options, out Packet? packet, out ParseFailure failure)
        || packet is null)
    {
        Console.Error.WriteLine($"frame {frame.Id.Value}: {failure}");
        continue;
    }

    nextId++;
    Console.WriteLine($"#{packet.Id.Value} {packet.Info}");
}
```

### Read From A Stream

```csharp
using NetworkInspector.Core;
using NetworkInspector.Sources.Asc;

using FileStream fileStream = File.OpenRead("trace.asc");
using AscStreamSource source = AscStreamSource.FromStream(fileStream, "ASC stream", leaveOpen: true);

FrameInterfaceRegistry registry = new();
source.Start(registry.RegisterSource(source), registry);
while (source.NextFrame() is { } frame)
{
    // Hand the frame to the parser or an exporter.
}
```

## Choose A Source

| Need | Source |
| --- | --- |
| File with random access by frame id | `PcapngSource`, `BlfSource`, `AscSource` |
| Forward-only stream (network, pipe, archive entry) | `PcapngStreamSource`, `BlfStreamSource`, `AscStreamSource` |
| Synthetic test traffic | `RandomFrameSource` (`NetworkInspector.Sources.Random`) |
| Random access over a stream source | `CachedFrameSource` (`NetworkInspector.Sources.Cached`) |

Namespaces: `NetworkInspector.Sources.Pcapng`, `.Blf`, `.Asc`, `.Random`, `.Cached`. All file sources also offer
`FromData(...)` for captures already in memory. There is no live capture from network interfaces.

## Common Tasks

### Use Random Access

`PcapngSource`, `BlfSource`, `AscSource`, `RandomFrameSource`, and `CachedFrameSource` implement `FrameById`. It is
thread-safe and may run while another thread reads sequentially, also during a lazy scan: it returns the frame for
every id the scan has indexed and `null` for ids not reached yet. From a preloaded file it returns a zero-copy
slice; from a memory-mapped file it copies the frame into its own array.

### Configure Large BLF Files

BLF log containers are decompressed into memory. Bound the peak with `BlfSourceOptions`:

```csharp
using NetworkInspector.Sources.Blf;

BlfSourceOptions options = new()
{
    // Reject containers larger than 128 MiB before allocation (default).
    // Set to 0 only when you trust the capture source.
    MaxUncompressedContainerSize = BlfSourceOptions.DefaultMaxUncompressedContainerSize,

    // Limit simultaneous decompressions (default: ProcessorCount).
    MaxDecompressionConcurrency = 2,

    // Memory-map instead of preloading files larger than 64 MiB (default: 256 MiB on 64-bit).
    PreloadBudget = 64L * 1024 * 1024,

    // Bound the 2Q container cache (default: 32 MiB).
    CacheBudget = 16 * 1024 * 1024,
};

using BlfSource source = BlfSource.Open("bus.blf", options);
```

For streams, pass the same options to `BlfStreamSource.FromStream(stream, options: options)`. File-only members
(scan mode, preload, cache) are ignored there.

### Read Vector Files With Local Time

The BLF start date and ASC header dates have no time zone. They are read as UTC by default
(`BlfSourceOptions.TimestampTimeZone`, `AscSourceOptions.TimestampTimeZone`). Vector tools write local civil time:
set `TimeZoneInfo.Local`, or the recording zone, for files from those tools.

### Choose The Error Tolerance

`ErrorTolerance` on every options class selects the behavior for malformed records:

- `ErrorToleranceMode.Tolerant` (default): skip the record, raise `FrameSkipped`, continue.
- `ErrorToleranceMode.Strict`: return the frames in front of the first error, then stop. `FrameSkipped` is still
  raised, in file order.

### Reduce Allocations With Frame Slabs

`FrameSlabSize` (on `PcapngSourceOptions`, `BlfSourceOptions`, `AscSourceOptions`; default 0 = off) groups the
bytes of sequentially read frames into shared slabs of 1 KiB to 16 MiB. Frames above a quarter of the slab keep
their own array. Fewer, larger allocations help most when the consumer keeps the frames; the cost is retention:
one held frame keeps its whole slab alive. Preloaded files already serve zero-copy slices, so slabs only affect
memory-mapped and stream reading.

Measured on 50 000 PCAPNG frames with 64 KiB slabs (`NetworkInspector.Profiling` scenarios `read-pcapng-*`;
"mapped" with `PreloadBudget = null`):

| Path | Off | 64 KiB slabs |
| --- | --- | --- |
| mapped, frames dropped | 3.55 M/s | 3.72 M/s |
| mapped, frames held | 2.09 M/s | 2.40 M/s |
| stream, frames dropped | 6.3 M/s | 7.55 M/s |
| stream, frames held | 2.55 M/s | 3.45 M/s |

## Format Notes

- **PCAP / PCAPNG:** frames expose the captured bytes (`Frame.Length` / `Frame.Data`); the on-wire original length
  is not kept. A DLT outside the `LinkType` enum is passed through as its raw number. IDB `if_name` becomes the
  interface `UiName` (an unusable name falls back to `Interface {n}`), `if_description` its `Description`; the
  options `if_hardware`, `if_os`, `if_speed`, `if_txspeed`, `if_rxspeed`, `if_filter`, `if_fcslen` become
  interface properties (`if.*`). Legacy PCAP records are bounded by the snaplen; a record cut by the end of the
  file raises `TruncatedStream`. A section holds at most 65 536 interfaces.
- **BLF:** frames are produced for classic CAN, CAN FD, CAN XL (Type 139), LIN, FlexRay, and Ethernet Types 71,
  102, and 120–123. Ethernet status and PHY objects (103/133) and AppText are metadata; AppText channel names
  become interface names. Objects that cannot be read raise `FrameSkipped`.
- **ASC:** CAN, CAN FD, CAN XL, LIN, FlexRay, and Ethernet (`ETH`, AFDX data). `ETH STAT`, `ETH RxEr`,
  AFDX status, and AFDX bus statistics are not frames. ASC always scans the full file on open.
- **Streams:** `PcapngStreamSource` and `BlfStreamSource` wrap a stream that is neither a `MemoryStream` nor a
  `BufferedStream` in a 64 KiB `BufferedStream`. `AscStreamSource` reads 4 MiB chunks into its own buffer.
- A file larger than `Array.MaxLength` is never preloaded, even with `PreloadBudget = long.MaxValue`.

## Limits And Thread-Safety Notes

- `NextFrame` is single-consumer: call it from one thread. `FrameById` and the statistics may be used from any
  thread.
- `CachedFrameSource` keeps the inner `Frame` and does not copy payload bytes; if the inner mapping is released,
  its frames' `Data` is no longer valid. It rejects a random-access inner source unless
  `allowRandomAccessInner: true` is passed.
- Use stream sources or preload budgets to bound memory for very large captures.

## Safe Usage (STRIDE)

- **Spoofing**: File names and extensions can mislead; the readers validate magic numbers and headers.
- **Tampering**: Malformed headers and partial records are expected input; handle `FrameSkipped`.
- **Repudiation**: Keep source metadata when downstream workflows need traceability.
- **Information disclosure**: Captures can contain secrets; protect them like the traffic they record.
- **Denial of service**: Keep the BLF container limit, bound preload and cache budgets, and use cancellation.
- **Elevation of privilege**: Run readers with least-required file-system permissions.

## Learn More

- [Source guide](SOURCE_GUIDE.md): backends, lazy scan, frame slabs, error tolerance, threading, volatile fields
- [Exporters](../NetworkInspector.Exporters/README.md) · [Sessions](../NetworkInspector.Sessions/README.md) ·
  [Core](../NetworkInspector.Core/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
