<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# PBF Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`PbfExporter` writes parsed packets to the NetworkInspector Packet Binary Format (`.pbf`, magic
`NETWORK-INSPECTOR-PBF-FORMAT-v1`): a compact, protobuf-based file for storing large parse results and reading
them back fast. It is a packet exporter (`IPacketListener`): the hierarchy shape (default) needs packets with a field tree
(`FieldTreeMode.Build`); the flat shapes also take the tree-free parse of `ParseDemand` (see Export shapes).

## Highlights

- **Compact.** Typed binary values, LZ4 block compression, and delta-encoded ids and timestamps.
- **Two layouts.** `Standard` is packet-oriented; `Columnar` stores one typed column block per field for
  analytical post-processing.
- **Indexed.** The trailer holds a block index (timestamp range per block) and one field-presence bitmap for the whole file,
  so readers can skip blocks by time and know which fields occur without decoding.
- **Same columnar model and export content as Parquet and DuckDB** (`WithContent(ExportContent)`).

## Layouts

| Layout | Purpose |
| --- | --- |
| `PbfExportFormat.Standard` (default) | Sequential packet records with typed payloads |
| `PbfExportFormat.Columnar` | Packet ids, timestamps, topology, and one field block per field id |

A `Columnar` block contains:

- packet ids and timestamps (delta `sint64`), optional info strings, frame bytes, and additional buffers (the last two are off by
  default, see Frame bytes and Additional buffers),
- topology rows (`packet_id`, `node_id`, `field_id`, `parent_node_id`, and with `CustomText` the presence-encoded `custom_text`
  column, field 5 of the topology message) in the hierarchy shape,
- one field block per distinct field id, with typed value columns and optional custom representation and text.

Field numbers are defined in [PbfFieldNumbers.cs](PbfFieldNumbers.cs).

### Field values

A reader decodes the value of a field by the field type the block names (field 4 in a record, field 2 in a column). Every address
is an unsigned integer, the big-endian reading of the address bytes, so the numeric order is the address order; all fixed-width
values are little-endian, as protobuf fixes them.

| Field type | `Standard` record | `Columnar` column |
| --- | --- | --- |
| `Bool`, `U64` | field 5 varint | field 5 (`Bool`) or 7 (`U64`), one varint per row |
| `I64`, `Timestamp` | field 5 zigzag varint | field 6 or 9, one zigzag varint per row |
| `F64` | field 5 fixed64 | field 8, one fixed64 per row |
| `String` | field 5 length-delimited | field 12, one string per row |
| `Bytes` | field 5 length-delimited | field 10, one length-delimited entry per row |
| `IPv4Address` | field 5 fixed32: the address as a number (`192.168.1.1` is `0xC0A80101`) | field 19: one packed field of fixed32 values, 4 bytes per row |
| `MacAddress` | field 5 varint: the 48-bit address | field 20: one packed field of fixed64 values, 8 bytes per row |
| `Eui64` | field 5 fixed64 | field 20, as for a MAC address |
| `IPv6Address`, `Uuid` | field 5 fixed64 (high 64 bits) and field 11 fixed64 (low 64 bits) | field 20 (high 64 bits) and field 21 (low 64 bits): two packed fields of fixed64 values with one entry per row, in the same row order |

A reader needs both halves of a 128-bit value. In the `Standard` layout a value that equals the previous occurrence's is left out
with its same-flag, which covers both halves. The conversion of every type to its display text is in the
[exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line). The format version is **1**; the value encodings
are the integer addresses (above) and the presence-encoded text columns (see Texts).

### Texts

A custom representation and a custom text are written only when the dissector set one; a default text is never written. A reader
rebuilds a display line with the rule of the [exporter guide](../EXPORTER_GUIDE.md#123-rebuilding-the-display-line) (the custom
text alone when set; else the UI name for a node without a value; else UI name, `: `, and the representation or the default text of
the value). With `CustomText = false` or `CustomRepresentation = false` the member is not written at all.

- **Standard:** field 6 (representation) and field 7 (text) of a field record exist only for a set text; an empty text is a
  zero-length field. A text that equals the previous occurrence's is left out and signalled by its same-flag; an absent text is
  never flagged. The text of a structure node (a layer) is field 7 of its record.
- **Columnar:** the representation column (field 15) and the text column (field 18) of a field block are presence-encoded
  sub-messages that list the block's rows in order: sub-field 1 is one row with a text (also an empty one), sub-field 2 is the
  number of consecutive rows without a text (a varint, at least 1). `2:2, 1:"a", 1:"", 2:1` is five rows: two without a text,
  `a`, an empty text, one without. A column in which no row has a text is not written, so a missing column means "every row
  absent". The text of a structure node (a layer) is the presence-encoded text column of the topology message (field 5,
  in the same encoding and in topology row order): a value node and a node whose dissector set no text are absent rows. The
  string-value column (field 12) keeps one entry per row, a missing string as an empty entry.

### Frame bytes

The frame bytes are off by default: the field tree already holds the parsed bytes of every layer, so a copy of every frame is
written only on request (`ExportContent.FrameBytes = true`, CLI `pbf:frames=true`). Both layouts write them:

- **Standard:** packet field 6 of the packet record, length-delimited bytes holding the exact frame. An empty frame is a
  zero-length field, so the field is present in every packet record of an export that asked for frames and in none of an export
  that did not. The packet record's field numbers are 1 id, 2 timestamp, 3 info, 4 same-flags, 5 field records (repeated), 6 frame, 7 additional buffers (repeated).
- **Columnar:** the frame column (field 13 of the block payload) repeats sub-field 1 (bytes) once per packet.

The frame is part of the block, so it counts toward the block size limit (`WithMaxBlockSize`).

### Additional buffers

Protocols attach bytes to a packet that the field tree shows only as parsed values: the datagram an IPv4 or IPv6 fragment completes,
the PDU a TCP stream completes, a decoded (dechunked or inflated) HTTP body, an unmasked WebSocket payload. They are the packet's
additional buffers (index 1 to 255; index 0 is the frame). They are off by default and written only on request
(`ExportContent.Buffers = true`, CLI `pbf:buffers=true`). Both layouts write them; the field records are the same with the member on
and off.

- **Standard:** packet field 7 of the packet record, repeated once per additional buffer in ascending index order. Each is a
  sub-message: sub-field 1 the buffer index (varint), sub-field 2 the bytes (length-delimited; zero length for a bound empty
  buffer). A packet without additional buffers has no field 7.
- **Columnar:** the block field 16, one sub-message with three parallel columns, one entry per buffer of the block in packet order
  and ascending index: sub-field 1 the owning packet ids (a sub-message with the first id as sub-field 1 and the differences to
  the previous id as sub-field 2, both zigzag varints, like the packet id column), sub-field 2 the indexes (a repeated varint),
  sub-field 3 the bytes (repeated, zero length for an empty buffer). A block whose packets have no additional buffer has no field
  16.

The buffers count toward the block size limit (`WithMaxBlockSize`), so the limit bounds the memory they take. A buffer can hold
decoded content that the frame does not show in clear and can be much larger than the frames. Decision record:
[export-packet-bytes-opt-in](../../docs/decisions/export-packet-bytes-opt-in.md).

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Pbf;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WritePbf(IEnumerable<Packet> packets)
{
    using PbfExporter exporter = PbfExporter.CreateBuilder()
        .ToFile("packets.pbf")
        .WithFormat(PbfExportFormat.Columnar)
        .Build(); // compressed, with trailer index

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

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` / `ToTarget` | Output target (`ToTarget` takes a host `IExportTarget` and writes the one entry `Main`) | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithFormat(format)` | `Standard` or `Columnar` | `Standard` |
| `WithCompressed(bool)` | LZ4 block compression | `true` |
| `WithMaxPacketsPerBlock(int)` | Flush a block after N packets (1 – `int.MaxValue`) | 50 000 |
| `WithMaxBlockSize(long)` | Flush a block at this estimated size (1 – `long.MaxValue`) | 16 MiB |
| `WithWriteMode(ExportWriteMode)` | `Background` writer thread or `Synchronous` writes on the caller thread | `Background` |
| `WithTrailerIndex(bool)` | Block index in the trailer | `true` |
| `WithContent(ExportContent)` | Shape, selected fields, and optional members (info, frame bytes (default off), additional buffers (default off), custom texts) | `DefaultContent` |
| `WithTimestampSorted(bool)` | Declare non-decreasing timestamps (metadata) | `false` |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`ExportContent` lives in `NetworkInspector.Exporters`: `Shape`, `Fields`, `Info`, `FrameBytes`, `Buffers`, `CustomRepresentation`,
`CustomText`. The topology is written only for `ExportShape.Hierarchy`.

### Export shapes and the file header

The file header carries the format version (**1**, unchanged by the shape code and the other 0.20.0 layout changes) and the shape
code, which a reader checks first:

| Shape | Header shape code | Position column of a field block | Parse |
| --- | --- | --- | --- |
| `Hierarchy` | 0 (also: field absent, a file of 0.19.0) | node id | field tree |
| `SelectedFields` | 1 | occurrence number | tree-free |
| `AllFields` | 2 | occurrence number | tree-free |

Both layouts support all three shapes. In the `Standard` layout the hierarchy shape keeps the nested packet records; the flat
shapes write, per packet, a flat list of field records (field id, name / UI name / type on the first presence in the block,
`occurrence` when it is not 0, the value, and the texts) with no nested children. Same-as-previous compression applies to the
flat records too: a repeated value is flagged and omitted. Both layouts take the same content (frame bytes included) in either
order of `WithFormat` and `WithContent`, and a builder without `WithContent` writes the same default content in both. A selected
field that no packet had has no field block or record. The exporter states its parse need as `IParseDemandSource.ParseDemand`
(field tree for the hierarchy, a tree-free parse with the exporter's observer for the flat shapes); tree packets are accepted
for every shape and give the same blocks.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread. In the default `Background` write mode one writer
  thread ("PBF export writer") serializes, compresses (LZ4), and writes each full block while the caller fills the next
  one. This also holds for `ToStream` and `ToStdout`: **do not read, write, or dispose that stream until `OnFinish`
  returned**; `OnFinish` waits for every block and writes the trailer, so the complete file is in the stream then. Use
  `Synchronous` to keep all writes on the calling thread.
- A block that fails to write is reported one block later in background mode (at the latest by `OnFinish`) and by the
  completing call in `Synchronous` mode; its packets leave `WrittenCount` and the trailer's packet count.
- Blocks of 64 bytes or less, and blocks that do not shrink, are stored uncompressed.
- The field-presence bitmap covers 32 768 field ids (0 to 32 767).
- In the hierarchy shape a packet without a field tree is skipped: it is counted as skipped and reported through `ItemSkipped`
  as `ExportErrorKind.SerializationError` with the message `Skip packets cannot be exported (HasFieldTree is false).`, by an
  explicit check without an exception, as in the Parquet and DuckDB exporters. In the flat shapes such a packet is written when
  this exporter's observer staged its fields during the parse.
- A packet that fails while it is added to a standard block leaves the block unchanged; the next packet is written in
  full (no same-as-previous flags refer to the failed packet).
- A file that cannot be created is reported through `ItemSkipped` and counted as an error; `OnPacket()` returns `false`
  and does not throw.
- A standard block whose field tree was cut at the maximum nesting depth is written in `Tolerant` mode (one
  `MalformedData` event). In `Strict` mode the block is dropped: its packets count as skipped, not as written, and the
  trailer does not count them.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
