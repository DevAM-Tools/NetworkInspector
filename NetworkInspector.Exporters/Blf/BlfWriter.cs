// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// Low-level BLF file writer: writes the file header, compresses (zlib) and writes log containers, and builds the final header.
/// <para>
/// The objects are collected in <see cref="BlfContainer"/>s by the exporter; this type is the <see cref="IBatchWriter{TBatch}"/>
/// that turns a full container into a compressed log container on the stream. The 144-byte file header is written immediately
/// on construction with placeholder values; call <see cref="Finish"/> to compute final statistics and optionally rewrite the
/// header via <see cref="BlfWriterFinishResult.UpdateHeader"/>.
/// </para>
/// <para>
/// <b>Seek-free by design:</b> Only <see cref="Stream.Write(ReadOnlySpan{byte})"/>
/// is required during writing — no seeking is needed. The initial header contains
/// conservative placeholder values (<c>ObjectCount=0</c>, <c>measurement_end=0</c>,
/// <c>file_size=0</c>) that are valid for readers. BLF readers (including Vector
/// CANoe and our own <c>BlfSource</c>) count objects themselves and do not rely
/// on the header statistics. Seeking is only used by
/// <see cref="BlfWriterFinishResult.UpdateHeader"/> to rewrite the file header at
/// the position where the file started (0 for a fresh stream) after all data is written, and by
/// <see cref="TryRealignStartEarlier"/> to move the start anchor before
/// the first object — both are best-effort optimizations for
/// seekable streams. For non-seekable streams (pipes, network), the header
/// placeholders remain and the file is still valid.
/// </para>
/// <para>
/// <b>Threads:</b> in background mode <see cref="Write"/> runs on the writer thread "BLF export writer"; the header, the realign
/// before the first object, <see cref="ToRelative"/>, and <see cref="Finish"/> run on the caller's thread, before the first
/// container is handed over or after the writer thread was joined, so the stream is never used by two threads at once.
/// <see cref="BytesWritten"/> is read by the caller during the export (progress, size splitting) and written by the writer
/// thread, so it uses <see cref="Interlocked"/>/<see cref="Volatile"/>; the other totals are read only in <see cref="Finish"/>,
/// after the join.
/// </para>
/// </summary>
internal sealed class BlfWriter : IBatchWriter<BlfContainer>
{
    #region Configuration and state

    /// <summary>
    /// Largest compressed-output capacity rented up front: 64 MiB, the largest container the exporter accepts. The shared array
    /// pool hands a returned array of this size (and of the smaller sizes the test pins) out again on the same thread, so the
    /// rent on the caller's thread is the last allocation of the buffer in a normal export.
    /// </summary>
    /// <remarks>Sync: <c>PooledBufferTests.TheSharedPoolReusesAnArrayOfThisSizeOnTheSameThread</c> (its largest size).</remarks>
    private const int _PooledCapacityLimit = 64 * 1024 * 1024;

    /// <summary>BLF file header size in bytes.</summary>
    internal const int FileHeaderSize = 144;

    /// <summary>Combined size of block header (16) + log object header V1 (16); shared with <see cref="BlfContainer"/>.</summary>
    internal const int ObjectHeaderTotalSize =
        BlfConstants.BlockHeaderSize + BlfConstants.LogObjectHeaderType1Size; // 32

    /// <summary>Target stream. Owned by the caller; not disposed here.</summary>
    private readonly Stream _Stream;

    /// <summary>
    /// The LOGG <c>start_date</c> anchor in nanoseconds (always a whole number of milliseconds). Mutable only because
    /// <see cref="TryRealignStartEarlier"/> may lower it before the first object is written.
    /// </summary>
    private long _StartNs;

    /// <summary>zlib level of the containers; <see cref="CompressionLevel.NoCompression"/> stores them raw.</summary>
    private readonly CompressionLevel _Compression;

    /// <summary>
    /// Buffer for the zlib-compressed container output, reused across <see cref="Write"/> calls so that no container allocates.
    /// <para>
    /// <b>Why it is rented in the constructor:</b> in background mode <see cref="Write"/> runs on a fresh writer thread. A
    /// buffer that grows there rents every doubling step (16 KiB, 32 KiB, …) from <see cref="ArrayPool{T}.Shared"/> on a thread
    /// whose per-thread cache is empty, while the arrays of the previous exporter were returned on the caller's thread. Each
    /// of those rents misses and allocates a new array, about 0.5 MiB of garbage per exporter (measured with
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> on the writer thread: 517 KB for the first container, 264 bytes for every
    /// later one). The caller's thread is the one that returned the arrays, so renting here hits its cache and the writer thread
    /// finds the buffer already large enough.
    /// </para>
    /// <para>
    /// <b>Capacity:</b> the container size (at most <see cref="_PooledCapacityLimit"/>), because compressed data is never larger
    /// than the container except for data that does not compress at all. The pool rounds the rent up to the next power of two
    /// (a 10 MiB container rents a 16 MiB array). A container whose output still exceeds the capacity grows the buffer on the
    /// writer thread (correct, only allocating; rare and bounded by the container size).
    /// </para>
    /// </summary>
    private readonly PooledBuffer _CompressedBuf;

    /// <summary>Number of objects written into containers so far (the header <c>obj_count</c>).</summary>
    private int _ObjectsWritten;

    /// <summary>Bytes of containers written to the stream after the file header (padded, as stored).</summary>
    private long _BytesWritten;

    /// <summary>
    /// Total size the file would have if all containers were stored uncompressed. This is the value written to the LOGG header
    /// <c>len_uncompressed</c> field; the compressed file size would make a reader scan past the last object. Each flushed
    /// container contributes 16 bytes LOBJ block header, 16 bytes container header, the uncompressed content, and the 4-byte
    /// alignment padding.
    /// </summary>
    private long _UncompressedBytesWritten;

    /// <summary>Zone of the header SYSTEMTIME fields (see the constructor).</summary>
    private readonly TimeZoneInfo _TimestampTimeZone;

    /// <summary>
    /// Stream position where the file header starts: 0 for a fresh stream, but a caller may hand over a seekable stream that
    /// already holds other data. Header rewrites must go back to this position, not to 0, or they overwrite that data.
    /// Always 0 for a stream that cannot seek (no rewrite is possible there).
    /// </summary>
    private readonly long _HeaderPosition;

    /// <summary>
    /// Creates a BLF writer and writes the 144-byte file header immediately.
    /// </summary>
    /// <param name="stream">Target output stream.</param>
    /// <param name="startNs">
    /// Start timestamp in nanoseconds since Unix epoch. Floored to whole milliseconds (the header resolution).
    /// </param>
    /// <param name="maxContainerSize">
    /// Uncompressed container size of the exporter in bytes; sizes the compressed-output buffer (see <c>_CompressedBuf</c>).
    /// </param>
    /// <param name="compression">Compression level for container output. Use
    /// <see cref="CompressionLevel.NoCompression"/> to disable compression.</param>
    /// <param name="timestampTimeZone">
    /// Zone in which the header <c>start_date</c> / <c>end_date</c> SYSTEMTIME fields are written; null means UTC.
    /// The SYSTEMTIME carries no zone, so the reader must use the same zone
    /// (<c>BlfSourceOptions.TimestampTimeZone</c>) to recover the absolute timestamps.
    /// </param>
    internal BlfWriter(
        Stream stream,
        long startNs,
        int maxContainerSize,
        CompressionLevel compression = CompressionLevel.Optimal,
        TimeZoneInfo? timestampTimeZone = null)
    {
        _Stream = stream;
        _TimestampTimeZone = timestampTimeZone ?? TimeZoneInfo.Utc;
        // start_date is a SYSTEMTIME with millisecond precision. The object timestamp is
        // nanoseconds relative to that millisecond anchor (flags word 2). Flooring _StartNs
        // keeps the sub-millisecond residual in the object field. Leaving it on _StartNs
        // would drop that residual, because the header cannot store it.
        _StartNs = (startNs / 1_000_000L) * 1_000_000L;
        _Compression = compression;

        // Perf: the buffer is rented here, on the caller's thread, at the container size (see _CompressedBuf): the writer thread
        // then never rents on its empty cache. An uncompressed writer never touches the buffer, so it rents nothing.
        int compressedCapacity = compression == CompressionLevel.NoCompression
            ? 0
            : Math.Min(maxContainerSize, _PooledCapacityLimit);
        _CompressedBuf = new PooledBuffer(compressedCapacity);

        // Remember where the file begins before the header goes out; a stream that already holds earlier data must not have
        // that data overwritten when the header is patched later.
        _HeaderPosition = stream.CanSeek
            ? stream.Position
            : 0L;
        _WriteFileHeader(_StartNs);
    }

    #endregion

    #region File header and anchor

    /// <summary>LOGG <c>start_date</c> anchor used for relative object timestamps (floored to whole milliseconds).</summary>
    internal long AnchorStartNanos => _StartNs;

    /// <summary>
    /// If no object has been appended yet (the caller states it), the stream is seekable, and
    /// <paramref name="earliestAbsoluteNanos"/> floor-ms is earlier than the current anchor, rewinds and rewrites only the
    /// 144-byte file header so the anchor matches the minimum seen timestamp. The condition is "no object appended at all", so
    /// it only helps when the frames before the early one produced no objects, for example because they were skipped.
    /// Once an object exists, the header can no longer be moved because its relative timestamp is already encoded.
    /// <para>
    /// Runs on the caller's thread and only before the first container is handed to the writer thread, so it never races with a
    /// container write (the stream is not used by two threads at once).
    /// </para>
    /// </summary>
    /// <param name="earliestAbsoluteNanos">The smallest frame timestamp seen so far.</param>
    /// <param name="objectsAppended">Whether any object was appended to a container already (the exporter counts them).</param>
    /// <returns><c>true</c> when the header was rewritten with the earlier anchor; <c>false</c> when nothing changed.</returns>
    internal bool TryRealignStartEarlier(long earliestAbsoluteNanos, bool objectsAppended)
    {
        // Called once per frame by the exporter, so the common case (no earlier timestamp, or an object already appended)
        // must be a few integer compares: the stream is not touched before these checks pass.
        long newStart = (earliestAbsoluteNanos / 1_000_000L) * 1_000_000L;
        if (newStart >= _StartNs || objectsAppended)
        {
            return false;
        }

        long headerEnd = _HeaderPosition + FileHeaderSize;
        if (!_Stream.CanSeek || _Stream.Position != headerEnd)
        {
            return false;
        }

        // Flush any buffered stream data before reading Position, so that
        // the position reflects bytes actually written and not bytes pending in a
        // BufferedStream or FileStream internal buffer that hasn't yet been flushed.
        _Stream.Flush();
        if (_Stream.Position != headerEnd)
        {
            return false;
        }

        // Rewind to where this file's header begins (not to 0): earlier bytes in the stream belong to the caller.
        _StartNs = newStart;
        _Stream.Seek(_HeaderPosition, SeekOrigin.Begin);
        _WriteFileHeader(_StartNs);
        return _Stream.Position == headerEnd;
    }

    /// <summary>
    /// Converts an absolute timestamp to the object field: nanoseconds relative to the millisecond <c>start_date</c> anchor. A
    /// negative delta is not representable in the unsigned object field, so it clamps to 0 (the exporter reports the first
    /// clamp once per export). Runs on the caller's thread.
    /// </summary>
    /// <param name="timestampNs">Absolute timestamp in nanoseconds since the Unix epoch.</param>
    /// <returns>The relative timestamp for the object header.</returns>
    internal ulong ToRelative(long timestampNs)
    {
        long relativeNs = timestampNs - _StartNs;
        if (relativeNs < 0)
        {
            relativeNs = 0;
        }

        return (ulong)relativeNs;
    }

    /// <summary>Writes the initial 144-byte BLF file header with placeholder values.</summary>
    /// <param name="startNs">Anchor in nanoseconds since the Unix epoch; written as the header <c>start_date</c>.</param>
    private void _WriteFileHeader(long startNs)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        header.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(header, BlfConstants.FileMagic);                // "LOGG"
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)FileHeaderSize);         // header_length = 144
        // api_version, application, app_major, app_minor = 0 (already zeroed)
        header[13] = _CompressionLevelToByte(_Compression);                       // compression_level
        // len_compressed, len_uncompressed, obj_count, app_build = 0 (placeholder)
        _WriteBlfDate(header.Slice(40), startNs);                                 // start_date
        // end_date = 0 (placeholder)

        _Stream.Write(header);
    }

    /// <summary>
    /// Writes a 16-byte BLF date (Windows SYSTEMTIME layout) from Unix nanoseconds.
    /// Layout: Year(2) + Month(2) + DayOfWeek(2) + Day(2) + Hour(2) + Minute(2) + Second(2) + Millisecond(2).
    /// <para>
    /// The fields are written as wall-clock components in the configured time zone (default UTC, see the constructor).
    /// A SYSTEMTIME carries no zone, and Vector's reference tooling interprets it in the reader's zone, so writer and
    /// reader must agree on the zone to recover the absolute timestamps. A fixed, configurable zone (instead of the
    /// host's local zone) keeps files reproducible across machines. A timestamp of 0 or less writes all zeros.
    /// </para>
    /// </summary>
    /// <param name="dest">The 16 bytes of the date field; overwritten completely.</param>
    /// <param name="unixNanos">Absolute time in nanoseconds since the Unix epoch; 0 or less writes an all-zero date.</param>
    private void _WriteBlfDate(Span<byte> dest, long unixNanos)
    {
        if (unixNanos <= 0)
        {
            dest.Slice(0, 16).Clear();
            return;
        }

        // The BLF header stores wall-clock SYSTEMTIME without a zone; the zone is a builder option (default UTC) so
        // writer and reader agree on a machine-independent zone, independent of the host's local time.
        DateTimeOffset dto = DateTimeOffset.FromUnixTimeMilliseconds(unixNanos / 1_000_000);
        DateTime local = TimeZoneInfo.ConvertTime(dto, _TimestampTimeZone).DateTime;

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)local.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2), (ushort)local.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4), (ushort)local.DayOfWeek); // Sunday = 0
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6), (ushort)local.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(8), (ushort)local.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(10), (ushort)local.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(12), (ushort)local.Second);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(14), (ushort)(unixNanos / 1_000_000 % 1_000));
    }

    #endregion

    #region Container writing

    /// <summary>
    /// Bytes of containers written to the stream after the file header (padded, as stored). Safe to read while the writer
    /// thread runs; the exporter adds the header and the pending container for its size estimate.
    /// </summary>
    internal long BytesWritten => Interlocked.Read(ref _BytesWritten);

    /// <summary>
    /// Compresses one container with zlib if compression is enabled, then writes it (block header + container header + data).
    /// Called on the writer thread in background mode and on the caller's thread in synchronous mode, never concurrently.
    /// <para>
    /// Padding policy: <c>object_length</c> is the unpadded total
    /// (16-byte block header + 16-byte container header + compressed payload). 0–3 zero
    /// bytes are written after that so the next file-level LOBJ is 4-aligned. Readers skip
    /// with <c>max(max(16, object_length), header_size)</c> and consume pad via a 1-byte
    /// LOBJ scan. The compressed payload passed to zlib never includes those zeros.
    /// The writer compresses with zlib only; LZ4 is a source-side read method.
    /// </para>
    /// </summary>
    /// <param name="container">The full container; empty containers write nothing.</param>
    public void Write(BlfContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        ReadOnlySpan<byte> content = container.Content;
        if (content.Length == 0)
        {
            return;
        }

        uint uncompressedSize = (uint)content.Length;
        ushort compressionMethod;
        int compressedLen;

        if (_Compression == CompressionLevel.NoCompression)
        {
            // No compression — write raw buffer data directly
            compressionMethod = BlfConstants.CompressionNone;
            compressedLen = content.Length;
        }
        else
        {
            // Perf: compress into the reused _CompressedBuf. A MemoryStream plus ToArray would allocate twice per container
            // (its buffer and the copy).
            compressionMethod = BlfConstants.CompressionZlib;
            _CompressWithZlib(content, _CompressedBuf);
            compressedLen = _CompressedBuf.Length;
        }

        // Container layout:
        //   block_header(16) + container_header(16) + data + 4-byte alignment padding
        // The block header's `header_length` is the size of the block header alone (16),
        // NOT block+container. A reader treats `header_length - 16` as additional
        // unknown padding it must skip, then reads a fixed 16-byte container header.
        const int blockHeaderSize = BlfConstants.BlockHeaderSize;                 // 16
        const int containerHeaderSize = BlfConstants.ContainerHeaderSize;         // 16
        const int containerHeaderTotal = blockHeaderSize + containerHeaderSize;   // 32
        int totalLength = containerHeaderTotal + compressedLen;
        int containerPadding = BlfConstants.AlignmentPaddingByteCount(totalLength);
        int paddedLength = totalLength + containerPadding;

        // Write 32-byte combined header: block(16) + container(16)
        Span<byte> headerBuf = stackalloc byte[containerHeaderTotal];
        headerBuf.Clear();

        // Block header for the container object
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf, BlfConstants.ObjectMagic);               // "LOBJ"
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.Slice(4), (ushort)blockHeaderSize);    // header_length = 16 (block header only)
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.Slice(6), 1);                            // header_type = V1
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.Slice(8), (uint)totalLength); // object_length: unpadded 32+compressedLen
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.Slice(12), BlfConstants.ObjTypeLogContainer);

        // Container header at offset 16
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.Slice(16), compressionMethod);
        // headerBuf[18..24] = 0 (reserved, already zeroed)
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.Slice(24), uncompressedSize);
        // headerBuf[28..32] = 0 (reserved, already zeroed)

        _Stream.Write(headerBuf);

        // Write compressed or raw payload data
        if (compressionMethod == BlfConstants.CompressionZlib)
        {
            _Stream.Write(_CompressedBuf.WrittenSpan);
        }
        else
        {
            _Stream.Write(content);
        }

        // Write 4-byte alignment padding
        if (containerPadding > 0)
        {
            Span<byte> pad = stackalloc byte[4];
            pad.Clear();
            _Stream.Write(pad.Slice(0, containerPadding));
        }

        Interlocked.Add(ref _BytesWritten, paddedLength);

        // Track the size this container would have if stored uncompressed, so we can
        // populate the LOGG `len_uncompressed` field correctly (see field comment).
        // The headers are NOT compressed in either mode, only the payload is, so the
        // uncompressed-equivalent length is 32 (block + container header) + raw payload,
        // padded to 4-byte alignment.
        int uncompressedTotal = containerHeaderTotal + (int)uncompressedSize;
        int uncompressedPadded = uncompressedTotal + BlfConstants.AlignmentPaddingByteCount(uncompressedTotal);
        _UncompressedBytesWritten += uncompressedPadded;
        _ObjectsWritten += container.ObjectCount;
    }

    /// <summary>
    /// Compresses <paramref name="data"/> with zlib and writes the result into
    /// <paramref name="destination"/>, which is reset before writing.
    /// <para>
    /// Compresses directly into <paramref name="destination"/> via
    /// <see cref="PooledBufferWriteStream"/> so each container flush reuses the
    /// destination array instead of allocating a <see cref="MemoryStream"/>.
    /// </para>
    /// </summary>
    /// <param name="data">The uncompressed container content.</param>
    /// <param name="destination">Receives the complete zlib stream; its previous content is discarded.</param>
    private void _CompressWithZlib(ReadOnlySpan<byte> data, PooledBuffer destination)
    {
        // The zlib stream is declared after (so disposed before) the destination stream: disposing it writes the final
        // deflate block and the zlib trailer, so the destination holds the complete container only after this method
        // returns. leaveOpen keeps destStream usable until its own disposal; the PooledBuffer is never owned by the stream.
        destination.Reset();
        using PooledBufferWriteStream destStream = new(destination);
        using ZLibStream zlib = new(destStream, _Compression, leaveOpen: true);
        zlib.Write(data);
    }

    #endregion

    #region Finish

    /// <summary>
    /// Finalizes the BLF file: computes the final statistics and returns a result that can update the file header. All containers
    /// must have been written (the writer thread joined, lost containers accounted) before this call; it flushes the stream.
    /// </summary>
    /// <param name="endNs">End timestamp in nanoseconds since Unix epoch.
    /// Used for the file header end date.</param>
    /// <returns>A <see cref="BlfWriterFinishResult"/> with the finalized file header.</returns>
    internal BlfWriterFinishResult Finish(long endNs)
    {
        _Stream.Flush();

        // Return the compressed buffer to the pool (no longer needed)
        _CompressedBuf.Return();

        // Build the finalized 144-byte file header with all statistics
        byte[] finalHeader = new byte[FileHeaderSize];
        Span<byte> h = finalHeader;
        h.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(h, BlfConstants.FileMagic);                    // "LOGG"
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), (uint)FileHeaderSize);             // header_length = 144
        // h[8..12] = 0  (api_version, already zeroed)
        // h[12]    = 0  (application, already zeroed)
        h[13] = _CompressionLevelToByte(_Compression);                           // compression_level
        // h[14..16] = 0 (application_major, application_minor, already zeroed)

        long totalFileSize = FileHeaderSize + _BytesWritten;
        // For an uncompressed file the two sizes are identical. For a compressed file
        // len_uncompressed must be the size the file would have if all
        // containers were uncompressed; using the compressed size makes a reader try to
        // scan past the actual end.
        long uncompressedFileSize = FileHeaderSize + _UncompressedBytesWritten;
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(16), (ulong)totalFileSize);            // len_compressed
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(24), (ulong)uncompressedFileSize);     // len_uncompressed
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(32), (uint)_ObjectsWritten);            // obj_count
        // h[36..40] = 0 (application_build, already zeroed)

        _WriteBlfDate(h.Slice(40), _StartNs);                                    // start_date
        _WriteBlfDate(h.Slice(56), endNs);                                       // end_date
        // h[72..76] = 0  (restore_point_offset, already zeroed)
        // h[76..144] = 0 (padding, already zeroed)

        return new BlfWriterFinishResult(finalHeader, _ObjectsWritten, _HeaderPosition);
    }

    /// <summary>
    /// Maps <see cref="CompressionLevel"/> to a byte value for the BLF file header.
    /// Corresponds to the zlib compression level (0=off, 1=fast, 6=default, 9=best).
    /// </summary>
    /// <param name="level">The compression level of the containers.</param>
    /// <returns>The zlib level byte for the header's <c>compression_level</c> field; 6 for a level without a preset.</returns>
    private static byte _CompressionLevelToByte(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.Optimal => 6,
        CompressionLevel.SmallestSize => 9,
        _ => 6,
    };

    #endregion
}

/// <summary>
/// Result of finishing a <see cref="BlfWriter"/>.
/// Contains the finalized 144-byte file header with correct object count,
/// end date, and file size statistics.
/// </summary>
internal sealed class BlfWriterFinishResult
{
    #region Configuration and state

    /// <summary>The complete 144-byte finalized file header that <see cref="UpdateHeader"/> writes.</summary>
    private readonly byte[] _FinalizedHeader;

    /// <summary>Stream position where the file header was written; <see cref="UpdateHeader"/> rewrites it there.</summary>
    private readonly long _HeaderPosition;

    /// <summary>Creates a new finish result.</summary>
    /// <param name="finalizedHeader">The complete 144-byte finalized file header.</param>
    /// <param name="objectCount">Total number of objects written.</param>
    /// <param name="headerPosition">Stream position where the file (and its header) starts; 0 for a fresh stream.</param>
    internal BlfWriterFinishResult(byte[] finalizedHeader, int objectCount, long headerPosition = 0)
    {
        _FinalizedHeader = finalizedHeader;
        ObjectCount = objectCount;
        _HeaderPosition = headerPosition;
    }

    /// <summary>Total number of objects written to the BLF file.</summary>
    internal int ObjectCount
    {
        get;
    }

    #endregion

    #region Header update

    /// <summary>
    /// Seeks to the position where the file started (0 for a fresh stream; the writer's start position when the caller
    /// supplied a stream that already held data) and rewrites the file header with finalized statistics
    /// (object count, end date, file sizes). No-op for non-seekable streams (pipes,
    /// network). In that case, the initial placeholder header (ObjectCount=0,
    /// measurement_end=0) remains — BLF readers count objects themselves and do not
    /// depend on these header statistics.
    /// </summary>
    /// <param name="stream">The stream to update. Must be the same stream used by the writer.</param>
    internal void UpdateHeader(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return;
        }

        // Seek back to the start of this file, not to 0: bytes before it belong to the caller and must stay untouched.
        long endPos = stream.Position;
        stream.Seek(_HeaderPosition, SeekOrigin.Begin);
        stream.Write(_FinalizedHeader);
        stream.Seek(endPos, SeekOrigin.Begin);
    }

    #endregion
}
