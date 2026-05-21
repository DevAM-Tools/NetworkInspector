<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Protocols

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols)](https://www.nuget.org/packages/NetworkInspector.Protocols)

Built-in dissector package for NetworkInspector parser stacks.

## What This Is

`NetworkInspector.Protocols` provides the ready-to-use protocol dissectors that plug into `NetworkInspector.Core`.

Use this package when you want standard protocol parsing without writing custom dissectors first.

## Why It Stands Out

- Broad built-in coverage across enterprise and automotive traffic.
- Works out of the box with one registration call.
- Keeps protocol parsing consistent across code and CLI workflows.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Protocols;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
ProtocolRegistration.RegisterStandardProtocols(builder);
Stack stack = builder.Build();
```

## Protocol Coverage

Current built-ins include:

- Link and framing: Ethernet, VLAN (802.1Q), Linux SLL/SLL2, LLC/SNAP, Frame.
- Network: IPv4, IPv6, ARP, ICMPv4, ICMPv6.
- Transport: TCP, UDP.
- Application: DNS, DHCPv4, DHCPv6, HTTP/1.x, HTTP/2, TLS, DTLS, WebSocket, JSON, Text.
- Automotive and bus: CAN (classic/FD/XL), FlexRay, LIN, SOME/IP, PDU Transport, Signal PDU.
- Fallback: Data.

## Full Built-In Protocol List

| # | Protocol | Layer |
|---|----------|-------|
| 1 | ArpProtocol | Network |
| 2 | CanProtocol | Link (Automotive) |
| 3 | DataProtocol | Fallback |
| 4 | DhcpProtocol | Application |
| 5 | Dhcpv6Protocol | Application |
| 6 | DnsProtocol | Application |
| 7 | DtlsProtocol | Application |
| 8 | EthernetProtocol | Link |
| 9 | FlexRayProtocol | Link (Automotive) |
| 10 | FrameProtocol | Meta |
| 11 | Http2Protocol | Application |
| 12 | HttpProtocol | Application |
| 13 | IcmpProtocol | Network |
| 14 | Icmpv6Protocol | Network |
| 15 | IPv4Protocol | Network |
| 16 | IPv6Protocol | Network |
| 17 | JsonProtocol | Application |
| 18 | LinProtocol | Link (Automotive) |
| 19 | LlcProtocol | Link |
| 20 | PduTransportProtocol | Automotive |
| 21 | SignalPduProtocol | Automotive |
| 22 | Sll2Protocol | Link |
| 23 | SllProtocol | Link |
| 24 | SomeIpProtocol | Application |
| 25 | TcpProtocol | Transport |
| 26 | TextProtocol | Application |
| 27 | TlsProtocol | Application |
| 28 | UdpProtocol | Transport |
| 29 | VlanProtocol | Link |
| 30 | WebSocketProtocol | Application |

## Adding Custom Protocols

For custom dissectors, use the implementation guide:

- [PROTOCOL_GUIDE.md](PROTOCOL_GUIDE.md)

## Limits And Thread-Safety Notes

- Keep protocol registration deterministic during stack construction.
- Validate assumptions at trust boundaries when parsing third-party captures.
- Use versioned release processes when introducing custom protocol behavior in shared systems.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Protocols)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Protocols)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
