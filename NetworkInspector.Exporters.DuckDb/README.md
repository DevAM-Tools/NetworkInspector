<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Exporters.DuckDb

[![NuGet][nuget-badge]][nuget]
[![Downloads][downloads-badge]][nuget]
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters.DuckDb?logo=nuget&label=NuGet
[downloads-badge]: https://img.shields.io/nuget/dt/NetworkInspector.Exporters.DuckDb?logo=nuget&label=downloads
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters.DuckDb

Export parsed packets into a single DuckDB database file and query them with SQL right away: no load step, no
schema to write.

This package is separate from `NetworkInspector.Exporters`, so DuckDB's native runtime is only pulled in by
applications that export to DuckDB.

## Highlights

- **SQL immediately.** Open the `.duckdb` file in the DuckDB CLI, DBeaver, Python, or .NET and query packets.
- **Bulk load.** Every table is filled through `DuckDBAppender`, one transaction per batch, one `CHECKPOINT` at
  the end. Never row-at-a-time `INSERT`.
- **Native types.** `U64` fields become `UBIGINT`, timestamps `BIGINT` nanoseconds, strings `VARCHAR`.
- **Self-describing.** `catalog` and `protocols` tables name every value field and its protocol without the
  dissector assemblies.
- **Same columnar model as Parquet and PBF Columnar**, with the same detail flags.
- **Available from the CLI:** `ni export capture.pcapng -o packets.duckdb`.

## Install

```bash
dotnet add package NetworkInspector.Exporters.DuckDb
```

`NetworkInspector.Exporters` comes in as a dependency. To read captures and parse them you also need
`NetworkInspector.Sources` and `NetworkInspector.Protocols`; see the end-to-end example in the
[Exporters README](../NetworkInspector.Exporters/README.md).

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.DuckDb;

// packets: parsed packets with a field tree (FieldTreeMode.Build, the default).
static void WriteDuckDb(IEnumerable<Packet> packets)
{
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
}
```

Then query it:

```sql
-- Which fields were captured, per protocol?
SELECT p.name AS protocol, c.name AS field, c.field_id
FROM catalog c JOIN protocols p USING (protocol_id)
ORDER BY protocol, field;

-- Values of one field (field id from the catalog) next to the packet time
SELECT pk.packet_id, pk.timestamp_ns, f.value
FROM packets pk JOIN field_42 f USING (packet_id);
```

## Table Layout

| Table | Contents |
| --- | --- |
| `packets` | `packet_id INTEGER`, `timestamp_ns BIGINT`, optional `info VARCHAR`, `frame_bytes BLOB` |
| `topology` | `packet_id`, `node_id`, `field_id`, `parent_node_id` (all `INTEGER`) |
| `catalog` | `field_id`, `name`, `ui_name`, `field_type`, `protocol_id`, `table_name`, `description` |
| `protocols` | `protocol_id`, `name`, `ui_name` for every protocol in the stack |
| `field_{id}` | `packet_id`, `node_id`, `value`, optional `custom_repr`, `custom_text` |

Value types of `field_{id}.value`:

| `FieldType` | DuckDB type |
| --- | --- |
| `Bool` | `BOOLEAN` |
| `I64` / `U64` | `BIGINT` / `UBIGINT` |
| `F64` | `DOUBLE` |
| `Timestamp` | `BIGINT` (nanoseconds) |
| `String` | `VARCHAR` |
| addresses, UUID, bytes | `BLOB` (MAC 6, IPv4 4, IPv6 16, EUI-64 8, UUID 16 bytes) |

- `packet_id` matches Core `PacketId`.
- The catalog has one row per field that carries a value; protocol and container nodes appear only in the
  topology. `description` is `NULL` when a field has none.
- `protocols` is filled at the first packet, `catalog` in `OnFinish()`. Field tables are created lazily.
  `topology`, `catalog`, and `protocols` always exist, possibly empty.
- New catalog columns are only ever appended, so positional readers stay valid.
- Exporting to an existing path deletes the old `.duckdb` file and its `.wal` sidecar first: re-exports replace,
  they never append.

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile(path)` | Output `.duckdb` file, created on the first packet | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithMaxPacketsPerBlock(int)` | Flush and commit a batch after N packets | 50 000 |
| `WithMaxBlockSize(long)` | Flush a batch at this estimated size | 16 MiB |
| `WithDetailFlags(ColumnarDetailFlags)` | Optional columns (info, frame bytes, custom texts, topology) | `All` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps | `false` |
| `WithMemoryLimit(long)` | DuckDB `memory_limit` in bytes (`> 0`) | DuckDB default |
| `WithThreads(int)` | DuckDB `threads` (`> 0`) | DuckDB default |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`WithMemoryLimit` and `WithThreads` throw `ArgumentOutOfRangeException` for zero or negative values; without them
no `SET` is issued. `ColumnarDetailFlags` lives in `NetworkInspector.Exporters.Columnar`.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- There is no stream or stdout target.
- If no packets arrive, no database file is created.
- In tolerant mode an unwritable path raises `ItemSkipped` and the export ends cleanly; strict mode aborts.
- Packets parsed with `FieldTreeMode.Skip` are reported as `SerializationError`.
- Call `OnFinish()` (or dispose) to release the file handle.

## Links

- [Exporters overview](../NetworkInspector.Exporters/README.md)
- [Parquet exporter](../NetworkInspector.Exporters/Parquet/README.md) (the same data as Parquet files)
- [CLI](../NetworkInspector.CLI/README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
