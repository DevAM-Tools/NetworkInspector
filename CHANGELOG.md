<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [0.6.0] — Filter language, columnar exporters, ParseResult two-method API

Delta since `2edfc3cf` (0.5.0). Version is already `0.6.0` in `Directory.Build.props`.

### Added

- **`NetworkInspector.Filter`** — packable JIT filter language (`Filter.Compile` / `TryIsMatch` / `TryDerive`). Presence-index pruning, subtree `$Name[i?]{…}` scope, and stateful `flank`. Language spec: `NetworkInspector.Filter/FILTER_GUIDE.md`.
- CLI `convert` / `export` **`--filter <expr>`** — keep matching frames/packets. Convert stays a raw frame copy unless a non-empty filter forces parse.
- **Sessions** — `TryAddListener(..., IFilter?)` and filtered `ISessionReader` reads; listeners still notify unfiltered.
- **`NetworkInspector.Exporters` Parquet** — `ParquetExporter` writing a directory dataset (`packets` / `topology` / `catalog` / `fields/field_{id}.parquet`) via shared `ColumnarPacketBatch`. See `Parquet/README.md`.
- **`NetworkInspector.Exporters.DuckDb`** — standalone package for `DuckDbExporter`, writing a single `.duckdb` via `DuckDBAppender` bulk-load (txn-per-flush, single final `CHECKPOINT`). See `NetworkInspector.Exporters.DuckDb/README.md`. `NetworkInspector.Exporters` no longer depends on `DuckDB.NET.Data.Full`.
- Shared `NetworkInspector.Exporters/Columnar/` model: `FieldValueData` / topology / detail flags / `IColumnarBatchSink`.
- **`NetworkInspector.CLI.Core`** — class library hosting convert/export (ExitPointGaps-gated). The `ni` executable is a thin `CliEntry.Run` host. Not a NuGet package (`IsPackable=false`).
- CLI `export`/`convert` `--split-size` uses live `IExportByteProgress.EstimatedOutputBytes` (no filesystem size probes). Parquet/DuckDB also support `--split-count` (sibling dirs / numbered files).
- **Signal Message** — JSON-driven per-message automotive dissectors (`SignalMessageRegistration`, `Schemas/signal-message-config.schema.json`) replacing Signal PDU. FrameBuilder `SignalMessageLayer` / `SignalMessageLayout` match the decoder. Physical signal values are `FieldType.F64`.

### Changed

- **`ParseResult` is a named three-way result** (breaking, pre-1.0): **Ok** (consumed bytes), **`ParseResult.NotDispatched`** (table present, key has no protocol), and **Error**. Public consumption is exactly two methods: `TryPropagateError` (error path; `return` the `out` result) and `TryGetConsumed` (Ok path; `consumed == 0` on miss). `IsSuccess` / `IsNotDispatched` / `IsError` / `TryGetError` are internal. `Value`, `Error`, and `TryGetValue` are removed. `ParseErrorKind.NotDispatched` and `ParseError.NotDispatched` remain removed. Missing or invalid dispatch tables return `ParseErrorKind.ProtocolTableMissing`. Migration: `if (r.IsError) return r;` → `if (r.TryPropagateError(out ParseResult error)) return error;`; miss fallback `r.IsNotDispatched` → `!r.TryGetConsumed(out _)` after the error return; consumed-or-zero `TryGetValue` ternary → `_ = r.TryGetConsumed(out consumed);`.
- Settings read-only surface now returns struct views (`ReadOnlySettingView`, `ReadOnlySettingGroupView`) from `IReadOnlySettingsManager` / `IStack.Settings` instead of `IReadOnlySetting` / `IReadOnlySettingGroup`. Do not assign those structs to the interface types (boxing). Mutable `SettingsManager` / `SettingGroup.Settings` still expose `Setting`.
- Docs and comments no longer describe a shipped `ValueCacheSeries` / parse-time value-cache. That type was never implemented; eager field appends exist so downstream parsers and the presence index can read key fields without materialising lazy groups.
- **PBF format is v1** (magic `NETWORK-INSPECTOR-PBF-FORMAT-v1`). Columnar blocks use the shared `ColumnarPacketBatch` model with native typed columns (plain strings for `FieldType.String` / customs).
- **`StandardBlockBuilder`** — payloads and same-as-previous use Core `FieldValue` / `FieldValueData` plus `PreviousFieldValueStore` (no `FieldValueFormatter` on the Standard value path). JSON Compact uses typed `PreviousFieldStore` (`FieldValue`); PBF Standard uses `PreviousFieldValueStore` (`FieldValueData`).
- Packet IDs in the columnar batch / topology / Parquet / DuckDB schemas use Core `PacketId` as `int` (`INTEGER` / INT32). PBF wire still encodes packet-ID columns as sint64 varints.
- Parquet / DuckDB re-export overwrites prior artifacts at the target path.
- DuckDB export lives in `NetworkInspector.Exporters.DuckDb` (CLI still supports `--format duckdb`).
- Version bumped to `0.6.0`.

### Fixed

- **`Icmpv6Protocol` checksum used the outermost IPv6 addresses.** `_ValidateChecksum` scanned the flat field array with `TryGetFieldValue` (first occurrence wins). UDP/TCP walk previous siblings via `IpAddressExtractor` and take the innermost IP layer — required for tunneled packets. ICMPv6 now uses the same sibling walk.

### Removed

- **`LargeBuffer`** / `LargeBufferElement` / `LargeBufferStreamExtensions` — unused Core buffer type and tests.
- Signal PDU public names (`SignalPduProtocol`, `SignalPduConfig`, `SignalDecoder`) — replaced by Signal Message.
- Exporter-local `TypedFieldValue` / `FieldValueExtractor` — columnar and Standard sinks read `Field` / `FieldValueData` directly.
- String dictionary encoding across PBF Columnar, Parquet, and DuckDB (`StringDictionaryEncoder`, `GlobalStringDictionary`, `WithDictionaryEncoding`, dict index/raw columns and dict tables/files). Strings are stored plainly; a future dedicated compression approach may return later.
- Legacy PBF columnar helpers superseded by `ColumnarPacketBatch` (`ColumnBuilder`, `TopologyEncoder`, `DictionaryEncoder`, `DeltaEncoder`) — deleted from the tree.

---

## [0.5.0] — FlexRay dispatch key, CSharpStyleChecker, ExitPointGaps

Historical section restored from the 0.5.0 release commit (`2edfc3cf`). Changelog at that tag still ended at 0.3.0.

### Fixed

- **FlexRay `flexray.id` dispatch omitted cycle count.** Keys used slot + Channel B only; the same slot on the same channel in different cycles could bind the wrong sub-protocol (including Signal PDU). Keys now encode Frame ID, Channel B, and cycle count via `FlexRayLinkTypeFrame.EncodeDispatchKey`.

### Changed

- CSharpStyleChecker (with bundled ExitPoints) applied to SDK-style C# projects via `Directory.Build.targets`. ExitPointGaps registered as a local dotnet tool; the written release gate is `summary.exitGapCount == 0` (coverage was improved, not yet complete).
- MSBuild/CPM centralised: `RepoRoot`, LangVersion 14, AnalysisLevel `10-recommended`, test-project conventions, floating `CSharpStyleChecker` / `TUnit` `1.*`.
- Agent instructions split into `.github/skills/` (tech + workflow). `CUSTOM_INSTRUCTIONS.md` removed.
- **`.github/workflows/ci.yml` removed** from this repository (CI expected externally or restored later).
- Version bumped to `0.5.0`.

---

## [0.4.0] — ValueCache removal, field alias groups, protocol index fixes

Historical section restored from the 0.4.0 release commit (`8f9b096`). Changelog at that tag still ended at 0.3.0.

### Added

- **Field alias groups** — independent namespace (`eth.addr`, `ip.addr`, `udp.port`, …) on `IStackBuilder` / `IStack`. `GetFieldId` resolves canonical names only and never falls through to aliases.
- `IStack.FrameInterfaceRegistry` and `IncludeExceptionStackTrace` (no downcast to `Stack`).
- `NetworkInspector.Values.Tests` — dedicated coverage for IPv4/IPv6/MAC/EUI-64/UUID/Timestamp.
- Cross-platform GitHub Actions test job (tshark 4.6.x on Ubuntu/Windows/macOS). Removed again in 0.5.0.

### Changed

- TLS index groups: pre-pass over every record in the segment (not only the first) before recording handshake/SNI/ALPN/… groups.
- HTTP (and HTTP/2) body sub-protocol dispatch runs eagerly with a real `ParseContext` so nested index groups are recorded.
- Version bumped to `0.4.0`.

### Removed

- **ValueCache subsystem** (series, builders, `index.value_cache_fields`, `IPacketIndexReader` value-cache members). Presence index is bitmap-only.

---


## [0.3.0] — Bug fixes, copyright update, dependency cleanup

### Fixed

- **`AscExporter` — CAN XL frames misidentified as CAN classic/FD:** CAN XL frames share `LinkType.CanSocketcan` with classic CAN and CAN FD but are distinguished by the XLF bit (bit 7 of byte 4 in the SocketCAN header). Previously the exporter forwarded CAN XL frames into `TryParseCanFrame`, which interpreted the 12-byte CAN XL header as a malformed classic/FD frame. An early XLF-bit guard now rejects CAN XL frames as `ExportErrorKind.UnsupportedType` before any CAN parsing occurs.
- **`StackBuilder` — post-parser execution order not deterministic:** `RegisterPostParser` appended post-parsers in registration order without sorting, and `Build()` copied the list unsorted. The documented contract (ascending `Priority`, then ascending `Id` as a stable tie-breaker) was not honoured. `RegisterPostParser` now re-sorts the list after every call, and `Build()` sorts the final snapshot before freezing it into the `Stack`.

### Added

- `NetworkInspector.Core.Tests/PostParserTests.cs` — 884-line test file covering sort order with mixed, equal, and negative priorities; full lifecycle (all post-parsers execute, correct protocol context, root-level parent); indexed-parse integration; exception isolation (one failing post-parser does not suppress subsequent ones); and `StackBuilder` round-trips.
- `NetworkInspector.Exporters/README.md` — top-level package readme added to the Exporters project; now included in the NuGet package via `PackageReadmeFile`.

### Changed

- **Copyright notices** — all source files updated to the canonical form `Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.` The `COPYRIGHT` root file now includes the licence statement.
- **`CUSTOM_INSTRUCTIONS.md`** — added an "Implementation Guides" table listing the four mandatory per-component guides (protocol dissectors, frame source readers, exporters, FrameBuilder layers) with their applicability conditions.
- **`NetworkInspector.Core.csproj`** — replaced the hard-coded generator DLL pack path with a dynamic `_PackGenerator` MSBuild target that calls `GetTargetPath` on the generator project, ensuring the correct Debug/Release artifact is always selected.
- **`NetworkInspector.Exporters.csproj`** — added `PackageReadmeFile` and the corresponding `<None Include="README.md" />` pack item so the readme appears on NuGet.org.
- **`Directory.Packages.props`** — `Microsoft.CodeAnalysis.Analyzers` updated from `5.3.0` to `5.3.0-2.25625.1`.
- **`NetworkInspector.Generators.csproj`** — package reference order corrected: `Microsoft.CodeAnalysis.Analyzers` now precedes `Microsoft.CodeAnalysis.CSharp`.
- **`.gitignore`** — added `.vs` directory exclusion.
- Version bumped to `0.3.0`.

---

## [0.2.0] — FrameBuilder, Sources, Exporters, CLI and Test Projects

### Added

**`NetworkInspector.FrameBuilder`** — Typed, allocation-free frame builder library:
- Generic cons-list API for composing protocol stacks at compile time with full type safety.
- Layers: Ethernet, VLAN, Linux SLL/SLL2, LLC/SNAP, IPv4, IPv6 (with extension headers), TCP, UDP, ICMP, ICMPv6, ARP, DHCP/DHCPv6, DNS, TLS, DTLS, HTTP/1.x, WebSocket, SOME/IP, PDU Transport, Signal Message, CAN (classic/FD/XL), FlexRay, LIN.
- Automatic checksum calculation, IP fragmentation, TCP segmentation, and pseudo-header computation.
- Stateful builder for multi-frame streams (TCP sessions, fragmented IP, etc.).

**`NetworkInspector.Sources`** — Frame source readers for capture file formats:
- PCAPNG reader with streaming, random-access, error-tolerance, and memory-mapped I/O.
- Vector BLF reader with LZ4 container decompression, multi-channel demux, and configurable cache budget.
- Vector ASC reader with timestamp normalization and channel filtering.
- Random frame source for synthetic test data generation.
- `CachedFrameSource` — in-memory caching wrapper for repeated random-access reads.

**`NetworkInspector.Exporters`** — Frame and packet exporters:
- PCAPNG exporter with optional LZ4 compression and SHB/IDB metadata.
- BLF exporter with configurable compression (off / fast / best).
- CSV exporter with configurable column definitions and delimiter.
- JSON exporter (compact, pretty, array styles).
- PBF exporter (row-oriented and columnar with optional LZ4 compression).
- ASC exporter for Vector CANalyzer ASCII log files (CAN classic, CAN FD, LIN, FlexRay).
- Plain-text exporter with configurable verbosity levels (summary / standard / full) and value truncation.

**`NetworkInspector.CLI`** — Command-line tool (`ni`):
- `ni convert` — Frame-level format conversion between PCAPNG, BLF, and more. Supports file splitting, progress reporting, error tolerance, and BLF cache budgets.
- `ni export` — Parse and export packets to JSON, PBF, or plain-text with full protocol stack dissection. Supports settings profiles.

**Test Projects:**
- `NetworkInspector.Core.Tests` — 100% coverage of Core internals (roaring bitmap, slab allocator, etc.).
- `NetworkInspector.Protocols.Tests` — Per-dissector tests for all 30 protocols including tshark UAT integration tests.
- `NetworkInspector.FrameBuilder.Tests` — Smoke tests, integration tests, and negative compilation tests for FrameBuilder.
- `NetworkInspector.Sources.Tests` — Reader tests for PCAPNG, BLF, ASC, random, and cached sources.
- `NetworkInspector.Exporters.Tests` — Exporter tests for all formats including round-trip verification with tshark.
- `NetworkInspector.Testing.Tshark` — Shared tshark test helper library used by Protocols.Tests, FrameBuilder.Tests, and Exporters.Tests.

### Changed
- Version bumped to `0.2.0`.
- `NetworkInspector.Core.csproj`: added `InternalsVisibleTo` for `NetworkInspector.Core.Tests` and `NetworkInspector.Exporters.Tests`.
- `NetworkInspector.Protocols.csproj`: added `InternalsVisibleTo` for `NetworkInspector.Protocols.Tests`.
- `NetworkInspector.Exporters` — fixed CA2014: moved `stackalloc` for timestamp formatting buffer outside the column loop in `CsvExporter`.
- `Directory.Packages.props`: added `TUnit` package version entry.

---

## [0.1.0] — Initial Release

### Added

**`NetworkInspector.Core`**
- Protocol stack (`StackBuilder` / `Stack`) — build, configure, and freeze the protocol registry; parse raw frames into typed packet trees.
- Flat field tree — fields stored as a contiguous array of `FieldBody` structs; parent/child/sibling relationships encoded as `ushort` indices for cache-efficient traversal with zero heap pointers.
- Thread-local slab allocators (`SlabAllocator<T>`) — eliminate per-packet GC allocations; three slabs per parsing thread (FieldBody, ChunkDescriptor, LazyPopulator).
- Lazy fields (`LazyPopulator`) — deferred, cached field materialization for expensive computations (formatting, crypto, deep inspection).
- `PacketIndex` — cross-packet roaring bitmap index for constant-time presence queries across captures.
- `FieldValue` — 16-byte discriminated union covering all field types without heap allocation.
- `ParseResult` — 4-byte struct encoding consumed byte count and error code; no exceptions on parse errors.
- Typed dispatch tables (`ProtocolTable`) — `U8`, `U16`, `U32`, `U64`, `String`, and heuristic variants; protocol routing built at stack-construction time with no runtime reflection.
- `DatagramDefragmenter` — IPv4 fragment reassembly with roaring-bitmap completion tracking and FIFO eviction.
- `TwoQueueCache<K,V>` — generic 2Q eviction cache with weight-based capacity.
- `SettingsManager` — typed runtime settings with JSON persistence; loaded by generated `RegisterFields`.
- Roslyn source generator (`NetworkInspector.Generators`, bundled) — processes `[Protocol]`-annotated partial classes and generates: field ID assignment, `RegisterFields`, `OnStart`, `OnShutdown`, dispatch table registration, setting loading, index group wiring, and public name constants. Supported field attributes: `[NoneField]`, `[BoolField]`, `[I64Field]`, `[U64Field]`, `[F64Field]`, `[StringField]`, `[BytesField]`, `[MacField]`, `[IPv4Field]`, `[IPv6Field]`, `[Eui64Field]`, `[UuidField]`, `[TimestampField]`. Diagnostics NIGEN001–NIGEN013.

**`NetworkInspector.Protocols`** — 30 built-in dissectors:
- Link: `FrameProtocol`, `EthernetProtocol`, `VlanProtocol`, `SllProtocol`, `Sll2Protocol`, `LlcProtocol`
- Network: `IPv4Protocol` (with fragment reassembly), `IPv6Protocol` (with extension headers), `ArpProtocol`, `IcmpProtocol`, `Icmpv6Protocol`
- Transport: `TcpProtocol` (with options and heuristic sub-protocol detection), `UdpProtocol`
- Application: `DnsProtocol`, `DhcpProtocol`, `Dhcpv6Protocol`, `HttpProtocol` (HTTP/1.x), `Http2Protocol`, `TlsProtocol` (1.0–1.3), `DtlsProtocol`, `WebSocketProtocol`, `JsonProtocol`, `TextProtocol`
- Automotive: `CanProtocol` (CAN classic / FD / XL), `FlexRayProtocol`, `LinProtocol`, `SomeIpProtocol`, `PduTransportProtocol`, Signal messages via `SignalMessageRegistration`
- Fallback: `DataProtocol`

**`NetworkInspector.Values`** — strongly-typed value types:
- `MacAddress` — 48-bit EUI-48, formatted as `00:1a:2b:3c:4d:5e`
- `IPv4Address` — 32-bit, dotted-decimal notation
- `IPv6Address` — 128-bit, RFC 5952 compressed notation
- `Eui64` — 64-bit EUI-64 identifier
- `Uuid` — 128-bit UUID
- `Timestamp` — nanosecond-precision UNIX timestamp with arithmetic operators and ISO 8601 formatting
