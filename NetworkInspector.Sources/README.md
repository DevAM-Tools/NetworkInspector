<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.Sources

File format readers for the NetworkInspector packet analysis framework.
Supports reading captured network data from standard file formats
and generating synthetic frames for testing.

## Supported Formats

| Source | Format | Description |
|--------|--------|-------------|
| `PcapSource` | PCAP / PCAP-NG | Standard packet capture files (libpcap, Wireshark) |
| `PcapStreamSource` | PCAP / PCAP-NG | Streaming reader for large files |
| `BlfSource` | BLF | Binary Logging Format (Vector automotive tools) |
| `BlfStreamSource` | BLF | Streaming BLF reader |
| `AscSource` | ASC | Vector ASCII trace files with random access |
| `AscStreamSource` | ASC | Streaming ASC reader |
| `RandomFrameSource` | — | Synthetic frame generator for testing and benchmarking |
| `CachedFrameSource` | — | In-memory cached frame source |

## Features

- **Memory-mapped I/O** — efficient access to large capture files via `MmapPool`
- **Incremental scanning** — parse file structure without loading all frames into memory
- **Frame indexing** — fast random access to any frame by index
- **Streaming** — process frames sequentially without full file scan
- **Multiple link types** — Ethernet, Linux SLL/SLL2, SocketCAN, FlexRay, LIN, and more
- **Dual-backend ASC reading** — small ASC files are loaded into memory; large files use a
  buffered disk reader so the allocation stays proportional to the configured `PreloadBudget`

## Memory management — ASC sources

`AscSource` (random-access) chooses between two internal backends at open time:

| Condition | Backend | Behaviour |
|-----------|---------|-----------|
| `fileSize ≤ PreloadBudget` (default 256 MiB) | **In-memory** | All lines are kept in a `string[]`; frames are served entirely from RAM. |
| `fileSize > PreloadBudget` | **Disk** | Only a frame-index is kept in memory. Individual lines are read on-demand via a new `FileStream` seek per `FrameById` call. |

The initial index scan always uses a 4 MiB read buffer (`AscSourceOptions.DiskReadBufferSize`)
regardless of which backend is used. This avoids small-read stalls on spinning disks and
reduces system-call overhead for network shares.

```csharp
// Default — 256 MiB budget, tolerant error mode
AscSource source = AscSource.Open("trace.asc");

// Custom budget — forces disk backend for files larger than 64 MiB
AscSourceOptions opts = new() { PreloadBudget = 64 * 1024 * 1024 };
AscSource source = AscSource.Open("large_trace.asc", opts);
```

`AscStreamSource` (forward-only) always uses a single 4 MiB buffered `StreamReader`.
Interface registration is deferred per `(busType, channel)` pair as frames arrive,
keeping `Start()` allocation-free.

## Usage

### PCAP-NG File

```csharp
using NetworkInspector.Sources.Pcapng;

// Open a PCAP-NG file
PcapSource source = PcapSource.Open("capture.pcapng");

// Read frames
foreach (RawFrame rawFrame in source)
{
    Frame frame = Frame.Create(
        new FrameId(rawFrame.Index),
        rawFrame.Timestamp,
        rawFrame.Data,
        rawFrame.LinkType,
        rawFrame.InterfaceId,
        stack.FrameInterfaceRegistry).Value;

    Packet packet = Packet.ParseFrame(new PacketId(rawFrame.Index), stack, frame);
}
```

### Vector ASC File

```csharp
using NetworkInspector.Sources.Asc;

// Random-access: open an ASC file and access any frame by ID
AscSource source = AscSource.Open("trace.asc");

// Streaming: process frames sequentially from a stream
AscStreamSource streamSource = AscStreamSource.FromStream(fileStream, "CAN trace");
```

Supported ASC bus types: CAN classic, CAN FD, LIN, FlexRay, Ethernet/AFDX.

### PCAP-NG lazy scan and random access

When a PCAP-NG file is opened from disk with lazy scanning (`PcapSourceOptions.ScanMode.Lazy`),
sequential reads obtain payloads through `DataBackend.ReadFrameData`, matching the random-access
path. In-memory captures avoid an extra copy per frame; memory-mapped captures copy through
`MmapPool` (`ReadInto` uses an uninitialized buffer and clears any unread tail, preserving the
same observable bytes as `new byte[length]`).

`FrameById` on `PcapSource` and `BlfSource` is safe to call concurrently once the index is fully
built (see `SOURCE_GUIDE.md` for lifecycle details). Stress coverage lives in
`NetworkInspector.Sources.Tests.Stress.FrameByIdStressTests`.

## License

[MIT License](../LICENSE) — © DevAM
