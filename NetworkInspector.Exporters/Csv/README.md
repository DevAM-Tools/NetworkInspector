<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# CSV Exporter

The CSV exporter serializes parsed packets to a comma-separated values (CSV) file. It implements `IPacketListener` and writes one row per packet with configurable columns, delimiter, header, and optional UTF-8 BOM.

## Output Format

Each output file begins with an optional BOM and an optional header row, followed by one data row per packet. Fields are delimited by the configured byte (default: `,`). Values containing the delimiter, double quotes, or newlines are enclosed in double quotes with internal double quotes escaped as `""`.

**Example (default columns, comma delimiter):**

```
No.,Time,Info,Length
1,2024-01-01T12:00:00.000000000Z,Ethernet / IPv4 / UDP,74
2,2024-01-01T12:00:00.001000000Z,"Comment with ""quoted"" text",42
```

## Configuration

| Builder Method | Default | Description |
|----------------|---------|-------------|
| `ToFile(path)` | — | Write to a file (buffered, 4 MiB). |
| `ToStream(stream)` | — | Write to an existing stream. |
| `ToStdout()` | — | Write to standard output. |
| `WithUiName(name)` | `"CSV Exporter"` | Display name for UI and logging. |
| `WithDescription(desc)` | `null` | Optional human-readable description. |
| `WithCancellationToken(token)` | none | Stop export when the token is cancelled. |
| `WithTargetPacketCount(n)` | `0` (unlimited) | Stop after `n` packets. |
| `Delimiter(char)` | `','` | Field separator character. |
| `WriteBom(bool)` | `true` | Prepend UTF-8 BOM (`0xEF BB BF`). |
| `WriteHeader(bool)` | `true` | Emit a header row with column names. |
| `AddColumn(kind, header?)` | — | Add a built-in column (`PacketNumber`, `Timestamp`, `Info`, `FrameLength`). |
| `AddFieldColumn(name, id, header?)` | — | Add a column reading a specific protocol field by `FieldId`. |
| `AddDefaultColumns()` | — | Adds No., Time, Info, Length. Auto-applied when no columns are configured. |

## Thread Safety

Not thread-safe. `OnPacket()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
