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
/// <b>Write mode and streams:</b> in <see cref="ExportWriteMode.Background"/> (default) a writer thread named
/// "PBF export writer" serializes, compresses and writes each full block while the next one fills. Serializing a block and
/// compressing it takes as long as parsing thousands of packets; on the caller's thread parsing would stop for every block, so
/// the writer thread runs both at the same time. This also holds for <see cref="Builder.ToStream"/> and
/// <see cref="Builder.ToStdout"/>: the caller must not read, write or dispose that stream until <see cref="OnFinish"/>
/// returned; <see cref="OnFinish"/> waits for every block and writes the trailer before it returns, so the complete file is in
/// the stream then. Use <see cref="ExportWriteMode.Synchronous"/> to keep all writes on the calling thread. Memory: two open
/// blocks in background mode, one in synchronous mode.
/// </para>
/// <para>
/// <b>Error timing:</b> in background mode a block is written after <see cref="OnPacket"/> returned for its last packet, so a
/// failing block is reported by the next <see cref="OnPacket"/> call that finds it (at the latest by <see cref="OnFinish"/>); in
/// synchronous mode by that very call. Its packets leave <see cref="WrittenCount"/> and count as skipped when it is reported, so
/// the packet count of the trailer is exact.
/// </para>
/// <para>
/// <b>Export shapes:</b> <see cref="Builder.WithContent"/> chooses the shape. <see cref="ExportShape.Hierarchy"/> (the default,
/// <see cref="DefaultContent"/>) writes every field with its place in the field tree and needs packets with a field tree; a
/// hierarchy packet without one is skipped and reported as <see cref="ExportErrorKind.SerializationError"/>.
/// <see cref="ExportShape.SelectedFields"/> and <see cref="ExportShape.AllFields"/> write value rows numbered by occurrence and
/// no topology; they are fed field by field while a packet is parsed: <see cref="ParseDemand"/> states the exporter's observer, a
/// <see cref="ParsePlan"/> parses with it, and <see cref="OnPacket"/> commits the staged fields of that very packet. A packet the
/// filter rejected is simply not committed. Tree packets are accepted for every shape and give the same output.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. Callers are responsible for synchronization
/// if used from multiple threads. Statistics are valid to read after <see cref="OnFinish"/> returns; <see cref="BlockCount"/> and
/// <see cref="EstimatedOutputBytes"/> may be read at any time. Internally the writer thread never touches the counters or raises
/// <see cref="ItemSkipped"/>, which stay on the caller's thread.
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
public sealed class PbfExporter : IPacketListener, IErrorTolerantExporter, IParseDemandSource, IBatchWriter<PbfBlock>, IDisposable
{
    #region Configuration and state

    /// <summary>44-byte PBF magic header/footer.</summary>
    private static ReadOnlySpan<byte> _Magic => "NETWORK-INSPECTOR-PBF-FORMAT-v1\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"u8;

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

    /// <summary>Whether blocks of more than 64 bytes are LZ4-compressed when that makes them smaller.</summary>
    private readonly bool _Compressed;

    /// <summary>Packets per block before the block is handed to the writer (1 to <see cref="int.MaxValue"/>; see <see cref="BatchLimits"/>).</summary>
    private readonly int _MaxPacketsPerBlock;

    /// <summary>Block size in bytes before the block is handed to the writer (1 to <see cref="long.MaxValue"/>; see <see cref="BatchLimits"/>).</summary>
    private readonly long _MaxBlockSize;

    /// <summary>Whether the trailer carries the block index (one timestamp range per block).</summary>
    private readonly bool _IncludeTrailerIndex;

    /// <summary>What the file contains: the shape, the selected fields, and the optional columns.</summary>
    private readonly ExportContent _Content;

    /// <summary>
    /// Flat shapes only: the collector whose observer stages the fields of a packet while it is parsed. The columnar layout stages
    /// straight into the open block's batch (Stage of the block); the standard layout stages into <see cref="_RowStage"/>, committed
    /// in <see cref="OnPacket"/>. <see langword="null"/> for the hierarchy shape (written from the field tree).
    /// </summary>
    private readonly ExportFieldCollector? _Collector;

    /// <summary>
    /// The standard layout's own stage for the flat shapes (reused for every packet, independent of the open block);
    /// <see langword="null"/> for the hierarchy shape and the columnar layout.
    /// </summary>
    private readonly RowFieldStage? _RowStage;

    /// <summary>Whether the caller declared non-decreasing packet timestamps (metadata for the shared columnar batch).</summary>
    private readonly bool _IsTimestampSorted;

    /// <summary>Whether full blocks are written by a writer thread or inline.</summary>
    private readonly ExportWriteMode _WriteMode;

    /// <summary>
    /// Output target consumed on lazy init; it owns the underlying stream, which the block writer writes to without owning it.
    /// <see langword="null"/> once released.
    /// </summary>
    private ExportOutput? _Output;

    /// <summary>
    /// The writer pipeline owns both open blocks, the writer thread (or writes inline in synchronous mode), and the queue of lost
    /// blocks (see <see cref="BatchWriterPipeline{TBatch}"/> for the primitive choice, the memory bound, and the error timing). It
    /// is created in the constructor without I/O, blocks, or a thread. The exporter is the pipeline's writer (explicit
    /// <see cref="IBatchWriter{TBatch}"/> implementation) and forwards to the <see cref="PbfBlockWriter"/> that
    /// <see cref="_Start"/> creates once the output is open.
    /// </summary>
    private readonly BatchWriterPipeline<PbfBlock> _Pipeline;

    /// <summary>
    /// Serializes, compresses and writes blocks; owns the block index, the global field bitmap and the committed byte count.
    /// <see langword="null"/> before the start and after the disposal.
    /// </summary>
    private PbfBlockWriter? _BlockWriter;

    /// <summary>The block count survives the disposal of the block writer, so <see cref="BlockCount"/> stays readable after <see cref="OnFinish"/>.</summary>
    private int _FinalBlockCount;

    /// <summary>
    /// Intake and accounting of this export (stop checks, the one-stack rule of the columnar layout, the lazy start, the counters,
    /// the block hand-off with the nesting check, the lost-block accounting, and the error channel); shared in shape with the
    /// other columnar exporters. This exporter keeps its builder, the file framing, the block write, and the finish sequence.
    /// </summary>
    private readonly PbfExportCore _Core;

    /// <summary>Creates a new exporter (use <see cref="CreateBuilder"/> for construction).</summary>
    /// <param name="output">The output target; the exporter disposes it when it finishes.</param>
    /// <param name="uiName">Display name; not empty.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="format">The block layout.</param>
    /// <param name="compressed">Whether blocks are LZ4-compressed.</param>
    /// <param name="maxPacketsPerBlock">Packets per block; already validated by the builder.</param>
    /// <param name="maxBlockSize">Block size in bytes; already validated by the builder.</param>
    /// <param name="includeTrailerIndex">Whether the trailer carries the block index.</param>
    /// <param name="targetPacketCount">Packets after which the export is finished; 0 means unlimited.</param>
    /// <param name="content">The validated content; both layouts write every member of it.</param>
    /// <param name="isTimestampSorted">Whether packets arrive in non-decreasing timestamp order (metadata only).</param>
    /// <param name="writeMode">Background writer thread or inline writes.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
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
        ExportContent content,
        bool isTimestampSorted,
        ExportWriteMode writeMode,
        CancellationToken cancellationToken)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _Compressed = compressed;
        _MaxPacketsPerBlock = maxPacketsPerBlock;
        _MaxBlockSize = maxBlockSize;
        _IncludeTrailerIndex = includeTrailerIndex;
        _Content = content;
        _IsTimestampSorted = isTimestampSorted;
        _WriteMode = writeMode;

        // The batch factory runs on first use (first packet or finish), never here, so no builder is allocated for an exporter that
        // never receives a packet. The per-block bitmap capacity must match the global bitmap capacity (4096 bytes = 32768 bits)
        // so that field IDs greater than 4095 are tracked in both the per-block presence and the merged trailer bitmap: a smaller
        // per-block bitmap would silently lose the presence information of every field ID above its range when the blocks are
        // merged into the trailer.
        _Pipeline = new BatchWriterPipeline<PbfBlock>(
            "PBF",
            writeMode,
            () => format == PbfExportFormat.Columnar
                ? new PbfBlock(null, new ColumnarBlockBuilder(_MaxFieldId, content, maxPacketsPerBlock, maxBlockSize, isTimestampSorted))
                : new PbfBlock(new StandardBlockBuilder(_MaxFieldId, content, maxPacketsPerBlock, maxBlockSize), null),
            this);

        // The observer stages the first packet before the first OnPacket, so for the columnar flat shapes the first block (and
        // with it its builder) is needed now: the pipeline creates it on the first access to Current. Nothing is written and no
        // thread exists yet; an exporter that never receives a packet still leaves no file behind.
        if (format == PbfExportFormat.Columnar && content.Shape != ExportShape.Hierarchy)
        {
            _Collector = new ExportFieldCollector(content, _Pipeline.Current.Stage!); // Invariant: a columnar block always has a stage.
        }
        else if (format == PbfExportFormat.Standard && content.Shape != ExportShape.Hierarchy)
        {
            // The standard layout formats a packet as one record, so its fields wait in a stage of the exporter's own (reused for
            // every packet, independent of the block that is open) until OnPacket commits them.
            _RowStage = new();
            _Collector = new ExportFieldCollector(content, _RowStage);
        }

        // The delegates are created once here, so the per-packet path allocates none. The lazy start takes no packet: the header
        // does not depend on it (the finish of an export without packets starts the file the same way).
        _Core = new PbfExportCore(
            format,
            content,
            targetPacketCount,
            _Pipeline,
            _Collector,
            _RowStage,
            _RaiseItemSkipped,
            _ => _Start(),
            cancellationToken);
    }

    /// <summary>Creates a new builder for configuring the exporter.</summary>
    /// <returns>A builder with the defaults of the format; at least an output target must be set before <see cref="Builder.Build"/>.</returns>
    public static Builder CreateBuilder() => new();

    /// <summary>
    /// The content written when <see cref="Builder.WithContent"/> is not called: every field with its place in the field tree,
    /// the packet info, and the custom texts (the hierarchy shape), in both layouts. The frame bytes and the additional buffers are off (see
    /// <see cref="ExportContent.FrameBytes"/> and <see cref="ExportContent.Buffers"/>); both layouts write them on request.
    /// </summary>
    public static ExportContent DefaultContent { get; } = new();

    /// <inheritdoc/>
    /// <remarks>
    /// The hierarchy shape demands the field tree; the flat shapes of both layouts demand a tree-free parse with
    /// this exporter's observer, so a pipeline that honors the demand builds no field tree for this exporter. A caller that
    /// ignores the demand and passes tree packets still gets the same output (the exporter walks the tree through the same rules).
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
    /// Number of packets accepted so far. A packet counts as soon as it is added to the current block, which may
    /// still be buffered in memory and not yet written to the output.
    /// </summary>
    public int PacketCount => _Core.PacketCount;

    /// <summary>Number of blocks written so far. Safe to read while the writer thread runs.</summary>
    public int BlockCount => _BlockWriter is null
        ? _FinalBlockCount
        : _BlockWriter.BlockCount;

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
            long committed = _BlockWriter is null
                ? 0L
                : _BlockWriter.CommittedBytes;
            return committed + _Pipeline.CurrentEstimatedSizeBytes;
        }
    }

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
    /// The stop checks, the one-stack rule of the columnar layout (the standard layout writes every packet self-describing and
    /// needs no check), the lazy start (<see cref="_Start()"/>; the output stream and with it the file are created only when the
    /// first packet arrives), the add of the three feeds, and the hand-off of a full block are the shared intake of
    /// <see cref="PbfExportCore"/>.
    /// </remarks>
    public bool OnPacket(Packet packet) => _Core.OnPacket(packet);

    #endregion

    #region Block write

    /// <summary>
    /// Writer-thread entry (or caller entry in synchronous mode): forwards to the block writer that <see cref="_Start"/> created.
    /// The writer is non-null here because a block is only submitted after a started export.
    /// </summary>
    /// <param name="batch">The full block to serialize, compress, and write.</param>
    void IBatchWriter<PbfBlock>.Write(PbfBlock batch) => _BlockWriter!.Write(batch); // Invariant: a block exists only after _Start.

    #endregion

    #region File framing

    /// <summary>Lazily initializes output and writes magic + header.</summary>
    /// <returns><see langword="true"/> when the output is ready; <see langword="false"/> after recording an error.</returns>
    private bool _Start()
    {
        // Output already released (for example by a finished exporter) or never configured: nothing can be written.
        // Note that this path sets the error flag without raising ItemSkipped or incrementing ErrorCount, so a late call on
        // a disposed exporter does not start an error loop.
        if (_Output is null)
        {
            _Core.LatchError();
            return false;
        }

        _Core.MarkStarted();

        // Creates the file or opens stdout on demand, so nothing is touched before the first packet. An open failure (bad
        // path, missing directory, access denied) or a missing stream goes through the normal error path instead of
        // escaping from OnPacket.
        if (!_Output.TryGetOrCreateUnderlyingStream(out Stream? underlyingStream, out string openFailure))
        {
            _Core.ReportStartFailure(ExportErrorKind.IoError, $"PBF output could not be opened: {openFailure}");
            return false;
        }

        try
        {
            // _Magic + header writes go straight to the underlying stream and may
            // throw on a broken target — surface as an export error rather than
            // bubbling out to the caller.
            _BlockWriter = new PbfBlockWriter(underlyingStream, _Compressed, _MaxFieldId / 8);
            _BlockWriter.WriteDirect(_Magic);
            _WriteHeader();
        }
        catch (Exception ex)
        {
            _Core.ReportStartFailure(
                ex is IOException ? ExportErrorKind.IoError : ExportErrorKind.SerializationError,
                $"PBF header write failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>Writes the PBF file header. Encodes the three fields directly into a stack-allocated
    /// buffer to avoid a pool rent+return for this single call-site.</summary>
    /// <remarks>
    /// The header is written once per file, so the hand-rolled encoding (which duplicates the varint loop of
    /// <see cref="ProtobufEncoder"/>) trades a little duplication for not needing a <see cref="PooledBuffer"/> here.
    /// The creation time has millisecond resolution, stored as nanoseconds since the Unix epoch.
    /// </remarks>
    private void _WriteHeader()
    {
        // Encode the three-field protobuf header message directly into a stack-allocated buffer.
        // Maximum encoded size: 2 bytes (version) + 11 bytes (sint64 timestamp) + 2 bytes (shape) = 15 bytes.
        // 32 bytes is ample headroom.
        Span<byte> data = stackalloc byte[32];
        int pos = 0;

        // Field 1 (HeaderVersion): tag = (1 << 3) | 0 = 0x08, value = the format version (1; the shape field is part of it).
        data[pos++] = (byte)((PbfFieldNumbers.HeaderVersion << 3) | 0);
        data[pos++] = PbfFieldNumbers.FormatVersion;

        // Field 2 (HeaderCreationTimestamp): tag = (2 << 3) | 0 = 0x10, zigzag-encoded sint64
        data[pos++] = (byte)((PbfFieldNumbers.HeaderCreationTimestamp << 3) | 0);
        ulong zigzag = ProtobufEncoder.ZigZagEncode(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        while (zigzag > 0x7F)
        {
            data[pos++] = (byte)(zigzag | 0x80);
            zigzag >>= 7;
        }
        data[pos++] = (byte)zigzag;

        // Field 3 (HeaderShape): tag = (3 << 3) | 0 = 0x18, value = the shape code (0, 1 or 2: one byte, a varint below 128). A
        // reader checks it first: it says whether the position column of the field columns holds node ids or occurrence numbers.
        data[pos++] = (byte)((PbfFieldNumbers.HeaderShape << 3) | 0);
        data[pos++] = (byte)ExportInfo.ShapeCode(_Content.Shape);

        // Write header as length-prefixed: [4-byte length LE] + [protobuf data]
        Span<byte> lengthPrefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, pos);
        PbfBlockWriter writer = _BlockWriter!; // Invariant: _Start created the block writer before the header is written.
        writer.WriteDirect(lengthPrefix);
        writer.WriteDirect(data[..pos]);
    }

    /// <summary>Writes the trailer, trailer size, and closing magic.</summary>
    private void _WriteTrailer()
    {
        // Guard against a failed _Start() leaving the block writer null; the start failure has already recorded the error.
        PbfBlockWriter? writer = _BlockWriter;
        if (writer is null)
        {
            return;
        }

        // The trailer is built in a pooled buffer because its 4-byte size follows the content and readers locate the
        // trailer by reading that size backwards from the closing magic. It is written once per file, so the
        // per-entry buffers below are not on a hot path. The block index, the field bitmap, and the block count are read
        // from the block writer here, after the pipeline joined its thread (see _FinalizeFile), so they are complete.
        PooledBuffer trailer = new(256);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerPacketCount,
            (ulong)PacketCount);
        ProtobufEncoder.WriteVarintField(ref trailer, PbfFieldNumbers.TrailerBlockCount, (ulong)writer.BlockCount);

        // Field presence bitmap: only the prefix up to the last non-zero byte is stored, so a file whose fields all
        // have low IDs does not carry the full 4 KiB. The bitmap is omitted when no field was ever present.
        ReadOnlySpan<byte> globalBitmap = writer.GlobalFieldBitmap;
        int usedBitmapLen = 0;
        for (int i = globalBitmap.Length - 1; i >= 0; i--)
        {
            if (globalBitmap[i] != 0)
            {
                usedBitmapLen = i + 1;
                break;
            }
        }
        if (usedBitmapLen > 0)
        {
            ProtobufEncoder.WriteLengthDelimited(ref trailer, PbfFieldNumbers.TrailerFieldBitmap,
                globalBitmap[..usedBitmapLen]);
        }

        // Block index (optional): one entry per block with its timestamp range, so a reader can skip blocks by time
        // without decoding them.
        if (_IncludeTrailerIndex)
        {
            foreach (BlockIndexEntry entry in writer.BlockIndex)
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
        writer.WriteDirect(trailer.WrittenSpan);
        trailer.Return();
        writer.WriteDirect(trailerSizeBytes);
        writer.WriteDirect(_Magic);
    }

    #endregion

    #region Finish

    /// <inheritdoc/>
    /// <remarks>Idempotent: only the first call finalizes the file. Also invoked by <see cref="Dispose"/>.</remarks>
    /// <exception cref="AggregateException">One or more resource release steps failed.</exception>
    public void OnFinish()
    {
        if (!_Core.TryBeginFinish())
        {
            return;
        }

        // Shared finish sequence: finalize, then release every resource even when a step fails. The pipeline goes first (its
        // writer thread must have ended before the block writer and the output are released), the output last.
        ExporterFinish.Run(
            "PBF",
            _FinalizeFile,
            _Core.ReportFinishError,
            new ExporterReleaseStep("writer pipeline disposal", _Pipeline.Dispose),
            new ExporterReleaseStep("block writer disposal", _DisposeBlockWriter),
            new ExporterReleaseStep("row stage disposal", _DisposeRowStage),
            new ExporterReleaseStep("output disposal", _DisposeOutput));
    }

    /// <inheritdoc/>
    public void Dispose() => OnFinish();

    /// <summary>Writes the last block, accounts the lost ones, writes the trailer, and flushes the stream (starting first for an empty export).</summary>
    private void _FinalizeFile()
    {
        // An export without packets still produces a valid, empty PBF file (magic, header, trailer, magic), as the
        // exporter guide requires for file formats; this is why the start is forced here.
        if (!_Core.Started && _Output is not null)
        {
            _Start();
        }

        // Last (possibly partial) block, then the trailer. The order matters: the trailer's counts and block index
        // include the block written here. The last block goes through the same strict nesting check as every block; a dropped
        // block is simply not written. Complete joins the writer thread, which makes everything it wrote (block index, field
        // presence, byte count) visible to this thread.
        if (!_Pipeline.IsCompleted)
        {
            // Fields staged for a packet that was never committed (the filter rejected it) must not reach the last block. The
            // standard layout stages into the exporter's own row stage; the columnar layout into the open block's batch.
            if (_RowStage is not null)
            {
                _RowStage.Discard();
            }
            else if (_Collector is not null)
            {
                _Pipeline.Current.Stage!.Discard(); // Invariant: a collector without a row stage exists only for the columnar layout.
            }

            // A dropped block is empty afterwards (the check reset it), so Complete writes nothing for it; the check itself did
            // the accounting (skipped items, packet count), hence the discarded result.
            if (_Pipeline.CurrentPacketCount > 0)
            {
                _ = _Core.PassesNestingCheck(_Pipeline.Current);
            }

            _Pipeline.Complete(firstItemIndex: PacketCount - _Pipeline.CurrentPacketCount);
        }

        // Lost blocks first, so the packet count of the trailer states only what is in the file.
        _Core.DrainLostBatches(reportAlways: true);
        _WriteTrailer();
        _BlockWriter?.Flush();
    }

    /// <summary>Disposes the block writer once (returns its framing buffer).</summary>
    private void _DisposeBlockWriter()
    {
        PbfBlockWriter? writer = _BlockWriter;
        _BlockWriter = null;
        _FinalBlockCount = writer is null
            ? _FinalBlockCount
            : writer.BlockCount;
        writer?.Dispose();
    }

    /// <summary>Returns the pooled chunks of the standard layout's row stage (a no-op for the other layouts).</summary>
    private void _DisposeRowStage() => _RowStage?.Dispose();

    /// <summary>Disposes the output once (closes the stream unless the caller owns it).</summary>
    private void _DisposeOutput()
    {
        ExportOutput? output = _Output;
        _Output = null;
        output?.Dispose();
    }

    #endregion

    #region Builder

    /// <summary>Fluent builder for constructing a <see cref="PbfExporter"/>.</summary>
    public sealed class Builder
    {
        /// <summary>The output target; required before <see cref="Build"/>.</summary>
        private ExportOutput? _Output;

        /// <summary>Display name shown in UI and logs.</summary>
        private string _UiName = "PBF Exporter";

        /// <summary>Optional description; <see langword="null"/> when none.</summary>
        private string? _Description;

        /// <summary>Cooperative cancellation; <see langword="default"/> never cancels.</summary>
        private CancellationToken _CancellationToken;

        /// <summary>The block layout.</summary>
        private PbfExportFormat _Format = PbfExportFormat.Standard;

        /// <summary>Whether blocks are LZ4-compressed.</summary>
        private bool _Compressed = true;

        /// <summary>
        /// Packets per block. Limits: see <see cref="BatchLimits"/> (range, threat, posture, who may change them); shared with the
        /// other batching exporters.
        /// </summary>
        private int _MaxPacketsPerBlock = BatchLimits.DefaultPacketsPerBatch;

        /// <summary>Bytes per block. Limits: see <see cref="BatchLimits"/>.</summary>
        private long _MaxBlockSize = BatchLimits.DefaultBytesPerBatch;

        /// <summary>Background writer thread or inline writes.</summary>
        private ExportWriteMode _WriteMode = ExportWriteMode.Background;

        /// <summary>Whether the trailer carries the block index.</summary>
        private bool _IncludeTrailerIndex = true;

        /// <summary>Packets after which the export is finished; 0 means unlimited.</summary>
        private int _TargetPacketCount;

        /// <summary>The content; <see langword="null"/> selects the default of the layout at <see cref="Build"/>.</summary>
        private ExportContent? _Content;

        /// <summary>Whether the caller declares non-decreasing packet timestamps (metadata only).</summary>
        private bool _IsTimestampSorted;

        /// <summary>Sets the output to a file path with a 4 MiB buffer.</summary>
        /// <param name="path">The file to create on the first packet (or at finish for an empty export); an existing file is replaced.</param>
        /// <returns>This builder.</returns>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>
        /// Sets the output to an existing stream. The caller keeps ownership: the exporter flushes the stream when
        /// it finishes but does not close it.
        /// </summary>
        /// <param name="stream">A writable stream; in background mode the caller must not use it until <see cref="OnFinish"/> returned.</param>
        /// <returns>This builder.</returns>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Sets the output to stdout.</summary>
        /// <returns>This builder.</returns>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
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

        /// <summary>Sets the PBF block format.</summary>
        /// <param name="format">The block layout; the default is <see cref="PbfExportFormat.Standard"/>.</param>
        /// <returns>This builder.</returns>
        public Builder WithFormat(PbfExportFormat format)
        {
            _Format = format;
            return this;
        }

        /// <summary>Enables or disables LZ4 compression for blocks.</summary>
        /// <param name="compressed"><see langword="true"/> (default) compresses blocks of more than 64 bytes when that makes them smaller.</param>
        /// <returns>This builder.</returns>
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
        /// <param name="count">Packets per block; 1 to <see cref="int.MaxValue"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1 (the message names the range).</exception>
        public Builder WithMaxPacketsPerBlock(int count)
        {
            _MaxPacketsPerBlock = BatchLimits.ValidatePacketsPerBatch(count, nameof(count));
            return this;
        }

        /// <summary>
        /// Sets the maximum block size in bytes before auto-flush. The Standard format compares against the
        /// serialized size, the Columnar format against the batch's in-memory estimate. Default 16 MiB.
        /// </summary>
        /// <param name="bytes">Bytes per block; 1 to <see cref="long.MaxValue"/>.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is below 1 (the message names the range).</exception>
        public Builder WithMaxBlockSize(long bytes)
        {
            _MaxBlockSize = BatchLimits.ValidateBytesPerBatch(bytes, nameof(bytes));
            return this;
        }

        /// <summary>
        /// Chooses how full blocks are written: <see cref="ExportWriteMode.Background"/> (default) serializes, compresses and writes
        /// on a writer thread named "PBF export writer" while the next block fills; <see cref="ExportWriteMode.Synchronous"/> does it
        /// inside the <see cref="OnPacket"/> call that completed the block. Background holds two blocks in memory, synchronous one.
        /// With a caller-owned stream (<see cref="ToStream"/>, <see cref="ToStdout"/>) the caller must not use the stream until
        /// <see cref="OnFinish"/> returned in background mode.
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

        /// <summary>Includes a block index in the trailer for random access.</summary>
        /// <param name="include"><see langword="true"/> (default) writes one timestamp range per block into the trailer.</param>
        /// <returns>This builder.</returns>
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
        /// <returns>This builder.</returns>
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
        /// <param name="description">The description; <see langword="null"/> clears it.</param>
        /// <returns>This builder.</returns>
        public Builder WithDescription(string? description)
        {
            _Description = description;
            return this;
        }

        /// <summary>
        /// Sets the cancellation token for cooperative shutdown.
        /// </summary>
        /// <param name="token">The token; once cancelled, <see cref="OnPacket"/> refuses every packet.</param>
        /// <returns>This builder.</returns>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
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
        /// Declares that packets will be added in non-decreasing timestamp order for
        /// <see cref="PbfExportFormat.Columnar"/> blocks. This is metadata only in the current
        /// PBF writer (it does not change the emitted bytes); it is accepted here so callers can
        /// configure the shared <see cref="ColumnarPacketBatch"/> knobs identically across the
        /// PBF, Parquet, and DuckDB exporters. Defaults to <see langword="false"/>.
        /// </summary>
        /// <param name="sorted"><see langword="true"/> when the caller guarantees non-decreasing timestamps.</param>
        /// <returns>This builder.</returns>
        public Builder WithTimestampSorted(bool sorted)
        {
            _IsTimestampSorted = sorted;
            return this;
        }

        /// <summary>Builds the exporter. Throws if no output target was configured.</summary>
        /// <returns>The exporter; the output is opened on the first packet.</returns>
        /// <exception cref="InvalidOperationException">No output target was configured.</exception>
        public PbfExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException("No output target configured. Call ToFile(), ToStream(), or ToStdout().");
            }

            // Without WithContent both layouts write the same default content: no layout has a content member the other lacks.
            ExportContent content = _Content ?? DefaultContent;
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
                content,
                _IsTimestampSorted,
                _WriteMode,
                _CancellationToken);
        }
    }

    #endregion
}
