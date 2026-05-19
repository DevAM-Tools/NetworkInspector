<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.Core

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Core)](https://www.nuget.org/packages/NetworkInspector.Core)

Core engine of the NetworkInspector packet analysis framework.
Provides the protocol stack, field tree, packet index, value cache, reassembly,
and all value types needed to parse and inspect network packets with zero
allocations in hot paths.

## Table of Contents

- [Quick Start](#quick-start)
- [Architecture Overview](#architecture-overview)
- [Field Tree — Flat Array with Tree Navigation](#field-tree--flat-array-with-tree-navigation)
- [SlabAllocator — Generic Slab Allocation](#slaballocator--generic-slab-allocation)
- [Lazy Fields — Deferred Materialization](#lazy-fields--deferred-materialization)
- [PacketIndex — Cross-Packet Bitmap Index](#packetindex--cross-packet-bitmap-index)
- [Value Cache — Columnar Time-Series Storage](#value-cache--columnar-time-series-storage)
- [FieldValue — 16-Byte Discriminated Union](#fieldvalue--16-byte-discriminated-union)
- [ParseResult — 4-Byte Error Encoding](#parseresult--4-byte-error-encoding)
- [Protocol Dispatch Tables](#protocol-dispatch-tables)
- [Packet Lifecycle](#packet-lifecycle)
- [LargeBuffer — Beyond the 2 GB Limit](#largebuffer--beyond-the-2-gb-limit)
- [2Q Cache — Weight-Based Eviction](#2q-cache--weight-based-eviction)
- [Value Types](#value-types)
- [ID Types](#id-types)
- [Source Generator](#source-generator)
- [License](#license)

---

## Quick Start

```csharp
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

## Architecture Overview

| Component | Description |
|-----------|-------------|
| `StackBuilder` / `Stack` | Build and configure the protocol stack; parse frames into packets |
| `Packet` / `Frame` | Frame storage and parsed packet representation |
| `Field` / `MutField` | Read-only and mutable views into the protocol field tree |
| `FieldValue` / `FieldValueData` | 16-byte discriminated union for typed field values |
| `PacketIndex` | Cross-packet roaring bitmap index with presence queries |
| `ValueCacheSeries` | Columnar time-series cache for field values across packets |
| `IProtocol` | Protocol trait: `RegisterFields()`, `Parse()`, lifecycle hooks |
| `ProtocolTable` | Typed dispatch tables for protocol-to-sub-protocol lookup |
| `SettingsManager` | Runtime settings with typed metadata and JSON persistence |
| `DatagramDefragmenter` | IP datagram reassembly with FIFO eviction |
| `LargeBuffer` | Growable unmanaged memory buffer (~32 GB capacity) |
| `TwoQueueCache<K,V>` | Generic 2Q eviction cache with weight-based capacity |

---

## Field Tree — Flat Array with Tree Navigation

Parsed protocol fields form a tree (e.g. Ethernet → IP → TCP → payload).
Instead of allocating individual node objects linked by references, the field
tree is stored as a **flat contiguous array** of `FieldBody` structs.
Parent, child, and sibling relationships are encoded as `ushort` indices
into that array:

```
Flat array layout:

[0] root              ParentIndex=-  FirstChild=1  LastChild=4
[1]  └─ eth           ParentIndex=0  FirstChild=2  LastChild=3  NextSibling=4
[2]      ├─ eth.src   ParentIndex=1  NextSibling=3
[3]      └─ eth.dst   ParentIndex=1  PrevSibling=2
[4]  └─ ip            ParentIndex=0  FirstChild=5  LastChild=6
[5]      ├─ ip.src    ParentIndex=4  NextSibling=6
[6]      └─ ip.dst    ParentIndex=4  PrevSibling=5
```

**Why a flat array?**

- **Cache locality.** Scanning the array hits L1 cache; pointer-chasing
  through heap-allocated nodes hits L3.
- **Compact indices.** A `ushort` index is 2 bytes vs. 8 bytes for a
  reference — 75 % smaller.
- **Append-only.** During parsing, fields are only ever appended. No
  allocations after the initial capacity is reserved.
- **Zero-copy navigation.** `Field` and `MutField` are lightweight
  readonly structs that index into the packet's array without copying data.

### Field vs. MutField

| Type | Lifetime | Purpose |
|------|----------|---------|
| `Field` (readonly struct) | After parsing | Read-only navigation, safe to store and share |
| `MutField` (readonly ref struct) | During parsing only | Build the tree, cannot escape the call stack |

`MutField` is a `ref struct` by design — it cannot be captured in closures
or stored on the heap. This enforces the rule that the field tree is only
mutated during the synchronous parse phase.

---

## SlabAllocator — Generic Slab Allocation

All per-packet arrays are allocated from thread-local **slab allocators**
(`SlabAllocator<T>`). Instead of each packet allocating its own arrays
(`new FieldBody[48]`, `new LazyPopulator[8]`, `new FieldBodyChunk[4]`),
multiple packets share a single large backing array per type. This eliminates
thousands of small GC allocations per second.

### How It Works

A `SlabAllocator<T>` pre-allocates a large `T[]` backing array and hands out
contiguous slices via a bump pointer (`_Used`). Each allocation advances the
pointer and returns a `(T[] buffer, int offset)` pair. The consumer stores
these two values — the allocator does not track individual allocations.

```
SlabAllocator<FieldBody> slab (capacity=1024):

_Buffer: [0..15] Packet A  [16..31] Packet B  [32..47] Packet C  [48..1023] free...
          ▲                  ▲                  ▲
          │                  │                  └─ C._Buffer = slab._Buffer, C._Offset = 32
          │                  └──────────────────── B._Buffer = slab._Buffer, B._Offset = 16
          └─────────────────────────────────────── A._Buffer = slab._Buffer, A._Offset = 0

_Used = 48
```

When the slab is full, a new one is created. The old slab's `_Buffer` stays
alive as long as any packet references it — the GC handles cleanup
automatically.

**Why "slab" and not "arena"?** An arena allocator implies a shared lifetime —
all allocations are freed together via `Reset()`. Our allocator has no collective
deallocation. Each consumer independently holds a reference to its slice of the
backing array. The slab stays alive as long as *any* consumer still references it.
Individual lifetimes are managed by the GC, not by the allocator. The bump
pointer is just the allocation strategy within the slab, not a lifetime scope.

### Three Slab Instances per Thread

Each parsing thread maintains three `[ThreadStatic]` slab allocators:

| Slab | Element Type | Capacity | Approx. Size | Access Pattern |
|------|-------------|----------|-------------|----------------|
| **FieldBody** | `FieldBody` | 1024 | ~64 KB | Chunked (16-slot segments via `FieldBodyChunk`) |
| **ChunkDescriptor** | `FieldBodyChunk` | 256 | ~3 KB | Flat slice per packet (4 descriptors, doubled on demand) |
| **LazyPopulator** | `LazyPopulator` | 512 | ~4 KB | Flat slice per packet (8 slots, grown via `Array.Resize`) |

All three use the same generic `SlabAllocator<T>` — no type-specific slab
classes are needed. However, the **access pattern** differs:

- **FieldBody** has an additional indirection level: the packet does not address
  FieldBody slots directly. Instead, it stores an array of `FieldBodyChunk`
  descriptors, each pointing to a 16-slot region within the FieldBody slab.
  This chunking enables zero-copy growth (new chunk = new 16-slot region, old
  chunks untouched).
- **ChunkDescriptor** and **LazyPopulator** are flat slices — the packet stores
  a `(T[] buffer, int offset)` pair and accesses elements directly by index.

```
FieldBody (chunked):
  Packet → ChunkDescriptor[] → each descriptor → FieldBody slab [offset..offset+15]

ChunkDescriptor (flat):
  Packet → (FieldBodyChunk[] buffer, int baseOffset) → buffer[baseOffset + i]

LazyPopulator (flat):
  Packet → (LazyPopulator[] buffer, int baseOffset) → buffer[baseOffset + i]
```

### Chunk-Based Field Storage

The field tree is not one flat array but a **chain of 16-slot chunks**.
Each chunk is described by a `FieldBodyChunk` descriptor (a `FieldBody[]`
reference + `int` offset into the slab). A packet starts with 4 chunk
descriptors (covering 64 fields) and doubles on demand:

```
Packet with 25 fields:

ChunkDescriptor[0] → FieldBody slab [48..63]    (fields 0–15)
ChunkDescriptor[1] → FieldBody slab [80..95]    (fields 16–24 + 7 free)
ChunkDescriptor[2]   (reserved, unused)
ChunkDescriptor[3]   (reserved, unused)

Logical index → physical location:
  chunkIdx = index >> 4    (divide by 16)
  slotIdx  = index & 0xF  (modulo 16)
  ref FieldBody = Chunks[chunkIdx].Buffer[Chunks[chunkIdx].Offset + slotIdx]
```

**Why chunks instead of one flat array?**

- **No copy-on-grow.** Adding a chunk just allocates 16 new slots from the
  slab — existing chunks are untouched.
- **Lazy allocation.** Only 1 chunk (16 slots) is allocated at parse time.
  Subsequent chunks are allocated on-demand during materialization.
  Packets that are never materialized consume only 16 slab slots.
- **Uniform scaling.** All packets use the same growth path regardless of
  size. No special-casing for small vs. large packets.

### Cross-Thread and Cross-Slab Scenarios

Packets are parsed on a **parsing thread** but may be materialized on a
different thread (e.g. UI thread). When materialization triggers chunk
growth, the new chunks are allocated from the **materializing thread's**
slab — which may be a completely different `SlabAllocator<T>` instance
than the one used during parsing:

```
Parsing thread:   Packet._Chunks → SlabA._Buffer[8..11]  (4 descriptors)
                  Packet.ChunkDescriptor[0].Buffer → SlabA_FB._Buffer[48..63]

UI thread materializes → needs chunk 5 → descriptor array doubles:
                  Packet._Chunks → SlabB._Buffer[0..7]   (8 descriptors, copied)
                  Packet.ChunkDescriptor[4].Buffer → SlabB_FB._Buffer[0..15]
```

The old descriptor slots in `SlabA` become orphaned (48 bytes waste — negligible).
The packet now references buffers from **two different slab instances** — this is
by design and fully safe because:

1. Slab backing arrays stay alive via GC references from the packets.
2. `Volatile.Write` on `_FieldCount` provides the release fence that publishes
   all chunk allocations to concurrent reader threads.

### Memory Savings

| State | Old Design | Slab Design |
|-------|-----------|-------------|
| Never materialized | 48 × 64 B = 3072 B (3 inline chunks) | 16 × 64 B + 4 × 12 B = 1072 B |
| Fully materialized (47 fields) | Same 3072 B | 48 × 64 B + 4 × 12 B = 3120 B |
| Per-packet heap objects | `FieldBody[]` + `LazyPopulator[]` + `FieldBodyChunk[]` | **Zero** (all slab-backed) |

---

## Lazy Fields — Deferred Materialization

Not all fields can be populated during parsing. Some depend on runtime data
(e.g., interface names), post-parse analysis, or expensive computations
that should only run when the field is actually accessed.

### How It Works

During parsing, a protocol registers a **lazy populator** — a callback that
will fill in the field's children when they are first needed:

```csharp
// During Parse(): register a deferred populator
parentField.AppendLazy(interfaceFieldId, FieldValue.None, (in MutField field) =>
{
    // Called only when someone iterates into this field
    field.Append(nameFieldId, FieldValue.NewString(interfaceName));
    return 0; // consumed bytes
});
```

Internally, each lazy field stores a **1-based index** into a per-packet
`LazyPopulator[]` array. A `_PendingLazyCount` counter tracks how many
populators have not yet run, providing an O(1) early exit when all fields
are already materialized.

### Thread-Safe Materialization

After `Seal()`, multiple threads may read the packet concurrently.
Materialization uses a **lock-free CAS + SpinWait guard** to ensure each
populator runs exactly once:

```
Thread A reads field → LazyIndex != 0 → CAS(flag, 0→1) succeeds
  → runs populator, sets LazyIndex = 0, decrements _PendingLazyCount
  → Volatile.Write(flag, 0) releases guard

Thread B reads same field → CAS(flag, 0→1) fails → SpinWait
  → retries after Thread A is done → LazyIndex == 0 → returns (already populated)
```

Volatile reads/writes ensure cross-thread visibility on weakly-ordered
architectures (ARM64, WASM).

### Impact on Iteration

This design has a direct impact on how you iterate over packets:

```csharp
// DEFAULT: Children(materialize: true)
// Triggers lazy population before iterating — correct, but may do work you don't need.
foreach (Field child in field.Children())
{
    // All children visible, including lazily populated ones
}

// FAST PATH: Children(materialize: false)
// Returns only eager (already populated) fields — faster when you know lazy fields
// are irrelevant for your use case (e.g., counting protocols, reading header fields).
foreach (Field child in field.Children(materialize: false))
{
    // Only pre-populated children visible
}
```

When iterating over *millions of packets* (e.g., for indexing, filtering, or
exporting), the difference matters. If you only need header fields that are
always populated eagerly, passing `materialize: false` avoids running
populators for fields you never read.

---

## PacketIndex — Cross-Packet Bitmap Index

The `PacketIndex` answers questions like *"Which packets contain TCP?"* or
*"Which packets have both IPv4 and UDP?"* in O(1) per bitmap, regardless of
how many packets exist. It uses **Roaring Bitmaps** — compressed sparse
bitsets that store packet IDs efficiently.

### Index Groups — Reducing Bitmap Count

A naive approach would create one bitmap per field (~80+ fields for standard
protocols). But fields that are *always present together* (e.g., `eth.src`,
`eth.dst`, `eth.type` all appear whenever Ethernet is present) can share a
single bitmap.

Fields are grouped into **index groups**:

```
Group "eth":         eth.src, eth.dst            (always together)
Group "eth.type":    eth.type                    (only Ethernet II)
Group "eth.len":     eth.len                     (only 802.3)
Group "ip":          ip.src, ip.dst, ip.version  (always together)
Group "ip.options":  ip.options, ip.options.*    (only when options exist)
Group "udp":         udp.src_port, udp.dst_port  (always together)
```

**Result:** ~80 fields → ~8 group bitmaps → 90 % memory savings.

### Per-Packet Dedup

During parsing, a protocol may call `RecordGroupPresence()` multiple times
for the same group (e.g., inside a loop). A **ulong bit-vector** tracks
which groups have already been recorded for the current packet, preventing
redundant `RoaringBitmap.Add()` calls:

```csharp
int word = groupId >> 6;           // which ulong in the dedup array?
ulong bit = 1UL << (groupId & 63); // which bit?
if ((dedupWord & bit) != 0) return; // already recorded
dedupWord |= bit;
_GroupBitmaps[groupId].Add(packetId);
```

### PresenceQuery — Fluent Set Algebra

Query the index with boolean set operations on groups, protocols, or fields:

```csharp
// "All packets with TCP but not UDP"
long count = index.Query()
    .SelectProtocol(tcpProtocolId)
    .AndNotProtocol(udpProtocolId)
    .Count();

// "All packets with either ARP or ICMP"
RoaringBitmap result = index.Query()
    .SelectProtocol(arpProtocolId)
    .OrProtocol(icmpProtocolId)
    .ToBitmap();

// "Does packet #42 contain IPv4?"
bool has = index.Query()
    .SelectField(ipSrcFieldId)
    .Contains(42);
```

`PresenceQuery` is a `ref struct` — it stays on the stack and composes
bitmap operations without heap allocation.

Public bitmap lookup APIs return live read-only bitmap views. External callers can
query them allocation-free and call `ToBitmap()` only when they explicitly need a
detached mutable copy. Internal filter hot paths continue to use dedicated live-
bitmap accessors to avoid any wrapper-to-copy transitions on per-packet checks.

### Zero-Cost When Not Used

The index is optional. `ParseContext` carries a nullable `PacketIndex?`.
When null, all `RecordGroupPresence()` / `RecordProtocolPresence()` calls
are no-ops — protocols pay exactly zero cost if the index is not attached.

---

## Value Cache — Columnar Time-Series Storage

The value cache stores selected field values across all packets in a
**columnar layout**, enabling fast time-series access without re-parsing.

### The Problem

To answer "What are all TCP source port values across 1 million packets?",
you'd have to iterate each packet, find the `tcp.srcport` field, and read
its value. That means traversing 1 million field trees — slow and
cache-unfriendly.

### The Solution: ValueCacheSeries

A `ValueCacheSeries` stores all values of *one field* in parallel arrays:

```
ValueCacheSeries for "tcp.srcport":
  _Timestamps:  [1000, 1001, 1002, 1003, ...]   // nanos since epoch
  _PacketIds:   [0,    1,    3,    5,    ...]   // which packet
  _Data:        [443,  80,   8080, 443,  ...]   // the actual values
```

This is a **columnar layout**: all timestamps together, all packet IDs
together, all values together. Sequential reads hit L1 cache instead of
chasing through scattered packet objects.

### Thread-Safety: Single-Writer / Multi-Reader

- **Writer** (parse thread): appends at index `_Count`, then publishes with
  `Volatile.Write(ref _Count)` — a release fence that makes all prior
  writes visible to readers.
- **Reader** (UI/query thread): reads `Volatile.Read(ref _Count)` (acquire
  fence), then accesses arrays sliced to that count. Readers never block
  the writer.
- **Growth safety:** When arrays need to grow, new larger arrays are
  allocated and old data is copied. Readers holding references to old arrays
  continue to work — the GC keeps them alive.

### Compact Storage Modes

Not every field needs full-precision storage. The `ValueCacheStorageMode`
enum allows lossy compression for reduced memory:

| Mode | Size | Use case |
|------|------|----------|
| `Native` | Full size | Lossless (default) |
| `CompactFloat` | 4 bytes | Single-precision float |
| `CompactUInt8` | 1 byte | Port ranges 0–255, small counters |
| `CompactUInt16` | 2 bytes | Port numbers, protocol IDs |
| `CompactUInt32` | 4 bytes | Large counters |
| `CompactInt8/16/32` | 1/2/4 bytes | Signed variants |

### Configuration

The value cache is configured via a runtime setting:

```
index.value_cache_fields = "tcp.srcport:compact_uint16,ip.src,udp.length"
```

Each entry names a field and an optional storage mode. The `ValueCacheBuilder`
creates O(1) field-to-slot routing so that during parsing, recording a value
is three operations: bounds check, dedup bit-check, append.

### First-Value-Wins Dedup

When a field appears multiple times in a single packet (e.g., nested
protocols), only the first occurrence is recorded. A per-packet `ulong[]`
bit-vector (same pattern as the index dedup) ensures this without branches.

---

## FieldValue — 16-Byte Discriminated Union

`FieldValueData` packs any field value into exactly 16 bytes using a
`ulong` for inline data and an `object?` reference as both type
discriminant and payload:

```
Layout (16 bytes):
┌──────────────────────┬──────────────────────────┐
│ _Data (8 bytes)      │ _Ref (8 bytes)           │
├──────────────────────┼──────────────────────────┤
│ u64/i64/f64/bool     │ U64Marker.Instance       │  → U64
│ MAC (6 bytes)        │ MacAddressMarker         │  → MacAddress
│ IPv4 (4 bytes)       │ IPv4Marker               │  → IPv4Address
│ Timestamp (8 bytes)  │ TimestampMarker          │  → Timestamp
│ offset|length packed │ byte[] reference          │  → Bytes
│ (unused)             │ string reference           │  → String
│ (unused)             │ null                       │  → None
└──────────────────────┴──────────────────────────┘
```

**Type discrimination** uses `ReferenceEquals` checks against singleton
marker objects — a single pointer comparison, faster than an enum switch.
Numeric and small values (MAC, IPv4, Timestamp) are stored inline in `_Data`
with zero heap allocation. Bytes and strings use the `_Ref` slot to carry
the reference.

---

## ParseResult — 4-Byte Error Encoding

`ParseResult` fits success and error into a single `int` (4 bytes):

```
_EncodedValue > 0  →  Success: consumed = _EncodedValue - 1
_EncodedValue == 0 →  Uninitialized error
_EncodedValue < 0  →  Error: details in Thread-Local Storage
```

On the hot path, protocols just `return consumedBytes;` — the implicit
operator adds 1 and wraps it. On the rare error path, `ParseError` is
written to TLS via `ParseError.SetLastError()`. This avoids boxing or
heap-allocating an error object for every successful parse call.

```csharp
// Hot path — zero-cost success
return 20; // implicitly becomes ParseResult with _EncodedValue = 21

// Error path — TLS write (rare)
return ParseError.InsufficientData("ip", 20, data.Length);
```

---

## Protocol Dispatch Tables

Protocols register themselves in typed dispatch tables keyed by u64, string,
bytes, or bool values. When a protocol finishes parsing, it dispatches to
the next layer via the table:

```csharp
// IPv4 dispatches to TCP (protocol=6), UDP (protocol=17), etc.
field.TryCallNextProtocolU64(ipProtocolTableId, protocolNumber, payload);
```

Multi-match is supported: if multiple protocols register for the same key,
all are called and wrapped in a `packet.choice` container labelled
`"Choice: <table>: <key>"` (e.g. `"Choice: frame.link_type: 227"`). A
`HeuristicProtocolTable` supports content-based detection when no explicit
key is available.

---

## Packet Lifecycle

```
 CREATE         PARSE              SEAL          READ (concurrent)
   │               │                 │               │
   ▼               ▼                 ▼               ▼
 Packet()    protocols append    Volatile.Write   Field / foreach
 root field  fields to tree     (_Finalized, 1)  Children(materialize)
 count = 1   register lazy      release fence    → lazy materialization
             populators                           (CAS guard, exactly once)
             record index                         PresenceQuery
             presence                             ValueCacheSeries read
```

1. **Create**: Allocate `Packet` with root field, capacity pre-reserved.
2. **Parse**: Single-threaded. Protocols append fields, register lazy
   populators, record index/cache data. `MutField` (ref struct) prevents
   escape.
3. **Seal**: `Volatile.Write` publishes all writes. After this, the packet
   is immutable (except lazy materialization).
4. **Read**: Multiple threads iterate fields, query the index, read cached
   values. Lazy fields are materialized on demand via the CAS guard.

---

## LargeBuffer — Beyond the 2 GB Limit

.NET arrays are limited to ~2 GB. `LargeBuffer` bypasses this by
storing data as an array of 16-byte `LargeBufferElement` structs and
reinterpreting the memory as a byte span via `MemoryMarshal.Cast`:

```
Capacity: Array.MaxLength × 16 bytes ≈ 32 GB
```

Used for TCP reassembly buffers and PCAP reconstruction where a single
stream can exceed 2 GB.

---

## 2Q Cache — Weight-Based Eviction

`TwoQueueCache<TKey, TValue>` implements the Two-Queue eviction algorithm:

| Queue | Strategy | Purpose |
|-------|----------|---------|
| **A1in** | FIFO | Recently inserted items (first access) |
| **Am** | LRU | Frequently accessed items (promoted on second access) |
| **Ghost** | Key-only tracking | Recently evicted A1in keys for re-access detection |

Eviction is weight-based (not count-based), allowing entries of different
sizes. `Create2Q` trims A1in first (25 % budget), then Am, matching the
original 2Q paper.

```csharp
// Simple weight-bounded cache — FIFO-first, no A1in budget.
TwoQueueCache<int, byte[]>.CreateBounded(maxBytes);

// Full 2Q algorithm — 25% A1in, 50% ghost (paper defaults).
TwoQueueCache<int, byte[]>.Create2Q(maxBytes);

// Full 2Q with explicit partition sizes.
TwoQueueCache<int, byte[]>.Create2QCustom(maxWeight, a1InMax, ghostMax);

// No eviction (testing / unbounded scenarios).
TwoQueueCache<int, byte[]>.CreateUnbounded();
```

Additional convenience APIs:

```csharp
// Factory invoked only on miss.
byte[] value = cache.GetOrAdd(key, static currentKey => LoadValue(currentKey));

// Allocation-friendly overload for hot paths that need extra state.
byte[] value = cache.GetOrAdd(key, static (currentKey, state) => state.Load(currentKey), loader);
```

Bounded factories accept a weight of `0` to disable caching while keeping the same call site.

---

## Value Types

`MacAddress`, `IPv4Address`, `IPv6Address`, `Eui64`, `Timestamp`, `Uuid`

All are readonly structs implementing `ISpanFormattable`, `IUtf8SpanFormattable`,
`IEquatable<T>`, `IComparable<T>`, and `IParsable<T>`.

## ID Types

`FieldId`, `ProtocolId`, `ProtocolTableId`, `PacketId`, `FrameId`,
`FrameSourceId`, `FrameInterfaceId`, `IndexGroupId`, `PostParserId`,
`HeuristicProtocolTableId`

All are strongly-typed wrappers (source-generated via ZeroAlloc) preventing
ID mix-ups at compile time.

## Source Generator

The `NetworkInspector.Generators` Roslyn source generator is bundled with
this package. It processes `[Protocol]` attributes and generates:

- `RegisterFields()` for field/table/setting registration
- Constant field ID members
- Index group registration
- `Name` / `UiName` properties

---

## License

[MIT License](../LICENSE) — © DevAM
