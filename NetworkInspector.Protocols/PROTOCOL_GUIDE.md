<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# C# Protocol Implementation Guide

> Comprehensive reference for implementing protocol parsers in **NetworkInspector**.
> Based on the production patterns in `FrameProtocol.cs`, `EthernetProtocol.cs`,
> `IPv4Protocol.cs`, `IPv6Protocol.cs`, `UdpProtocol.cs`, and `VlanProtocol.cs`.
> Target: .NET 10+, ZeroAlloc source generator, lazy field materialization architecture.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Threading & Execution Model](#2-threading--execution-model)
3. [Class Structure & Naming Conventions](#3-class-structure--naming-conventions)
4. [Source Generator Attributes](#4-source-generator-attributes)
5. [Field Attributes Reference](#5-field-attributes-reference)
6. [Constants — What to Define and What Not](#6-constants--what-to-define-and-what-not)
7. [Cross-Protocol References](#7-cross-protocol-references)
8. [The Parse Method](#8-the-parse-method)
9. [Lazy Field Materialization](#9-lazy-field-materialization)
10. [Protocol Dispatch Tables](#10-protocol-dispatch-tables)
    - [PDU Transport → Signal Message](#109-pdu-transport--signal-message)
11. [Heuristic Protocol Tables](#11-heuristic-protocol-tables)
12. [Dispatch Cache Optimization](#12-dispatch-cache-optimization)
13. [Index Groups & PacketIndex](#13-index-groups--packetindex)
    - [Field Alias Groups (Any-Match Names)](#13a-field-alias-groups-any-match-names)
    - [Primary / Details Field Layout](#13b-primary--details-field-layout)
    - [Mandatory Eager Rules](#13c-mandatory-eager-rules)
14. [String Handling & Display Text](#14-string-handling--display-text)
15. [Error Handling](#15-error-handling)
16. [Binary Header Parsing](#16-binary-header-parsing)
17. [FieldValue Factories](#17-fieldvalue-factories)
18. [Protocol Registration](#18-protocol-registration)
19. [Source Generator Output](#19-source-generator-output)
20. [Current Dispatch Table Map](#20-current-dispatch-table-map)
21. [Checklist for New Protocols](#21-checklist-for-new-protocols)

---

## 1. Architecture Overview

NetworkInspector uses a **layered protocol stack** where each protocol is a
self-contained parser that:

1. **Registers** its fields, dispatch tables, and settings at build time via a Source Generator.
2. **Parses** packet data at runtime, producing a tree of lazily-materialized fields.
3. **Dispatches** to sub-protocols via typed dispatch tables (e.g., EtherType → IPv4).

The field tree is the central data structure — filters, UI, and exporters all
operate on it. Protocols produce fields; everything else consumes them.

```
Packet bytes
    │
    ▼
  FrameProtocol ──── frame.link_type table ────► EthernetProtocol
                                                       │
                                                 eth.type table
                                                   ┌───┼───┐
                                                   ▼   ▼   ▼
                                                IPv4  IPv6  VLAN
                                                  │    │
                                            ip.proto table
                                              ┌───┼───┐
                                              ▼   ▼   ▼
                                            UDP  TCP  ICMP
                                              │    │
                                        udp.port  tcp.port table
                                           │         │
                              ┌────────────┼─────┐   ▼
                              ▼            ▼     ▼  HTTP, TLS, …
                            DNS      PDU Transport  …
                                           │
                                  pdu_transport.id
                                           ▼
                                    Signal Message
```

**Field tree structure** (sibling layout, not nested):

```
root
├── frame: Frame 1, 128 bytes, Ethernet
│   ├── frame.id: 1
│   └── ...
├── eth: Ethernet II, Src: AA:BB:CC:DD:EE:FF, Dst: 11:22:33:44:55:66
│   ├── eth.dst: 11:22:33:44:55:66
│   ├── eth.src: AA:BB:CC:DD:EE:FF
│   └── eth.type: 0x0800 (IPv4)
├── ip: Internet Protocol Version 4, Src: 192.168.1.1, Dst: 10.0.0.1
│   ├── ip.version: 4
│   ├── ...
│   ├── ip.src: 192.168.1.1
│   └── ip.dst: 10.0.0.1
├── udp: User Datagram Protocol, Src Port: 12345, Dst Port: 53
│   ├── udp.srcport: 12345
│   ├── udp.dstport: 53
│   └── ...
├── pdu_transport: PDU Transport                 // sibling of udp when bound on udp.port
│   └── pdu_transport.pdu: PDU: BenchPdu (ID: 32)
│       ├── pdu_transport.id / length / name
│       └── pdu_transport.payload                // only when no sub-protocol matches
└── fixture_message: Fixture PDU                 // sibling of pdu_transport (dispatch on parentField)
    ├── fixture_message.EngineRpm
    └── fixture_message.Thr
```

> Sub-protocols are **siblings** of their parent protocol, not children.
> Dispatch always happens on `parentField`, not inside a protocol's container field.
> When the frame protocol consumes less than the capture buffer, the tail is
> `packet.unparsed_data` under the packet container (§8). Ethernet padding stays on `eth`.

---

## 2. Threading & Execution Model

### Parse Execution

The **first** parse of each packet is single-threaded and ordered: one thread walks the
packets of a capture in ascending id order, and that thread alone drives every protocol
on the stack. Once a packet has been parsed, however, it may be parsed *again* from any
thread at any time — the UI, filter evaluation and export all do this, concurrently and
while the first-parse thread is still working through later packets. So a `Stack` is
owned by one *first-parse* thread, not by one thread outright. This means:

- **No locks, no synchronization primitives** in protocol code.
- Protocol instance fields that are written during parsing need the watermark and
  effect-store pattern described below; everything else must be set up during the build
  phase (`OnStartCustom()`) and treated as read-only afterwards, or kept in
  `[ThreadStatic]` fields. Most protocols are stateless and need neither.
- The `in MutField` parameter is a `ref struct` — it cannot escape the call stack.

### Random Access Parsing

Packets must be parseable **multiple times** (random access from UI, filter evaluation,
export). The same packet data may be fed to `Parse()` again at any time. This means:

- **No mutable per-packet state** stored on the protocol instance between parse calls.
  All packet-specific state lives in the field tree (`MutField` / `FieldValue`).
  Protocols that genuinely need cross-packet state (connection trackers, reassembly and
  fragment buffers) are the documented exception and must follow the watermark plus
  effect-store pattern described under *First parse vs. re-parse* below — otherwise a
  concurrent re-parse races on that state.
- Caches and dispatch tables are **per-stack** state, initialized once in `OnStartCustom()`
  and immutable during parsing.
- A `LazyPopulator` may be called zero or one time per packet — never assume it runs.
- Lazy fields store their raw header bytes in `FieldValue` so re-parsing is self-contained.

### First parse vs. re-parse (concurrent subsequent parses)

There is **no parse-mode parameter**. `Packet.ParseFrame` / `ParseFrameIndexed` (plus the
`TryParseFrame*` recycle variants) are the only entry points, and `ParseContext` carries
nothing about parse intent. Each stateful protocol decides for itself whether a call is a
first parse or a re-parse.

**Mechanism — watermark.** Every stateful protocol holds
`private volatile int _IngestWatermark = -1`, the highest packet id whose first parse completed.
`_IsReplay(id)` is `id.Value <= _IngestWatermark` (plain volatile read).

- `id > watermark` → **first parse**: mutate cross-packet state (trackers, reassembly and
  fragment buffers) and record a compact **effect** for the packet.
- `id <= watermark` → **re-parse**: read the recorded effect only. Never mutate shared
  state. A missing effect degrades to a stateless path — it never falls back to mutation.

The watermark is raised in a `finally` at the end of `IProtocol.Parse` (outermost ingest only),
so even error and exception exits close the door: a later re-parse of that id can never
re-enter the first-parse path.

Authors implement `IProtocol.Parse`. `Stack.CallProtocol` stamps `ParseContext.SelfProtocolId`
and then invokes the bound `ParseDelegate`. A raw `protocol.Parse(...)` or a cached
`ParseDelegate` is a valid entry; it simply does not go through `CallProtocol`. Effect keys
do not depend on that stamp.

**Mechanism — layer key.** Effects are keyed by `(PacketId, packed buffer location)`.
`Packet.GetEffectLayerKey(data)` packs the buffer index (`0` = `Frame.Data`, `1…` =
`Packet.AddBuffer`) into bits 31–24 and the byte offset of the `Parse` `data` slice into
bits 23–0. The argument must be the slice passed into `Parse`, not a heap copy. First parse
and reparse of the same frame see the same key because the bytes and additional buffers are
stable. Do not key on remaining length (`data.Length`) or any walk ordinal. Effects live in
`EffectStore<TEffect>`: one packed row per packet that actually ran the protocol, binary
search on replay, nested layers chained when the tail entry already belongs to the same
packet id. Without the packed location, the inner layer of a tunnel (or a defragmented
datagram in an additional buffer) would collide with an outer layer and replay the wrong
values. Reassembled payloads must be attached with `Packet.BindParseBuffer` before the
nested `Parse` so the inner slice is a packet buffer, not a heap copy.

`Stack.ProtocolCount` is how many protocols are registered. There is no
packet walk ordinal. `ProtocolId` is assigned at `RegisterProtocol` time.

Protocols must not retain `Packet` references. Cross-packet protocol state is keyed by
`PacketId` and layer key where needed.

Effect stores live on the protocol instances and therefore share the `Stack` lifetime — a
stack swap creates fresh protocols with empty stores.

**Contract for callers.** First parses must be ordered, single-threaded, and use dense packet
ids `0, 1, 2, …`. `Packet.ParseFrame` throws `InvalidOperationException` on a jump (for example
id 5 after id 0). Re-parses of already-parsed ids may run on any number of threads at any time,
including while later packets are being parsed for the first time. The session guarantees the
first half structurally: the source loop parses under `_ParseLock` with ids from a monotonic
allocator starting at 0, while `TryGetPacket` re-parses lock-free via `_TryReparseFrame`.
`ParseFrameIndexed` is safe on a re-parse: the index no-ops for a packet it has already seen.

Stateful protocols still use their own watermark (`id <= protocol watermark` → replay) because
they are not invoked for every packet. The dense check lives on the `Stack`, which sees every
`ParseFrame` call.

**Audit of cross-packet mutable state** (every protocol that survives state across packets):

| Protocol | Cross-packet state | Measure |
|----------|--------------------|---------|
| `TcpProtocol` | `_ConnectionTracker`, `_ReassemblyEngine` | Watermark + `EffectStore<TcpLayerEffect>` (analysis facts, dispatch mode, reassembled PDU bytes). Re-parse without effect → stateless raw-port dispatch. |
| `UdpProtocol` | `_StreamTracker` | Watermark + `EffectStore<StreamEffect>`. Re-parse without effect → no `udp.stream` field. |
| `IPv4Protocol` | `_Defragmenter` fragment buffers | Watermark + `EffectStore<DefragLayerEffect>` holding the reassembled datagram (only for the completing fragment). Re-parse without effect → fragment fields, no reassembly. |
| `IPv6Protocol` | `_Defragmenter` fragment buffers | Same as IPv4. |
| `SomeIpProtocol` | `_TpReassembler` sessions | Watermark + `EffectStore<SomeIpTpReassemblyResult>`. Re-parse without effect → segment reported without reassembly. |
| `EthernetProtocol`, `IPv4Protocol`, `IPv6Protocol` | address caches | `[ThreadStatic]` — thread-local, safe as-is. |
| `SignalMessageProtocol`, `CanProtocol`, `SomeIpProtocol` name maps, all `*SparseCache` / `*DelegateCache` | dispatch and display lookups | Written during registration / `OnStart` only, immutable during parsing — safe as-is. |

All other protocols hold no cross-packet mutable state.

### Lifecycle

```
Construction     → new UdpProtocol()              Default state, no IDs yet
Registration     → RegisterFields(builder, id)    Source Generator wires fields, tables, settings,
                                                  loads setting backing fields, then calls
                                                  RegisterFieldsCustom(builder, protocolId)
Stack Build      → Stack.Build()                  All registrations frozen, IDs finalized,
                                                  When*Registered callbacks fired
OnStart          → OnStartCustom(stack)           Build dispatch caches, resolve cross-protocol
                                                  fields against the frozen stack
Parse (×N)       → Parse(parentField, data, stack) Hot path — called once per packet per protocol
OnShutdown       → OnShutdownCustom(stack)        Cleanup (rarely needed)
```

**Two custom hooks — when to use which:**

- `RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)` — runs during
  registration. Use it for **anything that depends on settings or registers entries
  into the stack** (loading config files, registering parsers in dispatch tables
  derived from config, declaring additional fields). Settings are already loaded
  into backing fields when this hook runs. Use `builder.WhenFieldRegistered(...)`
  / `WhenProtocolTableRegistered(...)` to defer references to entities that may
  be registered by another protocol later (the callbacks fire immediately if the
  entity already exists, otherwise during `Build`).
- `OnStartCustom(Stack stack)` — runs after the stack is built and frozen. Use it
  for **work that requires the immutable stack**: pre-allocating lazy populators,
  building dispatch caches via `stack.BuildU64DelegateCache(...)`, resolving
  cross-protocol field IDs, caching parse delegates.

The stack itself is **immutable** after `Build()`. There is no runtime API to
register new fields, parsers, or settings — config-driven registration must run
in `RegisterFieldsCustom`.

---

## 3. Class Structure & Naming Conventions

### File Layout

One protocol per file. The file contains the protocol class and its binary header
struct (if any). Large protocols may split sub-field modules into separate files
(e.g., `TcpOptions.cs`, `TcpAnalysis.cs`).

### Class Declaration

```csharp
[Protocol("udp", "User Datagram Protocol", Description = "UDP (RFC 768)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class UdpProtocol : IProtocol
```

- `sealed` — not for inheritance beyond the generator partial.
- `partial` — required for Source Generator integration.
- `IProtocol` — implement `Parse`. Prefer `parentField.CallProtocol` for child dispatch.

### Member Ordering

```
1. Structural constants           (HeaderSize, MinPayloadSize, ...)
2. Table Key Constants            (public const ulong)
3. Table Name Constants           (public const string)
4. Index Group Constants          (private const string, only if used >1×)
5. Protocol-specific constants    (extension header types, magic bytes, ...)
6. Fields                         (FieldId members with [*Field] attributes)
7. Dispatch Tables                (ProtocolTableId with [ProtocolTableU64] / [UsesTable])
8. Runtime Settings               ([BoolSetting], [StringSetting], ...)
9. Cross-protocol field refs      (FieldId resolved in OnStartCustom)
10. Dispatch caches               (sparse/dense arrays, pre-allocated populator)
11. RegisterFieldsCustom / OnStartCustom / OnShutdownCustom
12. Lazy Populator method(s)
13. Parse method
14. Dispatch helper method(s)
15. Utility methods
```

### Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Protocol class | `{Name}Protocol` | `UdpProtocol`, `VlanProtocol` |
| Header struct | `{Name}Header` | `UdpHeader`, `IPv4Header` |
| Table key constants | `public const ulong {Description}Key` | `IpProtoKey`, `EtherTypeKey` |
| Table key (multi) | `public const ulong {Description}Key{Variant}` | `EtherTypeKey8021Q`, `EtherTypeKeyQinQ` |
| Table name constants | `public const string {Desc}TableName` | `PortTableName`, `EtherTypeTableName` |
| Index group constants | `private const string {Name}IndexGroup` | `UdpIndexGroup`, `EthIndexGroup` |
| FieldId fields | `private FieldId _{PascalName}FieldId` | `_SrcPortFieldId`, `_DstFieldId` |
| ProtocolTableId fields | `private ProtocolTableId _{Name}TableId` | `_PortTableId`, `_EtherTypeTableId` |
| Populator field | `private LazyPopulator _Populator` | `_Populator` |
| Dispatch cache | `private ... _{Name}Cache` | `_EtherTypeSparseCache`, `_IpProtoInstanceCache` |
| Populator method | `Populate{Name}Fields` | `PopulateUdpFields`, `PopulateIPv4Fields` |
| Dispatch method | `Dispatch{TableName}` | `DispatchEtherType`, `DispatchIpProtocol` |

### XML Documentation

Every protocol class must have a `<summary>` with a field tree example:

```csharp
/// <summary>
/// User Datagram Protocol (RFC 768) parser with checksum validation.
/// <para>Field tree structure:</para>
/// <code>
/// udp: User Datagram Protocol, Src Port: 12345, Dst Port: 53
/// ├── udp.srcport: 12345
/// ├── udp.dstport: 53
/// ├── udp.length: 30
/// ├── udp.checksum: 0xabcd
/// ├── udp.checksum.status: [Good] / [Bad]  [optional]
/// └── udp.payload: (22 bytes)              [optional]
/// </code>
/// </summary>
```

---

## 4. Source Generator Attributes

### Protocol Definition

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[Protocol("name", "UI Name")]` | Class | Defines protocol identity, generates `ProtocolName` constant |
| `[RegisterAtTable(table, key)]` | Class | Registers at a u64 dispatch table |
| `[RegisterAtStringTable(table, key)]` | Class | Registers at a string dispatch table |
| `[RegisterAtBoolTable(table, key)]` | Class | Registers at a bool dispatch table |
| `[RegisterAtAnyTable(table)]` | Class | Registers as catch-all in a table |

### Table Declaration & Reference

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[ProtocolTableU64(name, uiName)]` | `ProtocolTableId` field | Declares a new u64 dispatch table |
| `[UsesTable(tableName)]` | `ProtocolTableId` field | References another protocol's table (deferred resolution) |

### Runtime Settings

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[BoolSetting(name, uiName, group)]` | `bool` field | Runtime toggle (e.g., checksum verification) |
| `[StringSetting(name, uiName, group)]` | `string` field | Runtime text setting |
| `[F64Setting(name, uiName, group)]` | `double` field | Runtime numeric setting with Min/Max |

Settings are loaded automatically in `OnStart()` from the stack's setting registry.
The `Default` property sets the initial value.

---

## 5. Field Attributes Reference

Every field in the protocol tree is declared with an attribute on a `FieldId` field:

| Attribute | `FieldType` | Typical use | Example |
|-----------|-------------|-------------|---------|
| `[U64Field]` | `U64` | Ports, lengths, counters, enums | `[U64Field("udp.srcport", "Source Port")]` |
| `[I64Field]` | `I64` | Signed numeric fields | `[I64Field("tcp.window_scale", "Window Scale")]` |
| `[F64Field]` | `F64` | Floating-point fields | `[F64Field("frame.time_delta", "Delta Time")]` |
| `[BoolField]` | `Bool` | Single-bit flags | `[BoolField("ip.flags.df", "Don't Fragment")]` |
| `[MacField]` | `MacAddress` | 48-bit MAC addresses | `[MacField("eth.dst", "Destination")]` |
| `[IPv4Field]` | `IPv4Address` | IPv4 addresses | `[IPv4Field("ip.src", "Source Address")]` |
| `[IPv6Field]` | `IPv6Address` | IPv6 addresses | `[IPv6Field("ipv6.src", "Source")]` |
| `[BytesField]` | `Bytes` | Raw byte data, payloads | `[BytesField("udp.payload", "Payload")]` |
| `[StringField]` | `Str` | Text fields, status messages | `[StringField("udp.checksum.status", "Status")]` |
| `[TimestampField]` | `Timestamp` | Nanosecond-precision timestamps | `[TimestampField("frame.time", "Arrival Time")]` |
| `[NoneField]` | `None` | Grouping node (no intrinsic value) | `[NoneField("frame", "Frame")]` |

**Common properties on all field attributes:**

| Property | Type | Purpose |
|----------|------|---------|
| `IndexGroup` | `string` | **Required.** Groups fields for index bitmap optimization (see §13) |
| `Description` | `string?` | Optional long description for tooltips |

### Protocol Container Field

Every protocol must declare a **container** field — typically a `BytesField` that
stores the header bytes for lazy re-parsing:

```csharp
[BytesField("udp", "UDP", IndexGroup = UdpIndexGroup)]
private FieldId _ProtocolFieldId;
```

This field serves as the root node for all protocol-specific sub-fields in the tree.

---

## 6. Constants — What to Define and What Not

### Define Named Constants For:

| Category | Visibility | Example |
|----------|-----------|---------|
| **Table names** | `public const string` | `public const string PortTableName = "udp.port";` |
| **Table key values** | `public const ulong` | `public const ulong IpProtoKey = 17;` |
| **Index groups** (used >1×) | `private const string` | `private const string UdpIndexGroup = "udp";` |
| **Structural sizes** | `private const int` | `private const int HeaderSize = 8;` |
| **Protocol-specific magic numbers** | `private const` | `private const byte ExpectedVersion = 4;` |

### Do NOT Define Constants For:

| Category | Use instead | Rationale |
|----------|-------------|-----------|
| Field names | Inline string in attribute | Used exactly once, no cross-reference |
| Protocol names | Auto-generated `ProtocolName` | Source Generator provides it |
| UI display names | Inline string in attribute | Used exactly once |
| Index groups (used only once) | Inline string literal | No benefit to extracting |

**Rationale:** Table names and key values are **cross-protocol contracts** — they
must stay in sync across files. Field names are private, used in exactly one
attribute, and extracting them into constants adds indirection without benefit.

---

## 7. Cross-Protocol References

### Table Ownership

The protocol that **creates** a dispatch table owns its name constant:

```csharp
// EthernetProtocol OWNS the table → defines the name
public const string EtherTypeTableName = "eth.type";

[ProtocolTableU64(EtherTypeTableName, "EtherType")]
private ProtocolTableId _EtherTypeTableId;
```

### Registering at Another Protocol's Table

Protocols that dispatch through another protocol's table use the owner's constant:

```csharp
// IPv4 registers at Ethernet's EtherType table
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey)]
public sealed partial class IPv4Protocol : IProtocol
{
    public const ulong EtherTypeKey = 0x0800;
}

// VLAN registers at two keys
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKey8021Q)]
[RegisterAtTable(EthernetProtocol.EtherTypeTableName, EtherTypeKeyQinQ)]
public sealed partial class VlanProtocol : IProtocol
{
    public const ulong EtherTypeKey8021Q = 0x8100;
    public const ulong EtherTypeKeyQinQ = 0x88A8;
}
```

### Reusing Another Protocol's Table

When a protocol needs to dispatch using a table it didn't create (e.g., IPv6
dispatches using IPv4's `ip.proto` table), use `[UsesTable]`:

```csharp
// IPv6 REUSES IPv4's dispatch table (deferred resolution)
[UsesTable(IPv4Protocol.IpProtoTableName)]
private ProtocolTableId _IpProtoTableId;
```

The Source Generator calls `builder.WhenProtocolTableRegistered(...)` which resolves
the table ID after all protocols have registered. This works regardless of registration
order.

### Reading Fields From Other Protocols

When a downstream protocol needs values from an upstream protocol (e.g., UDP
reading IP addresses for checksum validation), resolve field IDs in `OnStartCustom()`:

```csharp
private FieldId _IpSrcFieldId;
private FieldId _IpDstFieldId;

partial void OnStartCustom(Stack stack)
{
    _IpSrcFieldId = stack.GetFieldId("ip.src") ?? default;
    _IpDstFieldId = stack.GetFieldId("ip.dst") ?? default;
}
```

At parse time, read eagerly-appended fields from the packet tree:

```csharp
if (_IpSrcFieldId.IsValid
    && packet.TryGetFieldValue(_IpSrcFieldId, out FieldValue ipv4Src, materialize: false)
    && ipv4Src.Type == FieldType.IPv4Address)
{
    // Use ipv4Src — materialize: false keeps sibling lazy containers untouched
}
```

> **Important:** This only works if the upstream protocol **eagerly appends** the
> field (not deferred inside a lazy populator). See §9.5 for the eager-append pattern.
> Pass `materialize: false` on the parse hot path; use `materialize: true` only when
> the caller intentionally needs lazy field trees (exporters, assertions, full dumps).

### Thread-Local Address Caches

The sibling-walk approach above is a safe fallback, but it is **not the primary
pattern for hot-path cross-protocol data** such as IP addresses for checksum
validation. The production pattern is a **per-thread, per-packet cache** stored
in a `[ThreadStatic]` field on the *providing* protocol class.

**Why thread-local instead of sibling walk?**

The parse thread owns the stack exclusively (§2). A `[ThreadStatic]` field read
or write is a single load/store with no heap allocation, no lock, and no field-tree
traversal. It is the correct choice for data that is:
- Produced unconditionally by an upstream protocol for every packet, and
- Consumed by one or more immediate downstream protocols on the same thread.

**Pattern — provider side** (e.g., `IPv4Protocol`):

```csharp
#region Thread-Local Address Cache

/// <summary>
/// Per-thread cache for the current packet's IPv4 src/dst addresses.
/// Written by <see cref="Parse"/> before dispatching; consumed by TCP, UDP.
/// Null is the correct default for <see langword="[ThreadStatic]"/>.
/// </summary>
[ThreadStatic]
private static (int PacketId, IPv4Address Src, IPv4Address Dst)? _ThreadCache;

[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal static void SetCachedAddresses(PacketId packetId, IPv4Address src, IPv4Address dst)
    => _ThreadCache = (packetId.Value, src, dst);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal static bool TryGetCachedAddresses(PacketId packetId, out IPv4Address src, out IPv4Address dst)
{
    (int PacketId, IPv4Address Src, IPv4Address Dst)? c = _ThreadCache;
    if (c.HasValue && c.Value.PacketId == packetId.Value)
    {
        src = c.Value.Src;
        dst = c.Value.Dst;
        return true;
    }
    src = default;
    dst = default;
    return false;
}

#endregion
```

Key design decisions:
- The cache stores `(PacketId, Src, Dst)` — the `PacketId` guards against stale
  entries left from the previous packet on the same thread.
- `null` is the correct initial value for `[ThreadStatic]` fields (no explicit
  initialization needed; `null` means "no data cached yet on this thread").
- The methods are `internal static` so only sibling protocols in the same assembly
  can read the cache — they are not part of the public API.

**Call site in `Parse()` — write before dispatch:**

```csharp
// Cache src/dst on the parse thread so downstream protocols can read them
// without a field-tree walk. Must be called BEFORE dispatching to the next protocol.
SetCachedAddresses(parentField.Packet.Id, src, dst);

// Now dispatch — TCP/UDP will read the cache during their Parse() call
ParseResult result = DispatchIpProtocol(in parentField, proto, payload, in context);
```

**Pattern — consumer side** (e.g., `UdpProtocol`):

When multiple parent protocols share the same dispatch table and one of them is
a tunnel (e.g., IPv6-in-IPv4), both caches may hold valid entries for the same
`PacketId`. Use `context.Dispatch.CallerProtocolId` to decide which cache to
read first — this prevents selecting the outer tunnel endpoints instead of the
inner transport endpoints:

```csharp
PacketId packetId = parentField.Packet.Id;

// CallerProtocolId tells us which IP version dispatched us in this invocation.
// In tunnel scenarios (e.g., 6in4) both caches hold valid data for the same
// PacketId — check the correct one first.
bool callerIsIpv4 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv4ProtocolId;
bool callerIsIpv6 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv6ProtocolId;

if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
{
    // Use IPv4 addresses
}
else if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
{
    // Use IPv6 addresses
}
else
{
    // Fallback: walk previous siblings for edge cases (custom stacks, unusual encapsulations)
    TryFindPreviousIpAddressesFallback(in parentField, out IPv4Address fbSrc4, out IPv4Address fbDst4, in context);
}
```

**Summary — when to use thread-local caches vs. sibling walk:**

| Scenario | Approach |
|----------|---------|
| Hot path, same-assembly protocols, unconditionally produced per packet | `[ThreadStatic]` cache (zero allocation, O(1)) |
| Edge cases, custom stacks, unusual encapsulations | Sibling walk via `IpAddressExtractor` fallback |
| Cross-assembly or external consumers | Eagerly-appended field + `packet.TryGetFieldValue(..., materialize: false)` (§7 Reading Fields) |

---

## 8. The Parse Method

Handwritten dissectors implement `IProtocol.Parse`. `Stack.CallProtocol` stamps
`ParseContext.SelfProtocolId` and then invokes the bound parse method. Prefer
`parentField.CallProtocol` so invalid ids return `ParseError`.

```csharp
// NEVER: bind a copied local function as ParseDelegate when you need SelfProtocolId
// ALWAYS: parentField.CallProtocol(id, data, in context) or TryCallNextProtocol*
```

### Signature

```csharp
public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
```

- `parentField` — the parent field to append to (sibling dispatch target).
- `data` — the raw packet bytes for this protocol layer.
- `context` — the parse context providing the protocol stack (dispatch tables, field registry) and dispatch state.
- Returns `ParseResult` — consumed byte count on success, or a `ParseError`.

### Mandatory Sequence

```
Step 1: Validate minimum data size
Step 2: Record protocol + index group presence
Step 3: Parse header eagerly (local variables from ReadOnlySpan<byte>)
Step 4: Validate header integrity (version, length bounds, ...)
Step 5: Compute payload bounds
Step 6: Record optional index groups conditionally
Step 7: Build summary LazyString
Step 8: Append lazy container field
Step 9: Eagerly append cross-protocol fields (if needed by downstream)
Step 10: Dispatch to next protocol (on parentField!)
Step 11: Append trailing fields (padding, trailer)
Step 12: Return consumed bytes
```

### Complete Example (UDP)

```csharp
public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
{
    // ── Step 1: Validate minimum data size ──────────────────────────
    if (data.Length < UdpHeader.HeaderSize)
    {
        return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
    }

    // ── Step 2: Record presence ─────────────────────────────────────
    context.RecordProtocolPresence(_ProtocolId);
    context.RecordGroupPresence(_UdpGroupId);

    // ── Step 3: Parse header eagerly ────────────────────────────────
    ReadOnlySpan<byte> span = data.Span;
    if (!UdpHeader.TryParse(span, out UdpHeader header, out _))
    {
        return ParseError.InsufficientDataWithInfo(ProtocolName, UdpHeader.HeaderSize, (ulong)data.Length);
    }

    ushort srcPort = header.SrcPort.Value;
    ushort dstPort = header.DstPort.Value;
    ushort length = header.Length.Value;

    // ── Step 5: Compute payload bounds ──────────────────────────────
    int payloadLen = Math.Max(0, Math.Min(length - UdpHeader.HeaderSize, data.Length - UdpHeader.HeaderSize));
    ReadOnlyMemory<byte> payloadData = payloadLen > 0
        ? data.Slice(UdpHeader.HeaderSize, payloadLen)
        : ReadOnlyMemory<byte>.Empty;

    // ── Step 6: Record optional index groups ────────────────────────
    bool checksumVerified = _VerifyChecksum && header.Checksum.Value != 0;
    if (checksumVerified)
    {
        context.RecordGroupPresence(_UdpChecksumStatusGroupId);
    }
    if (payloadLen > 0)
    {
        context.RecordGroupPresence(_UdpPayloadGroupId);
    }

    // ── Step 7: Build summary ───────────────────────────────────────
    // ZA.Lazy captures only value-type args — no per-packet closure allocation
    LazyString summary = ZA.Lazy(
        "User Datagram Protocol, Src Port: ", srcPort, ", Dst Port: ", dstPort);

    // ── Step 8: Set packet info (transport-layer display) ───────────
    parentField.SetPacketInfo(ZA.Lazy("Src Port: ", srcPort, ", Dst Port: ", dstPort));

    // ── Step 9: Append lazy container ───────────────────────────────
    // Store the full UDP datagram so the populator can re-parse without captured state
    FieldValue containerValue = FieldValue.NewBytes(data)
        .WithCustomRepresentation(new LazyString("8 bytes"));
    parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

    // ── Step 10: Dispatch to next protocol (on parentField!) ────────
    if (payloadLen > 0)
    {
        ushort lowPort = Math.Min(srcPort, dstPort);
        ushort highPort = Math.Max(srcPort, dstPort);

        ParseResult result = parentField.TryCallNextProtocolU64(
            _PortTableId, lowPort, payloadData, in context);
        if (result.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        if (!result.TryGetConsumed(out _) && lowPort != highPort)
        {
            ParseResult highResult = parentField.TryCallNextProtocolU64(
                _PortTableId, highPort, payloadData, in context);
            if (highResult.TryPropagateError(out ParseResult highError))
            {
                return highError;
            }
        }
    }

    // ── Step 12: Return consumed bytes ──────────────────────────────
    return Math.Min(length, data.Length);
}
```

### Critical Rules

1. **Dispatch on `parentField`** — sub-protocols are siblings, not children.
2. **Append lazy container before dispatch** — sub-protocols may read parent fields.
3. **All `context.RecordGroupPresence()` calls happen eagerly in Parse()** — never inside
   a lazy populator.
4. **Guard against negative payload lengths** with `Math.Max(0, ...)`.
5. **Propagate dispatch errors** — call `TryPropagateError` after every
   `TryCallNextProtocol*()` call and `return` the `out` result when it is
   `true`. After a false `TryPropagateError`, a false `TryGetConsumed` is
   exactly `NotDispatched` (try another key or stop). Ok(0) returns `true`
   from `TryGetConsumed` with `consumed == 0` and must not be treated as a miss.

### Leftover bytes (`packet.unparsed_data`)

When the **frame** protocol returns success with `consumed < frame.Length`,
`PacketProtocol` appends `packet.unparsed_data` (Bytes) under the packet
container and records the optional index group `packet.unparsed`. Inner
protocols do **not** insert a leftover `data` PDU. Ethernet uses the child’s
consumed count to append padding/trailer and then returns `data.Length`, so a
typical Ethernet/SLL stack never produces `packet.unparsed_data`.

**Author rules:**

1. Return the bytes this PDU actually used, not always `data.Length`, when the
   PDU length is known and shorter than the buffer.
2. Do **not** wrap every `TryCallNextProtocol*` with leftover handling. Link-layer
   padding and trailers stay with the frame protocol
   (`EthernetProtocol._AppendPaddingAndTrailer`).
3. Tails **inside** a fully consumed frame (for example UDP payload after a
   signal message that returned `RequiredByteLength`) are not `packet.unparsed_data`
   — they are not at the end of the capture buffer. Store only the bytes the
   PDU needs; the rest of that payload is not shown as a sibling protocol.

`DataProtocol` (`Name = "data"`) remains a normal dissector for registered
payloads (HTTP content-type, WebSocket, …). It is not the leftover fallback.

---

## 9. Lazy Field Materialization

### 9.1 Why Lazy?

Lazy materialization provides a ~3× throughput improvement on the parse hot path.
Instead of building the complete field tree for every packet, `Parse()` records
only the minimum information (index presence, dispatch keys) and defers the
expensive field tree construction to a `LazyPopulator` that runs **only when
someone reads the fields** (UI, filter, exporter).

### 9.2 The Pre-Allocated Populator Pattern

**Inline lambda closures per packet are prohibited.** Every lazy populator must be
a **pre-allocated delegate** created once in `OnStartCustom()`:

```csharp
// The delegate field — lives on the protocol instance
private LazyPopulator _Populator = null!;

partial void OnStartCustom(Stack stack)
{
    // Allocate once — captures only 'this' (the singleton protocol instance)
    _ = PopulateUdpFields(in container);
}
```

This eliminates the per-packet closure allocation that a lambda would generate.

### 9.3 How Data Flows to the Populator

The populator re-parses header data from the `FieldValue` stored in the container field.
At `Parse()` time, store the raw header bytes (or the entire datagram) as a `BytesField`:

```csharp
// In Parse(): Store header bytes in the container's value
FieldValue containerValue = FieldValue.NewBytes(data[..headerLen])
    .WithCustomRepresentation(new LazyString("20 bytes"));
parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);
```

In the populator method, re-read and re-parse from the stored bytes:

```csharp
private ParseResult PopulateIPv4Fields(in LazyField container)
{
    // Re-read the header bytes stored in the container's field value
    ReadOnlyMemory<byte> headerBytes = container.Value.Data.AsBytes();
    if (!IPv4Header.TryParse(headerBytes.Span, out IPv4Header header, out _))
    {
        return ParseError.InvalidData(ProtocolName, "Failed to parse IPv4 header");
    }

    // Append all child fields from the re-parsed header
    container.Append(_VersionFieldId, FieldValue.NewU64(header.Version));
    // ... more fields ...

    return 0;  // Success (return value ignored for populators)
}
```

### 9.4 LazyPopulator Delegate

```csharp
public delegate ParseResult LazyPopulator(in LazyField parentField);
```

- Called **exactly once** on first access to the container's children.
- **Must NOT** call `TryCallNextProtocol*()` — dispatch belongs in `Parse()`.
- **Must NOT** call `context.RecordGroupPresence()` — index recording belongs in `Parse()`.
- Returns `ParseResult` — errors propagate to the caller that triggered materialization.

### 9.5 Eager Append for Cross-Protocol Fields

Some fields must be accessible by downstream protocols **without triggering lazy
materialization**. For example, IPv4 eagerly appends `ip.src` and `ip.dst` so
that UDP/TCP can read them for checksum validation:

```csharp
// In Parse(): Append lazy container ...
MutField protoField = parentField.AppendLazyWithCustomText(
    _ProtocolFieldId, headerValue, summary, _Populator);

// ... then eagerly append specific fields AS CHILDREN of the lazy container
protoField.Append(_SrcFieldId, FieldValue.NewIPv4(src), in context);
protoField.Append(_DstFieldId, FieldValue.NewIPv4(dst), in context);
```

These eager fields coexist with the lazy populator. When the container is later
materialized, the populator appends the remaining fields — the eagerly-appended
fields remain in place.

> **Rule of thumb:** Eagerly append only fields that downstream protocols need
> for their own parsing (addresses for checksum, protocol numbers for sub-dispatch).
> Everything else stays inside the lazy populator.

### 9.6 When NOT to Use Lazy Fields

Lazy materialization is **not worth the complexity** when:

- The protocol is a **leaf** (no sub-dispatch) with very few fields (e.g., ARP with 4 fields).
- The fields are needed **eagerly by every packet** anyway (e.g., addresses for checksums).
- The populator would be trivial (just appending 2-3 fields).

In these cases, use the direct `Append()` / `AppendWithCustomText()` methods on
`parentField` without a lazy populator. The protocol container is still created
but populated immediately.

### 9.7 Summary vs. Populator: Captured Data

| Concern | Technique | What it captures | Allocation |
|---------|-----------|-----------------|------------|
| **Summary text** | `ZA.Lazy(...)` | Small set of display values (ports, addresses) | One display-class per packet |
| **Populator** | Pre-allocated `_Populator` delegate | Only `this` — re-parses from stored bytes | Zero per packet |
| **Container value** | `FieldValue.NewBytes(headerSlice)` | Raw header bytes (`ReadOnlyMemory<byte>`) | Zero (slice of existing buffer) |

The summary closure is the **only** per-packet allocation. Minimize what it captures
— prefer small value types (ushort, byte, IPv4Address) over full header structs.

**Ethernet example** — captures only the 14-byte header slice to minimize display class size:

```csharp
ReadOnlyMemory<byte> hdrBytes = data[..HeaderSize];
LazyString summary = LazyString.FormatLazy(() =>
{
    ReadOnlySpan<byte> s = hdrBytes.Span;
    MacAddress lazySrc = MacAddress.FromBytes(s[6..12]);
    MacAddress lazyDst = MacAddress.FromBytes(s[..6]);
    ushort lazyTypeOrLen = BinaryPrimitives.ReadUInt16BigEndian(s[12..14]);
    return lazyTypeOrLen >= MinEtherType
        ? ZA.String("Ethernet II, Src: ", lazySrc, ", Dst: ", lazyDst)
        : ZA.String("IEEE 802.3, Src: ", lazySrc, ", Dst: ", lazyDst);
});
```

### 9.8 Pre-Computed Values Pattern (Parse → Populator)

Sometimes the lazy populator needs a derived value (e.g., a pre-computed
checksum pseudo-header sum) that would require an expensive operation to
re-derive inside the populator. The pattern is:

1. **Eagerly compute and append** an internal helper field during `Parse()` —
   just before the lazy container.
2. **Read it back** inside the populator via a short sibling walk.

This keeps the populator allocation-free and avoids re-computing or capturing
state.

**Step 1 — Eager append of internal helper field in `Parse()`:**

```csharp
// Compute the pseudo-header checksum sum eagerly so the populator can read it
// without needing to walk the entire field tree or capture IP addresses.
ulong pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
    src.RawValue, dst.RawValue, ProtocolNumber, length);
parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(pseudoSum), in context);

// Append the lazy container immediately after — the helper field is now 1 sibling back.
FieldValue containerValue = FieldValue.NewBytes(data).WithCustomRepresentation(summary);
parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);
```

**Step 2 — Read back in the populator via MutField navigation:**

```csharp
private ParseResult PopulateMyProtocolFields(in MutField container)
{
    // Walk previous siblings with MutField APIs (no AsField conversion required).
    // Use materialize: false so sibling lazy containers are not populated.
    MutField current = container;
    while (current.TryGetPrev(out current))
    {
        // Read FieldId / Value of eagerly-appended helper fields only.
    }

    // Append remaining fields ...
    container.Append(_MyFieldId, FieldValue.NewU64(value));
    return 0;
}
```

**Tree navigation API (Field and MutField are parallel; MutField returns `MutField`):**

| Method | Materialize arg? | Description |
|--------|------------------|-------------|
| `IsValid` | — | Whether the cursor/reference is usable |
| `IsRoot` | — | Whether this is the packet root |
| `TryGetParent(out …)` | no | Parent field; false on root |
| `TryGetPrev(out …)` / `TryGetNext(out …)` | no | Previous / next sibling |
| `TryGetFirstChild(out …, bool materialize)` | **required** | First child; `false` skips lazy population |
| `TryGetLastChild(out …, bool materialize)` | **required** | Last child; `false` skips lazy population |
| `HasChildren(bool materialize)` | **required** | Whether direct children exist |
| `ChildCount(bool materialize)` | **required** | Number of direct children |
| `Children(bool materialize)` | **required** | Direct children enumerator |
| `Descendants(bool materialize)` | **required** | DFS pre-order descendants |
| `mutField.AsField()` | — | Read-only `Field` snapshot (optional) |

> **Rules:**
> - Read accessors and tree navigation are equivalent on `Field` and `MutField`.
> - Only `MutField` may mutate the tree (`Append`/`Prepend`/`InsertAfter`, lazy registration,
>   value/custom-text setters, packet-info helpers, protocol dispatch).
> - The populator **must not** trigger lazy materialization of sibling containers —
>   pass `materialize: false` and only read `FieldId` / `Value` of eagerly-appended fields.
> - The walk length must be bounded (e.g., `maxWalk = 3`) — helper fields are
>   always placed immediately before the container, so longer walks indicate a bug.
> - `context.RecordGroupPresence()` must NOT be called inside the populator —
>   all presence recording belongs in `Parse()`.
> - There are **no default values** for `materialize` — callers must choose explicitly.

---

## 10. Protocol Dispatch Tables

### 10.1 Table Types

| Table Type | Attribute | Key Type | Typical Use |
|------------|-----------|----------|-------------|
| U64 | `[ProtocolTableU64]` | `ulong` | EtherType, IP protocol, port numbers |
| String | `[ProtocolTableString]` | `string` | Content types, URI schemes |
| Bytes | `[ProtocolTableBytes]` | `BytesKey` | Magic byte sequences |
| Bool | `[ProtocolTableBool]` | `bool` | Present/absent detection |
| Any | `[ProtocolTableAny]` | — | Catch-all fallback |

U64 tables are the most common. Other types exist for application-layer protocols.

### 10.2 Declaring a Dispatch Table

The protocol that **owns** the table declares it with `[ProtocolTableU64]`:

```csharp
public const string PortTableName = "udp.port";

[ProtocolTableU64(PortTableName, "UDP Port")]
private ProtocolTableId _PortTableId;
```

### 10.3 Dispatch Methods

Available on `MutField`:

```csharp
// Most common — u64 key dispatch
parentField.TryCallNextProtocolU64(tableId, key, data, in context)

// String key dispatch
parentField.TryCallNextProtocolString(tableId, key, data, in context)

// Bytes key dispatch
parentField.TryCallNextProtocolBytes(tableId, key, data, in context)

// Bool key dispatch
parentField.TryCallNextProtocolBool(tableId, key, data, in context)

// Catch-all dispatch (any registered protocol gets called)
parentField.TryCallNextProtocolAny(tableId, data, in context)

// Direct protocol call by ID
parentField.CallProtocol(protocolId, data, in context)
```

All return `ParseResult`. Consume with exactly two methods:
- **`TryPropagateError`**: `true` ⇔ Error — `return` the `out` result immediately.
- **`TryGetConsumed`**: `true` ⇔ Ok (including Ok(0)); `false` after a prior
  false `TryPropagateError` ⇔ `NotDispatched`, and `consumed` is then 0.

### 10.4 Multi-Protocol Keys

When multiple protocols register at the same key, the stack creates a `packet.choice`
container labelled `"Choice: <table>: <key>"` (e.g. `"Choice: frame.link_type: 227"`).
All matching protocols are called as children of that container. The maximum consumed bytes is returned.
Individual alternative errors are **not fatal** — only errors from the overall
dispatch fail the parse.

### 10.5 Multi-Attempt Dispatch (Bidirectional Port Matching)

Transport protocols (UDP, TCP) use bidirectional port matching — try the lower port
first, then the higher port if no match:

```csharp
ushort lowPort = Math.Min(srcPort, dstPort);
ushort highPort = Math.Max(srcPort, dstPort);

ParseResult result = parentField.TryCallNextProtocolU64(_PortTableId, lowPort, payload, in context);
if (result.TryPropagateError(out ParseResult error)) { return error; }

if (!result.TryGetConsumed(out _) && lowPort != highPort)
{
    ParseResult highResult = parentField.TryCallNextProtocolU64(_PortTableId, highPort, payload, in context);
    if (highResult.TryPropagateError(out ParseResult highError)) { return highError; }
}
```

### 10.6 Identifying the Caller and Self from the Context

Every protocol receives two identity properties on `context`:

| Property | Type | Value |
|---|---|---|
| `context.SelfProtocolId` | `ProtocolId` | The ID of **this** protocol — set by `Stack.CallProtocol` via `ParseContext.WithSelfProtocol`. A raw `Parse` / cached `ParseDelegate` does not stamp it. |
| `context.Dispatch.CallerProtocolId` | `ProtocolId` | The ID of the **parent** protocol that triggered the table dispatch. Only meaningful when `context.Dispatch.HasDispatch == true`. |

**When is `CallerProtocolId` useful?**

Multiple parent protocols can share the same dispatch table. For example, both
`IPv4Protocol` and `IPv6Protocol` register sub-protocols in the `ip.proto` table
(TCP, UDP, ICMPv4/v6, etc.). TCP's `Parse()` needs to distinguish whether it was
dispatched from IPv4 or IPv6 in order to build the correct pseudo-header for
checksum validation:

```csharp
public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
{
    // ...
    ProtocolId caller = context.Dispatch.CallerProtocolId;
    if (caller == _Ipv4ProtocolId)
    {
        // compute IPv4 pseudo-header
    }
    else if (caller == _Ipv6ProtocolId)
    {
        // compute IPv6 pseudo-header
    }
    else
    {
        // Fallback: walk previous siblings via IpAddressExtractor
    }
}
```

**Important caveats:**
- `CallerProtocolId` is only valid when `context.Dispatch.HasDispatch == true`.
  When a protocol is invoked directly via `CallProtocol()` or as the root packet
  protocol, `HasDispatch` is `false` and `CallerProtocolId` is undefined.
- `context.SelfProtocolId` equals `default(ProtocolId)` (Value=0, `IsValid=true`)
  for `default(ParseContext)` and inside lazy populators — since `ParseContext` is a
  `readonly ref struct`, it cannot be captured in closures. Do not use `SelfProtocolId`
  inside lazy populator lambdas.
- Both IDs are set automatically — protocols never need to pass their own ID explicitly.

### 10.7 Reading the Dispatch Key from Context

Beyond the caller identity, `context.Dispatch` also exposes the **key** that
matched this protocol invocation. This lets a protocol select internal
configuration (e.g., a PDU definition by CAN ID) without re-parsing the
header or walking the field tree.

| Member | Returns | Description |
|--------|---------|-------------|
| `context.Dispatch.HasDispatch` | `bool` | `true` when invoked via a table lookup |
| `context.Dispatch.Kind` | `DispatchKeyKind` | Key type: `U64`, `String`, `Bytes`, `Bool`, `Any`, `None` |
| `context.Dispatch.TableId` | `ProtocolTableId` | Which dispatch table triggered this call |
| `context.Dispatch.TryGetU64(out ulong key)` | `bool` | Reads the key when `Kind == U64` |
| `context.Dispatch.TryGetString(out string? key)` | `bool` | Reads the key when `Kind == String` |
| `context.Dispatch.TryGetBytes(out BytesKey key)` | `bool` | Reads the key when `Kind == Bytes` |
| `context.Dispatch.TryGetBool(out bool key)` | `bool` | Reads the key when `Kind == Bool` |

**Example — config-driven signal messages (`SignalMessageRegistration`)**:

There is no meta loader protocol. `SignalMessageRegistration.Register(builder)` reads
JSON (`signal_message.config_file`), compiles each message, and registers one
`SignalMessageProtocol` instance per successful message. Each instance is wired into
parent tables (`can.id`, `lin.id`, `flexray.id`, `pdu_transport.id`, `udp.port`, …)
via `dispatch_bindings`. Parse needs **no dispatch-key lookup** — the table already
selected the correct protocol instance.

See schema: `NetworkInspector.Protocols/Schemas/signal-message-config.schema.json`.
JSON may be loaded from a file setting (`signal_message.config_file`, may be empty)
or from a caller-owned stream (`SignalMessageRegistration.TryLoadConfig` /
`Register(builder, stream)`). Stream/object registration **adds** messages on top of
the settings/profile file. After `RegisterStandardProtocols`, `Register(builder, stream)`
registers only the extra messages (settings are already present). Load, compile, and
registration warnings are returned to the caller — they are not discarded.

**Best effort:** Signal Message shows as much as it can. Invalid or colliding **signals**
(and invalid mux selectors / mux groups) are skipped with a warning; siblings in the
same message still register. A **message** is skipped only when its protocol identity
(`name` / `ui_name`) or declared `byte_length` is unusable. Name collisions are
detected and never throw. A faulty signal does not take down the rest of that message.

**Compile vs runtime**

| JSON / compile | Runtime |
|----------------|---------|
| `byte_length` | Compile-only floor: must be `>= RequiredByteLength` of the **remaining** signals after per-signal skips (max exclusive end byte over static + mux + mux-group signals). Not stored on `CompiledSignalMessage`. |
| Signal `name` / `ui_name` | JSON values are the registered field name / UI name. The parser does not prefix `{message}.`. Optional protocol suffixes `.raw`, `.enum`, and mux `.value` are appended to that name. Invalid or duplicate names skip that signal. Config providers (e.g. the FrameBuilder test bridge) may write `{message}.{signal}` as the JSON `name`. |
| CustomText cache | Signal bit length `<= 12` precomputes `{ui_name}: {phys:F}[ {unit}] ({raw})[ [{enum}]]` for every raw value at registration (4096 strings at 12 bits). Wider signals format on materialize. |
| `byte_order` | Required (`little_endian` / `big_endian`). Null/whitespace skips that signal with a warning; later signals and messages still compile. |
| `mux_signal` / `mux_groups` | Invalid mux selector skips mux and groups, static signals remain. `mux_groups` without `mux_signal` are ignored with a warning. Duplicate or out-of-range `mux_value` skips that group. Selector bit length `<= 8` builds a dense `muxValue → SignalInfo[]` table (256 slots at 8 bits). Wider selectors keep a linear group scan. |

**Hot-path contract for message `Parse`:**

1. One length check: `data.Length >= RequiredByteLength`.
2. Record protocol/group presence; append **lazy** message container
   (`AppendLazyWithCustomText` + `FieldValue.NewBytes(data[..RequiredByteLength])`
   + container populator).
3. **Return `RequiredByteLength`** — not `data.Length`. Inner leftover is not
   turned into a sibling `data` protocol; frame-level tails become
   `packet.unparsed_data` (§8).
4. Inside the container populator: unchecked `ulong` extract per signal
   (`SignalMessageBits.ExtractRawUnchecked`; raw is always unsigned).
   Byte-aligned little-endian 8/16/32/64-bit signals use a single load
   (`BinaryPrimitives`); unaligned 64-bit LE uses two `ulong`s (low 8 bytes +
   9th byte), never `UInt128` and never `ulong << 64`.
5. Append each signal under the container: **`FieldValue` = physical F64**
   (`raw × factor + offset`). `FieldInfo.FieldType` is `F64` for the parent
   signal, `U64` for optional `.raw`, `String` for optional `.enum`.
   CustomText is `{ui_name}: {phys:F}[ {unit}] ({raw})[ [{enum}]]`
   (invariant culture). For bit length `<= 12` that string is looked up from
   a table built at registration; wider signals call `_BuildCustomText`.
   Enum name lookup on materialize runs only when CustomText is not cached
   and `signal.Enums.Kind != None`, or when a `.enum` child is registered.

---

### 10.8 Accessing the Stack and Index from Context

| Member | Type | Usage |
|--------|------|-------|
| `context.Stack` | `Stack?` | The protocol stack for this parse invocation. `null` only for `default(ParseContext)`. |
| `context.HasStack` | `bool` | `false` only for the default/empty context. |
| `context.HasIndex` | `bool` | `true` when a `PacketIndex` is attached (i.e., this parse is being indexed). Use to skip expensive index-only work during non-indexed reads. |

`context.Stack` is needed only in rare cases — for example, building a
`new ParseContext(container.Packet.Stack)` is not needed inside a lazy populator.
`LazyField.Append*()` methods do not require a `ParseContext`:

```csharp
private ParseResult PopulateMyFields(in LazyField container)
{
    container.Append(_MyFieldId, FieldValue.NewU64(value));
    return 0;
}
```

---

### 10.9 PDU Transport → Signal Message

Two hops. PDU Transport does **not** parse signals. Signal Message does **not** parse the PDU-Transport header. Each protocol binds to the table **above** it.

```
UDP  ──[udp.port ∈ pdu_transport.udp_dispatch_ports]──►  PDU Transport
                                                          │
                              [pdu_transport.id = dispatch_bindings.key]──►  Signal Message
```

Empty settings/profile is valid: PDU Transport still parses concatenated `[ID][Length][Payload]` tuples, but it never auto-binds on UDP (`udp_dispatch_ports` default empty), and Signal Message registers zero message protocols. Configure both hops or you get a raw `pdu_transport.payload` (or no PDU Transport at all).

UDP looks up `min(src,dst)` first, then `max` only if the first lookup did not consume. A well-known low port (for example `53`) therefore wins over a listed high destination. Listing a port is parser selection on `udp.port`, not a socket bind.

Host code equivalent (tests / custom stacks) is `SettingsManager.PreloadValue` **before** `RegisterStandardProtocols`:

```csharp
sm.PreloadValue(
    "pdu_transport.udp_dispatch_ports",
    SettingValue.U64Array([47290UL, 47291UL]));
```

#### Settings (profile group files)

`RegisterStandardProtocols` registers these settings. Persist them in the profile directory as **group files** (`{group}.json`). Paths in `*_config_file` are resolved relative to the settings storage path (the profile directory).

**`pdu_transport.json`** (settings group `pdu_transport`):

```json
{
  "pdu_transport.udp_dispatch_ports": [47290, 47291],
  "pdu_transport.id_field_size": 4,
  "pdu_transport.length_field_size": 4,
  "pdu_transport.config_file": "pdu-names.json"
}
```

| Setting | Default | Role |
|---------|---------|------|
| `pdu_transport.udp_dispatch_ports` | `[]` | JSON array of UDP ports (1–65535) that select PDU Transport. Empty = UDP never calls this parser. Elements outside 1–65535 are skipped with `SettingsLoadWarningKind.OutOfRange`; in-range ports still bind. Value **must be a JSON array**, not a single number. |
| `pdu_transport.id_field_size` | `4` | On-wire PDU ID width: `1`, `2`, or `4` (big-endian). Other values clamp to `4` with a warning. |
| `pdu_transport.length_field_size` | `4` | On-wire length width: `1`, `2`, or `4` (big-endian). Must match the capture. |
| `pdu_transport.config_file` | `""` | Optional JSON of PDU **display names** only. Empty = IDs still parse; `pdu_transport.name` is omitted. Does **not** register Signal Messages. |

**`signal_message.json`** (settings group `signal_message`):

```json
{
  "signal_message.config_file": "signal-messages.json",
  "signal_message.show_raw": false,
  "signal_message.show_enum": false,
  "signal_message.max_enum_values": 4096
}
```

| Setting | Default | Role |
|---------|---------|------|
| `signal_message.config_file` | `""` | JSON of message protocols + `dispatch_bindings`. Empty = no Signal Message dissectors. |
| `signal_message.show_raw` | `false` | Append `{signal}.raw` (U64) under each signal. |
| `signal_message.show_enum` | `false` | Append `{signal}.enum` when a `value_names` hit occurs. |
| `signal_message.max_enum_values` | `4096` | Cap on `value_names` entries per signal. |

#### External JSON (not the group files)

**`pdu-names.json`** — schema `Schemas/pdu-transport-config.schema.json`. Names only:

```json
{
  "pdus": [
    { "id": 32, "name": "BenchPdu" }
  ]
}
```

`id` must equal the on-wire PDU ID (same integer the Signal Message binding uses as `key`).

**`signal-messages.json`** — schema `Schemas/signal-message-config.schema.json`. This is the dispatch configuration:

```json
{
  "messages": [
    {
      "name": "fixture_message",
      "ui_name": "Fixture PDU",
      "byte_length": 4,
      "dispatch_bindings": [
        { "table": "pdu_transport.id", "key": 32 }
      ],
      "signals": [
        {
          "name": "fixture_message.EngineRpm",
          "ui_name": "Engine RPM",
          "start_bit": 0,
          "bit_length": 16,
          "byte_order": "little_endian",
          "factor": 0.25,
          "offset": 100.0
        }
      ]
    }
  ]
}
```

The binding **`table` must be `pdu_transport.id`** (not `udp.port`) when the payload sits inside PDU Transport. `key` is the PDU ID. The same message may list several bindings (CAN + PDU Transport, etc.).

#### Additional JSON by hand

Settings/profile files may be empty. Extra documents merge **on top**:

- `PduTransportRegistration.Register(builder, stream, warnings)` — extra PDU names (same ID overwrites the file).
- `SignalMessageRegistration.Register(builder, stream)` — extra messages. After `RegisterStandardProtocols` this does **not** reload the file; it only adds the stream.

Do **not** call `PduTransportRegistration.Register(builder, stream)` after `RegisterStandardProtocols`: that would register a second `pdu_transport` protocol. Put extra names in the file, or use the stream overload **instead of** the standard PDU registration.

#### Field tree (sibling dispatch)

PDU Transport and Signal Message both dispatch on `parentField`, same as Ethernet / IPv6 / UDP.

```
… ipv4 …
├── udp
│   ├── udp.srcport / udp.dstport / …
├── pdu_transport
│   └── pdu_transport.pdu
│       ├── pdu_transport.id
│       ├── pdu_transport.length
│       ├── pdu_transport.name          // from pdu-names.json when the ID hits
│       └── pdu_transport.payload       // only if no Signal Message consumed the payload
└── fixture_message                     // lazy container; sibling of pdu_transport
    ├── fixture_message.EngineRpm       // physical F64; children of the message, not of pdu
    └── fixture_message.Thr
```

Signals are **never** children of `pdu_transport` or `pdu_transport.pdu`. Optional `{signal}.raw` / `{signal}.enum` hang under the signal. Mux-group signals hang under the mux field.

#### Misconfiguration (silent miss, not a crash)

| Symptom | Cause |
|---------|--------|
| No `pdu_transport` in the tree | `pdu_transport.udp_dispatch_ports` is empty, no listed port matches, the value is not a JSON array (TypeMismatch keeps `[]`), leftover `pdu_transport.udp_dispatch_port` is ignored, or UDP already consumed `min(src,dst)` (for example source `53`). |
| `pdu_transport.payload` instead of the message | `dispatch_bindings.table` is not `pdu_transport.id`, or `key` ≠ on-wire PDU ID, or `id_field_size` / `length_field_size` do not match the capture (wrong ID read). |
| Message protocol missing from the stack | `signal_message.config_file` empty or JSON failed to load (inspect `RegisterStandardProtocols` warnings). |
| `pdu_transport.name` missing | `pdu_transport.config_file` empty or ID not listed. Parsing and dispatch still work. |

---

## 11. Heuristic Protocol Tables

### 11.1 Purpose

When no dispatch table key matches (e.g., unknown port), heuristic tables provide
a **content-based fallback**. Each registered heuristic parser inspects the payload
bytes and returns `true` if it recognizes the data.

### 11.2 IHeuristicParser Interface

```csharp
public interface IHeuristicParser
{
    bool Test(ReadOnlyMemory<byte> data);
    ProtocolId ProtocolId { get; }
    string Name { get; }
    string UiName { get; }
    string? Description => null;
}
```

### 11.3 Declaring a Heuristic Table

```csharp
// In StackBuilder registration:
HeuristicProtocolTableId heuristicTableId =
    builder.RegisterHeuristicProtocolTable(protocolId, "tcp.heuristic", "TCP Heuristic");
builder.RegisterHeuristicParser(heuristicTableId, new HttpHeuristicParser());
builder.RegisterHeuristicParser(heuristicTableId, new TlsHeuristicParser());
```

### 11.4 Dispatch with Heuristic Fallback

```csharp
// Try table-based dispatch first
ParseResult result = parentField.TryCallNextProtocolU64(_PortTableId, port, payload, in context);
if (result.TryPropagateError(out ParseResult error)) { return error; }

// If no table match, try heuristic fallback. Only a false TryGetConsumed after
// a false TryPropagateError is a miss — Ok(0) means a protocol ran and consumed
// zero bytes; do not fall through.
if (!result.TryGetConsumed(out _))
{
    result = parentField.TryCallHeuristicProtocol(_HeuristicTableId, payload, in context);
    if (result.TryPropagateError(out ParseResult heuristicError)) { return heuristicError; }
}
```

`TryCallHeuristicProtocol()` scans registered heuristic parsers and calls the
**first matching** protocol. The order of heuristic parsers determines priority.

### 11.5 Performance Consideration

Heuristic parsing inspects payload bytes — it is more expensive than key-based
dispatch. Use it only as a fallback after table-based dispatch fails:

```
1. Try port/key-based dispatch (O(1) with cache)
2. If no match → Try heuristic dispatch (O(n) with n=number of heuristic parsers)
3. If still no match → Leave payload undissected
```

---

## 12. Dispatch Cache Optimization

The generic `TryCallNextProtocolU64()` path involves table lookup, multi-protocol
handling, and virtual dispatch. For performance-critical protocols, build a
**dispatch cache** in `OnStartCustom()` to bypass the generic path entirely.

### 12.1 Dense Cache (Small domains: u8 → 256 entries)

For key domains that fit in a small array (8-bit IP protocol field = 256 entries):

```csharp
// ~2 kB array — fits in L2 cache
private ParseDelegate?[] _IpProtoDelegateCache = [];

partial void OnStartCustom(Stack stack)
{
    _IpProtoDelegateCache = stack.BuildU64DelegateCache(_IpProtoTableId, 256);
}
```

Dispatch is a **single array lookup** — O(1) with zero indirection:

```csharp
private ParseResult DispatchIpProtocol(
    in MutField parentField, byte protocol, ReadOnlyMemory<byte> payload, in ParseContext context)
{
    ParseDelegate? fastParse = _IpProtoDelegateCache.Length > 0 ? _IpProtoDelegateCache[protocol] : null;
    return fastParse is not null
        ? fastParse(in parentField, payload, in context)              // Direct delegate call
        : parentField.TryCallNextProtocolU64(_IpProtoTableId, protocol, payload, in context);  // Fallback
}
```

- **Non-null entry**: single protocol registered → direct delegate call.
- **Null entry**: zero or multiple protocols → fall back to full table dispatch.

### 12.2 Sparse Cache (Large domains with few entries: u16 → 4–6 entries)

For key domains where only a handful of values are registered (EtherType = 65 536
possible values, but only 4–6 active):

```csharp
// Tiny array — all entries fit in L1 D-cache
private (ulong Key, ParseDelegate Parse)[] _EtherTypeSparseDelegateCache = [];

partial void OnStartCustom(Stack stack)
{
    _EtherTypeSparseDelegateCache = stack.BuildU64SparseDelegateCache(_EtherTypeTableId);
}
```

Dispatch is a **linear scan** — faster than dictionary hash for 4–6 entries:

```csharp
private ParseResult DispatchEtherType(
    in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
{
    foreach ((ulong key, ParseDelegate parse) in _EtherTypeSparseDelegateCache)
    {
        if (key == etherType)
        {
            return parse(in parentField, payload, in context);        // Direct delegate call
        }
    }

    return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
}
```

### 12.3 Choosing a Cache Strategy

| Domain size | Registered entries | Strategy | Method |
|-------------|--------------------|----------|--------|
| ≤ 256 (u8) | Any | Dense array | `BuildU64DelegateCache(table, 256)` |
| ≤ 65 536 (u16) | 1–10 | Sparse array | `BuildU64SparseDelegateCache(table)` |
| Large / dynamic | Many | No cache | `TryCallNextProtocolU64()` directly |

---

## 13. Index Groups & PacketIndex

### 13.1 How the PacketIndex Works

The `PacketIndex` is a cross-packet structure that tracks **which fields are present
in which packets** using Roaring Bitmaps. This enables fast filtering:
"show me only packets that contain field `tcp.flags.syn`" becomes a bitmap intersection.

Without index groups, each of the ~84+ protocol fields would need its own bitmap.
With groups, fields that **always appear together** share **one** bitmap — reducing
memory and lookup overhead by ~10×.

### 13.2 Index Group Rules

1. **Always-present fields** share the protocol's **main group**.
   If the protocol is present, these fields are guaranteed to exist.

   ```csharp
   private const string UdpIndexGroup = "udp";

   [U64Field("udp.srcport", "Source Port", IndexGroup = UdpIndexGroup)]
   [U64Field("udp.dstport", "Destination Port", IndexGroup = UdpIndexGroup)]
   [U64Field("udp.length", "Length", IndexGroup = UdpIndexGroup)]
   [U64Field("udp.checksum", "Checksum", IndexGroup = UdpIndexGroup)]
   ```

2. **Optional / conditional fields** each get their **own group**.
   They may or may not appear depending on packet content.

   ```csharp
   [BytesField("udp.payload", "Payload", IndexGroup = "udp.payload")]
   [StringField("udp.checksum.status", "Checksum Status", IndexGroup = "udp.checksum.status")]
   ```

3. **Co-occurring optional fields** may share a group if they **always appear together**.

   ```csharp
   // Both always appear together → share one group
   private const string FcsIndexGroup = "eth.fcs";
   [U64Field("eth.fcs", "FCS", IndexGroup = FcsIndexGroup)]
   [StringField("eth.fcs.status", "FCS Status", IndexGroup = FcsIndexGroup)]
   ```

4. **Mutually exclusive fields** each need their **own group**.

   ```csharp
   // Ethernet II vs IEEE 802.3 — only one appears per packet
   [U64Field("eth.type", "Type", IndexGroup = "eth.type")]
   [U64Field("eth.len", "Length", IndexGroup = "eth.len")]
   ```

### 13.3 Recording Presence in Parse()

**All `context.RecordGroupPresence()` calls must happen eagerly in `Parse()`** — never
inside a lazy populator. The index is populated during the initial parse pass;
lazy populators run much later (if at all).

```csharp
public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
{
    // Always record
    context.RecordProtocolPresence(_ProtocolId);
    context.RecordGroupPresence(_UdpGroupId);

    // ... parse header ...

    // Conditionally record optional groups
    if (_VerifyChecksum && checksum != 0)
    {
        context.RecordGroupPresence(_UdpChecksumStatusGroupId);
    }
    if (payloadLen > 0)
    {
        context.RecordGroupPresence(_UdpPayloadGroupId);
    }
    // ...
}
```

### 13.4 Generated Index Group IDs

The Source Generator creates one `IndexGroupId` field per unique `IndexGroup` value:

```csharp
// Auto-generated from IndexGroup strings in field attributes:
private IndexGroupId _UdpGroupId;              // "udp"
private IndexGroupId _UdpPayloadGroupId;       // "udp.payload"
private IndexGroupId _UdpChecksumStatusGroupId;// "udp.checksum.status"

// Auto-generated in RegisterFields():
_UdpGroupId = builder.GetOrCreateIndexGroup("udp");
_UdpPayloadGroupId = builder.GetOrCreateIndexGroup("udp.payload");
_UdpChecksumStatusGroupId = builder.GetOrCreateIndexGroup("udp.checksum.status");
```

### 13.5 Impact

| Approach | Bitmaps for standard stack |
|----------|---------------------------|
| Per-field bitmaps | ~84 |
| With index groups | ~8–10 |

---

## 13a. Field Alias Groups (Any-Match Names)

Field alias groups are **metadata-only** registries that expose any-match semantic
names (e.g., `eth.addr`, `ip.addr`, `ipv6.addr`, `udp.port`, `tcp.port`) without
adding any extra field nodes to the parse tree. They replace the older pattern
of physically appending a duplicate field (`eth.addr` appended as a child of both
`eth.dst` and `eth.src`) which doubled allocations and confused enumeration.

### 13a.1 Core Rules (Mandatory)

- **No physical duplicate field is registered or appended** for any-match names.
  Do **not** declare `[MacField("eth.addr") private FieldId _AddrFieldId;` —
  this is the breaking change locked by Step 4 of the alias plan.
- **Alias names and canonical field names live in independent namespaces.**
  `stack.GetFieldId("eth.addr")` returns `null` by design. Always use
  `stack.GetFieldAliasGroupId("eth.addr")` to resolve alias names. The
  separation keeps indexing and per-packet field-lookup paths
  on canonical fields only.
- **Alias members may have mixed `FieldType` values.** The registry stores
  member `FieldId`s only; consumers query each member by its own type.

### 13a.2 Manual Registration Pattern

Alias groups are registered manually inside `RegisterFieldsCustom` (the partial
that the generator invokes after all attribute fields are registered, so member
`FieldId`s are populated):

```csharp
[Protocol("eth", "Ethernet II")]
public sealed partial class EthernetProtocol : IProtocol
{
    [MacField("eth.dst", "Destination", IndexGroup = EthIndexGroup)]
    private FieldId _DstFieldId;

    [MacField("eth.src", "Source", IndexGroup = EthIndexGroup)]
    private FieldId _SrcFieldId;

    // Holds the alias group ID returned from manual registration.
    private FieldAliasGroupId _AddrAliasGroupId;

    partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        // Members must already be registered by the time this is called.
        _AddrAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId, "eth.addr", null, [_DstFieldId, _SrcFieldId]);
    }
}
```

The same pattern applies to `ip.addr` (→ `{ ip.src, ip.dst }`), `ipv6.addr`,
`udp.port` (→ `{ udp.srcport, udp.dstport }`), `tcp.port`, and any future
any-match name. Note that the alias name `udp.port` is **independent** of the
protocol-table name `udp.port` (UDP demux) — alias, field, and table namespaces
are three separate registries.

### 13a.3 Consumer Pattern (Enumeration via Alias)

```csharp
FieldAliasGroupId? aliasId = stack.GetFieldAliasGroupId("eth.addr");
if (aliasId is { } id)
{
    FieldAliasGroupInfo info = stack.GetFieldAliasGroup(id)!;
    foreach (FieldId memberId in info.Members.Span)
    {
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        // materialize: true — consumer enumeration must see lazy member fields too
        while (packet.TryGetNextFieldValue(memberId, ref cookie, out FieldValue value, materialize: true))
        {
            // process value per member type
        }
    }
}
```

### 13a.4 Ethernet I/G + L/G Bits via CustomText

The legacy `eth.dst.ig`, `eth.dst.lg`, `eth.src.ig`, `eth.src.lg` bool fields
are removed. The I/G and L/G semantics are now exposed as **CustomText on
`eth.dst` and `eth.src`** via `EthernetProtocol.FormatMacAddressBits`:

| I/G   | L/G    | CustomText                          |
|-------|--------|-------------------------------------|
| false | false  | `Unicast, Globally Unique`          |
| false | true   | `Unicast, Locally Administered`     |
| true  | false  | `Multicast, Globally Unique`        |
| true  | true   | `Multicast, Locally Administered`   |

Tests assert this via `AssertDisplayText(stack, packet, "eth.dst", "…")`.

---

## 13b. Eager / Lazy Field Layout

Every in-scope protocol (`eth`, `ip`, `ipv6`, `udp`, `tcp`, `tls`, `dtls`)
splits its work between **eager** fields appended during `Parse()` (small,
summary-critical, or required by downstream protocols) and **lazy** fields
materialised by a single per-protocol populator when the protocol container
is first accessed. All decoded fields are direct children of the protocol
container — there is no nested `*.hdr` intermediate container.

### 13b.1 Required Layout

| Protocol | Eager (Parse) | Lazy populator children (direct under protocol container) |
|----------|---------------|------------------------------------------------------------|
| `eth`    | —             | `eth.dst`, `eth.src` (+ ig/lg CustomText), `eth.type` / `eth.len`, padding, … |
| `ip`     | `ip.src`, `ip.dst` (required by transport protocols) | version, hlen, dscp/ecn, flags, ttl, proto, checksum, options |
| `ipv6`   | `ipv6.src`, `ipv6.dst` (required by transport protocols) | version, tclass, flow, payload length, next header, hop limit, extension headers |
| `udp`    | —             | `udp.srcport`, `udp.dstport`, checksum (+status), length, payload |
| `tcp`    | —             | ports, checksum, seq/ack, hdr_len, flags (+ sub-flags), window, urgent ptr, options, len, payload |
| `tls`    | —             | per record: `tls.record` (content_type, version, length) followed by handshake/alert as siblings |
| `dtls`   | —             | per record: `dtls.record` (content_type, version, epoch, seq, length) followed by handshake as siblings |
| signal message (`SignalMessageProtocol`) | — | static signals (physical F64); mux container + `.value` + matching mux-group signals; optional `.raw` / `.enum` |

### 13b.2 Implementation Sketch

```csharp
// One populator delegate, captured once in OnStartCustom — zero per-packet allocation.
private LazyPopulator _Populator = null!;

partial void OnStartCustom(Stack stack) => _Populator = PopulateFoo;

private ParseResult PopulateFoo(in LazyField container)
{
    // Append all decoded fields directly under the protocol container.
    // For ip/ipv6: src/dst stay in Parse (eager); everything else goes here.
    container.Append(_VersionFieldId, FieldValue.NewU64(version));
    container.Append(_HlenFieldId, FieldValue.NewU64(hlen));
    // … remaining fields …
    return 0;
}
```

---

## 13c. Mandatory Eager Rules

The presence index is the **single** reliable filtering layer — there is no
value-cache fallback. A filter that asks "does this packet contain field
`tls.sni` / `dns.opt` / `http.host`?" must be answerable from the index alone,
without materialising the lazy descriptive field tree. The five rules below are
**binding** for every current and future protocol; they make that guarantee
hold by construction. They are enforced by
`EagerIndexGroupRegistrationTests` and the eager-dispatch tests.

### 13c.1 All sub-protocol dispatch is eager

**All dispatch to sub-protocols must occur in `Parse()`** — never inside a lazy
populator. A populator builds descriptive fields for *one* protocol; it must not
decide or invoke the next protocol. Dispatch governs which protocols are present
in a packet, and presence must be known the moment the packet is finalised, not
deferred until (or if) a container is later expanded.

### 13c.2 The index is finalised when the packet is finalised

A lazy populator must **never** mutate the index, record presence, or dispatch.
It only builds the field tree. This is enforced by the type system: a populator
receives a `LazyField` / `MutField` cursor with **no `ParseContext`**, so it has
no API surface to touch the index. Everything index-affecting happens during the
eager `Parse()` pass.

### 13c.3 Every emittable group is recorded eagerly — no false positives, no false negatives

For each packet, the index must record a group **if and only if** materialisation
would emit at least one field of that group. Two failure modes are equally
forbidden:

- **False negative** — a lazily-materialised field whose group was *not* recorded
  in `Parse()`. The filter would wrongly exclude a matching packet.
- **False positive** — a group recorded in `Parse()` for which no field is ever
  emitted. The filter would wrongly include a non-matching packet.

Unconditional groups are recorded directly. **Conditional and content-dependent
groups must be decided in `Parse()`**, even when the corresponding fields are
deferred to the populator. This frequently means `Parse()` must perform an eager
payload scan that **duplicates the populator's parsing logic**, with each eager
guard mirroring its populator's emission guard exactly. Duplicated evaluation is
the accepted price of the lazy model. For example, TLS records all twelve
`tls.*` groups by walking records/handshakes/extensions in `Parse()` with the
same length and bounds checks the populator uses; DNS walks the question and
resource-record sections to record `dns.opt`, `dns.ds`, `dns.rrsig`, `dns.nsec`,
`dns.dnskey`, etc.

A field that is declared but **never emitted** (dead field) must **not** have its
group recorded — doing so would be a false positive.

### 13c.4 Prefer eager fields; defer only with enough candidates

Append fields eagerly by default. Introduce a lazy populator **only** when a
protocol has **≥ 2 (ideally ≥ 3)** lazy-field candidates; for a single deferrable
field the populator's per-access overhead is not worth it — append eagerly and
omit the populator. (Recording the group eagerly per §13c.3 is required either
way.)

### 13c.5 Follow the approved eager/lazy cut

The authoritative eager/lazy split for the in-scope protocols is the table in
**§13b.1** and is binding:

- `ip` / `ipv6` keep **only** `*.src` and `*.dst` eager, because transport
  demux reads them during its own `Parse()`; every other field is lazy.
- `tcp.flags` (and all other TCP fields) are **lazy**.
- `eth`, `udp`, `tls`, `dtls` are **fully lazy** — no eager descriptive fields —
  while still recording their presence and applicable groups eagerly per §13c.3.

Any new protocol must document its cut as a row consistent with §13b.1 before
implementation.

---

## 14. String Handling & Display Text

### 14.1 Priority Order

String allocation is the primary source of GC pressure in protocol parsing.
Follow this strict priority order:

| Priority | Pattern | Allocation | When to use |
|----------|---------|------------|-------------|
| 1 | `DisplayTables.Get*(value)` | **Zero** | Value from precomputed static table |
| 2 | Literal string `"constant text"` | **Zero** | Well-known constant text |
| 3 | `$"text {var}"` in lazy populator | **Deferred** | Dynamic text inside a populator (runs only on access) |
| 4 | `ZA.Lazy(...)` | **One display-class** per packet | Summary text, packet info |
| 5 | `$"text {var}"` in Parse() | **One string** per packet | **Avoid** — only if no other option |

### 14.2 Precomputed Display Tables

`DisplayTables` in `Helpers/DisplayTables.cs` provides zero-allocation lookups
for fixed domains:

```csharp
// Dense array — entire u8/u16 domain precomputed
string dscpText = DisplayTables.GetDscpDisplayText(dscp);         // 64 entries, 6-bit
string ecnText = DisplayTables.GetEcnDisplayText(ecn);            // 4 entries, 2-bit
string protoText = DisplayTables.GetIpProtocolDisplayText(proto);  // 256 entries, 8-bit
string etherText = DisplayTables.GetEtherTypeDisplayText(type);    // 65536 entries, 16-bit
string pcpText = DisplayTables.GetVlanPriorityDisplayText(pcp);    // 8 entries, 3-bit

// Pre-formatted hex — avoids string.Format / interpolation
string hexU8 = DisplayTables.FormatHexU8(value);                   // 256 entries: "0x00".."0xff"
string hexU16 = DisplayTables.FormatHexU16(value);                 // 65536 entries: "0x0000".."0xffff"

// Precomputed structural text
string hdrLen = DisplayTables.GetHeaderLengthDisplayText(len);     // "20 bytes", "40 bytes", ...
```

**Adding new tables:** For any new domain field, add a table in `DisplayTables.cs`:
- Full-domain arrays (u8 = 256, u16 = 65536) for key identifier domains.
- Partial arrays for structural fields (header lengths 0–60).
- Named entries for well-known values; fill remaining with numeric string representation.

### 14.3 ZeroAlloc String Formatting (ZA.Lazy / ZA.String)

The `ZA.Lazy()` shorthand from the ZeroAlloc source generator creates a `LazyString`
that is evaluated once on first access. `ZA.String()` writes directly into a pooled
buffer using `ISpanFormattable.TryFormat()`, eliminating intermediate `ToString()` calls
and boxing:

```csharp
// ✅ ZA.Lazy — preferred form for simple inline expressions
LazyString summary = ZA.Lazy(
    "User Datagram Protocol, Src Port: ", srcPort, ", Dst Port: ", dstPort);
```

The Source Generator creates type-specific overloads for each combination of
argument types, so there is no boxing or virtual dispatch.

**Use `ZA.Lazy(...)` for:**
- Protocol summary text (displayed in the packet list / tree root).
- Packet info text (displayed in the info column).
- Any text containing runtime values that is computed per packet.

For complex multi-statement cases (conditionals, local variables), use
`LazyString.FormatLazy(() => { ... return ZA.String(...); })` instead.

### 14.4 LazyString

`LazyString` defers string evaluation until first access:

```csharp
// Static text — zero allocation
new LazyString("8 bytes")

// Deferred — evaluated once on first access, then cached via Interlocked.CompareExchange
ZA.Lazy("Options: (", len, " bytes)")

// Null — absent text
LazyString.Empty
```

### 14.5 AppendWithCustomText vs. Append

- `Append(fieldId, value)` — uses the default formatting for the field type.
- `AppendWithCustomText(fieldId, value, displayText)` — overrides the display text.

Use `AppendWithCustomText` when you need a human-readable label that differs from
the raw value representation (e.g., `"TCP (6)"` instead of just `"6"`).

### 14.6 WithCustomRepresentation

For container fields where the `FieldValue` stores raw bytes but the UI should
display a summary:

```csharp
FieldValue headerValue = FieldValue.NewBytes(data[..20])
    .WithCustomRepresentation(new LazyString("20 bytes"));
```

### 14.7 Anti-Patterns

```csharp
// ❌ BAD — string interpolation for a fixed domain (use DisplayTables)
$"0x{etherType:x4}"

// ❌ BAD — string.Format anywhere on the hot path
string.Format("Protocol: {0}", protocol)

// ❌ BAD — ToString() on numeric types in the hot path
checksum.ToString("x4")

// ❌ BAD — per-packet string allocation in Parse() for display text
string summary = $"UDP, Src: {srcPort}, Dst: {dstPort}";  // Allocates every packet

// ✅ GOOD — deferred with ZA.String (allocates only if accessed)
LazyString summary = LazyString.FormatLazy(() =>
    ZA.String("UDP, Src: ", srcPort, ", Dst: ", dstPort));
```

---

### 14.8 Precomputed Flags Formatters

Every protocol that has a flags container field **must** provide a dedicated
`*FlagsFormatter` class with a precomputed lookup table indexed by the packed
flag bits.  This eliminates all per-packet allocations for the common case of
displaying which flags are set.

#### Two display patterns

| Field type | Output format | Example |
|-----------|--------------|---------|
| `NoneField` (grouping container) | `[FLAG1, FLAG2]` | `[SYN, ACK]` |
| `U64Field` (flags register with numeric value) | `0xNN [FLAG1, FLAG2]` | `0x12 [SYN, ACK]` |

Both patterns use `[None]` when no flags are set.

#### Canonical reference implementation

`Tcp/TcpFlagsFormatter.cs` is the canonical example.  Follow its exact structure:

```csharp
// ✅ Canonical pattern — zero-allocation precomputed table
internal static class TcpFlagsFormatter
{
    private static readonly string[] FlagsTable = BuildFlagsTable();

    internal static string Format(ushort flags) => FlagsTable[flags & 0x1FF];

    private static string[] BuildFlagsTable()
    {
        string[] table = new string[512]; // 2^N entries, N = number of flag bits
        for (int i = 0; i < 512; i++)
        {
            table[i] = BuildFlagString((ushort)i);
        }
        return table;
    }

    private static string BuildFlagString(ushort flags)
    {
        if (flags == 0) return "[None]";

        // ... compute totalLen ...

        return string.Create(totalLen, flags, static (chars, f) =>
        {
            // Write "[FLAG1, FLAG2]" into chars without any allocation
        });
    }
}
```

#### Key design rules

1. **Static read-only table** — built once in the static initializer, never mutated.
2. **Direct array index** — use `FlagsTable[key]` without bounds checks in release
   (array size == full domain of the key).
3. **`string.Create` with a value-type state** — avoids boxing; pass a struct or
   primitive (e.g. `ushort flags`) as the `state` argument.
4. **`[None]` sentinel** — always return the pre-allocated literal `"[None]"` for
   the zero-flags case (no `string.Create` call).
5. **Compact key extraction** — when the flag bits are not contiguous (e.g. DNS),
   pack them into a synthetic byte key with explicit shift-and-OR logic and
   document each mapping in a comment.
6. **Prefix token for frame-type flags** — when a flag set always implies a
   specific frame type (e.g. CAN FD's `"FD"` prefix, CAN XL's `"XLF"` prefix),
   embed it as the first token in every non-`[None]` table entry.
7. **`U64Field` formatters include the hex prefix** — the formatter returns
   `"0x12 [SYN, ACK]"` directly (consistent with `DisplayTables.FormatHexU8/U16`
   conventions) so the call site does not need to concatenate.
8. **`NoneField` container formatters return brackets only** — `"[SYN, ACK]"` or
   `"[None]"`.  For `NoneField` containers, the hex prefix has no meaning.
9. **DNS / mixed-field case** — when a 16-bit word mixes boolean flags with
   multi-bit numeric fields (opcode, RCODE), build a synthetic 8-bit key from
   only the boolean bits, build a 256-entry table, and return the brackets string
   only.  The caller concatenates hex + brackets:
   ```csharp
   ZA.Lazy(DisplayTables.FormatHexU16(flags), " ", DnsFlagsFormatter.Format(flags))
   ```

#### Formatter placement

Place the formatter in the same sub-folder as the protocol:

| Protocol | Formatter file |
|---------|----------------|
| TCP | `Tcp/TcpFlagsFormatter.cs` |
| CAN / CAN FD / CAN XL | `Can/CanFlagsFormatter.cs` |
| IPv4 | `IPv4/IPv4FlagsFormatter.cs` |
| FlexRay | `FlexRayFlagsFormatter.cs` |
| ICMPv6 NDP | `Icmpv6/Icmpv6NdpFlagsFormatter.cs` |
| HTTP/2 | `Http2/Http2FlagsFormatter.cs` |
| DNS | `Dns/DnsFlagsFormatter.cs` |

---

## 15. Error Handling

### 15.1 ParseResult Encoding

`ParseResult` is a compact 4-byte struct:

| Encoded value | Meaning |
|--------------|---------|
| `> 0` | Success: consumed bytes = value − 1 |
| `≤ 0` | Error: details in thread-local `ParseError.LastError` |

Implicit conversions make it transparent:

```csharp
return 42;                          // Success: consumed 42 bytes
return ParseError.InvalidData(...); // Error: stored in TLS
```

### 15.2 Never Throw on the Hot Path

Exceptions are **forbidden** during parsing. Use `ParseResult` for all error
propagation. Exceptions are reserved for registration time only.

```csharp
// ✅ GOOD — ParseResult with implicit conversion
if (data.Length < HeaderSize)
{
    return ParseError.InsufficientDataWithInfo(ProtocolName, HeaderSize, (ulong)data.Length);
}

// ✅ GOOD — propagate dispatch errors
ParseResult result = parentField.TryCallNextProtocolU64(_TableId, key, payload, in context);
if (result.TryPropagateError(out ParseResult error))
{
    return error;
}

// ❌ BAD — exception on the hot path
throw new InvalidOperationException("Invalid header");

// ❌ BAD — try/catch for flow control
try { ParseHeader(span); }
catch (IndexOutOfRangeException) { return ParseError.InsufficientData(ProtocolName); }
```

### 15.3 ParseError Factory Methods

| Factory | Usage |
|---------|-------|
| `ParseError.InsufficientDataWithInfo(proto, expected, actual)` | Data too short for header/payload |
| `ParseError.InvalidData(proto, message)` | Structurally invalid data (bad version, corrupt length) |
| `ParseError.Custom(proto, message)` | Protocol-specific errors |
| `ParseError.InternalError(proto, message)` | Unexpected programming error |

Always pass `ProtocolName` (the auto-generated constant) as the first argument.

### 15.4 Error Propagation from Dispatch

All `TryCallNextProtocol*()` return `ParseResult` with three named variants:
**Ok** (consumed bytes, including Ok(0)), **NotDispatched** (table present, no protocol
for this key — not an error and not Ok(0)), and **Error**. Consume with two methods
in this order: `TryPropagateError` first, then `TryGetConsumed`. After a false
`TryPropagateError`, a false `TryGetConsumed` is exactly `NotDispatched`.

```csharp
ParseResult result = parentField.TryCallNextProtocolU64(_TableId, key, payload, in context);
if (result.TryPropagateError(out ParseResult error))
{
    return error;
}

if (!result.TryGetConsumed(out int consumed))
{
    // no protocol for this key — try another key or stop
}
else
{
    // Ok, including Ok(0) when consumed == 0
}
```

---

## 16. Binary Header Parsing

### 16.1 BinaryParsable Source Generator

Fixed-size protocol headers use ZeroAlloc's `[BinaryParsable]` source generator:

```csharp
[BinaryParsable]
internal readonly partial struct UdpHeader
{
    /// <summary>Source port number.</summary>
    public U16BE SrcPort { get; init; }

    /// <summary>Destination port number.</summary>
    public U16BE DstPort { get; init; }

    /// <summary>Length of the UDP datagram (header + payload) in bytes.</summary>
    public U16BE Length { get; init; }

    /// <summary>One's complement checksum.</summary>
    public U16BE Checksum { get; init; }

    /// <summary>Serialized header size in bytes (8).</summary>
    internal const int HeaderSize = 8;
}
```

### 16.2 Generated API

```csharp
// Safe parse — returns false if data is too short
bool success = UdpHeader.TryParse(span, out UdpHeader header, out int consumed);

// Serialization
int written = header.Write(destinationSpan);

// Compile-time size constant
int size = UdpHeader.SIZE;
```

### 16.3 Big-Endian Types

Network byte order fields use ZeroAlloc's endian types:

| Type | Size | Access |
|------|------|--------|
| `U16BE` | 2 bytes | `.Value` → `ushort` |
| `U32BE` | 4 bytes | `.Value` → `uint` |
| `U64BE` | 8 bytes | `.Value` → `ulong` |
| `I16BE` | 2 bytes | `.Value` → `short` |
| `I32BE` | 4 bytes | `.Value` → `int` |
| `I64BE` | 8 bytes | `.Value` → `long` |

### 16.4 Bit Fields

For sub-byte fields (flags, version nibbles), use `[BinaryField(BitCount = N)]`:

```csharp
[BinaryParsable]
internal readonly partial struct IPv4Header
{
    [BinaryField(BitCount = 4)]
    public byte Version { get; init; }

    [BinaryField(BitCount = 4)]
    public byte Ihl { get; init; }

    [BinaryField(BitCount = 6)]
    public byte Dscp { get; init; }

    [BinaryField(BitCount = 2)]
    public byte Ecn { get; init; }

    public U16BE TotalLength { get; init; }
    // ...
}
```

The generator handles bit-level packing/unpacking automatically.

### 16.5 Header Struct Conventions

- Always `internal readonly partial struct`.
- Always include a `HeaderSize` / `MinHeaderSize` constant.
- Include an ASCII-art header diagram in the XML doc comment.
- Use `init` properties (not `set`).

---

## 17. FieldValue Factories

All field values are constructed via static factories:

```csharp
// Numeric types
FieldValue.NewU64(42)              // unsigned 64-bit
FieldValue.NewI64(-1)              // signed 64-bit
FieldValue.NewF64(3.14)            // 64-bit float
FieldValue.NewBool(true)           // boolean

// Address types
FieldValue.NewMacAddress(mac)      // 48-bit MAC
FieldValue.NewIPv4(addr)           // 32-bit IPv4
FieldValue.NewIPv6(addr)           // 128-bit IPv6

// Data types
FieldValue.NewBytes(memory)        // ReadOnlyMemory<byte>
FieldValue.NewString("text")       // string (implicit LazyString)
FieldValue.NewTimestamp(ts)        // nanosecond timestamp

// Special
FieldValue.None                    // empty / container with no intrinsic value
```

**Display text override:**

```csharp
// Override how the value is displayed in the UI
string csumText = DisplayTables.FormatHexU16(checksum);
container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), csumText);

// Override the container value's representation
FieldValue headerValue = FieldValue.NewBytes(data[..20])
    .WithCustomRepresentation(new LazyString("20 bytes"));
```

---

## 18. Protocol Registration

### 18.1 Registration Flow

```csharp
public static void RegisterStandardProtocols(this IStackBuilder builder)
{
    // Frame — entry point, owns frame.link_type table.
    // The frame protocol is discovered automatically during StackBuilder.Build()
    // by convention: the protocol whose Name is "frame" becomes the packet entry point.
    FrameProtocol frame = new();
    ProtocolId frameId = builder.RegisterProtocol(frame);
    frame.RegisterFields(builder, frameId);

    // Ethernet — auto-registered at frame.link_type via [RegisterAtTable]
    EthernetProtocol ethernet = new();
    ProtocolId ethId = builder.RegisterProtocol(ethernet);
    ethernet.RegisterFields(builder, ethId);

    // VLAN — auto-registered at eth.type = 0x8100, 0x88A8
    VlanProtocol vlan = new();
    ProtocolId vlanId = builder.RegisterProtocol(vlan);
    vlan.RegisterFields(builder, vlanId);

    // IPv4 — registered at eth.type = 0x0800, owns ip.proto table
    IPv4Protocol ipv4 = new();
    ProtocolId ipv4Id = builder.RegisterProtocol(ipv4);
    ipv4.RegisterFields(builder, ipv4Id);

    // IPv6 — registered at eth.type = 0x86DD, uses ip.proto table
    IPv6Protocol ipv6 = new();
    ProtocolId ipv6Id = builder.RegisterProtocol(ipv6);
    ipv6.RegisterFields(builder, ipv6Id);

    // UDP — registered at ip.proto = 17
    UdpProtocol udp = new();
    ProtocolId udpId = builder.RegisterProtocol(udp);
    udp.RegisterFields(builder, udpId);
}
```

The production helper also registers `DataProtocol` (`Name = "data"`) for
registered raw payloads (HTTP content-type, WebSocket, …) and
`SignalMessageRegistration.Register`. Frame-level leftover bytes are
`packet.unparsed_data` on `PacketProtocol` (§8), not a `data` protocol.
`SignalMessageRegistration.Register` **returns** `IReadOnlyList<SettingsLoadWarning>`;
`RegisterStandardProtocols` returns the combined PDU Transport + Signal Message
warning list so callers can decide how to handle each entry. Settings/profile JSON
paths may be empty. Stream overloads (`Register(builder, stream)`,
`PduTransportRegistration.TryLoadConfig`) **add** names/messages on top of that file;
they do not replace it.

### 18.2 Registration Order

Registration order does not affect dispatch table resolution. The `[UsesTable]`
attribute uses deferred resolution (`WhenProtocolTableRegistered()`), so a
protocol can reference a table before the owning protocol is registered.

However, for readability, register protocols in **stack order** (frame → link → network → transport → application).

---

## 19. Source Generator Output

The Source Generator produces the following from attributes:

### 19.1 From `[Protocol("name", "UI Name")]`

```csharp
// Generated constants
public const string ProtocolName = "udp";
public const string ProtocolUiName = "User Datagram Protocol";

// Generated Protocol.Name and UiName
public override string Name => ProtocolName;
public override string UiName => ProtocolUiName;
public override void OnStart(Stack stack) { _OnStartCustom(stack); }

// Generated ProtocolId field
private ProtocolId _ProtocolId;
```

### 19.2 From Field Attributes

```csharp
// Source:
[U64Field("udp.srcport", "Source Port", IndexGroup = "udp")]
private FieldId _SrcPortFieldId;

// Generated in RegisterFields():
_SrcPortFieldId = builder.RegisterFieldInGroup(
    protocolId, "udp.srcport", "Source Port", FieldType.U64, "udp");
```

### 19.3 From `[ProtocolTableU64]`

```csharp
// Source:
[ProtocolTableU64(PortTableName, "UDP Port")]
private ProtocolTableId _PortTableId;

// Generated in RegisterFields():
_PortTableId = builder.RegisterProtocolTable("udp.port", "UDP Port", ProtocolTableKeyType.U64);
```

### 19.4 From `[RegisterAtTable]`

```csharp
// Source:
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]

// Generated in RegisterFields():
builder.RegisterParserInU64TableByName("ip.proto", 17UL, protocolId);
```

### 19.5 From `[UsesTable]`

```csharp
// Source:
[UsesTable(IPv4Protocol.IpProtoTableName)]
private ProtocolTableId _IpProtoTableId;

// Generated in RegisterFields():
builder.WhenProtocolTableRegistered("ip.proto", id => _IpProtoTableId = id);
```

### 19.6 Index Group IDs

```csharp
// Generated fields (one per unique IndexGroup value):
private IndexGroupId _UdpGroupId;
private IndexGroupId _UdpPayloadGroupId;

// Generated in RegisterFields():
_UdpGroupId = builder.GetOrCreateIndexGroup("udp");
_UdpPayloadGroupId = builder.GetOrCreateIndexGroup("udp.payload");
```

### 19.7 RegisterFields / OnStart / OnShutdown Hooks

```csharp
// Generated:
public void RegisterFields(IStackBuilder builder, ProtocolId protocolId)
{
    // ... register fields, tables, settings, RegisterAtTable entries ...

    // Load setting backing fields (so custom hook can read them)
    _VerifyChecksum = builder.GetBoolSetting("udp.verify_checksum", false);

    // Call user-defined custom hook (config loading, deferred registrations)
    RegisterFieldsCustom(builder, protocolId);
}

public override void OnStart(Stack stack)
{
    // Call user-defined custom hook (caches against the frozen stack)
    OnStartCustom(stack);
}

// User writes (optional — for config-driven protocols):
partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
{
    // Load config file, register parsers in u64 tables, etc.
    // Use builder.WhenProtocolTableRegistered / WhenFieldRegistered
    // for cross-protocol references that may not be registered yet.
}

// User writes (optional — for caches and delegates):
partial void OnStartCustom(Stack stack)
{
    _ = PopulateUdpFields(in container);
    // Build dispatch caches, resolve cross-protocol fields, etc.
}
```

---

## 20. Current Dispatch Table Map

| Table Name | Owner | Key Type | Consumers |
|------------|-------|----------|-----------|
| `frame.link_type` | `FrameProtocol` | `u64` (LinkType enum) | `EthernetProtocol`, `SllProtocol`, `Sll2Protocol`, `CanProtocol`, `FlexRayProtocol`, `LinProtocol` |
| `eth.type` | `EthernetProtocol` | `u64` (EtherType) | `IPv4Protocol`, `IPv6Protocol`, `VlanProtocol`, `ArpProtocol`, `LlcProtocol` |
| `eth.ieee8023` | `EthernetProtocol` | `u64` / any | `LlcProtocol` (catch-all for IEEE 802.3 frames) |
| `ip.proto` | `IPv4Protocol` | `u64` (IP protocol number) | `TcpProtocol`, `UdpProtocol`, `IcmpProtocol`, `Icmpv6Protocol`; shared by `IPv6Protocol` via `[UsesTable]` |
| `tcp.port` | `TcpProtocol` | `u64` (port number) | `DnsProtocol`, `TlsProtocol`, `HttpProtocol`, `Http2Protocol`, `SomeIpProtocol`, … |
| `udp.port` | `UdpProtocol` | `u64` (port number) | `DnsProtocol`, `DhcpProtocol`, `Dhcpv6Protocol`, `DtlsProtocol`, `SomeIpProtocol`, `PduTransportProtocol` (when `pdu_transport.udp_dispatch_ports` contains a matching port) |
| `pdu_transport.id` | `PduTransportProtocol` | `u64` (PDU ID) | Signal Message via `dispatch_bindings` |
| `http.content_type` | `HttpProtocol` | `string` | `JsonProtocol`, `TextProtocol` |
| `http.upgrade` | `HttpProtocol` | `string` | `WebSocketProtocol` |
| `can.id` | `CanProtocol` | `u64` | Signal Message and sub-protocols by CAN ID / CAN XL priority |
| `can.extended_id` | `CanProtocol` | `u64` | Extended-frame and CAN XL acceptance-field sub-protocols |
| `flexray.id` | `FlexRayProtocol` | `u64` (slot + channel + cycle) | Signal Message and sub-protocols; key = `FlexRayLinkTypeFrame.EncodeDispatchKey(slot, channelB, cycle)` — bits `[10:0]` slot, bit `11` channel B, bits `[17:12]` cycle |
| `lin.id` | `LinProtocol` | `u64` (6-bit frame ID) | Signal Message and sub-protocols by LIN protected ID |
| `someip.messageid` | `SomeIpProtocol` | `u64` | Payload deserializers by SOME/IP Message ID |
| `tcp.heuristic` | `TcpProtocol` | heuristic | `HttpProtocol`, `TlsProtocol`, `Http2Protocol` (content-based) |

**Dispatch chain:**

```
Frame ──[frame.link_type]──► Ethernet ──[eth.type]──► IPv4/IPv6/VLAN/ARP/LLC
                         │                                     │
                         └──► SLL / SLL2                [ip.proto]
                         │                                │         │
                         └──► CAN ──[can.id/can.extended_id]       │
                         │                              UDP        TCP
                         └──► FlexRay ──[flexray.id]   │          │
                         └──► LIN ──[lin.id]           │          │
                                                  [udp.port]  [tcp.port / tcp.heuristic]
                                                   │    │      │         │
                                          DNS/DHCP PDU-Tr. HTTP/1.x   TLS/HTTP2
                                                       │          │
                                              [pdu_transport.id]  [http.upgrade / content_type]
                                                       │               │
                                                Signal Message      WS / JSON
```

---

## 21. Checklist for New Protocols

### Class Structure

- [ ] Create `sealed partial class` implementing `IProtocol`
- [ ] Implement `public ParseResult Parse`
- [ ] Prefer `parentField.CallProtocol` for child dispatch
- [ ] Effect keys use `Packet.GetEffectLayerKey(data)` (never remaining length)
- [ ] Add `[Protocol("name", "UI Name", Description = "...")]` attribute
- [ ] Add `[RegisterAtTable(OwnerProtocol.TableName, KeyConstant)]` attribute(s)
- [ ] Define `public const ulong` for table registration key value(s)
- [ ] Define `public const string` for owned dispatch table name(s) if any
- [ ] Define `private const string` for index groups used by multiple fields
- [ ] Use inline string literals in attributes (field names, single-use groups)
- [ ] Add `IndexGroup` to **every** field attribute

### Header Struct

- [ ] Create `[BinaryParsable] internal readonly partial struct` for fixed-size headers
- [ ] Use `U16BE` / `U32BE` for big-endian fields
- [ ] Use `[BinaryField(BitCount = N)]` for sub-byte fields
- [ ] Include `HeaderSize` / `MinHeaderSize` constant
- [ ] Include ASCII-art header diagram in XML doc

### Parse Method

- [ ] Validate minimum data size → `ParseError.InsufficientDataWithInfo(ProtocolName, ...)`
- [ ] Call `context.RecordProtocolPresence()` and `context.RecordGroupPresence()` eagerly
- [ ] Parse header with `TryParse()` into local variables
- [ ] Validate header integrity (version, length bounds)
- [ ] Compute payload bounds with `Math.Max(0, ...)` guard
- [ ] Record optional index groups conditionally
- [ ] Build `LazyString` summary with `ZA.String()` (minimize captured state)
- [ ] Store header bytes in `FieldValue.NewBytes()` for lazy re-parsing
- [ ] Append lazy container with pre-allocated `_Populator`
- [ ] Eagerly append cross-protocol fields if needed (src/dst addresses)
- [ ] If upstream address data is needed on hot path: implement `[ThreadStatic]` cache with `PacketId` guard (§7 Thread-Local Address Caches)
- [ ] If pre-computed values are needed in the populator: eagerly append internal helper field before the container (§9.8)
- [ ] If the dispatch key is needed without re-parsing: use `context.Dispatch.TryGetU64()` / `TryGetString()` (§10.7)
- [ ] If work is index-only (expensive derived fields): guard with `context.HasIndex` (§10.8)
- [ ] Dispatch on `parentField` (siblings) — **not** on container
- [ ] Propagate dispatch errors (`TryPropagateError`)
- [ ] Append trailing fields after dispatch (padding, trailer)
- [ ] Return consumed bytes (the PDU length this protocol used, not always `data.Length`)
- [ ] Do **not** insert leftover `data` PDUs. Frame-level tails are `packet.unparsed_data` on `PacketProtocol` (§8). Frame padding/trailer stay with the frame protocol.

### Lazy Populator

- [ ] Pre-allocate delegate in `OnStartCustom()` (captures only `this`)
- [ ] Re-parse header from `container.Value.Data.AsBytes()`
- [ ] Read pre-computed helper fields via `TryGetPrev` on `MutField` with `materialize: false` (bounded walk)
- [ ] Append all child fields via `container.Append()` / `container.AppendWithCustomText()`
- [ ] May call `TryCallNextProtocol*()` dispatch methods
- [ ] Do NOT call `context.RecordGroupPresence()` inside the populator
- [ ] Return `ParseResult` (0 on success)

### Display Text

- [ ] Precomputed static table in `DisplayTables.cs` for fixed-domain fields
- [ ] `ZA.Lazy(...)` for dynamic summary text and packet info
- [ ] No `string.Format()`, `$""`, or `.ToString()` on the parse hot path
- [ ] Use `DisplayTables.FormatHexU8()` / `FormatHexU16()` for hex formatting
- [ ] Flags container field uses a precomputed `*FlagsFormatter` class (see §14.8) with bracket-style display text (`[FLAG1, FLAG2]` or `0xNN [FLAG1, FLAG2]`)

### Dispatch (if protocol owns a table)

- [ ] Declare table with `[ProtocolTableU64(name, uiName)]`
- [ ] Build dispatch cache in `OnStartCustom()` (dense or sparse)
- [ ] Write dispatch helper method with cache + fallback pattern
- [ ] Consider heuristic table for content-based fallback

### Tests

- [ ] Basic parse tests (standard packets, all fields verified)
- [ ] Edge case tests (min-length, max-length, boundary values)
- [ ] Malformed data tests (too short, invalid values, corrupt headers)
- [ ] Display text format tests
- [ ] Cross-protocol field access tests (if applicable)
- [ ] Dispatch chain tests (verify sub-protocol is called)

### Documentation

- [ ] Field tree example in class XML doc comment
- [ ] ASCII-art header diagram in header struct doc comment
- [ ] Protocol-specific parsing notes and RFC references
