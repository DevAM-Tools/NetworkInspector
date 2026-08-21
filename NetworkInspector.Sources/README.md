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
| Random access by frame id | `PcapSource`, `BlfSource`, `AscSource` |
| Sequential processing with low memory pressure | `PcapStreamSource`, `BlfStreamSource`, `AscStreamSource` |
| Test data generation | `RandomFrameSource` |
| In-memory replay | `CachedFrameSource` |

## Quick Start

### Open From File

```csharp
using NetworkInspector.Sources.Pcapng;

PcapSource source = PcapSource.Open("capture.pcapng");
foreach (RawFrame rawFrame in source)
{
    // Parse with Core/Protocols pipeline
}
```

### Open From Stream

```csharp
using NetworkInspector.Sources.Asc;

AscStreamSource source = AscStreamSource.FromStream(fileStream, "ASC stream");
foreach (RawFrame rawFrame in source)
{
    // Parse with Core/Protocols pipeline
}
```

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

For stream-based reading, set `BlfStreamSource.MaxUncompressedContainerSize` before `Start()`.

### Combine With Parser Stack

Read `RawFrame` instances from sources, then convert them to `Frame` and parse into `Packet` via Core.

### Use Random Access

Use random-access source variants where frame-id lookups are required for replay, indexing, or test cases.

## Limits And Thread-Safety Notes

- Choose streaming sources for very large captures to bound memory usage.
- Validate source format and assumptions before automated ingestion.
- Use cancellation and external execution limits in long-running jobs.

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
