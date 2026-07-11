// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// Low-level BLF file writer with container buffering and zlib compression.
/// <para>
/// Objects are accumulated in an internal buffer (up to 10 MB) and periodically
/// flushed as compressed log containers. The 144-byte file header is written
/// immediately on construction with placeholder values; call <see cref="Finish"/>
/// to compute final statistics and optionally rewrite the header via
/// <see cref="BlfWriterFinishResult.UpdateHeader"/>.
/// </para>
/// <para>
/// <b>Seek-free by design:</b> Only <see cref="Stream.Write(ReadOnlySpan{byte})"/>
/// is required during writing — no seeking is needed. The initial header contains
/// conservative placeholder values (<c>ObjectCount=0</c>, <c>measurement_end=0</c>,
/// <c>file_size=0</c>) that are valid for readers. BLF readers (including Vector
/// CANoe and our own <c>BlfSource</c>) count objects themselves and do not rely
/// on the header statistics. Seeking is only used by
/// <see cref="BlfWriterFinishResult.UpdateHeader"/> to rewrite the file header at
/// position 0 after all data is written — this is a best-effort optimization for
/// seekable streams. For non-seekable streams (pipes, network), the header
/// placeholders remain and the file is still valid.
/// </para>
/// </summary>
internal sealed class BlfWriter
{
    /// <summary>BLF file header size in bytes.</summary>
    private const int _FileHeaderSize = 144;

    /// <summary>Combined size of block header (16) + log object header V1 (16).</summary>
    private const int _ObjectHeaderTotalSize =
        BlfConstants.BlockHeaderSize + BlfConstants.LogObjectHeaderType1Size; // 32

    /// <summary>Maximum uncompressed container buffer size before flushing (10 MB).</summary>
    private const int _MaxContainerBufferSize = BlfConstants.MaxContainerBufferSize;

    /// <summary>BLF timestamp resolution flag value: 10 µs units.</summary>
    private const uint _TimestampResolution10Us = BlfConstants.TimestampResolution10Us;

    /// <summary>Nanoseconds per 10 µs tick (the BLF timestamp unit).</summary>
    private const long _NanosPerTick = 10_000;

    private readonly Stream _Stream;
    private long _StartNs;
    private readonly CompressionLevel _Compression;
    private readonly PooledBuffer _ContainerBuffer;

    /// <summary>
    /// Pre-allocated buffer for zlib-compressed container output.
    /// Reused across <see cref="FlushContainer"/> calls to avoid per-flush allocations.
    /// Grows as needed (worst-case zlib output is slightly larger than input).
    /// </summary>
    private readonly PooledBuffer _CompressedBuf = new(0);

    private int _ObjectCount;
    private long _BytesWritten; // bytes written to stream after the file header

    /// <summary>
    /// Count of frames whose absolute timestamp was earlier than <see cref="_StartNs"/>.
    /// A non-zero value indicates frames with negative relative timestamps that were
    /// clamped to zero.  Callers can poll this to surface a diagnostic to the user.
    /// </summary>
    private long _NonMonotonicTimestampCount;

    // Total size the file would have if all containers were stored uncompressed.
    // This is the value Wireshark/tshark expects in the LOGG header `len_uncompressed`
    // field; using the compressed file size instead causes tshark to read past the
    // last frame and report "appears to have been cut short". Each flushed container
    // contributes (16 LOBJ block header + 16 container header + uncompressed content +
    // 4-byte alignment padding).
    private long _UncompressedBytesWritten;

    /// <summary>
    /// Creates a BLF writer and writes the 144-byte file header immediately.
    /// </summary>
    /// <param name="stream">Target output stream.</param>
    /// <param name="startNs">_Start timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="compression">Compression level for container output. Use
    /// <see cref="CompressionLevel.NoCompression"/> to disable compression.</param>
    internal BlfWriter(
        Stream stream,
        long startNs,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        _Stream = stream;
        // The BLF file header stores `start_date` as a Windows SYSTEMTIME with millisecond
        // precision. The per-object `timestamp` field is a 10 µs tick offset relative to
        // `start_date`. Readers (tshark, Vector tools, our own source) reconstruct each
        // frame's absolute time as `start_date_ns + relative_ticks * 10 µs`. If we kept
        // sub-millisecond precision in `_StartNs` it would silently disappear in the file
        // header and never be added back via the relative ticks, shifting every frame by
        // up to ~1 ms. Round down to ms here so the relative-tick computation absorbs the
        // residual sub-ms offset and the round-trip is exact.
        _StartNs = (startNs / 1_000_000L) * 1_000_000L;
        _Compression = compression;
        _ContainerBuffer = new PooledBuffer(_MaxContainerBufferSize);
        _WriteFileHeader(_StartNs);
    }

    /// <summary>LOGG <c>start_date</c> anchor used for relative object timestamps (floored to whole milliseconds).</summary>
    internal long AnchorStartNanos => _StartNs;

    /// <summary>
    /// If no objects have been written yet, the container buffer is empty, the stream is
    /// seekable, and <paramref name="earliestAbsoluteNanos"/> floor-ms is earlier than the
    /// current anchor, rewinds and rewrites only the 144-byte file header so the anchor
    /// matches the minimum seen timestamp. This absorbs out-of-order arrivals where a
    /// later frame opened the file before an earlier timestamp was observed, as long as
    /// no LOBJ data was flushed yet.
    /// </summary>
    internal bool TryRealignStartEarlier(long earliestAbsoluteNanos)
    {
        long newStart = (earliestAbsoluteNanos / 1_000_000L) * 1_000_000L;
        if (newStart >= _StartNs || _ObjectCount != 0 || _ContainerBuffer.Length != 0)
        {
            return false;
        }

        if (!_Stream.CanSeek || _Stream.Position != _FileHeaderSize)
        {
            return false;
        }

        // Flush any buffered stream data before reading Position, so that
        // the position reflects bytes actually written and not bytes pending in a
        // BufferedStream or FileStream internal buffer that hasn't yet been flushed.
        _Stream.Flush();
        if (_Stream.Position != _FileHeaderSize)
        {
            return false;
        }

        _StartNs = newStart;
        _Stream.Seek(0, SeekOrigin.Begin);
        _WriteFileHeader(_StartNs);
        return _Stream.Position == _FileHeaderSize;
    }

    /// <summary>
    /// Number of objects whose timestamp was earlier than the file's start anchor
    /// (<see cref="AnchorStartNanos"/>) and whose relative tick was clamped to zero.
    /// A non-zero count indicates the caller supplied out-of-order timestamps; the
    /// affected frames appear with a relative timestamp of 0 in the BLF output.
    /// </summary>
    internal long NonMonotonicTimestampCount => _NonMonotonicTimestampCount;

    /// <summary>Number of objects written so far.</summary>
    internal int ObjectCount => _ObjectCount;

    /// <summary>
    /// Writes a raw BLF object into the container buffer.
    /// The buffer is flushed automatically when it would exceed the 10 MB threshold.
    /// <para>
    /// <b>Container boundary invariant:</b> A BLF object is always fully contained
    /// within a single container — it is never split across container boundaries.
    /// If adding this object would exceed the container limit, the current container
    /// is flushed first, and the object starts in a fresh container. A single object
    /// larger than the container limit is written into its own dedicated container
    /// (the check <c>_ContainerBuffer.Length &gt; 0</c> prevents a flush when the
    /// buffer is empty).
    /// </para>
    /// </summary>
    /// <param name="objectType">BLF object type (e.g., <see cref="BlfConstants.ObjTypeEthernetFrame"/>).</param>
    /// <param name="objectVersion">Object structure version (typically 0).</param>
    /// <param name="timestampNs">Absolute timestamp in nanoseconds since Unix epoch.</param>
    /// <param name="payload">Pre-built object payload bytes.</param>
    internal void WriteRawObject(
        uint objectType,
        ushort objectVersion,
        long timestampNs,
        ReadOnlySpan<byte> payload)
    {
        // Convert absolute Unix nanos to relative 10 µs ticks
        long relativeNs = timestampNs - _StartNs;
        if (relativeNs < 0)
        {
            // Record the non-monotonic event and clamp to 0 so the output
            // remains a valid BLF file (negative tick offsets are not representable).
            // Callers can poll NonMonotonicTimestampCount to surface a diagnostic.
            _NonMonotonicTimestampCount++;
            relativeNs = 0;
        }
        ulong blfTimestamp = (ulong)(relativeNs / _NanosPerTick);

        // Calculate object sizes with 4-byte alignment
        int rawObjectSize = _ObjectHeaderTotalSize + payload.Length;
        int padding = (4 - (rawObjectSize & 3)) & 3;
        int totalObjectSize = rawObjectSize + padding;

        // If adding this object would exceed the container limit, flush first.
        // This guarantees no object spans container boundaries.
        if (_ContainerBuffer.Length > 0
            && _ContainerBuffer.Length + totalObjectSize > _MaxContainerBufferSize)
        {
            FlushContainer();
        }

        // Reserve space for the entire object (headers + payload + padding)
        Span<byte> buf = _ContainerBuffer.Reserve(totalObjectSize);

        // -- Block header (16 bytes) --
        BinaryPrimitives.WriteUInt32LittleEndian(buf, BlfConstants.ObjectMagic);               // "LOBJ" signature
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(4), (ushort)_ObjectHeaderTotalSize); // header_size = 32
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(6), 1);                            // header_type = V1
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(8), (uint)rawObjectSize);           // object_length (unpadded)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(12), objectType);                   // object_type

        // -- Log object header V1 (16 bytes) per Vector blf_logobjectheader_t --
        //   uint32 flags (4) | uint16 client_index (2) | uint16 object_version (2) | uint64 object_timestamp (8)
        // The previous layout (timestamp first) does not match Vector/Wireshark and produces files where
        // tshark cannot derive a usable frame.time_epoch.
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16), _TimestampResolution10Us);      // flags (resolution = 10 µs)
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(20), 0);                            // client_index
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(22), objectVersion);                // object_version
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(24), blfTimestamp);                 // object_timestamp (10 µs units)

        // -- Payload --
        payload.CopyTo(buf.Slice(_ObjectHeaderTotalSize));

        // -- 4-byte alignment padding (zeros) --
        if (padding > 0)
        {
            buf.Slice(rawObjectSize, padding).Clear();
        }

        _ObjectCount++;
    }

    /// <summary>
    /// Flushes the current container buffer to the output stream.
    /// Compresses the accumulated objects with zlib if compression is enabled,
    /// then writes the container (block header + log obj header + container header + data).
    /// </summary>
    internal void FlushContainer()
    {
        if (_ContainerBuffer.Length == 0)
        {
            return;
        }

        uint uncompressedSize = (uint)_ContainerBuffer.Length;
        ushort compressionMethod;
        int compressedLen;

        if (_Compression == CompressionLevel.NoCompression)
        {
            // No compression — write raw buffer data directly
            compressionMethod = BlfConstants.CompressionNone;
            compressedLen = _ContainerBuffer.Length;
        }
        else
        {
            // Compress the buffer with zlib into the pre-allocated _CompressedBuf.
            // This avoids the double allocation that MemoryStream.ToArray() caused
            // (the MemoryStream internal buffer plus the extra ToArray copy).
            compressionMethod = BlfConstants.CompressionZlib;
            _CompressWithZlib(_ContainerBuffer.WrittenSpan, _CompressedBuf);
            compressedLen = _CompressedBuf.Length;
        }

        // Container layout per Vector blf.h / Wireshark blf.c (blf_dump_start_logcontainer):
        //   block_header(16) + container_header(16) + data + 4-byte alignment padding
        // The block header's `header_length` is the size of the block header alone (16),
        // NOT block+container. Wireshark's reader treats `header_length - 16` as additional
        // unknown padding it must skip, then reads a fixed 16-byte container header.
        const int blockHeaderSize = BlfConstants.BlockHeaderSize;                 // 16
        const int containerHeaderSize = BlfConstants.ContainerHeaderSize;         // 16
        const int containerHeaderTotal = blockHeaderSize + containerHeaderSize;   // 32
        int totalLength = containerHeaderTotal + compressedLen;
        int paddedLength = (totalLength + 3) & ~3;
        int containerPadding = paddedLength - totalLength;

        // Write 32-byte combined header: block(16) + container(16)
        Span<byte> headerBuf = stackalloc byte[containerHeaderTotal];
        headerBuf.Clear();

        // Block header for the container object
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf, BlfConstants.ObjectMagic);               // "LOBJ"
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.Slice(4), (ushort)blockHeaderSize);    // header_length = 16 (block header only) per Vector/Wireshark
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.Slice(6), 1);                            // header_type = V1
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.Slice(8), (uint)paddedLength);   // object_length (padded total = block + container + payload + pad)
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
            _Stream.Write(_ContainerBuffer.WrittenSpan);
        }

        // Write 4-byte alignment padding
        if (containerPadding > 0)
        {
            Span<byte> pad = stackalloc byte[4];
            pad.Clear();
            _Stream.Write(pad.Slice(0, containerPadding));
        }

        _BytesWritten += paddedLength;

        // Track the size this container would have if stored uncompressed, so we can
        // populate the LOGG `len_uncompressed` field correctly (see field comment).
        // The headers are NOT compressed in either mode, only the payload is, so the
        // uncompressed-equivalent length is 32 (block + container header) + raw payload,
        // padded to 4-byte alignment.
        int uncompressedTotal = containerHeaderTotal + (int)uncompressedSize;
        int uncompressedPadded = (uncompressedTotal + 3) & ~3;
        _UncompressedBytesWritten += uncompressedPadded;

        _ContainerBuffer.Reset();
    }

    /// <summary>
    /// Finalizes the BLF file: flushes remaining buffered objects, computes
    /// final statistics, and returns a result that can update the file header.
    /// </summary>
    /// <param name="endNs">End timestamp in nanoseconds since Unix epoch.
    /// Used for the file header end date.</param>
    /// <returns>A <see cref="BlfWriterFinishResult"/> with the finalized file header.</returns>
    internal BlfWriterFinishResult Finish(long endNs)
    {
        FlushContainer();
        _Stream.Flush();

        // Return the container buffer and compressed buffer to the pool (no longer needed)
        _ContainerBuffer.Return();
        _CompressedBuf.Return();

        // Build the finalized 144-byte file header with all statistics
        byte[] finalHeader = new byte[_FileHeaderSize];
        Span<byte> h = finalHeader;
        h.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(h, BlfConstants.FileMagic);                    // "LOGG"
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), (uint)_FileHeaderSize);             // header_length = 144
        // h[8..12] = 0  (api_version, already zeroed)
        // h[12]    = 0  (application, already zeroed)
        h[13] = _CompressionLevelToByte(_Compression);                           // compression_level
        // h[14..16] = 0 (application_major, application_minor, already zeroed)

        long totalFileSize = _FileHeaderSize + _BytesWritten;
        // For an uncompressed file the two sizes are identical. For a compressed file
        // tshark requires len_uncompressed to be the size the file would have if all
        // containers were uncompressed; using the compressed size makes tshark try to
        // read past the actual end and report "appears to have been cut short".
        long uncompressedFileSize = _FileHeaderSize + _UncompressedBytesWritten;
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(16), (ulong)totalFileSize);            // len_compressed
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(24), (ulong)uncompressedFileSize);     // len_uncompressed
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(32), (uint)_ObjectCount);              // obj_count
        // h[36..40] = 0 (application_build, already zeroed)

        _WriteBlfDate(h.Slice(40), _StartNs);                                    // start_date
        _WriteBlfDate(h.Slice(56), endNs);                                       // end_date
        // h[72..76] = 0  (restore_point_offset, already zeroed)
        // h[76..144] = 0 (padding, already zeroed)

        return new BlfWriterFinishResult(finalHeader, _ObjectCount);
    }

    // ========================================================================
    // Private helpers
    // ========================================================================

    /// <summary>Writes the initial 144-byte BLF file header with placeholder values.</summary>
    private void _WriteFileHeader(long startNs)
    {
        Span<byte> header = stackalloc byte[_FileHeaderSize];
        header.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(header, BlfConstants.FileMagic);                // "LOGG"
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)_FileHeaderSize);         // header_length = 144
        // api_version, application, app_major, app_minor = 0 (already zeroed)
        header[13] = _CompressionLevelToByte(_Compression);                       // compression_level
        // len_compressed, len_uncompressed, obj_count, app_build = 0 (placeholder)
        _WriteBlfDate(header.Slice(40), startNs);                                 // start_date
        // end_date = 0 (placeholder)

        _Stream.Write(header);
    }

    /// <summary>
    /// Compresses <paramref name="data"/> with zlib and writes the result into
    /// <paramref name="destination"/>, which is reset before writing.
    /// <para>
    /// Uses <see cref="MemoryStream.GetBuffer"/> to access the internal buffer of the
    /// intermediate <see cref="MemoryStream"/> directly (no extra copy), then copies
    /// into <paramref name="destination"/>. This eliminates the second allocation that
    /// <c>MemoryStream.ToArray()</c> caused (MEDIUM-6 fix).
    /// </para>
    /// </summary>
    private void _CompressWithZlib(ReadOnlySpan<byte> data, PooledBuffer destination)
    {
        destination.Reset();
        using MemoryStream ms = new(capacity: data.Length);
        using (ZLibStream zlib = new(ms, _Compression, leaveOpen: true))
        {
            zlib.Write(data);
        }
        // GetBuffer() returns the raw internal array (may be larger than written data);
        // use ms.Length to get the number of valid bytes — no copy to a new array.
        destination.Write(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>
    /// Writes a 16-byte BLF date (Windows SYSTEMTIME layout) from Unix nanoseconds.
    /// Layout: Year(2) + Month(2) + DayOfWeek(2) + Day(2) + Hour(2) + Minute(2) + Second(2) + Millisecond(2).
    /// <para>
    /// The fields are written in the system's <b>local</b> time zone because both Vector's
    /// reference tooling and Wireshark/tshark interpret the on-disk SYSTEMTIME using the
    /// reader's local timezone (via <c>mktime</c>). Writing UTC components instead would
    /// shift every reported frame timestamp by the local UTC offset.
    /// </para>
    /// </summary>
    private static void _WriteBlfDate(Span<byte> dest, long unixNanos)
    {
        if (unixNanos <= 0)
        {
            dest.Slice(0, 16).Clear();
            return;
        }

        DateTimeOffset dto = DateTimeOffset.FromUnixTimeMilliseconds(unixNanos / 1_000_000);
        DateTime local = dto.LocalDateTime;

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)local.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2), (ushort)local.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4), (ushort)local.DayOfWeek); // Sunday = 0
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(6), (ushort)local.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(8), (ushort)local.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(10), (ushort)local.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(12), (ushort)local.Second);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(14), (ushort)(unixNanos / 1_000_000 % 1_000));
    }

    /// <summary>
    /// Maps <see cref="CompressionLevel"/> to a byte value for the BLF file header.
    /// Corresponds to the zlib compression level (0=off, 1=fast, 6=default, 9=best).
    /// </summary>
    private static byte _CompressionLevelToByte(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.Optimal => 6,
        CompressionLevel.SmallestSize => 9,
        _ => 6,
    };
}

/// <summary>
/// Result of finishing a <see cref="BlfWriter"/>.
/// Contains the finalized 144-byte file header with correct object count,
/// end date, and file size statistics.
/// </summary>
internal sealed class BlfWriterFinishResult
{
    private readonly byte[] _FinalizedHeader;

    /// <summary>Total number of objects written to the BLF file.</summary>
    internal int ObjectCount
    {
        get;
    }

    /// <summary>Creates a new finish result.</summary>
    /// <param name="finalizedHeader">The complete 144-byte finalized file header.</param>
    /// <param name="objectCount">Total number of objects written.</param>
    internal BlfWriterFinishResult(byte[] finalizedHeader, int objectCount)
    {
        _FinalizedHeader = finalizedHeader;
        ObjectCount = objectCount;
    }

    /// <summary>
    /// Seeks to position 0 and rewrites the file header with finalized statistics
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

        long endPos = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(_FinalizedHeader);
        stream.Seek(endPos, SeekOrigin.Begin);
    }
}
