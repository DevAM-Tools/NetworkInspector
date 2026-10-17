<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Text Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`TextExporter` prints parsed packets as an indented protocol tree, similar to the detail pane of Wireshark. Use it
to look at parse results without a viewer, in CI artifacts, and in triage logs. It is a packet exporter
(`IPacketListener`): packets must be parsed with a field tree (`FieldTreeMode.Build`, the default).

## Highlights

- **Readable protocol trees** with the same display texts as the dissectors (`Ethernet II, Src: …`).
- **Three detail levels.**
- **Bounded output.** Long strings and byte values are truncated (256 characters by default).
- **Stdout support** for live inspection in a terminal.

## Detail Levels

| Level | Output |
| --- | --- |
| `TextDetailLevel.Summary` | Container fields (`FieldType.None`) only |
| `TextDetailLevel.Standard` (default) | Fields with values, without `Bytes` fields |
| `TextDetailLevel.Full` | Every field; `Bytes` values as lowercase hex |

> **Known issue in 0.16.0:** protocol containers (`frame`, `eth`, `ip`, `udp`, …) carry their raw bytes as a
> `Bytes` value. `Summary` and `Standard` therefore omit every protocol together with its subtree and print little
> more than the packet header. Use `TextDetailLevel.Full` (CLI: `text:level=full`) for the complete protocol tree.

Excerpt of `Full` output for one UDP packet:

```text
Packet 1  [2023-11-14T22:13:20.000000000Z]
Ethernet: Ethernet II, Src: 00:11:22:33:44:55, Dst: FF:FF:FF:FF:FF:FF
  Destination: FF:FF:FF:FF:FF:FF (Multicast, Locally Administered)
  Source: 00:11:22:33:44:55 (Unicast, Globally Unique)
  Type: IPv4 (0x0800)
IPv4: Internet Protocol Version 4, Src: 192.168.0.1, Dst: 192.168.0.2
  Source Address: 192.168.0.1
  Time To Live: 64
UDP: User Datagram Protocol, Src Port: 12345, Dst Port: 53
  Source Port: 12345
  Destination Port: 53
```

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Text;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteText(IEnumerable<Packet> packets)
{
    using TextExporter exporter = TextExporter.CreateBuilder()
        .ToStdout()
        .WithDetailLevel(TextDetailLevel.Full)
        .Build();

    foreach (Packet packet in packets)
    {
        if (!exporter.OnPacket(packet))
        {
            break;
        }
    }

    exporter.OnFinish();
}
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithDetailLevel(level)` | Summary, Standard, or Full | `Standard` |
| `WithMaxTextLength(int)` | Truncate string and byte values (`0` = no limit) | 256 |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- The packet header number is the running count of written packets, not `Packet.Id`.
- `Full` is also truncated by `WithMaxTextLength`; pass `0` for complete values. Full detail on byte-heavy
  traffic can produce very large files.
- Packets parsed with `FieldTreeMode.Skip` are skipped.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
