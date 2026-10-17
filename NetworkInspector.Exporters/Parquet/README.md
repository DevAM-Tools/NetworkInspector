<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Parquet Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`ParquetExporter` writes parsed packets as a small relational dataset of Apache Parquet files that DuckDB,
pandas, Polars, Spark, and other Parquet tools query directly. It is a packet exporter (`IPacketListener`):
packets must be parsed with a field tree (`FieldTreeMode.Build`, the default).

## Highlights

- **No custom reader needed.** Any Parquet-aware tool opens the dataset.
- **Narrow tables.** One file per logical table and one file per field id, instead of one wide, mostly empty table.
- **Native types**, including unsigned 64-bit (`UINT_64`) and 16-byte blobs for IPv6 and UUID.
- **Self-describing.** A field catalog and a protocol table name every value field and its protocol without the
  dissector assemblies.
- **Clean re-exports.** Exporting into an existing directory removes the previous dataset files first, so no
  orphan field files remain.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Parquet;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteParquet(IEnumerable<Packet> packets)
{
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
}
```

Query it with DuckDB (look up the field id in `catalog.parquet` first):

```sql
SELECT c.name, c.field_id FROM 'packets_dataset/catalog.parquet' c WHERE c.name = 'udp.dstport';

SELECT p.packet_id, p.timestamp_ns, f.value
FROM 'packets_dataset/packets.parquet' p
JOIN 'packets_dataset/fields/field_42.parquet' f USING (packet_id);
```

## Directory Layout

| File | Contents |
| --- | --- |
| `packets.parquet` | `packet_id` (INT32), `timestamp_ns` (INT64), optional `info`, `frame_bytes` |
| `topology.parquet` | `packet_id`, `node_id`, `field_id`, `parent_node_id` (−1 at the top level) |
| `catalog.parquet` | `field_id`, `name`, `ui_name`, `field_type`, `protocol_id`, `table_name`, `description` |
| `protocols.parquet` | `protocol_id`, `name`, `ui_name` for every protocol in the stack |
| `fields/field_{id}.parquet` | `packet_id`, `node_id`, `value`, optional `custom_repr`, `custom_text` |

- The value column of a `FieldType.Timestamp` field is named `value_timestamp_ns`.
- The catalog has one row per field that carries a value. Protocol and container nodes appear in the topology but
  not in the catalog.
- `topology.parquet` is written only with `ColumnarDetailFlags.IncludeTopology` and a non-empty topology. Empty
  catalog and protocol files are omitted.
- Each table file gets one row group per flushed batch. The protocols file is written at the start, the catalog at
  the end. New catalog columns are only ever appended, so positional readers stay valid.

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToDirectory(path)` | Output directory, created on the first packet | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithMaxPacketsPerBlock(int)` | Flush a row group after N packets | 50 000 |
| `WithMaxBlockSize(long)` | Flush a row group at this estimated size | 16 MiB |
| `WithDetailFlags(ColumnarDetailFlags)` | Optional columns (info, frame bytes, custom texts, topology) | `All` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps | `false` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`ColumnarDetailFlags` lives in `NetworkInspector.Exporters.Columnar`.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- There is no stream or stdout target; the output is always a directory.
- If no packets arrive, no directory is created. The first packet creates the directory and clears old dataset
  files, even when that packet is then skipped.
- Do not run the exporter on a thread with a `SynchronizationContext` (a UI thread). Parquet.Net has only an
  asynchronous writer and `IPacketListener` is synchronous, so the exporter waits in one place with
  `ConfigureAwait(false).GetAwaiter().GetResult()`.

## Links

- [Exporters overview](../README.md)
- [DuckDB exporter](../../NetworkInspector.Exporters.DuckDb/README.md) (same data in one `.duckdb` file)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
