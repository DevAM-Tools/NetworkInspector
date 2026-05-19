<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# PBF Exporter (Packet Binary Format)

The PBF exporter serializes parsed packets to a compact binary format based on Protocol Buffers encoding. It implements `IPacketListener` and supports two block layouts (Standard and Columnar), optional LZ4 compression, and trailer indexing.

## File Structure

```
┌─────────────────────────────────────────┐
│  Magic (44 bytes)                       │  "NETWORK-INSPECTOR-PBF-FORMAT-v1" + 13 null bytes
├─────────────────────────────────────────┤
│  Header (length-prefixed protobuf)      │  [4-byte LE length] + [protobuf data]
├─────────────────────────────────────────┤
│  Block 0                                │  [1B flags] + [4B original size LE] + [4B stored size LE] + [data]
├─────────────────────────────────────────┤
│  Block 1                                │
├─────────────────────────────────────────┤
│  ...                                    │
├─────────────────────────────────────────┤
│  Block N                                │
├─────────────────────────────────────────┤
│  Trailer (protobuf data)                │  Summary stats + field bitmap + block index
├─────────────────────────────────────────┤
│  Trailer Size (4 bytes LE)              │  Size of the trailer in bytes
├─────────────────────────────────────────┤
│  Magic (44 bytes)                       │  Same magic as header (for reverse scanning)
└─────────────────────────────────────────┘
```

## Magic Bytes

```
4E 45 54 57 4F 52 4B 2D 49 4E 53 50 45 43 54 4F  NETWORK-INSPECTO
52 2D 50 42 46 2D 46 4F 52 4D 41 54 2D 76 31 00  R-PBF-FORMAT-v1.
00 00 00 00 00 00 00 00 00 00 00 00               ............
```

44 bytes total. Appears at both the start and end of the file, enabling bidirectional detection.

## Header

Length-prefixed protobuf message:

| Field # | Wire Type | Name                | Description                              |
|---------|-----------|---------------------|------------------------------------------|
| 1       | Varint    | version             | Format version (currently 1)             |
| 2       | Varint    | creation_timestamp  | Creation time (nanoseconds since epoch, sint64) |

## Block Format

Each block starts with a fixed 9-byte header:

| Offset | Size | Description                                           |
|--------|------|-------------------------------------------------------|
| 0      | 1    | Flags (bit 0 = compressed)                            |
| 1      | 4    | Original (uncompressed) data size (LE)                |
| 5      | 4    | Stored (compressed or original) data size (LE)        |
| 9      | var  | Block data (protobuf, optionally LZ4-compressed)      |

### Standard Block Layout (Row-Oriented)

Packets are serialized sequentially as nested protobuf messages — one per packet. Suitable for streaming and sequential processing.

Each block contains:
- Per-packet protobuf messages with timestamp, ID, info, and field tree
- Field-info deduplication (name/ui_name/type written once per field ID per block)
- Same-as-previous flags for value deduplication

### Columnar Block Layout

Fields are separated into per-field columns, optimized for analytical queries and compression. Each block contains:
- **Topology**: Tree structure encoded as flat arrays (parent ID, depth)
- **Per-field columns**: Homogeneous value arrays with dictionary encoding
- **Delta-encoded timestamps**: First timestamp + deltas for remaining packets

## Trailer

Protobuf message with session summary:

| Field # | Wire Type        | Name              | Description                        |
|---------|------------------|-------------------|------------------------------------|
| 1       | Varint           | packet_count      | Total packets written              |
| 2       | Varint           | block_count       | Number of data blocks              |
| 3       | Length-delimited  | field_bitmap      | Bitmap of all field IDs present    |
| 4       | Length-delimited  | block_index_entry | Repeated: per-block timestamp range|

### Block Index Entry (embedded protobuf)

| Field # | Wire Type | Name          | Description                     |
|---------|-----------|---------------|---------------------------------|
| 1       | Varint    | min_timestamp | Minimum timestamp in block (sint64) |
| 2       | Varint    | max_timestamp | Maximum timestamp in block (sint64) |

## Compression

LZ4 compression is applied per-block when enabled. The compressor compares compressed vs. uncompressed size and falls back to uncompressed when compression provides no benefit (small blocks or incompressible data). The flags byte (bit 0) indicates whether the block is compressed.

## Builder Options

| Method                    | Description                                    | Default          |
|---------------------------|------------------------------------------------|------------------|
| `.ToFile(path)`           | Write to file with 4 MiB buffer                | required         |
| `.ToStream(stream)`       | Write to existing stream                       | required         |
| `.ToStdout()`             | Write to standard output                       | required         |
| `.WithUiName(name)`       | Display name shown in UI and logs              | `"PBF Exporter"` |
| `.WithDescription(d)`     | Optional description                           | `null`           |
| `.Format(format)`         | Block layout (Standard or Columnar)            | `Standard`       |
| `.Compressed(bool)`       | Enable LZ4 compression                         | `true`           |
| `.MaxPacketsPerBlock(n)`  | Max packets before block flush                 | 50,000           |
| `.MaxBlockSize(n)`        | Max block size in bytes before flush           | 16 MiB           |
| `.IncludeTrailerIndex(b)` | Write per-block timestamp index in trailer     | `true`           |
| `.WithTargetPacketCount(n)` | Auto-stop after N packets                    | 0 (unlimited)    |
| `.WithCancellationToken(t)` | Cooperative cancellation                     | `CancellationToken.None` |

## Field Presence Bitmap

A global bitmap (up to 4096 bytes = 32,768 field IDs) tracks which fields appear anywhere in the file. Stored in the trailer for efficient random access and query planning.

## Empty Files

An empty export (no packets) still produces a valid PBF file with magic + header + trailer + magic. The trailer will report `packet_count = 0` and `block_count = 0`.

## Thread Safety

Not thread-safe. `OnPacket()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
