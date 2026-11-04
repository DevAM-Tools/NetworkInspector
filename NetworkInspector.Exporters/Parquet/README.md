<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Parquet Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`ParquetExporter` writes parsed packets as a small relational dataset of Apache Parquet files that DuckDB,
pandas, Polars, Spark, and other Parquet tools query directly. It is a packet exporter (`IPacketListener`):
packets must carry a field tree (`FieldTreeMode.Build`, the default) for the hierarchy shape; the flat shapes also accept the
tree-free parse of `ParseDemand` (see Export Shapes).

## Highlights

- **No custom reader needed.** Any Parquet-aware tool opens the dataset.
- **Narrow tables.** One file per logical table and one file per field id, instead of one wide, mostly empty table.
- **Native types**, including unsigned 64-bit (`UINT_64`) and addresses as unsigned integers (`UINT_32` for IPv4, two
  `UINT_64` halves for IPv6 and UUID) whose numeric order is the address order.
- **Small files by default.** Every column chunk of every file is compressed with Zstandard at the optimal level, set
  explicitly by the exporter (never the library default), and `WithCompression` picks another codec or level.
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

<!-- Sync: ParquetBatchSink summary -->

| File | Contents |
| --- | --- |
| `packets.parquet` | `packet_id` (INT32), `timestamp_ns` (INT64), optional `info`, optional `frame_bytes` (only with `FrameBytes = true`, CLI `parquet:frames=true`; an empty frame is an empty byte array) |
| `packet_buffers.parquet` | `packet_id` (INT32), `buffer_index` (UINT_8), `bytes` (BYTE_ARRAY): one row per additional packet buffer; only with `Buffers = true`, CLI `parquet:buffers=true`, and only when a packet had one |
| `topology.parquet` | `packet_id`, `node_id`, `field_id`, `parent_node_id` (−1 at the top level), and with `CustomText` the nullable string `custom_text` (the display line of a node without a value); hierarchy shape only |
| `export_info.parquet` | `key`, `value`: the shape marker (`shape`, `fields`, `occurrences`, `layout_version`) |
| `catalog.parquet` | `field_id`, `name`, `ui_name`, `field_type`, `protocol_id`, `table_name`, `description` |
| `protocols.parquet` | `protocol_id`, `name`, `ui_name` for every protocol in the stack |
| `fields/field_{id}.parquet` | `packet_id`, `node_id` (hierarchy) or `occurrence` (flat shapes), `value`, optional `custom_repr`, `custom_text` |

- The value column of a `FieldType.Timestamp` field is named `value_timestamp_ns`.
- **Additional buffers** (`packet_buffers.parquet`, off by default). Protocols attach bytes to a packet that the field tree shows only
  as parsed values: the datagram an IPv4 or IPv6 fragment completes, the PDU a TCP stream completes, a decoded (dechunked or
  inflated) HTTP body, an unmasked WebSocket payload. With `Buffers = true` every such buffer is one row: `buffer_index` is its
  index (1 to 255; index 0 is the frame and lives in `packets.frame_bytes`), rows are in packet order and ascending index, and a
  packet without additional buffers has no row. A bound empty buffer is an empty byte array, never null. Like every optional table
  file, `packet_buffers.parquet` is created with its first row, so a dataset whose packets had no additional buffer has no such
  file. The field files, the topology, the catalog, and the protocols are the same with the member on and off. Buffers can hold
  decoded content that the frame does not show in clear and can be much larger than the frames; `batchBytes` bounds the memory
  the copies take. Decision record: [export-packet-bytes-opt-in](../../docs/decisions/export-packet-bytes-opt-in.md).
- **Field files** (`value` per field type):

  | `FieldType` | Parquet column |
  | --- | --- |
  | `Bool`, `F64`, `String` | `BOOLEAN`, `DOUBLE`, `BYTE_ARRAY` (UTF-8) |
  | `I64`, `U64` | `INT64`, `UINT_64` |
  | `Timestamp` | `INT64` nanoseconds, named `value_timestamp_ns` |
  | `IPv4Address` | `UINT_32`: the address as a number (`192.168.1.1` is `3232235777`) |
  | `MacAddress`, `Eui64` | `UINT_64`: the address as a number (a MAC address uses the low 48 bits) |
  | `IPv6Address`, `Uuid` | `value_hi` and `value_lo`, both `UINT_64`, and no `value` column: the high and the low 64 bits |
  | `Bytes` | `BYTE_ARRAY` |

  An address integer is the big-endian reading of the address bytes, so sorting by `value` (or by `value_hi`, then `value_lo`)
  is the address order, and a subnet is a range. Parquet has no 128-bit integer and the writer library writes no raw 16-byte
  column, which is why the 128-bit types have two columns; always read both halves (`value_hi` alone is the `/64` prefix of an
  IPv6 address). The conversion of every type to its text is in the
  [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line).
- The catalog has one row per field id that occurs in the export. Structure nodes (protocols, groups) have
  `field_type = 0` and `table_name` NULL; they appear only when the topology is included. A field that has a value in
  one batch and appears only as a structure node in another keeps its row with a `table_name`.
- Custom text of structure nodes (a protocol layer, a group) is `topology.custom_text`: null where the node has a value or its
  dissector set no text, the empty string where it set an empty one. A reader rebuilds the line of a structure node with the same
  rule as a value field: `custom_text` when it is not null, else the UI name (`catalog.ui_name`). The column exists only with
  `CustomText = true`.
- **Texts.** `custom_repr` and `custom_text` are null where the dissector set none and the empty string where it set an empty
  text; a default text is never written into them. A reader rebuilds the display line of a value field with the rule of the
  [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line): `custom_text` when it is not null; else the UI name
  (`catalog.ui_name`), `: `, and `custom_repr`, or the default text of `value` when `custom_repr` is null. With
  `CustomText = false` or `CustomRepresentation = false` the column does not exist.
- `topology.parquet` is written only in the hierarchy shape and with a non-empty topology. Empty
  catalog and protocol files are omitted.
- Each table file gets one row group per flushed batch. The protocols file is written at the start, the catalog at
  the end. New catalog columns are only ever appended, so positional readers stay valid.

## Export Shapes

`WithContent(ExportContent)` chooses what the dataset contains. The shape decides the parse the exporter needs and what the
position column of the field files means:

| Shape | Contents | Position column | Parse |
| --- | --- | --- | --- |
| `Hierarchy` (default) | Every field with its place in the tree, plus `topology.parquet` | `node_id` | field tree |
| `AllFields` | Every value field, no tree | `occurrence` | tree-free |
| `SelectedFields` | Only the named fields (`FieldSelection`), each with `First`, `Last`, or `All` occurrences | `occurrence` | tree-free |

- `export_info.parquet` is written first and says which shape the dataset has (`shape` = `hierarchy`, `all_fields`, or
  `selected_fields`), the selected names, the occurrence choice per name, and `layout_version` = `1`. A reader checks it
  before it reads a field file.
- A selected field that no packet had has no file ("no file = never present"): an empty Parquet file needs a row group to
  be useful to readers. The catalog lists every selected field, also the absent ones.
- The exporter states its parse need as `IParseDemandSource.ParseDemand`. With a tree-free parse built from it, a
  two-field export builds and walks no field tree; tree packets are accepted too and give the same files.

```csharp
ExportContent content = new()
{
    Shape = ExportShape.SelectedFields,
    Fields = selection,                // FieldSelection.TryCreate(stack, [new FieldRequest("ip.src")], Occurrences.All, ...)
    Info = false,
};
ParquetExporter exporter = ParquetExporter.CreateBuilder().ToDirectory("dataset").WithContent(content).Build();
ParseDemand demand = exporter.ParseDemand;   // combine with other demands in a ParsePlan, then parse with plan.Options
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToDirectory(path)` | Output directory, created on the first packet | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithMaxPacketsPerBlock(int)` | Flush a row group after N packets (1 – `int.MaxValue`) | 50 000 |
| `WithMaxBlockSize(long)` | Flush a row group at this estimated size (1 – `long.MaxValue`) | 16 MiB |
| `WithWriteMode(ExportWriteMode)` | `Background` writer thread or `Synchronous` writes on the caller thread | `Background` |
| `WithContent(ExportContent)` | Shape, selected fields, and optional columns and files (info, frame bytes (default off), additional buffers (default off), custom texts) | `DefaultContent` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps | `false` |
| `WithCompression(ParquetCompression, ParquetCompressionLevel)` | The codec and the level of every column chunk of every file (see Compression) | `Zstd`, `Optimal` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`ExportContent` and the types around it live in `NetworkInspector.Exporters` (see Export Shapes).

## Compression

The exporter sets the codec and the level on every writer of the dataset (`packets`, `packet_buffers`, `topology`, `catalog`,
`protocols`, `export_info`, and every field file), so the dataset never changes when Parquet.Net changes its own default.
Without `WithCompression` the files use Zstandard at the optimal level.

| Enum member | Option text (CLI `compression=`) | Parquet codec a reader reports | Notes |
| --- | --- | --- | --- |
| `None` | `none` | `UNCOMPRESSED` | Largest files, fastest write, the reader needs no codec |
| `Snappy` | `snappy` | `SNAPPY` | Fast, light; supported by the common tools; no levels |
| `Gzip` | `gzip` | `GZIP` | Usually smaller than Snappy, slower to write; supported by the common tools |
| `Brotli` | `brotli` | `BROTLI` | Small, slow to write; not every tool reads it |
| `Lz4` | `lz4` | `LZ4_RAW` | Fast, light compression in the class of Snappy; written as the raw codec of the Parquet specification (the framed `LZ4` codec is deprecated there); older tools may not know it |
| `Zstd` (default) | `zstd` | `ZSTD` | A good balance of size and write time for most data; supported by current tools (DuckDB, Arrow, Spark) |

| Level member | Option text (CLI `compressionLevel=`) | Effect |
| --- | --- | --- |
| `Fastest` | `fastest` | Quickest write, largest files of the codec |
| `Optimal` (default) | `optimal` | The codec's balance |
| `Smallest` | `smallest` | Smallest files of the codec, slowest write |

The level applies to the codecs that have levels (for example Gzip, Brotli, Zstd); `None` and `Snappy` ignore it.

```csharp
ParquetExporter exporter = ParquetExporter.CreateBuilder()
    .ToDirectory("packets_dataset")
    .WithCompression(ParquetCompression.Snappy)   // supported by the common tools
    .Build();
```

A reader sees the codec with `SELECT DISTINCT file_name, compression FROM parquet_metadata('packets_dataset/**/*.parquet')`.
`WithCompression` throws `ArgumentOutOfRangeException` for an undefined enum value (a caller bug) and the message lists the
allowed values; the CLI refuses an option text outside the tables with the allowed values and creates no dataset.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread. In the default `Background` write mode one writer
  thread ("Parquet export writer") encodes and writes each full row group while the caller fills the next batch; counters
  and `ItemSkipped` stay on the caller's thread. A failed batch is reported by the next `OnPacket` call that finds it (at
  the latest by `OnFinish`) in background mode and by the completing call in `Synchronous` mode.
- There is no stream or stdout target; the output is always a directory.
- If no packets arrive, no directory is created. The first packet creates the directory and clears old dataset
  files, even when that packet is then skipped.
- A hierarchy packet without a field tree is skipped and counted: `ItemSkipped` carries `ExportErrorKind.SerializationError`
  and the message `Skip packets cannot be exported (HasFieldTree is false).`
- A packet that cannot be added to a batch leaves the batch unchanged and is skipped. When writing a batch fails, the
  whole batch is dropped (it is never written a second time): its packets count as skipped, not as written, and one
  error is raised. The tables of that batch may hold partial row groups, because Parquet files cannot be rolled back.
  The catalog lists the fields of a failed batch too.
- Do not run the exporter on a thread with a `SynchronizationContext` (a UI thread) in `Synchronous` mode, or for the file
  creation and the catalog in either mode; the row groups of `Background` mode are written on the exporter's own thread.
  Parquet.Net has only an
  asynchronous writer and `IPacketListener` is synchronous, so the exporter waits in one place with
  `ConfigureAwait(false).GetAwaiter().GetResult()`.

## Links

- [Exporters overview](../README.md)
- [DuckDB exporter](../../NetworkInspector.Exporters.DuckDb/README.md) (same data in one `.duckdb` file)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
