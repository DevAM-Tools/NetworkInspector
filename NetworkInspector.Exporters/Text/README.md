<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Text Exporter

The Text exporter serializes parsed packets to a human-readable plain-text file. It implements `IPacketListener` and outputs a protocol field tree similar to Wireshark's packet details view.

## Output Format

Each packet begins with a header line showing the packet number and timestamp, followed by the protocol field tree with two-space indentation per nesting level, and then a blank separator line after the tree.

**Example output (Standard detail level):**

```
Packet 1  [2024-01-01T12:00:00.000000000Z]
Frame
  Arrival Time: 2024-01-01T12:00:00.000000000Z
Ethernet II
  Destination: aa:bb:cc:dd:ee:ff
  Source: 11:22:33:44:55:66
  Type: IPv4 (0x0800)
  Internet Protocol Version 4
    Source Address: 192.168.1.1
    Destination Address: 10.0.0.1
    User Datagram Protocol
      Source Port: 53
      Destination Port: 12345

```

## Detail Levels

| Level | Description |
|-------|-------------|
| `Summary` | Top-level protocol layer names only; no field values. |
| `Standard` | All fields with their display values (default). |
| `Full` | All fields with display values plus raw hex for bytes fields. |

## Configuration

| Builder Method | Default | Description |
|----------------|---------|-------------|
| `ToFile(path)` | — | Write to a file (buffered, 4 MiB). |
| `ToStream(stream)` | — | Write to an existing stream. |
| `ToStdout()` | — | Write to standard output. |
| `WithUiName(name)` | `"Text Exporter"` | Display name for UI and logging. |
| `WithDescription(desc)` | `null` | Optional human-readable description. |
| `WithCancellationToken(token)` | none | Stop export when the token is cancelled. |
| `WithTargetPacketCount(n)` | `0` (unlimited) | Stop after `n` packets. |
| `DetailLevel(level)` | `Standard` | Controls how much field detail is written. |
| `MaxTextLength(n)` | `256` | Truncate string and bytes values to `n` characters. `0` disables truncation. |

## Thread Safety

Not thread-safe. `OnPacket()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
