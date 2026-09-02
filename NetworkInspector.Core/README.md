<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Core

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core)](https://www.nuget.org/packages/NetworkInspector.Core)

`NetworkInspector.Core` is the parsing runtime of the NetworkInspector stack.

## What This Is

Use this package when you need to turn raw frame bytes into structured packet fields and inspect those fields in .NET code.

Core provides:

- parser stack construction (`StackBuilder` and `Stack`),
- frame creation and packet parsing APIs,
- field traversal primitives used by exporters and custom analysis code,
- runtime plumbing used by built-in protocol packages.

## Why It Stands Out

- Built for high-throughput parsing workflows.
- Integrates directly with built-in dissectors from `NetworkInspector.Protocols`.
- Works in both application code and service pipelines.

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

## Post-Parser Pipeline

Post-parsers are protocol-owned callbacks that run after the main protocol dispatch on every packet. Register them during stack construction with `RegisterPostParser(protocolId, priority)`.

**Execution order** — post-parsers are sorted once at build time: ascending by `priority` (lower values run first), then ascending by registration order as a stable tie-breaker. No sorting overhead occurs during parsing.

**Lifecycle** — post-parsers execute after the full protocol dispatch tree, before `packet.info` is appended, and before the packet is sealed. They receive the packet root field as parent, so their fields appear as root-level siblings identical to top-level protocol fields.

**Index** — in indexed parses (`ParseFrameIndexed`), post-parsers run before `PacketIndex.EndPacket`. Their `RecordProtocolPresence` and `RecordGroupPresence` calls are treated identically to those of normal parsers. **ValueCache** — `ParseFrameRecorded` tees selected field values into RAM columns during the same first parse (with or without a `PacketIndex`); unrecorded `ParseFrame` is a no-op on the tee. Membership is a compact array (few fields) or a dense probe plus bitset. See `docs/value-cache-design.md`.

**Error policy** — a `ParseResult` error or exception from any post-parser is recorded as a `packet.error` and made visible. Remaining post-parsers always continue executing regardless of earlier failures. No errors are silently discarded.

## Common Tasks

### Register Standard Protocols

Use `ProtocolRegistration.RegisterStandardProtocols(builder)` to activate the default dissector set.

### Parse From Capture Readers

Combine Core with `NetworkInspector.Sources` readers to parse frames from PCAP/PCAPNG/BLF/ASC sources.

### Feed Export Pipelines

Parse packets with Core, then send them to `NetworkInspector.Exporters` packet exporters (JSON/PBF/CSV/Text).

## Practical Notes

- Build stacks once and reuse for batch parsing jobs.
- Dispose stacks explicitly when parsing is complete.
- Keep parser lifecycle and mutable operations in controlled execution scopes.

## Limits And Thread-Safety Notes

- First parse of each packet id is ordered and single-threaded (dense ids `0,1,2,…`). Re-parse of an already-first-parsed id may run concurrently. Protocol authors implement `IProtocol.Parse`. `Stack.CallProtocol` sets `ParseContext.SelfProtocolId` only. Stateful protocols key `EffectStore<T>` with `Packet.GetEffectLayerKey`. Dense packet maps use `ChunkedGrowOnlyStore<T>`; packed effect logs use `ChunkedAppendOnlyStore<T>`. `Stack.ProtocolCount` is the number of registered protocols. See [PROTOCOL_GUIDE.md](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md).
- Treat stack construction and mutable parse contexts as single-threaded unless package docs state otherwise.
- Validate external frame bytes at system boundaries.
- Use cancellation in surrounding workflow code for long-running ingest loops.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Core)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Core)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)
- [Protocols package](../NetworkInspector.Protocols/README.md)
- [Sources package](../NetworkInspector.Sources/README.md)
- [Exporters package](../NetworkInspector.Exporters/README.md)
- [Root overview](../README.md)

## License

[MIT License](../LICENSE)
