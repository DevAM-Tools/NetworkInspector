<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Exporters.DuckDb

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Exporters.DuckDb)](https://www.nuget.org/packages/NetworkInspector.Exporters.DuckDb)

Columnar packet exporter that writes a single `.duckdb` database file for direct SQL analysis.

This package is separate from `NetworkInspector.Exporters` so DuckDB's native runtime is only
pulled in by consumers that actually export to DuckDB.

## What This Is

The `DuckDbExporter` serializes parsed packets through `IPacketListener` into a DuckDB database,
built on the same `ColumnarPacketBatch` accumulator shared by the PBF columnar format and the
Parquet exporter in `NetworkInspector.Exporters`.

**Overwrite semantics:** exporting to an existing `.duckdb` path deletes the previous file (and
its `.wal` sidecar) before writing, so re-exports replace rather than append.

## Install

```bash
dotnet add package NetworkInspector.Exporters.DuckDb
```

`NetworkInspector.Exporters` is a package dependency and does not need to be referenced separately.

## Why Use It

- Query the export immediately with SQL — no separate load step.
- Bulk-loaded via `DuckDBAppender`, never row-at-a-time `INSERT`.
- `FieldType.U64` is stored as DuckDB's native `UBIGINT`.

## Table Layout

| Table | Contents |
| --- | --- |
| `packets(packet_id INTEGER, timestamp_ns BIGINT [, info VARCHAR] [, frame_bytes BLOB])` | One row per packet; `packet_id` matches Core `PacketId` (`int`) |
| `topology(packet_id INTEGER, node_id INTEGER, field_id INTEGER, parent_node_id INTEGER)` | One row per field-tree node |
| `catalog(field_id INTEGER, name VARCHAR, ui_name VARCHAR, field_type INTEGER, protocol_id INTEGER, table_name VARCHAR, description VARCHAR)` | One row per distinct field ID observed; `description` is `NULL` when the field has none |
| `protocols(protocol_id INTEGER, name VARCHAR, ui_name VARCHAR)` | One row per protocol registered in the parsing stack |
| `field_{id}(packet_id INTEGER, node_id INTEGER, value <type> [, custom_repr VARCHAR] [, custom_text VARCHAR])` | One row per field occurrence; `value` is `VARCHAR` for `FieldType.String` |

Catalog is populated once in `OnFinish()`. The `protocols` table is populated once at the start
(first `OnPacket()`), because the protocol set is fixed by the stack. Field data tables are
created lazily. Join `catalog.protocol_id` to `protocols.protocol_id` to label fields by protocol
without the dissector assemblies. New catalog columns are only ever appended, so positional
readers stay valid.

## Quick Start

```csharp
using NetworkInspector.Exporters.DuckDb;

using DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
    .ToFile("packets.duckdb")
    .Build();

foreach (Packet packet in packets)
{
    if (!exporter.OnPacket(packet))
    {
        break;
    }
}

exporter.OnFinish();
```

```sql
SELECT p.packet_id, p.timestamp_ns, f.value
FROM packets p
JOIN field_42 f USING (packet_id);
```

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` | Required. Output `.duckdb` file, created lazily on the first `OnPacket()` call |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithCancellationToken(token)` | Enable cooperative cancellation |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithMaxPacketsPerBlock(count)` | Flush (and commit) the in-memory batch after N packets |
| `WithMaxBlockSize(bytes)` | Flush the in-memory batch by estimated size threshold |
| `WithDetailFlags(flags)` | Choose optional columns (info, frame bytes, custom representation/text, topology) |
| `WithTimestampSorted(sorted)` | Declares packets arrive in non-decreasing timestamp order |
| `WithMemoryLimit(bytes)` | DuckDB `memory_limit` in bytes (`> 0`). Default: DuckDB's own default (no `SET` issued) |
| `WithThreads(threads)` | DuckDB `threads` (`> 0`). Default: DuckDB's own default (no `SET` issued) |

`WithMemoryLimit` and `WithThreads` throw `ArgumentOutOfRangeException` for zero or negative
values. Leave them uncalled to keep DuckDB's defaults.

## Write Performance

- Every `WriteBatch` call wraps appends in a single explicit `BEGIN`/`COMMIT` transaction.
- All data tables use `DuckDBAppender` — never row-at-a-time `INSERT`.
- `CHECKPOINT` runs exactly once at the end of `OnFinish()`.
- `threads` and `memory_limit` are set once per connection, and only when configured through
  `WithThreads` / `WithMemoryLimit`; otherwise DuckDB's own defaults apply.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- If no packets are ever added, no database file is created.
- Call `OnFinish()` (or dispose) to release the file handle.

## License

[MIT License](../LICENSE)
