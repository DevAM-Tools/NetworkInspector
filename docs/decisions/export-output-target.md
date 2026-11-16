# Decision — Exports write through named streams of a host target

**Status:** accepted

**Context:** The owner's application embeds the exporters and decides where every export lands, in the end a network share. It
hands an exporter only streams to write into; the exporter must not know a path or open a file itself, and the streams may be
write-only and non-seekable. Most single-file exporters already took a caller stream, but the Parquet exporter wrote a directory
of many files by path, the plugin contract handed every exporter a `TargetPath`, and DuckDB hands a path to its native library.
A Parquet dataset cannot be one stream: it is a set of files. Evidence that Parquet.Net needs no seekable stream:
`ParquetForwardOnlyStreamTests` in `NetworkInspector.Exporters.Tests` writes two row groups to a stream that refuses seek, read,
length, and position and reads the bytes back.

**Decision:** The host hands an exporter an [`IExportTarget`](../../NetworkInspector.Core/Interfaces/IExportTarget.cs) that opens
write-only streams by [`ExportEntryName`](../../NetworkInspector.Core/Interfaces/ExportEntryName.cs): a single-file format opens
the one entry `Main`, Parquet opens one entry per table file (`packets.parquet`, `fields/field_{id}.parquet`, …). The name grammar
is closed (lower case, no `..`, no backslash, no device names, at most 255 characters per segment, a fact of the file systems), so
every target can rely on it. The streams need not seek; a refused entry is an export error, never an exception. The contract is
not a sandbox: plugins stay trusted in-process code and can still call file APIs. DuckDB is the one file format: its library
opens the file itself, so its registration receives a path that the host prepared. The plugin contract says which of the two a
registration wants (`ForStreams` or `ForFile`); both kinds are exclusive. The builder shortcuts `ToFile`, `ToStream`,
`ToStdout`, and `ToDirectory` stay and build a target.

**Rejected:** Spool to a local folder and copy — temporary files, unwanted · One TAR or ZIP stream for the dataset — another
layout, not queryable on the share · Buffering in memory — memory grows with the capture · DuckDB in memory — no way to emit the
file bytes · ParquetSharp — a native dependency, not needed because Parquet.Net writes forward · Two nullable creation fields on
one registration record — a registration could hold both or neither · Plugins out of process without file rights — not wanted,
plugins are trusted code.

**Consequences:** Every exporter has one output path over a target, so an in-memory target lets tests read output without
temporary files. A host with a limit of entries or open streams refuses an open with its reason; Parquet keeps every entry open
until the export finishes, so such a limit equals a limit of entries. The plugin contract of 0.21 breaks: a registration passes
`ExporterCreation` instead of a delegate, and plugins must be rebuilt. A plugin that writes files itself is not stopped.

**Enforced in:** `ExportOutput` in [ExportOutput.cs](../../NetworkInspector.Exporters/ExportOutput.cs), `ParquetBatchSink` in
[ParquetBatchSink.cs](../../NetworkInspector.Exporters/Parquet/ParquetBatchSink.cs), `ExporterCreation` in
[ExporterCreation.cs](../../NetworkInspector.Plugins/Exporters/ExporterCreation.cs).
