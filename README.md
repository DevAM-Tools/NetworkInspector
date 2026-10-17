<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core?logo=nuget&label=NuGet)](https://www.nuget.org/packages?q=NetworkInspector)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#build-from-source)
[![License: MIT](https://img.shields.io/github/license/DevAM-Tools/NetworkInspector)](LICENSE)

Packet analysis for .NET 10: read network and automotive captures, dissect them into typed fields, filter them
with Wireshark-style expressions, and export them to formats for tools, spreadsheets, and SQL. Use it as a
library, or from the command line with `ni`.

```bash
dotnet tool install -g NetworkInspector.CLI
ni export capture.pcapng -o packets.duckdb      # every packet, every field, queryable with SQL
```

## Why NetworkInspector

- **Enterprise and automotive traffic in one stack.** Ethernet, IP, TCP/UDP, DNS, DHCP, HTTP/1.x, HTTP/2, TLS,
  WebSocket *and* CAN, CAN FD, CAN XL, LIN, FlexRay, SOME/IP, and JSON-configured signal decoding.
- **Capture formats both worlds use.** PCAP, PCAPNG, Vector BLF, and Vector ASC in and out, with round trips that
  keep interface metadata and bus details (CAN DLC 9–15, LIN protected id).
- **Fast.** Millions of frames per second from memory-mapped files and streams, allocation-aware parsing with
  packet recycling, skip-parse modes, and Roaring-bitmap presence indexes for filtering huge captures.
- **Analytics built in.** Export to JSON, CSV, Text, PBF, Parquet, or a single DuckDB file with a field catalog
  and native types; record selected fields into columnar RAM caches while parsing.
- **Wireshark-style filters, compiled.** `tcp.port in {80, 443} && !tls`, checked against the stack at compile
  time with "did you mean" hints, plus subtree scopes and edge detection across packets.
- **Live sessions for applications.** Multi-source, multi-listener sessions with per-listener filters, background
  value caches, and hot protocol-stack swaps.
- **Verified against Wireshark.** More than 5,000 tests; dissectors, readers, writers, and the frame builder are
  cross-validated against `tshark` 4.6.
- **Modular.** Take only what you need: values, parser, readers, writers, filters, sessions, or the CLI.

## Quick Start

### Command Line

```bash
dotnet tool install -g NetworkInspector.CLI

ni convert capture.blf -o capture.pcapng                       # Vector BLF -> Wireshark
ni export capture.pcapng -o packets.json                       # parsed packets as JSON
ni export capture.pcapng -o dns.json --filter "udp.port == 53" # only DNS
ni export capture.pcapng -o out -f parquet                     # Parquet dataset for pandas, Polars, Spark
```

More: [CLI README](NetworkInspector.CLI/README.md).

### Library

```bash
dotnet add package NetworkInspector.Sources
dotnet add package NetworkInspector.Protocols
dotnet add package NetworkInspector.Filter
```

Read a capture, parse every frame, and print the DNS packets:

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Settings;
using NetworkInspector.Filter;
using NetworkInspector.Filter.Errors;
using NetworkInspector.Protocols;
using NetworkInspector.Sources.Pcapng;

// One registry for the stack and the source.
FrameInterfaceRegistry registry = new();
StackBuilder builder = new(new SettingsManager(), registry);
builder.RegisterStandardProtocols();
using Stack stack = builder.Build();

if (!Filter.TryCompile("udp.port == 53", stack, out Filter? dns, out FilterError? error))
{
    throw new InvalidOperationException(error.ToString());
}

using PcapngSource source = PcapngSource.Open("capture.pcapng"); // PCAPNG and legacy PCAP
source.Start(registry.RegisterSource(source), registry);

ParseOptions options = new();
int nextId = 0; // first-parse ids are dense: 0, 1, 2, ...
while (source.NextFrame() is { } frame)
{
    if (!Packet.TryParse(new PacketId(nextId), stack, frame, in options, out Packet? packet, out ParseFailure failure)
        || packet is null)
    {
        Console.Error.WriteLine($"frame {frame.Id.Value}: {failure}");
        continue;
    }

    nextId++;
    if (dns.TryIsMatch(packet, out bool matched, out _) && matched)
    {
        Console.WriteLine($"#{packet.Id.Value} {frame.Timestamp} {packet.Info}");
    }
}
```

Next steps: walk the field tree ([Core](NetworkInspector.Core/README.md)), write the packets to a file
([Exporters](NetworkInspector.Exporters/README.md)), or run the same pipeline on background threads with
listeners ([Sessions](NetworkInspector.Sessions/README.md)).

## Packages

| Package | Version | Downloads | Purpose |
| --- | --- | --- | --- |
| [NetworkInspector.Core](NetworkInspector.Core/README.md) | [![v][core-v]][core] | [![d][core-d]][core] | Parser engine, field tree, index, value cache |
| [NetworkInspector.Protocols](NetworkInspector.Protocols/README.md) | [![v][prot-v]][prot] | [![d][prot-d]][prot] | Built-in dissectors |
| [NetworkInspector.Sources](NetworkInspector.Sources/README.md) | [![v][src-v]][src] | [![d][src-d]][src] | Read PCAP, PCAPNG, BLF, ASC |
| [NetworkInspector.Exporters](NetworkInspector.Exporters/README.md) | [![v][exp-v]][exp] | [![d][exp-d]][exp] | Write capture and analysis formats |
| [NetworkInspector.Exporters.DuckDb](NetworkInspector.Exporters.DuckDb/README.md) | [![v][duck-v]][duck] | [![d][duck-d]][duck] | Write a DuckDB database |
| [NetworkInspector.Filter](NetworkInspector.Filter/README.md) | [![v][filt-v]][filt] | [![d][filt-d]][filt] | Compiled display filters |
| [NetworkInspector.Sessions](NetworkInspector.Sessions/README.md) | [![v][sess-v]][sess] | [![d][sess-d]][sess] | Multi-threaded capture sessions |
| [NetworkInspector.FrameBuilder](NetworkInspector.FrameBuilder/README.md) | [![v][fb-v]][fb] | [![d][fb-d]][fb] | Build wire frames from typed layers |
| [NetworkInspector.Values](NetworkInspector.Values/README.md) | [![v][val-v]][val] | [![d][val-d]][val] | MAC, IP, EUI-64, UUID, timestamp values |
| [NetworkInspector.CLI](NetworkInspector.CLI/README.md) | [![v][cli-v]][cli] | [![d][cli-d]][cli] | `ni` command-line tool |

[core]: https://www.nuget.org/packages/NetworkInspector.Core
[core-v]: https://img.shields.io/nuget/v/NetworkInspector.Core?label=
[core-d]: https://img.shields.io/nuget/dt/NetworkInspector.Core?label=
[prot]: https://www.nuget.org/packages/NetworkInspector.Protocols
[prot-v]: https://img.shields.io/nuget/v/NetworkInspector.Protocols?label=
[prot-d]: https://img.shields.io/nuget/dt/NetworkInspector.Protocols?label=
[src]: https://www.nuget.org/packages/NetworkInspector.Sources
[src-v]: https://img.shields.io/nuget/v/NetworkInspector.Sources?label=
[src-d]: https://img.shields.io/nuget/dt/NetworkInspector.Sources?label=
[exp]: https://www.nuget.org/packages/NetworkInspector.Exporters
[exp-v]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?label=
[exp-d]: https://img.shields.io/nuget/dt/NetworkInspector.Exporters?label=
[duck]: https://www.nuget.org/packages/NetworkInspector.Exporters.DuckDb
[duck-v]: https://img.shields.io/nuget/v/NetworkInspector.Exporters.DuckDb?label=
[duck-d]: https://img.shields.io/nuget/dt/NetworkInspector.Exporters.DuckDb?label=
[filt]: https://www.nuget.org/packages/NetworkInspector.Filter
[filt-v]: https://img.shields.io/nuget/v/NetworkInspector.Filter?label=
[filt-d]: https://img.shields.io/nuget/dt/NetworkInspector.Filter?label=
[sess]: https://www.nuget.org/packages/NetworkInspector.Sessions
[sess-v]: https://img.shields.io/nuget/v/NetworkInspector.Sessions?label=
[sess-d]: https://img.shields.io/nuget/dt/NetworkInspector.Sessions?label=
[fb]: https://www.nuget.org/packages/NetworkInspector.FrameBuilder
[fb-v]: https://img.shields.io/nuget/v/NetworkInspector.FrameBuilder?label=
[fb-d]: https://img.shields.io/nuget/dt/NetworkInspector.FrameBuilder?label=
[val]: https://www.nuget.org/packages/NetworkInspector.Values
[val-v]: https://img.shields.io/nuget/v/NetworkInspector.Values?label=
[val-d]: https://img.shields.io/nuget/dt/NetworkInspector.Values?label=
[cli]: https://www.nuget.org/packages/NetworkInspector.CLI
[cli-v]: https://img.shields.io/nuget/v/NetworkInspector.CLI?label=
[cli-d]: https://img.shields.io/nuget/dt/NetworkInspector.CLI?label=

The source generator ([NetworkInspector.Generators](NetworkInspector.Generators/README.md)) ships inside
`NetworkInspector.Core`. Packages pull in their own dependencies: Sources, Exporters, and Filter bring Core;
Core brings Values.

### Which Packages Do I Need?

| Goal | Packages |
| --- | --- |
| Convert or export captures without code | `NetworkInspector.CLI` (tool) |
| Parse frames you already have | Core + Protocols |
| Read capture files | Sources (+ Protocols to parse) |
| Write captures or analysis files | Exporters (+ Exporters.DuckDb for DuckDB) |
| Filter packets | Filter (+ Protocols) |
| Build an analysis application with live views | Sessions + Protocols |
| Generate test traffic | FrameBuilder (+ Exporters to save it) |
| Typed addresses and timestamps only | Values |
| Write your own dissector | Core + Protocols ([Protocol guide](NetworkInspector.Protocols/PROTOCOL_GUIDE.md)) |

## Capabilities

### Protocols

| Layer | Protocols |
| --- | --- |
| Link | Ethernet II, VLAN (802.1Q, 802.1ad), LLC/SNAP, Linux SLL, Linux SLL2 |
| Network | IPv4, IPv6 (with fragment reassembly), ARP, ICMPv4, ICMPv6 |
| Transport | TCP (stream reassembly and analysis), UDP |
| Application | DNS, DHCPv4, DHCPv6, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket, JSON, Text |
| Automotive | CAN (classic, FD, XL), LIN, FlexRay, SOME/IP (SD, TP), PDU Transport, signal messages |

Details and configuration: [Protocols README](NetworkInspector.Protocols/README.md).

### Formats

| Format | Read | Write | Kind |
| --- | :---: | :---: | --- |
| PCAPNG | ✅ | ✅ | frames |
| PCAP (legacy) | ✅ | — | frames |
| Vector BLF | ✅ | ✅ | frames |
| Vector ASC | ✅ | ✅ | frames |
| JSON, CSV, Text | — | ✅ | packets |
| PBF (NetworkInspector binary) | — | ✅ | packets |
| Apache Parquet | — | ✅ | packets |
| DuckDB | — | ✅ | packets |

### Performance

Measured with the `NetworkInspector.Profiling` scenarios on 50 000 PCAPNG frames (64 KiB frame slabs):

| Read path | Frames per second |
| --- | --- |
| Stream source, frames dropped | 7.55 M |
| Memory-mapped file, frames dropped | 3.72 M |
| Stream source, frames held | 3.45 M |
| Memory-mapped file, frames held | 2.40 M |

The parser recycles packets, decodes lazily, and can skip the field tree entirely when only values, indexes, or
filter verdicts are needed. Run `NetworkInspector.Profiling` to measure parse, filter, export, and session
throughput on your hardware.

## Quality

- More than 5,000 tests (TUnit on Microsoft.Testing.Platform), including round trips through every capture format.
- Dissectors, capture readers and writers, and frame-builder output are cross-validated against `tshark` 4.6.
- Warnings are errors; .NET analyzers and a style checker run on every build.
- Untrusted-input hardening: bounded lengths, counts, and nesting; decompression-bomb limits; process-seeded
  connection hashing; linear-time regular expressions where possible.

## Limits And Thread-Safety Notes

- Build a protocol stack once and reuse it; construction is single-threaded. First parses run in packet-id order;
  re-parses may run concurrently.
- Exporters and filters are single-threaded per instance. `Session` is thread-safe. Package READMEs state the rules
  for each type.
- Dispose stacks, sources, exporters, and sessions to release memory maps and files promptly.
- No live capture from network interfaces; inputs are capture files, streams, and in-memory frames.
- NativeAOT and trimming are not supported.

## Safe Usage (STRIDE)

- **Spoofing**: Treat captures as untrusted input and check their provenance where it matters.
- **Tampering**: Expect malformed records; use tolerant processing and inspect the reported errors.
- **Repudiation**: Keep source metadata and command settings when results must be reproducible.
- **Information disclosure**: Captures and exports can contain credentials and personal data; control access.
- **Denial of service**: Use split, count, size, and cancellation limits on large or unknown inputs.
- **Elevation of privilege**: Run processing with least-privilege file-system and process rights.

## Build From Source

Requires the .NET SDK 10.0.100 or newer (see [`global.json`](global.json)). Windows, Linux, and macOS on x64 and
ARM64.

```bash
dotnet build NetworkInspector.slnx -c Release
dotnet test NetworkInspector.slnx -c Release
```

The cross-validation tests need [tshark 4.6.x](https://www.wireshark.org/download/) (part of Wireshark) on `PATH`:

| Platform | Install |
| --- | --- |
| Windows | [Wireshark installer](https://www.wireshark.org/download/) (includes tshark) or `choco install wireshark` |
| Ubuntu / Debian | `sudo add-apt-repository ppa:wireshark-dev/wireshark && sudo apt-get install tshark` |
| macOS | `brew install wireshark` |

Without Wireshark, set `NETWORKINSPECTOR_ALLOW_MISSING_TSHARK=1` to skip the tests that opt into
`TsharkAvailability.ShouldSkip()`; all other tests still run. Do not set it in CI or release runs, so
cross-validation evidence is never discarded silently.

`NetworkInspector.Core` bundles `NetworkInspector.Generators` in its analyzer assets, and the library packages
carry `ZeroAlloc` (including its generator) transitively, so consumers need no extra references.

## Links

- [Changelog](CHANGELOG.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues) ·
  [Packages on NuGet](https://www.nuget.org/packages?q=NetworkInspector)

## License

[MIT License](LICENSE)
