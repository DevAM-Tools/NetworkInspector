// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.DuckDb;

/// <summary>
/// DuckDB packet exporter. Writes parsed packets to a single <c>.duckdb</c> database file using
/// bulk-load <see cref="DuckDBAppender"/>s — see <see cref="DuckDbBulkWriter"/> for the full table
/// layout.
/// <para>
/// Shares the <see cref="ColumnarPacketBatch"/> accumulator with the PBF columnar format and the
/// Parquet exporter, so <see cref="ExportContent"/> is configured identically across all
/// three columnar formats. <c>packet_id</c> columns use Core <see cref="PacketId"/> width
/// (<see cref="int"/> / DuckDB <c>INTEGER</c>).
/// </para>
/// <para>
/// <b>Export shapes:</b> <see cref="Builder.WithContent"/> chooses the shape. <see cref="ExportShape.Hierarchy"/> (the default,
/// <see cref="DefaultContent"/>) writes every field with its place in the field tree and needs packets with a field tree; a
/// packet parsed without one is skipped and counted (<see cref="ExportErrorKind.SerializationError"/>).
/// <see cref="ExportShape.SelectedFields"/> and <see cref="ExportShape.AllFields"/> write value rows numbered by occurrence and
/// no topology; they are fed field by field while a packet is parsed: <see cref="ParseDemand"/> states the exporter's observer, a
/// <see cref="ParsePlan"/> parses with it, and <see cref="OnPacket"/> commits the staged fields of that very packet. A packet the
/// filter rejected is simply not committed. Tree packets are accepted for every shape and give the same output.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/> must
/// be called sequentially from a single thread. Statistics are valid to read after
/// <see cref="OnFinish"/> returns. In <see cref="ExportWriteMode.Background"/> (the default) one named writer thread
/// ("DuckDB export writer", see <c>BatchWriterPipeline</c>) writes the full batches; it never touches the counters or raises
/// <see cref="ItemSkipped"/>, which stay on the caller's thread.
/// </para>
/// <para>
/// <b>Single-use:</b> Once <see cref="OnFinish"/> (or <see cref="Dispose"/>) is called, this
/// instance is finalized and cannot be reused. Subsequent calls to <see cref="OnPacket"/> after
/// <see cref="OnFinish"/> are silently ignored.
/// </para>
/// <para>
/// <b>Empty export:</b> If no packets are ever added, no database file is created — matching the
/// lazy-initialisation behaviour of the other exporters (see <c>EXPORTER_GUIDE.md</c> §7.2/§7.5).
/// </para>
/// <para>
/// <b>Memory model:</b> packets are accumulated column-wise in a <see cref="ColumnarPacketBatch"/> and handed to the
/// writer whenever the packet count or the estimated batch size reaches its limit. In background mode there are two batches:
/// while the writer thread writes one in a single DuckDB transaction, the caller's thread fills the other, so parsing and
/// writing overlap. Memory stays bounded by two batches regardless of capture size; when the caller is faster than the
/// writer, it waits for the batch to come back. <see cref="ExportWriteMode.Synchronous"/> keeps one batch and writes it inside
/// the <see cref="OnPacket"/> call that completed it.
/// </para>
/// <para>
/// <b>Error timing:</b> in background mode a batch is written after <see cref="OnPacket"/> returned for its last packet, so a
/// failing batch is reported by the next <see cref="OnPacket"/> call that finds it (at the latest by <see cref="OnFinish"/>), not
/// by the call that completed the batch. In synchronous mode it is reported by that very call. Its packets leave
/// <see cref="WrittenCount"/> and count as skipped when it is reported; until then <see cref="WrittenCount"/> may include
/// packets that are still being written or failed.
/// </para>
/// <para>
/// <b>Existing output:</b> the first packet deletes an existing database file (and its <c>.wal</c>) at the target
/// path, so a re-export replaces the file instead of appending to it.
/// </para>
/// <para>
/// <b>Storage version:</b> <see cref="Builder.WithStorageVersion"/> chooses the DuckDB storage format of the file, which decides the
/// oldest DuckDB release that can open it; the default is <see cref="DefaultStorageVersion"/>. The export runs no compaction or
/// copy pass: it only appends and ends with the checkpoint.
/// </para>
/// </summary>
public sealed class DuckDbExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IBatchWriter<ColumnarPacketBatch>, IDisposable
{
    #region Configuration and state

    /// <summary>The full path of the database file; created on the first packet.</summary>
    private readonly string _FilePath;

    /// <summary>Packets per batch before the batch is handed to the writer (a flush limit, see <see cref="BatchLimits"/>).</summary>
    private readonly int _MaxPacketsPerBlock;

    /// <summary>Estimated bytes per batch before the batch is handed to the writer (a flush limit, see <see cref="BatchLimits"/>).</summary>
    private readonly long _MaxBlockSize;

    /// <summary>What the database contains: the shape, the selected fields, and the optional columns.</summary>
    private readonly ExportContent _Content;

    // Null for the hierarchy shape, which is written from the field tree (AddPacket). For the flat shapes the observer of this
    // collector stages the fields of a packet straight into the current batch while the packet is parsed; OnPacket commits them.

    /// <summary>The staging collector of the flat shapes; <see langword="null"/> for the hierarchy shape.</summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>Declared by the caller: packets arrive in non-decreasing timestamp order. Passed on to the batches.</summary>
    private readonly bool _IsTimestampSorted;

    /// <summary>DuckDB <c>memory_limit</c> in bytes; <c>0</c> = DuckDB's default (no <c>SET</c> issued).</summary>
    private readonly long _MemoryLimitBytes;

    /// <summary>DuckDB <c>threads</c>; <c>0</c> = DuckDB's default (no <c>SET</c> issued).</summary>
    private readonly int _Threads;

    /// <summary>
    /// The value of DuckDB's <c>storage_compatibility_version</c> setting for the file (<c>v1.0.0</c> to <c>v1.5.0</c>), already
    /// mapped and validated by <see cref="Builder.WithStorageVersion"/>.
    /// </summary>
    private readonly string _StorageVersionSetting;

    /// <summary>The writer that owns the database connection; <see langword="null"/> until <see cref="_Start"/> succeeded and after the finish.</summary>
    private DuckDbBulkWriter? _Writer;

    /// <summary>Whether full batches are written on a writer thread or inside the call that completed them.</summary>
    private readonly ExportWriteMode _WriteMode;

    // The writer pipeline owns both batches, the writer thread (or writes inline in synchronous mode), and the queue of lost
    // batches; see BatchWriterPipeline for the primitive choice, the memory bound of two batches, and the error timing. It is
    // created in the constructor without I/O, batches, or a thread (all lazy), so an export that never receives a packet leaves
    // nothing behind. The exporter is the pipeline's writer (explicit IBatchWriter implementation below) and forwards to the
    // bulk writer that _Start opens on the first packet; Write can only run after a Submit, which needs a started export.

    /// <summary>The batches, the writer thread, and the lost-batch queue of this export.</summary>
    private readonly BatchWriterPipeline<ColumnarPacketBatch> _Pipeline;

    // Intake and accounting (stop checks, the one-stack rule, the lazy start, the counters, the batch hand-off, the lost-batch
    // accounting, and the error channel) are the same for every columnar exporter and live in the shared core; this exporter
    // keeps its builder, the opening of the database, the batch write, and the finish sequence. The statistics properties below
    // forward to the core.

    /// <summary>Intake and accounting of this export; shared with the other columnar exporters.</summary>
    private readonly ColumnarBatchExportCore _Core;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="filePath">The full path of the database file.</param>
    /// <param name="uiName">The display name.</param>
    /// <param name="description">The description; <see langword="null"/> for none.</param>
    /// <param name="maxPacketsPerBlock">Packets per batch.</param>
    /// <param name="maxBlockSize">Estimated bytes per batch.</param>
    /// <param name="targetPacketCount">Stop after this many packets; 0 means unlimited.</param>
    /// <param name="content">What the database contains.</param>
    /// <param name="isTimestampSorted">Whether the caller declared non-decreasing timestamps.</param>
    /// <param name="memoryLimitBytes">DuckDB <c>memory_limit</c> in bytes; 0 keeps DuckDB's default.</param>
    /// <param name="threads">DuckDB <c>threads</c>; 0 keeps DuckDB's default.</param>
    /// <param name="storageVersionSetting">The <c>storage_compatibility_version</c> text; validated by the builder.</param>
    /// <param name="writeMode">Whether full batches are written on a writer thread or inline.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    private DuckDbExporter(
        string filePath,
        string uiName,
        string? description,
        int maxPacketsPerBlock,
        long maxBlockSize,
        int targetPacketCount,
        ExportContent content,
        bool isTimestampSorted,
        long memoryLimitBytes,
        int threads,
        string storageVersionSetting,
        ExportWriteMode writeMode,
        CancellationToken cancellationToken)
    {
        _MemoryLimitBytes = memoryLimitBytes;
        _Threads = threads;
        _StorageVersionSetting = storageVersionSetting;
        _FilePath = filePath;
        UiName = uiName;
        Description = description;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        _Content = content;
        _IsTimestampSorted = isTimestampSorted;
        _WriteMode = writeMode;
        _Pipeline = new BatchWriterPipeline<ColumnarPacketBatch>(
            "DuckDB",
            writeMode,
            () => new ColumnarPacketBatch(content, maxPacketsPerBlock, maxBlockSize, isTimestampSorted),
            this);

        // The observer stages the first packet before the first OnPacket, so for the flat shapes the first batch is needed
        // now (the pipeline creates it on the first access to Current; no database file and no thread exists yet).
        if (content.Shape != ExportShape.Hierarchy)
        {
            _Collector = new ExportFieldCollector(content, _Pipeline.Current);
        }

        // The delegates are created once here, so the per-packet path allocates none.
        _Core = new ColumnarBatchExportCore(
            "DuckDB",
            content,
            targetPacketCount,
            _Pipeline,
            _Collector,
            _RaiseItemSkipped,
            _Start,
            cancellationToken);
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    /// <returns>A builder with the defaults; <see cref="Builder.ToFile"/> is required before <see cref="Builder.Build"/>.</returns>
    public static Builder CreateBuilder() => new();

    /// <summary>
    /// The content written when <see cref="Builder.WithContent"/> is not called: every field with hierarchy, the packet info, and
    /// the custom texts (the hierarchy shape); the frame bytes (<see cref="ExportContent.FrameBytes"/>) and the additional buffers
    /// (<see cref="ExportContent.Buffers"/>) are off.
    /// </summary>
    public static ExportContent DefaultContent { get; } = new();

    /// <summary>
    /// The storage version written when <see cref="Builder.WithStorageVersion"/> is not called: <see cref="DuckDbStorageVersion.V1Dot5"/>
    /// (DuckDB 1.5 or later opens the file). It is set explicitly on every connection, so the file format never follows a library
    /// default that changes with a DuckDB.NET upgrade. Posture: open by default for the newest format (no size claim: the effect
    /// depends on the data); a reader with an older DuckDB needs an older version chosen. Sync: README § Storage Version and the plugin option default.
    /// </summary>
    public const DuckDbStorageVersion DefaultStorageVersion = DuckDbStorageVersion.V1Dot5;

    /// <inheritdoc/>
    /// <remarks>
    /// The hierarchy shape demands the field tree; the flat shapes demand a tree-free parse with this exporter's observer, so a
    /// pipeline that honors the demand builds no field tree for this exporter. A caller that ignores the demand and passes tree
    /// packets still gets the same output (the exporter walks the tree through the same rules).
    /// </remarks>
    public ParseDemand ParseDemand
    {
        get
        {
            if (_Collector is null)
            {
                return ParseDemand.FieldTree;
            }

            return _Collector.CreateDemand();
        }
    }

    /// <inheritdoc/>
    public string UiName
    {
        get;
    }

    /// <inheritdoc/>
    public string? Description
    {
        get;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Number of packets accepted so far. A packet counts as soon as it is added to the current batch, which may
    /// still be buffered in memory and not yet written to the database.
    /// </summary>
    public int PacketCount => _Core.PacketCount;

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    public long EstimatedOutputBytes =>
        _Core.FlushedOutputBytes + _Pipeline.CurrentEstimatedSizeBytes;

    /// <inheritdoc/>
    public int SkippedCount => _Core.SkippedCount;

    /// <inheritdoc/>
    public int ErrorCount => _Core.ErrorCount;

    /// <inheritdoc/>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Whether the exporter has stopped due to reaching the target count, error, or cancellation.</summary>
    public bool IsFinished => _Core.IsFinished;

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance
    {
        get => _Core.ErrorTolerance;
        set => _Core.ErrorTolerance = value;
    }

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    /// <summary>Raises <see cref="ItemSkipped"/> with this exporter as the sender; the core calls it for every reported error.</summary>
    /// <param name="error">The error to report.</param>
    private void _RaiseItemSkipped(ExportErrorEventArgs error) => ItemSkipped?.Invoke(this, error);

    #endregion

    #region Packet intake

    /// <inheritdoc/>
    /// <remarks>
    /// The stop checks, the one-stack rule, the lazy start (<see cref="_Start"/>), the add, and the hand-off of a full batch are
    /// the shared intake of <see cref="ColumnarExportCore{TBatch}"/>.
    /// </remarks>
    public bool OnPacket(Packet packet) => _Core.OnPacket(packet);

    /// <summary>
    /// Lazily opens the DuckDB file and creates the base tables. The protocol catalog is taken from the first packet's stack:
    /// all packets of one export come from the same parsing stack. Batches and the writer thread are not created here: the
    /// pipeline creates the first batch on first use and the second batch and the thread on the first background submit, so an
    /// export shorter than one batch never starts a thread.
    /// </summary>
    /// <param name="firstPacket">The first packet of the export.</param>
    /// <returns><see langword="true"/> when the database is open; <see langword="false"/> after the failure was reported.</returns>
    private bool _Start(Packet firstPacket)
    {
        // The start is marked before the work so a failed start is not retried with the next packet; the error flag
        // below then stops the export. The writer constructor deletes any previous database at the path.
        _Core.MarkStarted();
        try
        {
            _Writer = new DuckDbBulkWriter(
                _FilePath,
                _Content,
                ProtocolCatalogEntry.FromStack(firstPacket.Stack),
                _MemoryLimitBytes,
                _Threads,
                _StorageVersionSetting);

            return true;
        }
        catch (Exception ex)
        {
            _Core.ReportStartFailure(ExportErrorKind.IoError, Invariant($"DuckDB output initialization failed: {ex.Message}"));
            return false;
        }
    }

    #endregion

    #region Batch write

    /// <summary>
    /// Writer-thread entry (or caller entry in synchronous mode): forwards to the bulk writer that <see cref="_Start"/> opened.
    /// The writer is non-null here because a batch is only submitted after a started export.
    /// </summary>
    /// <param name="batch">The full batch to write in one DuckDB transaction.</param>
    void IBatchWriter<ColumnarPacketBatch>.Write(ColumnarPacketBatch batch) => _Writer!.WriteBatch(batch);

    #endregion

    #region Finish

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the database. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (!_Core.TryBeginFinish())
        {
            return;
        }

        if (!_Core.Started)
        {
            // No packets were ever added — leave no database file on disk (§7.5). The pipeline holds nothing yet (batches and
            // thread are created lazily), disposing it only marks it completed.
            _Pipeline.Dispose();
            return;
        }

        // Shared finish sequence: final flush and commit, then the pipeline (thread, batches) and the writer are released even
        // when a step fails. The pipeline goes first: the writer must not be disposed while the writer thread may still use it.
        ExporterFinish.Run(
            "DuckDB",
            _FinalizeDatabase,
            _Core.ReportFinishError,
            new ExporterReleaseStep("writer pipeline disposal", _Pipeline.Dispose),
            new ExporterReleaseStep("writer disposal", _DisposeWriter));
    }

    /// <summary>
    /// Writes the last batch, waits until every batch is written, accounts the batches that failed, and completes the writer
    /// (catalog rows, commit, then the explicit checkpoint).
    /// </summary>
    private void _FinalizeDatabase()
    {
        // The last batch may fail on its own; the batches committed before it still need their catalog and the checkpoint, so
        // a failed batch is reported and accounted like a mid-export one and Complete runs regardless.
        if (!_Pipeline.IsCompleted)
        {
            // Fields staged for a packet that was never committed (the filter rejected it) must not reach the last batch.
            if (_Collector is not null)
            {
                _Pipeline.Current.Discard();
            }

            _Pipeline.Complete(firstItemIndex: PacketCount - _Pipeline.CurrentPacketCount);
        }

        // Lost batches first, so the catalog and the counters describe what is really in the database; then the catalog,
        // commit and checkpoint, which run even when the last batch failed.
        _Core.DrainLostBatches(reportAlways: true);
        _Writer?.Complete();
    }

    /// <summary>Disposes the writer once (closes the connection and the database file).</summary>
    private void _DisposeWriter()
    {
        DuckDbBulkWriter? writer = _Writer;
        _Writer = null;
        writer?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Builder

    /// <summary>Fluent builder for constructing a <see cref="DuckDbExporter"/>.</summary>
    /// <remarks>
    /// Output: a later call of <c>ToFile</c> replaces the path of an earlier one, and the last call before <c>Build</c> wins. The
    /// file is created by the first item, not by the builder. Build every further exporter from a new builder: a second
    /// <c>Build</c> would let two exporters write the same database file.
    /// </remarks>
    public sealed class Builder
    {
        /// <summary>The full path of the output file; <see langword="null"/> until <see cref="ToFile"/> was called.</summary>
        private string? _FilePath;

        /// <summary>The display name.</summary>
        private string _UiName = "DuckDB Exporter";

        /// <summary>The description; <see langword="null"/> for none.</summary>
        private string? _Description;

        /// <summary>Cooperative cancellation; <see cref="CancellationToken.None"/> by default.</summary>
        private CancellationToken _CancellationToken;

        // Flush thresholds for the in-memory batch: see BatchLimits (range, threat, posture, who may change them). Defaults
        // 50 000 packets / 16 MiB, shared with the PBF, Parquet, and BLF exporters; whichever limit is hit first triggers the flush.

        /// <summary>Packets per batch before a flush; validated by <see cref="BatchLimits"/>.</summary>
        private int _MaxPacketsPerBlock = BatchLimits.DefaultPacketsPerBatch;

        /// <summary>Estimated bytes per batch before a flush; validated by <see cref="BatchLimits"/>.</summary>
        private long _MaxBlockSize = BatchLimits.DefaultBytesPerBatch;

        /// <summary>Whether full batches are written on a writer thread (default) or inside the completing call.</summary>
        private ExportWriteMode _WriteMode = ExportWriteMode.Background;

        /// <summary>Stop after this many packets; 0 means unlimited.</summary>
        private int _TargetPacketCount;

        /// <summary>What the database contains; <see cref="DefaultContent"/> unless <see cref="WithContent"/> was called.</summary>
        private ExportContent _Content = DefaultContent;

        /// <summary>Declared non-decreasing timestamp order (metadata only).</summary>
        private bool _IsTimestampSorted;

        // DuckDB resource limits; 0 means "not set" (DuckDB's defaults apply, open by default). See
        // DuckDbBulkWriter.BuildSettingsSql for the limit description.

        /// <summary>DuckDB <c>memory_limit</c> in bytes; 0 keeps DuckDB's default.</summary>
        private long _MemoryLimitBytes;

        /// <summary>DuckDB <c>threads</c>; 0 keeps DuckDB's default.</summary>
        private int _Threads;

        /// <summary>
        /// The <c>storage_compatibility_version</c> text of the file; the mapping of <see cref="DefaultStorageVersion"/> until
        /// <see cref="WithStorageVersion"/> replaces it with the mapping of a validated value.
        /// </summary>
        private string _StorageVersionSetting = DuckDbStorageVersions.DefaultSettingValue;

        /// <summary>
        /// Sets the output <c>.duckdb</c> file path. Required — DuckDB output is always a single
        /// database file, never a directory or stream. The file is created lazily on the first
        /// <see cref="OnPacket"/> call; an existing file (and its <c>.wal</c>) at the path is deleted at that point.
        /// </summary>
        /// <param name="path">Database file path; resolved to a full path immediately so later working-directory changes do not move it.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
        public Builder ToFile(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _FilePath = Path.GetFullPath(path);
            return this;
        }

        /// <summary>Sets the user-friendly display name shown in UI and logs.</summary>
        /// <param name="name">Display name; not empty or white space.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or white space.</exception>
        public Builder WithUiName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _UiName = name;
            return this;
        }

        /// <summary>Sets an optional description.</summary>
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the cancellation token for cooperative shutdown.</summary>
        /// <param name="token">The token; once cancelled, <see cref="OnPacket"/> returns <see langword="false"/> and the export is finished.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Stops after writing the specified number of packets. <c>0</c> means unlimited (default).
        /// When the target is reached, <see cref="OnPacket"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        /// <param name="count">Packet limit; 0 means unlimited.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // The upper limit is the number of items an array-index ID can address (ArrayIndexIdRange.MaxCount);
            // packet IDs are non-negative int32 values (INTEGER columns).
            if (count > ArrayIndexIdRange.MaxCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    Invariant($"Target packet count must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} ") +
                    Invariant($"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)})."));
            }

            _TargetPacketCount = count;
            return this;
        }

        /// <summary>
        /// Chooses how full batches are written: <see cref="ExportWriteMode.Background"/> (default) writes on a writer thread named
        /// "DuckDB export writer" while the next batch fills; <see cref="ExportWriteMode.Synchronous"/> writes inside the
        /// <see cref="OnPacket"/> call that completed the batch. Background holds two batches in memory, synchronous one.
        /// </summary>
        /// <param name="mode">The write mode.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
        public Builder WithWriteMode(ExportWriteMode mode)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Write mode must be Background or Synchronous.");
            }

            _WriteMode = mode;
            return this;
        }

        /// <summary>
        /// Sets the maximum number of packets accumulated per batch before an automatic flush. Memory: a batch is held until it
        /// is written and background mode holds two, so raising this trades memory for fewer, larger transactions.
        /// </summary>
        /// <param name="count">Packets per flush; 1 to <see cref="int.MaxValue"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1 (the message names the range).</exception>
        public Builder WithMaxPacketsPerBlock(int count)
        {
            _MaxPacketsPerBlock = BatchLimits.ValidatePacketsPerBatch(count, nameof(count));
            return this;
        }

        /// <summary>
        /// Sets the maximum estimated batch size (bytes) before an automatic flush. The size is the batch's in-memory
        /// estimate, so the real peak memory can be higher.
        /// </summary>
        /// <param name="bytes">Estimated bytes per flush; 1 to <see cref="long.MaxValue"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is below 1 (the message names the range).</exception>
        public Builder WithMaxBlockSize(long bytes)
        {
            _MaxBlockSize = BatchLimits.ValidateBytesPerBatch(bytes, nameof(bytes));
            return this;
        }

        /// <summary>
        /// Sets what the export contains: the shape (selected fields, all fields, or the hierarchy), the selected fields, and the
        /// optional columns and records (info, frame bytes, additional buffers, custom representation and text). Defaults to <see cref="DefaultContent"/>.
        /// </summary>
        /// <param name="content">The content; the topology is written only for <see cref="ExportShape.Hierarchy"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The shape and the fields do not match (<see cref="ExportContent.TryValidateShape"/>).</exception>
        public Builder WithContent(ExportContent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (!content.TryValidateShape(out string? problem))
            {
                throw new ArgumentException(problem, nameof(content));
            }

            _Content = content;
            return this;
        }

        /// <summary>
        /// Declares that packets will be added in non-decreasing timestamp order. Metadata only;
        /// accepted so callers can configure the shared <see cref="ColumnarPacketBatch"/> knobs
        /// identically across the PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
        /// <param name="sorted"><see langword="true"/> when the caller guarantees non-decreasing timestamps.</param>
        /// <returns>This builder.</returns>
        public Builder WithTimestampSorted(bool sorted)
        {
            _IsTimestampSorted = sorted;
            return this;
        }

        /// <summary>
        /// Sets DuckDB's <c>memory_limit</c> for the export connection, in bytes. When not called, no <c>SET</c> is issued and
        /// DuckDB's own default applies.
        /// </summary>
        /// <param name="bytes">Memory limit in bytes; must be greater than zero.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is zero or negative.</exception>
        public Builder WithMemoryLimit(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            _MemoryLimitBytes = bytes;
            return this;
        }

        /// <summary>
        /// Sets DuckDB's <c>threads</c> for the export connection. When not called, no <c>SET</c> is issued and DuckDB's own
        /// default applies.
        /// </summary>
        /// <param name="threads">Number of DuckDB worker threads; must be greater than zero.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is zero or negative.</exception>
        public Builder WithThreads(int threads)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
            _Threads = threads;
            return this;
        }

        /// <summary>
        /// Chooses the DuckDB storage version of the file, which decides the oldest DuckDB release that can open it
        /// (<see cref="DuckDbStorageVersion"/>). When not called, <see cref="DefaultStorageVersion"/> applies. The version is set
        /// explicitly as the connection setting <c>storage_compatibility_version</c> before the file is created; the exporter
        /// runs no compaction or copy pass afterwards (the export only appends, so a compaction gains little and adds a failure
        /// point at the end).
        /// </summary>
        /// <param name="version">The storage version; one of the six defined values.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="version"/> is not a defined value; the message lists the allowed setting texts. An undefined value is a
        /// bug of the caller (the plugin option accepts only the six registered texts and refuses others with its own message),
        /// so it throws like the other <c>With…</c> methods of this builder.
        /// </exception>
        /// <remarks>
        /// Validation: the six defined values map to their setting text and every other value is refused here, so the writer
        /// receives a valid text and needs no check of its own. Sync: README § Storage Version.
        /// </remarks>
        public Builder WithStorageVersion(DuckDbStorageVersion version)
        {
            if (!version.TryGetSettingValue(out string setting))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(version),
                    version,
                    Invariant($"Storage version must be one of {DuckDbStorageVersions.AllowedText}."));
            }

            _StorageVersionSetting = setting;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output file was configured.</summary>
        /// <returns>A new exporter; no file exists until its first packet.</returns>
        /// <exception cref="InvalidOperationException">No output file was configured.</exception>
        public DuckDbExporter Build()
        {
            if (_FilePath is null)
            {
                throw new InvalidOperationException("No output file configured. Call ToFile().");
            }

            return new DuckDbExporter(
                _FilePath,
                _UiName,
                _Description,
                _MaxPacketsPerBlock,
                _MaxBlockSize,
                _TargetPacketCount,
                _Content,
                _IsTimestampSorted,
                _MemoryLimitBytes,
                _Threads,
                _StorageVersionSetting,
                _WriteMode,
                _CancellationToken);
        }
    }

    #endregion
}
