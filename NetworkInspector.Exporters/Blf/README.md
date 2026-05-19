<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# BLF Exporter (Binary Logging Format)

The BLF exporter writes raw captured frames to a BLF file (Binary Logging Format), a standard automotive logging format used by Vector tools (CANoe, CANalyzer) and other automotive software. It implements `IFrameListener` and supports Ethernet, CAN classic, CAN FD, FlexRay, and LIN frames with configurable compression.

## File Structure

```
┌──────────────────────────────────────────┐
│  File Header (144 bytes)                 │  "LOGG" magic, statistics, timestamps
├──────────────────────────────────────────┤
│  Log Container (compressed)              │  Contains multiple BLF objects
│  ├── Object: Ethernet Frame              │
│  ├── Object: CAN Message                 │
│  ├── Object: CAN FD Message              │
│  ├── Object: FlexRay Rcv Message         │
│  └── Object: LIN Message2               │
├──────────────────────────────────────────┤
│  Log Container                           │
│  ├── ...                                 │
├──────────────────────────────────────────┤
│  ...                                     │
└──────────────────────────────────────────┘
```

## File Header (144 bytes)

| Offset | Size | Field                    | Description                          |
|--------|------|--------------------------|--------------------------------------|
| 0      | 4    | Magic                    | `0x47474F4C` ("LOGG")                |
| 4      | 4    | Header Size              | 144                                  |
| 8      | 4    | API Version              | 0x0403 (4.3)                         |
| 12     | 4    | Platform                 | 0x01 (Windows)                       |
| 16     | 4    | Creation Flags           | 0                                    |
| 20     | 4    | Measurement Start (D/M/Y)| BLF SYSTEMTIME structure             |
| 36     | 4    | Measurement End (D/M/Y)  | BLF SYSTEMTIME structure             |
| 52     | 4    | Object Count             | Total number of BLF objects           |
| ...    | ...  | (reserved/statistics)    | Padding to 144 bytes                  |

The header is written with placeholder values on construction. After all objects have been written, `Finish()` computes final statistics and `UpdateHeader()` seeks back to offset 0 to rewrite the header with correct timestamps and counts.

## BLF Object Structure

Each object has a two-part header:

### Block Header (16 bytes)

| Offset | Size | Field                | Description                     |
|--------|------|----------------------|---------------------------------|
| 0      | 4    | Magic                | `0x4A424F4C` ("LOBJ")           |
| 4      | 2    | Header Size          | 16                              |
| 6      | 2    | Header Version       | 1                               |
| 8      | 4    | Object Size          | Total size including headers    |
| 12     | 4    | Object Type          | See supported types below       |

### Log Object Header V1 (16 bytes)

| Offset | Size | Field                | Description                     |
|--------|------|----------------------|---------------------------------|
| 0      | 4    | Timestamp Status     | 0x22 (valid, 10µs resolution)   |
| 4      | 8    | Timestamp            | Relative time in 10µs units     |
| 12     | 4    | (reserved)           | 0                               |

All objects are padded to 4-byte alignment.

## Supported Object Types

### Ethernet Frame (Type 71)

BLF payload layout:

| Offset | Size | Field          | Description                     |
|--------|------|----------------|---------------------------------|
| 0      | 6    | Source MAC     | Source hardware address          |
| 6      | 2    | Channel        | BLF channel number (LE)         |
| 8      | 6    | Destination MAC| Destination hardware address     |
| 14     | 2    | Direction      | 0=receive, 1=transmit (LE)      |
| 16     | 2    | EtherType      | Protocol type (BE)              |
| 18     | 2    | TPID           | VLAN TPID (BE, 0 if no VLAN)   |
| 20     | 2    | TCI            | VLAN TCI (BE, 0 if no VLAN)    |
| 22     | 2    | Payload Length | Length of payload data (LE)      |
| 24     | var  | Payload        | Ethernet payload bytes           |

### CAN Message (Type 1)

BLF payload layout (from SocketCAN frame):

| Offset | Size | Field     | Description                          |
|--------|------|-----------|--------------------------------------|
| 0      | 2    | Channel   | BLF channel number (LE)             |
| 2      | 1    | Flags     | CAN flags (EFF=0x04)                |
| 3      | 1    | DLC       | Data length code (0-8)              |
| 4      | 4    | ID        | CAN arbitration ID (LE, masked to 29-bit) |
| 8      | 0-8  | Data      | CAN data bytes                       |

### CAN FD Message (Type 86)

BLF payload layout (from SocketCAN FD frame):

| Offset | Size  | Field     | Description                          |
|--------|-------|-----------|--------------------------------------|
| 0      | 2     | Channel   | BLF channel number (LE)             |
| 2      | 1     | DLC       | Data length code (0-64)             |
| 3      | 1     | Valid Payload Length | Actual data bytes         |
| 4      | 4     | ID        | CAN arbitration ID (LE, masked)     |
| 8      | 4     | Frame Length | Total frame length in bytes       |
| 12     | 4     | Flags     | CAN FD flags (BRS=0x01, ESI=0x02)  |
| 16     | 0-64  | Data      | CAN FD data bytes                    |

## SocketCAN FD Detection

CAN FD frames are distinguished from CAN classic by checking the FD Format indicator flag at byte offset 5 of the SocketCAN frame (bit 2 = `0x04`). If set, the frame is written as a CAN FD Message (Type 86); otherwise as a CAN Message (Type 1).

### FlexRay Rcv Message (Type 50)

BLF payload layout (from DLT_FLEXRAY frame):

| Offset | Size | Field          | Description                          |
|--------|------|----------------|--------------------------------------|
| 0      | 2    | Channel        | FlexRay channel (LE, from DLT byte 0) |
| 2      | 2    | Version        | 0 (LE)                              |
| 4      | 2    | Channel Mask   | 0x0001 (LE)                          |
| 6      | 2    | Direction      | 0x0001 = RX (LE)                    |
| 8      | 4    | Client Index   | 0 (LE)                              |
| 12     | 4    | Cluster Number | 0 (LE)                              |
| 16     | 2    | Frame ID       | FlexRay slot ID (LE)                |
| 18     | 2    | Header CRC 1   | Header CRC (LE)                     |
| 20     | 2    | Header CRC 2   | 0 (LE)                              |
| 22     | 2    | Payload Length | Payload byte count (LE)              |
| 24     | 1    | Cycle          | Cycle counter                        |
| 25     | 1    | Tag            | 0                                    |
| 26     | 1    | Data Flag      | 0                                    |
| 27     | 1    | Frame Flags    | Bit flags (see below)               |
| 28     | var  | Data           | FlexRay payload bytes                |

**Frame Flags mapping (DLT type_flags → BLF frame_flags):**

| DLT Bit | BLF Flag | Meaning              |
|---------|----------|----------------------|
| bit 7   | 0x01     | Payload preamble     |
| bit 6   | 0x02     | Null frame           |
| bit 5   | 0x04     | Sync frame           |
| bit 4   | 0x08     | Startup frame        |

### LIN Message2 (Type 57)

BLF payload layout (from DLT_LIN frame):

| Offset | Size | Field           | Description                         |
|--------|------|-----------------|-------------------------------------|
| 0      | 8    | Data            | LIN data bytes (zero-padded to 8)   |
| 8      | 1    | CRC             | Checksum from DLT frame             |
| 9      | 1    | Direction       | 0 = RX                             |
| 10     | 1    | Simulated       | 0                                   |
| 11     | 1    | Is ETF          | 0                                   |
| 12     | 1    | ETF Assoc Index | 0                                   |
| 13     | 1    | ID              | 6-bit frame ID (PID masked)        |
| 14     | 1    | DLC             | Data length                         |
| 15     | 8    | Start of Frame  | 0 (LE)                              |
| 23     | 4    | Baudrate        | 0 (LE)                              |
| 27     | 4    | Response Flags  | 0 (LE)                              |
| 31     | 1    | Channel         | Channel number                      |

## Unsupported Link Types

Currently only these link types are supported:
- `LinkType.Ethernet` → Ethernet Frame (Type 71)
- `LinkType.CanSocketcan` → CAN Message (Type 1) or CAN FD Message (Type 86)
- `LinkType.Can20B` → CAN Message (Type 1)
- `LinkType.Flexray` → FlexRay Rcv Message (Type 50)
- `LinkType.Lin` → LIN Message2 (Type 57)

All other link types (Loopback, PPP, Raw IP, etc.) are silently skipped. The frame count only reflects successfully written objects.

## Container Compression

BLF objects are accumulated in an internal buffer (up to 10 MB). When the buffer is full, it is flushed as a log container. Compression is applied per container using zlib (via `System.IO.Compression.ZLibStream`).

| Compression Level | Mapping                    |
|-------------------|----------------------------|
| None              | `CompressionLevel.NoCompression` |
| Fast              | `CompressionLevel.Fastest` |
| Default           | `CompressionLevel.Optimal` |
| Best              | `CompressionLevel.SmallestSize` |

## Timestamps

- Input timestamps are absolute nanoseconds since Unix epoch
- BLF timestamps are relative to the file start time, in 10µs units
- Timestamp resolution flag: `0x22` (valid, 10µs)
- Negative relative timestamps (frames before start time) are clamped to 0

### Timezone Handling

BLF `SYSTEMTIME` fields (`start_date` / `end_date`) are written in the **local timezone**
of the machine producing the file, not UTC. This matches Vector tooling (CANoe, vSignalyzer)
and Wireshark/tshark, which reconstruct absolute timestamps using `mktime` with the reader's
local timezone. Writing UTC components instead would shift all reported frame timestamps by
the local UTC offset. As a consequence, BLF files produced on machines in different timezones
will have different raw header bytes for the same capture session, but tshark and Vector tools
will display identical absolute timestamps when run in the same timezone as the producer. The
round-trip tests in `BlfRoundtripTests` verify timestamp correctness by comparing exported
frames against tshark output rather than comparing raw bytes.

## Builder Options

| Method                        | Description                                  | Default       |
|-------------------------------|----------------------------------------------|---------------|
| `.ToFile(path)`               | Write to file with 4 MiB buffer              | required      |
| `.ToStream(stream)`           | Write to existing stream                     | required      |
| `.ToStdout()`                 | Write to standard output                     | required      |
| `.WithUiName(name)`           | Display name shown in UI and logs            | `"BLF Exporter"` |
| `.WithDescription(d)`         | Optional description                         | `null`        |
| `.WithCompressionLevel(level)` | Container compression level                 | `Default`     |
| `.WithTargetFrameCount(n)`    | Auto-stop after N frames                     | 0 (unlimited) |
| `.WithCancellationToken(t)`   | Cooperative cancellation                     | `CancellationToken.None` |

## Lazy Initialization

The file header is written on the first frame. If no frames are received but `OnFinish()` /
`Dispose()` is called on a successfully-built exporter, an empty-but-valid BLF file is
produced: a 144-byte LOGG header with `obj_count = 0` and a zeroed `start_date`. tshark,
Vector tooling, and `BlfSource` all open such files and report zero frames. If construction
itself failed (e.g. the output stream rejected the header write), no file is created.

## Empty Exports

If no frames are received but `OnFinish()` / `Dispose()` is called on a
successfully-built exporter, an empty-but-valid BLF file is still produced (same as
described in [Lazy Initialization](#lazy-initialization) above): a 144-byte LOGG header
with `obj_count = 0`. This is consistent with PCAPNG, which always emits at least an SHB.

When the BLF file is successfully written, the `start_date` and `end_date` fields in the header contain the timestamps of the first and last BLF object respectively, converted from the capture timestamp. If no objects were written (or the measurement-start time is unavailable), both fields are zeroed-out SYSTEMTIME structures (`start_date = 0`, `end_date = 0`).

## Compatibility

Output files are compatible with:
- Vector CANoe / CANalyzer
- Vector vSignalyzer
- PEAK PCAN-View
- BLF-capable analysis tools

## Thread Safety

Not thread-safe. `OnFrame()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
