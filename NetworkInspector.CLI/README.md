<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.CLI

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.CLI)](https://www.nuget.org/packages/NetworkInspector.CLI)

Command-line entry point for capture conversion and packet export workflows.
The executable name is `ni`.

## What This Is

`NetworkInspector.CLI` provides two production-oriented commands:

- `ni convert` for frame-level format conversion (PCAP/PCAPNG/BLF workflows).
- `ni export` for protocol parsing and packet-level export (JSON/PBF/Text workflows).

## Why It Stands Out

- One CLI for both format conversion and parsed packet export.
- Stream-friendly operation for pipelines and automation.
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

Export packets as compact JSON to stdout:

```bash
ni export capture.pcapng --format json
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

- `--output`, `-o` Output path (`-` for stdout stream).
- `--output-format`, `--format`, `-f` Explicit output format spec (overrides extension).
- `--profile <name>` Profile name accepted for script compatibility with `export`.
- `--settings-path <dir>` Settings root accepted for script compatibility with `export`.
- `--max-frames`, `-n` Maximum number of frames to process.
- `--split-size <MB>` Split output at this size in MiB.
- `--split-count <N>` Split output every N frames.
- `--blf-cache-size <MB>` BLF cache budget in MiB.
- `--progress <N>` Report progress every N frames.
- `--tolerant` Skip malformed frames instead of aborting.

Format variants:

| Format | Variants |
| --- | --- |
| `pcapng` | `pcapng` (default), `pcap` |
| `blf` | `blf` (default compression), `blf:compression=off`, `blf:compression=fast`, `blf:compression=default`, `blf:compression=best` |

Examples:

```bash
# Convert BLF to PCAPNG
ni convert capture.blf --output capture.pcapng

# Split a large file into 100 MiB chunks
ni convert big.pcapng --output split/ --split-size 100

# Convert multiple sources into one output
ni convert a.blf b.blf --output merged.pcapng

# Stream converted output to stdout
ni convert capture.pcapng --output - --format pcapng
```

## ni export

Parse frames through the protocol stack and export one record per packet.

```text
ni export <input> [<input2> ...] [--format <fmt>] [--output <path>] [options]
```

Common options:

- `--format`, `-f` Export format spec.
- `--output`, `-o` Output path (omit or use `-` for stdout).
- `--max-packets`, `-n` Maximum packets to export.
- `--profile <name>` Settings profile name.
- `--settings-path <dir>` Base directory for settings storage.
- `--blf-cache-size <MB>` BLF cache budget in MiB.
- `--progress <N>` Report progress every N packets.
- `--tolerant` Skip malformed frames instead of aborting.

If `--format` is omitted, format is chosen from `--output` extension when possible. For stdout workflows, compact JSON is used by default.

Format variants:

| Format | Variants |
| --- | --- |
| `json` | `json:style=compact` (default), `json:style=pretty`, `json:style=array` |
| `pbf` | `pbf:format=standard` (default), `pbf:format=columnar`, `pbf:format=columnar,compressed`, `pbf:format=columnar,nocompress` |
| `text` | `text:level=summary`, `text:level=standard` (default), `text:level=full`, `text:truncate=<N>` |

For PBF, compression is enabled by default unless `nocompress` is specified.

Examples:

```bash
# Compact JSON to stdout
ni export capture.pcapng --format json

# Pretty JSON to file
ni export capture.pcapng --format json:style=pretty --output capture.json

# Human-readable protocol tree
ni export capture.pcapng --format text --output capture.txt

# Columnar PBF with compression
ni export capture.pcapng --format pbf:format=columnar,compressed --output capture.pbf

# Tolerant export with progress checkpoints
ni export unknown-input.blf --format text --output output.txt --tolerant --progress 50000
```

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
- `Ctrl+C` triggers cooperative cancellation for long-running operations.
- Use `-` for stream-oriented workflows where supported.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.CLI)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.CLI)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)
- [Root overview](../README.md)

## License

[MIT License](../LICENSE)
