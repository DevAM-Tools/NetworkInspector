<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.CLI

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.CLI?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.CLI)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.CLI?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.CLI)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

`ni` converts and exports network and automotive captures from the command line: PCAP, PCAPNG, Vector BLF, and
Vector ASC in; PCAPNG, BLF, ASC, CSV, JSON, Text, PBF, Parquet, and DuckDB out. Filter with Wireshark-style
expressions, split large outputs, extend it with plugins, keep settings in profiles, and script it with a stable
exit-code contract.

## Highlights

- **Two commands for everything.** `ni convert` copies frames between capture formats without parsing; `ni export`
  parses every packet and writes analysis formats.
- **From capture to SQL in one line:** `ni export capture.pcapng -o packets.duckdb`.
- **Filters on both commands.** `--filter "udp.dstport == 53"`; a filter that matches nothing writes no file.
- **Large captures.** Split by size or count, cap the record count, report progress, and cancel with Ctrl+C: finished
  split files stay, the output that was still open is removed.
- **Automotive ready.** CAN, CAN FD, CAN XL, LIN, FlexRay, and automotive Ethernet between BLF, ASC, and PCAPNG.
- **Plugins and profiles.** Sources, exporters, and protocols come from plugins (the built-in ones use the same
  contract); `ni settings` and `ni profile` read and change settings per profile.
- **Script friendly.** Exit codes 0–3, diagnostics on stderr, UTF-8 output, invariant culture. Existing outputs are
  never replaced without `--overwrite`.

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
| `export` | always | Parsed packets as CSV, JSON, Text, PBF, Parquet, or DuckDB |
| `plugins` | no | List the plugins of this run and what each contributes |
| `settings` | no | List, read, set, and reset application and profile settings |
| `profile` | no | List, create, rename, and delete settings profiles |

Run `ni <command> --help` for the built-in help.

### Inputs

Both commands take one or more source specs. The reader is chosen by file extension (case-insensitive):
`.pcap` and `.pcapng` (PCAP or PCAPNG, detected by magic number), `.blf`, `.asc`. Typed specs work too:
`pcap:path=…`, `blf:path=…`, `asc:path=…`. `random:count=N,seed=S,mode=M` generates synthetic traffic
(`count` default 1000, `0` = unlimited; `seed` default 42; modes `udp4` (default), `udp6`, `ipv4`, `ipv6`,
`ethernet`, `can`, `canfd`, `random`). Plugins add source kinds: `SampleGen:count=10,transport=tcp` is the form
`kind:key=value,key=value`, where the keys are the option settings of the kind and the values use the text form of
[`ni settings`](#ni-settings). A misspelled key is refused with the closest valid one
(`Unknown option 'cout'; did you mean: count?`), and so is a pair without a key or without `=`
(`random:count=5,flag` is an error, not a source with a dropped option; a doubled or trailing comma is ignored).
`ni plugins list` shows which kinds exist.

The `random` source checks its ranges when it is created and names the option and the range in the error: `maxFrameSize` is
at most 65 549 for `ipv4`, `udp4`, and `tcp4`, at most 65 589 for `ipv6`, `udp6`, and `tcp6`, and at most 2 147 483 591 for
`fullrandom` and `ethernet` (the modes `can`, `canfd`, `tcpstream4`, and `tcpstream6` ignore the frame sizes);
`framesPerSecond` is 0.001 to 1 000 000 000 and must be high enough that the timestamps of the last frame fit in 64 bits
(at least 0.233 for `count=0`, an unlimited source).

Several inputs are interleaved one frame at a time (round-robin), not concatenated and not merged by timestamp.
Source specs must not contain shell metacharacters (`| & > < ; \` ( ) { }`); rename such files first.

## ni convert

Frame-level conversion. Without `--filter` no frame is parsed (the protocol stack of the profile is built anyway, because
every exporter request carries it; that takes a few milliseconds).

```text
ni convert <input> [<input2> ...] --output <path> [options]
```

| Option | Meaning |
| --- | --- |
| `-o`, `--output <path>` | Output file (required; `-` for stdout is not supported) |
| `-f`, `--format`, `--output-format <spec>` | Output format; overrides the extension |
| `--overwrite` | Replace an existing output (without it an existing output is an error) |
| `-n`, `--max-frames <N>` | Maximum frames to write |
| `--split-size <MiB>` / `--split-count <N>` | Split the output by size or frame count |
| `--filter <expr>` | Keep frames whose packet matches (parses each frame, without a field tree when the expression allows) |
| `--verbose` | Print one line `parse: …` with the parse work chosen (`parse: none` without a filter) |
| `--progress <N>` | Report progress every N frames on stderr |
| `--tolerant` | Skip unreadable records instead of aborting; a source that fails again before it delivered a frame is dropped |
| `--blf-cache-size <MiB>` | BLF container cache budget (max 2047) |
| `--profile <name>`, `--settings-path <dir>` | Protocol settings profile and storage (used with `--filter`) |
| `--set <key>=<value>` | Override one setting for this run only (repeatable; nothing is written). A limit takes a number with a unit such as `64MiB`, or `off`, for example `--set settings.maxFileSize=1GiB`. See [`ni settings describe --limits`](#ni-settings) |

Output formats:

| Spec | Output | Inferred from |
| --- | --- | --- |
| `pcapng` (alias `pcap`) | PCAPNG | `.pcapng`, `.pcap`, no or unknown extension |
| `blf`, `blf:compression=off\|fast\|default\|best` | BLF (default compression `default`) | `.blf` |
| `asc` | ASC | `.asc` |

There is no classic PCAP writer: `pcap` and `-o x.pcap` write PCAPNG. Frame formats from plugins appear in the same
way as built-in ones (`ni plugins list` shows what a plugin contributes); a packet format given to `convert` is refused
with the command that writes it
(`Format 'CSV' writes packets; use 'ni export'.`).

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
| `--overwrite` | Replace an existing output (without it an existing output is an error) |
| `-n`, `--max-packets <N>` | Maximum packets to write |
| `--split-size <MiB>` | Split when the live output estimate reaches this size |
| `--split-count <N>` | Split every N packets |
| `--filter <expr>` | Export only matching packets |
| `--progress <N>`, `--tolerant`, `--blf-cache-size <MiB>` | As for `convert` |
| `--verbose` | Print one line `parse: …` before the first frame that says which parse work the run chose (see Choosing what to export) |
| `--profile <name>`, `--settings-path <dir>`, `--set <key>=<value>` | Protocol settings, and a one-run override of any setting |

Export formats:

| Spec | Variants | Inferred from |
| --- | --- | --- |
| `csv` | `csv:delimiter=;`, `header=false`, `bom=false`, `fields=ip.src;udp.dstport`, `separator=#` (between several occurrences in one cell, default `\|`), `neutralizeFormulas=false` | `.csv` |
| `json` | `json:style=compact` (default), `pretty`, `array` | `.json` |
| `text` | `text:level=summary\|standard\|full`, `text:truncate=<N>` (default 256, `0` = no limit) | `.txt` |
| `pbf` | `pbf:format=standard\|columnar`, `compressed` (default) or `nocompress` | `.pbf` |
| `parquet` | directory dataset; `parquet:compression=none\|snappy\|gzip\|brotli\|lz4\|zstd`, `compressionLevel=fastest\|optimal\|smallest`; splits become `base_00001`, … | — (use `-f parquet`) |
| `duckdb` | one file; splits become `base_00001.duckdb`, … | `.duckdb` |

Parameters combine with commas: `text:level=full,truncate=0`, `pbf:format=columnar,nocompress`. Any other
extension needs `-f`. CSV writes a UTF-8 byte order mark and a header row by default; without `fields` or `shape` it writes
the number, time, info, and length of each packet. The delimiter is one ASCII character other than NUL, CR, LF, the
double quote, an ASCII letter or digit, and `-+.:` (a tab is allowed, and so is `;`). Packet formats of plugins work the same way
(`-f SampleText:includeHeader=false`).

**CSV and spreadsheets.** Text from a capture is chosen by whoever sent the traffic, and a spreadsheet runs a cell that
starts with `=`, `+`, `-`, `@`, a tab, or a carriage return as a formula (CSV injection). By default `csv` therefore puts
an apostrophe in front of such a text cell (the info column and text fields; numbers, times, addresses, and the header row
are not changed). Pass `csv:neutralizeFormulas=false` for machine processing, where the apostrophe would change the data;
a file written that way is unsafe to open in a spreadsheet.

The options `timestampSorted` of `pbf` and `parquet` only declare that the packets arrive in timestamp order; nothing is
sorted and the written bytes do not change.

```bash
# Pretty JSON
ni export capture.pcapng -o capture.json -f json:style=pretty

# Columnar PBF without compression
ni export capture.pcapng -o capture.pbf -f pbf:format=columnar,nocompress

# Parquet dataset, a new directory every 50 000 packets
ni export capture.pcapng -o out_parquet -f parquet --split-count 50000

# Only port 443, tolerant of damaged records, progress every 50 000 packets
ni export unknown.blf -o tls.json --filter "tcp.port == 443" --tolerant --progress 50000

# Two fields, first occurrence, as a DuckDB database (no field tree is built)
ni export capture.pcapng -o dns.duckdb -f "duckdb:fields=ip.src;dns.qry.name,occurrence=first"

# Every field without hierarchy, CSV long layout
ni export capture.pcapng -o all.csv -f "csv:shape=all"

# Parquet dataset with Snappy instead of the default Zstandard (for a tool that predates Zstandard)
ni export capture.pcapng -o out_snappy -f parquet:compression=snappy

# Add the raw frame of every packet (off by default; the field tree already holds the parsed bytes)
ni export capture.pcapng -o frames.json -f json:frames=true

# Add the additional buffers of every packet: the reassembled datagram, TCP PDU, decoded HTTP body (off by default)
ni export capture.pcapng -o out.duckdb -f duckdb:buffers=true

# A DuckDB file that DuckDB 1.2 can open (the default v1.5.0 needs DuckDB 1.5 or later)
ni export capture.pcapng -o old.duckdb -f duckdb:storageVersion=v1.2.0

# Show which parse work the run chose
ni export capture.pcapng -o out_parquet -f "parquet:fields=ip.src;udp.dstport" --verbose
```

### Choosing What To Export

Every field format (`csv`, `json`, `text`, `pbf`, `parquet`, `duckdb`) takes the same options, so `duckdb:fields=…` and
`csv:fields=…` behave alike. An option a format does not have (for example `separator` for `json`) is refused with a message that names it.

| Option | Meaning | Default |
| --- | --- | --- |
| `fields=a;b:first;c:last` | The fields to write, separated by `;` (a comma would end the option). `:all`, `:first`, `:last` after a name picks the occurrence | none |
| `occurrence=all\|first\|last` | Occurrence of the fields without their own choice | `all` |
| `shape=auto\|fields\|all\|hierarchy` | `fields`: only the listed fields; `all`: every field, flat; `hierarchy`: every field with parent and order. `auto` is `fields` when `fields` is set, else the format default (CSV also knows `packets`: one row per packet) | `auto` |
| `info` | Write the packet info string (DuckDB, Parquet, PBF, JSON; CSV has the `Info` column in the packet rows, Text shows it as a field line) | `true` |
| `frames` | Write the raw frame bytes of every packet, in every packet format: a `frame_bytes` column (DuckDB, Parquet, PBF columnar, CSV), a packet field (PBF standard), a Base64 key (JSON), a hex dump after the packet (Text). Off by default: the field tree already holds the parsed bytes. CSV adds the column last and only for packet rows and `shape=fields`; `shape=all` and `shape=hierarchy` refuse it | `false` |
| `buffers` | Write the additional buffers of every packet (reassembled IPv4 or IPv6 datagrams, TCP PDUs, decoded HTTP bodies, unmasked or inflated WebSocket payloads) with their index, in every packet format: a `packet_buffers` table (DuckDB) or file (Parquet), a block field (PBF columnar), a packet field (PBF standard), a `buffers` key of Base64 records (JSON), a `buffers` column of `<index>:<HEX>` records (CSV), a hex dump per buffer after the packet (Text). Off by default. A buffer can hold decoded content that the frame does not show in clear and can be much larger than the frames. CSV adds the column after the frame column, only for packet rows and `shape=fields`; `shape=all` and `shape=hierarchy` refuse it. The frame formats (`pcapng`, `pcap`, `blf`, `asc`) have no such option | `false` |
| `text`, `repr` | Write the custom text / custom representation of a field, only where a dissector set one (an absent text is NULL, a missing key, or an empty cell; see the [exporter guide](../NetworkInspector.Exporters/EXPORTER_GUIDE.md#123-rebuilding-the-display-line)). Off writes none | `true` |
| `writeMode=background\|sync` | DuckDB, Parquet, PBF, BLF: write full batches on a writer thread while the next fills, or inline | `background` |
| `batchPackets=<N>`, `batchBytes=<N>` | DuckDB, Parquet, PBF: packets (1 – 2 147 483 647) and estimated bytes (≥ 1) per batch. A batch is held in memory until it is written; the defaults are secure | 50 000 / 16 MiB |
| `containerSize=<bytes>` | BLF: size of one log container (65 536 – 67 108 864) | 10 MiB |
| `compression=none\|snappy\|gzip\|brotli\|lz4\|zstd` | Parquet: the codec of every column chunk of every file of the dataset. `zstd` gives small files and is read by current tools; `snappy` is fast and supported by the common tools; `gzip` is smaller than `snappy` but slower; `brotli` is small and slow and not every tool reads it; `lz4` is written as `LZ4_RAW` (older tools may not read it); `none` writes no compression. Any other value is refused with the allowed values and no dataset is created | `zstd` |
| `compressionLevel=fastest\|optimal\|smallest` | Parquet: how hard the codec works (quickest write and largest files, balance, smallest files and slowest write). `none` and `snappy` ignore it | `optimal` |
| `storageVersion=v1.0.0\|v1.1.0\|v1.2.0\|v1.3.0\|v1.4.0\|v1.5.0` | DuckDB: the storage format of the file, which decides the oldest DuckDB release that opens it (`v1.2.0` opens in DuckDB 1.2 and later). Newer versions allow newer encodings; the effect on the file size depends on the data. Any other value is refused with the allowed values and no file is created; there is no compaction option | `v1.5.0` |

**Why the shape matters for speed:** an export of a few fields does not need the field tree, and building the tree costs more
than the export itself. `ni export` therefore asks the exporter and the filter what they need and parses with exactly that. With
`--verbose` the run prints which choice it made:

```text
parse: fields only, no tree (2 fields, 1 observer)           # fields=… and no filter, or a filter on plain fields
parse: packet only, no tree                                   # CSV packet rows
parse: field tree                                             # shape=hierarchy, json, text, pbf standard without fields
parse: field tree — filter needs the field tree: <reason>    # a subtree scope or a protocol presence term in --filter
```

`ni convert` without `--filter` parses nothing at all (`parse: none`); with a filter it parses without field tree when the
expression allows. A filter that needs the field tree makes the whole run build it, and `--verbose` says why.

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
dataset directory). An existing output, including a split file left over from an earlier run, is an error
unless `--overwrite` is given. With `--overwrite` the old output is moved aside while the new one is written and deleted only
when the new one exists: a run that is refused or cancelled gives the old output back. A folder (Parquet dataset) is replaced
only when it is empty or was written by an earlier export (it holds the file `.ni-export`); any other folder is refused, so a
mistyped `-o` cannot delete your files. A run that ends early (error or Ctrl+C) deletes the output that was still open and keeps
the splits that were finished.

## ni plugins

```text
ni plugins list [--json] [--profile <name>] [--settings-path <dir>]
```

Lists the plugins of this run: the built-in plugins and those found in the folders of the application setting
`plugins.paths`. The first line is the pre-scan summary (candidate files, inspected files, files served from the scan
cache, plugin assemblies, time). Each plugin line has the name, version, tier, state (`Loaded`, `Skipped`, `Failed`), the
reason for a state other than `Loaded`, path, license, load time, and what the plugin contributes (sources, exporters,
protocols, settings). A plugin that fails never stops the command. `--json` writes one JSON document instead.

```bash
ni settings set plugins.paths ./plugins/Sample.Protocol --scope application
ni plugins list
ni export SampleGen:count=10 -o out.json
```

Starting and stopping the plugins is bounded by the application settings `plugins.startTimeoutSeconds` and
`plugins.stopTimeoutSeconds` (defaults 120 s and 60 s). Plugin log lines (`<level> <plugin>: <text>`) go to the error
output and to [`logs/ni.log`](#profiles-and-settings). How to write a plugin: [plugin guide](../docs/plugins/guide.md).

## ni settings

```text
ni settings list  [--scope application|profile|all] [--group <group>] [--json]
ni settings get   <name> [--scope application|profile] [--group <group>]
ni settings set   <name> <value> [--scope application|profile] [--group <group>]
ni settings reset <name> [--scope application|profile]
ni settings describe <name> [--format text|json]
ni settings describe --limits [--format text|json|markdown]
```

Reads and changes the application settings and the settings of one profile (`--profile`, default `default`), the
settings of plugins included. `list` prints `scope  name  type  value` per line (`--json` adds the default, the
description, the activation, and whether a value is stored); `get` prints the value; `set` prints `name = value`;
`reset` removes the stored value so that the default applies again. `describe` says what a setting is; for a limit it
prints the unit, the value and the default, the allowed range, and in plain words what the limit bounds, why it exists, how
it helps, what a smaller and a larger value do, and what `off` means. `describe --limits` lists every limit (a table, a JSON
array with the same fields as one key, or Markdown with one section per limit).

- **Limits.** The limits of the settings library are settings of the application scope: `settings.maxFileSize` (64 MiB),
  `settings.maxJsonDepth` (64), `settings.folderLockTimeout` (2 s), `settings.maxGroupFiles` (1,024), and
  `settings.maxTextLength` (1,048,576 characters). A limit takes a number with a unit (`256MiB`, `90s`) or `off`; a decimal
  unit such as `MB` is refused with the binary spelling. A stored limit lives in `application/settings.json`, is read first,
  and bounds every other settings file of the run.
- **`--set <key>=<value>`.** Every command accepts it, any number of times. The value is in effect for this run only and
  nothing is written (`settings set` and `settings reset` refuse it, because they store or remove values); a repeated key
  takes the last value. The loader limits are in effect before the first settings file is read, so
  `ni settings list --set settings.maxFileSize=1GiB` loads a file that the stored limit would skip. An unknown key or a value
  that does not fit is refused before any work, with the same message as `settings set` (and the closest known names).
  `--settings-max-config-file-bytes` and `--settings-max-json-depth` are gone: the program names the `--set` to use.

- **Text form.** Values are `true`/`false`, numbers, hex bytes (`0A1B`), enumeration names, text, and arrays as JSON
  (`[1,2]`) or comma lists. A value that does not fit is refused with one line that names the setting, the problem, and the
  accepted range or values; an unknown name is refused with the closest known names.
- **Nothing changes on a refusal.** Every check happens before a file is touched.
- **Scope.** A name that exists in both scopes needs `--scope`; `--group` narrows a lookup by group.
- **Paths.** Relative folders of `plugins.paths` are stored as full paths.
- **Plugins that are not loaded.** A stored value whose plugin is not loaded is listed as `(not loaded)` and takes
  JSON as its value.

```bash
ni settings list --scope profile --group tcp
ni profile create CAN
ni settings set tcp.relative_sequence_numbers false --profile CAN
ni export capture.pcapng -o can.json --profile CAN
```

## ni profile

```text
ni profile list
ni profile create <name> [--copy-from <profile>]
ni profile rename <old> <new>
ni profile delete <name>
```

Manages the profile folders under the settings base. No plugin is loaded. A name has 1 to 256 letters, digits, spaces,
dots, dashes, or underscores and is not one of the reserved folder names (`application`, `logs`, `plugins`,
`plugins-data`, `plugin-scan-cache`, `host`, `app`, `presets`). The last profile cannot be deleted. The CLI cannot see
an application that uses a profile: renaming or deleting a profile that a running application has open changes the
folder under that application.

## Profiles And Settings

The settings base is `NetworkInspector` in the application data folder of the user, or the folder given with
`--settings-path`.

| Path under the base | Content |
| --- | --- |
| `application/` | Application-scope settings, shared by all profiles (`plugins.paths`, timeouts) |
| `<profile>/` | Settings of one profile; `default` is created on first use |
| `logs/ni.log` | Log file (10 MiB, 5 files); plugin log lines and problems |
| `plugin-scan-cache.json` | Cache of the plugin pre-scan |

- **Sparse files.** Only values that differ from their default are stored, one JSON file per settings group. A setting
  that is never changed adds no file, and setting a value back to its default removes the key, so a later change of a
  default reaches every profile that did not choose a value.
- **Merge writes.** `ni settings set` and `reset` change their own key only and keep every other key of the file, also
  keys that another process wrote in the meantime. The folder lock `.ni-settings.lock` serializes writers of several
  processes; a writer that waits more than two seconds fails with "in use by another process; try again".
- **Profiles apply.** `--profile <name>` is used by every command. A profile that does not exist is an error; `default`
  is created on first use.
- **Breaking change.** The default profile used to be the base folder itself; it is now `<base>/default`. Files in the
  base folder were never applied by the CLI, so no setting stops working; move them into `default/` to keep them.

## Exit Codes

| Code | Meaning |
| ---: | --- |
| `0` | Success, also after Ctrl+C (`Export cancelled.` / `Conversion cancelled.`; the unfinished output is removed) |
| `1` | Usage or validation error: missing or unknown option, unknown input extension, bad format spec or option, a source option without `key=value`, unknown setting or profile, a profile name or operation that the rules refuse, an existing output without `--overwrite`, filter that does not compile |
| `2` | Source cannot be opened or read: missing file, invalid file header |
| `3` | Runtime failure during conversion or export, including a packet that fails to parse, a filter that fails to evaluate, a split file that already exists, an output format that stops taking records before the input is finished, a profile operation that fails in the file system (`ni profile create` below a file, a folder that cannot be moved), and a settings folder that stays locked by another process |

## Operational Notes

- `--tolerant` skips records the reader cannot read. A source that fails again right after a failure, without delivering
  a frame in between, cannot move past the problem: it is dropped with a warning that names it, and the other inputs carry
  on, so a source that fails on every read cannot keep a run alive forever. A packet that the parser rejects still stops
  `export` with code `3`.
- In a split run the refusal of an existing output names the first split file (`base_00001.ext`), the first path that
  would be written, not the base name that was typed.
- Progress, warnings, and the completion summary go to stderr; the console uses UTF-8.
- Ctrl+C cancels cooperatively. Outputs that were finished stay; the output that was still open is deleted, because it
  is not a complete result. The same happens after an error. A file that `ni` refused to overwrite is never deleted.
- Commands and options are case-insensitive. `-h`, `--help`, `-?` work as the first argument after the command.

## Safe Usage (STRIDE)

- **Spoofing**: Prefer captures from trusted, attributable sources.
- **Tampering**: Use `--tolerant` for unknown files and inspect the warnings on stderr.
- **Repudiation**: Keep inputs, outputs, and the command line together for reproducibility.
- **Information disclosure**: JSON, Text, and columnar outputs can contain payload secrets; protect them.
- **Denial of service**: Use `--max-frames`, `--max-packets`, split options, and the settings size limits on
  untrusted input. The protocol settings bound the state that parsing keeps: `tcp.max_tracked_streams` and
  `udp.max_tracked_streams` (default 10 000 streams each), `http.max_decompressed_bytes` and
  `ws.max_decompressed_bytes` (default 64 MiB), and the bytes that one packet keeps after decoding or reassembling
  (`http.max_derived_bytes_per_packet` and `ws.max_derived_bytes_per_packet`, default 256 MiB, counted over all bytes bound to the packet;
  `someip.tp_max_derived_bytes_per_packet`, default 64 MiB); `ni settings list --scope profile` shows them with their ranges.
- **Elevation of privilege**: Run `ni` with least-required file-system permissions.

## Learn More

- [Exporters](../NetworkInspector.Exporters/README.md) (formats in detail) ·
  [Sources](../NetworkInspector.Sources/README.md) (readers) · [Filter](../NetworkInspector.Filter/README.md) ·
  [Root overview](../README.md)
- The `ni` executable is a thin host over the `NetworkInspector.CLI.Core` library (`CliEntry.RunAsync`).
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
