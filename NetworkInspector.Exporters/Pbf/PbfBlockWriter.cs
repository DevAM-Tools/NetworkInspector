// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>Timestamp range of one written block, kept for the optional block index of the trailer.</summary>
/// <param name="MinTimestamp">Smallest packet timestamp of the block (nanoseconds).</param>
/// <param name="MaxTimestamp">Largest packet timestamp of the block (nanoseconds).</param>
internal readonly record struct BlockIndexEntry(long MinTimestamp, long MaxTimestamp);

/// <summary>
/// Serializes, compresses (LZ4) and writes PBF blocks; the <see cref="IBatchWriter{TBatch}"/> of the PBF exporter.
/// <para>
/// <b>Why it exists:</b> serializing a block, compressing it, and writing it takes as long as parsing thousands of packets. Doing
/// these three phases here lets the exporter hand a full block to the pipeline and keep parsing while a writer thread does the
/// work. The strict nesting check (which may end the export and raise <c>ItemSkipped</c>) stays on the caller's thread, in
/// <see cref="PbfExportCore.PassesNestingCheck"/>.
/// </para>
/// <para>
/// <b>State it owns:</b> the block index and the global field-presence bitmap of the trailer and the committed byte count. In
/// background mode only the writer thread touches them until <see cref="BatchWriterPipeline{TBatch}.Complete"/> joined it; the
/// exporter reads them afterwards for the trailer. <see cref="CommittedBytes"/> and <see cref="BlockCount"/> are read by the caller
/// during the export (progress, size splitting), so they are written with <see cref="Interlocked"/> and read with
/// <see cref="Volatile"/> — two words, the cheapest correct primitive.
/// </para>
/// <para>
/// <b>Caller-owned streams and stdout:</b> in background mode this writer writes to the stream from the writer thread. The
/// exporter's contract (see <c>PbfExporter</c> remarks) is that the caller does not use the stream until <c>OnFinish</c>
/// returned; <c>OnFinish</c> joins the writer before it writes the trailer, so every byte is in the stream when it returns.
/// </para>
/// </summary>
internal sealed class PbfBlockWriter : IBatchWriter<PbfBlock>, IDisposable
{
    #region Configuration and state

    /// <summary>Non-owning reference: the <see cref="ExportOutput"/> of the exporter owns and disposes the stream.</summary>
    private readonly Stream _Stream;

    /// <summary>Whether blocks of more than 64 bytes are LZ4-compressed when that makes them smaller.</summary>
    private readonly bool _Compressed;

    /// <summary>
    /// Reusable framing buffer. Sized with 64 KiB initial capacity and reset between blocks instead of allocating a fresh buffer
    /// per block; it grows to the largest framed block. Framing the block (flags, sizes, data) in one buffer lets it reach the
    /// stream with a single write.
    /// </summary>
    private readonly PooledBuffer _BlockBuf = new(64 * 1024);

    /// <summary>One timestamp range per written block, in write order, for the trailer's block index.</summary>
    private readonly List<BlockIndexEntry> _BlockIndex = new(64);

    /// <summary>Union of the field-presence bitmaps of the written blocks, in the layout of <see cref="FieldPresence"/>.</summary>
    private readonly byte[] _GlobalFieldBitmap;

    /// <summary>Bytes written to the stream so far; written with <see cref="Interlocked"/>, read with <c>Interlocked.Read</c>.</summary>
    private long _CommittedBytes;

    /// <summary>Blocks written so far; written with <see cref="Interlocked"/>, read with <see cref="Volatile"/>.</summary>
    private int _BlockCount;

    /// <summary>Creates the writer for an opened output stream.</summary>
    /// <param name="stream">The output stream (owned by the caller of this writer).</param>
    /// <param name="compressed">Whether blocks of more than 64 bytes are LZ4-compressed.</param>
    /// <param name="fieldBitmapLength">Length of the global field-presence bitmap in bytes.</param>
    internal PbfBlockWriter(Stream stream, bool compressed, int fieldBitmapLength)
    {
        _Stream = stream;
        _Compressed = compressed;
        _GlobalFieldBitmap = new byte[fieldBitmapLength];
    }

    /// <summary>Bytes written to the stream so far (header, blocks, trailer); safe to read during the export.</summary>
    internal long CommittedBytes => Interlocked.Read(ref _CommittedBytes);

    /// <summary>Blocks written so far; safe to read during the export.</summary>
    internal int BlockCount => Volatile.Read(ref _BlockCount);

    /// <summary>Timestamp ranges of the written blocks; read after the pipeline completed only.</summary>
    internal IReadOnlyList<BlockIndexEntry> BlockIndex => _BlockIndex;

    /// <summary>Union of the field-presence bitmaps of the written blocks; read after the pipeline completed only.</summary>
    internal ReadOnlySpan<byte> GlobalFieldBitmap => _GlobalFieldBitmap;

    #endregion

    #region Block writing

    /// <summary>Writes bytes outside the block stream (header, trailer); caller's thread, before the writer starts or after it ended.</summary>
    /// <param name="data">The bytes to write.</param>
    internal void WriteDirect(ReadOnlySpan<byte> data)
    {
        _Stream.Write(data);
        Interlocked.Add(ref _CommittedBytes, data.Length);
    }

    /// <summary>Flushes the output stream (after the trailer); caller's thread, after the writer thread ended.</summary>
    internal void Flush() => _Stream.Flush();

    /// <summary>Serializes, compresses and writes one block, then records its index entry and field presence.</summary>
    /// <param name="block">The full block; empty blocks write nothing.</param>
    public void Write(PbfBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.PacketCount == 0)
        {
            return;
        }

        // Phase 1: serialize through the layout's builder; the span stays valid until the block's Reset. The timestamp range
        // and the field presence go into the trailer; MinTimestamp/MaxTimestamp are populated by Build().
        ReadOnlySpan<byte> blockData;
        long minTs;
        long maxTs;
        if (block.Columnar is { } columnar)
        {
            blockData = columnar.Build();
            minTs = columnar.MinTimestamp;
            maxTs = columnar.MaxTimestamp;
            columnar.FieldPresence.MergeInto(_GlobalFieldBitmap);
        }
        else
        {
            StandardBlockBuilder standard = block.Standard!; // Invariant: exactly one builder is set.
            blockData = standard.Build();
            minTs = standard.MinTimestamp;
            maxTs = standard.MaxTimestamp;
            ReadOnlySpan<byte> presenceBytes = standard.FieldPresenceBytes;
            for (int i = 0; i < presenceBytes.Length && i < _GlobalFieldBitmap.Length; i++)
            {
                _GlobalFieldBitmap[i] |= presenceBytes[i];
            }
        }

        // Phase 2: optional LZ4 compression of the whole block (one compression call per block; the codec clears a 256 KB hash
        // table at the start of every call, which this amortizes over the whole block). Blocks of 64 bytes or less are stored
        // uncompressed without trying. Perf: at this size the LZ4 block format's token and literal overhead leaves no room to
        // shrink, and clearing the codec's 256 KB hash table would cost more than the block; the value is a constant, not a limit.
        ReadOnlySpan<byte> outputData;
        byte[]? compressedBuf = null;
        bool isCompressed = false;
        if (_Compressed && blockData.Length > 64)
        {
            // The compressor needs room for its worst case (MaxCompressedSize), which is larger than the input for
            // incompressible data. The array comes from the pool because blocks can be megabytes, which would otherwise land
            // on the large object heap for every block. The span is cut to exactly the worst-case size because the pool may
            // return a longer array than requested. MaxCompressedSize is -1 for an input above the LZ4 block limit: renting
            // that would throw, so such a block is stored uncompressed like one that does not shrink.
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
                // LZ4 output is not smaller than the original (or the block is above the LZ4 limit); fall back to storing the
                // block uncompressed. flags remains 0 (uncompressed), which is correct: the reader determines the storage
                // format from flags alone, not from comparing the two size fields. Both size fields carry the same value.
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

        try
        {
            // Phase 3: frame the block and write it. Both sizes are always stored (equal when uncompressed) so a reader can
            // size its decompression buffer before touching the data and can skip a block by its stored size.
            // Block: [1-byte flags] + [4-byte original size LE] + [4-byte stored size LE] + [data]. Flags: bit 0 = compressed.
            // Sizes are int32 (the block buffers are int-indexed, so a block cannot exceed 2 GiB).
            byte flags = isCompressed
                ? (byte)1
                : (byte)0;
            _BlockBuf.Reset();
            _BlockBuf.WriteByte(flags);
            Span<byte> sizes = _BlockBuf.Reserve(8);
            BinaryPrimitives.WriteInt32LittleEndian(sizes, blockData.Length);
            BinaryPrimitives.WriteInt32LittleEndian(sizes[4..], outputData.Length);
            _BlockBuf.Write(outputData);
            WriteDirect(_BlockBuf.WrittenSpan);
            _BlockBuf.Reset();
        }
        finally
        {
            // The compression buffer goes back whether the write succeeded or not.
            if (compressedBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(compressedBuf);
            }
        }

        // Commit the block-index entry only after the bytes have been successfully written. If the write throws, no phantom
        // index entry is left behind.
        _BlockIndex.Add(new BlockIndexEntry(minTs, maxTs));
        Interlocked.Increment(ref _BlockCount);
    }

    /// <summary>Returns the framing buffer to the pool.</summary>
    public void Dispose() => _BlockBuf.Return();

    #endregion
}
