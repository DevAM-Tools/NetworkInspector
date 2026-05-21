// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// PBF packet exporter. Writes parsed packets to a PBF (Packet Binary Format) file.
/// <para>
/// File layout: Magic(44B) + Header + [Blocks...] + Trailer + TrailerSize(4B) + Magic(44B).
/// Supports standard (row-oriented) and columnar block formats with optional LZ4 compression.
/// Skipped packets are counted in <see cref="IExporterStatistics.SkippedCount"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. Callers are responsible for synchronization
/// if used from multiple threads. Statistics are valid to read after <see cref="OnFinish"/> returns.
/// </para>
/// <para>
/// <b>Single-use:</b> Once <see cref="OnFinish"/> (or <see cref="Dispose"/>) is called,
/// this instance is finalized and cannot be reused. Subsequent calls to <see cref="OnPacket"/>
/// after <see cref="OnFinish"/> are silently ignored.
/// </para>
/// <para>
/// <b>Field-ID limit:</b> Per-block field-presence deduplication tracks up to 32 768 distinct
/// field IDs. Field IDs beyond this limit are serialized correctly but their name/type metadata
/// is emitted in every block rather than being deduplicated.
/// </para>
/// </summary>
public sealed class PbfExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    /// <summary>44-byte PBF magic header/footer.</summary>
    private static ReadOnlySpan<byte> Magic => "NETWORK-INSPECTOR-PBF-FORMAT-v1\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"u8;

    private readonly CancellationToken _CancellationToken;
    private readonly PbfExportFormat _Format;
    private readonly bool _Compressed;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize;
    private readonly bool _IncludeTrailerIndex;
    private readonly long _TargetPacketCount;

    // Output target consumed on lazy init
    private ExportOutput? _Output;
    // Non-owning stream reference for direct writes; _Output owns the underlying stream
    // and disposes it. We must not dispose this reference directly — CA2213 suppressed.
    [SuppressMessage("Design", "CA2213:Disposable fields should be disposed",
        Justification = "_DirectStream is a non-owning reference to _Output's stream; _Output.Dispose() handles cleanup.")]
    private Stream? _DirectStream;

    // Block builders
    private StandardBlockBuilder? _StandardBuilder;
    private ColumnarBlockBuilder? _ColumnarBuilder;

    // Trailer tracking
    private readonly List<BlockIndexEntry> _BlockIndex = new(64);
    /// <summary>
    /// Maximum field ID supported by both the per-block presence bitmap and the global
    /// trailer bitmap. Bitmap byte size = <c>(MaxFieldId + 7) / 8</c>. Choosing a power
    /// of two keeps both bitmaps aligned and avoids partial-byte arithmetic.
    /// </summary>
    private const int MaxFieldId = 32768;
    private readonly byte[] _GlobalFieldBitmap = new byte[MaxFieldId / 8]; // supports up to MaxFieldId field IDs

    private bool _HasError;
    private bool _Started;
    private bool _Finished;

    /// <summary>
    /// Reusable framing buffer for <see cref="FlushCurrentBlock"/>. Sized with
    /// 64 KiB initial capacity (typical block size) and reset between flushes
    /// instead of allocating a fresh <see cref="PooledBuffer"/> every call.
    /// Returned in <see cref="OnFinish"/>'s finally.
    /// </summary>
    private readonly PooledBuffer _BlockBuf = new(64 * 1024);

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    private PbfExporter(
        ExportOutput output,
        string uiName,
        string? description,
        PbfExportFormat format,
        bool compressed,
        int maxPacketsPerBlock,
        long maxBlockSize,
        bool includeTrailerIndex,
        long targetPacketCount,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _Format = format;
        _Compressed = compressed;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        _IncludeTrailerIndex = includeTrailerIndex;
        _TargetPacketCount = targetPacketCount;
        _CancellationToken = cancellationToken;
    }

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
    public long PacketCount
    {
        get; private set;
    }

    /// <summary>Number of blocks written so far.</summary>
    public int BlockCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public long WrittenCount => PacketCount;

    /// <inheritdoc/>
    public long SkippedCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public long ErrorCount
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
    bool IExporterStatistics.IsFinished => IsFinished;

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

        if (!_Started && !Start())
        {
            return false;
        }

        return HandlePacket(packet);
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;

        // try/finally guarantees the underlying output is disposed even if
        // the final block flush or trailer write throws. cleanupErrors is declared
        // here so the throw can occur after the finally block — CA2219 prohibits
        // throwing inside finally.
        List<Exception> cleanupErrors = [];
        try
        {
            if (!_Started && _Output is not null)
            {
                Start();
            }

            // Flush remaining block
            FlushCurrentBlock();

            // Write trailer
            WriteTrailer();

            _DirectStream?.Flush();
        }
        catch (Exception ex)
        {
            _HasError = true;
            ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"PBF finalization failed: {ex.Message}",
            });
        }
        finally
        {
            // Each cleanup step is independent so a failure in one step does not
            // prevent remaining resources from being released. Failures are surfaced
            // via the error channel. cleanupErrors is declared before the try so the
            // throw can occur after the finally block — CA2219 prohibits throwing inside finally.
            try
            {
                _BlockBuf.Return();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"PBF block buffer return failed: {ex.Message}",
                });
            }
            try
            {
                _ColumnarBuilder?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"PBF columnar builder disposal failed: {ex.Message}",
                });
            }
            _ColumnarBuilder = null;
            try
            {
                _StandardBuilder?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"PBF standard builder disposal failed: {ex.Message}",
                });
            }
            _StandardBuilder = null;
            try
            {
                _Output?.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
                _HasError = true;
                ErrorCount++;
                ItemSkipped?.Invoke(this, new ExportErrorEventArgs
                {
                    ItemIndex = PacketCount,
                    Kind = ExportErrorKind.IoError,
                    Message = $"PBF output disposal failed: {ex.Message}",
                });
            }
            _Output = null;
            _DirectStream = null;
        }
        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException("PBF exporter cleanup failed.", cleanupErrors);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    // ========================================================================
    // Private implementation
    // ========================================================================

    /// <summary>Lazily initializes output and writes magic + header.</summary>
    private bool Start()
    {
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        Stream? underlyingStream = _Output.GetOrCreateUnderlyingStream();
        if (underlyingStream is null)
        {
            _HasError = true;
            return false;
        }

        // Initialize block builder. The per-block bitmap capacity must match the global
        // bitmap capacity (_GlobalFieldBitmap = 4096 bytes = 32768 bits) so that field IDs
        // greater than 4095 are tracked in both per-block presence and the merged trailer
        // bitmap. Previously this was 4096 bits, causing silent loss of presence info for
        // any field ID >= 4096 (review B7).
        int maxFieldId = MaxFieldId;
        if (_Format == PbfExportFormat.Columnar)
        {
            _ColumnarBuilder = new ColumnarBlockBuilder(maxFieldId, _MaxPacketsPerBlock, _MaxBlockSize);
        }
        else
        {
            _StandardBuilder = new StandardBlockBuilder(maxFieldId, _MaxPacketsPerBlock, _MaxBlockSize);
        }

        try
        {
            // Magic + header writes go straight to the underlying stream and may
            // throw on a broken target — surface as an export error rather than
            // bubbling out to the caller.
            _DirectStream = underlyingStream;
            _DirectStream.Write(Magic);
            WriteHeader(_DirectStream);
        }
        catch (Exception ex)
        {
            _HasError = true;
            ErrorCount++;
            ItemSkipped?.Invoke(this, new ExportErrorEventArgs
            {
                ItemIndex = 0,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"PBF header write failed: {ex.Message}",
            });
            return false;
        }

        return true;
    }

    /// <summary>Writes the PBF file header. Encodes the two fields directly into a stack-allocated
    /// buffer to avoid a pool rent+return for this single call-site.</summary>
    private static void WriteHeader(Stream stream)
    {
        // Encode the two-field protobuf header message directly into a stack-allocated buffer.
        // Maximum encoded size: 2 bytes (version varint field) + 11 bytes (sint64 timestamp field) = 13 bytes.
        // 32 bytes is ample headroom.
        Span<byte> data = stackalloc byte[32];
        int pos = 0;

        // Field 1 (HeaderVersion = 1): tag = (1 << 3) | 0 = 0x08, value = 0x01
        data[pos++] = (byte)((PbfFieldNumbers.HeaderVersion << 3) | 0);
        data[pos++] = 1;

        // Field 2 (HeaderCreationTimestamp): tag = (2 << 3) | 0 = 0x10, zigzag-encoded sint64
        data[pos++] = (byte)((PbfFieldNumbers.HeaderCreationTimestamp << 3) | 0);
        ulong zigzag = ProtobufEncoder.ZigZagEncode(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        while (zigzag > 0x7F)
        {
            data[pos++] = (byte)(zigzag | 0x80);
            zigzag >>= 7;
        }
        data[pos++] = (byte)zigzag;

        // Write header as length-prefixed: [4-byte length LE] + [protobuf data]
        Span<byte> lengthPrefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, pos);
        stream.Write(lengthPrefix);
        stream.Write(data[..pos]);
    }

    /// <summary>Handles a single packet: adds to block, flushes if needed.</summary>
    private bool HandlePacket(Packet packet)
    {
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        bool shouldFlush;

        // Wrap the entire add+flush sequence: AddPacket may throw on a malformed
        // packet, FlushCurrentBlock may throw on I/O errors. Either case must
        // degrade to a skipped packet rather than tearing down the pipeline.
        try
        {
            if (_Format == PbfExportFormat.Columnar)
            {
                shouldFlush = _ColumnarBuilder!.AddPacket(packet);
            }
            else
            {
                shouldFlush = _StandardBuilder!.AddPacket(packet);
            }

            if (shouldFlush)
            {
                FlushCurrentBlock();
            }
        }
        catch (Exception ex)
        {
            return HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"PBF packet write failed: {ex.Message}",
            });
        }

        PacketCount++;

        return true;
    }

    /// <summary>
    /// Handles a skipped packet: increments counters, fires the event in Tolerant mode,
    /// and returns false to abort in Strict mode.
    /// </summary>
    private bool HandleSkip(ExportErrorEventArgs error)
    {
        SkippedCount++;
        ErrorCount++;

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _HasError = true;
            return false;
        }

        // Tolerant mode: fire event and continue
        ItemSkipped?.Invoke(this, error);
        return true;
    }

    /// <summary>Flushes the current block to output.</summary>
    private void FlushCurrentBlock()
    {
        ReadOnlySpan<byte> blockData;
        long minTs = 0, maxTs = 0;

        if (_Format == PbfExportFormat.Columnar && _ColumnarBuilder is not null)
        {
            if (_ColumnarBuilder.PacketCount == 0)
            {
                return;
            }
            blockData = _ColumnarBuilder.Build();
            // MinTimestamp/MaxTimestamp are populated by Build() so the trailer block
            // index carries correct time-range metadata for columnar blocks.
            minTs = _ColumnarBuilder.MinTimestamp;
            maxTs = _ColumnarBuilder.MaxTimestamp;
            // Merge field presence
            _ColumnarBuilder.FieldPresence.MergeInto(_GlobalFieldBitmap);
        }
        else if (_StandardBuilder is not null)
        {
            if (_StandardBuilder.PacketCount == 0)
            {
                return;
            }
            blockData = _StandardBuilder.Build();
            minTs = _StandardBuilder.MinTimestamp;
            maxTs = _StandardBuilder.MaxTimestamp;
            // Report any fields that were silently dropped due to the nesting depth limit.
            int truncated = _StandardBuilder.TruncatedFieldCount;
            if (truncated > 0)
            {
                if (!HandleSkip(new ExportErrorEventArgs
                {
                    Kind = ExportErrorKind.MalformedData,
                    Message = $"{truncated} field subtree(s) were dropped because the protocol tree exceeded the maximum nesting depth"
                        + $" ({truncated} root(s) at depth >= 16). The block was written with incomplete field data."
                }))
                {
                    return;
                }
            }
            // Merge field presence
            ReadOnlySpan<byte> presenceBytes = _StandardBuilder.FieldPresenceBytes;
            for (int i = 0; i < presenceBytes.Length && i < _GlobalFieldBitmap.Length; i++)
            {
                _GlobalFieldBitmap[i] |= presenceBytes[i];
            }
        }
        else
        {
            return;
        }

        // Optional LZ4 compression
        ReadOnlySpan<byte> outputData;
        byte[]? compressedBuf = null;
        bool isCompressed = false;

        if (_Compressed && blockData.Length > 64)
        {
            int maxCompressed = Lz4Compressor.MaxCompressedSize(blockData.Length);
            compressedBuf = ArrayPool<byte>.Shared.Rent(maxCompressed);
            int compressedLen = Lz4Compressor.Compress(blockData, compressedBuf.AsSpan(0, maxCompressed));
            if (compressedLen > 0 && compressedLen < blockData.Length)
            {
                outputData = compressedBuf.AsSpan(0, compressedLen);
                isCompressed = true;
            }
            else
            {
                // LZ4 output is not smaller than the original; fall back to storing
                // the block uncompressed. flags remains 0 (uncompressed), which is
                // correct: the reader determines the storage format from flags alone,
                // not from comparing the two size fields. Both size fields will carry
                // the same value (blockData.Length) in the written header.
                outputData = blockData;
                ArrayPool<byte>.Shared.Return(compressedBuf);
                compressedBuf = null;
            }
        }
        else
        {
            outputData = blockData;
        }

        // Write block: [1-byte flags] + [4-byte original size LE] + [4-byte compressed size LE] + [data]
        // Flags: bit 0 = compressed
        byte flags = isCompressed ? (byte)1 : (byte)0;

        PooledBuffer blockBuf = _BlockBuf;
        blockBuf.Reset();
        blockBuf.WriteByte(flags);
        Span<byte> sizes = blockBuf.Reserve(8);
        BinaryPrimitives.WriteInt32LittleEndian(sizes, blockData.Length); // original size
        BinaryPrimitives.WriteInt32LittleEndian(sizes[4..], outputData.Length); // stored size
        blockBuf.Write(outputData);

        if (compressedBuf is not null)
        {
            ArrayPool<byte>.Shared.Return(compressedBuf);
        }

        // Send block to output (_DirectStream guaranteed non-null after Start())
        _DirectStream!.Write(blockBuf.WrittenSpan);
        blockBuf.Reset();

        // Commit the block-index entry only after the bytes have been successfully
        // written. If Write() throws, no phantom index entry is left behind.
        _BlockIndex.Add(new BlockIndexEntry(minTs, maxTs));

        BlockCount++;

        // Reset builder for next block
        if (_Format == PbfExportFormat.Columnar)
        {
            _ColumnarBuilder!.Reset();
        }
        else
        {
            _StandardBuilder!.Reset();
        }
    }

    /// <summary>Writes the trailer, trailer size, and closing magic.</summary>
    private void WriteTrailer()
    {
        // Guard against a failed Start() leaving _DirectStream null; the outer
        // try/catch in OnFinish will have already recorded the error.
        if (_DirectStream is null)
        {
            return;
        }

        PooledBuffer trailer = new(256);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerPacketCount,
            (ulong)PacketCount);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerBlockCount, (ulong)BlockCount);

        // Field presence bitmap
        int usedBitmapLen = 0;
        for (int i = _GlobalFieldBitmap.Length - 1; i >= 0; i--)
        {
            if (_GlobalFieldBitmap[i] != 0)
            {
                usedBitmapLen = i + 1;
                break;
            }
        }
        if (usedBitmapLen > 0)
        {
            ProtobufEncoder.WriteLengthDelimited(ref trailer, PbfFieldNumbers.TrailerFieldBitmap,
                _GlobalFieldBitmap.AsSpan(0, usedBitmapLen));
        }

        // Block index (optional)
        if (_IncludeTrailerIndex)
        {
            foreach (BlockIndexEntry entry in _BlockIndex)
            {
                PooledBuffer indexEntry = new(32);
                ProtobufEncoder.WriteSint64(ref indexEntry, 1, entry.MinTimestamp);
                ProtobufEncoder.WriteSint64(ref indexEntry, 2, entry.MaxTimestamp);
                ProtobufEncoder.WriteLengthDelimited(ref trailer, 4, indexEntry.WrittenSpan);
                indexEntry.Return();
            }
        }

        // Write: [trailer data] + [4-byte trailer size LE] + [magic]
        Span<byte> trailerSizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(trailerSizeBytes, trailer.Length);

        // _DirectStream is guaranteed non-null after Start() succeeds
        _DirectStream!.Write(trailer.WrittenSpan);
        trailer.Return();
        _DirectStream.Write(trailerSizeBytes);
        _DirectStream.Write(Magic);
    }

    // ========================================================================
    // Block index entry
    // ========================================================================

    /// <summary>Stores timestamp range for a single block.</summary>
    private readonly record struct BlockIndexEntry(long MinTimestamp, long MaxTimestamp);

    // ========================================================================
    // Builder
    // ========================================================================

    /// <summary>Fluent builder for constructing a <see cref="PbfExporter"/>.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "PBF Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private PbfExportFormat _Format = PbfExportFormat.Standard;
        private bool _Compressed = true;
        private int _MaxPacketsPerBlock = 50000;
        private long _MaxBlockSize = 16 * 1024 * 1024;
        private bool _IncludeTrailerIndex = true;
        private long _TargetPacketCount;

        /// <summary>Sets the output to a file path with a 4 MiB buffer.</summary>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>Sets the output to an existing stream.</summary>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Sets the output to stdout.</summary>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        /// <summary>Sets the user-friendly display name shown in UI and logs.</summary>
        public Builder WithUiName(string name)
        {
            _UiName = name;
            return this;
        }

        /// <summary>Sets the PBF block format.</summary>
        public Builder WithFormat(PbfExportFormat format)
        {
            _Format = format;
            return this;
        }

        /// <summary>Enables or disables LZ4 compression for blocks.</summary>
        public Builder WithCompressed(bool compressed)
        {
            _Compressed = compressed;
            return this;
        }

        /// <summary>Sets the maximum number of packets per block before auto-flush.</summary>
        public Builder WithMaxPacketsPerBlock(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            _MaxPacketsPerBlock = count;
            return this;
        }

        /// <summary>Sets the maximum block size in bytes before auto-flush.</summary>
        public Builder WithMaxBlockSize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            _MaxBlockSize = bytes;
            return this;
        }

        /// <summary>Includes a block index in the trailer for random access.</summary>
        public Builder WithTrailerIndex(bool include)
        {
            _IncludeTrailerIndex = include;
            return this;
        }

        /// <summary>
        /// Stops after writing the specified number of packets. <c>0</c> means unlimited (default).
        /// When the target is reached, <see cref="OnPacket"/> returns <c>false</c> and
        /// <see cref="IsFinished"/> becomes <c>true</c>.
        /// </summary>
        public Builder WithTargetPacketCount(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _TargetPacketCount = count;
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

        /// <summary>Builds the exporter. Throws if no output target was configured.</summary>
        /// <exception cref="InvalidOperationException">No output target was configured.</exception>
        public PbfExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException("No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            return new PbfExporter(
                _Output,
                _UiName,
                _Description,
                _Format,
                _Compressed,
                _MaxPacketsPerBlock,
                _MaxBlockSize,
                _IncludeTrailerIndex,
                _TargetPacketCount,
                _CancellationToken);
        }
    }
}
