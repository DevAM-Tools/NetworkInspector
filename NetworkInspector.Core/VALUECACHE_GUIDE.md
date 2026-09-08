<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# ValueCache — Usage Guide

How to fill and read RAM columnar field series. Design and invariants: [`docs/value-cache-design.md`](../docs/value-cache-design.md).

**Audience:** callers who want selected field values over many packets without walking a field tree.

Filters still read the field tree, not this cache. See [`FILTER_GUIDE.md`](../NetworkInspector.Filter/FILTER_GUIDE.md).

---

## 1. When to use it

| Need | Use |
|------|-----|
| One packet, topology, display text | Field tree (`ParseFrame` default / `TryGetPacket`) |
| Same field(s) across many packets, RAM columns | `ValueCache` |
| Presence only (protocol/group “did this appear?”) | `PacketIndex` |
| Parse for effects, drop the tree | `FieldTreeMode.Skip` plus a cache and/or index |

A cache is grow-only. There is no row cap and no silent drop. Plan `ChunkShift` for capture length (default 12 = 4096 slots in the first inner chunk).

---

## 2. Construct

```csharp
ValueCache cache = new(
    stack,
    [new ValueCacheFieldConfig(portId, ValueCaptureMode.FirstOccurrence)],
    options: new ValueCacheBuildOptions { ChunkShift = 12 });
```

- Pass `null` / omit `options` for defaults. Do **not** pass `default(ValueCacheBuildOptions)` — that zeroes `ChunkShift` and is rejected.
- `RecordAllFields` creates payload series for fields that actually appear. It does not pre-create unused stack fields and does not auto-create custom-text series.
- Explicit `ValueCacheFieldConfig` entries override capture mode and can add custom-text / custom-representation string series.

Session uses names:

```csharp
new Session(stack, new SessionOptions
{
    ValueCache = new ValueCacheRequest { FieldNames = ["udp.srcport"] },
});
```

---

## 3. Fill (one writer)

| Path | When |
|------|------|
| `Packet.ParseFrame(..., cache)` | First parse records. Replays skip unless `recordOnReplay: true`. |
| Skip parse + cache | Ingest and Session PullFill. No tree retained. |
| `ValueCache.RecordPacket` | Sealed **Build** packet (CLI, tests). Throws on skip packets. |

One writer per cache is the intended ingest shape. Contended `Record` on the same series waits (serialized, no throw). Readers may see a packet that is still being parsed: rows publish as soon as `Record` appends.

---

## 4. Read: snapshot handle, not live `Count` in a loop

`series.Handle` freezes `Count` and the monotonic flags. That prefix is stable (grow-only slots). New rows are invisible until you take another handle.

**Best practice — watermark tail**

```csharp
int watermark = 0;

void OnMoreRows(ValueCacheSeries<ulong> series)
{
    ValueCacheSeriesHandle<ulong> handle = series.Handle;
    foreach (ValueCacheRow<ulong> row in handle.EnumerateFrom(watermark))
    {
        _ = row.Value;
    }

    watermark = handle.Count;
}
```

Do not keep iterating the **old** handle hoping it grows. Do not `await` inside `foreach` over a handle enumerator (ref struct / `ReadOnlySpan`).

Live `series.Count` / `series.GetValue(i)` is valid if you load count once, then read `i < thatCount`. The handle does that load for you and gives range / chunk APIs on the same prefix.

`ReadOnlyValueCache` / `ReadOnlyValueCacheSeries<T>` expose the same read APIs (`Handle`, `Count`, `GetValue`) without `Record` / `RecordPacket`. `ValueCache.GetSeries<T>` on the class still returns the writer series.

---

## 5. Find a packet id or time range

| Situation | API |
|-----------|-----|
| Ids/timestamps still non-decreasing (`PacketIdsMonotonic` / `TimestampsMonotonic`) | `TryFindPacketIds` / `TryFindTimestamps` → contiguous `ValueCacheSeriesRange`, then `Enumerate(range)` or SIMD chunks over that range |
| Out of order (flag false) | `TryFind*` returns `false`. Hits are **not** a range. Walk and filter. |
| `AllOccurrences` duplicate ids, still sorted | Binary search still works; the range covers the closed interval of duplicates |

```csharp
if (handle.TryFindPacketIds(10, 20, out ValueCacheSeriesRange range))
{
    foreach (ValueCacheRow<ulong> row in handle.Enumerate(range))
    {
        _ = row.Value;
    }
}
else
{
    foreach (ValueCacheRow<ulong> row in handle)
    {
        if (row.PacketId is >= 10 and <= 20)
        {
            _ = row.Value;
        }
    }
}
```

Parse-time record and increasing `RecordPacket` stay monotonic. Offline merge / rewind can clear the flag.

---

## 6. Scalar row vs SIMD chunks

| Goal | API |
|------|-----|
| One row at a time (UI, mixed types, `string` / `byte[]`) | `foreach` / `EnumerateFrom` → `ValueCacheRow<T>` |
| Bulk numeric / unmanaged `T` (`ulong`, `int`, …) | `EnumerateChunks` / `EnumerateChunksFrom` → SoA spans |
| Persistence / one inner array from slot 0 | `TryGetValueChunk(chunkIndex)` on the handle (clipped to snapshot `Count`) |

`EnumerateChunksFrom(watermark)` slices the **first** span from that slot so you do not re-process the prefix of a mid-chunk watermark. Full inner chunks are `1 << ChunkShift` rows; first/last spans may be shorter. Spans are not promised 32-byte aligned — unaligned `Vector256` / `Vector128` loads.

```csharp
foreach (ValueCacheSeriesHandle<ulong>.Chunk chunk in handle.EnumerateChunksFrom(watermark))
{
    ReadOnlySpan<ulong> values = chunk.Values;
    // Vector256.Create(values.Slice(i, Vector256<ulong>.Count)) + scalar tail
}
```

---

## 7. Handle extras (`Keys`, `ChunkShift`)

You do not need these for the usual typed scan.

| Member | Role | Skip when |
|--------|------|-----------|
| `handle.Keys` | Same snapshot without `T` (packet id / timestamp only) | You already have `ValueCacheSeriesHandle<T>` |
| `handle.ChunkShift` | Same as `series.ChunkShift` | You still hold the series |

They exist so a helper can take a keys-only handle or size SIMD loops from the handle alone (`_Series` is private).

---

## 8. Writer gate

`_AppendGate` is a cheap CAS (not `Monitor`). Uncontended `Record` takes it in one `CompareExchange`. Contended callers **wait** in a `while` + `SpinWait` loop until the gate is free, then run. That serializes the three column `Append`s and `Count` publish so rows stay untorn. It is not taken on the read path.

The `while` inside `ChunkedGrowOnlyStore.Append` is a different retry (Count CAS vs `Clear`). Store `Append` still rejects a second overlapping `Append` with an exception; the series gate is what keeps two `Record`s from hitting that.

`AllOccurrences` has no extra per-packet counter: a packet cannot exceed `ushort.MaxValue - 1` fields (`Packet` storage indexes are `ushort`, `NullIndex` is `ushort.MaxValue`).

---

## 9. Session

- **Ingest** (`SessionOptions.ValueCache`): skip-parse on the source thread, `recordOnReplay: false`. `ISessionReader.IngestValueCache` is a `ReadOnlyValueCache`.
- **Runtime** (`TryAddValueCache`): dedicated slot, skip-parse from `TryGetFrame` with `recordOnReplay: true`. Never `RecordPacket`.
- `OnNewRows(fromIndex, toIndexExclusive)` is a **packet-id** window, same coalescing as `OnNewPackets`. Column length is `series.Count`. Keep a **row** watermark (`handle.Count`), not only the packet-id range.
- Restart abandons writers; runtime slots rebind and fill from 0 — reset your watermark.

```csharp
public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
{
    if (!cache.TryGetSeries<ulong>("udp.srcport", out ReadOnlyValueCacheSeries<ulong> series))
    {
        return;
    }

    ValueCacheSeriesHandle<ulong> handle = series.Handle;
    foreach (ValueCacheRow<ulong> row in handle.EnumerateFrom(_RowWatermark))
    {
        _ = row.PacketId;
    }

    _RowWatermark = handle.Count;
    _ = fromIndex;
    _ = toIndexExclusive;
}
```

---

## 10. Don’t

- Pass `default(ValueCacheBuildOptions)`.
- `RecordPacket` on a skip packet; filter or export a skip packet.
- `await` inside handle `foreach`.
- Assume `TryFind*` always succeeds — check the bool, then scan if false.
- Treat `OnNewRows` packet-id bounds as series indexes.
- Expect the cache to shrink or to overwrite in place (`LastOccurrence` does not exist).
- Assign `ReadOnlyValueCache` / `ReadOnlyValueCacheSeries<T>` to `IReadOnlyValueCache` / `IReadOnlyValueCacheSeries<T>` — that boxes. Keep the struct or use a generic constraint. `ValueCache` is a class and can be stored as the interface without boxing; its public `GetSeries<T>` still returns the writer series.

---

## Links

- Design: [`docs/value-cache-design.md`](../docs/value-cache-design.md)
- Skip parse: [`docs/skip-field-tree.md`](../docs/skip-field-tree.md)
- Core package: [`README.md`](README.md)
- Session: [`../NetworkInspector.Sessions/README.md`](../NetworkInspector.Sessions/README.md)
