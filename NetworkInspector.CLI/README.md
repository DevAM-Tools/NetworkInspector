<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.CLI

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.CLI?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.CLI)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.CLI?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.CLI)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

`ni` converts and exports network and automotive captures from the command line: PCAP, PCAPNG, Vector BLF, and
Vector ASC in; PCAPNG, BLF, ASC, JSON, Text, PBF, Parquet, and DuckDB out. Filter with Wireshark-style
expressions, split large outputs, and script it with a stable exit-code contract.

## Highlights

- **Two commands for everything.** `ni convert` copies frames between capture formats without parsing; `ni export`
  parses every packet and writes analysis formats.
- **From capture to SQL in one line:** `ni export capture.pcapng -o packets.duckdb`.
- **Filters on both commands.** `--filter "udp.dstport == 53"`; a filter that matches nothing writes no file.
- **Large captures.** Split by size or count, cap the record count, report progress, cancel with Ctrl+C and keep a
  readable partial output.
- **Automotive ready.** CAN, CAN FD, CAN XL, LIN, FlexRay, and automotive Ethernet between BLF, ASC, and PCAPNG.
- **Script friendly.** Exit codes 0–3, diagnostics on stderr, UTF-8 output, invariant culture.

## Install

Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet tool install -g NetworkInspector.CLI
ni --help
```

As a local tool for one repository:

```bash
dotnet new tool-manifest
dotnet tool install NetworkInspector.CLI
dotnet ni --help
```

## Quick Start

```bash
# Convert a Vector BLF log to PCAPNG for Wireshark
ni convert capture.blf -o capture.pcapng

# Export every packet as JSON
ni export capture.pcapng -o packets.json

# Query packets with SQL
ni export capture.pcapng -o packets.duckdb
duckdb packets.duckdb "SELECT count(*) FROM packets"

# Keep only DNS traffic, as a new capture
ni convert capture.pcapng -o dns.pcapng --filter "udp.port == 53"

# Read the protocol tree of the first 10 packets
ni export capture.pcapng -o first10.txt -f text:level=full -n 10
```

## Commands

| Command | Parses packets | Use when |
| --- | --- | --- |
| `convert` | only with `--filter` | Frame-preserving conversion, merging, or splitting |
| `export` | always | Parsed packets as JSON, Text, PBF, Parquet, or DuckDB |

Run `ni <command> --help` for the built-in help.

### Inputs

Both commands take one or more source specs. The reader is chosen by file extension (case-insensitive):
`.pcap` and `.pcapng` (PCAP or PCAPNG, detected by magic number), `.blf`, `.asc`. Typed specs work too:
`pcap:path=…`, `blf:path=…`, `asc:path=…`. `random:count=N,seed=S,mode=M` generates synthetic traffic
(`count` default 1000, `0` = unlimited; `seed` default 42; modes `udp4` (default), `udp6`, `ipv4`, `ipv6`,
`ethernet`, `can`, `canfd`, `random`).

Several inputs are interleaved one frame at a time (round-robin), not concatenated and not merged by timestamp.
Source specs must not contain shell metacharacters (`| & > < ; \` ( ) { }`); rename such files first.

## ni convert

Frame-level conversion. Without `--filter` no protocol stack is built and no frame is parsed.

```text
ni convert <input> [<input2> ...] --output <path> [options]
```

| Option | Meaning |
| --- | --- |
| `-o`, `--output <path>` | Output file (required; `-` for stdout is not supported) |
| `-f`, `--format`, `--output-format <spec>` | Output format; overrides the extension |
| `-n`, `--max-frames <N>` | Maximum frames to write |
| `--split-size <MiB>` / `--split-count <N>` | Split the output by size or frame count |
| `--filter <expr>` | Keep frames whose packet matches (parses each frame) |
| `--progress <N>` | Report progress every N frames on stderr |
| `--tolerant` | Skip unreadable records instead of aborting |
| `--blf-cache-size <MiB>` | BLF container cache budget (max 2047) |
| `--profile <name>`, `--settings-path <dir>` | Protocol settings profile and storage (used with `--filter`) |
| `--settings-max-config-file-bytes <bytes>` | Max settings/config JSON size (default 1073741824; `<= 0` disables) |
| `--settings-max-json-depth <n>` | Max settings/config JSON depth (default 1024; `<= 0` disables) |

Output formats:

| Spec | Output | Inferred from |
| --- | --- | --- |
| `pcapng` (alias `pcap`) | PCAPNG | `.pcapng`, `.pcap`, no or unknown extension |
| `blf`, `blf:compression=off\|fast\|default\|best` | BLF (default compression `default`) | `.blf` |
| `asc` | ASC | `.asc` |

There is no classic PCAP writer: `pcap` and `-o x.pcap` write PCAPNG.

```bash
# Split a large file into 100 MiB parts: split/part_00001.pcapng, split/part_00002.pcapng, ...
ni convert big.pcapng -o split/part.pcapng --split-size 100

# Interleave two BLF logs into one capture
ni convert a.blf b.blf -o merged.pcapng

# Uncompressed BLF for fast tooling
ni convert capture.pcapng -o capture.blf -f blf:compression=off
```

## ni export

Parse every frame through the standard protocol stack and write one record per packet.

```text
ni export <input> [<input2> ...] --output <path> [--format <spec>] [options]
```

| Option | Meaning |
| --- | --- |
| `-o`, `--output <path>` | Output file or directory (required) |
| `-f`, `--format <spec>` | Export format (see below) |
| `-n`, `--max-packets <N>` | Maximum packets to write |
| `--split-size <MiB>` | Split when the live output estimate reaches this size |
| `--split-count <N>` | Split every N packets |
| `--filter <expr>` | Export only matching packets |
| `--progress <N>`, `--tolerant`, `--blf-cache-size <MiB>` | As for `convert` |
| `--profile <name>`, `--settings-path <dir>`, `--settings-max-*` | Protocol settings |

Export formats:

| Spec | Variants | Inferred from |
| --- | --- | --- |
| `json` | `json:style=compact` (default), `pretty`, `array` | `.json` |
| `text` | `text:level=summary\|standard\|full`, `text:truncate=<N>` (default 256, `0` = no limit) | `.txt` |
| `pbf` | `pbf:format=standard\|columnar`, `compressed` (default) or `nocompress` | `.pbf` |
| `parquet` | directory dataset; splits become `base_00001`, … | — (use `-f parquet`) |
| `duckdb` | one file; splits become `base_00001.duckdb`, … | `.duckdb` |

Parameters combine with commas: `text:level=full,truncate=0`, `pbf:format=columnar,nocompress`. Any other
extension needs `-f`. CSV is available in the library (`NetworkInspector.Exporters`), not in the CLI.

> **Known issue in 0.16.0:** `text` with the default level `standard` (and `summary`) omits the protocol
> containers and prints little more than the packet header. Use `text:level=full`.

```bash
# Pretty JSON
ni export capture.pcapng -o capture.json -f json:style=pretty

# Columnar PBF without compression
ni export capture.pcapng -o capture.pbf -f pbf:format=columnar,nocompress

# Parquet dataset, a new directory every 50 000 packets
ni export capture.pcapng -o out_parquet -f parquet --split-count 50000

# Only port 443, tolerant of damaged records, progress every 50 000 packets
ni export unknown.blf -o tls.json --filter "tcp.port == 443" --tolerant --progress 50000
```

### Filtering

Both commands accept the expression language of [`NetworkInspector.Filter`](../NetworkInspector.Filter/README.md)
([full guide](../NetworkInspector.Filter/FILTER_GUIDE.md)).

- A packet is judged before any output is opened, so a filter that matches nothing writes no file and no dataset
  directory.
- An expression that does not compile exits with code `1` and names the offending token or field on stderr,
  including a "did you mean" suggestion for misspelled fields.
- An expression that fails to evaluate aborts with code `3` instead of writing a partially filtered output.
- `--max-frames` / `--max-packets` count written records, so they cap matching records.

### Splitting

Split files are named `{base}_{index:D5}{extension}`: `-o split/part.json --split-count 10000` writes
`split/part_00001.json`, `split/part_00002.json`, … The parent directory must exist (Parquet creates its own
dataset directory). Existing files are overwritten.

## Exit Codes

| Code | Meaning |
| ---: | --- |
| `0` | Success, also after Ctrl+C (the partial output is finalized) |
| `1` | Usage or validation error: missing or unknown option, unknown input extension, bad format spec, filter that does not compile |
| `2` | Source cannot be opened or read: missing file, invalid file header |
| `3` | Runtime failure during conversion or export, including a packet that fails to parse and a filter that fails to evaluate |

## Operational Notes

- `--tolerant` skips records the reader cannot read. A packet that the parser rejects still stops `export` with
  code `3`.
- Progress, warnings, and the completion summary go to stderr; the console uses UTF-8.
- Ctrl+C cancels cooperatively and finalizes what was written.
- Commands and options are case-insensitive. `-h`, `--help`, `-?` work as the first argument after the command.

## Safe Usage (STRIDE)

- **Spoofing**: Prefer captures from trusted, attributable sources.
- **Tampering**: Use `--tolerant` for unknown files and inspect the warnings on stderr.
- **Repudiation**: Keep inputs, outputs, and the command line together for reproducibility.
- **Information disclosure**: JSON, Text, and columnar outputs can contain payload secrets; protect them.
- **Denial of service**: Use `--max-frames`, `--max-packets`, split options, and the settings size limits on
  untrusted input.
- **Elevation of privilege**: Run `ni` with least-required file-system permissions.

## Learn More

- [Exporters](../NetworkInspector.Exporters/README.md) (formats in detail) ·
  [Sources](../NetworkInspector.Sources/README.md) (readers) · [Filter](../NetworkInspector.Filter/README.md) ·
  [Root overview](../README.md)
- The `ni` executable is a thin host over the `NetworkInspector.CLI.Core` library (`CliEntry.Run`).
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
