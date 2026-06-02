<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector

High-performance packet analysis for .NET 10 with a modular package ecosystem for parsing, capture I/O, export pipelines, and frame construction.

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core?label=NetworkInspector.Core)](https://www.nuget.org/packages/NetworkInspector.Core)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols?label=NetworkInspector.Protocols)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Values?label=NetworkInspector.Values)](https://www.nuget.org/packages/NetworkInspector.Values)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.FrameBuilder?label=NetworkInspector.FrameBuilder)](https://www.nuget.org/packages/NetworkInspector.FrameBuilder)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Sources?label=NetworkInspector.Sources)](https://www.nuget.org/packages/NetworkInspector.Sources)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Exporters?label=NetworkInspector.Exporters)](https://www.nuget.org/packages/NetworkInspector.Exporters)

## What This Is

NetworkInspector is a .NET toolkit for teams that need to process network captures end-to-end:

- ingest frame data from common capture formats,
- parse protocol stacks into structured packet fields,
- export results into operational or analytics-friendly outputs.

The project is intentionally modular. You can adopt only the packages needed for your scenario.

## Why It Stands Out

- **Composable package architecture**: parse-only, source-only, export-only, or full pipeline.
- **Broad built-in protocol coverage**: enterprise and automotive traffic in one stack.
- **Production workflow support**: conversion, streaming, cancellation, and tolerant modes.
- **Practical developer ergonomics**: quick stack setup, typed values, and CLI tooling.

## Install

Start with parser essentials:

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

Add workflow packages as required:

```bash
dotnet add package NetworkInspector.Sources
dotnet add package NetworkInspector.Exporters
dotnet add package NetworkInspector.Values
dotnet add package NetworkInspector.FrameBuilder
```

## Build Baseline

NetworkInspector is validated for .NET SDK 10.0.100 and newer.

## Local Development

### Prerequisites

The full test suite includes protocol cross-validation tests that require
[tshark 4.6.x](https://www.wireshark.org/download/) (part of the Wireshark distribution) to be
available on `PATH`.

| Platform | Install |
|---|---|
| Windows | [Wireshark installer](https://www.wireshark.org/download/) (includes tshark) or `choco install wireshark` |
| Ubuntu / Debian | `sudo add-apt-repository ppa:wireshark-dev/wireshark && sudo apt-get install tshark` |
| macOS | `brew install wireshark` |

### Running Tests

```bash
dotnet test NetworkInspector.slnx
```

### Skipping Cross-Validation Locally

If Wireshark is not installed, set `NETWORKINSPECTOR_ALLOW_MISSING_TSHARK=1` to skip
tshark-dependent tests. Tests that opt into this escape hatch via
`TsharkAvailability.ShouldSkip()` will be skipped; the remaining tests run normally.

Do not set this variable in CI or release runs — tshark must be present so that
cross-validation evidence is never silently discarded.

## Transitive Dependency And Generator Behavior

- `NetworkInspector.Core` bundles `NetworkInspector.Generators` in the package analyzer assets.
- NetworkInspector library packages keep `ZeroAlloc` as a transitive dependency, including analyzer/source-generator assets.
- Consumers that reference NetworkInspector packages do not need extra package references to activate generator support.

## Quick Start

### Parse One Frame

```csharp
using NetworkInspector.Core;
using NetworkInspector.Protocols;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
ProtocolRegistration.RegisterStandardProtocols(builder);
Stack stack = builder.Build();

Frame frame = Frame.Create(
    new FrameId(0),
    Timestamp.FromSecs(0),
    rawBytes,
    LinkType.Ethernet,
    FrameInterfaceId.Invalid,
    stack.FrameInterfaceRegistry).Value;

Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
foreach (Field field in packet)
{
    Console.WriteLine($"{field.Info.UiName}: {field.Value}");
}

stack.Dispose();
```

## Recommended Adoption Paths

### Path 1: Parse And Inspect Packets In Code

Use Core + Protocols to build a parser stack and inspect packet fields programmatically.

Best for:

- custom observability tools,
- packet validation,
- protocol-aware data extraction.

### Path 2: Convert Capture Files

Use `NetworkInspector.CLI` for frame-level conversion without full packet export:

```bash
ni convert input.blf --output output.pcapng
ni convert input.pcapng --output split/ --split-size 100
```

Best for:

- capture normalization,
- archive conversion,
- frame-level operational workflows.

### Path 3: Export Parsed Data

Use CLI export or packet exporters for analysis outputs:

```bash
ni export input.pcapng --format json --output packets.json
ni export input.pcapng --format pbf:format=columnar,compressed --output packets.pbf
```

Best for:

- analytics pipelines,
- report generation,
- downstream BI/data tooling.

## Package Map

| Package | What You Use It For | Guide |
|---------|---------------------|-------|
| NetworkInspector.Core | Build parser stack and parse frames to packets | [NetworkInspector.Core/README.md](NetworkInspector.Core/README.md) |
| NetworkInspector.Protocols | Register built-in dissectors | [NetworkInspector.Protocols/README.md](NetworkInspector.Protocols/README.md) |
| NetworkInspector.Sources | Read captures from PCAP/PCAPNG/BLF/ASC | [NetworkInspector.Sources/README.md](NetworkInspector.Sources/README.md) |
| NetworkInspector.Exporters | Write PCAPNG/BLF/ASC/JSON/PBF/CSV/Text | [NetworkInspector.Exporters/README.md](NetworkInspector.Exporters/README.md) |
| NetworkInspector.Values | Use typed addresses and timestamps | [NetworkInspector.Values/README.md](NetworkInspector.Values/README.md) |
| NetworkInspector.FrameBuilder | Construct protocol stacks into wire frames | [NetworkInspector.FrameBuilder/README.md](NetworkInspector.FrameBuilder/README.md) |
| NetworkInspector.CLI | Convert/export from the command line (`ni`) | [NetworkInspector.CLI/README.md](NetworkInspector.CLI/README.md) |
| NetworkInspector.Generators | Source generation bundled with Core | [NetworkInspector.Generators/README.md](NetworkInspector.Generators/README.md) |

## Protocol Coverage Snapshot

Built-in dissectors include link, network, transport, application, and automotive layers, including:

- Ethernet, VLAN, Linux SLL/SLL2,
- IPv4, IPv6, ARP, ICMPv4, ICMPv6,
- TCP, UDP,
- DNS, DHCPv4/v6, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket,
- CAN (classic/FD/XL), FlexRay, LIN, SOME/IP.

For full and current protocol list, see [NetworkInspector.Protocols/README.md](NetworkInspector.Protocols/README.md).

## Limits And Thread-Safety Notes

- Treat parser stack construction and exporter/listener workflows as single-threaded unless a package README states otherwise.
- Dispose stacks and exporter instances promptly to release resources predictably.
- Prefer streaming pipelines and explicit cancellation for large capture workloads.

## Safe Usage (STRIDE)

- **Spoofing**: Treat captures as untrusted input and validate source provenance where possible.
- **Tampering**: Expect malformed records from third-party data and use tolerant/error-aware processing.
- **Repudiation**: Retain source metadata and command settings when reproducibility is required.
- **Information disclosure**: Assume exports can contain sensitive payload data and control access accordingly.
- **Denial of service**: Apply split/target-count/cancellation controls on large jobs.
- **Elevation of privilege**: Run processing with least-privilege filesystem and process rights.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

### NuGet Package Pages

| Package | NuGet |
|---------|-------|
| NetworkInspector.Core | <https://www.nuget.org/packages/NetworkInspector.Core> |
| NetworkInspector.Protocols | <https://www.nuget.org/packages/NetworkInspector.Protocols> |
| NetworkInspector.Values | <https://www.nuget.org/packages/NetworkInspector.Values> |
| NetworkInspector.FrameBuilder | <https://www.nuget.org/packages/NetworkInspector.FrameBuilder> |
| NetworkInspector.Sources | <https://www.nuget.org/packages/NetworkInspector.Sources> |
| NetworkInspector.Exporters | <https://www.nuget.org/packages/NetworkInspector.Exporters> |
| NetworkInspector.CLI | <https://www.nuget.org/packages/NetworkInspector.CLI> |

## License

[MIT License](LICENSE)
