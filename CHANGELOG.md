<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.21.0] — Settings and limits as packages, invariant text, and a full review of Core

Delta since 0.20.0. Version is `0.21.0` in `Directory.Build.props`.

**Text is formatted with the invariant culture everywhere.** Numbers, GUIDs, and other values that the code writes into text
(export cells, messages, display lines) no longer depend on the machine's culture. On a machine whose culture writes decimals
with a comma, such text now writes a point, as the CLI README has always said. Inside the repository every interpolated string
that formats such a value is `Invariant($"…")` (`InvariantStringHelper`, referenced by every .NET 6+ project;
`NetworkInspector.Limits` uses `string.Create(CultureInfo.InvariantCulture, …)` and keeps no package dependency).

Two new packages, `NetworkInspector.Limits` and `NetworkInspector.Settings`, hold what `NetworkInspector.Core` used to hold for
trust-boundary limits and for typed, persisted settings. Both work in any .NET 10 application and know nothing of
NetworkInspector: the applications choose the file names of the settings folder (`SettingsStorageLayout`) and the sentence that
tells a user how to change a limit. Each package has a README and a guide whose code is the body of a passing test.
`NetworkInspector.Core` depends on both. The settings folders of `ni` are unchanged: the lock file and the ownership manifest
keep the names of every earlier release (`.ni-settings.lock`, `.ni-settings-owned`), and the messages of `ni` are unchanged.

**A small core and optional unit parts.** `NetworkInspector.Limits` holds the record that describes a limit (`LimitDefinition`:
key, UI title, seven texts, unit, a fixed default that is checked to lie inside the range, the range, `allowOff`, `readOnly`),
the value (`LimitValue`, a number or `off`), a lock-free live holder for hot loops (`Limit` is the read side: `Ceiling` is one volatile read and
allocates nothing, and it cannot change the value; `LimitWriter` is the only way to change it), a duplicate-key check (`LimitCatalog.Check`), and the unit seam `ILimitUnit`. The namespace
`NetworkInspector.Limits.Utility` holds the optional parts the core never uses: `UnitWordSet` (`Binary("B")`, `Decimal("bit/s")`,
`Of("ms").With("s", 1000)`), `TableUnit`, the ready units `LimitUnits.Count`, `Bytes`, `Duration`, and the hit sentence
(`LimitMessages.HitMessage`). Durations accept `d` and `w`; a month or a year is an unknown word because it has no fixed length.
A definition can refuse `off` (`allowOff: false`) and can be read-only (`readOnly: true`: only the default is valid).
NetworkInspector keeps `off` for every limit and has no read-only limit.

**No machine-dependent values.** A default is one fixed number. The defaults of NetworkInspector are sized for a reference
machine of 8 logical processors and 16 GB of memory; no default is read from the memory or the processors of the machine.

**Settings.** A limit setting owns a live holder (`Setting.LiveLimit`, `SettingsManager.GetLiveLimit`,
`ReadOnlySettingsManagerView.GetLiveLimit`) that follows the current value: apply, load, and reset change it, a pending value does
not. A read-only limit refuses every value but its default in the pending value, the text form, and a file
(`SettingProblemKind.ReadOnly`, appended at the end of the enumeration).

**The loader limits are limits.** The settings library bounds what it reads with five `LimitDefinition`s in `SettingsLimits`:
`settings.maxFileSize` (64 MiB), `settings.maxJsonDepth` (64), `settings.folderLockTimeout` (2 s, was a constant),
`settings.maxGroupFiles` (1,024 files, new), and `settings.maxTextLength` (1,048,576 characters, new). `SettingsLimitValues` carries their starting values into the `SettingsManager`
constructor and `SettingsManagerFactory.Create`; `LimitValue.Off` removes a limit and `0` is refused. A file above the size limit
is a `FileTooLarge` warning, a text above the length limit is a `TextTooLong` problem, and a refused folder lock names the limit;
all three use the standard hit sentence with the limit and its key.

**The loader limits are settings, on request.** `SettingsManager.RegisterOwnLimits()` registers the five limits as settings of the
group `settings` (file `settings.json`). `Load` then reads that file first, so a stored limit bounds every other file; the value
given to the constructor is the start value, and a sparse save stores only a value that differs from it. The call is opt-in: a
manager that does not make it lists no extra setting, as before. `SettingsManager.Limits` now returns the values in effect.
`ni` calls it on its application manager (see the command line below). `RegisterOwnLimits(defaults)` takes the default values of the settings, and returns the warnings for stored values that the registration refused.

**Limits on the command line.** The five loader limits are settings of the application scope in `ni`: `ni settings get`, `set`,
`reset`, and `list` work on `settings.maxFileSize`, `settings.maxJsonDepth`, `settings.folderLockTimeout`,
`settings.maxGroupFiles` (new), and `settings.maxTextLength`, with unit forms (`256 MiB`, `off`); a stored limit lives in
`application/settings.json` and bounds every other settings file of the run. `--set <key>=<value>` on every command overrides one
setting for one run and writes nothing (`settings set` and `settings reset` refuse it); the loader limits are in effect before the
first file is read. `ni settings describe <key>` and `ni settings describe --limits` (text, JSON, Markdown) print what a limit
bounds, why it exists, what a smaller and a larger value do, and what `off` means. **Breaking:** `--settings-max-config-file-bytes`
and `--settings-max-json-depth` are refused and name the `--set` to use. `--blf-cache-size` stays until the BLF limits are
settings.

**Hardening.** `Save` reads every file it would replace or delete before it writes the first, and throws when `Load` had skipped
one of them (too large, too deep, not valid JSON), so a typo in a hand-edited file no longer costs its group. A depth hit is the
warning `JsonTooDeep`, a folder over the file limit is `TooManyGroupFiles`, and a key in the file of another group is
`WrongGroupFile` (not applied, kept by a save); a file that vanishes during a load is skipped. `SettingValueText` refusals and load
warnings quote at most 32 characters of the input and replace control characters. `JsonConfigStream.TryLoad` is bounded by default
(64 MiB, depth 64) and takes the limits as a last optional argument; `JsonConfigFile` returns a path with a NUL character or an
unknown type discriminator as an error instead of throwing. A repeated key makes `SaveChanged` and `Unset` throw the documented
`PersistenceSettingsException`. The folder lock retries only a sharing violation and `SaveChangedAsync` waits without blocking a
thread. A late registration returns the refusal text in `SettingRegistrationResult.Warning`. `SettingValueText.TryParse` in `ni
settings set` and in the option texts now uses the text length limit of its manager. The manager uses one plain lock for its
registries; JSON conversion no longer runs under it, and a save streams to the file. The key rule of `LimitUnits` also refuses
`MiB`, `KiB`, `GiB`, `TiB`, `Secs`, `Mins`, and `Hrs`, and the Limits package has no package dependency.

**Breaking:** `Limit` has no public constructor and no `TrySet`: `LimitWriter` creates and changes it, and
`IReadOnlySettingsManager.GetLiveLimit` and `IReadOnlySetting.LiveLimit` return the read side.
`SettingProblemKind.InvalidPath` and `UnknownSetting` are removed, `JsonConfigFile` has no overload without limits, and a refused
document of the SOME/IP configuration stream is the warning of `JsonConfigStream` (kind `ExternalConfigUnavailable`).
`SettingsManager.MaxConfigFileBytes`, `MaxJsonDepth`, the two `Default…` constants, and the matching constructor and
factory parameters are replaced by `SettingsManager.Limits` (`SettingsLimitValues`); the defaults are now 64 MiB and depth 64 (were
1 GiB and 1024), and `0` or less no longer switches a check off (the CLI flags refuse it). The property that returns the definition of a limit setting is named `LimitDefinition` (was `Limit`) on
`Setting`, `IReadOnlySetting`, and `ReadOnlySettingView`. A `Setting` can be registered with one manager only; a second registration throws
`InvalidOperationException`. Temporary files of a save carry a 32-digit id and `Save` deletes only those, no longer every
`*.json.tmp`. The namespace `NetworkInspector.Core.Settings` moved to `NetworkInspector.Settings`, and the settings errors
(`SettingsException` and its subclasses) moved with it; `SettingScope` stays in `NetworkInspector.Core.Settings`. Plugins built
against 0.20.0 must be rebuilt. Creating a persisting `SettingsManager`, a profile, or a settings path takes a
`SettingsStorageLayout` (`CoreSettingsLayout.Instance` for NetworkInspector). An invalid UI name of a setting throws the new
`InvalidUiNameSettingsException`, and the group name `default` is refused where a limit becomes a setting. The limit types
(`LimitDefinition`, `LimitValue`, `Limit`, `ILimitUnit`, the `Utility` parts, `CoreLimitPolicy`) are new in this release.

### Core: a full review of `NetworkInspector.Core`

**Fixed.** A field insert whose value-cache record throws no longer leaves a linked tombstone that cuts later siblings out of the
tree (`Packet`: the cache records before the links are written). A packet at the field limit keeps its index entry and its limit
error (`PacketProtocol` guards its two trailing appends). `MutField.AsField` works on a tree-free packet. A lazy field that waits
for a table growth no longer invokes an empty populator slot. `OutOfMemoryException` is never contained at a parse boundary (all
eight catch blocks use one filter). `Stack.Dispose` waits for a `Shutdown` that another thread started before it releases the
settings manager. A replay that reaches a stateful protocol whose gate never saw the packet is a replay (the gate asks the stack).
Held TCP segments sit in a binary heap, so a crafted segment order no longer costs a list shift per segment. The SIMD set
operations of the array container bound both counts to the arrays they hold. `FrameInterfaceRegistry` registers in amortized
constant time (snapshot with spare capacity, doubling) instead of copying its array per registration. `RunContainer.Or`/`Xor` fill
bitmaps with word masks, rank and select of run and array containers no longer probe every value, and `RoaringBitmap.AndWith`/
`AndNotWith` take one pass per container pair.

**Registration is checked.** Every `StackBuilder` registration member throws `InvalidOperationException` after `Build`. Unknown
protocol ids (post-parsers, parser entries, heuristic table owners, stream reassembly configs) throw `NotFoundRegistrationException`,
null arguments throw `ArgumentNullException`, a field registered with an index group has the group when its subscribers run, and
`RegisterStreamReassemblyConfig` refuses a buffer smaller than the PDU and names the protocol of a duplicate. `ParsePlan.TryCreate`
refuses a packet index of another stack (`ParsePlanErrorCode.IndexStackMismatch`). `LazyString.Lazy` and `FormatLazy` refuse a null
delegate; a null result of a delegate reads as an empty text. The HTTP/2 stream buffer is as large as its largest frame (16 MiB plus
8 bytes) so a maximal frame can complete.

**API changes in `NetworkInspector.Core`.**
- Added: `Packet.ErrorCount` (dissector errors, caught exceptions, observer failures; counts also in `FieldTreeMode.Skip`),
  `ParseContext.IsReplay`, `ParsePlanErrorCode.IndexStackMismatch`, `TwoQueueCache.GhostDroppedCount`.
- Changed: `IngestGate.IsReplay` and `IngestGate.Enter` take the `ParseContext` (`IsReplay(in context, id)`, `Enter(in context, id)`);
  `IStack.TryMatchHeuristic` returns `bool` with `out ProtocolId` (it returned `ProtocolId?`); `Packet.TryParse`, the
  `ValueCache.TryGet*Series` members, `DispatchContext.TryGetString`, and `TwoQueueCache.TryGet` carry `[NotNullWhen(true)]` or
  `[MaybeNullWhen(false)]`; `RecycleError` is internal.
- Removed: `RoaringTreemap` (no use in the repository), `LazyString.TryWriteTo`, `Packet.PackEffectLayerKeyForTests`.
- A scope of an `IngestGate` that is disposed twice (through a copy) throws `InvalidOperationException`.
- Changed behavior found by the closing review: `PresenceQuery.SelectProtocol`/`SelectGroup`/`SelectField` start the query again and
  discard the result built so far (they intersected with it before, which made `Select*(a).Select*(b)` an AND). A `default` `Field`
  has no tree: `HasFieldTree` is false, so `TryGetParent`/`TryGetNext`/`TryGetPrev`/`TryGetFirstChild`/`TryGetLastChild`/`HasChildren`/
  `ChildCount` return false and 0 instead of throwing `NullReferenceException`. `AppendLazy` with a `null` populator records a
  `packet.error` (counted in `ErrorCount`) and nothing is thrown. A series that hits its index limit leaves its monotonic flags as they were.

### Core limits are settings

Nine limits of Core are `LimitDefinition`s in `CoreLimits`, registered by every `StackBuilder` as settings and shown by
`ni settings describe --limits`; each allows `off`: `reassembly.maxStreamBufferSize` (1 MiB), `reassembly.maxStreamPduSize` (64 KiB),
`reassembly.maxStreamRebases` (64, new), `reassembly.maxPendingDatagrams` (1,024), `reassembly.maxFragmentStorage` (64 MiB, was
unbounded), `reassembly.maxFragmentsPerDatagram` (8,192), `packet.maxLazyDepth` (128), `valuecache.maxRows` (100 million, new), and
`frames.maxInterfaces` (1,048,576, new). `Stack.Limits` (`CoreLimitSet`) holds the live holders. A stream configuration that assigns
no size follows the setting (`StreamReassemblyConfig.WithLimits`); the IP reassemblers apply the settings when the stack starts
(`FragmentReassembler.ApplyLimits`). `SequenceStreamBuffer.TryAppend(…, out StreamAppendRefusal)` names the limit that refused a
segment, and descending segments stop at the rebase limit. A value cache stops recording at its row limit and sets
`ValueCache.IsRecordingIncomplete`; `RecordingLimitMessage` names the limit. `LengthPrefixDetector.DefaultMaxPduSize` is removed (a
detector without a `maxPduSize` has no limit of its own; the stream's PDU limit bounds it). `FragmentReassemblyOptions.IPv4`/`IPv6`
use a finite byte budget of 64 MiB (it was `long.MaxValue`).

---

## [0.20.0] — Exports that parse only what they write, a writer thread for the big formats, and SOME/IP names you can type

Delta since 0.19.0. Version is `0.20.0` in `Directory.Build.props`.

**An export parses only what it writes.** Every field exporter (CSV, JSON, Text, PBF, Parquet, DuckDB) takes an
`ExportContent`: selected fields, all fields, or all fields with their hierarchy. A run states what its exporter, its filter,
and its caches need (`ParsePlan`) and parses each frame once with exactly that, so an export of two fields builds no field
tree. `ni export capture.pcapng -o ports.duckdb -f "duckdb:fields=udp.srcport;udp.dstport" --verbose` prints
`parse: fields only, no tree (2 fields, 1 observer)` and is many times faster than the full export.

**DuckDB, Parquet, PBF, and BLF write on a second thread.** A full batch is encoded, compressed, and written while the next
one fills, with memory bounded by two batches. `WithWriteMode(ExportWriteMode.Synchronous)` keeps every write on the calling
thread. The overlap needs a free core: BLF gains about 3 % on an idle machine, and on a machine with busy cores it is up to
7 % slower than 0.19.0 in the background mode, so choose `Synchronous` there (`-f "blf:writeMode=sync"`).

**SOME/IP services, methods, and values have names you can type.** `ClimateControl.v2` selects a service,
`ClimateControl.v2.SetTemperature` a method, `ClimateControl.v2.SetTemperature.req.request.zone == 7` a value; the filter input
completes them, none needs quotes, and the names are the ones of the service description, unchanged.

**A session can export a range of its packets** (`SessionExport.TryStart`) on a job thread while ingest keeps running.

### Highlights

| Area | What you get | Start here |
| --- | --- | --- |
| Export content | Selected fields, all fields, or the hierarchy; first, last, or all occurrences of a field | [Exporter guide](NetworkInspector.Exporters/EXPORTER_GUIDE.md) §14 |
| Parse plan | One parse per frame with the least work all consumers allow; `--verbose` says what was chosen | [Core README](NetworkInspector.Core/README.md) |
| Writer thread | DuckDB, Parquet, PBF, and BLF overlap parsing and writing | [Exporter guide](NetworkInspector.Exporters/EXPORTER_GUIDE.md) §9.5 |
| CLI | `fields`, `occurrence`, `shape`, `info`, `frames`, `buffers`, `text`, `repr`, `writeMode`, `batchPackets`, `batchBytes`, `containerSize` for every format; DuckDB `storageVersion`; Parquet `compression` and `compressionLevel` | [CLI README](NetworkInspector.CLI/README.md) |
| Smaller exports | Frame bytes and buffers are opt-in, addresses are integers in the binary formats, Parquet is Zstandard, DuckDB storage is `v1.5.0` | [Exporter guide](NetworkInspector.Exporters/EXPORTER_GUIDE.md) §12.3 |
| Sessions | `ISessionReader.TryVisitPackets`, `SessionExport.TryStart` | [Session guide](NetworkInspector.Sessions/SESSION_GUIDE.md) §8.6 |
| SOME/IP | Filter names for services, methods, and values; `someip.field_prefix` | [Protocols README](NetworkInspector.Protocols/README.md) |
| Field observers | `EndPacket`, custom text in a tree-free parse | [Core README](NetworkInspector.Core/README.md) |
| Ids | `PacketId` and `FrameId` convert implicitly from `int` | [Core README](NetworkInspector.Core/README.md) |

### Upgrade notes

Every breaking change is described in detail in the sections below. In short, besides the table of the exporter rework:

| You used | Now |
| --- | --- |
| `someip.payload.<service>.v<major>.<member>[.in\|.out].<path>` in filters and exports | `[<prefix>.]<service>.v<major>.<member>[.req\|.resp].<path>`; the names of the description are used unchanged (no lower-casing, no replaced characters, no numeric suffixes) |
| `filter_name` in a SOME/IP description (usage sites, bitfield members) | Remove it; the filter name is the `name`, and `ui_name` sets the display text on every level |
| `RegisterProtocol`, `RegisterField`, `RegisterFieldInGroup`, `RegisterFieldAliasGroup` with a name another kind holds | `DuplicateNameRegistrationException` (its `HolderKind` names the holder); the first registration keeps the name |
| `SomeIpServiceInfo` / `SomeIpMemberInfo` `Token` | `Path`, `Segment`, `GroupName`, `FieldId`, `Group`, `UiName` |
| A Text export that read `Label: custom text` lines or lower-case hex | A custom text is the whole line (no UI name in front of it, so a dissector that wants the label writes it into the text; a custom text that holds no label, such as the value part of many built-in protocol fields, shows without a label in Text lines and in `Field.ToString()`), a flat shape line is `name=<display line>`, and bytes are upper-case hex (`0A FF 00`) |
| A reader of the PBF Columnar custom-text and custom-representation columns (fields 15 and 18) that expected one entry per row | The columns list the rows as entries: sub-field 1 is a row with a text (also an empty one), sub-field 2 is a run of rows without a text; a column without any text is not written. The PBF format version stays 1 |
| A reader of the address values in DuckDB, Parquet, PBF Standard, or PBF Columnar that expected `BLOB` / byte-array / length-delimited bytes | Addresses are unsigned integers: IPv4 `UINTEGER` / `UINT_32` / fixed32 (PBF field 19 packed in Columnar), MAC and EUI-64 `UBIGINT` / `UINT_64` / varint or fixed64 (field 20 packed), IPv6 `UHUGEINT`, UUID `UUID`; in Parquet and PBF the 128-bit types are the two halves `value_hi` and `value_lo` (PBF fields 5 and 11, columns 20 and 21). The big-endian reading of the address bytes, so `ORDER BY value` is the address order; `WHERE value BETWEEN 3232235776 AND 3232236031` selects `192.168.1.0/24`. Conversion back to text: [exporter guide](NetworkInspector.Exporters/EXPORTER_GUIDE.md) § 12.3, Address values. CSV, JSON, and Text keep the notation |
| An export that relied on the frame bytes (`ExportContent.FrameBytes` was true by default: DuckDB, Parquet, PBF Columnar) | `FrameBytes` is false by default in every exporter; set `FrameBytes = true` (`WithContent(DuckDbExporter.DefaultContent with { FrameBytes = true })`) or pass `frames=true` on the CLI (`-f duckdb:frames=true`). The field tree already holds the parsed bytes of every layer, so a default export is smaller |
| `WithContent` of CSV, JSON, Text, or the standard PBF layout refused `FrameBytes = true` | JSON, Text, and both PBF layouts accept it; CSV takes the frame as a column (`WithColumn(CsvColumnKind.FrameBytes)`) and still refuses the content member, naming the column kind |
| A Parquet dataset read by a tool without Zstandard support (the library default was Snappy) | Parquet files are written with Zstandard at the optimal level by default; export with `-f parquet:compression=snappy` (or `WithCompression(ParquetCompression.Snappy)`) for the former codec |
| A CSV export with an ASCII letter, a digit, or one of `-+.:` as the delimiter (`WithDelimiter`, `csv:delimiter=`) | The delimiter is refused with a message that names the set: such a character occurs in the unquoted number, timestamp, and hex cells and would split them, so the file was corrupt; use `,`, `;`, a tab, `\|`, or another character |
| `WithCancellationToken(token: …)` of the CSV, JSON, and Text builders | The parameter is `cancellationToken`, like every other builder; only a call with the named argument `token:` changes |
| A DuckDB export opened by a DuckDB older than 1.5 | The file is written with storage version `v1.5.0` by default and an older DuckDB refuses it; export with `storageVersion=v1.0.0` (or the version of that DuckDB, for example `-f duckdb:storageVersion=v1.2.0`), or `WithStorageVersion` in code |
| A protocol that binds a buffer with `MutField.BindParseBuffer` (returned an empty slice on failure) | `MutField.TryBindBuffer(buffer, out bound, out BufferBindFailure failure)`: the only bind, it returns `false` with `TableFull` (255 buffers) or `Sealed` and stores nothing then. It has no limit that a caller could omit; the packet knows no protocol. A protocol that derives bytes from untrusted input reads `MutField.BoundBytes` (the sum of all bound bytes) and applies its own total before it binds (`DerivedBytesLimit.TryBind` does it for the built-in protocols) |
| `MutField.TryBindDerivedBuffer` and `DerivedBindFailure` (never released) | Removed with `BindParseBuffer`; use `TryBindBuffer` and, for a per-packet total, `BoundBytes` |
| `Packet.TryGetNextFieldValue(id, ref cookie, out value, materialize)` | Removed: `Packet.TryGetNextField(id, ref cookie, out Field field, materialize)` returns the field; read the value with `ref readonly FieldValue value = ref field.Value;` (no 32-byte copy). `Packet.TryGetFieldValue` stays for the first occurrence |
| A dispatch with `TryCallNextProtocolU64`, `TryCallNextProtocolString`, `TryCallNextProtocolBytes`, `TryCallNextProtocolBool`, or `TryCallNextProtocolAny` on a table of another key type | An error result that names the table and both key types; it read as `NotDispatched` (no protocol for the key) before |
| A packet with many decoded HTTP bodies, unmasked or inflated WebSocket payloads, or reassembled SOME/IP-TP messages beyond 256 MiB (SOME/IP-TP: 64 MiB) | The body, payload, or message that passes the total is not parsed and `packet.error` names `http.max_derived_bytes_per_packet`, `ws.max_derived_bytes_per_packet`, or `someip.tp_max_derived_bytes_per_packet`; raise the setting if the capture needs it. The total counts every byte bound to the packet when the protocol binds, whichever protocol bound it (the TCP PDUs of the same packet included), not only the bytes of that protocol; because of that the minimum of the three totals is 2 KiB (a smaller total would refuse a body whenever the PDU or the reassembled datagram alone passes it) |
| A plugin built against 0.19.0 | Rebuild it against 0.20.0: Core changed public signatures (`ref readonly` field properties, `IFieldObserver.EndPacket`), so an old plugin fails when its code runs |

### Exporter rework: exports parse only what they write, batch writing on a second thread

**Field observers get what a parse without field tree needs.** `IFieldObserver.EndPacket(id, complete)` is called once for
every `BeginPacket` that ran, before the packet is sealed (a required member: every observer implements it, and an observer
without per-packet state leaves the body empty); `FieldVisit.CustomText` is a `ref readonly LazyString` to the text set at append (the field's own slot in a parse with tree, so a deferred
text evaluated through it is cached for the tree and later readers); `ParseObserver.DisplayText` makes dissectors build custom
text and gated custom representations for the fields of a tree-free parse; `FieldWatch.ForExtraction(stack, ids)` accepts the
ids plus every field of their owning protocols, so a field inside a lazy container outside its index group is no longer missed.
`FilterObserver.ExtractionWatch` and `FilterObserver.ParseDemand` replace the internal owner-expanding watch.

**One parse for all consumers.** `ParseDemand`, `IParseDemandSource`, and `ParsePlan` let an exporter, a filter, and a value cache
state what they need (nothing, a parse without field tree, a field tree) and take the least parse work that serves all of them.
`ParsePlan.ToString()` describes the choice in one line.

**Custom text is final at append.** `MutField.SetCustomText`, `AppendCustomText`, and `ClearCustomText` are discouraged and now
throw `InvalidOperationException` after the packet is sealed (a sealed packet is read by other threads and the text store is
not synchronized). The SOME/IP array and union texts are set at append, so a tree-free export shows the same text as the field
tree; an empty union inside an array keeps its element prefix (`[2] union X (empty)`).

**DuckDB, Parquet, PBF, and BLF write on a writer thread.** A full batch is encoded, compressed, and written by a thread named
`"{format} export writer"` while the caller fills the next batch (`ExportWriteMode.Background`, the default); the new
`WithWriteMode(ExportWriteMode.Synchronous)` keeps every write on the calling thread. Memory stays bounded by two batches. A
batch that fails to write is reported one batch later in background mode (at the latest by `OnFinish`) and by the completing
call in synchronous mode; its packets leave `WrittenCount` and count as skipped. Batch sizes are validated with a message that
names the range (`1`–`int.MaxValue` packets, `1`–`long.MaxValue` bytes); `BlfExporter.Builder.WithMaxContainerSize` sets the BLF
container size (64 KiB – 64 MiB, default 10 MiB). Parquet writes value columns from pooled arrays (no array per column and flush),
PBF writes `Bytes` values, frame bytes, and address values straight from their memory (no array per value), and `ByteSlab` is now
`BatchByteArena` with the allocator lifetime models documented at the three allocator types.

**Exports write what you ask for, and parse only what that needs.** Every field exporter (CSV, JSON, Text, PBF Standard, PBF
Columnar, Parquet, DuckDB) takes an `ExportContent` (`WithContent`) with a shape: selected fields, all fields, or all fields with
hierarchy. A field that occurs several times in a packet is kept with its `occurrence` (`All`, `First`, or `Last` per field); the
values, types, and custom texts are the same in every shape and from every feed. The exporter states its parse need
(`IParseDemandSource`), and a run parses each frame once with exactly what its consumers need: an export of two fields builds no
field tree (`duckdb-selected` about 291 000 packets/s against about 11 000 for the hierarchy; `csv-selected` about 1.29 million
against about 174 000 for the former column export), `ni convert` without a filter parses nothing, and a filter on plain fields
adds no tree either. A packet the filter rejects is simply never committed, so it leaves no row.

**`ni` chooses the parse work and says so.** `ni export` takes the same options for every format (`fields`, `occurrence`,
`shape`, `info`, `frames`, `buffers`, `text`, `repr`, `separator` for CSV, `writeMode`, `batchPackets`, `batchBytes`, and `containerSize` for
BLF); `--verbose` prints one line such as `parse: fields only, no tree (2 fields, 1 observer)` and, when a filter needs the field
tree (a subtree scope, a protocol-presence term), why the run builds it. An output that received no packet is discarded instead
of left behind empty, and a split is decided after the packet that reached the threshold, so the packets of a split are never
shared between two outputs.

**Sessions run exports as pull-driven jobs.** `SessionExport.TryStart` exports a packet range of a session (any shape, optional
filter) on a job thread through the new `ISessionReader.TryVisitPackets`, which parses a range with your own `ParseOptions`
under one stack lease. Ingest keeps running; `SessionExportJob` reports visited and exported counts and why a job ended early.

**Breaking changes (migration):**

| Change | Migration |
|--------|-----------|
| A caller-owned stream passed to the PBF or BLF exporter (`ToStream`, `ToStdout`) is written by the writer thread | Do not use the stream until `OnFinish` returned, or choose `WithWriteMode(ExportWriteMode.Synchronous)` |
| DuckDB, Parquet, PBF, BLF report a failed batch one batch later (background mode) | Read the counters after `OnFinish`, or choose the synchronous mode for the old timing |
| `MutField.SetCustomText` / `AppendCustomText` / `ClearCustomText` throw after seal | Set the text when appending the field (`AppendWithCustomText`) |
| `WithMaxPacketsPerBlock(0)` / `WithMaxBlockSize(0)` now throw `ArgumentOutOfRangeException` naming the range (was `ThrowIfNegativeOrZero`) | Pass a value of at least 1 |
| `FilterObserver.CreateOwnerExpandingWatch()` (internal) is gone | Use `FilterObserver.ExtractionWatch` |
| `ColumnarDetailFlags`, `WithDetailFlags(…)` on DuckDB, Parquet, and PBF Columnar | `WithContent(new ExportContent { … })`; `IncludeTopology` is `Shape = ExportShape.Hierarchy`, the other flags are `ExportContent` members |
| CSV `WithFieldColumn(name, FieldId, header)`, `CsvColumnKind.Field`, the field members of `CsvColumnDefinition` | `WithContent` with `Shape = SelectedFields` and a `FieldSelection` (the header is the field name); start from `CsvExporter.ContentTemplate`, which is a starting point and not the default: without `WithContent` CSV still writes packet rows, so `DefaultContent` exists on every field exporter except CSV |
| CLI `csv:columns=a;b` | `csv:fields=a;b` (the same names; `name:first` / `name:last` choose the occurrence); `columns` is refused as an unknown option, and the message lists the options, `fields` among them. Custom texts and representations are written as extra columns by default; add `text=false,repr=false` for one column per field as before |
| CSV wrote only the first occurrence of a field | one cell per field with all occurrences joined by `\|` (option `separator`); `csv:fields=ip.src:first` keeps the old result |
| DuckDB `field_{id}` tables, Parquet `field_{id}` files, PBF Columnar field blocks | the second column of a flat shape is `occurrence`, not a node id; the hierarchy shape keeps `node_id` and the topology; new `export_info` table / `export_info.parquet` / PBF header shape; `layout_version` is 1 (the first released layout) |
| PBF files | The format version stays 1 although the layout changed incompatibly (shape code in the header, `occurrence` position column, presence-encoded texts, integer addresses): a reader written for the files of 0.19.0 must read the shape code of the header first (absent means hierarchy) and the new encodings of the columns and fields named in the upgrade notes |
| A packet exporter received only packets with a field tree | an exporter fed by its own observer receives packets without a tree; a packet with neither a tree nor staged fields is skipped with a message that says why |
| `IFieldObserver` implementations | add `EndPacket(id, complete)`, a required member; an observer without per-packet state leaves the body empty, and one that keeps state per packet closes or discards it there |
| `Field.Value`, `Field.CustomText`, `MutField.Value`, `MutField.CustomText`, `FieldValue.CustomRepresentation` return `ref readonly` | Source: reading them is unchanged (`FieldValue v = field.Value;` copies); keep `ref readonly FieldValue v = ref field.Value;` to read repeatedly, and an evaluated deferred text then replaces the deferred object in the field. Expression-tree lambdas over these properties no longer compile (CS8153). Binary: rebuild every plugin against 0.20.0 |
| `LazyString.Lazy(factory)` | builds the string once per call however many copies are read, and allocates a small wrapper besides the delegate; `LazyString.FromRawValue` throws `ArgumentException` for a value `RawValue` cannot return |
| `FieldValue.DataText` and the nested type `FieldValue.DefaultText` | `FieldValue.Data` (a `FieldValueData`): `value.Data.ToString()`, `$"{value.Data}"`, and `value.Data.TryFormat(…)` give the same default text of the data, ignoring the custom representation |

Performance: the profiling tool gained export scenarios that include the parse (`export-*-hierarchy`, `export-*-frames`, the
`-sync` controls, `writer-handoff`) and a recorded baseline in `artifacts/perf/exporter-rework/`.

### SOME/IP: filter names for services, methods, and values

Before, a decoded value was reachable only as `someip.payload.<service>.v<major>.<member>[.in|.out].<path>` with names that
were lower-cased, stripped of characters, and sometimes numbered (`_2`) — names nobody had written — and a service or a method
could not be filtered without a quoted string compare on `someip.service.name`. Now every service, every method, and every value
in a payload is a field whose filter name is built from the names of the service description, unchanged and with their exact case:
`[<prefix>.]<service>.v<major>.<member>[.req|.resp].<path>`.

```text
ClimateControl.v2                                         every message of the service, version 2
ClimateControl.v2.SetTemperature                          every message of the method
ClimateControl.v2.SetTemperature && someip.msgtype == 0x80      the responses of the method
ClimateControl.v2.SetTemperature.req.request.zone == 7    a value of the request
```

- **Names.** A name starts with an ASCII letter or an underscore and continues with letters, digits, and underscores. The
  direction `req` or `resp` appears for methods and for the getter and setter of a field; events and notifiers have none. The
  filter input completes all of them. `ui_name` (service, member, parameter, member of a type, bit) is the display text and defaults
  to the name; `filter_name` is gone.
- **A broken name is one warning and the entry is skipped, nothing is renamed.** An invalid or repeated service is skipped; an
  invalid or repeated member is not registered (its messages show the method as unknown); an invalid or repeated parameter,
  structure member, TLV member, or union alternative stops the decode of that payload at its position with a diagnostic that names
  it, and the bytes from there on are `someip.payload.unparsed`; an invalid or repeated bit is left out.
- **`someip.field_prefix`** (default empty) puts a text such as `acme` or `acme.car` in front of every filter name, for users who
  need the names to stay clear of protocols and plugins. An invalid value is reported and not used.
- **The service node and the method node exist on every message of the member** (also an empty request, an error, a segment, or a
  payload another parser took); `someip.payload` is always the raw bytes of a payload that no other parser took, so
  `len(someip.payload) > 100` selects by size with or without a description. A filter on a service or a method is answered from
  the packet index without decoding any payload.
- **A protocol, a field, and a field alias group can no longer share a name.** The three kinds are one namespace in the stack
  builder and the first registration keeps a name: a later `RegisterProtocol`, `RegisterField`, `RegisterFieldInGroup`, or
  `RegisterFieldAliasGroup` with a name that another kind holds throws `DuplicateNameRegistrationException`, whose new
  `HolderKind` (`RegistrationHolderKind`) is `Protocol`, `Field`, or `FieldAliasGroup`. Before, the filter resolved such a name to one holder by a fixed
  order and the other one silently stopped working. A protocol's own container field may still carry the protocol's name
  (protocol `someip`, field `someip`). A SOME/IP name that another component holds skips the service, or else the member, with one
  warning that names the name and its holder.

### DuckDB export

- **Every topology node has a name.** Protocols and groups get a catalog row without a table (`table_name` is `NULL`,
  `field_type` is 0), so the tree of a packet can be rebuilt with the query of the README; `topology.custom_text` holds the display
  line of a structure node (for example the summary of a protocol layer). The Parquet catalog has the same nullable `table_name`.
- **A failing last batch no longer loses the catalog.** `OnFinish` always completes the catalog and counts a lost batch once:
  `WrittenCount` equals the rows in `packets`, `SkippedCount` the lost packets.
- **A rejected packet leaves no trace.** A packet the batch rejects (for example a value it cannot store) is rolled back with its
  catalog rows and its frame bytes, so a batch never holds half a packet.
- **No byte array per frame and per bytes value.** Frame bytes and `Bytes` values are copied once into pooled chunks
  (`BatchByteArena`), so a warm export allocates nothing for them.
- Flush thresholds (`WithMaxPacketsPerBlock`, `WithMaxBlockSize`) are labeled secure by default and refuse a value outside their
  range with a message that names it.

### Packet and frame ids convert implicitly from `int`

- **`PacketId id = 7;` and `source.FrameById(7)` compile.** `PacketId` and `FrameId` gain `implicit operator` from `int`
  (the other direction existed already), so the plain index a caller has no longer needs `new PacketId(index)` /
  `new FrameId(index)`: `Packet.TryParse(nextId, stack, frame, in options, out packet, out failure)`.
  The conversion runs the same range check as the constructor, so a value below `-1` or above `ArrayIndexIdRange.MaxValue`
  throws `ArgumentOutOfRangeException` at the conversion, and `-1` is `Invalid`. `new PacketId(int)` still works.
- **Comparing an id with an `int` is now an id comparison.** `id == 5` and `id < count` convert the `int` side to the id type
  (the user-defined operators of the id win over the predefined `int` operators), so an out-of-range `int` throws instead of
  comparing as a number. An id still converts to `int` for indexing and arithmetic.

### A field prints its display line

- **`field.ToString()`, `$"{field}"`, and `TryFormat` give the display line.** `Field` and `MutField` implement `ISpanFormattable` and
  `IUtf8SpanFormattable`, like `FieldValue`. The line is the custom text alone when a dissector set one
  (also an empty one); the UI name for a field without a value; otherwise the UI name, `: `, and the value's default string.
  `FieldValue.ToString()` stays the value part, so the parts (`FieldInfo.UiName`, `Value`, `Value.Data`,
  `Value.CustomRepresentation`, `CustomText`) still build an own line. Before, `ToString()` of a field printed the type name. The
  members never throw: a default field gives the empty line, and every cursor of a skip-tree packet except its root shows its
  label alone. A field id that the stack does not know (no `FieldInfo` at all) has no label: its line is the value part alone (no
  label, no `: `).

### Custom texts in the exports

- **A custom text and a custom representation are written only when a dissector set one, and an empty one is not an absent
  one.** DuckDB and Parquet write NULL for an absent text and `''` for an empty one, PBF Standard writes field 6 / 7 only for a set
  text (a zero-length field for an empty one), PBF Columnar presence-encodes its text columns (absent rows are runs, an empty text
  is an entry), JSON writes the key only for a set text, and CSV writes an empty unquoted cell for an absent text and `""` for an
  empty one (one occurrence per cell; a wide cell that joins several occurrences cannot show the difference). Breaking: a reader of
  the PBF Columnar columns must walk the entries (see the upgrade note). The rule that rebuilds a display line from these parts is
  in the [exporter guide](NetworkInspector.Exporters/EXPORTER_GUIDE.md) § 12.3 and in the README of every field exporter.
- **The Text export writes the display line of every field.** A custom text is the whole line (before, the UI name was written in
  front of it, so a text that already held the label showed it twice), a field without a value shows its UI name, and every other
  field shows UI name, `: `, and the custom representation or the default text; bytes are upper-case hex everywhere (before:
  lower case). A flat shape writes `name=<display line>`. Breaking for anything that parsed the old lines.
- **Every format keeps the text of a protocol layer.** The custom text of a node without a value (`Ethernet II, Src: …`) was kept by
  DuckDB, PBF Standard, CSV, JSON, and Text but not by Parquet and PBF Columnar, so a reader of those two showed the UI name.
  Now `topology.parquet` has the nullable string column `custom_text` and the PBF Columnar topology message has the
  presence-encoded text column in field 5 (both only with `CustomText`; null or an absent row for a value node and for a node whose
  dissector set no text). A reader rebuilds the same layer lines as the Text export. The tree-free shapes have no layers.
- **The Text export escapes C1 controls and the backslash.** A value with U+0080–U+009F (U+009B is a control sequence introducer on
  terminals that honor C1) is written as `\u009b`, and a backslash as `\\`, so a literal `\n` in a value no longer looks like an
  escaped line feed. Breaking for anything that read a backslash verbatim from a Text export.
- **A JSON tree export honors `Info = false`.** The tree styles (compact, pretty, array) wrote `info` (`IN`) whatever the content
  said; now the key is left out when `Info` is off. The Text exporter has no info line and ignores the member.

### Addresses are integers in the binary formats

- **DuckDB, Parquet, PBF Standard, and PBF Columnar store IPv4, MAC, EUI-64, IPv6, and UUID values as unsigned integers**, the
  big-endian reading of the address bytes, so the numeric order is the address order and a subnet is a range query
  (`WHERE value BETWEEN 3232235776 AND 3232236031`). DuckDB: `UINTEGER`, `UBIGINT`, `UBIGINT`, `UHUGEINT`, and the native `UUID`;
  Parquet: `UINT_32`, `UINT_64`, `UINT_64`, and the two `UINT_64` halves `value_hi` and `value_lo` for IPv6 and UUID; PBF Standard:
  field 5 fixed32, varint, fixed64, and fixed64 plus field 11; PBF Columnar: packed fixed-width columns 19, 20, and 21. The
  PBF encodings are never larger than the length-delimited bytes they replace (an IPv4 value in a Standard record is 5 bytes instead
  of 6; a Columnar row is 4 instead of 6).
  CSV, JSON, and Text keep the readable notation. The exporter guide ([§ 12.3](NetworkInspector.Exporters/EXPORTER_GUIDE.md), Address
  values) lists how a reader turns each integer back into the display text. Breaking for readers of the old byte forms (see the
  upgrade note).
- **`layout_version` is 1.** 0.20.0 is the first release that writes it, so the layout of the new exports is its first version.
  The PBF format version stays 1 although the PBF layout changed incompatibly.
- **DuckDB writes IPv6 values without a heap object per value.** The appender of DuckDB.NET takes a 128-bit integer only as a
  `BigInteger`; the exporter appends the two 64-bit halves to a temporary staging table and moves each batch into the field table
  with one `INSERT … SELECT` inside the batch transaction, so a failed batch still rolls back as a whole.

### Frame bytes are opt-in in every packet exporter

- **A default export no longer copies the frame of every packet.** The field tree is the leading content of an export and already
  holds the parsed bytes of every layer, so `ExportContent.FrameBytes` is `false` by default in DuckDB, Parquet, and PBF Columnar
  (it was `true`). The default files are smaller by the size of the captured frames. Breaking for a reader that expected the
  `frame_bytes` column or the PBF column 13 (see the upgrade note).
- **Every packet exporter can write the frame on request** (`FrameBytes = true`, CLI `frames=true`): DuckDB `packets.frame_bytes`
  and Parquet `frame_bytes` (an empty frame is an empty value, not NULL), PBF Columnar column 13, **PBF Standard** packet field 6,
  **JSON** the key `frame_bytes` in every style and shape (standard Base64 with padding), **CSV** the column
  `CsvColumnKind.FrameBytes` (upper-case hex without separators; packet rows and the selected-fields layout; the all-fields and
  hierarchy layouts refuse it, also as `csv:shape=all,frames=true`), and **Text** a hex dump `Frame bytes (n):` after the lines of
  each packet (16 bytes per line, upper-case hex, no ASCII column). The option `frames` is registered for all of them and
  defaults to `false`.
- **No measured export configuration grew.** Measured on a synthetic sample of 20 000 identical 512-byte IPv6/UDP frames, the default
  Parquet dataset shrank from 7,384,267 to 1,961,128 bytes (no frame copy, Zstandard instead of Snappy), the default DuckDB file
  from 31,731,712 to 29,634,560 bytes, the default PBF Columnar file from 849,202 to 793,496 bytes, and the default Text output by
  2,000,000 bytes; the CSV and JSON outputs kept their size to the byte. Because every sample frame carries the same bytes, the
  numbers show that nothing got larger, not how much a real capture shrinks.

### Additional packet buffers in every packet exporter

- **A packet exporter can write the bytes that protocols attach to a packet** (`ExportContent.Buffers = true`, CLI `buffers=true`,
  off by default): the datagram an IPv4 or IPv6 fragment completes, the PDU a TCP stream completes, a SOME/IP-TP message, a decoded
  (dechunked or inflated) HTTP body, an unmasked or inflated WebSocket payload. Each buffer is written with its index (1 to 255;
  index 0 is the frame): DuckDB the table `packet_buffers(packet_id, buffer_index UTINYINT, bytes BLOB)`, Parquet the file
  `packet_buffers.parquet`, **PBF Columnar** the block field 16, **PBF Standard** the repeated packet field 7, **JSON** the key
  `buffers` (`[{"index":1,"bytes":"<Base64>"}]`, in every style and shape), **CSV** the column `CsvColumnKind.Buffers`
  (`<index>:<HEX>` records joined by `;`; packet rows and the selected-fields layout only, refused next to the all-fields and
  hierarchy layouts, also as `csv:shape=all,buffers=true`), and **Text** a block `Buffer i (n):` with a hex dump per buffer. A packet
  without additional buffers has no record, and a bound empty buffer is an empty value. The frame exporters (PCAPNG, BLF, ASC) have
  no such option.
- **The field tree stays the leading content**: the field tables, the topology, the catalog, and the protocols of an export are the
  same with `frames` and `buffers` on and off. A buffer can hold decoded content that the frame does not show in clear and can be
  much larger than the frames; the estimate of a batch counts the buffers, so `batchBytes` bounds the memory they take.

### DuckDB storage version

- **The DuckDB file format is chosen, never the library default.** `DuckDbExporter.Builder.WithStorageVersion(DuckDbStorageVersion)` and
  the CLI option `duckdb:storageVersion=v1.0.0|v1.1.0|v1.2.0|v1.3.0|v1.4.0|v1.5.0` (enum members `V1Dot0` to `V1Dot5`) set DuckDB's
  `storage_compatibility_version` when the file is created. The default is `v1.5.0` (`DuckDbExporter.DefaultStorageVersion`), so a file needs DuckDB 1.5 or later to open;
  an older reader needs `storageVersion=v1.0.0` (opens in DuckDB 1.0 and later) or the version of its release. An undefined
  enum value throws `ArgumentOutOfRangeException` that lists the six texts, and a CLI value outside them is refused with the allowed
  values before any file is created. There is no compaction or copy option: the export only appends, and it ends with the checkpoint.

### Parquet compression

- **Every Parquet file is compressed with a codec the exporter chooses, never the library default.** The default is Zstandard at the
  optimal level (`ParquetExporter.DefaultCompression`, `DefaultCompressionLevel`); Parquet.Net would write Snappy.
  `Builder.WithCompression(codec, level)` and the CLI options `parquet:compression=none|snappy|gzip|brotli|lz4|zstd` and
  `compressionLevel=fastest|optimal|smallest` set the codec and the level on every writer of the dataset (`packets`,
  `packet_buffers`, `topology`, `catalog`, `protocols`, `export_info`, and every field file). `lz4` is written as the `LZ4_RAW` codec of the
  Parquet specification (a reader reports `LZ4_RAW`); `brotli` is not read by every tool. An undefined enum value throws
  `ArgumentOutOfRangeException` that lists the allowed values, and a CLI value outside the list is refused with the allowed values before
  any file is created. A reader that predates Zstandard needs `compression=snappy` or `gzip`.

### Fixed

- **JSON and Text honored `text` and `repr` only in the flat shapes.** The JSON tree styles (`Pretty`, `Array`, `Compact`)
  wrote `custom_text` and `custom_representation` whatever the content said, and the Text tree wrote the custom text always; they
  now leave a switched-off text out and do not read it. A CSV export wrote an empty text as an empty cell, the same as an absent
  one; it now writes `""`.
- **The decoded HTTP body and the unmasked or inflated WebSocket payload were not among the packet's buffers.** HTTP handed the
  dechunked and/or inflated body, and WebSocket the unmasked and/or inflated payload, to the nested protocol as a fresh array
  that is not a slice of any packet buffer. A stateful nested protocol then refused it ("Parse data is not a slice of this
  packet's buffers"), and `Packet.Buffer(i)` did not show the bytes. Both protocols now bind the derived bytes once before the
  nested dispatch, so `Packet.BufferCount` is one higher for such a packet (an undecoded body and an unmasked, uncompressed
  frame add nothing). An empty decoded body is not dispatched. A packet that already holds 255 additional buffers reports
  `packet.error` with a message that names the limit (`Decoded HTTP body cannot be parsed further: …` and `Unmasked or
  decompressed WebSocket payload cannot be parsed further: …`) instead of parsing the payload; every masked frame takes one
  buffer, so a packet with more than 255 masked frames reports it at the first frame beyond that and does not dispatch the later
  ones. The bound bytes stay alive as long as the packet and one packet can complete many messages, so HTTP, WebSocket, and
  SOME/IP-TP each bound the sum of the bytes bound to one packet (all protocols together) when they bind, with a setting of
  their own, next to their per-item limit:
  `http.max_derived_bytes_per_packet` and `ws.max_derived_bytes_per_packet` (default 256 MiB each, four times the per-item
  default, 2 KiB to 128 GiB) and `someip.tp_max_derived_bytes_per_packet` (default 64 MiB, the buffered-bytes default, 2 KiB to
  16 GiB). The body, payload, or message that would pass the total is not parsed, `packet.error` names the setting and says it
  is the total per packet, and the later payloads of the packet are not dispatched. The per-item limits are unchanged
  (`http.max_decompressed_bytes`, `ws.max_decompressed_bytes`, `someip.tp_max_message_size`: a larger item stays undecoded or is
  refused by its own field). Unmasked, uncompressed payloads and undecoded bodies bind nothing and do not count. The protocol
  guide (§22.4) states the rule for dissector authors. A buffer export (`buffers=true`) lists these bytes.
- **A file written on the first nine days of a month read back with every timestamp 0 (ASC).** The header date pads a one-digit
  day with a blank (`Sun Nov  1 10:00:00.250 2026`); the reader demanded two digits for the day, so the start time stayed 0.
  It now reads a one-digit day with and without milliseconds, in the 24-hour and the 12-hour style.
- **The BLF exporter allocated about 0.5 MiB per export on its writer thread.** The first container compressed on the new thread
  grew its output buffer from an empty per-thread pool cache; the buffer is now rented on the calling thread at the container
  size, where the pool has the arrays (see the exporter guide, §11.7). With compression the exporter holds a buffer of the
  container size for the compressed output besides the two containers.
- **The BLF exporter wrote a CAN FD or CAN XL frame on a channel above 255 on another channel.** The objects store the channel in
  one byte, so channel 300 was written as 44. Such a frame is now skipped as `MalformedData` with a message that names the limit.
- **The BLF exporter cut a CAN FD frame that declared more than 64 data bytes to 64 bytes.** The frame is corrupt; it is now
  skipped as `MalformedData` instead of becoming a plausible 64-byte frame.
- **The ASC exporter repaired a corrupt or cut CAN payload count without a word.** A CAN FD frame that declared more than 64 data
  bytes, or a classic frame that declared more than 8, was written with its count cut to the maximum; a frame that declared
  more bytes than it carried was written with the bytes present. A count above the maximum is now skipped as `MalformedData`
  with a message that names the declared count and the limit, like the BLF exporter does for CAN FD. A frame cut by a snap length
  is still written with the bytes present, and the first such frame now raises one advisory `ItemSkipped` event
  (`MalformedData`, not counted as skipped) that names the declared and the present bytes.
- **The ASC exporter repaired a corrupt or cut LIN data length without a word.** A LIN frame that declared more than 8 data
  bytes was written with 8, and a frame that declared more bytes than it carried was written with the bytes present. A length
  above 8 is now skipped as `MalformedData` with a message that names the declared length and the limit, like for CAN. A frame cut
  by a snap length is still written with the bytes present, and the first such LIN frame now raises one advisory `ItemSkipped`
  event (`MalformedData`, not counted as skipped) that names the declared and the present bytes. A well-formed frame is unchanged.
- **The ASC exporter reported every write exception as `IoError`.** A failure of the line writer that is not an `IOException`
  is now `SerializationError`, like in the other exporters.
- **A strict BLF export reported one frame too many.** When the synchronous write of a container failed, the call that
  completed the container returned `true`; it now returns `false`. A write failure that is not an I/O failure is reported as
  `SerializationError`, not `IoError`.
- **`BlfExporter.Builder.WithCompressionLevel` accepted any number.** `(BlfCompressionLevel)7` became the default level without a
  message; it now throws `ArgumentOutOfRangeException`.
- **A CSV export with the apostrophe as delimiter shifted the columns of every neutralized cell.** With formula neutralization on
  (the default), a text cell such as `=1` was written as `'=1`; the added apostrophe is the delimiter, so readers saw an extra
  empty column. Such a cell is now quoted (`"'=1"`).
- **`CsvExporter.Builder.WithColumn` accepted a `CsvColumnKind` that is not defined.** `(CsvColumnKind)9` added a column with
  the header `9` and an empty cell on every row; it now throws `ArgumentOutOfRangeException` that names the defined kinds.
- **A CSV export reported a failed write of the output as a serialization error.** A packet whose rows could not be written
  because the output threw an `IOException` (a full disk, a closed pipe) raised `ItemSkipped` with `SerializationError`; it is now
  `IoError`, like in the other exporters.
- **A compact JSON export that spanned a session restart named the fields of the second stack with the names of the first.** The
  compact format writes a field name once per document and refers to it by field id afterwards, and field ids are dense per
  stack, so id 17 names another field after a restart. `JsonExporter` now refuses a packet of another stack than its first
  written packet's with `ExportErrorKind.StackMismatch`; the packets written before stay a valid document. Pretty, Array, and the
  flat shapes write the name of every field and are not affected.
- **The text export at its default level `standard` (and at `summary`) printed hardly more than the packet header.** A protocol
  layer (`frame`, `eth`, `ip`, `udp`, …) carries its raw bytes as a `Bytes` value, and the detail levels dropped every `Bytes`
  field together with its subtree, so the default output had no protocols. A protocol layer now stays visible below `full` as
  `Label: display text` without hex, `standard` goes on with its value fields, and `summary` writes one line per layer. Only a
  `Bytes` field without children, such as a payload, is still left out below `full`. `full` is unchanged. The "Known issue"
  notes about `text:level=full` are gone from the READMEs.
- **A text export wrote control characters of a capture unescaped.** A line feed in a DNS name or an HTTP header forged
  `Packet N` lines in logs, and an escape character reached the terminal of a text export to standard output. The C0 controls and
  DEL in field values and display texts are now written as `\n`, `\r`, `\t`, or `\xHH`; an escape counts with its escaped width
  toward `truncate`. Printable text, including non-ASCII, is unchanged.
- **A text export reported a failed write of the output as a serialization error.** An `IOException` now raises `ItemSkipped`
  with `IoError`, like in the other exporters.
- **The text, JSON, and CSV exports reported a hierarchy packet without a field tree differently from the other exporters.** A
  packet parsed with `FieldTreeMode.Skip` was reported as `ExportErrorKind.Other` (CSV with its own message). The Text, JSON, and
  CSV exporters now report it like DuckDB, Parquet, and PBF: skipped and counted, `ItemSkipped` with
  `ExportErrorKind.SerializationError` and the message `Skip packets cannot be exported (HasFieldTree is false).`, no exception.
- **`TextExporter.Builder.WithDetailLevel` accepted any number.** `(TextDetailLevel)7` behaved like `Full` without a message; it
  now throws `ArgumentOutOfRangeException`.
- **A PCAPNG interface with a very long name lost every frame, and other unusable interface metadata vanished without a word.**
  An `if_name` above 65 535 UTF-8 bytes made the interface description fail again for every frame of that interface (one
  `ItemSkipped` per frame, no frame written). A longer description and a negative, mistyped, or oversized option were dropped
  silently. Such metadata is now left out (never truncated), the frames are written, and one advisory `ItemSkipped` event
  (`SerializationError`, not counted as skipped) per interface names the options that were left out.
- **A PCAPNG export cut frames at the snap length without telling the caller.** The first frame above the snap length (262 144
  bytes by default) now raises one advisory `ItemSkipped` event that names the snap length and `WithSnapLength`, and the new
  `PcapngExporter.TruncatedFrameCount` counts all cut frames.
- **`PcapngExporter.Builder.WithHardware`, `WithOs`, `WithApplication`, and `WithComment` accepted a text the format cannot
  hold.** A text above 65 535 UTF-8 bytes ended the export with a failed start at the first frame; the setter now throws
  `ArgumentException`, and the `comment` option of the PCAPNG export reports it as an invalid option.
- **A packet whose parse ended in an error of a nested protocol lost the error and the nested parse when it was parsed again.**
  The TCP layer recorded the reassembled messages of such a packet with the dispatch mode "nothing dispatched", so a view, a
  filter, or a session that parsed the packet again showed no `packet.error` and none of the messages that had parsed before the
  error (for example after HTTP refused a body because of `http.max_derived_bytes_per_packet`). The mode of an error exit is now
  recorded, and the second parse equals the first.

### Dependencies

- `DuckDB.NET.Data.Full` 1.5.5 → 1.5.6.1.

### Contributors

- The commit-message workflow and the instructions state that a commit message, `CHANGELOG.md`, and review text carry no
  attribution trailer and no tool name: the person who commits is the only author.
- `NetworkInspector.Profiling` measures the exporters with the parse included (`export-*-hierarchy`, `export-*-selected`,
  `export-*-selected-tree`, `export-*-frames`) and has a `-sync` control next to every writer-thread scenario; compare a change
  with `--ab <baseline-exe> --repeat <n> --set <names>` against a frozen baseline executable. `--write-export-sizes <dir>
  --out <csv> [--frames <n>]` exports a fixed sample in every configuration and records the bytes of each, and
  `--compare-export-sizes <beforeCsv> <afterCsv> --out <md>` reports the change per configuration and exits 1 when one
  grew or is missing.

---

## [0.19.0] — Plugins for `ni`, reference-grade TCP, TLS, HTTP/2, and SOME/IP analysis, and restarts you can steer

Delta since 0.18.0. Version is `0.19.0` in `Directory.Build.props`.

**NetworkInspector can now be extended without changing it.** A plugin is an ordinary .NET class library with one entry
class. `ni` loads it from the folders in the application setting `plugins.paths`, and every command uses what it
contributes: a plugin protocol is decoded and filterable, a plugin source is an input of `ni convert` and `ni export`, a
plugin format is an output, and a plugin setting is listed and changed with `ni settings`. The standard protocols, the
file and random sources, and the nine export formats run on the same contract as four built-in plugins, so a plugin can do
whatever the built-ins do. Three packages carry the plugin system: `NetworkInspector.Plugins` (the contract),
`NetworkInspector.Plugins.Loader` (find, load, start, and stop plugins in any application), and
`NetworkInspector.BuiltInPlugins`. `ni` also gained settings profiles (`ni settings`, `ni profile`, `--profile`), the CSV
format, and `--overwrite`.

**The protocol stack analyses traffic the way the reference does.** TCP analysis is rewritten on an ordered list of
unacknowledged segments (lost, out-of-order, retransmitted, and spurious segments, duplicate and partial ACKs, bytes in
flight, connection state, completeness), TLS records and HTTP/2 frames are reassembled across segments with a stateful
HPACK decoder, SOME/IP is rewritten with typed payloads from a service description, service discovery, SOME/IP-TP
reassembly, end-to-end protection, and method timing, and ICMP, ICMPv6 (Neighbor Discovery, MLDv2), and ARP decode
everything their messages carry. Fragmenting protocols share one bounded `FragmentReassembler<TKey>`.

**Sessions make a restart observable and controllable.** A restart is a generation change (`ISessionReader.Generation`),
`TryRestart` takes options (what happens to running jobs), returns a report, and raises an event, and a restart that fails
leaves the session `Faulted` instead of resuming with inconsistent ids. The `ZeroAlloc` dependency is gone; the fixed
headers of IPv4, IPv6, TCP, UDP, VLAN, BLF, and PCAPNG are decoded by hand and faster.

### Highlights

| Area | What you get | Start here |
| --- | --- | --- |
| Plugins | Protocols, sources, formats, and settings as a DLL that `ni` loads | [Plugin guide](docs/plugins/guide.md) |
| Own host | Load the same plugins in your application | [Loader README](NetworkInspector.Plugins.Loader/README.md) |
| CLI | `ni plugins`, `ni settings`, `ni profile`, `--profile`, CSV, `--overwrite` | [CLI README](NetworkInspector.CLI/README.md) |
| TCP, TLS, HTTP/2 | Reference-grade analysis and reassembly | `tcp.analysis.*`, `tcp.completeness` |
| SOME/IP | Typed payloads, SD, TP, E2E, method timing | `someip.*`, `Schemas/someip-config.schema.json` |
| ICMP, ICMPv6, ARP | Complete message decoding, common field names | `icmp.*`, `icmpv6.*`, `arp.*` |
| Sessions | Restart generations, options, reports, `Faulted` | [Session guide](NetworkInspector.Sessions/SESSION_GUIDE.md) |

Try the plugin system with the sample plugin:

```bash
dotnet build samples/Sample.Protocol/Sample.Protocol.csproj -c Release
ni settings set plugins.paths samples/Sample.Protocol/bin/Release/net10.0 --scope application
ni plugins list
ni export "SampleGen:count=5" -o sample.sampletxt -f SampleText
```

### Upgrade notes

Every breaking change is described in detail in the sections below. In short:

| You used | Now |
| --- | --- |
| `ISession.Restart(factory)` | `TryRestart(factory, out report, out failure)` or the overload with `RestartOptions`; a restart ends running stack-bound jobs by default |
| `SessionPhase` switches | Handle the new value `Faulted` |
| Own `ISessionReader`, `ISession`, `IReadOnlySetting` implementations | Add `Generation`, `CurrentStack`, `Fault`, the new `TryGetPacket` overload, `TryRestart`, `Restarted`, the `TryAddJob` overload, and `Activation` |
| `ISession.TryRemoveJob` exceptions | `false` with `JobStillRunning` |
| `DatagramDefragmenter<TKey>` | `FragmentReassembler<TKey>` with `FragmentReassemblyOptions.IPv4` or `.IPv6` |
| `ZeroAlloc` (`LazyString`, `ZA.Lazy`, `FormatTemp`, `ToTempString`) | `NetworkInspector.Core.Fields.LazyString`, `LazyString.FormatLazy`, `Format`, `ToString`, `TryFormat` |
| `LinkType.WiresharkUpperPdu` | `LinkType.UpperPdu` |
| The old `someip.config_file` format, `SomeIpProtocol.UdpPortKey` and `TcpPortKey` | The new format with `schema_version`, `SomeIpRegistration.Register(builder)`, `SomeIpProtocol.DefaultPort` |
| ICMP, ICMPv6, and TCP field names and texts in filters and exports | The new names (listed under Changed and Fixed) |
| `tcp.max_tracked_streams` default 100000 | Default 10000; set it back per profile if you need more |
| CLI settings in the settings base folder | Move them to `<base>/default/`; application settings live in `<base>/application/` |
| `ni` replacing existing outputs | Add `--overwrite` |
| `ExportErrorKind` switches | Handle `StackMismatch` |

### Known limitations

- **Only the core tier exists.** `PluginTier.Host` and `PluginTier.App` are reserved for applications of a higher tier;
  `ni` is a core-tier application, and a plugin that declares a higher tier is listed as `Skipped` and its assembly is not
  loaded.

### Added

- **`SomeIpRegistration`** — the one place where a SOME/IP configuration becomes stack state: 21 `someip.*` settings (the
  configuration file, nine switches, the ports that are never learned, and ten limits with their range and seven texts each),
  `Register(builder)`, `Register(builder, stream)`, `Register(builder, config)` and `TryLoadConfig`. Every problem is a returned
  `SettingsLoadWarning`; a missing or broken file still registers the protocol without names. TCP framing uses
  `someip.tcp_max_message_size` (16 MiB) and `someip.tcp_resync`; a message above the limit is never framed and its drop is
  reported as `tcp.error.reassembly_limit`.
- **`SomeIpSettings`** — the immutable effective settings of the SOME/IP parser.
- **Content-based SOME/IP detection** (`someip.heuristic`, off by default) on `udp.heuristic` and `tcp.heuristic`: a plausible
  header (length of at least 8, protocol version 1, a defined message type) is decoded on any port.
- **Plaintext of an encrypted transport.** A protocol that decrypts TLS or DTLS owns the dispatch table of its transport
  (`tls.port`, `dtls.port`, keyed by the server port) and marks the plaintext it hands on (thread-local `PlaintextHandoff`,
  guarded by the packet id); SOME/IP binds the ports of `tls_ports` and `dtls_ports` in that table and accepts a unit when the
  marker carries a listed port. The TLS and DTLS parsers of this version do not decrypt and register no table, so a configuration
  that lists such ports is reported with one warning per list: the ports have no effect until a decrypting protocol is in the
  stack.
- **`TcpPortCache`** — the thread-local ports of the TCP segment being dispatched, the counterpart of `UdpPortCache`, so an
  application protocol reached without a dispatch key can tell whether it is on one of its ports.
- **SOME/IP service discovery is decoded by the new parser.** The `someip_sd` container is lazy and expands on demand; an eager
  scan announces to the index exactly the groups the expanded tree will hold, including one group per diagnostic, so a filter on a
  diagnostic is answered without opening the message. New: the option runs an entry references (`options_referenced`), the service
  and eventgroup names from the description (`someip_sd.entry.service.name`, `someip_sd.entry.eventgroup.name`), the annotations
  `(ANY)`, `(other service)`, `(infinite)` and `(unknown)`, the entry kind in the type ("Stop Offer Service", "Subscribe
  Eventgroup Nack"), configuration items split into key and value, load balancing, raw bytes for what is not a whole entry or
  option (`someip_sd.entries.raw`, `someip_sd.options.raw`), and the diagnostics `someip_sd.error.*` (13 kinds). The packet info
  lists the kinds of the entries (`SOME/IP-SD [Find] [Offer] (multicast)`). The reserved bits of an eventgroup entry
  (`someip_sd.entry.reserved2`) and the reserved byte of an endpoint option (`someip_sd.option.reserved2`) are shown, and
  configuration items go through the payload text decoder, so a control character from the wire is escaped (`\x1B`) instead of
  reaching a console that prints the tree.
- **A SOME/IP message with the TP flag and fewer than four payload bytes is malformed.** It shows its bytes raw and one
  `someip.tp.error.malformed` that names the shortfall; its bytes are no longer decoded as method parameters.
- **SOME/IP port learning (`someip.learn_sd_ports`, off by default).** UDP endpoints that service discovery announces decode from
  the announcement on and are marked `someip.learned_port`; TCP endpoints, ignored ports (`someip.learn_sd_ports_ignore`), the
  other-service id and withdrawn offers teach nothing. A packet's decision is recorded by its first parse, so a re-parse never
  changes.
- **SOME/IP-TP is reassembled by the Core `FragmentReassembler`.** Segments of a message are collected across packets (key: service,
  method, client, session, base message type, interface version) with alignment 16, first bytes winning on overlap, restart on a second
  segment at offset 0, least-recently-used eviction and the three limits `someip.tp_max_sessions`, `someip.tp_max_message_size` and
  `someip.tp_max_buffered_bytes`. The segment that completes the message shows the payload, `someip.tp.segments` (the contributors
  in offset order), `segment_count`, `reassembled_length` and `partial`; the last segment behind a hole delivers the decodable prefix
  once (`someip.tp.region`, `someip.tp.error.gap`) and a late segment completes the message. New diagnostics: `someip.tp.error.reset`,
  `.overlap`, `.malformed`, `.size_limit`, `.evicted`, `.dropped`.
- **SOME/IP payloads are decoded into typed fields from the service description.** Every usage site of a type is its own
  filterable field (`someip.payload.<service>.v<major>.<member>[.in|.out].<path>`, or the `filter_name` of the site) with the
  type of its value: unsigned and signed integers, floating-point values, booleans, strings, UUIDs; containers carry no value.
  The decode is lazy: a parse records the index group of the member direction and attaches `someip.payload`, and the
  parameters are built when somebody reads them. Covered: base types with byte order and lookup names, strings (UTF-8, ASCII,
  UTF-16BE/LE; control characters and invalid bytes are escaped, one terminating NUL and a byte order mark are removed),
  padding, variable, fixed and nested arrays with element bounds, structures with or without a length field, aliases, TLV
  lists and structures (all eight wire types, tag length instead of inline length, unknown ids as `someip.payload.tlv.unknown`),
  unions and bitfields. Anything that does not fit is a `someip.payload.error` and never an exception; bytes behind the last
  parameter are `someip.payload.unparsed`. `someip.decode_payload` and `someip.show_type_metadata` act; `someip.max_type_depth`
  and `someip.max_payload_nodes` stop a decode with a diagnostic that names the setting and its value.
- **SOME/IP end-to-end protection (profiles 04, 44, 08).** A protected member shows `someip.e2e` with its header fields before
  the parameters, which decode as for an unprotected member. `someip.verify_e2e_crc` (off by default) verifies profiles 04 and 44
  with CRC-32/AUTOSAR over the header bytes (TP flag cleared), the first eight header bytes and the payload behind the header, and
  adds `someip.e2e.crc_calculated` and `someip.e2e.crc_valid`; a segmented message verifies on the segment that completes it. A
  payload shorter than the header and an unknown profile are shown raw with `someip.e2e.error`.
- **SOME/IP method timing (`someip.method_timing`, off by default).** A response shows `someip.timing` with `request_in` and `delay`
  (or `request_missing`); a request shows nothing. Pairing uses service, method, client, session, interface version and capture
  interface (`someip.method_timing_ignore_interface` pairs across interfaces) and takes the latest pending request. A segmented
  request registers with its first segment, a segmented response pairs on the segment that completes it. `someip.timing_max_pending`
  bounds the pending requests; the oldest is forgotten and its response says so. The pair is stored with the response, so a re-parse
  shows what the first parse showed.
- **SOME/IP DTLS detection (`someip.detect_dtls`, off by default).** A datagram on a SOME/IP UDP port that starts with a DTLS
  record header (content type 20 to 63, version 0xFEFC to 0xFEFF, a first record that fits) and is not a plausible SOME/IP message goes
  to the DTLS parser. On UDP, `someip.heuristic` now also requires the first message to fit into the datagram.
- **`HexCase` and a letter-case choice for the hex text of `MacAddress`, `Eui64`, `Uuid`, and `IPv6Address`.** Upper case stays
  the default. `Format(HexCase.Lower)`, `FormatInto(destination, HexCase.Lower)`, and the format specifier `x` (`ToString("x")`,
  `$"{address:x}"`, `TryFormat` with the span, also the UTF-8 overload) write lower case; `X` and the empty format give upper
  case. Another specifier throws `FormatException` instead of being ignored.
- **`icmpv6.nd.error.hop_limit`:** a Neighbor Discovery message whose enclosing IPv6 hop limit is not 255 is reported (RFC 4861
  section 6.1.2).
- **`FragmentReassembler<TKey>`, `FragmentReassemblyOptions`, and `FragmentOutcome`** — one generic, bounded, pooled fragment
  reassembler for every fragmenting protocol. The options choose the limits (open datagrams, total buffered bytes, datagram
  extent, fragments per datagram), the alignment of non-last fragments, the overlap policy (largest duplicate wins, discard
  the datagram, first bytes win), the eviction policy (oldest created, least recently used), restart on offset 0, and the gap
  policy (report the decodable prefix once, then the full datagram). Contributor tags come back in offset order, so a
  completing outcome lists the packets that contributed.
- **`udp.heuristic` table.** UDP tries content-based parsers after no `udp.port` parser consumed the payload; the standard
  stack registers the table empty and parsers add themselves. A parser reached this way reads the datagram's ports through
  the thread-local `UdpPortCache`. The decision is stateless, so a re-parse behaves the same.
- **`tcp.error.resync`.** A TCP stream that skipped bytes to resynchronize its message boundary now shows the number of
  skipped bytes on the segment that carried them (and again on a re-parse). `PayloadDispatchEffect` gained the optional last
  parameter `ResyncSkippedBytes`.
- **Schemas and examples ship in the `NetworkInspector.Protocols` package** under `Schemas/`: the three JSON schemas (PDU
  Transport, Signal Message, SOME/IP) and a compact example for every configuration under `Schemas/examples/` —
  `pdu-transport/pdu_names.json`, `signal-message/can_and_pdu_bindings.json` and `multiplexed.json`, four `someip/`
  examples, and the settings group files of a profile in `profile/`. Before, no schema was packed.
- **`IngestGate` and `IngestScope`** (`NetworkInspector.Core.Protocols`) — the first-parse versus replay decision of a
  stateful protocol in one type: `IsReplay(id)`, `Enter(id)` (a scope whose outermost end raises the watermark, also on an
  exception), and `Watermark`. IPv4, IPv6, TCP, and UDP use it instead of four copies of the watermark and nesting fields;
  their behavior is unchanged.
- **`NetworkInspector.Plugins`** — the core-tier plugin contract: `IPlugin` (entry point found by the pre-scan through its
  own interface list), `IPluginBuilder`, `IPluginContext`, `IPluginLog`, `PluginInfo`, `PluginDependency`,
  `SourceRegistration`/`SourceCreate`/`SourceRequest` for Core `IFrameSource` sources,
  `ExporterRegistration<TListener>`/`ExporterCreate<TListener>`/`ExportRequest` for Core `IFrameListener` and
  `IPacketListener` exporters, and `PluginError`/`PluginErrorCode` for expected failures. Options and settings are Core
  settings; the contract adds registrations and lifecycle only.
- **`samples/Sample.Protocol`** — the tier 1 sample plugin: a protocol on UDP 40000 (`sample.counter`, `sample.gear`,
  `sample.speed`), a `NIS1` heuristic parser on the TCP heuristic table, a post-parser, the `SampleGen` source, the
  `SampleText` exporter, and the application setting `sample.greeting`.
- **`SettingsRegistrar`** has a public, validated constructor, so plugins can register settings into managers they own;
  `default(SettingsRegistrar)` now fails with an `InvalidOperationException` that names the missing manager.
- **`SettingScope`** (`Application`, `Profile`, `Session`) — the one scope enumeration of Core settings.
- **`NetworkInspector.Plugins.Loader`** (new package) — the plugin loader of `ni` and of every application that wants to
  load the same plugins: a metadata-only pre-scan (`System.Reflection.Metadata`; no assembly is loaded to look at it) that finds entry types by the
  plugin interfaces they list, a per-file scan cache keyed by path, length, and last write time, one collectible load
  context per plugin assembly, `PluginRegistry` (stack contributions, plugin settings, source kinds, and export formats with
  one consistent snapshot for readers), `PluginLifecycle` (start in load order and stop in reverse order, each bounded by the
  application settings `plugins.startTimeoutSeconds` and `plugins.stopTimeoutSeconds`), `PluginContext`, and the settings
  helpers `PluginPathsSetting`, `PluginTimeoutSettings`, and `RegistrationOptions`. Every failure of a plugin is a `Failed`
  record with a reason; plugins of a higher tier than the application are `Skipped` and their assemblies are not loaded. A
  plugin built against a newer version of an application assembly (contract, Core, framework) is refused before any of its
  code runs, with a reason that names both versions (`The plugin was built against NetworkInspector.Plugins 0.20.0.0, but
  this application provides 0.19.0.0; build the plugin against 0.19.0.0 or older, or update the application.`); the same
  or an older version runs with the application's copy.
  `ExportTargets` adds the target rules every export runner applies: `TryPrepare` (full path, parent folder, refused overwrite,
  folder-versus-file mix-ups), `TryCheck` (the same rules without deleting anything, for a check before a long run), and
  `DeletePartial` (cleanup of an export that ended without `OnFinish`). The package README shows the whole sequence of
  load, stack build, start, source use, and stop in one compilable program.
- **`NetworkInspector.BuiltInPlugins`** (new package) — the functionality of the application as four core-tier plugins on the
  same contract as external ones, created by `NetworkInspectorBuiltIns.CreateAll()`: `NetworkInspector.StandardProtocols`,
  `NetworkInspector.FileSources` (BLF, PCAPNG, legacy PCAP, ASC), `NetworkInspector.RandomSource`, and
  `NetworkInspector.Exporters` (frames as BLF, PCAPNG, ASC; packets as CSV, JSON, Text, PBF, Parquet, DuckDB). Every
  registration returns the Core source or exporter itself and takes its options as Core settings (for example `scanMode`,
  `errorTolerance`, `preloadBudgetBytes`, `frameSlabBytes`, `timestampTimeZone`, `delimiter`, `columns`, `style`), with the
  defaults of the command line.
- **Core settings: one text form, one JSON form, and problems instead of exceptions.** `SettingValueText.TryParse`/`Format`
  and `SettingValueJson.TryRead`/`Write` convert values for command lines and files; a wrong value is a `SettingProblem`
  (`SettingProblemKind`: `WrongType`, `BelowMinimum`, `AboveMaximum`, `NotFinite`, `UnknownEnum`, `InvalidPath`,
  `UnknownSetting`) with one message that names the setting and the accepted values. `Setting.TryValidate` and
  `Setting.TrySetPendingValue` do not throw for wrong values. `SettingActivation` (`Immediate`, `SessionReset`, `Restart`)
  and `Setting.Activation` say when an application should use a changed value.
- **Core settings: lookup, per-key apply, and merge saves.** `SettingsManager.TryGetSetting` and `FindSimilar`; the per-key
  `ApplyChanges(names)` and `ResetChanges(names)` with the `PendingChanged` event; `SaveChanged(names)` merges single
  keys into the group files under the cross-process folder lock `.ni-settings.lock` (waits up to two seconds, then
  `PersistenceSettingsException`); `Unset(name)`; `Orphans` and `TrySetOrphanValue` for stored values whose plugin is not
  loaded.
- **Core settings: profiles.** `SettingsProfiles` (`List`, `Exists`, `TryCreate`, `TryRename`, `TryDelete`), `ProfileNames`,
  and `StorageNames`: profile names of up to 256 characters (letters, digits, spaces, dots, dashes, underscores); names
  longer than 120 characters get a shortened folder name with a hash and a `.name` file.
- **CLI: `ni plugins list`, `ni settings list|get|set|reset`, `ni profile list|create|rename|delete`.** Plugins are loaded
  from the application setting `plugins.paths`; the commands print one precise line per mistake, with a suggestion for a
  misspelled name or key. Plugin log lines (`<level> <plugin>: <text>`) go to the error output and to `logs/ni.log`
  (10 MiB, 5 files) in the settings base. `plugins.paths` is empty by default, so no foreign code is loaded until you add
  a folder.
- **CLI: CSV export** (`-f csv:delimiter=;,header=false,bom=false,columns=ip.src;udp.dstport`), plugin source kinds
  (`SampleGen:count=10`) and plugin formats, and `--overwrite` for `convert` and `export`.
- **`NetworkInspector.Testing.Traces`** (test support, not packed) — deterministic capture files for tests, built once per
  cache folder and shared by parallel tests and processes: `TraceKit` (`GetAsync(recipe)`), `TraceRecipe`, and the recipes
  `MixedEthernetRecipe`, `CanSignalsRecipe` (with `CanSignalTrace` and `SignalConfigFixture`), `JumboFramesRecipe`,
  `OutOfOrderRecipe`, `RepeatedFailureRecipe`, `SampleProtocolRecipe`, and `LargeRandomRecipe`.
- **Plugin documentation** — [`docs/plugins/guide.md`](docs/plugins/guide.md) (a quick start that builds and runs, project
  setup, entry point rules, contributions, options and settings, lifecycle, build and distribution, registration with
  `plugins.paths`, isolation, security, troubleshooting, and a walkthrough of the sample plugin),
  [`docs/plugins/protocols.md`](docs/plugins/protocols.md) (protocol class, fields, dispatch tables, heuristic parsers,
  post-parsers, stream reassembly, protocol settings, checklist), [`docs/plugins/reference.md`](docs/plugins/reference.md)
  (one entry per contract type with a usage example), and package READMEs for `NetworkInspector.Plugins.Loader` and
  `NetworkInspector.BuiltInPlugins`. The root README lists the two new packages and when you need them.
- **Sessions: a restart is a generation change.** `ISessionReader.Generation` (0 for the constructor stack, plus one per swap;
  one volatile read) and `ISessionReader.CurrentStack`, the immutable `StackView(Stack, Generation)` pair with `IsCurrent`.
  The generation rides on the stack holder that every read already leases, so noticing a restart adds no operation to the
  read path; a poller that reads only `Generation` and `QueriesEnabled` sees stable, in progress, and done.
- **Sessions: `TryRestart` with options, a report, and an event.** `RestartOptions` (`RestartJobPolicy`: `CancelNow`,
  `WaitThenCancel`, `Refuse`; `JobWaitTimeout` 1 s to 1 h, default 30 s; `WaitCancellation`), `RestartReport` (new generation,
  re-parsed packets, jobs waited for and cancelled, listeners whose filter could not be re-bound, durations), and
  `ISession.Restarted` with `RestartNotification`: one queue and one thread per handler (an independent job named
  `restart-event`), so a blocking or throwing handler cannot influence another or the restart.
- **Sessions: jobs declare a binding and carry an end reason.** `JobStackBinding` (`StackBound` is the default of the old
  `TryAddJob`, `Independent` is the opt-in), the `TryAddJob(…, binding, Action<JobContext>, …)` overload with `JobContext`
  (the `StackView` the job started in, the reader, the token), and `JobInfo.Binding`, `Generation`, and `EndReason`
  (`JobEndReason`: `Completed`, `UserCancelled`, `StackReplaced`, `SessionShutdown`, `Failed`), stored before the terminal
  status so a snapshot never sees one without the other.
- **Sessions: a failed restart is a state.** `SessionPhase.Faulted` and `ISessionReader.Fault`: a restart that fails after
  the swap keeps its failure, refuses reads and new work with `SessionFaulted`, keeps ingest parked, and is left by another
  `TryRestart` (ids stay dense, ingest resumes without a gap) or `Shutdown`.
- **Sessions: `TryGetPacket(id, in options, builtFor, recycle, …)`** parses one packet with caller-built `ParseOptions`
  (observers, first protocol, field-tree mode) under the session's stack lease and refuses options built for another
  generation with `OptionsStackMismatch`.
- **`SessionErrorCode`:** `RestartPending`, `RestartFromJob`, `JobsRunning`, `SessionFaulted`, `OptionsStackMismatch`,
  `RestartInProgress`, `RestartOptionOutOfRange` (appended; existing values are unchanged).
- **Exporters: `ExportErrorKind.StackMismatch`.** PBF columnar, Parquet, and DuckDB build their catalog from the first packet's
  stack and now refuse a packet of another stack (reported through `ItemSkipped`, fatal for the export, the earlier rows stay a
  valid dataset) instead of writing field ids that name other fields.
- **Profiling: scenario `session-restart-reparse`** measures the restart itself (re-parsed frames per second over a
  200 000-frame memory source).

### Changed

- **SOME/IP messages are decoded by a new message core.** A unit holds any number of messages (up to
  `someip.max_messages_per_unit`, default 100). The tree adds `someip.msgtype.base` (the message type without the TP and ACK
  flags, so one filter selects a request whether or not it is segmented or acknowledged), the names of the service, the method
  and the client from the service description, the display text `0x1234 (Name)` (`(unknown)` where a description exists but
  does not know the id), and named diagnostics for a length below 8, a message cut off by the data, a protocol version other
  than 1, bytes behind the last message and a header cut short. The ids are shown in upper-case hex. The packet info reads
  `SOME/IP 0xF232 (ClimateControl) REQUEST`, or `SOME/IP (3 messages)` for a bundle.
- **A TCP stream that resynchronizes now delivers the message behind the skipped bytes at once.** The buffer looks for the next
  message again in the same call instead of at the next segment, so a stream that ends right behind a damaged region still
  delivers it. The skipped bytes are reported as before (`tcp.error.resync`).

- **A message that the capture length cut shows `[Unverified]` instead of `[Bad]`** in `icmp.checksum.status` and
  `icmpv6.checksum.status`: the parsers compare the payload length of the enclosing IP header with the message they received.
- **ICMP, ICMPv6 (Neighbor Discovery, Multicast Listener Discovery) and ARP decode what the messages carry.** ICMPv4 and
  ICMPv6 error messages show the next-hop MTU (`icmp.mtu`, `icmpv6.mtu`), the parameter pointer (`icmp.pointer`,
  `icmpv6.pointer`), the RFC 4884 length (`icmp.length`, `icmpv6.length`) and the unused or reserved bytes. The quoted
  datagram shows addresses, protocol, TTL or hop limit and, for quoted TCP, UDP and echo, the ports or the identifier and
  sequence number (`icmp.orig_ip.*`, `icmpv6.orig_ipv6.*`); quoted bytes that do not start with an IP header are shown as
  `data` instead of being dropped, and Source Quench is an error type. ICMPv6 echo payloads are shown as `icmpv6.data`.
  Router Advertisement flags are decoded completely (M, O, H, preference, P, S), the Prefix Information option shows
  the R and P flags, and the Route Information, DNS Search List, PREF64, Captive-Portal, Redirected Header, Advertisement
  Interval, Home Agent, Nonce and RA Flags Extension options are decoded. The link-layer address option follows its
  length (8 bytes MAC, 16 bytes EUI-64, other lengths raw bytes) instead of showing six bytes of any address as a MAC. MLDv2
  queries and reports are decoded (`icmpv6.mld.*`, `icmpv6.mldr.*`) and a query no longer shows its raw response code as a
  delay. A zero-length or truncated option ends the option walk with `icmpv6.nd.error.option_length`. ICMP and ICMPv6 codes
  have display text, the type, code, option, ARP opcode and hardware type tables are complete, and the packet info shows
  echo identifiers, error codes, Neighbor Discovery targets and link-layer addresses. ARP flags follow RFC 5227
  (`arp.isgratuitous` only for requests and replies, new `arp.isannouncement`, `arp.isprobe` excludes gratuitous packets),
  the info text names gratuitous, announcement and probe packets, and the ATM hardware types (16, 19, 21) use the RFC 2225
  layout. The container text of an ICMP or ICMPv6 message names its real length instead of "8 bytes". ICMPv4 Router
  Advertisement (`icmp.num_addrs`, `icmp.addr_entry_size`, `icmp.lifetime`, one `icmp.router_address` and `icmp.pref_level` per
  complete entry), Timestamp Request and Reply (identifier, sequence number and the three `icmp.*_timestamp` fields in
  milliseconds) and Address Mask Request and Reply (identifier, sequence number and `icmp.address_mask`) are decoded; a message
  that the capture cut short shows the fields up to the last complete one and the rest as `icmp.data`. These types no longer
  show their whole body as `icmp.data`.
- **Breaking: field names of ICMP and ICMPv6 follow the common abbreviations.** `icmp.redirect_gw` is `icmp.redir_gw`;
  `icmp.resp_in_ip` and its children are `icmp.orig_ip`; `icmpv6.nd.ra.flags` / `.flag.managed` / `.flag.other` are
  `icmpv6.nd.ra.flag` / `.flag.m` / `.flag.o`; `icmpv6.nd.na.flags` and its flags are `icmpv6.nd.na.flag` and `.flag.r`,
  `.s`, `.o`; `icmpv6.nd.target_address` is split into `icmpv6.nd.ns.target_address`, `icmpv6.nd.na.target_address` and
  `icmpv6.nd.rd.target_address`; `icmpv6.nd.redirect.dst` is `icmpv6.nd.rd.destination_address`; the option fields
  `icmpv6.nd.opt*` are `icmpv6.opt*` (`.len` is `.length`, `.prefix.flag.onlink` and `.auto` are `.prefix.flag.l` and `.a`).
  Filters, exports and value caches that use the old names must change.
- **Allocation: the ICMP, ICMPv6 and ARP parsers no longer box precomputed texts.** Table strings are passed as `LazyString`
  (a bare `string` bound to the generic overload and boxed a copy per field), the container titles and byte counts come from
  shared tables, and Neighbor Discovery addresses are no longer copied into `System.Net.IPAddress` objects; their display text now uses the same upper-case IPv6 format as every other IPv6 text (`Target Address: 2001:DB8::1`).
- **TCP analysis rewritten on an ordered list of unacknowledged segments.** Per direction the tracker remembers the segments
  that are not yet acknowledged (bounded at 10000, allocated on the first data segment) and compares every segment and ACK
  against them with serial-number arithmetic. Result: a gap is only `lost_segment`, a late segment is `out_of_order` (not a
  retransmission), a retransmission names the first transmission (`rto`, `rto_frame`), `spurious_retransmission` and
  `fast_retransmission` are reported next to `retransmission`, duplicate ACKs keep counting across data segments of the
  receiver and restart only when the acknowledgement number moves, ACKs of lost, partially acknowledged or retransmitted data
  are flagged (`ack_lost_segment`, `partial_ack`, `ambiguous_ack`), `bytes_in_flight` is the sum of the unacknowledged bytes,
  and the window scale is applied from the SYNs (including the "scale unknown" and "scale off" cases). The per-direction ring
  buffers are gone, so an idle connection no longer carries them. The result is compared field by field with the reference
  decoder in twelve recorded scenarios.
- **TCP connection state, completeness and packet info.** `tcp.analysis.connection_state` is derived from recorded FIN and ACK
  facts and follows RFC 9293 for the side that closed first (TIME_WAIT only when both FINs are acknowledged; a retransmitted FIN
  no longer moves it; a reset is final; a connection without handshake reads `ESTABLISHED (handshake not captured)`). New
  fields: `tcp.completeness` with `tcp.completeness.str` and one flag per part, `tcp.nxtseq`, the 12-bit `tcp.flags` word with
  `tcp.flags.ae`, `tcp.window_size_scalefactor`, and `tcp.segment`/`tcp.segment.count` on reassembled PDUs. The packet info reads
  `49152 → 80 [PSH, ACK] Seq=1 Ack=1 Win=65535 Len=100`.
- **TCP stream lifecycle.** A FIN from both sides or an RST ends the reassembly of a stream and frees its buffers; a SYN on the
  same address and port pair after the close starts a new stream (`tcp.analysis.reused_ports`). A reassembly that reaches its
  limit reports `tcp.error.reassembly_limit` with the limit's name and goes on with a fresh buffer instead of staying silent.
- **Reassembly for TLS and HTTP/2.** TLS records and handshake messages that span segments or records are joined
  (`TlsRecordDetector`, PDU 2 MiB, buffer 4 MiB per direction). HTTP/2 frames are framed by `Http2FrameDetector` (it follows
  `SETTINGS_MAX_FRAME_SIZE`, 16 MiB buffer per direction), header blocks across CONTINUATION frames are joined (up to 256 KiB),
  and HPACK is decoded with a stateful decoder per direction (dynamic table, size updates, at most 64 KiB), so indexed headers
  of later requests resolve. A protocol chosen by the heuristic table gets the same reassembly. HTTP/1 framing keeps a cursor per
  direction and handles `HEAD`/`CONNECT` replies and bodies that end with the connection.
- **`SequenceStreamBuffer`:** held out-of-order segments are kept sorted (binary search, no scan per insertion, ordered across the
  sequence wrap), the buffer remembers which packets delivered which bytes (`TryAppend(sequence, payload, packetId)`,
  `GetContributingPacketIds`), and exposes `BufferedLength` and `RebaseCount`.
- **FrameBuilder:** `TcpOptionsBuilder` has `Nop`, `Eol`, `SackPermitted`, `WindowScale` (shift up to 14), `Timestamps`,
  `Sack` (one to four blocks) and `Unknown`; `SynOptions` is built from these blocks.
- **Dependency removed:** `ZeroAlloc` is no longer referenced by any package. Deferred display text is now
  `NetworkInspector.Core.Fields.LazyString` (same members, same cache-in-place semantics); eager text uses
  `string.Create(CultureInfo.InvariantCulture, $"…")`; the fixed headers of IPv4, IPv6, TCP, UDP, VLAN, BLF, and PCAPNG are
  decoded by hand with one length check (parsing is faster in the profiling gate set); FrameBuilder headers write
  themselves. No per-thread scratch buffer remains: the former formatting path kept a 2 Mi-char `char[]` (4 MiB) per thread
  and allocated another one for every nested call. NIGEN015 now reports a prebuilt `LazyString.FormatLazy` / `LazyString.Lazy`
  as the last argument of a custom-text append. `UpperHex8` (internal) replaces the package's `Hex8` fragment where the text
  shows eight uppercase hex digits. The size hint `TryGetStringSize` of `MacAddress`, `Eui64`, `Timestamp`, and `Uuid` is
  now `static` (their text length is a constant).

- **CLI: profiles are applied.** `--profile` selects a settings profile for every command; a profile that does not exist is
  an error. The default profile moved from the settings base to `<base>/default` (application settings live in
  `<base>/application`). Files in the old location were never applied by the CLI.
- **CLI: an existing output is refused** (exit code 1, or 3 for a split file left over from an earlier run) unless
  `--overwrite` is given. A run that is cancelled or fails deletes the output that was still open and keeps the finished
  split files; before, Ctrl+C finalized a partial output.
- **CLI: `convert` always builds the protocol stack of the profile** (an exporter request carries it); frames are still
  parsed only with `--filter`. Source and format options go through the registered options of plugins, so an unknown key
  is refused with the closest valid one.
- **Core: `SettingsManager.Save` is sparse.** Only values that differ from their default are written, values of settings that
  are not registered are written back unchanged, and the save runs under the folder lock. A later change of a default
  reaches every folder that never chose a value.
- **Core: the exception texts of `Setting.SetPendingValue`** are the texts of the `SettingProblem` of the same mistake.
- **Core: `SettingsManagerFactory.ResolvePath`** accepts spaces and dots in profile names and refuses names that break
  `ProfileNames` (path separators, traversal, device names, leading or trailing space or dot).
- **Core: the `Setting` factory methods** take an optional `activation` parameter, and `IReadOnlySetting` has the member
  `Activation`; code that implements `IReadOnlySetting` must add it.
- **BREAKING (default): `tcp.max_tracked_streams` defaults to 10000** (was 100000), and the new `udp.max_tracked_streams`
  (default 10000) bounds the UDP stream tracker; both are secure by default and configurable. Past the limit a packet is
  still parsed, but not tracked, and a named message says so.
- **BREAKING (minor): `ISession.TryRemoveJob`** returns `false` with `SessionFailure` code `JobStillRunning` instead of
  throwing. `SessionErrorCode.ValueCacheInvalidRequest` is new (appended) for invalid value-cache options.
- **Random source:** `MaxFrameSize`, `TcpStreamOptions.MaxPayloadSize`, and `TimestampInterval` are range-checked against
  the length fields of the formats (the registration and the CLI refuse out-of-range values with the range); timestamps
  saturate at `long.MaxValue`; seeds of CAN FD frames with a DLC code above 8 now produce a different byte 4.
- **Protocols:** HTTP header-limit overruns are reported as `http.error.header_limit`; `json.error.string_limit` reports a
  truncated JSON string; `icmpv6.checksum.status` is always emitted when verification is on (`[Unverified]` without an IPv6
  layer); DHCP shows `dhcp.hw.mac_addr` only for a hardware address length of 6; the HTTP/2 client preface is shown as
  `http2.magic`.
- **Exporters:** the DuckDB exporter and the Parquet exporter drop a failed batch and count its packets as skipped; the
  Text exporter counts a skipped packet in Strict mode as skipped, like the other exporters.
- **Sessions: a stack replaced by `Restart` is disposed as soon as the last in-flight read on it ends** (a per-stack lease
  counter; no lock and no allocation on the read path) instead of immediately under running reads. Packets handed out before
  a restart keep their materialised fields; unmaterialised lazy parts run on the retired protocols and may yield an error
  child. `Shutdown` now also throws the `OnShutdown` errors of session-owned stacks, which were lost before.
- **Sessions: one stack lease per read call.** `ReadPackets` (both overloads) and `TryReadPackets` (both modes, including the
  skip-parse pre-check) take one lease for the whole call instead of one per 64-packet chunk or per packet, so a call parses on
  one stack generation and never returns packets of two stacks in one buffer; a restart that starts during the call leaves the
  remaining slots `null`. The cost is that a replaced stack lives until the call returns (bounded by the caller's buffer).
- **Core: a recycled packet is rebound to the stack of the new parse** when both stacks share the frame registry (a session
  restart replaces the stack but keeps the registry), so a buffer of recycled packets survives a restart without one allocation
  per slot. A stack of another registry is still refused (`ParseFailure.StackMismatch`, packet untouched). The internal
  `Packet.PrepareForReuse` takes the target stack.
- **Sessions: job end reasons for the session's own slots.** Sources, listener slots, and value-cache slots are independent jobs;
  `TryUnsubscribe` records `UserCancelled`, the shutdown `SessionShutdown`.
- **Sessions: `Shutdown` delivers queued `Restarted` notifications** within its time budget and reports the undelivered count in
  its `AggregateException`.
- **Documentation:** `SESSION_GUIDE.md` (generation, phases, jobs, restart, faulted state, parse with options, checklist),
  the Sessions `README.md`, and `EXPORTER_GUIDE.md` ("one stack per export") follow the new contract; the guide samples are
  compiled by `SessionGuideSamplesTests`.

### Removed

- `FormatTemp()` on `IPv4Address`, `IPv6Address`, `MacAddress`, `Eui64`, `Timestamp`, `Uuid`, and `SettingValue`;
  `ToTempString()` on `FieldValue`, `FieldValue.DefaultText`, and `FieldValueData`; `TryGetWrittenSize` on the six value
  types; the `IStringSize` and `IBinarySerializable` implementations of the value types, `FieldValue`,
  `FieldValue.DefaultText`, `FieldValueData`, and `SettingValue`.

### Fixed

- **TCP: `TcpConnection.WriteFromClient` / `WriteFromServer` sent nothing for a payload close to `int.MaxValue`.** The segment
  count was computed in 32 bits and wrapped negative; it is computed in 64 bits now.
- **TCP:** the window-size, flags and checksum fields now follow the common model: `tcp.flags` is the 12-bit word (text
  `0x012 [SYN, ACK]`, `0x000 [None]`), `tcp.checksum.status` is numeric (0 bad, 1 good), the option fields are named
  `tcp.options.sack_perm`, `tcp.options.timestamp.tsval`/`tsecr`, `tcp.options.wscale.shift`/`multiplier`, and the SYN-ACK
  acknowledgement is shown relative. Filters, exports and value caches that used the old names or texts change accordingly.
- **HPACK:** the decoder had no dynamic table, so every header that referred to an earlier entry was lost; it is stateful now.
- **SOME/IP: the packet info showed the service id with a doubled prefix** (`SOME/IP 0x0x1234 …`); it now reads
  `SOME/IP 0x1234 …`. Exports that contain the packet info of SOME/IP packets change accordingly.
- **IPv6: the Encapsulating Security Payload header was never shown.** `ipv6.esp` (with `ipv6.esp.spi` and `ipv6.esp.seq`)
  was recorded in the presence index but not emitted, and the 8 bytes of SPI and sequence number counted as payload of
  protocol 50. They now belong to the header part: the fields appear (`Encapsulating Security Payload (SPI: 0x0A0B0C0D,
  Seq: 100000)`), and the payload handed to the next stage starts after them.
- **CLI: `--profile` was ignored** by `convert --filter` and `export`; the protocol settings of the profile now apply.
- **Core: `SettingsManager.Save` dropped stored values** of settings that were not registered in the saving process (for
  example the settings of a plugin that was not loaded).
- **Core: field trees deeper than 16** no longer return stale entries from the traversal stack; `ChunkedGrowOnlyStore.ReadRange`
  stops for start indices above the maximum; more than 32767 lazy containers in one packet are refused with a field error
  instead of corrupting the materialization marker; `FieldValueData.Equals` is consistent with `GetHashCode` for equal byte
  slices of one array; `ValueCache` can no longer pair a stale payload type with a new series under concurrent first-seen
  recording; the array-container linear search can no longer read past its array during a torn read; `TempString` buffers of
  `FieldValue`, `FieldValueData`, and `SettingValue` formatting are released again; `TwoQueueCache` budget checks no longer
  overflow `int`; `LengthPrefixDetector` reports a total length below the header as invalid; reassembly keeps the first total
  length of a datagram and discards it on a conflicting last fragment.
- **Values: `FormatTemp()` released nothing.** `IPv4Address`, `IPv6Address`, `MacAddress`, `Eui64`, `Uuid`, and `Timestamp` built
  their `TempString` without the acquire epoch, so `Dispose` never matched and the thread-static char buffer stayed locked on
  that thread for good; every later `TempString` there silently fell back to a heap allocation and a buffer check on a reused
  pool thread failed at random. The epoch now travels into the `TempString`.
- **Core: concurrent readers of a lazy container** wait for a populator that another thread is running. The `materialize: true`
  guards of `Field` (`HasChildren`, `ChildCount`, `TryGetFirstChild`, `TryGetLastChild`), of every field iterator, and of
  `Packet.TryGetNextField` skipped a field that another thread had already claimed, so the second reader could return a child
  list that was still being built (rarely visible as missing children, about one run in a few hundred of the eight-thread
  growth test). A thread that is itself inside a populator still does not wait, so a populator that reads its own container
  cannot block on itself.
- **Core: settings.** `Save` no longer lets a refused stored value overwrite a registered setting's value, replaces a group
  file that another reader has open (Windows), and reports invalid storage paths as `PersistenceSettingsException`; a
  wrong-type preload reports `TypeMismatch`; `SettingsManager(string)` and `Setting.EnumWithMetadata` reject null;
  `EnumSettingMetadata.AllowedValues` is read-only; `SettingValueJson.Write` fails loudly for a value without a JSON form;
  `SettingsProfiles.TryDelete` can no longer remove the last two profiles concurrently and a failed rename rollback no
  longer throws; `StackBuilder.Build` throws on a second call, and protocol tables reject Bool/Any registrations on tables
  of another key type.
- **Filter:** an out-of-range `FilterCompileOptions.RegexTimeout` fails the compile with an `InvalidValue` error that names
  the allowed range (it was reported as "invalid pattern"); digit separators work in flank windows (`1_000ms`); a long-lived
  `Filter` no longer keeps the last queried `PacketIndex` and its bitmaps alive.
- **Sessions:** registrations, `TryStart`, and `Shutdown` are serialized (no lost random-access sources, `TryStart` succeeds
  once, user jobs are cancelled, joined, and disposed, late slots are refused); source-loop and shutdown phase transitions
  are atomic; listener and value-cache slots drain flags raised before cancellation and end `Cancelled`; the `Get*` lists
  are read-only views; a PullFill value cache rebinds when a restart races its registration; a rejected parse fails the
  slot; a restart re-parse recycles one packet.
- **Protocols (HTTP, HTTP/2, TLS, WebSocket, TCP, UDP):** HTTP/1 framing crashes and off-by-ones (status line, chunk sizes
  near `int.MaxValue`, trailers, header terminators), HPACK integer and Huffman decoding, the HTTP/2 client preface, TLS
  GREASE lookups (`KeyNotFoundException`), exact WebSocket index groups, the TCP checksum inside tunnels, a UDP length below
  8, and a zero UDP checksum over IPv6 (invalid there). A segment that the reassembly buffer refuses is dispatched raw
  instead of dropped.
- **Protocols (other):** DNS (`0x0x` ids, truncated records, NSEC bitmaps, DNS over TCP accepts the maximum message), JSON
  (lone trailing backslash, over-long strings), PDU transport (length of 0x80000000 or more), SOME/IP (service discovery
  array lengths of 0x7FFFFFF8 or more, TP segment validation, exact SD index groups), IPv6 (the extension walk stops at
  fragments), DTLS (handshake decoded for epoch 0 only, connection-id records), SLL and SLL2 (0x0600 is an EtherType),
  ICMP/ICMPv6 index groups, DHCP hardware address length, signal messages (`max_enum_values` minimum 1, duplicate
  `value_names` keys reported), repeated CAN configuration ids are reported, and the IP address caches are cleared per
  parse so a stale pair of another packet or stack cannot be read.
- **Sources:** BLF header dates from 2262 on saturate instead of wrapping or throwing; huge file-header sizes and
  uncompressed-container size mismatches are rejected; a failed decompression no longer leaves readers waiting forever;
  classic frames of types 100 and 101 keep their DLC like type 1; ASC hex payload lines with blanks no longer end an Ethernet
  packet early, FlexRay slots above 2047 and negative decimal CAN/LIN ids are rejected, keyword lines are matched by
  position, German dates in the trigger block parse, and oversized Ethernet records are reported; PCAPNG keeps the index of a
  malformed Interface Description Block and reports it, an empty mapped file reports a format error, and
  `EstimatedFrameCount` stays unknown after disposing a partial scan; the random source stores the byte count of CAN FD
  frames in byte 4.
- **Exporters:** file-open failures are reported through `ItemSkipped` instead of thrown; the columnar batch and the PBF
  standard block builder are all-or-nothing per packet; a failed Parquet or DuckDB batch is dropped and counted as skipped;
  the BLF header patch respects the stream start position; BLF LIN DLC is limited to 8 and equals the data written;
  ASC skips negative channels and limits the LIN length to 8; the Text exporter cuts at exactly `MaxTextLength` and shows
  the ellipsis only when something was cut; PBF Strict mode no longer counts a dropped truncated block in the trailer;
  PCAPNG skips link types above 65535; compact JSON writes the next packet in full after a failed write; the DuckDB
  database path is no longer interpreted as connection-string syntax; `PooledBuffer` rejects oversized requests with a
  clear message.
- **CLI:** the overwrite refusal of a split run names the first split file; `convert` honours a stopping exporter; `--tolerant`
  drops a source that fails twice in a row without delivering a frame (it looped forever); `export` and `convert` exit with
  code 3 when the output format stops early; `ni profile` file-system failures exit with code 3; a source option pair
  without `key=value` is an error; profile load warnings and plugin contribution failures are printed once; the `timestampSorted`
  and CSV delimiter option descriptions are correct.

### Security

- **CSV: formula neutralization.** Text cells that start with `=`, `+`, `-`, `@`, TAB, or CR get a leading `'` by default
  (`CsvExporter.Builder.WithFormulaNeutralization`, registration option `neutralizeFormulas`; turn it off for machine
  processing).
- **Protocols: decompression limits.** HTTP bodies and WebSocket permessage-deflate payloads are inflated through a bounded
  buffer that stops at `http.max_decompressed_bytes` and `ws.max_decompressed_bytes` (default 64 MiB, range 1 KiB to
  128 GiB); a hit is reported in a named field instead of truncating silently.

### Breaking

- **The SOME/IP parser is rewritten; the old configuration format, service discovery and SOME/IP-TP reassembly are
  replaced.** `SomeIpProtocol` takes a compiled service description and the effective settings
  (`new SomeIpProtocol(model, in settings)`; `SomeIpRegistration.Register(builder)` creates it), and `SomeIpProtocol.UdpPortKey`
  and `TcpPortKey` are removed (`DefaultPort` replaces both). The old `someip.config_file` format (`services[].methods[]` with
  `service_id`/`method_id` only) is no longer read: the new format carries a `schema_version` and is described by the schema
  `Schemas/someip-config.schema.json`. The segmented-message fields changed (see below). A unit on a
  port that is not bound to SOME/IP is accepted only with `someip.heuristic` on or as the declared plaintext of an encrypted
  transport.

- **`DatagramDefragmenter<TKey>` is replaced by `FragmentReassembler<TKey>`** (namespace `NetworkInspector.Core.Reassembly`).
  `ProcessFragment(key, offset, moreFragments, data)` becomes `Add(in key, offset, moreFragments, data, contributor)` and
  returns a `FragmentOutcome` instead of `byte[]?`: the completed datagram is `outcome.Payload` when `outcome.Kind` is
  `Complete`, and every refusal carries a `DiscardReason` (extent, alignment, buffer limit, fragment limit, overlap,
  conflicting end, beyond end). The constructor takes `FragmentReassemblyOptions` (presets `FragmentReassemblyOptions.IPv4`
  and `.IPv6` reproduce the former `new()` and `new(dropOnOverlap: true)`). `DatagramFragmentBuffer` and `FragmentAddResult`
  are removed. IPv4 and IPv6 behave as before.
- **`LinkType.WiresharkUpperPdu` is now `LinkType.UpperPdu`** (value 252 and the wire format are unchanged); its display name is  `Upper PDU`. Change the name where you refer to the enum member.
- **The lazy text moved out of the `ZeroAlloc` namespace.** `LazyString`, `LazyStringError`, and `LazyStringErrorCode` are now in
  `NetworkInspector.Core.Fields`; change the `using` (no other change: the members are the same). `ZA.Lazy(…)` becomes
  `LazyString.FormatLazy(tuple, static s => string.Create(CultureInfo.InvariantCulture, $"…"))`; `FormatTemp()` becomes
  `Format()` or `TryFormat(Span<char>, …)`; `ToTempString()` becomes `ToString()` or `TryFormat`; `TryGetWrittenSize(out size)`
  becomes the return value of `ToBytes`. `TryGetStringSize` of `MacAddress`, `Eui64`, `Timestamp`, and `Uuid` is `static` now
  (call it on the type, not on a value). Plugins that referenced `ZeroAlloc` directly must drop that reference.

- **`ISession.Restart(factory)` is removed; use `TryRestart`.** Every expected refusal is now a result (`false` plus a
  `SessionFailure`) instead of an exception: a wrong phase (`InvalidPhase`), a second restart (`RestartInProgress`, was
  `InvalidOperationException`), running stack-bound jobs under `Refuse` (`JobsRunning`), an ingest value-cache request the new
  stack cannot satisfy (`ValueCacheUnknownField` and its siblings, decided before any job is ended, nothing changed), and a frame that cannot be re-read
  (`FrameUnavailable`, the session is `Faulted`). Only caller bugs throw (null arguments, a disposed session, a factory that
  returns `null` or a stack of another registry); an exception the factory itself throws propagates unchanged. A restart now
  ends running stack-bound jobs by default (`RestartJobPolicy.CancelNow`); a job added with the old `TryAddJob` overload is
  stack-bound.
- **A failed restart no longer resumes ingest.** The session becomes `Faulted` (new `SessionPhase` value) and parks the source
  threads until another `TryRestart` or `Shutdown`; before, ingest resumed with a counter that contradicted the new stack's
  watermark (`ParseIdGap`). Code that switches over `SessionPhase` must handle `Faulted`.
- **New abstract members.** `ISessionReader` gained `Generation`, `CurrentStack`, `Fault`, and the `TryGetPacket` overload with
  `ParseOptions`; `ISession` gained `TryRestart` (two overloads), `Restarted`, and the `TryAddJob` overload with a binding.
  Implementations outside this repository must add them.
- **`ExportErrorKind` has a new member `StackMismatch`;** a columnar exporter that receives a packet of a second stack stops with
  it instead of writing a mixed dataset.

---

## [0.18.0] — Very large captures, faster filters and matching reads, and faster Core internals

Delta since 0.17.0. Version is `0.18.0` in `Directory.Build.props`.

Random access to file captures now needs well under 2 bytes of RAM per frame instead of 24–38 bytes, and a session
needs about 0.25–0.5 bytes per packet for its packet → frame map instead of 8. `FrameById` still returns exactly the
frame the first sequential read returned. Filter compilation is about 1 750 times faster, presence pruning and
matching reads skip most rejected packets, and the Core hot paths (field storage, value caches, packet index) are
rewritten behind the same API. The public API only gains members: `IRandomAccessFrameSource.FramesById` (with a
default implementation, so existing sources compile unchanged) and `FrameReadErrorKind.SourceFileChanged`. Text and
JSON exports are byte-identical to 0.17.0 on the golden inputs; filter verdicts, session results, and filtered exports
are byte-identical to the build before the filter changes on the reference corpus.

### Added

- **`FrameReadErrorKind.SourceFileChanged`** — raised once by `PcapngSource`, `BlfSource`, and `AscSource` when the
  capture file changed (length or modification time) while it was read; sequential reading then stops and
  `FrameById` returns `null`. Detected on Linux and macOS for file-backed reads; a preloaded source serves its
  in-memory copy and never raises it. Windows denies writers instead.
- **`IRandomAccessFrameSource.FramesById(FrameId first, Span<Frame?> frames, CancellationToken)`** — reads the range
  `first … first + frames.Length − 1` in one call and returns how many frames it found; ids that are not indexed or lie
  past the valid id range give `null` entries. A default implementation calls `FrameById` per id, so existing sources
  support it unchanged; `PcapngSource` and `BlfSource` read the range forward (index cursor, block prefetch, shared
  container bytes). Ranges of 64 ids in random order: 19.1 M frames/s against 5.9 M frames/s with one `FrameById` per
  id (memory-mapped PCAPNG, IMIX).
- **Profiling A/B harness** — `NetworkInspector.Profiling --ab <baselineExe> --repeat N [--set a,b] --out <csv>` runs
  baseline and candidate as interleaved child processes and writes one CSV row per run;
  `--report <csv> [--noise <aa.csv>] [--set …] --out <md>` turns it into a Markdown table with a gate verdict
  (exit 0 pass, 3 fail). `--min-improvement <fraction>` and `--floor <fraction>` are accepted with `--report` only.
  `--affinity <mask>` pins A/B children, `--warmup` and `--duration` override the phase lengths, and
  `--write-golden-inputs <dir>` writes deterministic PCAPNG inputs for export comparisons. `--write-filter-verdicts`
  and `--write-session-digest` write the oracles of the filter and session changes; a timed phase also reports its
  lock-contention count.
- **Retained-memory measurement** — `IProfilingScenario.RetainedBytesPerUnit`, CSV column `retained_bytes_per_unit`,
  a retained-memory table and a GC pause column in `--report`. CSV files without the retained column still read.
- **Profiling scenarios** — `footprint-*` (retained bytes per frame or packet), `access-*` (`FrameById` in random,
  sequential, and reverse order, also on four threads), `access-session-*` (`Session.TryGetFrame`),
  `access-pcapng-large-random` and `access-pcapng-large-sequential` (5 000 000 IMIX frames, about 2 GB),
  `access-session-readpackets` and `access-session-readpackets-interleaved`, `access-pcapng-batch-random` and
  `access-pcapng-batch-sequential` (ranges of 64 ids), `read-pcapng-full`, `read-asc`, `parse-combo-build-index`,
  `parse-combo-skip-index`, `filter-multi-nomatch`, `filter-compile`, `session-read-matching`,
  `session-read-matching-pruned`, `session-read-matching-sparse`, and `parse-combo-filter-observer-flank`. Filter
  scenarios repeat their timed workload over a reused batch so a timed phase measures seconds, not milliseconds.

### Changed

- **Filter evaluation backend.** `EvalTreeCodegen` replaces `ExpressionTreeCodegen`: logic, presence, value,
  `contains`/`matches`, flank, and scope nodes evaluate directly; no `System.Linq.Expressions` or `DynamicMethod`
  remains in `NetworkInspector.Filter`. `Filter.Compile` builds a tree of typed evaluation nodes instead of generating
  code at run time: 594 → 1 040 238 compiles per second, 13.3 → 5.3 KB allocated per compile. Evaluation throughput
  is unchanged.
- **Presence pruning.** `PrunePlan` resolves the dependency tree against the packet index once per index and
  evaluates it with the same short-circuit order as before; the per-packet walk of `CandidateBitmapBuilder` is gone.
  It stops at the first decisive child (filter-complex-nomatch +25 %, filter-indexed-nomatch +14 %, filter-complex
  +13 %, filter-scoped +10 %).
- **Matching reads.** `Session.TryReadPackets(…, PacketReadMode.Matching, …)` consults the filter's match cache first
  and runs an adaptive skip-parse pre-check with an equivalent `FilterObserver` that only rules packets out. The
  pre-check parse opens the same lazy containers the field-tree lookup would materialize (every container of the
  watched fields' protocols); the field-tree filter still decides every returned packet. A packet that survives the
  pre-check is re-parsed from the frame the pre-check already read, so it costs one source read instead of two. For
  stateless filters without scopes and without `matches`, most rejected packets are ruled out with a parse without
  field tree (session-read-matching-sparse +40 %, 950 → 192 bytes allocated per packet). The pre-check switches itself
  off with a doubling backoff while most packets match (match-all reads within noise).
- **Filter and session A/B.** Measured against the build before the filter changes, with the profiling scenarios
  pinned to the performance cores; every other Filter and Session scenario stays within its noise.
- **Compact frame index (PCAPNG/PCAP, BLF, ASC).** The index keeps only where each accepted frame starts, packed in
  groups of 64 with bit-sliced deltas; interface, length, timestamp, and bytes are re-read from the file with the
  scanner's own parser. Retained memory per frame after a full scan (memory-mapped / disk-backed):

  | Capture | Before | After |
  | --- | --- | --- |
  | PCAPNG, 1 M × 64 B | 24.1 B | 0.40 B |
  | PCAPNG, 500 k IMIX | 24.2 B | 1.58 B |
  | BLF, 1 M CAN | 38.2 B | 0.67 B |
  | ASC, 1 M CAN, absolute | 32.0 B | 0.40 B |
  | ASC, 1 M CAN, relative | 32.0 B | 1.71 B |
  | Session over 500 k IMIX packets | 32.3 B/packet | 1.87 B/packet |

- **Session packet → frame map** packed per 64 packets (0.25 B per packet for one source, 0.5 B for two
  interleaved sources, 8 B before; exact for any input).
- **Field storage** — a packet keeps its field slots in a first chunk of 16 slots that it owns for its whole life,
  plus 16-slot follow-up chunks from a thread-local slab; a recycle clears only the used slots, keeps up to 64
  follow-up slots, and hands the surplus to a per-thread pool of at most 1 024 slots (64 KiB), so a recycled packet
  that keeps parsing similar frames allocates no field storage (1 553 → 528 B per packet). A classic CAN packet fits
  in the first chunk alone. Own A/B step: +6.1 %, four-thread re-dissection +16.6 %.
- **Field insert path** — append, prepend, and insert-after share one insert core that reads the seal state once and
  skips value-cache and observer work with one flag when neither is active. Own A/B step: +15.5 %, tree-building
  parses +20 … +27 %, skip-mode parses +5.6 … +7.8 %.
- **Value-cache appends** — the series gate already serializes appends, so the column stores append without their
  own compare-exchange and keep a tail-chunk cache. Own A/B step: +5.6 %, all-field caches +6 … +17 %.
- **Packet index** — roaring-bitmap adds take an append fast path for ascending ids, the seqlock end is a release
  store, and `TryBeginPacket` skips the bitmap for ids above the highest indexed one. Own A/B step: +8.4 %, index
  recording +14.5 % (tree-building) and +23.6 % (skip mode).
- **File sources keep the capture locked for their lifetime.** `Open` holds the file with `FileShare.Read` until
  `Dispose`, now also for preloaded files: on Windows other processes cannot write it meanwhile, and `Open` fails
  while another process writes it. Linux and macOS get an advisory lock plus change detection at `Open`, every
  4 096 sequential frames, at the end of a read, and by a background check once per second that sets an atomic
  flag; `FrameById` makes no system call and only reads that flag. Windows runs no background check.
- **ASC in-memory backend** keeps one buffer instead of a slice per line (16 B less per line).
- **ASC disk backend** reads each frame with one positional read sized by the index: the open-time scan stores, per
  group of 64 frames, the power-of-two window that covers every record of the group (the line, and for Ethernet its
  continuation lines plus the line that ends the packet), at 1/64 B per frame. Reading ids in order (`NextFrame`, or
  `FrameById` forward or backward) is served from a per-thread 64 KiB read-ahead window. Before, a line took a 512-byte
  read, a 64 KiB read when the line was longer, and a 16 KiB read for Ethernet. Best of ten runs against the earlier
  compact-index build: `NextFrame` +297 % (CAN), +82 % (Ethernet); `FrameById` in order +526 % (CAN), +140 %
  (Ethernet), +3 534 % on four threads (CAN); random `FrameById` +1 % (CAN), +97 % (Ethernet).
- **BLF** resolves frame interfaces from an immutable snapshot without a lock (the lock is taken only to register a
  new interface), and the sequential path keeps its current container row, so neither random nor sequential access
  pays for the smaller index.
- **PCAPNG** packet blocks are read with direct field access instead of a generic struct parse (scan and random
  access), and `FrameById` on a memory-mapped file copies the packet while it holds the view lease.
- **In-order `FrameById` and session reads** decode each group of 64 index entries once per thread through a
  lock-free per-thread cache (2 KiB per reading thread): +11 % for PCAPNG and +12 % for `Session.TryGetFrame` in id
  order, measured against the same build without the cache.
- **Speed trade-off:** `NextFrame` is as fast or faster everywhere (PCAPNG up to +23 %, ASC +75 %); `FrameById` is
  faster for BLF (+18 % random) and ASC (+183 % random), but slower for PCAPNG (−36 % random, −19 % in order on one
  thread, −4 % on four threads), because the block header is re-parsed instead of cached; that re-parse is what lets
  a changed or corrupt block return `null` instead of a wrong frame.
- **`Dispose` of a memory-mapped `PcapngSource` and of a `BlfSource` waits for `FrameById`/`FramesById` calls running
  on other threads** (at most one frame, or one chunk of 64 ids). Calls that start after `Dispose` throw
  `ObjectDisposedException`, as before; a PCAPNG call already running when disposal began returns `null` for the frames
  it could no longer read. `NextFrame` keeps its view lease, so a dispose from a `FrameSkipped` handler never waits for
  its own thread.
- **Reads by id share no written cache line between threads** (a per-thread reader slot replaces the per-call
  reference count of PCAPNG and the read lock of BLF), **a PCAPNG re-read requests the cache lines of its block at once**
  (at most 4 KiB), the interface of a re-read packet is resolved with one field read, and **BLF keeps the last container
  per reading thread**.
- **Session:** `ReadPackets` and `Restart` read frames 64 packets at a time, one range call per run of consecutive frame
  ids of a source; the random-access sources are kept in a sorted table instead of a dictionary.
- **Performance** (median of five interleaved runs against the build with the compact index;
  `GATE: PASS`, geomean +22 %, retained memory unchanged):

  | Scenario | Change |
  | --- | --- |
  | PCAPNG `FrameById` random / in order / reverse | +10.6 % / +10.5 % / +14.6 % |
  | PCAPNG `FrameById` 4 threads in order / reverse / random | +209 % / +162 % / +36 % |
  | BLF `FrameById` random / in order / 4 threads | +18.7 % / +30 % / +288 % |
  | Session `TryGetFrame` random / in order / 4 threads | +13.7 % / +9.6 % / +197 % |
  | Session `ReadPackets` (one source / two sources) | +4.9 % / +5.2 % |
  | 2 GB capture, `FrameById` random / in order | +6.8 % / +3.9 % |

### Fixed

- **ASC:** `Dispose` racing a `FrameById` could dereference a registry that `Dispose` had just cleared
  (`NullReferenceException`). A read now takes one snapshot of the registry and returns `null` when it is gone, so the
  documented outcomes are `null` or `ObjectDisposedException`.
- **BLF:** a sequential read after a full scan with `CacheBudget = 0` decompressed the container again for every
  frame; it now decompresses each container once. After a full scan the source no longer keeps the last
  decompressed container alive.
- **BLF:** concurrent `FrameById` calls that waited for the same container returned `null` when the container cache
  could not keep the result (`CacheBudget = 0` or a container larger than the budget); the waiting calls now get the
  decompressed bytes.
- **BLF lazy scan:** when every frame indexed so far failed to build, `NextFrame` ended the stream although the scan
  had not reached the end of the file; it now scans on, so lazy and full scans return the same frames.

### Breaking

- **Open file sources hold their capture for their whole lifetime, preloaded files included.** Code that opens a
  capture and then overwrites, renames, or deletes the same path while the source is still open now fails (Windows) or
  sees `FrameReadErrorKind.SourceFileChanged` (Linux and macOS). Dispose the source first.
- The public API otherwise only gains members (see Added).

---

## [0.17.0] — FrameBuilder hardening and package READMEs that compile

Delta since 0.16.0. Version is `0.17.0` in `Directory.Build.props`.

This release makes FrameBuilder keep its promises: a build allocates nothing per frame (proven for single-frame,
fragmenting and session builds), the stateless and the stateful path run one engine and report the same
`BuildStatus`, length fields never wrap, padding never leaks buffer residue, constructors reject values the wire
format cannot carry, and `TcpConnection` fails loudly instead of delivering empty or partial segments. Sequences
gain `Dispose` and are declared with `using`. Every package README is rewritten around a quick start that compiles
against the current API, and Filter ships its own package readme.

### Added

- **`FrameSequence.Dispose` / `StatefulFrameSequence.Dispose`** — declare sequences with `using`; disposing a
  sequence that stopped before its last fragment releases the per-thread scratch buffer. Before, one abandoned
  fragmenting build made every later fragmenting build on that thread allocate a private buffer.
- **`BuildStatus.SessionDisposed`** — a `StatefulFrameSequence` whose `Session` was disposed (and maybe re-rented)
  returns `false` with this status instead of writing into the new session's counters.
- **Limit constants** — `IPv6HopByHopLayer.MaxOptionBytes` (2046), `IPv6RoutingLayer.MaxTypeSpecificDataBytes`
  (2040), `FlexRayLayer.MaxPayloadBytes` (254).

### Fixed

- **FrameBuilder allocations** — the capability probes boxed the layer struct about seven times per Eth/IPv4/UDP
  frame; builds now allocate 0 bytes after warm-up.
- **Stateful status** — sessions rejected nothing a layer reported: UDP over IPv6 with a pinned zero checksum was
  emitted in a session but rejected stateless. Both paths now return `InvalidLayerState`, and a packet that fails
  before its first frame leaves IP ids, TCP sequence numbers and SOME/IP session ids unchanged.
- **Length fields** — IPv4 TotalLength, IPv6 PayloadLength and UDP Length above 65 535 wrapped silently (raw-IP
  roots, large MTUs); the build now returns `InvalidLayerState` and writes no frame.
- **IPv6 Hop-by-Hop padding** — the options form left up to five padding bytes uninitialised (previous-frame residue);
  they are Pad1 zero bytes now.
- **`TcpConnection`** — SYN with options ignored a failed build and handed an empty span to the sink, a late fragment
  failure was lost, and the mutator and session window were bypassed for SYN / SYN-ACK with options. A segment the
  carrier cannot build now throws `InvalidOperationException` naming the status, none of its frames reaches the sink,
  and sequence, ACK and window state stay unchanged; `OnSegment` sees SYN and SYN-ACK with options.
- **`TcpConnection.Open`** — an explicit `WindowSize: 0` was rewritten to 65535; it is emitted as a zero window now.
- **Debug and Release** — the IPv6 fragment layer no longer asserts in Debug builds only.

### Changed

- **WebSocket masking** — writes straight into the writer span: one copy less and no `ArrayPool` rental per frame.
- **Producer buffer cap** — `TcpConnection.WriteFromClient<TProducer>` / `WriteFromServer` keep producer buffers up
  to 64 KiB per thread and release larger ones after the write.
- **Docs** — every package README is rewritten around highlights, install, and a quick start that compiles and
  runs against the current API; shields show NuGet version, downloads, .NET 10, and license. New
  [`NetworkInspector.Filter/README.md`](NetworkInspector.Filter/README.md), now the Filter package readme instead of
  `FILTER_GUIDE.md`. The root README lists the Filter package and maps goals to packages.
- **Docs fixes** — Values constructors and text formats (uppercase hex, ISO-8601 timestamps), Generators hook names
  and attribute assembly, Sessions `TryStart(out failure)`, the lifecycle diagram (`Idle → Stopped`,
  `Idle → ShuttingDown`, no `Stopped → ShuttingDown`), CLI `pcap` alias, export extension inference, exit codes,
  `--tolerant` scope, exporter defaults, the Parquet `value_timestamp_ns` column, and the CSV delimiter rule.
  Removed links to the missing `docs/` folder.
- **FrameBuilder docs** — [`FRAMEBUILDER_GUIDE.md`](NetworkInspector.FrameBuilder/FRAMEBUILDER_GUIDE.md) describes
  FrameBuilder as the public library it ships as, lists every file, the constructor limits and the `using` rule; the
  FrameBuilder README "Limits" states the memory-ownership rule (`ReadOnlyMemory<byte>` arguments are referenced, not
  copied), the closed session-state slot set and the scope of the TCP helper.

### Breaking

- **Constructor validation** — out-of-range values throw `ArgumentOutOfRangeException`: `SocketCanLayer` /
  `SocketCanFdLayer` ids above 0x7FF (standard) or 0x1FFFFFFF (extended), `LinLayer` checksum types other than 1 and
  2, `FlexRayLayer` payloads above 254 bytes and type indexes above 127, `IPv6HopByHopLayer` options above 2046 bytes,
  `IPv6RoutingLayer` data above 2040 bytes. Before, the layers masked or wrapped the value and emitted a
  self-inconsistent frame.
- **`TcpConnection.Open`** — `options` is `TcpConnectionOptions?`; omit it for the defaults. `Mss: 0` throws.
  `default(TcpConnectionOptions)` passed explicitly has every field at zero and is rejected.
- **`IcmpV4EchoLayer` / `IcmpV6EchoLayer`** — constructors are `(identifier, sequenceNumber, isReply, code,
  checksum)`; `type` is gone. Positional calls such as `new IcmpV4EchoLayer(8, 1)` still compile but now mean
  identifier 8, sequence 1; use named arguments. Other ICMP types go through `IcmpV4Layer` / `IcmpV6Layer`.
- **`IPv6Layer`** — the `mtu` parameter and `MaxPayloadSize` are removed; they never limited anything. Use
  `EthernetLayer(maxFrameSize: …)` and an `IPv6FragmentExtensionLayer`.
- **`WebSocketLayer`** — implements only `IStreamProducer`; `Then(new WebSocketLayer(...))` no longer compiles (it
  emitted a TCP segment without WebSocket bytes). Pass it to `TcpConnection.WriteFromClient` / `WriteFromServer` or
  call `WriteStream`.
- **`ArpWriter`** — removed; use `ArpLayer.OpcodeRequest` / `ArpLayer.OpcodeReply`.
- **`TcpStreamLayer`** — public (its constructor stays internal), because the public `TcpConnection` session types
  name it.

---

## [0.16.0] — Filter completion, frame slabs, and capture round-trip fidelity

Delta since 0.15.0. Version is `0.16.0` in `Directory.Build.props`.

This release adds editor completion and "did you mean" suggestions for filter expressions, shared frame slabs and a single memory-mapped view for faster PCAPNG, BLF, and ASC reading, and lock-light chunked stores for faster value-cache ingest. CAN DLC 9–15, the LIN protected identifier, PCAPNG interface metadata, and unknown link types survive a read/write round trip. Session restart and shutdown no longer leave a half-changed session when they race a running source. TCP and UDP connection keys use a process-seeded hash, the Internet checksum is exact on every SIMD path, and settings loads enforce their size cap on the bytes actually read. `PcapSource` is renamed `PcapngSource`.

### Added

- **`Filter.GetCompletions(expression, caret, stack?)`** — editor completion for filter expressions while typing: the word or operator at the caret (`ReplaceStart`, `ReplaceLength`, `Prefix`) and the candidates that fit the grammar there (protocols, fields, aliases, keywords, operators, `flank` arguments) in alphabetical order, with kind and value type (`FilterCompletions`, `FilterCompletionItem`, `FilterCompletionKind`). Names come from a per-stack catalog built once.
- **`FilterError.Suggestion`** — an unknown name close to a known one reports `… Did you mean 'udp.srcport'?` and carries the name for a quick fix.
- **`RoaringBitmap.OrWith(ReadOnlyRoaringBitmap)`** — in-place OR from a view without cloning the viewed bitmap.
- **Filter limits** — expressions nest at most 64 levels and hold at most 1024 terms; beyond that `Compile` / `TryParse` fail with a positioned `SyntaxError` instead of overflowing the stack.
- **Frame slabs** — `FrameSlabSize` on `PcapngSourceOptions`, `BlfSourceOptions`, `AscSourceOptions` (0 = off, 1 KiB … 16 MiB) groups sequentially read frames into shared slabs: +5 % to +35 % read throughput in the PCAPNG measurement, at the cost that one held frame keeps its slab alive. `FrameById` is unaffected.
- **BLF Ethernet types 121–123** (forwarded, error EX, error forwarded) are read like Type 120.
- **BLF time zone and channel names on export** — `BlfExporter.Builder.WithTimestampTimeZone(zone)`; interface names other than the default are written as AppText channel names, which the BLF reader turns back into interface names.
- **ASC timestamp precision** — `AscExporter.Builder.WithTimestampDecimals(6..9)` (default 6); integer formatting, rounded, 9 digits round-trip nanoseconds.
- **PCAPNG interface metadata round trip** — the reader stores `if_hardware`, `if_txspeed`, `if_rxspeed` as well; the writer writes `if_description` and every stored `if.*` option back, and SHB options fall back to the source file's `capture.*` values.
- **Columnar catalogs** — `description` column in the Parquet and DuckDB field catalogs and a `protocols` table (`protocols.parquet`, DuckDB `protocols`).
- **DuckDB resource settings** — `DuckDbExporter.Builder.WithMemoryLimit(bytes)` / `WithThreads(n)`; without them DuckDB's own defaults apply.
- **Stream options** — `PcapngStreamSource.FromStream` and `BlfStreamSource.FromStream` take the file options class (`ErrorTolerance`, `FrameSlabSize`, BLF limits); unbuffered streams are wrapped in a 64 KiB `BufferedStream`.
- **`ProtocolRegistration.RegisterFairProfilingProtocols`** — registers only Frame, Ethernet, IPv4, IPv6, UDP, and TCP, in this order: no TCP heuristics, no stream reassembly configurations, no application protocols. The profiling tool's parse scenarios use it; its export and read scenarios keep `RegisterStandardProtocols`.

### Fixed

- **Session restart race** — a source thread that passed the parse gate just before `Restart` closed it now parks again under the parse lock instead of allocating id 0 from the reset counter; the source job no longer fails and no frame is lost.
- **Restart with an unsatisfiable ingest cache** — `Restart` builds the new ingest value cache and packet index before it changes anything. `ValueCacheUnknownField` / `ValueCacheInvalidFieldName` now leave the session as it was (old stack, open parse gate, queries enabled) instead of a closed gate and phase `Restarting`. A stack installed by the restart is owned by the session from the swap on and is no longer disposed when a later step fails.
- **Runtime value cache during restart** — a read refused with `QueriesDisabled` pauses a PullFill window instead of failing the slot.
- **Listener filters from another stack** — `TryAddListener` re-binds a filter whose `Stack` is not the session stack (and re-checks after registration, so a concurrent `Restart` cannot leave a retired-stack filter installed). A filter that cannot be re-bound throws `ArgumentException`.
- **Shutdown with a timeout** — a source that returns from `NextFrame` after `Shutdown(timeout)` ends `Cancelled`; the parse gate is no longer disposed under it and the job keeps its cancellation token.
- **Filter slices with two-digit bounds** — `udp.payload[10:12] == 0a:0b` parses; `10:12` inside brackets is no longer lexed as a byte literal.
- **Observer with `flank` and other terms** — `FilterObserver.TryReadMatch` evaluates the whole expression (`A && flank(…)`, `!flank(…)`); before, only the flank verdict counted.
- **Stateful filter without match cache** — `EnableMatchCache = false` is ignored for `flank` filters, so re-querying a packet no longer advances the tracker twice.
- **`len (name)` with spaces** — the name span reported to `OnFieldNameSpan` covers the name.
- **Oversized `within:` windows and scope indexes** — rejected with `InvalidValue` instead of a silent saturation or wrap.
- **PCAPNG error tolerance** — `PcapngSource.Open` / `FromData` apply `ErrorTolerance`; strict mode after a full scan returns the frames in front of the first error instead of none.
- **Legacy PCAP records** — the file reader bounds `incl_len` by the snaplen (`MalformedHeader`) and reports a record cut by the end of the file as `TruncatedStream` instead of a shortened frame.
- **PCAPNG interfaces** — a section with more than 65 536 IDBs no longer attributes packets to `id mod 65536`; an invalid `if_name` falls back to `Interface {n}`; `FrameById` works during a lazy scan for every indexed frame (PCAPNG and BLF).
- **CAN DLC 9–15** — BLF and ASC readers keep the payload count in SocketCAN byte 4 and the bus DLC in `len8_dlc` (byte 7); the BLF and ASC writers restore it (ASC writes the DLC as one hex digit). ASC remote frames keep their requested DLC.
- **LIN** — both readers write the protected identifier with parity; the checksum model (`CSM = classic|enhanced`) survives ASC and BLF round trips.
- **BLF robustness** — Type 71 keeps a VLAN tag with TCI 0; unparsable or truncated objects raise `FrameSkipped` (file and stream); absolute timestamps saturate instead of wrapping; a container header above the largest array is a format error; `Dispose` reports mapping failures.
- **Exporter cleanup** — all exporters finish through one sequence: a failing trailer is reported, every buffer and the output are still released, release failures arrive as one `AggregateException`.
- **BLF channel routing** — interfaces from ASC (`asc.channel`) and interface names like `CAN 3` keep their channel; an unusable channel property skips the frames with `ItemSkipped`.
- **Filter lexing and observers** — hex-like identifiers inside brackets stay identifiers and literals after `]` keep their kind; a flank leaf no longer leaks into the next frame; `EnableMatchCache = false` is also ignored on derived `flank` filters.
- **Dispose during reads** — `Dispose` of a memory-mapped PCAPNG or BLF source while `NextFrame` or `FrameById` runs (for example a `Shutdown` with a timeout) defers the unmap until the running copy ends instead of unmapping under it.
- **LIN error objects** — BLF LIN error objects v1 and v2 carry the protected identifier with both parity bits, like LIN frames.
- **Parquet and DuckDB failures** — in tolerant mode an unwritable table file or database path raises `ItemSkipped` and the export ends cleanly; strict mode aborts the export.
- **PBF strings** — consecutive strings are laid out back to back, also when the buffer grows between them.
- **Connection-key hashing** — TCP and UDP connection keys hash with the process-seeded `HashCode` over 32-bit words. The previous fixed XOR-rotate mix let colliding connections be computed offline from a crafted capture and turn every table insert into a long probe (denial of service).
- **Internet checksum** — every SIMD path sums the RFC 1071 network-order words and keeps every carry: u32 lanes move into the u64 sum before they can wrap (payloads above 1 MiB), and large pseudo-header sums fold until no carry is left. TCP over IPv6 uses the 32-bit upper-layer length in the pseudo-header (jumbograms, reassembled payloads).
- **TCP reassembly with a zero-length PDU** — a boundary detector that reports a complete PDU of length 0 moves the stream to `Error` instead of keeping the dispatch loop alive forever.
- **IPv6** — an Authentication Header with payload length 0 is malformed instead of taking its sequence number from the next header; option texts read `Pad1` / `PadN` without an underscore; the container text names the stored length including the extension-header chain.
- **UDP and TCP re-parse** — a UDP re-parse without a recorded stream effect reports a missing stream instead of a misleading insufficient-data error; TCP analysis timings keep double precision on re-parse and print with the invariant culture (`0.025`, not `0,025`, on a German thread).
- **TCP heuristic table** — TCP finds its heuristic table by `TcpProtocol.HeuristicTableName` when the stack starts, so a custom stack that registers the table under that name gets heuristic detection without the internal setter.
- **Protocol configuration and display texts** — CAN signal configs skip JSON `null` message entries with a warning; `ConfigLoader` reports a path the OS cannot represent (NUL character) as a configuration error instead of throwing; the CAN XL VCID text has a single `0x` prefix; unknown TCP option kinds print their number once; the LLC SAP lookup uses the full byte, so group addresses get their names.
- **`RoaringBitmap` readers on ARM64** — the seqlock fences both edges, so a reader no longer validates half-updated data; a read section that throws because a writer grew an array under it retries instead of throwing.
- **Empty cloned index containers** — `ArrayContainer` and `RunContainer` cloned from an empty container grow on the first add instead of writing past a zero-length array.
- **`ChunkedOuterArray` concurrent allocation** — two threads that allocated chunks at the same time could lose one: growth copied the outer array, an install landed in the old array after the copy, and the publish of the copy dropped it. The array now has one allocator (see Breaking); an overlapping allocation fails loudly instead of losing a chunk. The chunked stores and `CachedFrameSource` always allocated from one thread and were not affected.
- **Chunked store append during `Clear`** — an append that races `Clear` publishes into the new generation; readers never pair a count with chunks of another generation.
- **Full field tree** — a parse that fills the field tree still records the caught exception in `packet.error` instead of throwing out of `TryParse`; every catch boundary formats the message the same way.
- **Datagram defragmentation** — stale FIFO entries of a key that started a new datagram after a discard are ignored, so the older datagram is evicted first.
- **Stream reassembly** — a held segment is copied before its array returns to the shared pool; a payload whose size sum would wrap `int` is refused; `LengthPrefixDetector` rejects an offset plus size above `int.MaxValue`.
- **`TwoQueueCache`** — a byte-sized weight budget no longer reserves millions of map entries up front.
- **Settings size cap** — settings and configuration loads cap the bytes actually read, not only the length the file system reported, so a file that grows during the load or a non-regular file cannot bypass `MaxConfigFileBytes`.
- **Settings persistence** — access denials and saving onto a directory throw `PersistenceSettingsException` like every other file-system failure; a group file with a repeated top-level property or an undecodable property name is skipped with a warning and the other files still load; registering a preloaded setting while `Load` runs applies the preload instead of throwing; validation messages use the invariant culture.

### Changed

- **Shutdown waits** — a second `Shutdown` caller and a shutdown that waits for a running restart use kernel events instead of spin-and-sleep polling.
- **Read loops** — `ReadPackets` and `Matching` pulls snapshot the random-access source map once per call and reuse the last source lookup.
- **Value-cache ids** — the NotifyOnly listener of the ingest cache no longer burns a `ValueCacheId`; runtime caches are numbered right after the ingest cache.
- **Docs** — new [`NetworkInspector.Sessions/SESSION_GUIDE.md`](NetworkInspector.Sessions/SESSION_GUIDE.md); phase rules in `ISession` and the Sessions README match the code (listeners and value caches are refused while a restart runs).
- **Filter `matches`** — each pattern is compiled once at parse time and runs on the linear-time `NonBacktracking` engine when possible; only patterns that need backtracking use the timeout. Derived filters keep the pattern timeout.
- **Filter alias prune** — candidate bitmaps for alias groups clone only the first member bitmap.
- **Chunked stores** — `ChunkedGrowOnlyStore` and `ChunkedAppendOnlyStore` (and with them `EffectStore` and the value-cache series) publish an append with one interlocked operation instead of five: chunks and count form one generation, and `Clear` replaces it with one volatile write. Value-cache ingest +24 % to +34 %, `skip-cache-port` +13 %, parse paths that record effects +2 % to +4 %, no profiling scenario slower. Readers that overlap `Clear` still miss; `Clear` allocates one small object per call.
- **Filter docs** — [`FILTER_GUIDE.md`](NetworkInspector.Filter/FILTER_GUIDE.md) §11 documents completion, errors and spans; Sessions snippets match the current signatures; limits and `len()` / slice semantics are specified.
- **Memory-mapped reading** — PCAPNG and BLF use one mapped view for scans and concurrent `FrameById` (no accessor pool); files above `Array.MaxLength` are never preloaded.
- **BLF decompression** — each container is inflated once on the sequential path (the scanner's result is cached); output buffers are not zero-filled first.
- **ASC reading** — vectorised line classification and tokenising; disk-backed `FrameById` parses the rented line in place.
- **PBF / columnar** — string encoding without a thread-static scratch buffer; the batch size estimate is O(1).
- **Packet index** — once a packet of a higher 65 536-id chunk commits, `PacketIndex` converts the completed chunks to run containers where that is smaller (`RoaringBitmap.RunOptimizeKeys`); the values do not change.
- **LZ4 decompression** — overlapping matches use doubling block copies instead of a byte loop.
- **Dependencies** — `ZeroAlloc` 0.6.2; `CSharpStyleChecker` is pinned to `1.0.*` until Protocols absorbs the new CSC009 rule.
- **Docs** — [`SOURCE_GUIDE.md`](NetworkInspector.Sources/SOURCE_GUIDE.md) (backends, frame slabs with measurements, error tolerance in file order, volatile fields), [`EXPORTER_GUIDE.md`](NetworkInspector.Exporters/EXPORTER_GUIDE.md) (finish sequence, BLF nanosecond timestamps and time zone, shared bus-field representation, test layout) and the Sources / Exporters READMEs match the code; the Sources Quick Start compiles.

### Breaking

- **`PcapSource` renamed `PcapngSource`** — also `PcapSourceOptions` → `PcapngSourceOptions`, `PcapStreamSource` → `PcapngStreamSource` (CLI: `PcapngSourceConfig`). The types read PCAPNG and legacy PCAP as before.
- **Removed `PcapngSourceOptions.MaxHandles` and `BlfSourceOptions.MmapSlotCount`** — there is no view pool any more.
- **BLF timestamps default to UTC** — `BlfSourceOptions.TimestampTimeZone` and the BLF writer use `TimeZoneInfo.Utc` instead of `TimeZoneInfo.Local`. Pass `TimeZoneInfo.Local` for files from or for Vector tools and `tshark`.
- **Stream factories** — `PcapngStreamSource.FromStream(stream, uiName, leaveOpen, options)` and `BlfStreamSource.FromStream(stream, uiName, leaveOpen, options)` replace the overloads without options (the BLF time-zone parameter moved into the options); `BlfStreamSource.MaxUncompressedContainerSize` is read-only.
- **Unknown DLTs pass through** — PCAPNG frames with a link type outside `LinkType` are returned as `(LinkType)raw` with their bytes instead of being skipped, and the PCAPNG exporter writes them back.
- **SocketCAN byte 4 from BLF** — classic CAN frames with DLC 9–15 now carry 8 in byte 4 (was the raw DLC) and the DLC in byte 7.
- **LIN byte 5 from BLF** — the protected identifier with parity (was the bare 6-bit id).
- **`IErrorTolerantFrameSource`** — the contract now states what the sources do: `FrameSkipped` is raised in both modes, strict mode stops in file order.
- **`ISession.TryAddListener(ISessionListener, IFilter?, …)`** takes `NetworkInspector.Filter.Filter?`. Other `IFilter` implementations were rejected at runtime before; now the compiler rejects them.
- **`TryAddJob` after `Shutdown`** returns `false` with `InvalidPhase`; before, it started a thread nobody would cancel or join. `Stopped` without shutdown still accepts jobs.
- **`len()` is a byte length** — strings count UTF-8 bytes (was UTF-16 units); `Bool`, `I64`, `U64`, `F64`, `Timestamp` and `Uuid` can now be sliced, big-endian, matching their `len()`.
- **`IFilter.TryDerive`** returns `out IFilter?`; `Filter.TryDerive` keeps the concrete `out Filter?` overload.
- **Removed `FilterCompileOptions.CaretPosition`** (never read; use the caret parameter of `Filter.GetCompletions`) and **`FilterErrorKind.UnknownProtocol`** (never produced; value 5 stays unused, the other values are unchanged).
- **`ChunkedOuterArray` has one allocator** — allocating `GetOrAllocateChunk` calls and `Clear` must not overlap; an overlap throws `InvalidOperationException` from a compare-exchange gate that never waits (no lock). Reading an installed chunk (`GetChunk`, or `GetOrAllocateChunk` when the chunk exists) stays lock-free on any thread. Calls may follow one another on different threads. The factory runs once per allocation and must not call back into the instance; the spare chunk of a lost install race is gone.
- **Removed `InsufficientDataInfo`** — the record struct was never produced by any parser.
- **Settings group name `default` is reserved** — the default group is stored as `default.json`, so a group of that name would share its file. `NameValidation.IsValidGroupName("default")` returns `false`.

---

## [0.15.0] — SessionFailure host control and filter observer hardening

Delta since 0.14.0. Version is `0.15.0` in `Directory.Build.props`.

This release makes `Session` operable after a capture stops: operational `Try*` calls return a `SessionFailure`, listeners and value caches may attach in `Stopped` and backfill the current packet range, and multi-source ingest records the packet-to-frame map under the parse lock. `FilterObserver` rejects a second `flank`, fails containerless protocol presence closed, throws from `IsMatch` when `HasFlank` is true, and densifies field dispatch. Classic filters can turn off the match cache and prune via index groups; decimal digit separators lex as one integer.

### Added

- **`SessionFailure`** — operational `Try*` refusals return `false` with a `SessionFailure` (`SessionErrorCode` plus invariant message). New codes include `QueriesDisabled`, `PacketNotFound`, `JobNotFound`, and `JobTerminal`. Validation faults still throw `SessionException`.
- **`QueriesEnabled`** — false while restart rewrites packets, after restart fails closed, and after shutdown disables reads. A false packet read means “no row” only when this is true.
- **`FilterCompileOptions.EnableMatchCache`** — defaults to `true`. Set `false` for forward-only convert/export so per-id match bitmaps do not grow without bound. Derive keeps the caller's flag.

### Changed

- **Post-Stopped attach** — listeners, value caches, and jobs may be added in `Idle`, `Running`, `Restarting`, or `Stopped`. `ShuttingDown` is refused. An attach after packets exist is notified for the current id range. Frame sources stay `Idle` only.
- **Listener filter gate** — `Matching` `TryReadPackets` and restart rebind share one gate per listener. `All` reads do not take the gate.
- **Wait and shutdown timeout** — one budget for every source job together, not a fresh timeout per source.
- **Idle source remove** — `TryUnsubscribe` can remove a not-yet-started source. `FrameSourceInfo.Stop` clears the callback only when unsubscribe returns true.
- **Packet-to-frame chunks** — 8192 `long` entries (64 KB), under the large-object heap threshold.
- **Observer field dispatch** — dense field-id → slot tables; leaf tests can stop once the boolean root is permanently decided (flank still samples).
- **Index-group prune** — `CandidateBitmapBuilder` uses `FilterSymbol.IndexGroup` bitmaps when present.
- **`UnknownProtocol`** — reserved; unknown protocol names report `UnknownField` (same message shape as unknown fields).

### Breaking

- **Session `Try*` outs** — operational methods take a trailing `out SessionFailure?`. `TryReadPackets` keeps `out FilterError?` and adds `out SessionFailure? sessionFailure`; exactly one is set on failure. Update every call site.
- **`FrameSourceInfo.RegisterStopCallback`** — takes `Func<bool>` instead of `Action`. Return true only when stop/unsubscribe succeeded.
- **`FilterObserver.IsMatch`** — throws when `HasFlank` is true. Call `TryReadMatch` for flank observers.
- **Observer multi-flank** — a second `flank(...)` in one observer expression is a compile error (at most one flank).
- **Containerless protocol presence on observers** — fails compile with `NeedsFieldTree` instead of throwing from `FieldWatch.Only`.

### Fixed

- **Multi-source ingest** — allocate, parse, and append the packet-to-frame map under one lock so a second source cannot observe the next id before the first frame is stored.
- **Restart / Shutdown** — mutually exclusive; shutdown waiters are always released; the restart factory must not call `Shutdown`, `Restart`, `Dispose`, or `TryStart`.
- **Source-job publication** — `_SourceJobs` is published only when every slot is filled.
- **Foreign listener filter** — a non-`Filter` `IFilter` is rejected before any listener state is stored.
- **Observer `by:` check** — non-integer fields fail the same integer-field check as tree `Filter.Compile`.
- **Decimal digit separators** — `1_000` lexes as one integer (radix forms already did).
- **Lazy materialize coverage** — Filter.Tests exercise two-pass lazy field materialization on miss.

## [0.14.0] — Field watches, Vector ASC layout, and TCP corrections

Delta since 0.13.0. Version is `0.14.0` in `Directory.Build.props`.

This release parses through `Packet.TryParse` and a `ParseOptions` struct, so a field tree, a value cache, and a field watch are independent choices on one call. A filter expression can compile to a tree-free `FilterObserver`. ASC import and export cover Ethernet and CAN XL. TCP analysis, reassembly, and port dispatch match the handshake that was actually seen. `PacketId.Invalid`, a default field cursor, and a full parse-buffer table fail closed.

### Added

- **Field watch** — `ParseOptions.Observers` is a `ParseObserver` list (`IFieldObserver` plus `FieldWatch`) for that parse only. `FieldWatch.None` is the default and reports nothing. `FieldWatch.All` reports every produced field and expands every deferred group. `FieldWatch.Only` names one field or several. `BeginPacket` runs before any `OnField`. The synthetic packet root is not reported. A skip parse delivers `FieldVisit.Value` and no cursor. A build parse can navigate to the parent and to siblings already linked.
- **Filter observer** — `Filter.Compile(expression, stack, in FilterObserverOptions)` or `Filter.TryCompile` returns a `FilterObserver`. It is not a `Filter`. Read `IsMatch` after `TryParse`. When `HasFlank` is true, read `TryReadMatch` before the next frame. A subtree scope (`$udp { ... }`) fails with `FilterErrorKind.NeedsFieldTree`.
- **ASC Ethernet and CAN XL export** — `AscExporter` writes Ethernet as one `ETH` line (hex length, no FCS) and CAN XL as `CANXL` / `XLFF`. 0.13 skipped both as `UnsupportedType`. A length outside the allowed range is `ExportErrorKind.MalformedData`.
- **TCP stream-key settings** — `tcp.stream_key_last_vlan` and `tcp.stream_key_frame` default to false. Set either to true to split `tcp.stream` on the innermost VLAN id or the capture interface.

### Changed

- **ASC timestamps** — line time is whole seconds plus a nanosecond fraction, scaled in `long`. A tenth digit of 5 or more rounds up. Absolute lines replace the clock and add to the header date. Relative lines accumulate, including timestamped lines that are not frames. A relative split file starts at the previous-log absolute time.
- **ASC Ethernet import** — `ETH` and `AFDX` data frames are Ethernet. Wrapped hex joins until `DataLen` is met. `ETH STAT`, `ETH RxEr`, `AFDX STAT`, and `AFDX BUS` are not frames.
- **ASC CAN XL import** — `XLFF` is CAN XL. `CBFF` / `CEFF` stay classic. `FBFF` / `FEFF` stay CAN FD. Channel and the data-length column are decimal. Other numeric columns stay hex under `base dec`. Error and overload frames are SocketCAN error frames.

### Breaking

- **Parse entry** — `Packet.ParseFrame`, `ParseFrameIndexed`, `TryParseFrame`, and `TryParseFrameIndexed` are removed. Parse with `Packet.TryParse` and a `ParseOptions` struct. An id jump and a recycle precondition return `ParseFailure` and do not throw. `RecycleError` remains the code used inside packet recycle.
- **ASC text from 0.13** — CAN FD DLC is one hex digit under `base hex` (`12` becomes `C`). LIN channel 1 is `Li`, not `L1`. FlexRay writes a hex cycle, NM `0`, sync `0`, and a hex byte count, not a payload-word count and a decimal cycle. `AscSource` reads this layout. The token `L1` is not channel 1.

### Fixed

- **TCP keep-alive, FIN, and window scale** — a keep-alive (one sequence before next, 0 or 1 data bytes) sets `tcp.analysis.keep_alive` and not `tcp.analysis.retransmission`. After a handshake, the server `FIN, ACK` is `CLOSE_WAIT`. A FIN with no pure SYN stays `FIN_WAIT_1`. `tcp.window_size` uses that sender's own SYN shift. SYN and SYN-ACK do not publish `tcp.window_size`.
- **TCP reassembly and HTTP** — an out-of-order segment waits for the hole. A retransmission is not appended twice. The first payload sequence is the left edge. An HTTP/1 request split across segments is one message on the completing segment. Non-HTTP bytes on port 80 set `http.error.not_http`. A client source port of 80 does not select HTTP when the learned server port is elsewhere. HTTP/2 heuristic accepts only the connection preface.
- **TCP stream table** — when `tcp.max_tracked_streams` is full, the next five-tuple sets `tcp.error.stream_limit` and omits `tcp.stream`.
- **ASC CAN FD, classic CAN, LIN, and FlexRay columns** — hex-base FD DLC `12` is rejected; decimal-base DLC `12` is twelve; FD DLC 15 is 64 bytes; classic DLC 15 stores 8 bytes. A remote frame with no DLC is RTR and length 0. LIN channel 1 is `Li`. FlexRay cycle and byte count follow the file base.
- **Invalid packet id** — `Packet.TryParse` with `PacketId.Invalid` returns false and `ParseFailure.InvalidPacketId`. The creating overload leaves `packet` null. The recycle overload leaves the packet unchanged. Id 0 on a fresh stack still parses. The invalid id is not a replay.
- **`IsRoot`** — `Field.IsRoot` and `MutField.IsRoot` are true only for a valid cursor whose storage index is 0. `default(Field)` and `default(MutField)` are not the root.
- **`BindParseBuffer` cap** — the 255th additional buffer is stored and `BufferCount` becomes 256. The next `MutField.BindParseBuffer` returns empty, does not store, and `TryGetEffectLayerKey` on that empty memory is false. The cap is a constant (effect-key bits 31–24).
- **`ChunkedGrowOnlyStore`** — after `Append` returns, every index in `0 .. Count-1` is a published value, or a concurrent `Clear` has set `Count` to 0. `Clear` does not leave a hole at index 0. `Get` returns the constructor's unset sentinel on a miss, not null unless that sentinel is null. `ReadRange(int.MinValue, buffer)` fills every slot with null and returns `buffer.Length`.
- **Name lookup** — a null name throws `ArgumentNullException` from `GetProtocolId`, `GetFieldId`, `GetFieldAliasGroupId`, `GetIndexGroupId`, `GetProtocolTableId`, and `GetHeuristicProtocolTableId` on `Stack` and `StackBuilder`. An unknown name returns null.
- **Invariant error text** — `Invalid protocol ID: {n}` and `Slab allocation failed after {attempts} attempts (requested {count} slots).` format those integers with `CultureInfo.InvariantCulture`.

## [0.13.0] — Vector LIN events, Type 120 Ethernet, and PCAPNG limits

Delta since 0.12.0. Version is `0.13.0` in `Directory.Build.props`.

This release writes Vector LIN event objects and Ethernet Type 120, keeps an explicit BLF channel of 0, and reads PCAPNG captured bytes under one block-size cap. `FrameInterfacePropertyKeys` is no longer a public Core type; the property strings are unchanged.

### Added

- **BLF Ethernet Type 120 export** — `BlfExporter` writes `ETHERNET_FRAME_EX`: a 32-byte header, then the raw frame. VLAN TCI 0, QinQ inner tags, and an FCS trailer survive. Type 71 is not the exporter path. Direction is 0 (RX).
- **`blf.hw_channel`** — a `ushort` on the interface is written at Type 120 offset 6 with flags bit `0x0002`. Readers store a valid Type 120 or Type 102 hardware channel on the same key and keep that interface separate. Absent means the field is omitted.
- **BLF CAN error Type 2** — a SocketCAN frame with the error flag is `CAN_ERROR`, not classic Type 1.
- **BLF LIN events** — data frames (error byte 0, message type 0) are Type 57 with `object_version` 1. CRC / receive / send errors (bits `0x08` / `0x02` / `0x01`) are Types 60, 61, and 58. Sleep (event byte `0x01` or `0x02`) is Type 20. Wakeup (`0x04`) is Type 62. Any other LIN shape is skipped.
- **BLF CAN XL nested classic and FD** — Type 139 with the XLF flag clear reconstructs classic (8 bytes plus the data length) or CAN FD from FDF, BRS, ESI, and the remote-frame flag. Type 140 stays skipped.
- **FlexRay Type 50 channel and dual CRC on export** — Channel A/B is `channelMask`. The ISO header CRC is written to both `headerCrc1` and `headerCrc2`.

### Changed

- **PCAPNG captured length** — `PcapSource` / `PcapStreamSource` expose captured packet bytes only (`Frame.Data` / `Frame.Length`). EPB, obsolete PB, and legacy `orig_len` are not stored.
- **PCAPNG scan continues after a packet skip** — `Open` / `FromData` index the rest of the file. Skips from that pass are raised as `FrameSkipped` on the first `NextFrame` after `Start`. `ErrorToleranceMode.Strict` makes later `NextFrame` calls return null after the first skip. A malformed packet, a missing interface, an unknown DLT (`FrameReadErrorKind.Other`, message `Unknown link-layer type {raw}; frame skipped.`), or an unsupported `if_tsresol` skips that packet and continues. A block boundary that cannot be rounded, is below the minimum size, runs past EOF, exceeds 134348832 bytes, reads short, or has a mismatched trailer raises `FrameSkipped` with `CorruptedBlock` and then stops. The first section header that fails this check throws, and the source does not open.
- **ASC channel lookup** — `AscExporter` still reads `asc.channel`, then `blf.channel`, then its default.
- **PCAPNG `if_name` and `if_fcslen`** — `PcapngExporter` writes `FrameInterfaceInfo.UiName` as IDB `if_name`. A byte at `if.fcs_length` is `if_fcslen`; omit it and option 13 is absent. Per-packet EPB flags are not written. Snap truncation stores `orig_len` from the pre-snap `Frame.Length`.
- **PCAPNG `WithTimestampResolution`** — decimal power-of-ten exponent 0..19 (6 = microseconds, 9 = nanoseconds). Bit 7 or a value above 19 throws `ArgumentOutOfRangeException`.
- **Ethernet Type 120/102 length** — `frame_length` must be at least 14 and those bytes must all be present. A short object fails. Alignment zeros after the frame are not a packet.
- **FlexRay Type 50/66 payload** — ISO length is the even `payloadLength`. Copied bytes come from `payloadLengthValid` and are zero-padded to that length. Exclusive Channel B (`channelMask == 2`) uses `headerCrc2`; every other mask uses `headerCrc1`. Slot and cycle are masked to 11 and 6 bits.
- **BLF object timestamps on export** — nanoseconds (flags word 2) relative to the millisecond `start_date` anchor. A timestamp before that anchor clamps to 0.

### Breaking

- **`FrameInterfacePropertyKeys` left Core.** Use the strings: `blf.channel`, `blf.hw_channel`, `blf.object_type`, `blf.bus_type`, `if.fcs_length`, `if.speed`, `if.snap_length`, `if.filter`, `if.os`, `if.raw_link_type`, `capture.hardware`, `capture.os`, `capture.application`. The C# holders are internal to Sources and Exporters.
- **`blf.channel` 0 is stored as 0.** Omit the property for channel 1. 0.12.0 rewrote 0 to 1.
- **Classic CAN frames are 8 bytes plus the captured data length.** DLC 8 stays 16 bytes. RTR stays 8 bytes and keeps the DLC in byte 4. A fixed 16-byte buffer is gone.
- **LIN byte 5 is the 6-bit id.** Parity bits are not added. Type 57 checksum model 0 or 1 becomes classic or enhanced in byte 4 bits 1-0.
- **BLF timestamp flags must be exactly 1 (10 µs) or 2 (nanoseconds).** Any other word, including a value whose low nibble is 1 or 2, yields timestamp 0.
- **FlexRay channel mapping.** Type 29/41: channel 0 is A, any other value is B. Type 50/66: `channelMask` 1 is A; 0, exclusive B, and A+B are B.
- **`LinLayer` pads the DLT_LIN payload.** Data length 0..4 stores 4 payload bytes; 5..8 stores 8. A 2-byte payload is 12 bytes. The length nibble stays the real data length. Pad bytes are not checksummed.
- **Unsupported `if_tsresol` skips the interface.** A decimal exponent above 19 no longer falls back to nanoseconds. A binary exponent above 63 no longer wraps the clock.

### Fixed

- PCAPNG readers round a leading `block_total_length` up to a multiple of 4 before reading the block. The trailer must equal that rounded length, or round up to it. File and stream readers share one maximum block size (134348832 bytes).
- PCAPNG `if_tsoffset` is applied as seconds after tick conversion. Tick counts above `long.MaxValue` saturate instead of becoming negative.
- PCAPNG export throws `ArgumentOutOfRangeException` when the padded block length does not fit in 32 bits, before any block byte is written.
- `PcapPadding.OptionSize(0)` is 4. An empty SHB hardware string writes a valid section header.
- Disposing a memory-mapped capture throws `AggregateException` when a native view cannot be released. A second dispose does not throw.
- The PCAPNG frame index grows by 4096-entry chunks and does not copy earlier entries.

---

## [0.12.0] — Vector-compatible BLF objects

Delta since 0.11.0. Version is `0.12.0` in `Directory.Build.props`.

This release reads and writes Vector BLF objects using Vector object layouts. Vector captures reconstruct as SocketCAN, DLT_LIN, FlexRay, and Ethernet frames, and `BlfExporter` files those tools can open. CAN XL Type 139 is in the frame-producing set. Inner objects that straddle decompressed containers are stitched.

### Added

- **BLF CAN XL Type 139** — `BlfSource` / `BlfStreamSource` reconstruct SocketCAN XL from Vector `CAN_XL_CHANNEL_FRAME` objects. `BlfExporter` writes the same object from SocketCAN XL (`XLF` on byte 4). Type 140 (XL error) is still skipped.
- **BLF cross-container stitch** — a leftover inner `LOBJ` at a decompressed container tail is prepended to the next blob, capped by `BlfSourceOptions.MaxUncompressedContainerSize` (or `BlfStreamSource.MaxUncompressedContainerSize` before `Start()`). `FrameById` stitches later containers when `object_length` overruns the first blob. Nested containers stay rejected.
- **`/requirements` and `/illustrate`** — freeze `REQ{n}` user-view shall-statements before `/plan` when none exist, and ship a zippable HTML illustration of one concept without product code.

### Changed

- **Breaking (pre-1.0): classic CAN BLF Type 1/86 layout matches Vector `CAN_MESSAGE`.** `flags` is at offset 2, `dlc` at offset 3, extended IDs use CAN ID bit 31, RTR is flags `0x80`. Files written by earlier NetworkInspector builds used a private `dlc@2 flags@3` layout and are not compatible.
- **Breaking (pre-1.0): CAN FD, Ethernet, FlexRay, and LIN BLF payloads match Vector object layouts.** Type 100 is 20 bytes; FD export is Type 101 (40-byte header). Type 71 EtherType/VLAN fields are little-endian in the BLF struct; VLAN detection also accepts QinQ `0x9100` and `0x88A8`. Type 120 is 32 bytes (`frame_length` at 22). Type 102 RX-error is 20 bytes (channel at 2). FlexRay Type 29 is 12 bytes; Type 41 ISO flags come from `frameState`; Type 50/66 flags match Vector `FRAME_FLAG_*` and Type 66 payload starts at 84. LIN frames are DLT_LIN (8-byte header, data padded to 4 or 8). Earlier NetworkInspector-private layouts for those objects are not compatible.
- **BLF `object_length` is unpadded.** Readers advance `max(max(16, object_length), header_size)` and scan one byte at a time for the next `LOBJ`, so packed writers that omit 0–3 alignment zeros still parse.
- **`BlfExporter` stores channel 0 as 1** — Vector tools often reject channel 0. A non-zero `FrameInterfacePropertyKeys.BlfChannel` value is preserved.
- **ASC LIN uses the same DLT_LIN layout** as `BlfSource` (PID at byte 5, DLC in the high nibble of byte 4). The compact pid-plus-length header is gone.

### Fixed

- Classic CAN extended and RTR frames from Vector BLF were mis-parsed because DLC and flags were swapped and EFF lived in the flags byte.
- CAN FD Type 100/101, Ethernet Type 71/120/102, FlexRay Type 29/41/50/66, and LIN objects from Vector BLF were reconstructed with the wrong header sizes, endianness, or compact LIN bytes.

---

## [0.11.0] — Automotive dispatch, packet safety, and settings limits

Delta since 0.10.0. Version is `0.11.0` in `Directory.Build.props`.

This release corrects CAN, LIN, and FlexRay sub-protocol dispatch so Signal Message and other bindings sit beside the container on one identifier table and only for real application payloads. Packet parse, recycle, skip-tree reads, and concurrent field walks fail closed instead of throwing or tearing. Settings and referenced-config JSON honor configurable size and depth caps at construction. ZeroAlloc is 0.6.0.

### Added

- **`Field.TryGetValue` / `Field.TryGetCustomText`** — return `false` for skip-tree children outside the synthetic root and an in-flight skip-tree populator. Skip packets should read only the root; use these instead of `Value` / `CustomText` when a miss is expected.
- **`MutField.TryGetEffectLayerKey` / `MutField.BindParseBuffer`** — protocol parse entry points for effect-layer keys and nested-parse buffer binding. A miss returns `false` or `ReadOnlyMemory<byte>.Empty`; it does not throw.
- **`SettingsManager.MaxConfigFileBytes`** (default 1 GiB = 1073741824) and **`SettingsManager.MaxJsonDepth`** (default 1024) — construction-only caps for persisted settings JSON, the owned-group manifest, and `TryLoadReferencedJsonConfig` (Signal Message, PDU Transport, CAN, SOME/IP, …). Values `<= 0` disable that check.
- **`SettingsManager` / `SettingsManagerFactory.Create`** overloads that take `maxConfigFileBytes` and `maxJsonDepth`.
- CLI `convert` / `export` **`--settings-max-config-file-bytes <bytes>`** — sets `SettingsManager.MaxConfigFileBytes` at construction, before any settings or referenced-config load. Omit for 1 GiB. Values `<= 0` disable the file-size check.
- CLI `convert` / `export` **`--settings-max-json-depth <n>`** — sets `SettingsManager.MaxJsonDepth` at construction. Omit for 1024. Values `<= 0` disable the depth check.
- **`RecycleError.InvalidFieldTree`**, **`CacheStackMismatch`**, **`ParseIdGap`** — `TryParseFrame(recycle)` leaves the recycle packet unchanged and returns the error instead of mutating state.
- **`Stack.WouldJump(PacketId)`** — read-only first-parse gap check used before recycle.

### Changed

- **Breaking (pre-1.0): `Packet.GetEffectLayerKey` and `Packet.BindParseBuffer` are internal.** From `IProtocol.Parse`, call `MutField.TryGetEffectLayerKey` and `MutField.BindParseBuffer`. IPv4, IPv6, TCP, UDP, and SOME/IP return a parse error when the parse slice is not a buffer of the packet.
- **Breaking (pre-1.0): CAN dispatch uses exclusive tables.** Standard 11-bit classic and CAN FD frames (EFF clear) dispatch on `can.id`. Extended 29-bit frames (EFF set) and CAN XL acceptance fields dispatch on `can.extended_id`. CAN XL priority is not a `can.id` key. RTR and error CAN frames do not dispatch. Sub-protocols are siblings of the CAN container (`parentField`), not children of the CAN field.
- **Breaking (pre-1.0): LIN dispatch keys the 6-bit frame ID**, not the protected ID / PID byte. Event frames (`msgType = 3`) and captures with a non-zero error-flags byte do not dispatch. Sub-protocols are siblings of the LIN container.
- **Breaking (pre-1.0): FlexRay null frames (`NFI = 0`) do not dispatch** even when a payload length is declared. Sub-protocols are siblings of the FlexRay container.
- **Breaking (pre-1.0): settings and referenced-config file size cap default is 1 GiB** (`SettingsManager.DefaultMaxConfigFileBytes` = 1073741824), up from 1 MiB. The cap is `SettingsManager.MaxConfigFileBytes` (`long`, construction-only / `SettingsManagerFactory.Create`). Values `<= 0` disable the check.
- **Breaking (pre-1.0): settings and referenced-config JSON depth cap is `SettingsManager.MaxJsonDepth`** (default 1024, was a hardcoded 64). Construction-only. Values `<= 0` disable the check; System.Text.Json still applies a library ceiling.
- **Breaking (pre-1.0): `JsonConfigStream.TryLoad` is not size- or depth-capped.** Callers must bound untrusted streams. File loads still honor `MaxConfigFileBytes` and `MaxJsonDepth`.
- Concurrent field-tree walks wait on **`Packet.WaitUntilFieldPublished`** so a walker that observed a child/sibling pointer cannot read a default `FieldBody` before the matching publish.
- Lazy populator storage is a single published **`LazyPopulatorTable`** so concurrent nested `AppendLazy` cannot tear array/offset on growth.
- Recycle takes an exclusive **`_RecycleGate`** vs concurrent `MaterializeAll`; holding a `Field` across `TryParseFrame(recycle)` is unsupported.
- Additional packet buffers freeze at **`Seal`**; at most 255 additional buffers (effect-layer key packs the index in 8 bits).
- ZeroAlloc **0.6.0**.

### Fixed

- False automotive dispatch from overlapping CAN tables, CAN XL priority on `can.id`, LIN protected-ID keys, FlexRay null-frame payloads, and LIN Event / error-flag captures.
- Torn packet field walks during concurrent parse when a tree link was published before its `FieldBody` slot.
- Effect-layer key lookup throwing on a foreign slice; protocols now fail closed with a parse error.

---

## [0.10.0] — Skip-field-tree parse, frame cache, and grow-only ValueCache

Delta since 0.9.0. Version is `0.10.0` in `Directory.Build.props`.

This release separates decode work from what you keep in memory. Session ingest always uses `FieldTreeMode.Skip` and recycles the throwaway packet; `TryGetPacket` and export Build-reparse from the captured frame. ValueCache is grow-only First/All series with snapshot handles for concurrent reads. `ParseFrameRecorded`, the packet store, and packed frame copy are removed.

### Added

- **`FieldTreeMode`** — last-optional parse argument on `ParseFrame` / `ParseFrameIndexed` and recycle `Try*` variants. `Skip` keeps protocol decode, packet-index presence, and ValueCache record without retaining a FieldBody tree (`HasFieldTree == false`). Default remains `Build`. `ParseFrame` / `TryParseFrame` also take optional `ValueCache? cache` and `bool recordOnReplay` so a replay id can still record (Session PullFill).
- **`ValueCacheSeries.Handle`** — count snapshot with index access, `TryFindPacketId(s)` / `TryFindTimestamp(s)`, row `foreach` / `EnumerateFrom`, SoA `EnumerateChunks` / `EnumerateChunksFrom` (SIMD spans; first chunk sliced from the watermark), and `TryGet*Chunk` clipped to the snapshot.
- **`ReadOnlyValueCache`** — read-only façade (replaces `ValueCacheReaderView`). `ValueCache` and the struct implement `IReadOnlyValueCache`; series implement `IReadOnlyValueCacheSeries` / `IReadOnlyValueCacheSeries<T>`. Session listeners get `ReadOnlyValueCache` and `ReadOnlyValueCacheSeries<T>` (structs; do not box onto the interface). Class `GetSeries<T>` still returns `ValueCacheSeries<T>`; interface/struct getters return `ReadOnlyValueCacheSeries<T>`. `IReadOnlyValueCache.AllSeries` is the read-only list (`ValueCache.Series` stays the writer list). Untyped series interface uses `GetHandle()` so the typed `Handle` property can stay `ValueCacheSeriesHandle<T>`.
- **`ValueCacheBuildOptions` is a `readonly struct`.** `ChunkShift` is validated on assign (4…20). `ValueCache` still takes `ValueCacheBuildOptions?`; omit or pass `null` so defaults (`ChunkShift` 12) apply. Do not pass `default(ValueCacheBuildOptions)`.
- **ZeroAlloc generic `AppendWithCustomText` / `AppendLazyWithCustomText`** — format fragments are not wrapped in `ZA.Lazy` first; skip parse omits display-text allocation unless the tree or cache needs it. Leftover last-arg `ZA.Lazy` is NIGEN015.
- **Filter `NoFieldTree`** — `TryIsMatch` on a skip packet returns `FilterErrorKind.NoFieldTree` (not a negative match), except `AlwaysMatch`.
- Profiling skip counterparts: `parse-random-frames-recycled-skip`, `parse-random-frames-recycled-recorded-skip`, `value-cache-build-all-fields-skip`.
- **`FrameSourceAddOptions`** — per-`TryAddFrameSource` `CacheRandomAccess`. Stream sources are always wrapped. RA wrap is opt-in and holds the inner `Frame` (payload bytes are not copied).
- Profiling `IProfilingScenario.PrepareIteration` is an optional `Action?` (null = no prepare, tight `Run` loop). Throughput and alloc/packet use net `Run` time. Scenario: `cached-frames-hold-reference`.
- **`VALUECACHE_GUIDE.md`** — fill paths, snapshot handles, and Session integration.

### Changed

- Session never retains parsed packets. Ingest always `FieldTreeMode.Skip` and recycles the throwaway packet. `TryGetPacket` / bulk reads always Build-reparse from the cached or RA frame. Caller recycle is per slot.
- On-demand Session caches (`TryAddValueCache`) skip-tee from `TryGetFrame` with `recordOnReplay: true`. They do not walk FieldBodies and do not call `ValueCache.RecordPacket`.
- `ISessionReader.TryGetFrame` returns the captured frame without parsing.
- `SessionOptions.RedissectOnly` is `IndexPackets = false` only. Profiling session scenarios construct the session in `PrepareIteration`, not in `Run`.
- `CachedFrameSource` always holds the inner `Frame`. It does not copy payload bytes. `EstimatedPayloadBytes` / `EstimatedIndexBytes` report held `Frame.Data` lengths and index backing.
- UDP/TCP/ICMPv6 checksum status on Skip uses the IP address caches; ICMPv6 no longer maps a cache miss to `[Bad]`.
- Packet exporters reject skip packets (`HasFieldTree` false) via `ItemSkipped` instead of writing an empty tree.
- Restart re-parses with the original `PacketId` and fails closed (`SessionErrorCode.FrameUnavailable`) when `FrameById` cannot load a mapped frame. PullFill does the same on a `TryGetFrame` miss. A failed Restart leaves pull queries disabled so callers cannot read a torn `PacketCount` / mapping.
- **Breaking (pre-1.0): ValueCache is grow-only First/All series.** `ValueCaptureMode.LastOccurrence`, `ValueCache.BeginPacket` / `EndPacket`, `ValueCacheLimits`, `IsCapacityReached`, `ByteSize`, and specialized series types (`ValueCacheStringSeries`, `ValueCacheBytesSeries`, `ValueCacheIPv6Series`, `ValueCacheUuidSeries`) are gone. Callers use `GetSeries<T>` including `string`, `byte[]`, `IPv6Address`, and `Uuid`. `ValueCacheBuildOptions.ChunkShift` (default 12, allowed 4…20) is passed into every series store. Rows are visible as soon as `Record` appends; concurrent readers may observe a packet that is still being parsed. The cache has no silent capacity drop — it grows with traffic.
- **Breaking (pre-1.0): `ChunkedGrowOnlyStore<T>` mutation is `Append` / `AppendRange` only.** Published slots `0 .. Count-1` are immutable. `Set` / `SetRange` and `ChunkedGrowOnlyLongStore` were removed. Session stores PacketId → (FrameId, FrameSourceId) as packed `long` in `ChunkedGrowOnlyStore<long>` (dense sequential appends).

### Removed

- **Breaking:** `StoreParsedPackets`, `SessionOptions.WithoutPacketStore`, `ISession.StoreParsedPackets`, and `PacketStore`. There is no packet store.
- **Breaking:** `CachedFramePayloadMode`, `CachedFrameSource` payload-mode constructors, `PackedFramePayloadStore`, `SessionOptions.CachedFramePayloadMode`, and `FrameSourceAddOptions.PayloadMode`. Packed-arena copy was removed after it measured ~3× slower than hold.
- **Breaking:** `ValueCacheReaderView`, `ParseFrameRecorded`, `TryParseFrameRecorded`, and `teeOnReplay`. Pass `ValueCache?` and `recordOnReplay` into `ParseFrame` / `TryParseFrame` instead.

### Fixed

- `ValueCache.RecordPacket` begins custom-text and custom-representation series before staging so pull ingest publishes those rows (same begin/commit contract as payload and parse-time tee).
- Session ingest ValueCache profiling reused a `Stack` across iterations so packet ids 0…N were replays and the ingest tee skipped (`udp.srcport` stayed at 0 rows until timeout). Each iteration now builds a fresh `Stack`.
- Session `_TryReparseFrame` no longer swallows parse exceptions (truncated frames already become error packets; OOM and invariant failures propagate).
- `CachedFrameSource` concurrent `FrameById` tests compare against a payload clone taken at `NextFrame`, so a torn publish cannot pass by aliasing the writer’s `Frame.Data`.

---

## [0.9.0] — ValueCaches: parse-time tee and session on-demand fill

Delta since `3c7e46c` (0.8.0). Version is `0.9.0` in `Directory.Build.props`.

This release introduces RAM columnar **ValueCaches**. There are two fill paths: a parse-time tee on the first walk of each packet (`Packet.ParseFrameRecorded`, session ingest via `SessionOptions.ValueCache`), and on-demand fill from sealed packets (`ValueCache.RecordPacket`, session runtime caches via `ISession.TryAddValueCache`). Replays never tee. Filters still read the field tree, not the cache.

### Added

- **`ValueCache`** (`NetworkInspector.Core.ValueCaches`) — single-writer RAM columnar series for selected fields (or all fields). Fill with `RecordPacket` (sealed packet, lookup order) or parse-time tee (`ParseFrameRecorded`). Capture modes first/last/all occurrence; optional custom-text and custom-representation series; sticky packet-id and timestamp monotonic flags; optional row/byte limits (`ValueCacheLimits`). `ValueCacheReaderView` is the zero-allocation read-only façade. `Abandon()` / `IsAbandoned` are public so Session can evict a writer without Core internals.
- **`Packet.ParseFrameRecorded` / `TryParseFrameRecorded`** — first-parse tee into a `ValueCache`, including indexed overloads, first-protocol override, and recycle variants. Replays of an already first-parsed id do not write the cache. Unrecorded `ParseFrame` keeps a predicted null check on the active cache so the tee cannot inflate `AppendChild`.
- **Session ingest value cache** — `SessionOptions.ValueCache` / `ValueCacheListener`. The parse thread tees into the ingest writer (`NotifyOnly` when a listener is set). Exposed as `ISessionReader.IngestValueCache`. Ingest without a listener is listed by `GetValueCaches()` with UiName `ingest`.
- **Session on-demand value caches** — `ISession.TryAddValueCache(IValueCacheListener, ValueCacheRequest, …)` always constructs a new cache and fills it on a dedicated slot thread (`PullFill`: `TryGetPacket` then `RecordPacket`). Runtime caches never tee on the parse thread. `IValueCacheListener.OnNewRows` uses the same coalesced packet-id window as `OnNewPackets`; series indexes come from `ValueCacheSeries.Count`. `ValueCacheId`, `ValueCacheInfo`, `ValueCacheRequest` / `ValueCacheFieldRequest`. Restart abandons writers and rebinds surviving slots.
- **`ChunkedGrowOnlyStore.TryGetPublishedChunk`** — readers slice a published inner chunk by a caller-loaded committed count (used by value-cache series).
- Profiling scenarios: `session-value-cache-ingest-all-fields`, `session-value-cache-ingest-udp-srcport`, `session-value-cache-ondemand-all-fields`, `session-value-cache-ondemand-udp-srcport`, `value-cache-build-all-fields`, `value-cache-read-udp-srcport`, `packet-reparse-read-udp-srcport`, `parse-random-frames-recycled-recorded`. Session value-cache scenarios construct a new `Stack` and `Session` per `Run` so packet ids are first-parses. `session-listener` pulls without `MaterializeAll`; `session-listener-materialized` keeps the full-tree walk. `random-source-parse` / `random-source-parse-materialized` are the no-session counterparts.

### Changed

- **Breaking (pre-1.0): `Packet.TryGetFieldAt` is internal.** Storage indexes stay packet-owned. External navigation uses `RootField()`, Field parent/child/sibling APIs, `IterFieldsDfs` / `IterFieldsFlat`, or `TryGetFieldValue` / `TryGetNextField`.
- Session value-cache bind checks field and group names with `NameValidation.IsValidName` (same rule as stack registration). Invalid identifiers throw `SessionException(ValueCacheInvalidFieldName)` at construction, `TryAddValueCache`, and Restart; well-formed names missing from the stack still throw `ValueCacheUnknownField`. `ValueCacheListener` without `ValueCache` is `ValueCacheListenerWithoutRequest`.
- `MutField.SetCustomText` / `ClearCustomText` / `AppendCustomText` go through `Packet` so parse-time tee can record custom-text series.
- `ValueCache` parse tee uses a compact field-id array (linear scan) when at most 16 fields are recorded, otherwise a dense probe plus a bitset miss. There are no `FrozenDictionary` lookups. Tee hits run through `_TeeHitCold` (`NoInlining`); compact scans unroll one- and two-field lists. `BeginPacket` / `EndPacket` commit only series touched in the active packet (epoch tracking). `Tee` / `TeeCustomText` stay `NoInlining` so they cannot be pulled into `AppendChild`.
- Listener and value-cache slots skip redundant wake signals when the target flag is already set.
- Session first-parse uses a Monitor (`_ParseMutex`) instead of `SpinLock`. One frame parse is long enough that waiting source threads kernel-wait. Re-parse of announced ids stays lock-free. Dense packet ids and protocol-instance mutation remain serialized.
- Value-cache fill no longer rolls back on a protocol exception. Fields already teed (for example Ethernet before a UDP throw) stay in the cache; `packet.error` is still recorded. `RollbackCurrentPacket` / `RollbackActiveValueCache` are gone. `PacketIndex.RollbackCurrentPacket` is unchanged.
- `ValueCache` types live in namespace and folder `NetworkInspector.Core.ValueCaches`. Session listener/slot types live in `NetworkInspector.Sessions.ValueCaches`. Request types (`ValueCacheRequest`, `ValueCacheFieldRequest`) live in `NetworkInspector.Sessions`. Sessions talks to the cache only through public APIs (`RecordPacket`, `Abandon`, `ValueCacheReaderView`); Core does not grant `InternalsVisibleTo` to Sessions.
- `FILTER_GUIDE.md`: filters do not read `ValueCache`; values still come from the field tree. Core and Sessions package READMEs document parse-time tee and session ingest / on-demand caches.
- Version bumped to `0.9.0`.

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
