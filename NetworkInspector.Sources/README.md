<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Sources

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Sources)](https://www.nuget.org/packages/NetworkInspector.Sources)

Capture reader package for NetworkInspector with random-access and streaming source options.

## What This Is

`NetworkInspector.Sources` opens and reads capture files so you can parse them through Core/Protocols pipelines.

Supported families include:

- PCAP and PCAPNG,
- BLF,
- ASC,
- synthetic and cached sources for testing and benchmarking.

## Why It Stands Out

- Covers both random-access and stream-first workflows.
- Supports large-file handling with memory-budget-oriented options.
- Integrates directly into parser and exporter pipelines.

## Install

```bash
dotnet add package NetworkInspector.Sources
dotnet add package NetworkInspector.Core
```

## Choose The Right Source Type

| Need | Recommended Source |
|------|--------------------|
| Random access by frame id | `PcapngSource`, `BlfSource`, `AscSource` |
| Sequential processing with low memory pressure | `PcapngStreamSource`, `BlfStreamSource`, `AscStreamSource` |
| Test data generation | `RandomFrameSource` |
| In-memory replay / session stream cache | `CachedFrameSource` |
| Session stream sources | Session auto-wraps non-random-access sources in `CachedFrameSource` |

## Cached frames

`CachedFrameSource` wraps a sequential `IFrameSource` and implements `IRandomAccessFrameSource`. Default construction rejects an inner that is already random-access. Pass `allowRandomAccessInner: true` when a session (or caller) wants a copy of a file source.

Each published slot holds the inner `Frame`. Payload memory is whatever the inner source published (per-frame arrays, mmap slices, …). The wrapper does not copy bytes. If the inner mapping is released, aliased `Frame.Data` is no longer valid.

## Quick Start

### Open From File

```csharp
using NetworkInspector.Core;
using NetworkInspector.Sources.Pcapng;

using PcapngSource source = PcapngSource.Open("capture.pcapng");

// Every source registers its interfaces in a registry before the first frame.
FrameInterfaceRegistry registry = new();
source.Start(registry.RegisterSource(source), registry);

while (source.NextFrame() is { } frame)
{
    Console.WriteLine($"{frame.Timestamp.AsNanos} {frame.LinkType} {frame.Data.Length} bytes");
}
```

### Open From Stream

```csharp
using NetworkInspector.Core;
using NetworkInspector.Sources.Asc;

using FileStream fileStream = File.OpenRead("trace.asc");
using AscStreamSource source = AscStreamSource.FromStream(fileStream, "ASC stream", leaveOpen: true);

FrameInterfaceRegistry registry = new();
source.Start(registry.RegisterSource(source), registry);
while (source.NextFrame() is { } frame)
{
    // Parse with the Core/Protocols pipeline.
}
```

All three stream sources take the file source's options class (`FromStream(stream, uiName, leaveOpen, options)`);
members that only make sense for files (scan mode, preload, cache) are ignored. A stream that is neither a
`MemoryStream` nor a `BufferedStream` is wrapped in a 64 KiB `BufferedStream`, so network and pipe streams are not
read in 1- to 8-byte calls.

## Common Tasks

### Handle Large ASC Files

Use `AscSourceOptions.PreloadBudget` to control in-memory vs disk-backed behavior.

### Handle Large BLF Files (FlexRay, CAN, Ethernet)

BLF log containers are decompressed into memory. Configure `BlfSourceOptions` to bound peak usage:

```csharp
BlfSourceOptions options = new()
{
    // Reject containers larger than 128 MiB before allocation (default).
    // Set to 0 only when you trust the capture source.
    MaxUncompressedContainerSize = BlfSourceOptions.DefaultMaxUncompressedContainerSize,

    // Limit simultaneous decompressions (default: ProcessorCount).
    MaxDecompressionConcurrency = 2,

    // Use mmap instead of full preload for files larger than the budget.
    PreloadBudget = 64L * 1024 * 1024,

    // Bound the 2Q container cache (default: 32 MiB).
    CacheBudget = 16 * 1024 * 1024,
};
```

For stream-based reading, pass the same options to `BlfStreamSource.FromStream(stream, options: options)`;
`BlfStreamSource.MaxUncompressedContainerSize` is read-only and reports the configured limit.

A file larger than `Array.MaxLength` is never preloaded, even with `PreloadBudget = long.MaxValue`; it uses the
mapped backend instead. The rule is the same for PCAPNG, BLF, and ASC.

BLF sources emit frames for the object types in the frame-producing set: classic CAN,
CAN FD, CAN XL Type 139, LIN, FlexRay, and Ethernet Types 71, 102, and 120–123 (frame EX,
forwarded, error EX, error forwarded). Ethernet status/PHY objects (103/133) and AppText are
metadata, not reconstructed frames; AppText channel names become interface names.
`object_length` is unpadded; readers then scan one byte at a time for the next `LOBJ`.
A leftover inner object at a container tail is prepended to the next decompressed blob.
Objects that cannot be read (cut by the end of the file, a header or payload too short) raise `FrameSkipped`.

The BLF start date (a SYSTEMTIME without a zone) is read as UTC by default
(`BlfSourceOptions.TimestampTimeZone`, the same default as ASC and the BLF exporter). Vector tools and other
readers treat it as local civil time: set `TimeZoneInfo.Local` (or the recording zone) for files from those tools.

### Combine With Parser Stack

Sources produce `Frame` values (bytes, timestamp, link type, interface). Hand them to the Core/Protocols stack to
parse them into `Packet` instances; [`SESSION_GUIDE.md`](../NetworkInspector.Sessions/SESSION_GUIDE.md) shows the
pipeline a session builds.

### Use Random Access

`PcapngSource`, `BlfSource`, and `AscSource` implement `FrameById`. It is thread-safe and may run while another
thread reads sequentially, also during a lazy scan: it returns the frame for every id the scan has indexed and `null`
for ids it has not reached yet. Each call allocates the frame's own array; it never uses the sequential slabs below.

### Reduce Allocations With Frame Slabs

`FrameSlabSize` (on `PcapngSourceOptions`, `BlfSourceOptions`, `AscSourceOptions`; default 0 = off) groups the
bytes of sequentially read frames into shared slabs of 1 KiB to 16 MiB. Frames above a quarter of the slab keep
their own array. Fewer, larger allocations help most when the consumer keeps the frames; the cost is retention: one
held frame keeps its whole slab alive.

Measured on 50 000 PCAPNG frames (64 KiB slabs, `NetworkInspector.Profiling` scenarios `read-pcapng-*`):

| Path | Off | 64 KiB slabs |
|------|-----|--------------|
| mapped, frames dropped | 3.55 M/s | 3.72 M/s |
| mapped, frames held | 2.09 M/s | 2.40 M/s |
| stream, frames dropped | 6.3 M/s | 7.55 M/s |
| stream, frames held | 2.55 M/s | 3.45 M/s |

## Limits And Thread-Safety Notes

- Choose streaming sources for very large captures to bound memory usage.
- Validate source format and assumptions before automated ingestion.
- Use cancellation and external execution limits in long-running jobs.
- PCAPNG and PCAP readers expose captured packet bytes only (`Frame.Length` / `Frame.Data`). On-wire original length in the file is not kept.
- A DLT value outside the `LinkType` enum is passed through: the frame carries the raw number as its `LinkType` and its bytes unchanged, so exporters can write it back.
- IDB `if_name` becomes the registry interface `UiName` (an unusable name falls back to `Interface {n}`), `if_description` its `Description`. The IDB options `if_hardware`, `if_os`, `if_speed`, `if_txspeed`, `if_rxspeed`, `if_filter`, `if_fcslen` become interface properties (`if.*`) that the PCAPNG exporter writes back.
- Legacy PCAP records are bounded by the declared snaplen (`MalformedHeader` above it); a record cut by the end of the file raises `TruncatedStream` instead of a shortened frame. A section holds at most 65 536 interfaces; later IDBs are ignored and their packets skipped.
- In `ErrorToleranceMode.Strict` every source returns the frames in front of the first error, then stops — also after a full scan that found the error while opening.

## Safe Usage (STRIDE)

- **Spoofing**: Assume file names/extensions may be misleading; validate expected source format before processing.
- **Tampering**: Handle malformed headers and partial records as expected external-input failure modes.
- **Repudiation**: Preserve source metadata if downstream workflows require traceability.
- **Information disclosure**: Treat raw captures as sensitive data and apply access controls.
- **Denial of service**: Use streaming modes, budgets, and cancellation to guard against oversized inputs.
- **Elevation of privilege**: Run source readers with least-required filesystem permissions.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Sources)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Sources)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)
- [SOURCE_GUIDE.md](SOURCE_GUIDE.md)

## License

[MIT License](../LICENSE)
