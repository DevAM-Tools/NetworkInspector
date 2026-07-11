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
/// <para><b>Cross-Container Frame Handling:</b></para>
/// <para>
/// The BLF specification and all known correct BLF writers (including our own
/// <c>BlfWriter</c>) guarantee that a single LOBJ object is always fully contained
/// within one container — objects are never split across container boundaries.
/// Our writer enforces this by flushing the current container before adding an
/// object that would exceed <c>MaxContainerBufferSize</c>.
/// </para>
/// <para>
/// However, defective or third-party BLF writers may produce containers where an
/// LOBJ object is truncated at the container end, with the remainder at the start
/// of the next container. The current implementation handles this as follows:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       When <see cref="_DrainPendingContainer"/> encounters bytes at the end of a
///       container that cannot be parsed as a valid LOBJ header (too few bytes, or
///       <see cref="BlfObjectHeaderParser.TryParse"/> fails), it searches for the
///       next LOBJ magic within the remaining data.
///     </description>
///   </item>
///   <item>
///     <description>
///       If no LOBJ magic is found, the remaining bytes are discarded and the
///       container is marked as fully consumed. Any truncated object is lost.
///     </description>
///   </item>
///   <item>
///     <description>
///       When error tolerance is enabled (see <see cref="IErrorTolerantFrameSource"/>),
///       a <see cref="FrameReadErrorEventArgs"/> is raised for the lost frame.
///     </description>
///   </item>
/// </list>
/// <para>
/// A carry-over buffer (stitching truncated bytes from one container to the next)
/// is intentionally not implemented because:
///   (1) correct BLF files never produce this scenario,
///   (2) the complexity of cross-container stitching is high (requires buffering
///       partial objects and correlating with the next container's decompressed data),
///   (3) the error tolerance mechanism provides visibility into lost frames.
/// If a future need arises for carry-over support (e.g., recovery of BLF files from
/// a specific defective writer), the implementation point is at the end of
/// <see cref="_DrainPendingContainer"/> where remaining bytes could be saved to a
/// <c>_CarryOverBuffer</c> and prepended to the next container's decompressed data.
/// </para>
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

    // Inner loop state — pending decompressed container
    private byte[]? _PendingContainer;
    private int _ContainerOffset;
    private long _ContainerFileOffset;

    /// <summary>
    /// Number of containers where the container header offset fell outside the object body.
    /// Incremented inside <see cref="_ProcessContainer"/> on bounds violations.
    /// BlfSource polls this and forwards each new failure through the error-tolerance pipeline.
    /// </summary>
    private long _CorruptedContainerCount;

    /// <summary>
    /// Number of containers that had trailing bytes insufficient for a valid LOBJ header.
    /// Incremented inside <see cref="_DrainPendingContainer"/> when the container tail is
    /// too short to hold a complete object header. BlfSource polls this and forwards each
    /// new truncation through the error-tolerance pipeline.
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

    /// <summary>Whether the scanner has processed all data or the index is full.</summary>
    internal bool IsExhausted => _Exhausted || _Index.IsFull;

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
    /// Number of containers whose trailing bytes were too few for a valid LOBJ header and were
    /// therefore silently discarded. Callers can poll this after <see cref="ScanNext"/> to
    /// report truncation diagnostics through the error tolerance mechanism.
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
            //   - objectLength > int.MaxValue: cannot be addressed in a single Span<T>;
            //     indicates adversarial or wildly corrupt data.
            // On any violation, attempt corruption-recovery by scanning for the next LOBJ magic.
            if (headerSz < BlfConstants.BlockHeaderSize)
            {
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            if (objectLength == 0 || objectLength > int.MaxValue)
            {
                if (!_ScanForMagic())
                {
                    _Exhausted = true;
                }
                continue;
            }

            // Both headerSz and objectLength are now in [BlockHeaderSize, int.MaxValue].
            // Math.Max is safe; no overflow is possible.
            int skipDistance = Math.Max(Math.Max((int)headerSz, BlfConstants.BlockHeaderSize), (int)objectLength);

            // Validate we have enough data for the full object
            if (_FileOffset + skipDistance > _Backend.FileSize)
            {
                _Exhausted = true;
                return false;
            }

            // Fetch the complete object as a windowed span
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
            _CorruptedContainerCount++;
            return;
        }

        ReadOnlySpan<byte> containerHeaderData = objectData[containerHeaderOffset..];
        if (!BlfContainerHeader.TryParse(containerHeaderData, out BlfContainerHeader containerHeader, out _))
        {
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
            return;
        }

        try
        {
            _PendingContainer = BlfContainer.Decompress(payloadData, compressionMethod, uncompressedSize,
                _MaxUncompressedContainerSize);
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
            _PendingContainer = null;
            _DecompressionFailures++;
        }
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
                    // No more objects in this container
                    _PendingContainer = null;
                    _ContainerOffset = 0;
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

            // Try to convert to a frame
            if (BlfConstants.IsFrameProducingType(objInfo.ObjectType)
                && BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult frameResult))
            {
                BlfFrameEntry entry = new()
                {
                    ContainerOffset = _ContainerFileOffset,
                    ObjectOffset = objectStart,
                    ObjectLength = skipDistance,
                    ObjectType = frameResult.ObjectType,
                    Channel = frameResult.Channel,
                    HeaderSize = 0, // Not needed for container objects
                    TimestampNanos = _FileInfo.StartOffsetNanos + objInfo.TimestampNanos,
                };
                _Index.Push(in entry);
                foundFrame = true;

                // When the index is full, mark scanner exhausted to prevent
                // re-entering the drain loop on subsequent ScanNext() calls (which would
                // produce a CPU-spin / UI-hang until the caller observes IsExhausted).
                if (_Index.IsFull)
                {
                    _Exhausted = true;
                    break;
                }
            }
        }

        // Container fully consumed.
        // Detect trailing bytes too short for a valid LOBJ header: these are silently
        // discarded because a partial object cannot be reconstructed. In a correctly
        // written BLF file this never happens; in a defective file the truncated object
        // is lost. Increment _TruncatedObjectCount so BlfSource can surface the diagnostic.
        // See class remarks for carry-over buffer rationale.
        if (_ContainerOffset < containerSpan.Length)
        {
            _TruncatedObjectCount++;
        }

        _PendingContainer = null;
        _ContainerOffset = 0;
        return foundFrame;
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

        if (!BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult result))
        {
            return;
        }

        BlfFrameEntry entry = new()
        {
            ContainerOffset = fileOffset, // For raw objects, container offset = file offset
            ObjectOffset = -1, // Sentinel: raw object (not inside a decompressed container)
            ObjectLength = skipDistance,
            ObjectType = result.ObjectType,
            Channel = result.Channel,
            HeaderSize = 0,
            TimestampNanos = _FileInfo.StartOffsetNanos + objInfo.TimestampNanos,
        };

        // M4: honour the return value — when the index is full, stop scanning
        // to avoid wasting CPU on objects that can never be indexed.
        if (!_Index.Push(in entry))
        {
            _Exhausted = true;
        }
    }

    /// <summary>
    /// Whether the index reached its maximum capacity of <see cref="int.MaxValue"/> entries.
    /// When <c>true</c>, the file contains more frames than can be indexed.
    /// </summary>
    internal bool IsIndexFull => _Index.IsFull;

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
    /// 64 MiB provides a good balance between I/O granularity and memory pressure for
    /// the corruption-recovery scan. Each chunk is requested from the backend as a
    /// windowed span (zero-copy for in-memory backends, mapped-view for mmap backends).
    /// Decreasing this value reduces per-scan peak memory; increasing it reduces
    /// the number of chunk fetches in files with long corruption gaps.
    /// </summary>
    private const int _ScanForMagicChunkSize = 64 * 1024 * 1024; // 64 MiB

    /// <summary>
    /// Searches for the "LOBJ" byte sequence in data.
    /// Returns the byte offset, or -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _FindLobjMagic(ReadOnlySpan<byte> data) =>
        data.IndexOf(BlfConstants.ObjectMagicBytes);

    #endregion
}
