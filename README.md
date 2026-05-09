<!-- Copyright (c) DevAM and Network Inspector Contributors. Licensed under the MIT license. -->

# NetworkInspector

High-performance, zero-allocation network packet analysis framework for .NET 10.

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core?label=NetworkInspector.Core)](https://www.nuget.org/packages/NetworkInspector.Core)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols?label=NetworkInspector.Protocols)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Values?label=NetworkInspector.Values)](https://www.nuget.org/packages/NetworkInspector.Values)

## Packages

| Package | Description |
|---------|-------------|
| [`NetworkInspector.Core`](NetworkInspector.Core/README.md) | Core engine: protocol stack, field tree, packet index, slab allocator, reassembly. Includes the Roslyn source generator. |
| [`NetworkInspector.Protocols`](NetworkInspector.Protocols/README.md) | 30 built-in dissectors: Ethernet, IPv4/6, TCP, UDP, DNS, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket, CAN, FlexRay, SOME/IP, and more. |
| [`NetworkInspector.Values`](NetworkInspector.Values/README.md) | Strongly-typed value types: `MacAddress`, `IPv4Address`, `IPv6Address`, `Eui64`, `Uuid`, `Timestamp`. |
| [`NetworkInspector.Generators`](NetworkInspector.Generators/README.md) | Roslyn source generator — bundled with `NetworkInspector.Core`, no separate installation needed. |

---

## Quick Start

```
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

```csharp
using NetworkInspector.Core;
using NetworkInspector.Protocols;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
ProtocolRegistration.RegisterStandardProtocols(builder);
Stack stack = builder.Build();

Frame frame = Frame.Create(
    new FrameId(0), Timestamp.FromSecs(0), rawBytes,
    LinkType.Ethernet, FrameInterfaceId.Invalid,
    stack.FrameInterfaceRegistry).Value;

Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

// Iterate all fields (triggers lazy materialization)
foreach (Field field in packet)
{
    Console.WriteLine($"{field.Info.UiName}: {field.Value}");
}

stack.Dispose();
```

---

## Protocols

30 built-in dissectors across all network layers:

| Layer | Protocols |
|-------|-----------|
| Link | Ethernet, VLAN (802.1Q), Linux SLL, Linux SLL2, LLC/SNAP, Frame |
| Network | IPv4 (with fragmentation reassembly), IPv6 (with extension headers), ARP, ICMPv4, ICMPv6 |
| Transport | TCP, UDP |
| Application | DNS, DHCPv4, DHCPv6, HTTP/1.x, HTTP/2, TLS (1.0–1.3), DTLS, WebSocket, JSON, Text |
| Automotive | CAN (classic / FD / XL), FlexRay, LIN, SOME/IP, PDU Transport, Signal PDU |
| Fallback | Data |

---

## Source Generator

`NetworkInspector.Core` bundles a Roslyn source generator that processes
`[Protocol]`-annotated classes and emits all field registration boilerplate:
field IDs, dispatch tables, settings, index groups, and lifecycle hooks.

```csharp
[Protocol("eth", "Ethernet")]
public partial class EthernetProtocol : IProtocol
{
    [MacField("eth.src",  "Source")]      private FieldId _SrcFieldId;
    [MacField("eth.dst",  "Destination")] private FieldId _DstFieldId;
    [U16Field("eth.type", "EtherType")]   private FieldId _TypeFieldId;

    [ProtocolTableU16("eth.type", "EtherType dispatch")]
    public ProtocolTableId EtherTypeTableId { get; private set; }

    // Parse() implementation — registration code is generated automatically.
}
```

See [NetworkInspector.Generators/README.md](NetworkInspector.Generators/README.md) for the full
attribute reference and generated member documentation.

---

## Key Features

- **Zero allocations** in the parsing hot path via thread-local slab allocators and `ZeroAlloc` string formatting.
- **AOT and trim compatible** — all packages target `net10.0` with `IsAotCompatible=true` and `IsTrimmable=true`.
- **Cross-platform** — Windows, Linux, macOS — x64 and ARM64.
- **Flat field tree** — fields stored as a contiguous array of structs; navigation via `ushort` indices, no heap pointers.
- **Cross-packet index** — roaring bitmap index for constant-time presence queries across packet captures.
- **Typed dispatch tables** — protocol routing via `U8`, `U16`, `U32`, `U64`, `String`, and heuristic tables built at stack-construction time.
- **IP reassembly** — built-in IPv4 fragment reassembly with FIFO eviction.
- **Roslyn source generator** — eliminates all protocol registration boilerplate.

---

## Requirements

- .NET 10 SDK

---

## License

[MIT License](LICENSE) — © DevAM and Network Inspector Contributors
