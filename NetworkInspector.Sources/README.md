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
- **Huge captures, tiny index.** Random access to every frame costs well under 2 bytes of RAM per frame (0.4 B for
  64-byte PCAPNG frames, 0.7 B for CAN in BLF, 0.4 B for ASC), down from 24–38 B. A billion-frame capture stays
  browsable in about 1 GB instead of 24–38 GB. `FrameById` still returns exactly the frame the sequential read
  returned (see [Open Very Large Captures](#open-very-large-captures)).
- **Large files.** Small files are loaded into memory and served as zero-copy slices; larger files are memory
  mapped; files above 2 GiB are scanned in windows.
- **Files stay unchanged while read.** An open file source denies writers on Windows and detects changes on Linux
  and macOS. A preloaded source copies the file at `Open`; after the open-time check it keeps serving its copy and runs no monitor.
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

Read a range of consecutive ids in one call with `FramesById`: `frames[i]` receives the frame of id `first + i`, and
the range is as long as the buffer. A range lets the source read forward: `PcapngSource` and `BlfSource` walk their
index and file in order and request the bytes of the next frames early; every other source falls back to one
`FrameById` per id.

```csharp
IRandomAccessFrameSource source = PcapngSource.Open("capture.pcapng");
// … Start and read as usual …
Frame?[] frames = new Frame?[64];
int found = source.FramesById(new FrameId(1_000), frames);   // ids 1 000 … 1 063
for (int i = 0; i < frames.Length; i++)
{
    if (frames[i] is Frame frame)
    {
        Console.WriteLine(Invariant($"{1_000 + i}: {frame.Length} bytes"));
    }
}
```

Ids that are not indexed yet, or that lie past the valid id range, give `null` entries. Measured on 200 000 IMIX frames
(memory-mapped PCAPNG, one thread): ranges of 64 ids in random order 19.1 M frames/s, one `FrameById` per id in random
order 5.9 M frames/s.

### Open Very Large Captures

The file sources keep a compact frame index: for every frame only the position where its record starts, packed
into groups of 64 positions with bit-sliced deltas. Interface, length, timestamp, and bytes are re-read from the
file by the same parser the scan used, so `FrameById` returns exactly the frame the first sequential read returned,
or `null` when the bytes at that position no longer form that frame. Nothing needs to be configured.

Retained managed memory per frame after a full scan with the file memory-mapped (`PreloadBudget = null`; scenarios
`footprint-*` of `NetworkInspector.Profiling`):

| Capture | Before | Now |
| --- | --- | --- |
| PCAPNG, 1 M × 64-byte frames | 24.1 B | 0.40 B |
| PCAPNG, 500 k IMIX frames (64/594/1518 B) | 24.2 B | 1.58 B |
| BLF, 1 M CAN frames (container cache off) | 38.2 B | 0.67 B |
| ASC, 1 M CAN lines, absolute time | 32.0 B | 0.40 B |
| ASC, 1 M CAN lines, relative time | 32.0 B | 1.71 B |

The file bytes themselves are not on the managed heap when the file is memory-mapped (`PreloadBudget = null`, or
any file above the budget). The BLF container cache (`CacheBudget`) is separate from the index and bounded by its
budget. Hostile PCAPNG and BLF files never need more than the old fixed-size index (24 B per PCAPNG frame, 32 B per BLF
frame). An ASC file whose timestamps do not parse adds one 24 B clock override per such frame (the list can hold up to
twice that), so its index can exceed the old 32 B per frame.

Speed against the previous fixed-size index (same machine, median of five interleaved runs, scenarios `read-*` and
`access-*`):

| Path | PCAPNG | BLF | ASC |
| --- | --- | --- | --- |
| `NextFrame` | +2 … +23 % | +3 % | +75 % |
| `FrameById`, ids in order, 1 thread | −19 % | +3 % | +70 % |
| `FrameById`, ids in order, 4 threads | −4 % | +82 % | +54 % |
| `FrameById`, random ids | −36 % | +18 % | +183 % |

The disk-backed ASC source (files above `PreloadBudget`) reads each frame with one positional read whose size the
open-time scan stored per group of 64 frames (a power of two that covers every record of the group, 1/64 B per
frame), and a thread that reads ids in order is served from a 64 KiB read-ahead window. Against the same compact index
with the earlier per-line reads (best of ten runs each): `NextFrame` +297 % for CAN, +82 % for Ethernet, +154 % for
CAN with every 16th frame Ethernet; `FrameById` in order +526 % (CAN, 1 thread), +3 534 % (CAN, 4 threads), +140 %
(Ethernet); random `FrameById` +1 % (CAN), +97 % (Ethernet).

PCAPNG `FrameById` re-parses the block header that the old 24-byte entry cached; that check is what makes a changed
or corrupt block return `null` instead of a wrong frame. Reading ids in order (forward or backward) decodes each
group of 64 positions once per thread through a small per-thread cache (2 KiB per reading thread), so several
threads can walk one source at the same time without a lock.

Random access then got faster again without memory per frame (median of five interleaved runs against the build with
the compact index, `NetworkInspector.Profiling --ab`): reads by id no longer share a written cache line between
threads, a PCAPNG re-read requests the lines of its block at once, and BLF keeps the last container per reading
thread.

| Path | PCAPNG | BLF |
| --- | --- | --- |
| `FrameById`, ids in order, 1 thread | +10.5 % | +30 % |
| `FrameById`, ids in reverse order, 1 thread | +14.6 % | — |
| `FrameById`, random ids, 1 thread | +10.6 % | +18.7 % |
| `FrameById`, ids in order, 4 threads | +209 % | +288 % |
| `FrameById`, random ids, 4 threads | +36 % | — |

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

- `NextFrame` is single-consumer: call it from one thread. `FrameById`, `FramesById`, and the statistics may be used
  from any thread.
- `Dispose` waits for `FrameById`/`FramesById` calls running on other threads (at most one frame, or one chunk of 64
  ids of a range read) before it releases the memory-mapped file. A call that starts after `Dispose` throws
  `ObjectDisposedException`; a PCAPNG call that was already running when disposal began returns `null` for the frames
  it could no longer read. It never waits for `NextFrame`, so an error handler inside `NextFrame` may dispose the source.
- Each thread that reads by id keeps a 40-byte record of its read section, a BLF source keeps the last container a
  thread read by id (unless `CacheBudget` is 0) until that thread reads another container, and a disk-backed ASC source
  keeps a 64 KiB read-ahead window per thread (at most four sources per thread) until another source claims its slot.
- `CachedFrameSource` keeps the inner `Frame` and does not copy payload bytes; if the inner mapping is released,
  its frames' `Data` is no longer valid. It rejects a random-access inner source unless
  `allowRandomAccessInner: true` is passed.
- For very large captures open the file with `PreloadBudget = null` (memory-mapped PCAPNG and BLF, disk-backed ASC):
  the index then needs well under 2 B per frame; stream sources keep no index at all.
- **File locking.** `PcapngSource.Open`, `BlfSource.Open`, and `AscSource.Open` keep the file open with
  `FileShare.Read` until the source is disposed, for preloaded and memory-mapped files alike, because random access
  re-reads the file:
  - **Windows:** the OS enforces it. Opening the file for writing fails while a source reads it, and `Open` fails
    while another process writes it.
  - **Linux and macOS (best effort):** .NET takes an advisory lock that only cooperating writers respect. The source
    therefore compares the file length and modification time at `Open`, every 4 096 sequential frames, at the end of
    a sequential read, and in the background once per second while the source is open. A detected change sets an
    atomic flag: `FrameById` returns `null` from then on, sequential reading raises `FrameSkipped` with
    `FrameReadErrorKind.SourceFileChanged` once and stops, and a full scan at `Open` throws `IOException`.
    `FrameById` itself makes no system call; it only reads the flag, so a change is noticed within about a second
    even when no sequential read runs. A preloaded source copies the file at `Open`; after the open-time check it keeps
    serving its copy and runs no monitor. Windows runs no background check.
  - **Remaining risk on Linux and macOS:** a non-cooperating process that truncates a memory-mapped file can crash
    the reading process (SIGBUS) before any check runs. Do not modify captures while they are open, or load them
    with a preload budget, which reads the whole file into memory at `Open`.

## Safe Usage (STRIDE)

- **Spoofing**: File names and extensions can mislead; the readers validate magic numbers and headers.
- **Tampering**: Malformed headers and partial records are expected input; handle `FrameSkipped`. A capture that is
  modified while a source reads it cannot be re-read correctly: Windows denies writers, Linux and macOS report
  `FrameReadErrorKind.SourceFileChanged` (see File locking). Do not write to open captures.
- **Repudiation**: Keep source metadata when downstream workflows need traceability.
- **Information disclosure**: Captures can contain secrets; protect them like the traffic they record.
- **Denial of service**: Keep the BLF container limit, bound preload and cache budgets, and use cancellation. A hostile
  ASC file whose timestamps do not parse adds 24 B of index per frame; each reading thread keeps up to four 64 KiB ASC
  read-ahead windows until other sources claim them.
- **Elevation of privilege**: Run readers with least-required file-system permissions.

## Learn More

- [Source guide](SOURCE_GUIDE.md): backends, lazy scan, frame slabs, error tolerance, threading, volatile fields
- [Exporters](../NetworkInspector.Exporters/README.md) · [Sessions](../NetworkInspector.Sessions/README.md) ·
  [Core](../NetworkInspector.Core/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
