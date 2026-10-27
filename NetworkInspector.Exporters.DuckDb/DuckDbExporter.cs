// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.DuckDb;

/// <summary>
/// DuckDB packet exporter. Writes parsed packets to a single <c>.duckdb</c> database file using
/// bulk-load <see cref="DuckDBAppender"/>s — see <see cref="DuckDbBulkWriter"/> for the full table
/// layout.
/// <para>
/// Shares the <see cref="ColumnarPacketBatch"/> accumulator with the PBF columnar format and the
/// Parquet exporter, so <see cref="ColumnarDetailFlags"/> is configured identically across all
/// three columnar formats. <c>packet_id</c> columns use Core <see cref="PacketId"/> width
/// (<see cref="int"/> / DuckDB <c>INTEGER</c>).
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/> must
/// be called sequentially from a single thread. Statistics are valid to read after
/// <see cref="OnFinish"/> returns.
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
/// <b>Memory model:</b> packets are accumulated column-wise in one <see cref="ColumnarPacketBatch"/> and handed to the
/// <see cref="DuckDbBulkWriter"/> whenever the packet count or the estimated batch size reaches its limit, so memory
/// stays bounded by one batch regardless of capture size. Each flush is one DuckDB transaction.
/// </para>
/// <para>
/// <b>Existing output:</b> the first packet deletes an existing database file (and its <c>.wal</c>) at the target
/// path, so a re-export replaces the file instead of appending to it.
/// </para>
/// </summary>
public sealed class DuckDbExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    #region Fields

    private readonly string _FilePath;
    private readonly CancellationToken _CancellationToken;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize;
    private readonly int _TargetPacketCount;
    private readonly ColumnarDetailFlags _ColumnarDetailFlags;
    private readonly bool _IsTimestampSorted;

    /// <summary>DuckDB <c>memory_limit</c> in bytes; <c>0</c> = DuckDB's default (no <c>SET</c> issued).</summary>
    private readonly long _MemoryLimitBytes;

    /// <summary>DuckDB <c>threads</c>; <c>0</c> = DuckDB's default (no <c>SET</c> issued).</summary>
    private readonly int _Threads;

    private ColumnarPacketBatch? _Batch;
    private DuckDbBulkWriter? _Writer;

    // Sum of the in-memory size estimates of all flushed batches. Used for EstimatedOutputBytes so that reading the
    // property never touches the file system (the database file is also larger or smaller than this estimate).
    private long _FlushedOutputBytes;

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    // The stack of the first packet: the protocols table of the database is built from it, and every field id in the dataset
    // is an id of that stack. Threat: a packet of another stack (a session restart between two OnPacket calls) carries field
    // ids that name other fields in this catalog, so writing it would corrupt the database silently. Mitigation: the packet is
    // refused as a fatal, reported error (StackMismatch), the rows of the earlier packets are still committed by OnFinish, and
    // the owner of the export (whose job a restart ended anyway) starts a new export on the new stack. Rejected: skipping only
    // that packet in Tolerant mode (every later packet of the new stack would be skipped one by one, each reported) and a
    // second protocols table (the schema has one catalog per database). Residual risk: none for catalog exporters.
    private Stack? _CatalogStack;

    #endregion

    #region Constructor

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private DuckDbExporter(
        string filePath,
        string uiName,
        string? description,
        int maxPacketsPerBlock,
        long maxBlockSize,
        int targetPacketCount,
        ColumnarDetailFlags columnarDetailFlags,
        bool isTimestampSorted,
        long memoryLimitBytes,
        int threads,
        CancellationToken cancellationToken)
    {
        _MemoryLimitBytes = memoryLimitBytes;
        _Threads = threads;
        _FilePath = filePath;
        UiName = uiName;
        Description = description;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        _TargetPacketCount = targetPacketCount;
        _ColumnarDetailFlags = columnarDetailFlags;
        _IsTimestampSorted = isTimestampSorted;
        _CancellationToken = cancellationToken;
    }

    #endregion

    #region Public API

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    public static Builder CreateBuilder() => new();

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

    /// <summary>
    /// Number of packets accepted so far. A packet counts as soon as it is added to the current batch, which may
    /// still be buffered in memory and not yet written to the database.
    /// </summary>
    public int PacketCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    public long EstimatedOutputBytes =>
        _FlushedOutputBytes + (_Batch?.EstimatedSizeBytes ?? 0);

    /// <inheritdoc/>
    public int SkippedCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int ErrorCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Whether the exporter has stopped due to reaching the target count, error, or cancellation.</summary>
    public bool IsFinished => _Finished || _HasError
        || _CancellationToken.IsCancellationRequested
        || (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount);

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Tolerant;

    /// <inheritdoc/>
    public event EventHandler<ExportErrorEventArgs>? ItemSkipped;

    /// <inheritdoc/>
    public bool OnPacket(Packet packet)
    {
        // Stop signals first: cancellation, an earlier fatal error, or a finished exporter all end the export. After
        // OnFinish the writer and batch are released, so a late packet must never reach them.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy start: the database file is created only when the first packet arrives.
        if (!_Started)
        {
            _CatalogStack = packet.Stack;
            _Start(packet);
            if (_HasError)
            {
                return false;
            }
        }

        // One stack per export: see _CatalogStack.
        if (!ReferenceEquals(packet.Stack, _CatalogStack))
        {
            _ReportStackMismatch();
            return false;
        }

        return _HandlePacket(packet);
    }

    /// <summary>
    /// A packet of another stack than the catalog's arrived: latches the fatal state, counts it, and raises
    /// <see cref="ItemSkipped"/> with <see cref="ExportErrorKind.StackMismatch"/>. The packet is not written.
    /// </summary>
    private void _ReportStackMismatch()
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = PacketCount,
            Kind = ExportErrorKind.StackMismatch,
            Message = "The packet was parsed on another protocol stack than this export's catalog; start a new export after a session restart.",
        });
    }

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the database. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;
        _CatalogStack = null; // A finished exporter accepts no packet (OnPacket checks _Finished first); do not keep the stack alive.

        if (!_Started)
        {
            // No packets were ever added — leave no database file on disk (§7.5).
            return;
        }

        // Shared finish sequence: final flush and commit, then writer and batch release even when a step fails.
        ExporterFinish.Run(
            "DuckDB",
            _FinalizeDatabase,
            _ReportFinishError,
            new ExporterReleaseStep("writer disposal", _DisposeWriter),
            new ExporterReleaseStep("batch disposal", _DisposeBatch));
    }

    /// <summary>Flushes the last batch and completes the writer (catalog rows, commit, then the single checkpoint).</summary>
    private void _FinalizeDatabase()
    {
        if (_Batch is not null && _Batch.PacketCount > 0)
        {
            _FlushBatch();
        }

        _Writer?.Complete();
    }

    /// <summary>Disposes the writer once (closes the connection and the database file).</summary>
    private void _DisposeWriter()
    {
        DuckDbBulkWriter? writer = _Writer;
        _Writer = null;
        writer?.Dispose();
    }

    /// <summary>Disposes the batch once (returns its pooled column buffers).</summary>
    private void _DisposeBatch()
    {
        ColumnarPacketBatch? batch = _Batch;
        _Batch = null;
        batch?.Dispose();
    }

    /// <summary>Error channel of the finish sequence: marks the error, counts it, and raises <see cref="ItemSkipped"/>.</summary>
    private void _ReportFinishError(ExportErrorKind kind, string message)
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = PacketCount,
            Kind = kind,
            Message = message,
        });
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Private Helpers

    /// <summary>
    /// Lazily opens the DuckDB file, creates the base tables, and creates the batch accumulator. The protocol catalog is
    /// taken from the first packet's stack: all packets of one export come from the same parsing stack.
    /// </summary>
    /// <param name="firstPacket">The first packet of the export.</param>
    private void _Start(Packet firstPacket)
    {
        // _Started is set before the work so a failed start is not retried with the next packet; the error flag
        // below then stops the export. The writer constructor deletes any previous database at the path.
        _Started = true;
        try
        {
            _Writer = new DuckDbBulkWriter(
                _FilePath,
                _ColumnarDetailFlags,
                ProtocolCatalogEntry.FromStack(firstPacket.Stack),
                _MemoryLimitBytes,
                _Threads);
            _Batch = new ColumnarPacketBatch(
                _ColumnarDetailFlags, _MaxPacketsPerBlock, _MaxBlockSize, _IsTimestampSorted);
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ExportErrorKind.IoError,
                Message = $"DuckDB output initialization failed: {ex.Message}",
            });
        }
    }

    /// <summary>Adds one packet to the current batch, flushing to the writer if the batch is full.</summary>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse further packets so the caller stops feeding the exporter.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // The packet is added first and the flush happens only when a limit is reached. A packet without a field tree
        // is rejected by AddPacket (InvalidOperationException) and handled here like any other failure. AddPacket is
        // all-or-nothing: when it throws, the batch is exactly as it was, so only this packet is skipped.
        bool shouldFlush;
        try
        {
            shouldFlush = _Batch!.AddPacket(packet);
        }
        catch (Exception ex)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"DuckDB packet write failed: {ex.Message}",
            });
        }

        if (shouldFlush)
        {
            // The batch includes this packet, and the packets before it in the batch were already counted as written.
            int packetsInBatch = _Batch.PacketCount;
            try
            {
                _FlushBatch();
            }
            catch (Exception ex)
            {
                // _FlushBatch discarded the batch (see there), so none of its packets reached the database. The earlier
                // ones were counted as written when they were accepted; take them back and count them as skipped, so
                // WrittenCount and SkippedCount describe what is in the database. This packet is counted by _HandleSkip below.
                int lostEarlier = packetsInBatch - 1;
                int itemIndex = PacketCount - lostEarlier;
                PacketCount -= lostEarlier;
                SkippedCount = SkippedCount > int.MaxValue - lostEarlier
                    ? int.MaxValue
                    : SkippedCount + lostEarlier;
                return _HandleSkip(new ExportErrorEventArgs
                {
                    ItemIndex = itemIndex,
                    Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                    Message = $"DuckDB batch write failed, {packetsInBatch.ToString(CultureInfo.InvariantCulture)} packets lost: {ex.Message}",
                });
            }
        }

        PacketCount++;
        return true;
    }

    /// <summary>
    /// Flushes the current batch and accounts its estimated size toward
    /// <see cref="EstimatedOutputBytes"/> (in-memory only — no filesystem probe).
    /// </summary>
    /// <remarks>
    /// The batch is emptied whether the flush succeeds or not. The database transaction already rolled the failed batch
    /// back, so no row is duplicated by a retry, but keeping the rows would let the batch grow without bound while the
    /// output keeps failing and would re-run the failing flush for every following packet. The exporter instead reports
    /// the packets of the batch as lost, the same as the Parquet exporter.
    /// </remarks>
    private void _FlushBatch()
    {
        // Read the estimate before WriteBatch/Reset: Reset clears it.
        long batchBytes = _Batch!.EstimatedSizeBytes;
        try
        {
            _Writer!.WriteBatch(_Batch);
        }
        catch
        {
            _Batch.Reset();
            throw;
        }

        // Saturating add: the sum is only a progress estimate; wrapping to a negative value would be worse than
        // pinning it at the maximum.
        if (_FlushedOutputBytes > long.MaxValue - batchBytes)
        {
            _FlushedOutputBytes = long.MaxValue;
        }
        else
        {
            _FlushedOutputBytes += batchBytes;
        }

        _Batch.Reset();
    }

    /// <summary>
    /// Handles a skipped packet: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
    private bool _HandleSkip(ExportErrorEventArgs error)
    {
        if (SkippedCount < int.MaxValue)
        {
            SkippedCount++;
        }

        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _HasError = true;
            return false;
        }

        ItemSkipped?.Invoke(this, error);
        return true;
    }

    #endregion

    #region Builder

    /// <summary>Fluent builder for constructing a <see cref="DuckDbExporter"/>.</summary>
    public sealed class Builder
    {
        private string? _FilePath;
        private string _UiName = "DuckDB Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        // Flush thresholds for the in-memory batch: 1..int.MaxValue packets / 1..long.MaxValue estimated bytes;
        // threat: unbounded memory growth, since the whole batch is held until flushed; configurable by the
        // library caller through WithMaxPacketsPerBlock / WithMaxBlockSize; whichever limit is hit first triggers the
        // flush. Defaults 50 000 packets / 16 MiB (the same as the PBF and Parquet exporters; the origin of these
        // numbers is not recorded).
        private int _MaxPacketsPerBlock = 50000;
        private long _MaxBlockSize = 16 * 1024 * 1024;
        private int _TargetPacketCount;
        private ColumnarDetailFlags _ColumnarDetailFlags = ColumnarDetailFlags.All;
        private bool _IsTimestampSorted;

        // DuckDB resource limits; 0 means "not set" (DuckDB's defaults apply, open by default). See
        // DuckDbBulkWriter.BuildSettingsSql for the limit description.
        private long _MemoryLimitBytes;
        private int _Threads;

        /// <summary>
        /// Sets the output <c>.duckdb</c> file path. Required — DuckDB output is always a single
        /// database file, never a directory or stream. The file is created lazily on the first
        /// <see cref="OnPacket"/> call; an existing file (and its <c>.wal</c>) at the path is deleted at that point.
        /// </summary>
        /// <param name="path">Database file path; resolved to a full path immediately so later working-directory changes do not move it.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
        public Builder ToFile(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _FilePath = Path.GetFullPath(path);
            return this;
        }

        /// <summary>Sets the user-friendly display name shown in UI and logs.</summary>
        public Builder WithUiName(string name)
        {
            _UiName = name;
            return this;
        }

        /// <summary>Sets an optional description.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets the cancellation token for cooperative shutdown.</summary>
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
                    $"Target packet count must not exceed {ArrayIndexIdRange.MaxCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");
            }

            _TargetPacketCount = count;
            return this;
        }

        /// <summary>Sets the maximum number of packets accumulated per batch before an automatic flush.</summary>
        /// <param name="count">Packets per flush; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero or negative.</exception>
        public Builder WithMaxPacketsPerBlock(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            _MaxPacketsPerBlock = count;
            return this;
        }

        /// <summary>
        /// Sets the maximum estimated batch size (bytes) before an automatic flush. The size is the batch's in-memory
        /// estimate, so the real peak memory can be higher.
        /// </summary>
        /// <param name="bytes">Estimated bytes per flush; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is zero or negative.</exception>
        public Builder WithMaxBlockSize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            _MaxBlockSize = bytes;
            return this;
        }

        /// <summary>
        /// Controls which optional data columns (info, frame bytes, custom representation/text,
        /// topology) are captured. Defaults to <see cref="ColumnarDetailFlags.All"/>.
        /// </summary>
        public Builder WithDetailFlags(ColumnarDetailFlags flags)
        {
            _ColumnarDetailFlags = flags;
            return this;
        }

        /// <summary>
        /// Declares that packets will be added in non-decreasing timestamp order. Metadata only;
        /// accepted so callers can configure the shared <see cref="ColumnarPacketBatch"/> knobs
        /// identically across the PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
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

        /// <summary>Builds the exporter. Throws if no output file was configured.</summary>
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
                _ColumnarDetailFlags,
                _IsTimestampSorted,
                _MemoryLimitBytes,
                _Threads,
                _CancellationToken);
        }
    }

    #endregion
}
