<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Core

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Core)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Core?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Core)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

The parsing engine of NetworkInspector: turn raw frame bytes into a typed field tree, record what you need on the
way, and query across packets. Pair it with `NetworkInspector.Protocols` for the built-in dissectors.

## Highlights

- **Allocation-aware parsing.** Packets store a flat field tree in small chunks from a thread-local slab;
  `TryParse` can recycle a packet without allocating field storage again, and lazy protocol subtrees are only
  expanded when you look at them.
- **Parse only what you need.** `FieldTreeMode.Skip` decodes without building a tree, for value caches, indexes,
  and field watches that do not need the whole packet.
- **Typed values.** Every field carries a typed `FieldValue` (integers, addresses, timestamps, strings, bytes), a
  display text, and its `FieldInfo` metadata.
- **Cross-packet presence index.** `PacketIndex` records which protocols and fields occur in which packets in
  Roaring bitmaps, for fast filtering over millions of packets.
- **Columnar value caches.** `ValueCache` records selected field values into RAM columns during the first parse.
- **Stateful protocols done right.** TCP stream reassembly, IP defragmentation, and per-packet effect stores make
  re-parsing any packet, on any thread, give the same result as the first pass.
- **Results instead of exceptions.** Parsing returns `bool` plus a `ParseFailure` code; malformed input becomes
  `packet.error` fields, not crashes.
- **Declarative dissectors.** The bundled Roslyn source generator writes the registration code for your own
  protocols.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

`NetworkInspector.Values` and the source generator come with Core.

## Quick Start

Parse one Ethernet / IPv4 / UDP frame and print its fields:

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Fields;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Infos;
using NetworkInspector.Core.Settings;
using NetworkInspector.Protocols;
using NetworkInspector.Values;

// Ethernet II / IPv4 192.168.1.1 -> 192.168.1.2 / UDP 12345 -> 53, no payload (42 bytes)
byte[] rawBytes = Convert.FromHexString(
    "001122334455" + "66778899AABB" + "0800" +    // Ethernet: destination, source, EtherType
    "4500001C" + "12344000" + "4011A549" +        // IPv4: version/length, id/flags, TTL/protocol/checksum
    "C0A80101" + "C0A80102" +                     // IPv4: source, destination
    "30390035" + "00080000");                     // UDP: ports, length, checksum

// Build the stack once and reuse it; it owns and disposes the SettingsManager.
StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
builder.RegisterStandardProtocols();
using Stack stack = builder.Build();

Frame frame = Frame.Create(
    new FrameId(0),
    Timestamp.FromSecs(1_700_000_000),
    rawBytes,
    LinkType.Ethernet,
    FrameInterfaceId.Invalid,
    stack.FrameInterfaceRegistry).Value;

ParseOptions options = new(); // build a field tree
if (!Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure)
    || packet is null)
{
    throw new InvalidOperationException(failure.ToString());
}

Console.WriteLine(packet.Info);
foreach (Field field in packet.IterFieldsDfs(materialize: true))
{
    FieldInfo? info = field.FieldInfo;
    Console.WriteLine($"{info?.Name}: {field.Value}");
}
```

Output (abridged):

```text
Src Port: 12345, Dst Port: 53
frame.len: 42
eth.dst: 00:11:22:33:44:55
eth.src: 66:77:88:99:AA:BB
eth.type: 2048
ip.src: 192.168.1.1
ip.dst: 192.168.1.2
ip.ttl: 64
udp.srcport: 12345
udp.dstport: 53
udp.length: 8
```

Packet ids of first parses must be dense on a stack: 0, 1, 2, … A gap returns `ParseFailure.ParseIdGap`. Parsing
an id again is a re-parse and gives the same result.

## Core Concepts

| Type | Role |
| --- | --- |
| `StackBuilder` → `Stack` | Register protocols, settings, and tables once; `Build()` freezes them |
| `Frame` | Raw bytes, timestamp, link type, interface (from `NetworkInspector.Sources` or `Frame.Create`) |
| `Packet` | Parse result: field tree, summary `Info`, errors |
| `Field` / `FieldValue` / `FieldInfo` | Tree cursor, typed value, metadata (name, UI name, type, protocol) |
| `ParseOptions` | Field tree mode, value cache, packet index, observers for one parse |
| `PacketIndex` | Cross-packet protocol and field presence (Roaring bitmaps) |
| `ValueCache` | Columnar RAM series of selected field values |
| `SettingsManager` | Typed, persisted protocol settings with size and depth limits |

Namespaces: `NetworkInspector.Core` (stack, frame, packet, options), `.Fields`, `.Ids`, `.Infos`, `.Settings`,
`.Index`, `.ValueCaches`, `.Protocols` (dissector interfaces); value types are in `NetworkInspector.Values`.

## Common Tasks

### Parse Many Frames With Recycling

Reuse one packet object for a stream of frames to avoid a heap allocation per packet:

```csharp
ParseOptions options = new();
Packet? packet = null;
int nextId = 0;
foreach (Frame frame in frames)
{
    bool parsed = packet is null
        ? Packet.TryParse(new PacketId(nextId), stack, frame, in options, out packet, out ParseFailure failure)
        : Packet.TryParse(packet, new PacketId(nextId), stack, frame, in options, out failure);
    if (!parsed || packet is null)
    {
        continue;
    }

    nextId++;
    // Use the packet before the next iteration overwrites it.
}
```

### Parse Without A Field Tree

When you only need a `ValueCache`, a `PacketIndex`, observers, or protocol side effects, skip the tree:

```csharp
using NetworkInspector.Core.ValueCaches;

ParseOptions skip = new(FieldTreeMode.Skip, cache);
bool ok = Packet.TryParse(new PacketId(nextId), stack, frame, in skip, out Packet? throwaway, out ParseFailure failure);
```

Protocols still decode through the same code. The packet keeps no field bodies (`HasFieldTree` is false), so do
not filter, store, or export it. The cache and the index record only on the first parse of an id; pass
`recordOnReplay: true` to record on a re-parse. Default `new ParseOptions()` builds a tree.

### Collect Field Values Into Columns

Pass a `ValueCache` through `ParseOptions` to record selected fields during the first parse, then scan them
column-wise with SIMD. Usage: [`VALUECACHE_GUIDE.md`](VALUECACHE_GUIDE.md).

### Read Captures And Export

Read frames with [`NetworkInspector.Sources`](../NetworkInspector.Sources/README.md) (PCAP, PCAPNG, BLF, ASC) and
write packets with [`NetworkInspector.Exporters`](../NetworkInspector.Exporters/README.md) (JSON, CSV, Text, PBF,
Parquet, DuckDB). Packet exporters need a field tree.

### Write A Custom Dissector

Implement `IProtocol.Parse` and let the source generator write the registration code. Step by step:
[`PROTOCOL_GUIDE.md`](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md).

## Post-Parser Pipeline

Post-parsers are protocol-owned callbacks that run after the main protocol dispatch on every packet. Register them
during stack construction with `RegisterPostParser(protocolId, priority)`.

- **Order:** sorted once at build time, ascending by `priority`, then by registration order. No sorting while
  parsing.
- **Lifecycle:** they run after the dispatch tree, before `packet.info` is appended and before the packet is
  sealed. They receive the packet root as parent, so their fields are root-level siblings of the protocol fields.
- **Index:** in indexed parses (`ParseOptions.Index`) they run before `PacketIndex.EndPacket`; their presence
  records count like those of normal parsers.
- **Errors:** a `ParseResult` error or exception from a post-parser is recorded as `packet.error`. The remaining
  post-parsers still run. No error is silently discarded.

## Limits And Thread-Safety Notes

- Build a stack once and reuse it. Stack construction is single-threaded.
- The first parse of each packet id is ordered and single-threaded; re-parses of already parsed ids may run
  concurrently. A sealed packet is safe for concurrent reads.
- Dense packet maps use `ChunkedGrowOnlyStore<T>`; packed effect logs use `ChunkedAppendOnlyStore<T>`
  (`NetworkInspector.Core.Collections`). Details: [`PROTOCOL_GUIDE.md`](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md).
- Treat frame bytes from captures as untrusted input; the dissectors validate lengths and report malformed data.
- NativeAOT and trimming are not supported.

## Learn More

- [Protocols](../NetworkInspector.Protocols/README.md) · [Sources](../NetworkInspector.Sources/README.md) ·
  [Exporters](../NetworkInspector.Exporters/README.md) · [Filter](../NetworkInspector.Filter/README.md) ·
  [Root overview](../README.md)
- [Value cache guide](VALUECACHE_GUIDE.md) · [Protocol guide](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
