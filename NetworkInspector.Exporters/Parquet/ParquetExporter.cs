// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// Parquet packet exporter. Writes parsed packets to a directory of Parquet files forming a
/// small relational dataset: <c>packets.parquet</c>, <c>topology.parquet</c>,
/// <c>catalog.parquet</c>, <c>protocols.parquet</c>, and one <c>fields/field_{id}.parquet</c> file per distinct
/// field ID observed (see <see cref="ParquetBatchSink"/> for the full layout).
/// <para>
/// Shares the <see cref="ColumnarPacketBatch"/> accumulator with the PBF columnar format and
/// the DuckDB exporter, so <see cref="ColumnarDetailFlags"/> is configured identically across
/// all three columnar formats. <c>packet_id</c> columns use Core <see cref="PacketId"/> width
/// (<see cref="int"/> / Parquet INT32).
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. Statistics are valid to read after
/// <see cref="OnFinish"/> returns. Parquet.Net exposes only async writers, so the exporter blocks on them;
/// do not call it from a thread with a <see cref="SynchronizationContext"/> (UI thread).
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
/// </summary>
public sealed class ParquetExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    #region Fields

    private readonly string _RootDirectory;
    private readonly CancellationToken _CancellationToken;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize;
    private readonly int _TargetPacketCount;
    private readonly ColumnarDetailFlags _ColumnarDetailFlags;
    private readonly bool _IsTimestampSorted;

    private ColumnarPacketBatch? _Batch;
    private ParquetBatchSink? _Sink;
    private long _FlushedOutputBytes;

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    #endregion

    #region Constructor

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private ParquetExporter(
        string rootDirectory,
        string uiName,
        string? description,
        int maxPacketsPerBlock,
        long maxBlockSize,
        int targetPacketCount,
        ColumnarDetailFlags columnarDetailFlags,
        bool isTimestampSorted,
        CancellationToken cancellationToken)
    {
        _RootDirectory = rootDirectory;
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

    /// <summary>Number of packets written so far.</summary>
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
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        if (!_Started)
        {
            _Start(packet);
            if (_HasError)
            {
                return false;
            }
        }

        return _HandlePacket(packet);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;

        if (!_Started)
        {
            // No packets were ever added — leave no directory/files on disk (§7.5).
            return;
        }

        // Shared finish sequence: final flush and footers, then sink and batch release even when a step fails.
        ExporterFinish.Run(
            "Parquet",
            _FinalizeFiles,
            _ReportFinishError,
            new ExporterReleaseStep("sink disposal", _DisposeSink),
            new ExporterReleaseStep("batch disposal", _DisposeBatch));
    }

    /// <summary>Flushes the last batch and completes the sink (writes the catalog and closes the row groups).</summary>
    private void _FinalizeFiles()
    {
        if (_Batch is not null && _Batch.PacketCount > 0)
        {
            _FlushBatch();
        }

        _Sink?.Complete();
    }

    /// <summary>Disposes the sink once (closes every table file).</summary>
    private void _DisposeSink()
    {
        ParquetBatchSink? sink = _Sink;
        _Sink = null;
        sink?.Dispose();
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
    /// Lazily creates the output directory, sink, and batch accumulator. The protocol catalog is taken from the first
    /// packet's stack: all packets of one export come from the same parsing stack, so its protocol set is fixed.
    /// </summary>
    /// <param name="firstPacket">The first packet of the export.</param>
    private void _Start(Packet firstPacket)
    {
        _Started = true;
        try
        {
            Directory.CreateDirectory(_RootDirectory);
            _Sink = new ParquetBatchSink(_RootDirectory, _ColumnarDetailFlags, ProtocolCatalogEntry.FromStack(firstPacket.Stack));
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
                Message = $"Parquet output initialization failed: {ex.Message}",
            });
        }
    }

    /// <summary>Adds one packet to the current batch, flushing to the sink if the batch is full.</summary>
    private bool _HandlePacket(Packet packet)
    {
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        if (!packet.HasFieldTree)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ExportErrorKind.Other,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
        }

        try
        {
            bool shouldFlush = _Batch!.AddPacket(packet);
            if (shouldFlush)
            {
                _FlushBatch();
            }
        }
        catch (Exception ex)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"Parquet packet write failed: {ex.Message}",
            });
        }

        PacketCount++;
        return true;
    }

    /// <summary>
    /// Flushes the current batch to the sink and accounts its estimated size toward
    /// <see cref="EstimatedOutputBytes"/> (in-memory only — no filesystem probe).
    /// </summary>
    private void _FlushBatch()
    {
        long batchBytes = _Batch!.EstimatedSizeBytes;
        _Sink!.WriteBatch(_Batch);
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

    /// <summary>Fluent builder for constructing a <see cref="ParquetExporter"/>.</summary>
    public sealed class Builder
    {
        private string? _RootDirectory;
        private string _UiName = "Parquet Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private int _MaxPacketsPerBlock = 50000;
        private long _MaxBlockSize = 16 * 1024 * 1024;
        private int _TargetPacketCount;
        private ColumnarDetailFlags _ColumnarDetailFlags = ColumnarDetailFlags.All;
        private bool _IsTimestampSorted;

        /// <summary>
        /// Sets the output directory. Required — Parquet output is always a directory of files
        /// (see <see cref="ParquetBatchSink"/> for the layout), never a single stream.
        /// The directory is created lazily on the first <see cref="OnPacket"/> call.
        /// </summary>
        public Builder ToDirectory(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _RootDirectory = Path.GetFullPath(path);
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
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
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
        public Builder WithMaxPacketsPerBlock(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            _MaxPacketsPerBlock = count;
            return this;
        }

        /// <summary>Sets the maximum estimated batch size (bytes) before an automatic flush.</summary>
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
        /// Declares that packets will be added in non-decreasing timestamp order. Metadata only
        /// (see <see cref="NetworkInspector.Exporters.Pbf.PbfExporter"/>'s equivalent option);
        /// accepted so callers can configure the shared <see cref="ColumnarPacketBatch"/> knobs
        /// identically across the PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
        public Builder WithTimestampSorted(bool sorted)
        {
            _IsTimestampSorted = sorted;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output directory was configured.</summary>
        /// <exception cref="InvalidOperationException">No output directory was configured.</exception>
        public ParquetExporter Build()
        {
            if (_RootDirectory is null)
            {
                throw new InvalidOperationException("No output directory configured. Call ToDirectory().");
            }

            return new ParquetExporter(
                _RootDirectory,
                _UiName,
                _Description,
                _MaxPacketsPerBlock,
                _MaxBlockSize,
                _TargetPacketCount,
                _ColumnarDetailFlags,
                _IsTimestampSorted,
                _CancellationToken);
        }
    }

    #endregion
}
