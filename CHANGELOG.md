<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added

- **`ValueCache`** (`NetworkInspector.Core.ValueCaches`) — RAM columnar series for selected fields (or all fields), filled by `RecordPacket` or parse-time tee. Capture modes first/last/all occurrence; optional custom text and custom representation series; sticky packet-id and timestamp monotonic flags; optional row/byte limits. `ValueCacheReaderView` is the read-only façade.
- **`Packet.ParseFrameRecorded` / `TryParseFrameRecorded`** — first-parse tee into a `ValueCache`, including indexed overloads and recycle variants. Replays do not record.
- **Session ingest and runtime value caches** — `SessionOptions.ValueCache` / `ValueCacheListener`; `ISession.TryAddValueCache`; `IValueCacheListener.OnNewRows`; `ISessionReader.IngestValueCache` and `GetValueCaches()`. Restart abandons writers and rebinds surviving slots.
- Profiling scenarios: `session-value-cache-ingest-all-fields`, `session-value-cache-ondemand-all-fields`, `session-value-cache-ingest-udp-srcport`. Session value-cache scenarios construct a new `Stack` and `Session` per `Run` so packet ids are first-parses (replays do not tee). `value-cache-build-all-fields` and `parse-random-frames-recycled-recorded` allocate a fresh `ValueCache` per `Run`. `session-listener` pulls packets without `MaterializeAll`; `session-listener-materialized` keeps the old full-tree walk. `random-source-parse` / `random-source-parse-materialized` are the no-session counterparts.

### Changed

- **Breaking (pre-1.0): `Packet.TryGetFieldAt` is internal.** Storage indexes stay packet-owned. External navigation uses `RootField()`, Field parent/child/sibling APIs, `IterFieldsDfs` / `IterFieldsFlat`, or `TryGetFieldValue` / `TryGetNextField`.
- Session value-cache bind checks field and group names with `NameValidation.IsValidName` (same rule as stack registration). Invalid identifiers throw `SessionException(ValueCacheInvalidFieldName)` at construction, `TryAddValueCache`, and Restart; well-formed names missing from the stack still throw `ValueCacheUnknownField`.
- `ValueCache.Abandon()` and `IsAbandoned` are public. `ValueCacheReaderView.IsAbandoned` forwards that flag. Core no longer grants `InternalsVisibleTo` to `NetworkInspector.Sessions` or `NetworkInspector.Sessions.Tests`.
- `ValueCache` parse tee uses a compact field-id array (linear scan) when at most 16 fields are recorded, otherwise a dense probe plus a bitset miss. There are no `FrozenDictionary` lookups. Unrecorded parse keeps a predicted null check on `Packet._ActiveValueCache` and a `NoInlining` stub so the probe cannot inflate `AppendChild`. Tee hits run through `_TeeHitCold` (`NoInlining`); compact scans unroll one- and two-field lists. `BeginPacket` / `EndPacket` commit only series touched in the active packet (epoch tracking). `Tee` / `TeeCustomText` stay `NoInlining` so they cannot be pulled into `AppendChild`.
- Listener and value-cache slots skip redundant wake signals when the target flag is already set.
- Session first-parse uses a Monitor (`_ParseMutex`) instead of `SpinLock`. One frame parse is long enough that waiting source threads kernel-wait. Re-parse of announced ids stays lock-free. Dense packet ids and protocol-instance mutation remain serialized.
- Value-cache fill no longer rolls back on a protocol exception. Fields already teed (for example Ethernet before a UDP throw) stay in the cache; `packet.error` is still recorded. `RollbackCurrentPacket` / `RollbackActiveValueCache` are gone. `PacketIndex.RollbackCurrentPacket` is unchanged.
- `ValueCache` types live in namespace and folder `NetworkInspector.Core.ValueCaches`. Session listener/slot types live in `NetworkInspector.Sessions.ValueCaches`. The `Vc` type alias is gone; call sites use `ValueCache`.

---

## [0.8.0] — Settings arrays of scalar types, PDU Transport UDP port list

Delta since `36dee43` (0.7.0). Version is `0.8.0` in `Directory.Build.props`.

Settings can now store homogeneous JSON arrays of `bool`, `string`, `double`, `ulong`, and `long`. Protocol authors declare them with `[U64ArraySetting]` (and siblings); generated `RegisterFields` registers and loads them. PDU Transport uses that vehicle to bind several UDP ports.

### Added

- **`SettingType` array arms** — `BoolArray` (7), `StringArray` (8), `F64Array` (9), `U64Array` (10), `I64Array` (11). `SettingValue` factories, equality, hash, and format labels (`[N u64]`, same shape as `[N bytes]`). Getters return **defensive copies**. Empty arrays are valid. Duplicates are preserved.
- **`SettingsRegistrar.Register*ArraySetting`** and **`IReadOnlySettingsManager.Get*ArraySetting`** for all five types. Numeric arrays accept optional per-element `min`/`max` (same `Setting.MinValue` / `MaxValue` as scalars). F64 arrays reject non-finite elements the same way as scalar F64.
- **JSON persistence** — profile/group files store `"name": [1, 2]`. Load of a scalar, a non-array JSON value, a mixed-type array, or a `null` element is `TypeMismatch`, keeps the default, and does not apply a prefix. Out-of-range elements are `OutOfRange` and keep the default (whole array rejected by setting validation).
- **Protocol attributes** — `[BoolArraySetting]`, `[StringArraySetting]`, `[F64ArraySetting]`, `[U64ArraySetting]`, `[I64ArraySetting]` on matching array fields. `ProtocolGenerator` emits `Register*ArraySetting` and `{Field} = Get*ArraySetting(...) ?? {default}`.
- **`pdu_transport.udp_dispatch_ports`** (`U64Array`, default `[]`) — UDP ports in 1–65535 that select PDU Transport on `udp.port`. Protocol-side filter skips out-of-range elements with one `SettingsLoadWarningKind.OutOfRange`; in-range ports still bind. Empty means UDP never calls this parser. Listing a port is parser selection, not a socket listen. UDP still looks up `min(src,dst)` first, then `max` if the first lookup did not consume.

### Changed

- **Breaking (pre-1.0): `pdu_transport.udp_dispatch_port` (U64) removed.** Use `pdu_transport.udp_dispatch_ports` (JSON `[47290, 47291]`). A leftover scalar key or a JSON number (not an array) does not bind PDU Transport.
- `PROTOCOL_GUIDE.md` §10.9 documents the JSON array, host `PreloadValue(..., SettingValue.U64Array([...]))`, leftover-key miss, and UDP first-consumer collisions (for example source `53`).
- Version bumped to `0.8.0`.

---

## [0.7.0] — Concurrent re-parse, protocol-local effects, PDU Transport and Signal Message fixes

Delta since `d4d3511` (0.6.0). Version is `0.7.0` in `Directory.Build.props`.

After the first ordered parse of a packet, later parses of that same packet id are lock-free, field-identical, and safe on any number of threads — including while later ids are still being ingested. Stateful dissectors record a compact protocol-local effect during ingest and replay it on re-parse, so UI, filters, export, and session listeners no longer race on connection trackers, reassembly engines, or fragment buffers.

PDU Transport and Signal Message now share the Ethernet / IPv6 / UDP sibling-dispatch field tree, load extra JSON from a stream or object, and return every `SettingsLoadWarning` to the caller.

### Added

- **`EffectStore<TEffect>`** (`NetworkInspector.Core/Collections/EffectStore.cs`) — append-only sparse store keyed by `(PacketId, layerKey)`. First-parses write in ascending packet-id order; replay binary-searches the packed row, then the nested-layer chain. Duplicate layer keys on the same packet throw. Single ordered ingest writer; lock-free readers.
- **`Packet.GetEffectLayerKey(ReadOnlyMemory<byte> data)`** — packs buffer index (bits 31–24; `0` = `Frame.Data`, `1…` = `Packet.AddBuffer`) and byte offset of the `Parse` slice (bits 23–0). First match wins. `ReadOnlyMemory<byte>.Empty` throws `ArgumentException`; a slice that sits in no packet buffer throws `InvalidOperationException`.
- **`Packet.BindParseBuffer`** — attaches a reassembled payload as an additional packet buffer before nested `Parse`, so inner `GetEffectLayerKey` is stable across ingest and replay. Protocols must pass an owned copy when the payload would otherwise alias `Frame.Data` (TCP single-segment extract).
- **`JsonConfigStream`** — typed JSON load from a caller-owned `Stream` (does not close it; 1 MiB cap, same as file load). Failures map to `SettingsLoadWarning`.
- **`PduTransportRegistration`** — `Register(builder, warnings)`, plus stream and `PduTransportConfig` overloads that merge extra PDU names on top of `pdu_transport.config_file` (empty file is valid). Schema: `Schemas/pdu-transport-config.schema.json`. `PduTransportConfig` / `PduTransportPduEntry` are public.
- **`SignalMessageRegistration.TryLoadConfig(Stream)`** and `Register(builder, Stream)` / `Register(builder, SignalMessagesConfig)` — additional messages on top of `signal_message.config_file`. After `RegisterStandardProtocols`, a later stream register leaves settings untouched. Config models (`SignalMessagesConfig`, `SignalMessageConfig`, `DispatchBinding`, `SignalFieldConfig`, `MuxSignalConfig`, `MuxGroupConfig`) are public.
- **`SessionOptions`** — `Default` (store + index), `WithoutPacketStore`, `RedissectOnly`. `Session(Stack, SessionOptions? options = null)`. `ISession.StoreParsedPackets` / `IndexPackets`.
- **`ISessionReader.TryGetPacket(PacketId, Packet? recycle, out Packet?)`** — reuses the caller’s packet on re-parse. A store hit returns the stored instance and leaves `recycle` untouched. A rejected recycle falls back to a fresh allocation.
- Profiling scenarios: `ParseIngestUdpScenario`, `RedissectParallelUdpScenario`, `SessionConcurrentRedissectScenario`, plus `MemoryFrameSource`.

### Changed

- **Breaking (pre-1.0): first parse requires dense packet ids `0, 1, 2, …`.** `Packet.ParseFrame` / `ParseFrameIndexed` throw `InvalidOperationException` on a jump (for example id 5 after id 0). `Stack.ObserveParse` / `CompleteFirstParse` enforce this on every entry. Re-parse of an already first-parsed id is allowed from any thread. CLI `export` / `convert` packet-id allocator starts at 0.
- **There is no parse-mode parameter.** `ParseContext` does not carry ingest/redissect intent. Each stateful protocol decides via its own `volatile int _IngestWatermark` (`id.Value <= watermark` → replay). The watermark is raised in the outermost `finally` of ingest, including error and exception exits. Nested calls of the same protocol on one packet raise it only once.
- **Stateful protocols record and replay protocol-local effects** instead of mutating shared trackers on re-parse:
  - `UdpProtocol` — `EffectStore<StreamEffect>`; missing effect omits `udp.stream`.
  - `TcpProtocol` — `EffectStore<TcpLayerEffect>` (analysis flags, dispatch mode, reassembled PDU bytes); missing effect falls back to stateless raw-port dispatch. Reassembled PDUs are bound as owned `byte[]`.
  - `IPv4Protocol` / `IPv6Protocol` — `EffectStore<DefragLayerEffect>` on the completing fragment only; missing effect reports fragment fields without reassembly.
  - `SomeIpProtocol` — `EffectStore<SomeIpTpReassemblyResult>`; missing effect reports the segment without reassembly.
- **`IProtocol.Parse` remains the only parser contract.** `Stack.CallProtocol` stamps `ParseContext.SelfProtocolId` only. A raw `protocol.Parse(...)` or a cached `ParseDelegate` is a valid entry; effect keys do not depend on that stamp. Ethernet / Frame / VLAN / LLC / SLL / SLL2 keep `ProtocolId` plus `MutField.CallProtocol` caches.
- **Session ingest stays under `_ParseLock`** with monotonic ids. When `StoreParsedPackets` is `false`, listeners re-parse lock-free via `_TryReparseFrame` while later ids continue to ingest; the source thread recycles its ingest `Packet`. Shutdown keeps queries enabled until listener slots drain, so redissect still works on the last `NewPackets` window. `ISessionReader.PacketIndex` stays `null` when `IndexPackets` is `false`.
- **`PacketIndex.TryBeginPacket`** returns `false` for an already-indexed id (Contains first), so indexed re-parse during a live session is a no-op instead of throwing or double-counting presence bits.
- **Chunk stores stay split:** dense `ChunkedGrowOnlyStore<T>` (`Set` / `Get`) and packed `ChunkedAppendOnlyStore<T>` (`Append` / `Count` / `BinarySearch`) over shared `ChunkedSlotStore<T>`. Mixing `Set` and `Append` on one public instance is unrepresentable. `JsonConfigFile` confines referenced paths with `Path.GetFullPath` plus `_IsPathUnderBase` against the settings storage directory.
- `PROTOCOL_GUIDE.md` section 2 documents first-parse vs re-parse, watermark, packed layer key, `BindParseBuffer`, and the audit of every protocol that keeps cross-packet mutable state. Section 10.9 documents PDU Transport → Signal Message hops, settings vs external JSON, stream merge, sibling field tree, and silent-miss misconfiguration.
- Version bumped to `0.7.0`.

### Fixed

- **PDU Transport field tree put Signal Message under `pdu_transport`.** Dispatch ran on the protocol container and `pdu_transport.pdu` / `id` / `length` / `name` were built lazily in a second pass, so unmatched payload bytes sat as siblings of the PDU metadata and index recording diverged from the visible tree. `Parse` now appends header fields eagerly under `pdu_transport.pdu` and dispatches on `parentField` (`TryCallNextProtocolU64` on `pdu_transport.id`). Signal Message containers are siblings of `pdu_transport`, never children of `pdu_transport.pdu`. `pdu_transport.payload` is appended only when no sub-protocol consumed the payload.
- **PDU Transport / Signal Message registration warnings were incomplete or silent.** File-load failures, additional-stream failures, and field-size clamps are copied onto the caller list (`AppendRegistrationWarnings` / `Register` return values). An additional-stream warning is kept independent of the file warning. Empty Signal Message `dispatch_bindings.table` names now produce an explicit per-message warning instead of being skipped with no diagnostic.
- **TCP single-segment reassembly keyed ingest as the frame and replay as an additional buffer.** Zero-copy extract aliased `Frame.Data`; replay bound `ToArray()` as buffer 1, so `EffectStore.TryGet` missed and nested stateful fields (for example `udp.stream`) disappeared on redissect. Ingest now binds an owned copy, matching IPv4 / IPv6 / SOME/IP-TP.

### Removed

- Lazy PDU Transport populator (`_PopulatePduTransportFields`). Header fields, name lookup, and payload dispatch share the eager `IProtocol.Parse` walk.

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
