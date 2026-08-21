<!-- Copyright © 2026 DevAM. All rights reserved. -->

# Parquet Exporter

Columnar packet exporter that writes a directory of Apache Parquet files for analytics workloads
(DuckDB, pandas, Spark, Polars, etc.).

## What This Is

The `ParquetExporter` serializes parsed packets through `IPacketListener` into a small relational
dataset of Parquet files, built on the same `ColumnarPacketBatch` accumulator shared by the PBF
columnar format and the DuckDB exporter.

**Overwrite semantics:** exporting into an existing directory clears prior `packets` /
`topology` / `catalog` parquet files and all `fields/*.parquet` artifacts before writing, so
re-exports do not leave orphan field files from a previous FieldId set.

## Why Use It

- Directly queryable by any Parquet-aware tool without a custom reader.
- One file per logical table (packets, topology, catalog, one per distinct field ID) keeps schemas
  narrow and avoids a single wide, mostly-null table.
- Native typed columns (including `UINT_64` for `FieldType.U64` and 16-byte blobs for IPv6/UUID).

## Directory Layout

| File | Contents |
| --- | --- |
| `packets.parquet` | One row per packet: `packet_id` (INT32 / Core `PacketId`), `timestamp_ns`, optional `info` / `frame_bytes` |
| `topology.parquet` | One row per field-tree node: `packet_id`, `node_id`, `field_id`, `parent_node_id` |
| `catalog.parquet` | One row per distinct field ID observed: `field_id`, `name`, `ui_name`, `field_type`, `protocol_id`, `table_name` |
| `fields/field_{id}.parquet` | One row per field occurrence: `packet_id`, `node_id`, type-specific `value`, optional `custom_repr` / `custom_text` |

Each table file is written incrementally: one row group per flushed batch, closed once in
`OnFinish()`. The catalog file is written once at the end.

## Quick Start

```csharp
using NetworkInspector.Exporters.Parquet;

using ParquetExporter exporter = ParquetExporter.CreateBuilder()
    .ToDirectory("packets_dataset")
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
FROM 'packets_dataset/packets.parquet' p
JOIN 'packets_dataset/fields/field_42.parquet' f USING (packet_id);
```

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToDirectory(path)` | Required. Output directory, created lazily on the first `OnPacket()` call |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithCancellationToken(token)` | Enable cooperative cancellation |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithMaxPacketsPerBlock(count)` | Flush the in-memory batch after N packets |
| `WithMaxBlockSize(bytes)` | Flush the in-memory batch by estimated size threshold |
| `WithDetailFlags(flags)` | Choose optional columns (info, frame bytes, custom representation/text, topology) |
| `WithTimestampSorted(sorted)` | Declares packets arrive in non-decreasing timestamp order |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- If no packets are ever added, no directory or files are created.
- `FieldType.U64` is stored as a native Parquet unsigned 64-bit column (`DataField<ulong>` /
  `UINT_64` logical type).
- `Parquet.Net`'s low-level writer API is async-only; this exporter blocks on it synchronously
  (`GetAwaiter().GetResult()`), which is safe here because each write targets a dedicated
  `FileStream` with no captured synchronization context.

## Links

- [Exporters hub](../README.md)
- [Parquet source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Parquet)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
