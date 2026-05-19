<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# PCAPNG Exporter

The PCAPNG exporter writes raw captured frames to a PCAPNG (Packet Capture Next Generation) file. It implements `IFrameListener` and supports automatic interface discovery, snap-length truncation, configurable timestamp resolution, and SHB metadata.

## File Format

PCAPNG is a standardized capture format defined by the [pcapng specification](https://www.ietf.org/archive/id/draft-tuexen-opsawg-pcapng-06.html). Files produced by this exporter are compatible with Wireshark, tshark, tcpdump, and other PCAPNG-aware tools.

## File Structure

```
┌──────────────────────────────────────────┐
│  Section Header Block (SHB)              │  File magic, version, options
├──────────────────────────────────────────┤
│  Interface Description Block (IDB) #0    │  First interface (written on-demand)
├──────────────────────────────────────────┤
│  Enhanced Packet Block (EPB)             │  Frame data referencing IDB #0
├──────────────────────────────────────────┤
│  Interface Description Block (IDB) #1    │  Second interface (if different link type)
├──────────────────────────────────────────┤
│  Enhanced Packet Block (EPB)             │  Frame data referencing IDB #1
├──────────────────────────────────────────┤
│  ...                                     │
└──────────────────────────────────────────┘
```

### Section Header Block (SHB)

Written once at the start of the file. Contains:

| Field              | Size   | Value                          |
|--------------------|--------|--------------------------------|
| Block Type         | 4 LE   | `0x0A0D0D0A`                   |
| Block Total Length | 4 LE   | Variable                       |
| Byte-Order Magic   | 4      | `0x1A2B3C4D`                   |
| Major Version      | 2 LE   | 1                              |
| Minor Version      | 2 LE   | 0                              |
| Section Length      | 8 LE   | -1 (unspecified)               |
| Options (optional)  | var   | Hardware, OS, Application, Comment |
| Block Total Length | 4 LE   | (repeated)                     |

**SHB Options** (when configured):

| Code | Name         | Description          |
|------|--------------|----------------------|
| 2    | shb_hardware | Hardware description |
| 3    | shb_os       | OS description       |
| 4    | shb_userappl | Application name     |
| 1    | opt_comment  | Comment              |
| 0    | opt_endofopt | End of options       |

### Interface Description Block (IDB)

Written on-demand when a new (interface ID, link type) combination is encountered.

| Field              | Size   | Value                          |
|--------------------|--------|--------------------------------|
| Block Type         | 4 LE   | `0x00000001`                   |
| Block Total Length | 4 LE   | Variable                       |
| LinkType           | 2 LE   | e.g. 1 (Ethernet)             |
| Reserved           | 2 LE   | 0                              |
| SnapLen            | 4 LE   | Max capture length             |
| Options (optional)  | var   | Timestamp resolution, if_name |
| Block Total Length | 4 LE   | (repeated)                     |

**IDB Options:**

| Code | Name              | Description                          |
|------|-------------------|--------------------------------------|
| 9    | if_tsresol        | Timestamp resolution (1 byte)        |
| 2    | if_name           | Interface name string                |
| 0    | opt_endofopt      | End of options                       |

**Timestamp resolution:** Encoded as a power-of-10 exponent. `9` = nanosecond (10^-9), `6` = microsecond (10^-6).

### Enhanced Packet Block (EPB)

One per frame. Contains the actual captured data.

| Field              | Size   | Value                          |
|--------------------|--------|--------------------------------|
| Block Type         | 4 LE   | `0x00000006`                   |
| Block Total Length | 4 LE   | Variable                       |
| Interface ID       | 4 LE   | Index of IDB                   |
| Timestamp (High)   | 4 LE   | Upper 32 bits                  |
| Timestamp (Low)    | 4 LE   | Lower 32 bits                  |
| Captured Packet Len| 4 LE   | Bytes stored (may be truncated)|
| Original Packet Len| 4 LE   | Original frame length          |
| Packet Data        | var    | Frame bytes (padded to 4-byte) |
| Block Total Length | 4 LE   | (repeated)                     |

All blocks are padded to 4-byte alignment as required by the PCAPNG specification.

## Interface Discovery

Interfaces are registered dynamically. Each unique `(FrameInterfaceId, LinkType)` pair gets a new IDB. The IDB is written immediately before the first EPB that references it. This allows capturing from multiple interfaces with different link types in a single file.

## Supported Link Types

The PCAPNG exporter is format-agnostic — it writes raw frame data with the DLT value from `LinkType` directly into each IDB. Tested link types include:

| Link Type              | DLT Value | Description              |
|------------------------|-----------|--------------------------|
| `LinkType.Ethernet`    | 1         | Ethernet II              |
| `LinkType.CanSocketcan`| 227       | SocketCAN                |
| `LinkType.Can20B`      | 190       | CAN 2.0B                 |
| `LinkType.Flexray`     | 210       | FlexRay                  |
| `LinkType.Lin`         | 212       | LIN                      |

Any other `LinkType` value is also written correctly; the above are explicitly verified by tests.

## Snap-Length Truncation

Frames longer than the configured snap-length are truncated. The EPB records both the captured length (after truncation) and the original frame length. Default snap-length is 65535 bytes (per PCAPNG convention).

## Builder Options

| Method                       | Description                                  | Default      |
|------------------------------|----------------------------------------------|--------------|
| `.ToFile(path)`              | Write to file with 4 MiB buffer              | required     |
| `.ToStream(stream)`          | Write to existing stream                     | required     |
| `.ToStdout()`                | Write to standard output                     | required     |
| `.WithUiName(name)`          | Display name shown in UI and logs            | `"PCAPNG Exporter"` |
| `.WithDescription(d)`        | Optional description                         | `null`       |
| `.WithSnapLength(n)`         | Max captured bytes per frame                 | 65535        |
| `.WithTimestampResolution(r)`| Timestamp resolution exponent                | 9 (nanosec)  |
| `.WithHardware(s)`           | SHB hardware option                          | none         |
| `.WithOs(s)`                 | SHB OS option                                | none         |
| `.WithApplication(s)`        | SHB application option                       | none         |
| `.WithComment(s)`            | SHB comment option                           | none         |
| `.WithTargetFrameCount(n)`   | Auto-stop after N frames                     | 0 (unlimited)|
| `.WithCancellationToken(t)`  | Cooperative cancellation                     | `CancellationToken.None` |

## Lazy Initialization

The file is not created until the first frame is received (or `OnFinish()` is called for empty exports). This prevents creating empty files when no frames match a filter.

For empty exports, a minimal valid PCAPNG file is produced containing only an SHB (no IDBs or EPBs).

## Compatibility

Output files are compatible with:
- Wireshark (all versions supporting PCAPNG)
- tshark
- tcpdump
- Scapy
- Any tool supporting the PCAPNG format

## Thread Safety

Not thread-safe. `OnFrame()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
