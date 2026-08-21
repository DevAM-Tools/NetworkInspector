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

    private ColumnarPacketBatch? _Batch;
    private DuckDbBulkWriter? _Writer;
    private long _FlushedOutputBytes;

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

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
        CancellationToken cancellationToken)
    {
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
            _Start();
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
            // No packets were ever added — leave no database file on disk (§7.5).
            return;
        }

        // try/finally guarantees the writer and batch are disposed even if the final flush or
        // Complete() throws. cleanupErrors is declared here so the throw can occur after the
        // finally block — CA2219 prohibits throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            if (_Batch is not null && _Batch.PacketCount > 0)
            {
                _FlushBatch();
            }
            _Writer?.Complete();
        }
        catch (Exception ex)
        {
            _HasError = true;
            if (ErrorCount < int.MaxValue) ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"DuckDB finalization failed: {ex.Message}",
            });
        }
        finally
        {
            try
            {
                _Writer?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                if (ErrorCount < int.MaxValue) ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"DuckDB writer disposal failed: {ex.Message}",
                });
            }
            _Writer = null;
            _Batch?.Dispose();
            _Batch = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("DuckDB exporter cleanup failed.", cleanupErrors);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    #endregion

    #region Private Helpers

    /// <summary>Lazily opens the DuckDB file, creates the base tables, and creates the batch accumulator.</summary>
    private void _Start()
    {
        _Started = true;
        try
        {
            _Writer = new DuckDbBulkWriter(_FilePath, _ColumnarDetailFlags);
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
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
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
                Message = $"DuckDB packet write failed: {ex.Message}",
            });
        }

        PacketCount++;
        return true;
    }

    /// <summary>
    /// Flushes the current batch and accounts its estimated size toward
    /// <see cref="EstimatedOutputBytes"/> (in-memory only — no filesystem probe).
    /// </summary>
    private void _FlushBatch()
    {
        long batchBytes = _Batch!.EstimatedSizeBytes;
        _Writer!.WriteBatch(_Batch);
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
        private int _MaxPacketsPerBlock = 50000;
        private long _MaxBlockSize = 16 * 1024 * 1024;
        private int _TargetPacketCount;
        private ColumnarDetailFlags _ColumnarDetailFlags = ColumnarDetailFlags.All;
        private bool _IsTimestampSorted;

        /// <summary>
        /// Sets the output <c>.duckdb</c> file path. Required — DuckDB output is always a single
        /// database file, never a directory or stream. The file is created lazily on the first
        /// <see cref="OnPacket"/> call.
        /// </summary>
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
        /// Declares that packets will be added in non-decreasing timestamp order. Metadata only;
        /// accepted so callers can configure the shared <see cref="ColumnarPacketBatch"/> knobs
        /// identically across the PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
        public Builder WithTimestampSorted(bool sorted)
        {
            _IsTimestampSorted = sorted;
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
                _CancellationToken);
        }
    }

    #endregion
}
