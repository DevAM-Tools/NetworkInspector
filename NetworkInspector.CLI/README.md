<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.CLI

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.CLI)](https://www.nuget.org/packages/NetworkInspector.CLI)

Command-line entry point for capture conversion and packet export workflows.
The executable name is `ni`.

## What This Is

`NetworkInspector.CLI` provides two production-oriented commands:

- `ni convert` for frame-level format conversion (PCAP/PCAPNG/BLF/ASC workflows).
- `ni export` for protocol parsing and packet-level export (JSON/PBF/Text/Parquet/DuckDB workflows).

Application logic lives in the `NetworkInspector.CLI.Core` class library (tested and gated by ExitPointGaps). The `ni` executable is a thin host that forwards to `CliEntry.Run`.

## Why It Stands Out

- One CLI for both format conversion and parsed packet export.
- File-based workflows with optional size/count splitting for large captures.
- Tolerant mode and progress reporting for large or imperfect captures.
- Stable exit-code contract for scripts and CI jobs.

## Install

Global tool install:

```bash
dotnet tool install -g NetworkInspector.CLI
```

Local tool install (repository or project scope):

```bash
dotnet new tool-manifest
dotnet tool install NetworkInspector.CLI
```

Then run:

```bash
ni --help
```

## Quick Start

Convert a BLF capture to PCAPNG:

```bash
ni convert capture.blf --output capture.pcapng
```

Export packets as compact JSON to a file:

```bash
ni export capture.pcapng --format json --output capture.json
```

Export text output to a file:

```bash
ni export capture.pcapng --format text --output capture.txt
```

## Commands

| Command | Use when |
| --- | --- |
| `convert` | You need frame-preserving conversion or splitting without protocol parsing. |
| `export` | You need parsed packets in JSON, PBF, or text formats. |

Run `ni <command> --help` for full command help.

## ni convert

Frame-level conversion. No packet parsing is performed.

```text
ni convert <input> [<input2> ...] --output <path> [options]
```

Common options:

- `--output`, `-o` Output file path (required).
- `--output-format`, `--format`, `-f` Explicit output format spec (overrides extension).
- `--profile <name>` Settings profile (available to sources and exporters).
- `--settings-path <dir>` Base directory for settings storage.
- `--max-frames`, `-n` Maximum number of frames to process.
- `--split-size <MB>` Split output at this size in MiB.
- `--split-count <N>` Split output every N frames.
- `--filter <expr>` Only keep frames whose packet matches the expression.
- `--blf-cache-size <MB>` BLF cache budget in MiB.
- `--progress <N>` Report progress every N frames (stderr).
- `--tolerant` Skip malformed frames instead of aborting.

Conversion is frame-level by default: no protocol stack is built and no frame is parsed. Passing a
non-empty `--filter` changes that, because each frame has to be parsed before it can be judged. An
omitted, empty, or whitespace-only `--filter` keeps the fast frame-copy path.

Format variants:

| Format | Variants |
| --- | --- |
| `pcapng` | `pcapng` (default), `pcap` |
| `blf` | `blf` (default compression), `blf:compression=off`, `blf:compression=fast`, `blf:compression=default`, `blf:compression=best` |
| `asc` | `asc` |

Examples:

```bash
# Convert BLF to PCAPNG
ni convert capture.blf --output capture.pcapng

# Split a large file into 100 MiB chunks (base name + numbered suffix)
ni convert big.pcapng --output split/part.pcapng --split-size 100

# Convert multiple sources into one output
ni convert a.blf b.blf --output merged.pcapng

# Keep only DNS traffic (parses frames, unlike the plain conversion above)
ni convert capture.pcapng --output dns.pcapng --filter "udp.dstport == 53"
```

## ni export

Parse frames through the protocol stack and export one record per packet.

```text
ni export <input> [<input2> ...] --output <path> [--format <fmt>] [options]
```

Common options:

- `--output`, `-o` Output file path (required).
- `--format`, `-f` Export format spec.
- `--max-packets`, `-n` Maximum packets to export.
- `--split-size <MB>` Split when live `EstimatedOutputBytes` reaches this size (MiB; no filesystem probe).
- `--split-count <N>` Split output every N packets (numbered files, or sibling Parquet directories).
- `--filter <expr>` Only export packets matching the expression.
- `--profile <name>` Settings profile name.
- `--settings-path <dir>` Base directory for settings storage.
- `--blf-cache-size <MB>` BLF cache budget in MiB.
- `--progress <N>` Report progress every N packets (stderr).
- `--tolerant` Skip malformed frames instead of aborting.

If `--format` is omitted, format is chosen from `--output` extension when possible.

Format variants:

| Format | Variants |
| --- | --- |
| `json` | `json:style=compact` (default), `json:style=pretty`, `json:style=array` |
| `pbf` | `pbf:format=standard` (default), `pbf:format=columnar`, `pbf:format=columnar,compressed`, `pbf:format=columnar,nocompress` |
| `text` | `text:level=summary`, `text:level=standard` (default), `text:level=full`, `text:truncate=<N>` |
| `parquet` | `parquet` (directory dataset; `-o <dir>`; splits become sibling dirs `base_00001`, …) |
| `duckdb` | `duckdb` (file; `-o <file>.duckdb`; splits become `base_00001.duckdb`, …) |

For PBF, compression is enabled by default unless `nocompress` is specified.

Examples:

```bash
# Compact JSON to file
ni export capture.pcapng --format json --output capture.json

# Pretty JSON to file
ni export capture.pcapng --format json:style=pretty --output capture.json

# Human-readable protocol tree
ni export capture.pcapng --format text --output capture.txt

# Columnar PBF with compression
ni export capture.pcapng --format pbf:format=columnar,compressed --output capture.pbf

# Split export every 10_000 packets
ni export capture.pcapng --format json --output split/part.json --split-count 10000

# Parquet dataset directory
ni export capture.pcapng --format parquet --output out_parquet

# Split Parquet into sibling dataset directories every 50_000 packets
ni export capture.pcapng --format parquet --output out_parquet --split-count 50000

# DuckDB file (also supports --split-count / --split-size)
ni export capture.pcapng --format duckdb --output out.duckdb

# Tolerant export with progress checkpoints
ni export unknown-input.blf --format text --output output.txt --tolerant --progress 50000

# Only HTTPS packets
ni export capture.pcapng --format json --output tls.json --filter "tcp.port == 443"
```

### Filtering

Both commands accept the same expression language, documented in
[`FILTER_GUIDE.md`](../NetworkInspector.Filter/FILTER_GUIDE.md).

Behavior shared by `convert` and `export`:

- A packet is judged before any output is opened, so a filter that matches nothing writes no file
  and no dataset directory at all.
- An expression that does not compile exits with the usage/validation code (`1`) and names the
  position of the problem on stderr.
- An expression that fails to evaluate aborts with the runtime code (`3`) rather than writing a
  partially filtered output.
- `--max-frames` / `--max-packets` count what is written, so they cap matching records.

## Exit Codes

| Code | Meaning |
| ---: | --- |
| `0` | Success (including cooperative cancellation paths). |
| `1` | Usage or validation error (for example missing arguments or unknown command). |
| `2` | Source open/parse failure (for example missing file or unsupported source format). |
| `3` | Runtime failure during conversion/export processing. |

## Safe Usage (STRIDE)

- **Spoofing**: Prefer captures from trusted, attributable sources.
- **Tampering**: Use `--tolerant` for unknown files and inspect warnings.
- **Repudiation**: Keep original inputs and outputs together for reproducibility.
- **Information disclosure**: Treat JSON/Text outputs as sensitive when payload data may contain secrets.
- **Denial of service**: Use `--max-frames`, `--max-packets`, split options, progress checkpoints, and cancellation.
- **Elevation of privilege**: Run `ni` with least required file-system and process permissions.

## Operational Notes

- Output uses UTF-8 console encoding.
- Progress and diagnostics go to stderr.
- `Ctrl+C` triggers cooperative cancellation for long-running operations.
- Output path (`-o` / `--output`) is always required; stdout (`-`) is not supported.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.CLI)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.CLI)
- [Root overview](../README.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
