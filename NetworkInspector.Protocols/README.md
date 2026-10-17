<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Protocols

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Protocols?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Protocols)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Ready-to-use dissectors for `NetworkInspector.Core`: enterprise networking (Ethernet to TLS and HTTP/2) and
automotive buses (CAN, CAN FD, CAN XL, LIN, FlexRay, SOME/IP) in one stack, enabled with one call.

## Highlights

- **29 dissectors plus configurable signal messages** across link, network, transport, application, and bus
  layers.
- **One registration call.** `builder.RegisterStandardProtocols()` wires protocols, dispatch tables, TCP
  heuristics, and stream reassembly.
- **Real-world traffic.** TCP stream reassembly and analysis (retransmissions, keep-alives, window scaling),
  IPv4/IPv6 defragmentation, HTTP/1 messages across segments, HTTP/2 with HPACK, WebSocket with permessage-deflate,
  SOME/IP-SD and SOME/IP-TP reassembly.
- **Automotive signal decoding.** Decode CAN or PDU payloads into named, scaled signals from a JSON description
  (DBC-like: start bit, length, byte order, factor, offset, unit, value names, multiplexing).
- **Cross-validated against Wireshark.** Protocol tests compare field values with `tshark` 4.6.
- **Wireshark-style field names** (`ip.src`, `tcp.port`, `udp.dstport`, …) for filters, exports, and value caches.
- **Extensible.** Write your own dissector with the same attributes and source generator the built-ins use.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
foreach (SettingsLoadWarning warning in warnings)
{
    Console.Error.WriteLine(warning); // PDU Transport and signal-message configuration problems
}

using Stack stack = builder.Build();
```

Parse frames with the stack as shown in the [Core README](../NetworkInspector.Core/README.md#quick-start).

## Protocol Coverage

| Layer | Protocols |
| --- | --- |
| Meta | Frame |
| Link | Ethernet II, VLAN (802.1Q, 802.1ad), LLC/SNAP, Linux SLL, Linux SLL2 |
| Network | IPv4, IPv6, ARP, ICMPv4, ICMPv6 |
| Transport | TCP, UDP |
| Application | DNS, DHCPv4, DHCPv6, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket, JSON, Text |
| Automotive and bus | CAN (classic, FD, XL), LIN, FlexRay, SOME/IP, PDU Transport, signal messages |
| Fallback | Data |

`RegisterStandardProtocols` registers, in this order: Frame, Ethernet, VLAN, IPv4, IPv6, UDP, ARP, ICMP, ICMPv6,
TCP, SLL, SLL2, LLC, DNS, DHCPv4, DHCPv6, TLS, DTLS, CAN, SOME/IP, FlexRay, LIN, HTTP, HTTP/2, WebSocket, Data,
Text, JSON, PDU Transport, and the configured signal messages. It also registers the TCP heuristic table (HTTP/1,
TLS, HTTP/2) and the TCP reassembly configurations for DNS over TCP and HTTP/1.

`RegisterFairProfilingProtocols` registers only Frame, Ethernet, IPv4, IPv6, UDP, and TCP, without heuristics and
reassembly, for like-for-like benchmarks.

## Configuration

Settings are read from the stack's `SettingsManager`. Preload or persist them before `RegisterStandardProtocols`.

| Setting | Purpose |
| --- | --- |
| `pdu_transport.config_file` | JSON file with PDU names (schema in `Schemas/`) |
| `pdu_transport.udp_dispatch_ports` | UDP ports that carry PDU Transport (default: none) |
| `signal_message.config_file` | JSON signal description for CAN or PDU payloads (schema in `Schemas/`) |
| `signal_message.show_raw`, `.show_enum`, `.max_enum_values` | Signal display options |
| `can.config_file` | JSON CAN message names |
| `tcp.max_tracked_streams`, `tcp.stream_key_last_vlan`, `tcp.stream_key_frame` | TCP stream tracking |

Example: dispatch UDP port 47290 to PDU Transport.

```csharp
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols.PduTransport;

SettingsManager settings = new();
settings.PreloadValue(PduTransportRegistration.UdpDispatchPortsSetting, SettingValue.U64Array([47290UL]));
StackBuilder builder = new(settings, new FrameInterfaceRegistry());
builder.RegisterStandardProtocols();
```

Signal and PDU configuration loading is best effort: invalid entries are skipped and reported as
`SettingsLoadWarning`, never thrown.

## Adding Custom Protocols

Custom dissectors implement `IProtocol` with a public `Parse` method and declare their fields with attributes from
`NetworkInspector.Protocols.Attributes`; the source generator bundled with Core writes the registration code.

- The first parse of each packet id is ordered and single-threaded; a re-parse of an already parsed id may run
  concurrently.
- Stateful protocols store per-packet state in `EffectStore<T>`, keyed with `MutField.TryGetEffectLayerKey`, so a
  re-parse sees the same state as the first pass.

Step-by-step guide: [`PROTOCOL_GUIDE.md`](PROTOCOL_GUIDE.md). Generator reference:
[`NetworkInspector.Generators`](../NetworkInspector.Generators/README.md).

## Limits And Thread-Safety Notes

- Register protocols during stack construction only; the built stack is immutable.
- Captures are untrusted input: dissectors bound lengths and counts, report malformed data as `packet.error`, and
  cap tracked TCP streams (`tcp.max_tracked_streams`) and reassembly buffers.
- Connection keys hash with a process-seeded hash, so crafted captures cannot force hash-table collisions.

## Learn More

- [Protocol guide](PROTOCOL_GUIDE.md): writing dissectors, fields, tables, settings, effects, reassembly
- [Core](../NetworkInspector.Core/README.md) · [Filter](../NetworkInspector.Filter/README.md) ·
  [FrameBuilder](../NetworkInspector.FrameBuilder/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
