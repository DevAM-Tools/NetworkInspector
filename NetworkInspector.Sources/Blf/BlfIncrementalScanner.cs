// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Incremental scanner for BLF files.
/// Implements two-level iteration:
///   1. Outer loop: reads LOBJ block headers from the raw file data
///   2. Inner loop: iterates objects within decompressed container data
///
/// The scanner builds a <see cref="BlfFrameIndex"/> and processes
/// AppText objects for channel name discovery. It supports corruption
/// recovery via LOBJ magic scanning.
/// </summary>
/// <remarks>
/// <para><b>Padding:</b> <c>object_length</c> is the unpadded object. After a valid header,
/// the scanner advances only <c>max(max(16, object_length), header_size)</c>. Trailing
/// 0–3 zeros (when present) are consumed by the 1-byte <c>LOBJ</c> scan, not by adding a
/// computed 4-byte pad. Packed writers that omit those zeros are still readable.</para>
/// <para><b>Cross-container objects:</b> A defective writer may split one inner LOBJ across
/// two decompressed containers. Leftover bytes that look like a partial <c>LOBJ</c> are
/// prepended to the next decompressed blob (capped by
/// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/>). Index entries store the
/// file offset of the container where the object starts and the offset inside that
/// container's own decompressed bytes; random access stitches subsequent containers when
/// <c>object_length</c> overruns the first blob. Nested containers are still rejected.
/// Padding-only tails are discarded without counting a truncated object.</para>
/// <para><b>Thread-safety:</b> This class is <b>not</b> thread-safe.
/// All scanning must occur from a single thread.</para>
/// </remarks>
internal sealed class BlfIncrementalScanner
{
    #region Fields

    private readonly BlfDataBackend _Backend;
    private readonly BlfFileInfo _FileInfo;
    private readonly BlfFrameIndex _Index;
    private readonly Dictionary<(byte BusType, byte Channel), string> _ChannelNames = new();
    private readonly long _MaxUncompressedContainerSize;

    // Outer loop state
    private long _FileOffset;
    private bool _Exhausted;

    /// <summary>Number of containers that failed to decompress.</summary>
    private long _DecompressionFailures;

    // Inner loop state — pending decompressed container (may include a carry-over prefix)
    private byte[]? _PendingContainer;
    private int _ContainerOffset;
    private long _ContainerFileOffset;

    /// <summary>
    /// Incomplete inner-object bytes saved from the previous container tail, prepended to
    /// the next decompressed blob. Null when no carry is pending.
    /// </summary>
    private byte[]? _CarryOver;

    /// <summary>
    /// Byte length of the carry-over prefix inside <see cref="_PendingContainer"/>.
    /// Zero when the pending blob is a single container with no stitch.
    /// </summary>
    private int _CarryPrefixLength;

    /// <summary>File offset of the container that owns the current carry-over bytes.</summary>
    private long _CarrySourceFileOffset;

    /// <summary>
    /// Offset of the carry-over start inside the source container's own decompressed bytes
    /// (not the stitched buffer).
    /// </summary>
    private int _CarrySourceInnerOffset;

    /// <summary>
    /// Number of containers where the container header offset fell outside the object body.
    /// Incremented inside <see cref="_ProcessContainer"/> on bounds violations.
    /// BlfSource polls this and forwards each new failure through the error-tolerance pipeline.
    /// </summary>
    private long _CorruptedContainerCount;

    /// <summary>
    /// Number of container tails that were neither padding nor a recoverable partial LOBJ.
    /// BlfSource polls this and forwards each new truncation through the error-tolerance pipeline.
    /// </summary>
    private long _TruncatedObjectCount;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new scanner for the given BLF file backend.
    /// </summary>
    /// <param name="backend">Data backend providing access to the BLF file bytes.</param>
    /// <param name="fileInfo">Parsed file header info.</param>
    /// <param name="index">Frame index to populate.</param>
    /// <param name="maxUncompressedContainerSize">
    /// Maximum allowed uncompressed size per container in bytes.
    /// A value of <c>0</c> disables the check.
    /// </param>
    internal BlfIncrementalScanner(
        BlfDataBackend backend,
        BlfFileInfo fileInfo,
        BlfFrameIndex index,
        long maxUncompressedContainerSize = 0)
    {
        _Backend = backend;
        _FileInfo = fileInfo;
        _Index = index;
        _MaxUncompressedContainerSize = maxUncompressedContainerSize;
        _FileOffset = fileInfo.HeaderSize; // Objects start after file header
    }

    #endregion

    #region Properties

    /// <summary>Whether the scanner has processed all data.</summary>
    internal bool IsExhausted => _Exhausted;

    /// <summary>Discovered channel names (bus type + channel → name).</summary>
    internal IReadOnlyDictionary<(byte BusType, byte Channel), string> ChannelNames => _ChannelNames;

    /// <summary>
    /// Number of containers that failed to decompress.
    /// Callers can poll this after <see cref="ScanNext"/> to report decompression errors
    /// through the error tolerance mechanism.
    /// </summary>
    internal long DecompressionFailures => _DecompressionFailures;

    /// <summary>
    /// Number of containers whose header offset was out of bounds (corrupt <c>headerSize</c> field).
    /// Callers can poll this after <see cref="ScanNext"/> to report corruption diagnostics
    /// through the error tolerance mechanism.
    /// </summary>
    internal long CorruptedContainerCount => _CorruptedContainerCount;

    /// <summary>
    /// Number of container tails that could not be parsed as a partial LOBJ and were discarded.
    /// Callers can poll this after <see cref="ScanNext"/> to report truncation diagnostics
    /// through the error tolerance mechanism.
    /// </summary>
    internal long TruncatedObjectCount => _TruncatedObjectCount;

    #endregion

    #region Internal API

    /// <summary>
    /// Scans the next batch of frames from the BLF file.
    /// Returns true if at least one frame was found; false when exhausted.
    /// Processes one container or one raw object per call.
    /// Uses windowed reads via <see cref="BlfDataBackend.GetSpan"/> so that
    /// files larger than 2 GiB are handled correctly.
    /// </summary>
    internal bool ScanNext(CancellationToken cancellationToken = default)
    {
        // First: drain any pending container objects
        if (_PendingContainer is not null)
        {
            if (_DrainPendingContainer())
            {
                return true;
            }
        }

        // Outer loop: scan raw file for LOBJ blocks via the backend.
        // _FileOffset and _Backend.FileSize are both long, so files beyond
        // 2 GiB are handled correctly without any int.MaxValue cap.
        while (_FileOffset + BlfConstants.BlockHeaderSize <= _Backend.FileSize && !_Exhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fetch just enough bytes for the block header.
            int headerFetchSize = (int)Math.Min(BlfConstants.BlockHeaderSize, _Backend.FileSize - _FileOffset);
            ReadOnlySpan<byte> blockData = _Backend.GetSpan(_FileOffset, headerFetchSize);

            // Try to read block header
            if (!BlfBlockHeader.TryParse(blockData, out BlfBlockHeader blockHeader, out _))
            {
                // Try scanning forward for LOBJ magic (corruption recovery)
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            // Validate LOBJ signature
            if (blockHeader.Signature.Value != BlfConstants.ObjectMagic)
            {
                // Not a valid block — scan forward byte by byte for "LOBJ"
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            ushort headerSz = blockHeader.HeaderSize.Value;
            uint objectLength = blockHeader.ObjectLength.Value;
            uint objectType = blockHeader.ObjectType.Value;

            // Explicit validation of block header fields before skip-distance
            // computation. Each check guards a distinct corruption or attack scenario:
            //   - headerSz < BlockHeaderSize: the block header itself claims to be shorter
            //     than the minimum 16-byte layout, which is structurally invalid.
            //   - objectLength == 0: would produce a skip of 0, causing an infinite loop
            //     at the same file offset.
            //   - skipDistance > MaxBlockReadSize: untrusted object_length must not force
            //     an unbounded GetSpan.
            // On any violation, attempt corruption-recovery by scanning for the next LOBJ magic.
            if (headerSz < BlfConstants.BlockHeaderSize)
            {
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            if (objectLength == 0)
            {
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            long skipDistanceLong = Math.Max(Math.Max((long)headerSz, BlfConstants.BlockHeaderSize), objectLength);
            if (skipDistanceLong > BlfConstants.MaxBlockReadSize)
            {
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            int skipDistance = (int)skipDistanceLong;

            // Validate we have enough data for the full object
            if (_FileOffset + skipDistance > _Backend.FileSize)
            {
                _Exhausted = true;
                return false;
            }

            // Fetch the complete object as a windowed span. Pad after the object is not
            // included: the next loop iteration's signature check / 1-byte scan consumes it.
            ReadOnlySpan<byte> fullObjectData = _Backend.GetSpan(_FileOffset, skipDistance);
            long currentOffset = _FileOffset;
            _FileOffset += skipDistance;

            if (objectType == BlfConstants.ObjTypeLogContainer)
            {
                // Process container: decompress and set up inner iteration
                _ProcessContainer(fullObjectData, headerSz, currentOffset);
                if (_PendingContainer is not null && _DrainPendingContainer())
                {
                    return true;
                }
                continue;
            }

            if (objectType == BlfConstants.ObjTypeAppText)
            {
                // Process AppText for channel names
                _ProcessAppText(fullObjectData);
                continue;
            }

            // Try to parse as a frame-producing object
            if (BlfConstants.IsFrameProducingType(objectType))
            {
                _ProcessRawObject(fullObjectData, currentOffset);
                return true;
            }
        }

        _Exhausted = true;
        return false;
    }

    /// <summary>
    /// Scans all remaining data to exhaustion, populating the index.
    /// </summary>
    internal void ScanToEnd(CancellationToken cancellationToken = default)
    {
        while (ScanNext(cancellationToken))
        {
            // Keep scanning
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Decompresses a container and sets up the pending container for inner iteration.
    /// Prepends any carry-over tail from the previous container.
    /// </summary>
    private void _ProcessContainer(ReadOnlySpan<byte> objectData, ushort headerSize, long fileOffset)
    {
        // Container layout: [block header (16B)] [log object header] [container header (16B)] [payload]
        // The log object header size varies; container header starts after headerSize from the beginning.
        // But the container header is embedded after the log object header, within the headerSize region.
        // Payload starts after headerSize, container header is at headerSize - ContainerHeaderSize.

        // Bounds-check the container header offset before slicing.
        // A crafted huge headerSize would put containerHeaderOffset beyond objectData,
        // silently losing the container. Increment _CorruptedContainerCount so BlfSource
        // can surface the diagnostic through the error-tolerance pipeline.
        int containerHeaderOffset = Math.Max((int)headerSize, BlfConstants.BlockHeaderSize);
        int payloadOffset = containerHeaderOffset + BlfConstants.ContainerHeaderSize;
        if (containerHeaderOffset < 0 || containerHeaderOffset + BlfConstants.ContainerHeaderSize > objectData.Length)
        {
            _DropCarryAsTruncated();
            _CorruptedContainerCount++;
            return;
        }

        ReadOnlySpan<byte> containerHeaderData = objectData[containerHeaderOffset..];
        if (!BlfContainerHeader.TryParse(containerHeaderData, out BlfContainerHeader containerHeader, out _))
        {
            _DropCarryAsTruncated();
            return;
        }

        ushort compressionMethod = containerHeader.CompressionMethod.Value;
        uint uncompressedSize = containerHeader.UncompressedSize.Value;

        // Compressed/raw payload starts after block header + container header.
        ReadOnlySpan<byte> payloadData = objectData.Length > payloadOffset
            ? objectData[payloadOffset..]
            : ReadOnlySpan<byte>.Empty;

        if (payloadData.IsEmpty)
        {
            // Empty payload cannot complete a carried object; keep carry for a later container.
            return;
        }

        try
        {
            byte[] decoded = BlfContainer.Decompress(payloadData, compressionMethod, uncompressedSize,
                _MaxUncompressedContainerSize);
            _PendingContainer = _PrependCarry(decoded);
            _ContainerOffset = 0;
            _ContainerFileOffset = fileOffset;
        }
        catch (Exception ex) when (ex is BlfException or OutOfMemoryException)
        {
            // Decompression failed (format error or OOM from untrusted uncompressedSize)
            // — skip this container. No partial state is committed: _PendingContainer stays
            // null and only the failure counter advances. The caller (BlfSource) reports
            // failures via DecompressionFailures.
            // BlfDecompressionLimitExceededException is intentionally not caught here
            // — it propagates to the BlfSource scanning path so the caller can react.
            _DropCarryAsTruncated();
            _PendingContainer = null;
            _DecompressionFailures++;
        }
    }

    /// <summary>
    /// Prepends pending carry-over bytes to <paramref name="decoded"/>. Drops the carry
    /// (and counts a truncation) when the stitched size would exceed the decompress cap.
    /// </summary>
    private byte[] _PrependCarry(byte[] decoded)
    {
        if (_CarryOver is not { Length: > 0 })
        {
            _CarryPrefixLength = 0;
            return decoded;
        }

        long total = (long)_CarryOver.Length + decoded.Length;
        if (total > int.MaxValue
            || (_MaxUncompressedContainerSize > 0 && total > _MaxUncompressedContainerSize))
        {
            _DropCarryAsTruncated();
            _CarryPrefixLength = 0;
            return decoded;
        }

        int prefix = _CarryOver.Length;
        byte[] stitched = new byte[(int)total];
        Buffer.BlockCopy(_CarryOver, 0, stitched, 0, prefix);
        Buffer.BlockCopy(decoded, 0, stitched, prefix, decoded.Length);
        _CarryOver = null;
        _CarryPrefixLength = prefix;
        return stitched;
    }

    /// <summary>
    /// Drains objects from the pending decompressed container.
    /// Returns true if at least one frame-producing object was found.
    /// </summary>
    private bool _DrainPendingContainer()
    {
        if (_PendingContainer is null)
        {
            return false;
        }

        bool foundFrame = false;
        ReadOnlySpan<byte> containerSpan = _PendingContainer.AsSpan();

        while (_ContainerOffset + BlfConstants.BlockHeaderSize <= containerSpan.Length)
        {
            ReadOnlySpan<byte> objectData = containerSpan[_ContainerOffset..];

            if (!BlfObjectHeaderParser.TryParse(objectData, _ContainerFileOffset + _ContainerOffset,
                    out BlfObjectInfo objInfo, out int skipDistance))
            {
                // Try to find next LOBJ magic within container
                int nextMagic = _FindLobjMagic(containerSpan[(_ContainerOffset + 1)..]);
                if (nextMagic < 0)
                {
                    // Remainder may be a split object or padding — finish rather than drop it.
                    _FinishContainer(containerSpan);
                    return foundFrame;
                }
                _ContainerOffset += 1 + nextMagic;
                continue;
            }

            int objectStart = _ContainerOffset;
            _ContainerOffset += skipDistance;

            // Handle AppText within containers
            if (objInfo.ObjectType == BlfConstants.ObjTypeAppText)
            {
                _ProcessAppTextPayload(objInfo.Payload);
                continue;
            }

            // Index frame-producing objects from channel fields only — do not reconstruct FrameData.
            if (BlfConstants.IsFrameProducingType(objInfo.ObjectType)
                && BlfFrameDispatcher.TryGetChannel(objInfo.ObjectType, objInfo.Payload, out ushort ch))
            {
                BlfFrameEntry entry = _MakeContainerEntry(objectStart, skipDistance, in objInfo, ch);
                _Index.Push(in entry);
                foundFrame = true;
            }
        }

        _FinishContainer(containerSpan);
        return foundFrame;
    }

    /// <summary>
    /// Builds an index entry whose container offset and inner offset refer to the container
    /// where the object starts (the previous blob when the object still sits in the carry prefix).
    /// </summary>
    private BlfFrameEntry _MakeContainerEntry(int objectStart, int skipDistance, in BlfObjectInfo objInfo, ushort channel)
    {
        long containerOffset;
        int innerOffset;
        if (objectStart < _CarryPrefixLength)
        {
            containerOffset = _CarrySourceFileOffset;
            innerOffset = _CarrySourceInnerOffset + objectStart;
        }
        else
        {
            containerOffset = _ContainerFileOffset;
            innerOffset = objectStart - _CarryPrefixLength;
        }

        return new BlfFrameEntry
        {
            ContainerOffset = containerOffset,
            ObjectOffset = innerOffset,
            ObjectLength = skipDistance,
            ObjectType = objInfo.ObjectType,
            Channel = channel,
            HeaderSize = 0, // Not needed for container objects
            TimestampNanos = _FileInfo.StartOffsetNanos + objInfo.TimestampNanos,
        };
    }

    /// <summary>
    /// Saves a partial LOBJ tail as carry-over, discards padding, or counts a truncated object.
    /// </summary>
    private void _FinishContainer(ReadOnlySpan<byte> containerSpan)
    {
        if (_ContainerOffset < containerSpan.Length)
        {
            ReadOnlySpan<byte> tail = containerSpan[_ContainerOffset..];
            if (_LooksLikePartialLobj(tail) && !_DeclaredObjectLengthExceedsCap(tail))
            {
                if (_ContainerOffset < _CarryPrefixLength)
                {
                    // Still begins in the previous container — keep that source identity.
                    _CarryOver = tail.ToArray();
                }
                else
                {
                    _CarryOver = tail.ToArray();
                    _CarrySourceFileOffset = _ContainerFileOffset;
                    _CarrySourceInnerOffset = _ContainerOffset - _CarryPrefixLength;
                }
            }
            else if (!_IsAllZeros(tail))
            {
                _TruncatedObjectCount++;
                _CarryOver = null;
            }
            else
            {
                _CarryOver = null;
            }
        }
        else
        {
            _CarryOver = null;
        }

        _PendingContainer = null;
        _ContainerOffset = 0;
        _CarryPrefixLength = 0;
    }

    /// <summary>Drops a pending carry and counts it as a truncated object when non-empty.</summary>
    private void _DropCarryAsTruncated()
    {
        if (_CarryOver is { Length: > 0 })
        {
            _TruncatedObjectCount++;
        }

        _CarryOver = null;
        _CarryPrefixLength = 0;
    }

    /// <summary>
    /// True when <paramref name="tail"/> is padding zeros, optionally followed by a
    /// <c>LOBJ</c> magic or a truncated prefix of that magic (a split object).
    /// </summary>
    private static bool _LooksLikePartialLobj(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        int i = 0;
        while (i < tail.Length && tail[i] == 0)
        {
            i++;
        }

        if (i == tail.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> candidate = tail[i..];
        int n = Math.Min(candidate.Length, BlfConstants.ObjectMagicBytes.Length);
        return candidate[..n].SequenceEqual(BlfConstants.ObjectMagicBytes[..n]);
    }

    /// <summary>
    /// True when <paramref name="tail"/> begins with a parseable <c>LOBJ</c> header whose
    /// skip distance exceeds <see cref="BlfConstants.MaxBlockReadSize"/>. Those tails are
    /// corrupt, not split objects, and must not be carried into the next container.
    /// </summary>
    private static bool _DeclaredObjectLengthExceedsCap(ReadOnlySpan<byte> tail)
    {
        int i = 0;
        while (i < tail.Length && tail[i] == 0)
        {
            i++;
        }

        ReadOnlySpan<byte> candidate = tail[i..];
        if (candidate.Length < BlfConstants.BlockHeaderSize)
        {
            return false;
        }

        if (!BlfBlockHeader.TryParse(candidate, out BlfBlockHeader header, out _)
            || header.Signature.Value != BlfConstants.ObjectMagic)
        {
            return false;
        }

        long skip = Math.Max(
            Math.Max((long)BlfConstants.BlockHeaderSize, header.ObjectLength.Value),
            header.HeaderSize.Value);
        return skip > BlfConstants.MaxBlockReadSize;
    }

    /// <summary>True when every byte is zero (alignment padding).</summary>
    private static bool _IsAllZeros(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Processes a raw (non-container) frame-producing object from the file.
    /// </summary>
    private void _ProcessRawObject(ReadOnlySpan<byte> objectData, long fileOffset)
    {
        if (!BlfObjectHeaderParser.TryParse(objectData, fileOffset,
                out BlfObjectInfo objInfo, out int skipDistance))
        {
            return;
        }

        if (!BlfConstants.IsFrameProducingType(objInfo.ObjectType)
            || !BlfFrameDispatcher.TryGetChannel(objInfo.ObjectType, objInfo.Payload, out ushort channel))
        {
            return;
        }

        BlfFrameEntry entry = new()
        {
            ContainerOffset = fileOffset, // For raw objects, container offset = file offset
            ObjectOffset = -1, // Sentinel: raw object (not inside a decompressed container)
            ObjectLength = skipDistance,
            ObjectType = objInfo.ObjectType,
            Channel = channel,
            HeaderSize = 0,
            TimestampNanos = _FileInfo.StartOffsetNanos + objInfo.TimestampNanos,
        };

        _Index.Push(in entry);
    }

    /// <summary>
    /// Processes an AppText object for channel name extraction.
    /// </summary>
    private void _ProcessAppText(ReadOnlySpan<byte> objectData)
    {
        if (!BlfObjectHeaderParser.TryParse(objectData, _FileOffset,
                out BlfObjectInfo objInfo, out _))
        {
            return;
        }

        _ProcessAppTextPayload(objInfo.Payload);
    }

    /// <summary>
    /// Extracts channel name from AppText payload and stores it.
    /// </summary>
    private void _ProcessAppTextPayload(ReadOnlySpan<byte> payload)
    {
        if (Format.Objects.AppTextParser.TryParseChannelName(
                payload, out byte channelNumber, out byte busType, out string? name))
        {
            _ChannelNames[(busType, channelNumber)] = name!;
        }
    }

    /// <summary>
    /// Scans forward from the current file offset for the "LOBJ" magic.
    /// Used for corruption recovery.
    /// Searches in chunks of <see cref="_ScanForMagicChunkSize"/> bytes with a
    /// 3-byte overlap between chunks so that a magic sequence split across a
    /// chunk boundary is not missed.
    /// </summary>
    /// <returns>True if magic was found and <c>_FileOffset</c> was updated.</returns>
    private bool _ScanForMagic()
    {
        // Search starting from the byte after the current position
        long searchFrom = _FileOffset + 1;
        long fileSize = _Backend.FileSize;

        while (searchFrom < fileSize - 3)
        {
            // Fetch a chunk for magic scanning. The overlap of 3 bytes ensures
            // that a 4-byte magic sequence that spans a chunk boundary is found
            // in the next iteration.
            int chunkSize = (int)Math.Min(_ScanForMagicChunkSize, fileSize - searchFrom);
            ReadOnlySpan<byte> chunk = _Backend.GetSpan(searchFrom, chunkSize);

            int found = _FindLobjMagic(chunk);
            if (found >= 0)
            {
                _FileOffset = searchFrom + found;
                return true;
            }

            // Advance by chunkSize - 3 to preserve the overlap
            searchFrom += Math.Max(1, chunkSize - 3);
        }

        return false;
    }

    /// <summary>
    /// Chunk size used by <see cref="_ScanForMagic"/> when scanning for the LOBJ magic.
    /// 4 MiB with a 3-byte overlap is enough to find a magic that spans a chunk edge
    /// without pulling a 64 MiB window on every miss.
    /// </summary>
    private const int _ScanForMagicChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Searches for the "LOBJ" byte sequence in data.
    /// Returns the byte offset, or -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _FindLobjMagic(ReadOnlySpan<byte> data) =>
        data.IndexOf(BlfConstants.ObjectMagicBytes);

    #endregion
}
