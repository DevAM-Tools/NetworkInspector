// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// Parquet packet exporter. Writes parsed packets to a dataset of Parquet files, one entry of an <see cref="IExportTarget"/> per
/// file (a folder of files for <see cref="Builder.ToDirectory"/>), forming a
/// small relational dataset: <c>packets.parquet</c>, <c>packet_buffers.parquet</c> (only with <see cref="ExportContent.Buffers"/>),
/// <c>topology.parquet</c>, <c>catalog.parquet</c>, <c>protocols.parquet</c>, <c>export_info.parquet</c>, and one
/// <c>fields/field_{id}.parquet</c> file per distinct field ID observed (see <see cref="ParquetBatchSink"/> for the full layout).
/// <para>
/// Shares the <see cref="ColumnarPacketBatch"/> accumulator with the PBF columnar format and
/// the DuckDB exporter, so <see cref="ExportContent"/> is configured identically across
/// all three columnar formats. <c>packet_id</c> columns use Core <see cref="PacketId"/> width
/// (<see cref="int"/> / Parquet INT32).
/// </para>
/// <para>
/// <b>Memory model:</b> packets are accumulated column-wise in a <see cref="ColumnarPacketBatch"/> and handed to the
/// <see cref="ParquetBatchSink"/> as one row group per table whenever the packet count or the estimated batch size
/// reaches its limit (see <see cref="Builder.WithMaxPacketsPerBlock"/> and <see cref="Builder.WithMaxBlockSize"/>). In
/// <see cref="ExportWriteMode.Background"/> (the default) two batches exist: while a writer thread encodes and writes one
/// row group, the caller's thread fills the other, so parsing and writing overlap. Memory stays bounded by two batches
/// regardless of capture size, and Parquet's columnar encoding gets row groups large enough to compress well.
/// <see cref="ExportWriteMode.Synchronous"/> keeps one batch and writes it inside the call that completed it.
/// </para>
/// <para>
/// <b>Why a writer thread:</b> Parquet.Net encodes and compresses every row group on the thread that writes it, which takes as
/// long as parsing thousands of packets; on the caller's thread it would stop parsing for every flush.
/// </para>
/// <para>
/// <b>Error timing:</b> in background mode a batch is written after <see cref="OnPacket"/> returned for its last packet, so a
/// failing batch is reported by the next <see cref="OnPacket"/> call that finds it (at the latest by <see cref="OnFinish"/>); in
/// synchronous mode by that very call. Its packets leave <see cref="WrittenCount"/> and count as skipped when it is reported.
/// </para>
/// <para>
/// <b>Export shapes:</b> <see cref="Builder.WithContent"/> chooses the shape. <see cref="ExportShape.Hierarchy"/> (the default,
/// <see cref="DefaultContent"/>) writes every field with its place in the field tree and needs packets with a field tree.
/// <see cref="ExportShape.SelectedFields"/> and <see cref="ExportShape.AllFields"/> write value rows numbered by occurrence and
/// no topology; they are fed field by field while a packet is parsed: <see cref="ParseDemand"/> states the exporter's observer, a
/// <see cref="ParsePlan"/> parses with it, and <see cref="OnPacket"/> commits the staged fields of that very packet. A packet the
/// filter rejected is simply not committed. Tree packets are accepted for every shape and give the same output.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. Statistics are valid to read after
/// <see cref="OnFinish"/> returns. Internally one named writer thread ("Parquet export writer") writes the full batches in
/// background mode; it never touches the counters or raises <see cref="ItemSkipped"/>, which stay on the caller's thread.
/// Parquet.Net exposes only async writers, so the exporter blocks on them; the row groups are written on the exporter's own
/// writer thread (no <see cref="SynchronizationContext"/>) in background mode, while in synchronous mode and for the file
/// creation and the catalog the caller's thread blocks: do not call the exporter from a thread with a
/// <see cref="SynchronizationContext"/> (UI thread) in that mode.
/// </para>
/// <para>
/// <b>Single-use:</b> Once <see cref="OnFinish"/> (or <see cref="Dispose"/>) is called, this
/// instance is finalized and cannot be reused. Subsequent calls to <see cref="OnPacket"/>
/// after <see cref="OnFinish"/> are silently ignored.
/// </para>
/// <para>
/// <b>Empty export:</b> If no packets are ever added, no directory or files are created —
/// matching the lazy-initialisation behaviour of the other exporters (see <c>EXPORTER_GUIDE.md</c>
/// §7.2/§7.5).
/// </para>
/// <para>
/// <b>Compression:</b> <see cref="Builder.WithCompression"/> chooses the codec (<see cref="ParquetCompression"/>) and the level
/// (<see cref="ParquetCompressionLevel"/>) of every column chunk of every file of the dataset. They are always set explicitly,
/// <see cref="DefaultCompression"/> at <see cref="DefaultCompressionLevel"/> when the builder is not asked, so the files never follow
/// the Parquet.Net default.
/// </para>
/// </summary>
public sealed class ParquetExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IBatchWriter<ColumnarPacketBatch>, IDisposable
{
    #region Configuration and state

    /// <summary>
    /// Receives the table files as entries; for <see cref="Builder.ToDirectory"/> the internal <see cref="ParquetDirectoryTarget"/>.
    /// Nothing is opened before the first packet.
    /// </summary>
    private readonly IExportTarget _Target;

    /// <summary>Packets per batch before the batch is handed to the writer (a flush limit, see <see cref="BatchLimits"/>).</summary>
    private readonly int _MaxPacketsPerBlock;

    /// <summary>Estimated bytes per batch before the batch is handed to the writer (a flush limit, see <see cref="BatchLimits"/>).</summary>
    private readonly long _MaxBlockSize;

    /// <summary>What the dataset contains: the shape, the selected fields, and the optional columns.</summary>
    private readonly ExportContent _Content;

    // Null for the hierarchy shape, which is written from the field tree (AddPacket). For the flat shapes the observer of this
    // collector stages the fields of a packet straight into the current batch while the packet is parsed; OnPacket commits them.

    /// <summary>The staging collector of the flat shapes; <see langword="null"/> for the hierarchy shape.</summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>Declared by the caller: packets arrive in non-decreasing timestamp order. Passed on to the batches.</summary>
    private readonly bool _IsTimestampSorted;

    /// <summary>The Parquet.Net codec of every file; already mapped and validated by <see cref="Builder.WithCompression"/>.</summary>
    private readonly CompressionMethod _CompressionMethod;

    /// <summary>The compression level of every file; already mapped and validated by <see cref="Builder.WithCompression"/>.</summary>
    private readonly CompressionLevel _CompressionLevel;

    /// <summary>The sink that writes the batches; <see langword="null"/> until <see cref="_Start"/> succeeded and after the finish.</summary>
    private ParquetBatchSink? _Sink;

    /// <summary>Whether full batches are written on a writer thread or inside the call that completed them.</summary>
    private readonly ExportWriteMode _WriteMode;

    // The writer pipeline owns both batches, the writer thread (or writes inline in synchronous mode), and the queue of lost
    // batches (see BatchWriterPipeline for the primitive choice, the memory bound, and the error timing). It is created in the
    // constructor without I/O, batches, or a thread, so an export that never receives a packet leaves nothing behind. The
    // exporter is the pipeline's writer (explicit IBatchWriter implementation) and forwards to the sink that _Start opens.

    /// <summary>The batches, the writer thread, and the lost-batch queue of this export.</summary>
    private readonly BatchWriterPipeline<ColumnarPacketBatch> _Pipeline;

    // Intake and accounting (stop checks, the one-stack rule, the lazy start, the counters, the batch hand-off, the lost-batch
    // accounting, and the error channel) are the same for every columnar exporter and live in the shared core; this exporter
    // keeps its builder, the opening of the dataset, the batch write, and the finish sequence. The statistics properties below
    // forward to the core.

    /// <summary>Intake and accounting of this export; shared with the other columnar exporters.</summary>
    private readonly ColumnarBatchExportCore _Core;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="target">The target that receives the table files.</param>
    /// <param name="uiName">The display name.</param>
    /// <param name="description">The description; <see langword="null"/> for none.</param>
    /// <param name="maxPacketsPerBlock">Packets per batch.</param>
    /// <param name="maxBlockSize">Estimated bytes per batch.</param>
    /// <param name="targetPacketCount">Stop after this many packets; 0 means unlimited.</param>
    /// <param name="content">What the dataset contains.</param>
    /// <param name="isTimestampSorted">Whether the caller declared non-decreasing timestamps.</param>
    /// <param name="compressionMethod">The Parquet.Net codec of every file; validated by the builder.</param>
    /// <param name="compressionLevel">The compression level of every file; validated by the builder.</param>
    /// <param name="writeMode">Whether full batches are written on a writer thread or inline.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    private ParquetExporter(
        IExportTarget target,
        string uiName,
        string? description,
        int maxPacketsPerBlock,
        long maxBlockSize,
        int targetPacketCount,
        ExportContent content,
        bool isTimestampSorted,
        CompressionMethod compressionMethod,
        CompressionLevel compressionLevel,
        ExportWriteMode writeMode,
        CancellationToken cancellationToken)
    {
        _Target = target;
        _CompressionMethod = compressionMethod;
        _CompressionLevel = compressionLevel;
        UiName = uiName;
        Description = description;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        _Content = content;
        _IsTimestampSorted = isTimestampSorted;
        _WriteMode = writeMode;
        _Pipeline = new BatchWriterPipeline<ColumnarPacketBatch>(
            "Parquet",
            writeMode,
            () => new ColumnarPacketBatch(content, maxPacketsPerBlock, maxBlockSize, isTimestampSorted),
            this);

        // The observer stages the first packet before the first OnPacket, so for the flat shapes the first batch is needed
        // now (the pipeline creates it on the first access to Current; no file, directory, or thread exists yet).
        if (content.Shape != ExportShape.Hierarchy)
        {
            _Collector = new ExportFieldCollector(content, _Pipeline.Current);
        }

        // The delegates are created once here, so the per-packet path allocates none.
        _Core = new ColumnarBatchExportCore(
            "Parquet",
            content,
            targetPacketCount,
            _Pipeline,
            _Collector,
            _RaiseItemSkipped,
            _Start,
            cancellationToken);
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    /// <returns>
    /// A builder with the defaults; one of <see cref="Builder.ToDirectory"/> or <see cref="Builder.ToTarget"/> is required before
    /// <see cref="Builder.Build"/>.
    /// </returns>
    public static Builder CreateBuilder() => new();

    /// <summary>
    /// The content written when <see cref="Builder.WithContent"/> is not called: every field with hierarchy, the packet info, and
    /// the custom texts (the hierarchy shape); the frame bytes (<see cref="ExportContent.FrameBytes"/>) and the additional buffers
    /// (<see cref="ExportContent.Buffers"/>) are off.
    /// </summary>
    public static ExportContent DefaultContent { get; } = new();

    /// <summary>
    /// The codec written when <see cref="Builder.WithCompression"/> is not called: <see cref="ParquetCompression.Zstd"/>. It is set
    /// explicitly on every file, so the dataset never follows a library default that changes with a Parquet.Net upgrade.
    /// Posture: open by default for current readers (small files); a reader that predates Zstandard needs another codec.
    /// Sync: README § Compression and the plugin option default.
    /// </summary>
    public const ParquetCompression DefaultCompression = ParquetCompression.Zstd;

    /// <summary>
    /// The level written when <see cref="Builder.WithCompression"/> is not called: <see cref="ParquetCompressionLevel.Optimal"/>.
    /// Sync: README § Compression and the plugin option default.
    /// </summary>
    public const ParquetCompressionLevel DefaultCompressionLevel = ParquetCompressionLevel.Optimal;

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
    /// still be buffered in memory and not yet written to a file.
    /// </summary>
    public int PacketCount => _Core.PacketCount;

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    /// <remarks>
    /// An in-memory estimate (flushed batches plus the open batch), not the on-disk size: Parquet compresses and
    /// encodes the columns, so the files are usually smaller.
    /// </remarks>
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
    /// The stop checks, the one-stack rule, the lazy start (<see cref="_Start"/>; nothing is created on disk until the first packet
    /// arrives, see "Empty export" above), the add, and the hand-off of a full batch are the shared intake of
    /// <see cref="ColumnarExportCore{TBatch}"/>.
    /// </remarks>
    public bool OnPacket(Packet packet) => _Core.OnPacket(packet);

    /// <summary>
    /// Lazily creates the output directory and the sink. The protocol catalog is taken from the first packet's stack: all packets
    /// of one export come from the same parsing stack, so its protocol set is fixed. Batches and the writer thread are created
    /// by the pipeline on first use.
    /// </summary>
    /// <param name="firstPacket">The first packet of the export.</param>
    /// <returns><see langword="true"/> when the sink is open; <see langword="false"/> after the failure was reported.</returns>
    private bool _Start(Packet firstPacket)
    {
        // The start is marked before the work so a failed start is not retried with the next packet; the error flag
        // below then stops the export. The sink constructor writes protocols.parquet and export_info.parquet, so this step
        // is the first (and only) place where the target is asked for an entry; a ToDirectory target clears the earlier dataset
        // with that first open. A refused entry is an IOException of the sink and ends here as the start failure.
        _Core.MarkStarted();
        try
        {
            _Sink = new ParquetBatchSink(
                _Target,
                _Content,
                ProtocolCatalogEntry.FromStack(firstPacket.Stack),
                _CompressionMethod,
                _CompressionLevel);
            return true;
        }
        catch (Exception ex)
        {
            _Core.ReportStartFailure(ExportErrorKind.IoError, Invariant($"Parquet output initialization failed: {ex.Message}"));
            return false;
        }
    }

    #endregion

    #region Batch write

    /// <summary>
    /// Writer-thread entry (or caller entry in synchronous mode): forwards to the sink that <see cref="_Start"/> opened. The sink
    /// is non-null here because a batch is only submitted after a started export.
    /// </summary>
    /// <param name="batch">The full batch to write as one row group per table.</param>
    void IBatchWriter<ColumnarPacketBatch>.Write(ColumnarPacketBatch batch) => _Sink!.WriteBatch(batch);

    #endregion

    #region Finish

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the files. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps (sink or batch disposal) failed.</exception>
    public void OnFinish()
    {
        if (!_Core.TryBeginFinish())
        {
            return;
        }

        if (!_Core.Started)
        {
            // No packets were ever added — leave no directory/files on disk (§7.5). The pipeline holds nothing yet (batches
            // and thread are created lazily), disposing it only marks it completed.
            _Pipeline.Dispose();
            return;
        }

        // Shared finish sequence: final write and footers, then the pipeline (thread, batches) and the sink are released even
        // when a step fails. The pipeline goes first: the sink must not be disposed while the writer thread may still use it.
        ExporterFinish.Run(
            "Parquet",
            _FinalizeFiles,
            _Core.ReportFinishError,
            new ExporterReleaseStep("writer pipeline disposal", _Pipeline.Dispose),
            new ExporterReleaseStep("sink disposal", _DisposeSink));
    }

    /// <summary>Writes the last batch, accounts the lost ones, and completes the sink (writes the catalog and closes the row groups).</summary>
    private void _FinalizeFiles()
    {
        if (!_Pipeline.IsCompleted)
        {
            // Fields staged for a packet that was never committed (the filter rejected it) must not reach the last batch.
            if (_Collector is not null)
            {
                _Pipeline.Current.Discard();
            }

            _Pipeline.Complete(firstItemIndex: PacketCount - _Pipeline.CurrentPacketCount);
        }

        // Lost batches first, so the counters match the files; then the footers and catalog.parquet.
        _Core.DrainLostBatches(reportAlways: true);
        _Sink?.Complete();
    }

    /// <summary>Disposes the sink once (closes every table file).</summary>
    private void _DisposeSink()
    {
        ParquetBatchSink? sink = _Sink;
        _Sink = null;
        sink?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Builder

    /// <summary>Fluent builder for constructing a <see cref="ParquetExporter"/>.</summary>
    /// <remarks>
    /// Output: <c>ToDirectory</c> and <c>ToTarget</c> replace each other, and the last call before <c>Build</c> wins. Nothing is
    /// opened before the first packet, so a replaced output creates no folder and holds no resource. The output belongs to the
    /// exporter that <c>Build</c> returns: build every further exporter from a new builder, because a second <c>Build</c> hands both
    /// exporters the same target.
    /// </remarks>
    public sealed class Builder
    {
        /// <summary>
        /// The output; <see langword="null"/> until <see cref="ToDirectory"/> or <see cref="ToTarget"/> was called, and replaced
        /// by a later call of either.
        /// </summary>
        private IExportTarget? _Target;

        /// <summary>The display name.</summary>
        private string _UiName = "Parquet Exporter";

        /// <summary>The description; <see langword="null"/> for none.</summary>
        private string? _Description;

        /// <summary>Cooperative cancellation; <see cref="CancellationToken.None"/> by default.</summary>
        private CancellationToken _CancellationToken;

        // Flush thresholds for the in-memory batch: see BatchLimits (range, threat, posture, who may change them). Defaults
        // 50 000 packets / 16 MiB, shared with the PBF, DuckDB, and BLF exporters; whichever limit is hit first triggers the flush.

        /// <summary>Packets per batch before a flush; validated by <see cref="BatchLimits"/>.</summary>
        private int _MaxPacketsPerBlock = BatchLimits.DefaultPacketsPerBatch;

        /// <summary>Estimated bytes per batch before a flush; validated by <see cref="BatchLimits"/>.</summary>
        private long _MaxBlockSize = BatchLimits.DefaultBytesPerBatch;

        /// <summary>Whether full batches are written on a writer thread (default) or inside the completing call.</summary>
        private ExportWriteMode _WriteMode = ExportWriteMode.Background;

        /// <summary>Stop after this many packets; 0 means unlimited.</summary>
        private int _TargetPacketCount;

        /// <summary>What the dataset contains; <see cref="DefaultContent"/> unless <see cref="WithContent"/> was called.</summary>
        private ExportContent _Content = DefaultContent;

        /// <summary>Declared non-decreasing timestamp order (metadata only).</summary>
        private bool _IsTimestampSorted;

        /// <summary>
        /// The Parquet.Net codec of every file: <see cref="ParquetCompressions.DefaultMethod"/> (the mapping of
        /// <see cref="DefaultCompression"/>) until <see cref="WithCompression"/> replaces it with the mapping of a validated value.
        /// </summary>
        private CompressionMethod _CompressionMethod = ParquetCompressions.DefaultMethod;

        /// <summary>The compression level of every file; the mapping of <see cref="DefaultCompressionLevel"/> until <see cref="WithCompression"/>.</summary>
        private CompressionLevel _CompressionLevel = ParquetCompressions.DefaultLevel;

        /// <summary>
        /// Sets the output to a directory of Parquet files (see <see cref="ParquetBatchSink"/> for the layout); a host that wants the
        /// files as streams uses <see cref="ToTarget"/>. One of the two is required.
        /// The directory is created lazily on the first <see cref="OnPacket"/> call. Existing dataset files in it
        /// (the six table files <c>packets</c>, <c>packet_buffers</c>, <c>topology</c>, <c>catalog</c>, <c>protocols</c>,
        /// <c>export_info</c> and <c>fields/*.parquet</c>) are deleted at that point.
        /// </summary>
        /// <param name="path">Output directory; resolved to a full path immediately so later working-directory changes do not move it.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
        public Builder ToDirectory(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _Target = new ParquetDirectoryTarget(Path.GetFullPath(path));
            return this;
        }

        /// <summary>
        /// Sets the output to a host target: the exporter opens one entry per table file (<c>packets.parquet</c>,
        /// <c>fields/field_{id}.parquet</c>, …) and never learns where they land. The streams may be write-only and non-seekable.
        /// The target is used as given: an earlier dataset is not cleared (that is a behavior of <see cref="ToDirectory"/>).
        /// Nothing is opened before the first <see cref="OnPacket"/> call.
        /// </summary>
        /// <param name="target">The target; a refused entry becomes an export error.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
        public Builder ToTarget(IExportTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            _Target = target;
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
            // packet IDs are non-negative int32 values (INT32 columns).
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
        /// "Parquet export writer" while the next batch fills; <see cref="ExportWriteMode.Synchronous"/> writes inside the
        /// <see cref="OnPacket"/> call that completed the batch (and blocks the caller's thread on Parquet.Net's async writers).
        /// Background holds two batches in memory, synchronous one.
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
        /// Sets the maximum number of packets accumulated per batch (one row group per table) before an automatic flush.
        /// Memory: a batch is held until it is written and background mode holds two.
        /// </summary>
        /// <param name="count">Packets per row group; 1 to <see cref="int.MaxValue"/>.</param>
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
        /// <param name="bytes">Estimated bytes per row group; 1 to <see cref="long.MaxValue"/>.</param>
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
        /// Declares that packets will be added in non-decreasing timestamp order. Metadata only
        /// (see <see cref="NetworkInspector.Exporters.Pbf.PbfExporter"/>'s equivalent option);
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
        /// Chooses the compression of every column chunk of every file of the dataset. When not called,
        /// <see cref="DefaultCompression"/> at <see cref="DefaultCompressionLevel"/> applies. Codec and level are always set
        /// explicitly on every writer, never left to the Parquet.Net default.
        /// </summary>
        /// <param name="compression">The codec; one of the six defined values.</param>
        /// <param name="level">
        /// The level; one of the three defined values. It applies to the codecs that have levels and is ignored by
        /// <see cref="ParquetCompression.None"/> and <see cref="ParquetCompression.Snappy"/>.
        /// </param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="compression"/> or <paramref name="level"/> is not a defined value; the message lists the allowed values. An
        /// undefined value is a bug of the caller (the plugin options accept only the registered texts and refuse others with their
        /// own message), so it throws like the other <c>With…</c> methods of this builder. The builder keeps its previous choice.
        /// </exception>
        /// <remarks>
        /// Validation: the six codecs and the three levels map to their Parquet.Net settings and every other value is refused here,
        /// so the sink receives valid settings and checks nothing of its own. Both values are checked before either is stored, so a
        /// refused call changes nothing. Sync: README § Compression.
        /// </remarks>
        public Builder WithCompression(ParquetCompression compression, ParquetCompressionLevel level = DefaultCompressionLevel)
        {
            if (!compression.TryGetMethod(out CompressionMethod method))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(compression),
                    compression,
                    Invariant($"Compression must be one of {ParquetCompressions.AllowedCompressionText}."));
            }

            if (!level.TryGetLevel(out CompressionLevel mappedLevel))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(level),
                    level,
                    Invariant($"Compression level must be one of {ParquetCompressions.AllowedLevelText}."));
            }

            _CompressionMethod = method;
            _CompressionLevel = mappedLevel;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output was configured.</summary>
        /// <returns>A new exporter; no entry, file, or directory exists until its first packet.</returns>
        /// <exception cref="InvalidOperationException">No output was configured.</exception>
        public ParquetExporter Build()
        {
            if (_Target is null)
            {
                throw new InvalidOperationException("No output configured. Call ToDirectory() or ToTarget().");
            }

            return new ParquetExporter(
                _Target,
                _UiName,
                _Description,
                _MaxPacketsPerBlock,
                _MaxBlockSize,
                _TargetPacketCount,
                _Content,
                _IsTimestampSorted,
                _CompressionMethod,
                _CompressionLevel,
                _WriteMode,
                _CancellationToken);
        }
    }

    #endregion
}
