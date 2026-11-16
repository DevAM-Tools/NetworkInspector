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
- **Stateful protocols done right.** TCP stream reassembly, the generic `FragmentReassembler<TKey>` (IPv4, IPv6, and
  every later fragmenting protocol, configured by `FragmentReassemblyOptions` with limits, alignment, overlap, eviction,
  restart, and gap policies), and per-packet effect stores make re-parsing any packet, on any thread, give the same
  result as the first pass. Every call of the reassembler returns a `FragmentOutcome` that names what happened, so a
  refused fragment is a reported reason and not a silent `null`. `IngestGate` is the one first-parse-versus-replay
  decision a stateful protocol needs: `IsReplay(in context, id)` for the replay path (the stack's verdict first), `Enter(in context, id)` for the scope of the first parse, and
  the outermost scope raises the watermark that publishes the packet's recorded effects.
- **Results instead of exceptions.** Parsing returns `bool` plus a `ParseFailure` code; malformed input becomes
  `packet.error` fields, not crashes.
- **Declarative dissectors.** The bundled Roslyn source generator writes the registration code for your own
  protocols.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

`NetworkInspector.Values`, `NetworkInspector.Limits`, `NetworkInspector.Settings`, and the source generator come with Core. The limits and settings packages also work without NetworkInspector: [limits](../NetworkInspector.Limits/README.md) · [settings](../NetworkInspector.Settings/README.md).

## Quick Start

Parse one Ethernet / IPv4 / UDP frame and print its fields:

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Fields;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Infos;
using NetworkInspector.Protocols;
using NetworkInspector.Settings;
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
    0,
    Timestamp.FromSecs(1_700_000_000),
    rawBytes,
    LinkType.Ethernet,
    FrameInterfaceId.Invalid,
    stack.FrameInterfaceRegistry).Value;

ParseOptions options = new(); // build a field tree
if (!Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure))
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

`PacketId` and `FrameId` convert implicitly from `int`, which is why `0` stands for both ids above; `new PacketId(0)` is the same
call. The conversion checks the range like the constructor does: `-1` is `PacketId.Invalid`, any other negative value or a value
above `ArrayIndexIdRange.MaxValue` throws `ArgumentOutOfRangeException`. An id converts back to `int` for indexing, and
`id == 5` compares ids (the `int` side is converted and range-checked).

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
| `ParseDemand` / `ParsePlan` | What each consumer needs, combined into one parse per packet (`IParseDemandSource`) |
| `PacketIndex` | Cross-packet protocol and field presence (Roaring bitmaps) |
| `ValueCache` | Columnar RAM series of selected field values |
| `SettingsManager` | Typed, persisted settings (package `NetworkInspector.Settings`) with size and depth limits |
| `SettingsRegistrar` | Registers groups and settings into a `SettingsManager`; the public constructor lets plugins register the option settings of their own short-lived managers (package `NetworkInspector.Settings`) |
| `SettingScope` | The one scope enumeration: `Application`, `Profile`, `Session` (stays in Core) |
| `CoreSettingsLayout`, `CoreLimitPolicy` | The storage names of NetworkInspector and the hit sentence with the `ni` change hint |
| `IExportTarget`, `ExportEntryName` | Where an export writes: the host hands an exporter a target that opens one write-only stream per named entry (`Main` for a single-file format); the exporters of `NetworkInspector.Exporters` take it with `ToTarget` |

Namespaces: `NetworkInspector.Core` (stack, frame, packet, options), `.Interfaces` (listener and export-target contracts), `.Fields`, `.Ids`, `.Infos`, `.Settings` (scope and layout), `.Limits` (hit hint),
`.Index`, `.ValueCaches`, `.Protocols` (dissector interfaces); value types are in `NetworkInspector.Values`.

## Common Tasks

### Three Ways To Read Field Values

| You want | Use | Parse cost |
|----------|-----|------------|
| Everything about one packet (topology, every occurrence, texts) | **Field tree** | builds the tree of every packet |
| One field over many packets, as a column in RAM | **Value cache** | no tree (`FieldTreeMode.Skip`) |
| To act on chosen fields while the parse runs (export, metric, trigger) | **Field observer** | no tree; only the watched fields are delivered |

The snippets below are the smallest complete use of each. They run as tests
([`CoreApiSampleTests`](../NetworkInspector.Core.Tests/CoreApiSampleTests.cs)); `stack` and `frame` are the ones of the
[Quick Start](#quick-start), `dstPort` is `stack.GetFieldId("udp.dstport")!.Value`.

**Field tree.** Parse with the default options, then look fields up or walk the tree:

```csharp
ParseOptions options = new();                                   // builds a field tree
Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure);

packet!.TryGetFieldValue(dstPort, out FieldValue value, materialize: true);   // first occurrence: 53

FieldLookupCookie cookie = FieldLookupCookie.Start;             // every occurrence (tunnels, VLAN tags)
while (packet.TryGetNextField(dstPort, ref cookie, out Field field, materialize: true))
{
    Console.WriteLine(field);                                   // the default display line: "Destination Port: 53"
}
```

`field.ToString()` (also `$"{field}"` and the allocation-free `TryFormat`, on `MutField` too) is the **default display line**: a custom
text is the whole line (a dissector that wants the label writes it into the text); a field without a value shows its UI name;
every other field shows the UI name, `": "`, and the value's default string (its custom representation, else the default text of
the data, which is also what `field.Value.ToString()` returns). A field id that the stack does not know (it has no `FieldInfo`) has no
label, so its line is the value part alone. Every part stays readable, so a caller builds an own line from `field.FieldInfo?.UiName`
(never empty: registration refuses an empty UI name), `field.Value` (`Data`, whose `ToString` is the default
text of the data, and `CustomRepresentation`), and `field.CustomText`.

**Value cache.** Name the fields once, parse without a tree, read a column through a snapshot handle:

```csharp
ValueCache cache = new(stack, [new ValueCacheFieldConfig(dstPort, ValueCaptureMode.FirstOccurrence)]);
ParseOptions options = new(FieldTreeMode.Skip, cache);
for (int i = 0; i < frames.Count; i++)
{
    Packet.TryParse(i, stack, frames[i], in options, out _, out _);
}

ValueCacheSeriesHandle<ulong> handle = cache.GetSeries<ulong>(dstPort).Handle;   // frozen Count, stable rows
foreach (ValueCacheRow<ulong> row in handle)
{
    Console.WriteLine(row.Value);
}
```

**Field observer.** Implement `IFieldObserver`, pair it with a watch, and parse without a tree:

```csharp
sealed class PortRecorder(FieldId wanted) : IFieldObserver
{
    public void BeginPacket(PacketId id, Timestamp timestamp) { }

    public void OnField(in FieldVisit visit)
    {
        if (visit.FieldId == wanted)                            // ForExtraction also delivers sibling fields of the protocol
        {
            Console.WriteLine(visit.Value);                     // visit.CustomText: the text set at append
        }
    }

    public void EndPacket(PacketId id, bool complete) { }       // close or discard what you staged for this packet
}

ParseObserver[] observers = [new() { Observer = new PortRecorder(dstPort), Watch = FieldWatch.ForExtraction(stack, dstPort) }];
ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
Packet.TryParse(0, stack, frame, in options, out _, out _);
```

Rules that apply to all three: ids of first parses are dense (0, 1, 2, …); a packet parsed with `FieldTreeMode.Skip` has no tree
(`HasFieldTree` is `false`), so do not look fields up in it; an observer is called from the parse thread and is not thread-safe, and a cache has one
writer (readers may read a snapshot handle concurrently). To feed several consumers from one parse, combine their needs in a
[`ParsePlan`](#combine-consumers-into-one-parse-plan).

### Parse Many Frames With Recycling

Reuse one packet object for a stream of frames to avoid a heap allocation per packet:

```csharp
ParseOptions options = new();
Packet? packet = null;
int nextId = 0;
foreach (Frame frame in frames)
{
    bool parsed = packet is null
        ? Packet.TryParse(nextId, stack, frame, in options, out packet, out ParseFailure failure)
        : Packet.TryParse(packet, nextId, stack, frame, in options, out failure);
    if (!parsed)
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
bool ok = Packet.TryParse(nextId, stack, frame, in skip, out Packet? throwaway, out ParseFailure failure);
```

Protocols still decode through the same code. The packet keeps no field bodies (`HasFieldTree` is false), so do
not filter, store, or export it. The cache and the index record only on the first parse of an id; pass
`recordOnReplay: true` to record on a re-parse. Default `new ParseOptions()` builds a tree.

A dissector that fails during a Skip parse stores no `packet.error` field, because there is no tree: `Packet.ErrorCount` is the
error signal of such a parse. It is zero after a clean parse and counts every dissector error, caught exception, and observer
failure, in both tree modes.

### Observe Fields During The Parse

A `ParseObserver` receives the fields of a parse while it runs, with or without a field tree. Pair an `IFieldObserver` with a
`FieldWatch` (which fields) and, when you need display text, a `DisplayText` watch:

```csharp
ParseObserver[] observers =
[
    new()
    {
        Observer = recorder,                                   // your IFieldObserver
        Watch = FieldWatch.ForExtraction(stack, srcPort),      // delivers srcPort even from deferred containers
        DisplayText = FieldWatch.Only(stack, srcPort),         // dissectors also build its custom text
    },
];
ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
```

The observer sees `BeginPacket(id, timestamp)`, `OnField(in FieldVisit)` for every accepted field, and then **exactly one**
`EndPacket(id, complete)` per `BeginPacket` that ran, before the packet is sealed. `complete` is `false` when the observers
were muted (an observer threw) or an exception escaped the dissector boundaries; a dissector error does not clear it (read
`Packet.ErrorCount`). A consumer that staged fields uses this call to close or discard them and to
evaluate deferred custom text. `FieldVisit.CustomText` is a `ref readonly` to the text set when the field was appended: with a
field tree it refers to the field's own slot. A deferred text builds its string once, however many copies of the struct exist, so
keeping a copy (`LazyString text = visit.CustomText;`) is cheap and never builds the text twice. Read the text in `EndPacket`, not
in `OnField`: a dissector may complete a deferred text after the append, and the first evaluation freezes the string for the
tree and every copy. With a tree keep `visit.Field` and read `Field.CustomText`; without a tree keep the copied struct.

`EndPacket` is a required member of `IFieldObserver`: every observer implements it. An observer that keeps no state per packet
leaves the body empty, as the examples above show.

- **Why `ForExtraction` and not `Only`:** `Only` expands a deferred container only when the container or a field of its index
  group is watched, so a field in a container outside that group is silently missing in a parse without tree.
  `ForExtraction(stack, ids)` accepts the ids plus every field of their owning protocols, so every container that can hold a
  listed field expands. Cost: more expansion, paid once per owning protocol.
- **Why a separate `DisplayText` watch:** formatting text costs work that filters and most observers do not need. With
  `FieldWatch.None` (the default) a parse without tree builds no custom text for the observer.
- **Custom text is final at append.** Observers see the text present at the append; changing it later
  (`SetCustomText`, `AppendCustomText`, `ClearCustomText`) is discouraged and throws `InvalidOperationException` after the
  packet is sealed. See the [protocol guide](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md) §14.5.
- Observers run on every parse of a frame, including replays; the value cache and the index record only on the first parse
  unless `recordOnReplay` is set.

### Combine Consumers Into One Parse Plan

Several consumers of one run (an exporter, a filter, a value cache) each state what they need as a `ParseDemand`; a
`ParsePlan` takes the least parse work that serves all of them and parses each packet once:

```csharp
ParseDemand[] demands =
[
    filter.ParseDemand,                                   // FilterObserver implements IParseDemandSource; list the filter first
    ParseDemand.Fields(new ParseObserver { Observer = exportObserver, Watch = exportWatch }),
];
if (!ParsePlan.TryCreate(stack, demands, out ParsePlan? plan, out ParsePlanError error))
{
    throw new InvalidOperationException(error.Message);   // e.g. two caches, a foreign cache, one observer in two demands
}

ParseOptions options = plan.Options;                      // Skip unless a demand needs the field tree
Console.WriteLine(plan);                                  // e.g. "fields only, no tree (2 fields, 2 observers)"
```

| Depth | Meaning | Chosen when |
|-------|---------|-------------|
| `ParseDepth.None` | no parse at all | only frame exporters, no filter |
| `ParseDepth.Packet` | parse without field tree | observers, a value cache, or an index |
| `ParseDepth.FieldTree` | parse with field tree | a consumer walks or queries the tree |

**Why:** when the pipeline fixes the parse work (always a tree), every consumer pays for it; an export of two values pays
for the tree of every packet. Observers stay in demand order, so a filter listed first has its verdict before the exporter's
packet is committed. One plan per stack and per parse thread; the observers are not thread-safe.

### Collect Field Values Into Columns

Pass a `ValueCache` through `ParseOptions` to record selected fields during the first parse, then scan them
column-wise with SIMD. Usage: [`VALUECACHE_GUIDE.md`](VALUECACHE_GUIDE.md).

### Read Captures And Export

Read frames with [`NetworkInspector.Sources`](../NetworkInspector.Sources/README.md) (PCAP, PCAPNG, BLF, ASC) and
write packets with [`NetworkInspector.Exporters`](../NetworkInspector.Exporters/README.md) (JSON, CSV, Text, PBF,
Parquet, DuckDB). A packet exporter states what it needs as a `ParseDemand`; only a hierarchy export needs the field tree (see
[Combine Consumers Into One Parse Plan](#combine-consumers-into-one-parse-plan)).

### Read And Change Settings

Typed, persisted settings are the job of the package `NetworkInspector.Settings` ([README](../NetworkInspector.Settings/README.md),
[guide](../docs/guides/settings.md)); Core references it and adds what is specific to NetworkInspector:

- **The layout.** `CoreSettingsLayout.Instance` (namespace `NetworkInspector.Core.Settings`) names the application folder
  `NetworkInspector`, the folders that are no profiles (`logs`, `plugins`, `plugins-data`, `plugin-scan-cache`, `host`, `app`,
  `presets`), and the two bookkeeping files `.ni-settings.lock` and `.ni-settings-owned`. Every earlier release wrote the same
  two file names, so a changed name would strand files; pass the layout to `new SettingsManager(folder, layout)`,
  `SettingsManagerFactory`, `SettingsProfiles`, and `ProfileNames`.
- **The scope.** `SettingScope` (`Application`, `Profile`, `Session`) says which settings manager of a host a setting of a
  plugin belongs to. It stays in Core because a session is an idea of the host.
- **The stack.** `StackBuilder` takes the manager; protocols register their settings through `IStackBuilder.SettingsRegistrar`,
  and the built `Stack` owns and disposes the manager.

```csharp
using NetworkInspector.Core.Settings;
using NetworkInspector.Settings;

using SettingsManager manager = new(folder, CoreSettingsLayout.Instance);
manager.RegisterSetting(Setting.I64("net.retries", "Retries", "net", 3, min: 0, max: 10, activation: SettingActivation.SessionReset));
manager.Load();

Setting retries = manager.GetSetting("net.retries")!;   // registered above
if (!SettingValueText.TryParse(retries, "25", out SettingValue value, out SettingProblem? problem)
    || !retries.TrySetPendingValue(value, out problem))
{
    Console.Error.WriteLine(problem);   // one line: the setting name and the allowed range
    return;
}

manager.ApplyChanges(["net.retries"]);   // pending → current for this key only
manager.SaveChanged(["net.retries"]);    // merges this key into its group file
```

The text and JSON forms, sparse files, the folder lock, profiles, and the load warnings are described in the
[settings README](../NetworkInspector.Settings/README.md) and its [guide](../docs/guides/settings.md).

### Limits

A limit is a bound at a trust boundary (a file, a network stream, a plugin, a filter expression) whose right value depends on
the data or on how much the user trusts the input. The package `NetworkInspector.Limits`
([README](../NetworkInspector.Limits/README.md), [guide](../docs/guides/limits.md)) declares such a bound as one
`LimitDefinition` that the setting, the option, the protocol field, the command line, the help, and the hit text all read; the
live value for a hot loop is a `Limit` (`manager.GetLiveLimit(key)`). A bound that is a fact of a format, an internal tuning
value, or a display rule stays a constant with a comment that says which.

Every default is one fixed number sized for a reference machine of 8 logical processors and 16 GB of memory
([`docs/decisions/limits-policy.md`](../docs/decisions/limits-policy.md)); no default is read from the machine at run time. Core
adds one member in `CoreLimitPolicy` (namespace `NetworkInspector.Core.Limits`):

| Member | Policy |
| --- | --- |
| `HitMessage(eventText, effective)` | The hit sentence of the library with the change hint of `ni`: `ni settings set <key> <value>` for a stored change and `--set <key>=<value>` for one run |

```csharp
using NetworkInspector.Core.Limits;
using NetworkInspector.Limits;
using NetworkInspector.Settings;

// One catalog per project; a test checks it for duplicate keys.
IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check(ArchiveLimits.All);

// Expose the limit as a setting (read it with GetLimit) or as a source or format option (RegisterLimitOption).
registrar.RegisterLimit(ArchiveLimits.MaxEntrySize);

// Enforce: read the ceiling once per decision, and report a hit with the standard sentence.
long ceiling = options.MaxEntrySize.Ceiling;
if (entrySize > ceiling)
{
    return ArchiveLimits.MaxEntrySize.HitMessage(
        Invariant($"Entry is {ArchiveLimits.MaxEntrySize.Format(LimitValue.Of(entrySize))}"), options.MaxEntrySize);
}
```

`RegistrationOptions.CreateDefaults` seeds a source or format option from the stored limit with the same key, so an inline
option overrides the stored value for one source or output. Data is never truncated, dropped, or clamped silently: a hit is an
error, an `ItemSkipped`, or a counted message.

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
