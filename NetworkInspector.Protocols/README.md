<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.Protocols

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Protocols)](https://www.nuget.org/packages/NetworkInspector.Protocols)

30 built-in protocol dissectors for the NetworkInspector packet analysis framework.
Each protocol is a self-contained parser that registers fields, parses binary data,
and dispatches to sub-protocols via typed dispatch tables.

## Supported Protocols

| # | Protocol | Name | Layer |
|---|----------|------|-------|
| 1 | `ArpProtocol` | ARP | Network |
| 2 | `CanProtocol` | CAN (classic / FD / XL) | Link (Automotive) |
| 3 | `DataProtocol` | Data | Fallback |
| 4 | `DhcpProtocol` | DHCPv4 | Application |
| 5 | `Dhcpv6Protocol` | DHCPv6 | Application |
| 6 | `DnsProtocol` | DNS | Application |
| 7 | `DtlsProtocol` | DTLS | Application |
| 8 | `EthernetProtocol` | Ethernet | Link |
| 9 | `FlexRayProtocol` | FlexRay | Link (Automotive) |
| 10 | `FrameProtocol` | Frame | Meta |
| 11 | `Http2Protocol` | HTTP/2 | Application |
| 12 | `HttpProtocol` | HTTP/1.x | Application |
| 13 | `IcmpProtocol` | ICMPv4 | Network |
| 14 | `Icmpv6Protocol` | ICMPv6 | Network |
| 15 | `IPv4Protocol` | IPv4 | Network |
| 16 | `IPv6Protocol` | IPv6 | Network |
| 17 | `JsonProtocol` | JSON | Application |
| 18 | `LinProtocol` | LIN | Link (Automotive) |
| 19 | `LlcProtocol` | LLC/SNAP | Link |
| 20 | `PduTransportProtocol` | PDU Transport | Automotive |
| 21 | `SignalPduProtocol` | Signal PDU | Automotive |
| 22 | `Sll2Protocol` | Linux SLL2 | Link |
| 23 | `SllProtocol` | Linux SLL | Link |
| 24 | `SomeIpProtocol` | SOME/IP | Application |
| 25 | `TcpProtocol` | TCP | Transport |
| 26 | `TextProtocol` | Text | Application |
| 27 | `TlsProtocol` | TLS | Application |
| 28 | `UdpProtocol` | UDP | Transport |
| 29 | `VlanProtocol` | VLAN (802.1Q) | Link |
| 30 | `WebSocketProtocol` | WebSocket | Application |

## Registration

All protocols are registered via `ProtocolRegistration.RegisterStandardProtocols()`:

```csharp
StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
ProtocolRegistration.RegisterStandardProtocols(builder);
Stack stack = builder.Build();
```

## Adding New Protocols

See [PROTOCOL_GUIDE.md](PROTOCOL_GUIDE.md) for a comprehensive implementation guide
covering source generator attributes, field types, dispatch tables, index groups,
error handling, and the complete checklist for new protocols.

## License

[MIT License](../LICENSE) — © DevAM
