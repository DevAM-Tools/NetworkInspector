<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.CLI

Command-line interface for the **Network Inspector** stack. Provides
frame-level conversion between capture formats and packet-level export
to analysis formats.

The executable is published as `ni`.

## Commands

| Command   | Purpose                                                                           |
| --------- | --------------------------------------------------------------------------------- |
| `convert` | Convert capture files between **frame-level** formats (PCAPNG ↔ BLF, …).          |
| `export`  | Parse captures and **export packets** to analysis formats (JSON, PBF, Text, …).   |

Run `ni <command> --help` for the full set of options for a given command.

## `ni convert`

Frame-level format conversion. No protocol parsing is performed.
Suitable for splitting, re-compressing, or converting large captures.

```
ni convert <input> [<input2> ...] --output <path> [options]

Common options:
  --output, -o            Output path (file or directory if --split-* is set)
  --output-format, -f     Output format spec (see ni convert --help; overrides extension)
  --split-size <MB>       Split output at this size in MiB
  --split-count <N>       Split output every N frames
  --blf-cache-size <MB>   Container cache budget for BLF sources (MiB)
  --progress <N>          Report progress every N frames
  --tolerant              Skip malformed frames instead of aborting
```

### Examples

```sh
# Convert a BLF capture to PCAPNG
ni convert capture.blf --output capture.pcapng

# Split a large PCAPNG into 100 MiB chunks
ni convert big.pcapng --output split/ --split-size 100

# Convert multiple BLF files into a single PCAPNG
ni convert a.blf b.blf --output merged.pcapng
```

## `ni export`

Parses each frame through the full protocol stack and writes one
record per packet to the configured exporter. Supports optional
profile-based settings for protocol configuration.

```
ni export <input> [<input2> ...] --format <fmt> --output <path> [options]

Common options:
  --format <fmt>          Output format: json | pbf | text
  --output, -o            Output path (omit or use - for stdout)
  --profile <name>        Settings profile name
  --settings-path <dir>   Base directory for settings storage
  --blf-cache-size <MB>   Container cache budget for BLF sources (MiB)
  --progress <N>          Report progress every N packets
  --tolerant              Skip malformed frames instead of aborting
```

Format variants:

| Format | Variants |
| ------ | -------- |
| `json` | `json:style=compact` (default), `json:style=pretty`, `json:style=array` |
| `pbf`  | `pbf:format=standard` (default), `pbf:format=columnar`, `pbf:format=columnar,compressed` |
| `text` | `text:level=summary`, `text:level=standard` (default), `text:level=full`, `text:truncate=<N>` |

### Examples

```sh
# Compact JSON to stdout
ni export capture.pcapng --format json

# Pretty-printed JSON to file
ni export capture.pcapng --format json:style=pretty --output capture.json

# Human-readable Wireshark-style text dump
ni export capture.pcapng --format text --output capture.txt

# Columnar PBF with LZ4 compression
ni export capture.pcapng --format pbf:format=columnar,compressed --output capture.pbf
```

## Exit Codes

| Code | Meaning                                                                    |
| ---: | -------------------------------------------------------------------------- |
|  `0` | Success.                                                                   |
|  `1` | Usage error or unknown command.                                            |
|  `2` | Source open / parse failure (file not found, unsupported format, …).       |
|  `3` | Exporter failure that aborted the run (only when `--tolerant` is not set). |

## Notes

- Console output is forced to UTF-8 (`Console.OutputEncoding = Encoding.UTF8`).
- Cancellation (`Ctrl+C`) is observed cooperatively by every long-running
  command and propagated to the underlying source / exporter.
- Standard input / output streaming is supported by passing `-` as the path
  for sources and exporters that accept streams.

See the repository [`README.md`](../README.md) for an overview of the wider
Network Inspector project and the supported formats.
