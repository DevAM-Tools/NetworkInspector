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
- **Bulk load.** Every table is filled through `DuckDBAppender`, one transaction per batch, and a final `CHECKPOINT`
  folds the log into the file. Never row-at-a-time `INSERT`.
- **Parsing and writing overlap.** A writer thread writes a full batch while the next one fills.
- **Native types.** `U64` fields become `UBIGINT`, timestamps `BIGINT` nanoseconds, strings `VARCHAR`, and addresses unsigned
  integers (`UINTEGER` for IPv4, `UHUGEINT` for IPv6) whose numeric order is the address order: a subnet is a range query.
- **Self-describing.** `catalog` and `protocols` tables name every value field and its protocol without the
  dissector assemblies.
- **Same columnar model as Parquet and PBF Columnar**, configured with the same `ExportContent`.
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
| `packets` | `packet_id INTEGER`, `timestamp_ns BIGINT`, optional `info VARCHAR`, optional `frame_bytes BLOB` (only with `FrameBytes = true`, CLI `duckdb:frames=true`; an empty frame is an empty BLOB, not NULL) |
| `packet_buffers` | `packet_id INTEGER`, `buffer_index UTINYINT`, `bytes BLOB`: one row per additional packet buffer; only with `Buffers = true`, CLI `duckdb:buffers=true` |
| `topology` | `packet_id`, `node_id`, `field_id`, `parent_node_id` (all `INTEGER`), optional `custom_text VARCHAR`; hierarchy shape only |
| `catalog` | `field_id`, `name`, `ui_name`, `field_type`, `protocol_id`, `table_name`, `description` |
| `protocols` | `protocol_id`, `name`, `ui_name` for every protocol in the stack |
| `export_info` | `key`, `value`: the shape marker (`shape`, `fields`, `occurrences`, `layout_version`) |
| `field_{id}` | `packet_id`, `node_id` (hierarchy) or `occurrence` (flat shapes), `value`, optional `custom_repr`, `custom_text` |

Value types of `field_{id}.value`:

| `FieldType` | DuckDB type |
| --- | --- |
| `Bool` | `BOOLEAN` |
| `I64` / `U64` | `BIGINT` / `UBIGINT` |
| `F64` | `DOUBLE` |
| `Timestamp` | `BIGINT` (nanoseconds) |
| `String` | `VARCHAR` |
| `IPv4Address` | `UINTEGER`: the address as a number (`192.168.1.1` is `3232235777`) |
| `MacAddress` | `UBIGINT`: the 48-bit address as a number |
| `Eui64` | `UBIGINT`: the 64-bit identifier as a number |
| `IPv6Address` | `UHUGEINT`: the 128-bit address as a number, `high << 64 \| low` |
| `Uuid` | `UUID` (`value::VARCHAR` gives the text) |
| `Bytes` | `BLOB` |

- `packet_id` matches Core `PacketId`.
- **Addresses are integers**, the big-endian reading of the address bytes, so `ORDER BY value` is the address order and
  `WHERE value BETWEEN 3232235776 AND 3232236031` selects `192.168.1.0/24`. The text forms of the rule below are rebuilt from the
  integers (IPv4 dotted from its four bytes, MAC and EUI-64 as hex pairs, IPv6 from the two 64-bit halves `value >> 64` and
  `value & 0xFFFFFFFFFFFFFFFF`); the conversion of every type is in the
  [exporter guide](../NetworkInspector.Exporters/EXPORTER_GUIDE.md#123-rebuilding-the-display-line). The exporter writes IPv6
  values through a temporary staging table inside the batch transaction (the appender has no allocation-free 128-bit append);
  the staging table is never stored in the file.
- **Additional buffers** (`packet_buffers`, off by default). Protocols attach bytes to a packet that the field tree shows only as
  parsed values: the datagram an IPv4 or IPv6 fragment completes, the PDU a TCP stream completes, a decoded (dechunked or inflated)
  HTTP body, an unmasked WebSocket payload. With `Buffers = true` every such buffer is one row: `buffer_index` is its index (1 to
  255; index 0 is the frame and lives in `packets.frame_bytes`), rows of a packet are in ascending index order, and a packet
  without additional buffers has no row. A bound empty buffer is an empty BLOB, never NULL. The table exists whenever the member
  is on, also when no packet had a buffer. The field tables, the topology, the catalog, and the protocols are the same with the
  member on and off. Buffers can hold decoded content that the frame does not show in clear and can be much larger than the
  frames; `batchBytes` bounds the memory the copies take. Decision record:
  [export-packet-bytes-opt-in](../docs/decisions/export-packet-bytes-opt-in.md).
- The catalog has one row per field id that occurs in the export. Structure nodes (protocols, groups) have
  `field_type = 0` and `table_name` NULL; they appear only when the topology is included. `description` is `NULL`
  when a field has none.
- `topology.custom_text` (only with `ExportContent.CustomText`) holds the custom text of a structure node, for example the
  summary of a protocol layer. It is `NULL` for value nodes, whose text is in their `field_{id}.custom_text`, and for a structure
  node whose dissector set no text.
- **Texts.** `custom_repr` and `custom_text` are `NULL` where the dissector set none and `''` where it set an empty text; a
  default text is never written into them. A reader rebuilds the display line of a field with the rule of the
  [exporter guide](../NetworkInspector.Exporters/EXPORTER_GUIDE.md#123-rebuilding-the-display-line): `custom_text` when it is not
  `NULL`; else the UI name (`catalog.ui_name`) alone for a node without a value; else the UI name, `: `, and `custom_repr`, or
  the default text of `value` when `custom_repr` is `NULL`. With `CustomText = false` or `CustomRepresentation = false` the
  column does not exist.
- `protocols` is filled at the first packet, `catalog` in `OnFinish()`. Field tables are created lazily.
  `catalog`, `protocols`, and `export_info` always exist, possibly empty; `topology` exists only in the hierarchy shape.
- New catalog columns are only ever appended, so positional readers stay valid.
- Exporting to an existing path deletes the old `.duckdb` file and its `.wal` sidecar first: re-exports replace,
  they never append.

## Export Shapes

`WithContent(ExportContent)` chooses what the database contains. The shape decides the parse the exporter needs and what the
second key column of the field tables means:

| Shape | Contents | Second column | Parse |
| --- | --- | --- | --- |
| `Hierarchy` (default) | Every field with its place in the tree, plus the `topology` table | `node_id` | field tree |
| `AllFields` | Every value field, no tree | `occurrence` | tree-free |
| `SelectedFields` | Only the named fields (`FieldSelection`), each with `First`, `Last`, or `All` occurrences | `occurrence` | tree-free |

- `export_info` says which shape the file has: `shape` (`hierarchy`, `all_fields`, `selected_fields`), `fields` (selected
  names joined with `;`), `occurrences` (`name=all|first|last` joined with `;`), and `layout_version` = `1`. Check it
  before you read the field tables.
- For `SelectedFields` every selected field table exists from the start, with the declared type: an empty table means the
  field never occurred, a missing table means a misspelled name. The `catalog` lists every selected field.
- `occurrence` counts the values of a field inside one packet from 0. A tunnel packet (IPv4 in IPv4) holds `ip.src` twice:
  `All` writes occurrences 0 and 1, `First` writes occurrence 0 of the outer address, `Last` writes occurrence 0 holding
  the inner address (one row, the last value).
- The exporter states its parse need as `IParseDemandSource.ParseDemand`. With a tree-free parse built from it, a two-field
  export builds and walks no field tree; tree packets are accepted too and give the same rows.

```csharp
ExportContent content = new()
{
    Shape = ExportShape.SelectedFields,
    Fields = selection,                // FieldSelection.TryCreate(stack, [new FieldRequest("udp.dstport")], Occurrences.All, ...)
};
DuckDbExporter exporter = DuckDbExporter.CreateBuilder().ToFile("dns.duckdb").WithContent(content).Build();
ParseDemand demand = exporter.ParseDemand;   // combine with other demands in a ParsePlan, then parse with plan.Options
```

### Rebuild the field tree

(Hierarchy shape only.)

`node_id` numbers the nodes of one packet from 0 in depth-first pre-order, so a parent's id is smaller than its
children's and siblings sort by `node_id`. Top-level nodes have `parent_node_id = -1`. Value rows join on
`(packet_id, node_id)`; structure nodes (protocols, groups) have no value table (`catalog.table_name` is NULL) and keep
their display text in `topology.custom_text`.

```sql
WITH RECURSIVE tree AS (
    SELECT t.packet_id, t.node_id, t.field_id, 0 AS depth
    FROM topology t WHERE t.packet_id = 0 AND t.parent_node_id = -1
    UNION ALL
    SELECT c.packet_id, c.node_id, c.field_id, p.depth + 1
    FROM topology c JOIN tree p ON c.packet_id = p.packet_id AND c.parent_node_id = p.node_id
)
SELECT repeat('  ', depth) || c.name AS field, tree.node_id
FROM tree JOIN catalog c USING (field_id)
ORDER BY tree.node_id;
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile(path)` | Output `.duckdb` file, created on the first packet | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithMaxPacketsPerBlock(int)` | Flush and commit a batch after N packets (1 – `int.MaxValue`) | 50 000 |
| `WithMaxBlockSize(long)` | Flush a batch at this estimated size (1 – `long.MaxValue`) | 16 MiB |
| `WithWriteMode(ExportWriteMode)` | `Background` writer thread or `Synchronous` writes on the caller thread | `Background` |
| `WithContent(ExportContent)` | Shape, selected fields, and optional columns and tables (info, frame bytes (default off), additional buffers (default off), custom texts) | `DefaultContent` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps | `false` |
| `WithMemoryLimit(long)` | DuckDB `memory_limit` in bytes (`> 0`) | DuckDB default |
| `WithThreads(int)` | DuckDB `threads` (`> 0`) | DuckDB default |
| `WithStorageVersion(DuckDbStorageVersion)` | The storage format of the file, which decides the oldest DuckDB release that opens it (see Storage Version) | `DefaultStorageVersion` = `V1Dot5` (`v1.5.0`) |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`WithMemoryLimit` and `WithThreads` throw `ArgumentOutOfRangeException` for zero or negative values; without them
no `SET` is issued. `ExportContent` and the types around it live in `NetworkInspector.Exporters` (see Export Shapes).

The two flush limits (`WithMaxPacketsPerBlock`, `WithMaxBlockSize`) are **secure by default**: two batches are held in
memory at once (one filling, one being written), so the tight default keeps an export small on any host. Raise them for
fewer, larger transactions when memory allows. The size is an estimate, so the real peak can be higher.

An out-of-range value is refused with a message that names the range. `ExportWriteMode.Synchronous` keeps one batch in
memory and writes it inside the `OnPacket` call that completed it; it starts no thread.

**Error timing.** A batch that fails to write is reported by the next `OnPacket` call that finds it (at the latest by
`OnFinish`) in background mode, and by the call that completed the batch in synchronous mode. Its packets leave
`WrittenCount`, count as skipped, and raise one `ItemSkipped`.

## Storage Version

The exporter sets DuckDB's `storage_compatibility_version` explicitly when it creates the file, so the file format never
follows a library default that changes with a DuckDB.NET upgrade. The version decides the oldest DuckDB release that can open
the file; a reader with an older DuckDB refuses it with a message instead of reading garbage. Newer versions allow DuckDB to
choose newer column encodings, but the effect on the file size depends on the data: 20 000 random UDP/IPv4 packets exported with the
full stack gave 152.1 MB for `v1.2.0` and 153.4 to 153.6 MB for `v1.0.0`, `v1.4.0`, and `v1.5.0` (the largest file is about 1 percent bigger than
the smallest; one sample, not a promise). The newest version is the default so that the format is chosen by the exporter and is the one the
bundled DuckDB writes natively; choose an older one for an older reader.

| Enum member | Setting text (CLI `storageVersion=`) | Opens in |
| --- | --- | --- |
| `V1Dot0` | `v1.0.0` | DuckDB 1.0 and later |
| `V1Dot1` | `v1.1.0` | DuckDB 1.1 and later |
| `V1Dot2` | `v1.2.0` | DuckDB 1.2 and later |
| `V1Dot3` | `v1.3.0` | DuckDB 1.3 and later |
| `V1Dot4` | `v1.4.0` | DuckDB 1.4 and later |
| `V1Dot5` (default) | `v1.5.0` | DuckDB 1.5 and later |

A reader sees the version in `SELECT tags['storage_version'] FROM duckdb_databases() WHERE NOT internal`: DuckDB reports the
oldest version that has the format of the file, followed by `+` (`v1.2.0+` for a `v1.2.0` file). `v1.0.0` and `v1.1.0` write
the same file format, so both are reported as `v1.0.0+`; every later version has a format of its own.

```csharp
DuckDbExporter exporter = DuckDbExporter.CreateBuilder()
    .ToFile("packets.duckdb")
    .WithStorageVersion(DuckDbStorageVersion.V1Dot2)   // a DuckDB 1.2 reader can open the file
    .Build();
```

`WithStorageVersion` throws `ArgumentOutOfRangeException` for an undefined enum value (a caller bug) and the message lists the
six texts. The CLI refuses a text outside the table with the allowed values and creates no file. The export has no
compaction or copy option: it only appends rows, a compaction pass would gain little and add a failure point at the end, so
the export ends with the catalog write and `CHECKPOINT`. The setting is a connection setting that DuckDB fixes when the
database file is created; the exporter always creates a fresh file.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread. Internally one writer thread ("DuckDB export
  writer") writes the full batches while the caller's thread fills the next one; counters and `ItemSkipped` stay on the
  caller's thread.
- There is no stream or stdout target.
- If no packets arrive, no database file is created.
- In tolerant mode an unwritable path raises `ItemSkipped` and the export ends cleanly; strict mode aborts.
- In the hierarchy shape a packet parsed without a field tree (`FieldTreeMode.Skip`) is skipped and counted: `ItemSkipped`
  carries `ExportErrorKind.SerializationError` and the message `Skip packets cannot be exported (HasFieldTree is false).`
  In the flat shapes such a packet is written when this exporter's observer staged its fields during the parse (see Export
  Shapes); a packet with neither a tree nor staged fields is skipped with `SerializationError` and a message that names the
  cause.
- A packet that cannot be added to a batch leaves the batch unchanged and is skipped. When writing a batch fails, its
  transaction is rolled back and the batch is dropped: its packets count as skipped, not as written, and one error is
  raised. Field tables created by the rolled-back batch are created again by the next batch.
- A batch is written after `OnPacket()` returned for its last packet, so a failing batch is reported by a later
  `OnPacket()` call (at the latest by `OnFinish()`), not by the call that completed it. Until then `WrittenCount` may
  include packets that are still being written or that failed. In strict mode that later call returns `false`. This also
  applies to the last batch written by `OnFinish()`; the catalog of the earlier batches is still written.
- The database path is passed to DuckDB as one value, so characters such as `;` or quotes in a path stay part of the
  file name and cannot change connection options.
- Call `OnFinish()` (or dispose) to release the file handle.

## Links

- [Exporters overview](../NetworkInspector.Exporters/README.md)
- [Parquet exporter](../NetworkInspector.Exporters/Parquet/README.md) (the same data as Parquet files)
- [CLI](../NetworkInspector.CLI/README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
