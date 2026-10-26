// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// PBF packet exporter. Writes parsed packets to a PBF (Packet Binary Format) file.
/// <para>
/// File layout: _Magic(44B) + Header + [Blocks...] + Trailer + TrailerSize(4B) + _Magic(44B).
/// Supports standard (row-oriented) and columnar block formats with optional LZ4 compression.
/// Skipped packets are counted in <see cref="IExporterStatistics.SkippedCount"/>.
/// </para>
/// <para>
/// Framing (all integers little-endian): the header is a 4-byte length followed by a protobuf message (format version,
/// creation time). Each block is a 1-byte flags field (bit 0 = LZ4 compressed), the 4-byte uncompressed size, the
/// 4-byte stored size and the stored bytes. The trailer is a protobuf message (packet count, block count, field
/// presence bitmap, optional block index of per-block timestamp ranges) followed by its own 4-byte size, so a reader
/// can find it by reading backwards from the closing magic. Magic at both ends lets a reader reject truncated files.
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
/// <b>Field-ID limit:</b> Field IDs <c>0</c> to <c>32767</c> are tracked in the per-block presence bitmap and the
/// trailer's global bitmap. In the <see cref="PbfExportFormat.Standard"/> format a field with a larger ID is not
/// exported: the packet is rejected with a serialization error and counted as skipped. In the
/// <see cref="PbfExportFormat.Columnar"/> format such fields are written normally but are absent from the bitmaps.
/// </para>
/// </summary>
public sealed class PbfExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    /// <summary>44-byte PBF magic header/footer.</summary>
    private static ReadOnlySpan<byte> _Magic => "NETWORK-INSPECTOR-PBF-FORMAT-v1\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"u8;

    private readonly CancellationToken _CancellationToken;
    private readonly PbfExportFormat _Format;
    private readonly bool _Compressed;
    private readonly int _MaxPacketsPerBlock;
    private readonly long _MaxBlockSize;
    private readonly bool _IncludeTrailerIndex;
    private readonly int _TargetPacketCount;
    private readonly ColumnarDetailFlags _ColumnarDetailFlags;
    private readonly bool _IsTimestampSorted;

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
    /// trailer bitmap. Bitmap byte size = <c>(_MaxFieldId + 7) / 8</c>. Choosing a power
    /// of two keeps both bitmaps aligned and avoids partial-byte arithmetic.
    /// <para>
    /// Limit: IDs <c>0</c> to <c>32767</c> (exclusive bound 32 768, a 4 KiB bitmap). Threat: unbounded memory for the
    /// bitmaps and the trailer if IDs were unbounded. Constant, not configurable.
    /// Posture: secure by default. Exceeding it is an expected failure in the Standard format (the packet is rejected
    /// with a serialization error); the Columnar format instead omits the ID from the bitmaps without an error
    /// (see the type remarks).
    /// </para>
    /// </summary>
    private const int _MaxFieldId = 32768;
    private readonly byte[] _GlobalFieldBitmap = new byte[_MaxFieldId / 8]; // supports up to _MaxFieldId field IDs

    private bool _HasError;
    private bool _Started;
    private bool _Finished;
    private long _CommittedBytes;

    /// <summary>
    /// Reusable framing buffer for <see cref="_FlushCurrentBlock"/>. Sized with
    /// 64 KiB initial capacity and reset between flushes
    /// instead of allocating a fresh <see cref="PooledBuffer"/> every call; it grows to the largest framed block.
    /// Framing the block (flags, sizes, data) in one buffer lets it reach the stream with a single write.
    /// Returned by the first release step of the finish sequence in <see cref="OnFinish"/>.
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
        int targetPacketCount,
        ColumnarDetailFlags columnarDetailFlags,
        bool isTimestampSorted,
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
        _ColumnarDetailFlags = columnarDetailFlags;
        _IsTimestampSorted = isTimestampSorted;
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

    /// <summary>
    /// Number of packets accepted so far. A packet counts as soon as it is added to the current block, which may
    /// still be buffered in memory and not yet written to the output.
    /// </summary>
    public int PacketCount
    {
        get; private set;
    }

    /// <summary>Number of blocks written so far.</summary>
    public int BlockCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public int WrittenCount => PacketCount;

    /// <inheritdoc/>
    /// <remarks>
    /// Bytes already written plus the pending (uncompressed) content of the open block; an estimate that ignores
    /// compression of the pending block and the trailer. Computed from in-memory counters, no file-system probe.
    /// </remarks>
    public long EstimatedOutputBytes
    {
        get
        {
            long pending = 0L;
            if (_Format == PbfExportFormat.Columnar)
            {
                pending = _ColumnarBuilder?.EstimatedPendingBytes ?? 0L;
            }
            else
            {
                pending = _StandardBuilder?.CurrentSize ?? 0L;
            }

            return _CommittedBytes + pending;
        }
    }

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
        // OnFinish the builders are disposed, so a late packet must never reach them.
        if (_CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (_HasError || _Finished)
        {
            return false;
        }

        // Lazy start: the output stream (and with it the file) is created only when the first packet arrives.
        if (!_Started && !_Start())
        {
            return false;
        }

        return _HandlePacket(packet);
    }

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the file. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (_Finished)
        {
            return;
        }
        _Finished = true;

        // Shared finish sequence: finalize, then release every resource even when a step fails.
        ExporterFinish.Run(
            "PBF",
            _FinalizeFile,
            _ReportFinishError,
            new ExporterReleaseStep("block buffer return", _BlockBuf.Return),
            new ExporterReleaseStep("columnar builder disposal", _DisposeColumnarBuilder),
            new ExporterReleaseStep("standard builder disposal", _DisposeStandardBuilder),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <summary>Flushes the last block, writes the trailer, and flushes the stream (starting first for an empty export).</summary>
    private void _FinalizeFile()
    {
        // An export without packets still produces a valid, empty PBF file (magic, header, trailer, magic), as the
        // exporter guide requires for file formats; this is why the start is forced here.
        if (!_Started && _Output is not null)
        {
            _Start();
        }

        // Last (possibly partial) block, then the trailer. The order matters: the trailer's counts and block index
        // include the block flushed here.
        _FlushCurrentBlock();
        _WriteTrailer();
        _DirectStream?.Flush();
    }

    /// <summary>Disposes the columnar block builder once.</summary>
    private void _DisposeColumnarBuilder()
    {
        ColumnarBlockBuilder? builder = _ColumnarBuilder;
        _ColumnarBuilder = null;
        builder?.Dispose();
    }

    /// <summary>Disposes the standard block builder once.</summary>
    private void _DisposeStandardBuilder()
    {
        StandardBlockBuilder? builder = _StandardBuilder;
        _StandardBuilder = null;
        builder?.Dispose();
    }

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        _DirectStream = null;
        output?.Dispose();
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

    // ========================================================================
    // Private implementation
    // ========================================================================

    /// <summary>Lazily initializes output and writes magic + header.</summary>
    /// <returns><see langword="true"/> when the output is ready; <see langword="false"/> after recording an error.</returns>
    private bool _Start()
    {
        // Output already released (for example by a finished exporter) or never configured: nothing can be written.
        // Note that this path sets the error flag without raising ItemSkipped or incrementing ErrorCount, so a late call on
        // a disposed exporter does not start an error loop.
        if (_Output is null)
        {
            _HasError = true;
            return false;
        }

        _Started = true;

        // Creates the file or opens stdout on demand, so nothing is touched before the first packet. An open failure (bad
        // path, missing directory, access denied) or a missing stream goes through the normal error path instead of
        // escaping from OnPacket.
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? underlyingStream, out string openFailure))
        {
            _ReportStartFailure(ExportErrorKind.IoError, $"PBF output could not be opened: {openFailure}");
            return false;
        }

        // Initialize block builder. The per-block bitmap capacity must match the global
        // bitmap capacity (_GlobalFieldBitmap = 4096 bytes = 32768 bits) so that field IDs
        // greater than 4095 are tracked in both per-block presence and the merged trailer
        // bitmap. A smaller per-block bitmap would silently lose the presence information of every field ID above
        // its range when the blocks are merged into the trailer.
        int maxFieldId = _MaxFieldId;
        if (_Format == PbfExportFormat.Columnar)
        {
            _ColumnarBuilder = new ColumnarBlockBuilder(
                maxFieldId, _MaxPacketsPerBlock, _MaxBlockSize, _ColumnarDetailFlags, _IsTimestampSorted);
        }
        else
        {
            _StandardBuilder = new StandardBlockBuilder(maxFieldId, _MaxPacketsPerBlock, _MaxBlockSize);
        }

        try
        {
            // _Magic + header writes go straight to the underlying stream and may
            // throw on a broken target — surface as an export error rather than
            // bubbling out to the caller.
            _DirectStream = underlyingStream;
            _WriteDirect(_Magic);
            _WriteHeader();
        }
        catch (Exception ex)
        {
            _ReportStartFailure(
                ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                $"PBF header write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Latches the fatal state for a failed start, counts it, and raises <see cref="ItemSkipped"/> as item 0.</summary>
    private void _ReportStartFailure(ExportErrorKind kind, string message)
    {
        _HasError = true;
        if (ErrorCount < int.MaxValue)
        {
            ErrorCount++;
        }

        ItemSkipped?.Invoke(this, new ExportErrorEventArgs
        {
            ItemIndex = 0,
            Kind = kind,
            Message = message,
        });
    }

    /// <summary>Writes the PBF file header. Encodes the two fields directly into a stack-allocated
    /// buffer to avoid a pool rent+return for this single call-site.</summary>
    /// <remarks>
    /// The header is written once per file, so the hand-rolled encoding (which duplicates the varint loop of
    /// <see cref="ProtobufEncoder"/>) trades a little duplication for not needing a <see cref="PooledBuffer"/> here.
    /// The creation time has millisecond resolution, stored as nanoseconds since the Unix epoch.
    /// </remarks>
    private void _WriteHeader()
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
        _WriteDirect(lengthPrefix);
        _WriteDirect(data[..pos]);
    }

    /// <summary>
    /// Writes bytes to the direct stream and updates <see cref="_CommittedBytes"/>. Writes go straight to the output
    /// stream (which the file target buffers with 4 MiB); no second buffer sits in between. A missing stream is a
    /// silent no-op because the failed start has already recorded the error.
    /// </summary>
    private void _WriteDirect(ReadOnlySpan<byte> data)
    {
        if (_DirectStream is null)
        {
            return;
        }

        _DirectStream.Write(data);
        _CommittedBytes += data.Length;
    }

    /// <summary>Handles a single packet: adds to block, flushes if needed.</summary>
    private bool _HandlePacket(Packet packet)
    {
        // Target reached: refuse further packets so the caller stops feeding the exporter.
        if (_TargetPacketCount > 0 && PacketCount >= _TargetPacketCount)
        {
            return false;
        }

        // Skip packets carry no field tree, so there is nothing to serialize; counted as a skipped item.
        if (!packet.HasFieldTree)
        {
            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ExportErrorKind.Other,
                Message = "Skip packets cannot be exported (HasFieldTree is false).",
            });
        }

        bool shouldFlush;
        bool counted = false;

        // Wrap the entire add+flush sequence: AddPacket may throw on a malformed
        // packet, _FlushCurrentBlock may throw on I/O errors. Either case must
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

            // The packet is part of the open block from here on, so it is counted before the block is flushed:
            // _FlushCurrentBlock takes the packets of a block it has to drop (Strict nesting truncation) back out of the
            // count, and the trailer then states only what was written.
            PacketCount++;
            counted = true;
            if (shouldFlush && !_FlushCurrentBlock())
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            // AddPacket failed (nothing was added) or the flush failed (the packet stays in the open block but was not
            // written): either way this packet is not counted as written.
            if (counted)
            {
                PacketCount--;
            }

            return _HandleSkip(new ExportErrorEventArgs
            {
                ItemIndex = PacketCount,
                Kind = ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                Message = $"PBF packet write failed: {ex.Message}",
            });
        }

        return true;
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

        // Tolerant mode: fire event and continue
        ItemSkipped?.Invoke(this, error);
        return true;
    }

    /// <summary>Flushes the current block to output.</summary>
    /// <returns>
    /// <see langword="true"/> when the block was written or there was nothing to write; <see langword="false"/> when the
    /// block was dropped because its field tree was cut at the nesting limit and the error tolerance is Strict (the export
    /// has stopped then, and the dropped packets are counted as skipped instead of written).
    /// </returns>
    private bool _FlushCurrentBlock()
    {
        ReadOnlySpan<byte> blockData;
        long minTs = 0, maxTs = 0;

        // Phase 1: serialize the open block through the builder of the configured format and collect the block's
        // timestamp range and field presence for the trailer. An empty block writes nothing (no empty blocks in the file).
        if (_Format == PbfExportFormat.Columnar && _ColumnarBuilder is not null)
        {
            if (_ColumnarBuilder.PacketCount == 0)
            {
                return true;
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
                return true;
            }
            blockData = _StandardBuilder.Build();
            minTs = _StandardBuilder.MinTimestamp;
            maxTs = _StandardBuilder.MaxTimestamp;
            // Report any fields that were silently dropped due to the nesting depth limit.
            int truncated = _StandardBuilder.TruncatedFieldCount;
            if (truncated > 0)
            {
                if (!_HandleSkip(new ExportErrorEventArgs
                {
                    Kind = ExportErrorKind.MalformedData,
                    Message = $"{truncated} field subtree(s) were dropped because the protocol tree exceeded the maximum nesting depth"
                        + $" ({truncated} root(s) at depth >= {StandardBlockBuilder.MaxNestingDepth})."
                        + " The block was written with incomplete field data."
                }))
                {
                    // Strict: the block is not written. Its packets were counted as written when they were accepted, but
                    // none of them is in the file, so the count (and with it the trailer) must not include them. Dropping
                    // the block also keeps OnFinish from judging and skipping the same block a second time. _HandleSkip
                    // counted one skipped item already; the other packets of the block are added here.
                    int dropped = _StandardBuilder.PacketCount;
                    _StandardBuilder.Reset();
                    PacketCount -= dropped;
                    int extraSkipped = dropped - 1;
                    SkippedCount = SkippedCount > int.MaxValue - extraSkipped
                        ? int.MaxValue
                        : SkippedCount + extraSkipped;
                    return false;
                }
            }
            // Merge field presence.
            ReadOnlySpan<byte> presenceBytes = _StandardBuilder.FieldPresenceBytes;
            for (int i = 0; i < presenceBytes.Length && i < _GlobalFieldBitmap.Length; i++)
            {
                _GlobalFieldBitmap[i] |= presenceBytes[i];
            }
        }
        else
        {
            return true;
        }

        // Phase 2: optional LZ4 compression of the whole block (one compression call per block; the codec clears a
        // 256 KB hash table at the start of every call, which this amortizes over the whole block). Blocks of 64 bytes or less are stored
        // uncompressed without trying (documented limit; the reason for this exact value is not recorded).
        ReadOnlySpan<byte> outputData;
        byte[]? compressedBuf = null;
        bool isCompressed = false;

        if (_Compressed && blockData.Length > 64)
        {
            // The compressor needs room for its worst case (MaxCompressedSize), which is larger than the input for
            // incompressible data. The array comes from the pool because blocks can be megabytes, which would
            // otherwise land on the large object heap for every block. The span is cut to exactly the worst-case
            // size because the pool may return a longer array than requested.
            // MaxCompressedSize is -1 for an input above the LZ4 block limit: renting that would throw, so such a block
            // is stored uncompressed like one that does not shrink (the stored size is the block size either way).
            int maxCompressed = Lz4Compressor.MaxCompressedSize(blockData.Length);
            int compressedLen = -1;
            if (maxCompressed > 0)
            {
                compressedBuf = ArrayPool<byte>.Shared.Rent(maxCompressed);
                compressedLen = Lz4Compressor.Compress(blockData, compressedBuf.AsSpan(0, maxCompressed));
            }

            if (compressedBuf is not null && compressedLen > 0 && compressedLen < blockData.Length)
            {
                outputData = compressedBuf.AsSpan(0, compressedLen);
                isCompressed = true;
            }
            else
            {
                // LZ4 output is not smaller than the original (or the block is above the LZ4 limit); fall back to storing
                // the block uncompressed. flags remains 0 (uncompressed), which is
                // correct: the reader determines the storage format from flags alone,
                // not from comparing the two size fields. Both size fields will carry
                // the same value (blockData.Length) in the written header.
                outputData = blockData;
                if (compressedBuf is not null)
                {
                    ArrayPool<byte>.Shared.Return(compressedBuf);
                    compressedBuf = null;
                }
            }
        }
        else
        {
            outputData = blockData;
        }

        // Phase 3: frame the block and write it. Both sizes are always stored (equal when uncompressed) so a reader
        // can size its decompression buffer before touching the data and can skip a block by its stored size.
        // Write block: [1-byte flags] + [4-byte original size LE] + [4-byte compressed size LE] + [data]
        // Flags: bit 0 = compressed. Sizes are int32 (the block buffers are int-indexed, so a block cannot exceed
        // 2 GiB; a larger WithMaxBlockSize cannot take effect).
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

        // Send block to output (_DirectStream guaranteed non-null after _Start())
        _WriteDirect(blockBuf.WrittenSpan);
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

        return true;
    }

    /// <summary>Writes the trailer, trailer size, and closing magic.</summary>
    private void _WriteTrailer()
    {
        // Guard against a failed _Start() leaving _DirectStream null; the outer
        // try/catch in OnFinish will have already recorded the error.
        if (_DirectStream is null)
        {
            return;
        }

        // The trailer is built in a pooled buffer because its 4-byte size follows the content and readers locate the
        // trailer by reading that size backwards from the closing magic. It is written once per file, so the
        // per-entry buffers below are not on a hot path.
        PooledBuffer trailer = new(256);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerPacketCount,
            (ulong)PacketCount);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerBlockCount, (ulong)BlockCount);

        // Field presence bitmap: only the prefix up to the last non-zero byte is stored, so a file whose fields all
        // have low IDs does not carry the full 4 KiB. The bitmap is omitted when no field was ever present.
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

        // Block index (optional): one entry per block with its timestamp range, so a reader can skip blocks by time
        // without decoding them.
        if (_IncludeTrailerIndex)
        {
            foreach (BlockIndexEntry entry in _BlockIndex)
            {
                PooledBuffer indexEntry = new(32);
                ProtobufEncoder.WriteSint64(ref indexEntry, PbfFieldNumbers.TrailerBlockIndexMinTimestamp, entry.MinTimestamp);
                ProtobufEncoder.WriteSint64(ref indexEntry, PbfFieldNumbers.TrailerBlockIndexMaxTimestamp, entry.MaxTimestamp);
                ProtobufEncoder.WriteLengthDelimited(ref trailer, PbfFieldNumbers.TrailerBlockIndex, indexEntry.WrittenSpan);
                indexEntry.Return();
            }
        }

        // Write: [trailer data] + [4-byte trailer size LE] + [magic]
        Span<byte> trailerSizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(trailerSizeBytes, trailer.Length);

        // _DirectStream is guaranteed non-null after _Start() succeeds
        _WriteDirect(trailer.WrittenSpan);
        trailer.Return();
        _WriteDirect(trailerSizeBytes);
        _WriteDirect(_Magic);
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
        private int _TargetPacketCount;
        private ColumnarDetailFlags _ColumnarDetailFlags = ColumnarDetailFlags.All;
        private bool _IsTimestampSorted;

        /// <summary>Sets the output to a file path with a 4 MiB buffer.</summary>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream. The caller keeps ownership: the exporter flushes the stream when
        /// it finishes but does not close it.
        /// </summary>
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

        /// <summary>
        /// Sets the maximum number of packets per block before auto-flush. Together with
        /// <see cref="WithMaxBlockSize"/> this bounds the memory of the open block; whichever limit is reached first
        /// triggers the flush. Default 50 000.
        /// </summary>
        /// <param name="count">Packets per block; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero or negative.</exception>
        public Builder WithMaxPacketsPerBlock(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            _MaxPacketsPerBlock = count;
            return this;
        }

        /// <summary>
        /// Sets the maximum block size in bytes before auto-flush. The Standard format compares against the
        /// serialized size, the Columnar format against the batch's in-memory estimate. Default 16 MiB.
        /// </summary>
        /// <param name="bytes">Bytes per block; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is zero or negative.</exception>
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
        /// <param name="count">Packet limit; 0 means unlimited.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="count"/> is negative or exceeds <see cref="ArrayIndexIdRange.MaxCount"/>.
        /// </exception>
        public Builder WithTargetPacketCount(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            // The upper limit is the number of items an array-index ID can address (ArrayIndexIdRange.MaxCount);
            // packet IDs are non-negative int32 values.
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

        /// <summary>Sets an optional description.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>
        /// Sets the cancellation token for cooperative shutdown.
        /// </summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>
        /// Controls which optional data columns (info, frame bytes, custom representation/text,
        /// topology) are captured by <see cref="PbfExportFormat.Columnar"/> blocks. Ignored for
        /// <see cref="PbfExportFormat.Standard"/>, which always emits the full row-oriented tree.
        /// Defaults to <see cref="ColumnarDetailFlags.All"/>.
        /// </summary>
        public Builder WithDetailFlags(ColumnarDetailFlags flags)
        {
            _ColumnarDetailFlags = flags;
            return this;
        }

        /// <summary>
        /// Declares that packets will be added in non-decreasing timestamp order for
        /// <see cref="PbfExportFormat.Columnar"/> blocks. This is metadata only in the current
        /// PBF writer (it does not change the emitted bytes); it is accepted here so callers can
        /// configure the shared <see cref="ColumnarPacketBatch"/> knobs identically across the
        /// PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
        public Builder WithTimestampSorted(bool sorted)
        {
            _IsTimestampSorted = sorted;
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
                _ColumnarDetailFlags,
                _IsTimestampSorted,
                _CancellationToken);
        }
    }
}
