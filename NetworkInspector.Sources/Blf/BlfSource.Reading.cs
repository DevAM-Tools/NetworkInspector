// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

public sealed partial class BlfSource
{
    #region Private Helpers

    /// <summary>
    /// Performs a full scan of the entire BLF file, populating the index completely.
    /// </summary>
    private void _ScanFull()
    {
        BlfIncrementalScanner scanner = new(_Backend, _FileInfo, _Index, _Options.MaxUncompressedContainerSize);
        _Scanner = scanner;
        scanner.ScanToEnd();
        _FullyScanned = true;
    }

    /// <summary>
    /// Initializes the scanner for lazy/incremental scanning.
    /// Points _ChannelNames at the scanner's live dictionary so channel names
    /// discovered during lazy scanning are available for interface registration.
    /// </summary>
    private void _InitializeLazyScanner()
    {
        BlfIncrementalScanner scanner = new(_Backend, _FileInfo, _Index, _Options.MaxUncompressedContainerSize);
        _Scanner = scanner;
        _ChannelNames = scanner.ChannelNames;
    }

    /// <summary>
    /// Pure (read-only) variant of <see cref="_BuildFrame"/> for random-access callers.
    /// Returns <c>null</c> on any failure without mutating statistics, raising the
    /// <see cref="FrameSkipped"/> event, or setting the abort flag. This guarantees
    /// that <see cref="FrameById"/> never poisons sequential consumption.
    /// </summary>
    /// <remarks>
    /// <para><b>Snapshot invariant:</b> Any field that may be mutated concurrently
    /// (e.g. by <see cref="Dispose"/>) must be read exactly once via
    /// <see cref="System.Threading.Volatile"/> and stored in a local
    /// variable. That local is then passed to all helper methods so that no helper
    /// re-reads the field and observes a different (e.g. nulled-out) value.
    /// Specifically, <c>_Registry</c> is snapshotted once and passed to
    /// <see cref="_GetOrRegisterInterface"/>; callers must never re-read <c>_Registry</c>
    /// inside the same logical operation.</para>
    /// </remarks>
    private Frame? _TryBuildFrame(int frameIndex, CancellationToken cancellationToken = default)
    {
        BlfFrameEntry entry = _Index.GetEntry(frameIndex);

        ReadOnlyMemory<byte>? frameData = _TryExtractFrameData(in entry, frameIndex, cancellationToken);
        if (frameData is null)
        {
            return null;
        }

        // Snapshot _Registry once (see snapshot invariant in XML remarks above).
        // Dispose() can null _Registry between the disposed check in FrameById() and here.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return null;
        }

        LinkType linkType = _GetLinkTypeForObjectType(entry.ObjectType);
        FrameInterfaceId interfaceId = _GetOrRegisterInterface(entry.ObjectType, entry.Channel, registry);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(entry.TimestampNanos),
            frameData.Value,
            linkType,
            interfaceId,
            registry);

        if (!result.IsSuccess)
        {
            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Pure (read-only) variant of <see cref="_ExtractFrameData"/> for random-access callers.
    /// Returns <c>null</c> on any failure without invoking <see cref="_HandleSkip"/>.
    /// Container bytes are read through mmap slots hashed by <paramref name="frameIndex"/>.
    /// </summary>
    private ReadOnlyMemory<byte>? _TryExtractFrameData(
        in BlfFrameEntry entry, int frameIndex, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> objectMemory;
        if (entry.ObjectOffset >= 0)
        {
            byte[] containerData = _TryGetContainerData(entry.ContainerOffset, frameIndex, cancellationToken);
            if (containerData.Length == 0)
            {
                return null;
            }

            if ((long)entry.ObjectOffset + entry.ObjectLength > containerData.Length)
            {
                byte[]? stitched = _TryMaterializeSpanningObject(
                    entry.ContainerOffset, entry.ObjectOffset, entry.ObjectLength,
                    frameIndex, useRandomAccessSlots: true, reportErrors: false, cancellationToken);
                if (stitched is null)
                {
                    return null;
                }

                objectMemory = stitched;
            }
            else
            {
                objectMemory = containerData.AsMemory(entry.ObjectOffset, entry.ObjectLength);
            }
        }
        else
        {
            if (entry.ContainerOffset + entry.ObjectLength > _Backend.FileSize)
            {
                return null;
            }

            objectMemory = _Backend.ReadRegion(frameIndex, entry.ContainerOffset, entry.ObjectLength);
            if (objectMemory.Length < entry.ObjectLength)
            {
                return null;
            }
        }

        if (!BlfObjectHeaderParser.TryParse(
            objectMemory.Span, entry.ContainerOffset, objectMemory, out BlfObjectInfo objInfo, out _))
        {
            return null;
        }

        if (!BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult result))
        {
            return null;
        }

        return result.FrameData;
    }

    /// <summary>
    /// Builds a <see cref="Frame"/> from the index entry at the given position.
    /// Re-parses the object from the file or cached container data.
    /// Reports errors via <see cref="_HandleSkip"/> when frame construction fails.
    /// </summary>
    private Frame? _BuildFrame(int frameIndex, CancellationToken cancellationToken = default)
    {
        BlfFrameEntry entry = _Index.GetEntry(frameIndex);

        // Get or decompress the container data
        ReadOnlyMemory<byte>? frameData = _ExtractFrameData(in entry, frameIndex, cancellationToken);
        if (frameData is null)
        {
            // Error already reported in _ExtractFrameData
            return null;
        }

        // Snapshot _Registry; Dispose() can null it between the disposed check in
        // NextFrame() and this Frame.Create call.
        FrameInterfaceRegistry? registry = _Registry;
        if (registry is null)
        {
            return null;
        }

        // Determine link type from object type
        LinkType linkType = _GetLinkTypeForObjectType(entry.ObjectType);

        // Get or register the interface, passing the snapshotted registry so we never
        // re-read _Registry inside the lock (TOCTOU race with Dispose() nulling it).
        FrameInterfaceId interfaceId = _GetOrRegisterInterface(entry.ObjectType, entry.Channel, registry);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(entry.TimestampNanos),
            frameData.Value,
            linkType,
            interfaceId,
            registry);

        if (result.IsSuccess)
        {
            return result.Value;
        }

        _HandleSkip(new FrameReadErrorEventArgs
        {
            FrameIndex = frameIndex,
            FileOffset = entry.ContainerOffset,
            Kind = FrameReadErrorKind.Other,
            Message = $"Frame creation failed for object type 0x{entry.ObjectType:X} at offset {entry.ContainerOffset}."
        });

        return null;
    }

    /// <summary>
    /// Extracts the frame data for a given index entry.
    /// For container objects: retrieves from cache or decompresses the container,
    /// then re-parses the object at the stored offset.
    /// For raw objects: reads directly from the primary backend span (sequential path).
    /// Reports errors via <see cref="_HandleSkip"/> on failure.
    /// </summary>
    private ReadOnlyMemory<byte>? _ExtractFrameData(in BlfFrameEntry entry, int frameIndex, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> objectMemory = default;
        ReadOnlySpan<byte> objectSpan;
        bool hasObjectMemory;

        if (entry.ObjectOffset >= 0)
        {
            // Container object: get decompressed container data
            byte[] containerData = _GetContainerData(entry.ContainerOffset, frameIndex, cancellationToken);
            if (containerData.Length == 0)
            {
                // Error already reported in _GetContainerData
                return null;
            }

            if ((long)entry.ObjectOffset + entry.ObjectLength > containerData.Length)
            {
                byte[]? stitched = _TryMaterializeSpanningObject(
                    entry.ContainerOffset, entry.ObjectOffset, entry.ObjectLength,
                    frameIndex, useRandomAccessSlots: false, reportErrors: true, cancellationToken);
                if (stitched is null)
                {
                    return null;
                }

                objectMemory = stitched;
            }
            else
            {
                objectMemory = containerData.AsMemory(entry.ObjectOffset, entry.ObjectLength);
            }

            objectSpan = objectMemory.Span;
            hasObjectMemory = true;
        }
        else
        {
            // Raw file object — sequential path uses the primary mmap/in-memory span.
            if (entry.ContainerOffset + entry.ObjectLength > _Backend.FileSize)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = entry.ContainerOffset,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Raw object at offset {entry.ContainerOffset} with length {entry.ObjectLength} exceeds file size {_Backend.FileSize}."
                });
                return null;
            }

            objectSpan = _Backend.GetSpan(entry.ContainerOffset, entry.ObjectLength);
            hasObjectMemory = false;
        }

        bool parsed = hasObjectMemory
            ? BlfObjectHeaderParser.TryParse(objectSpan, entry.ContainerOffset, objectMemory, out BlfObjectInfo objInfo, out _)
            : BlfObjectHeaderParser.TryParse(objectSpan, entry.ContainerOffset, out objInfo, out _);
        if (!parsed)
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameIndex,
                FileOffset = entry.ContainerOffset,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = $"Failed to parse object header at offset {entry.ContainerOffset}."
            });
            return null;
        }

        if (!BlfFrameDispatcher.TryDispatch(in objInfo, out BlfFrameResult result))
        {
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameIndex,
                FileOffset = entry.ContainerOffset,
                Kind = FrameReadErrorKind.MalformedHeader,
                Message = $"Failed to dispatch object type 0x{objInfo.ObjectType:X} at offset {entry.ContainerOffset}."
            });
            return null;
        }

        return result.FrameData;
    }

    /// <summary>
    /// Pure (read-only) variant of <see cref="_GetContainerData"/> for random-access callers.
    /// Returns the cached decompressed container if present, otherwise decompresses it silently
    /// (no <see cref="_HandleSkip"/>, no sequential counter mutation).
    /// Returns an empty array on any failure except
    /// <see cref="Format.BlfDecompressionLimitExceededException"/>, which propagates to the
    /// caller so it can react.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thread-safety: all accesses to <see cref="_ContainerCache"/> and
    /// <see cref="_PendingDecompressions"/> are guarded by <see cref="_ContainerCacheLock"/>.
    /// Decompression and semaphore acquisition run outside the lock so that a long-running
    /// zlib/LZ4 pass on one thread never blocks parallel cache hits on other threads.
    /// </para>
    /// <para>
    /// Deduplication: when several threads request the same container offset concurrently,
    /// exactly one becomes the "winner" and performs the decompression. All others wait on
    /// <see cref="ContainerDecompressionWork.Ready"/> and share the winner's result.
    /// The per-container peak memory is therefore bounded by
    /// <see cref="BlfSourceOptions.MaxUncompressedContainerSize"/> regardless of concurrency.
    /// </para>
    /// <para>
    /// Concurrency limit: <see cref="_DecompressionSemaphore"/> ensures that at most
    /// <see cref="BlfSourceOptions.MaxDecompressionConcurrency"/> decompressions run at the same
    /// time across all container offsets. Waiting threads do not hold a semaphore slot.
    /// </para>
    /// </remarks>
    private byte[] _TryGetContainerData(long containerFileOffset, int frameId, CancellationToken cancellationToken = default)
    {
        ContainerDecompressionWork work;
        bool isWinner;

        lock (_ContainerCacheLock)
        {
            if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
            {
                return cached;
            }

            if (_PendingDecompressions.TryGetValue(containerFileOffset, out work!))
            {
                isWinner = false;
            }
            else
            {
                work = new();
                _PendingDecompressions[containerFileOffset] = work;
                isWinner = true;
            }
        }

        if (!isWinner)
        {
            // Another thread is already decompressing this container. Wait for it to finish.
            work.Ready.Wait(cancellationToken);

            lock (_ContainerCacheLock)
            {
                // Winner succeeded: result is in the cache.
                if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
                {
                    return cached;
                }

                if (work.Error is BlfDecompressionLimitExceededException limit)
                {
                    throw limit;
                }
            }

            // Winner failed. The failure was already counted by the winner thread.
            // Return [] without double-counting; the caller will treat a missing container
            // the same way it would treat any other decompression failure.
            return [];
        }

        // Winner path: read headers, acquire semaphore, decompress.
        byte[]? decompressed = null;
        Exception? failure = null;

        if (!_TryReadContainerPayload(containerFileOffset, frameId, useRandomAccessSlots: true,
            out ReadOnlySpan<byte> payloadData, out ReadOnlyMemory<byte> payloadOwner,
            out ushort compressionMethod, out uint uncompressedSize, out _, out _))
        {
            failure = new BlfException($"Failed to parse container headers at offset {containerFileOffset}.");
        }
        else
        {
            // CancellationToken.None is intentional: forwarding the caller token would leave the
            // work entry dangling in _PendingDecompressions if the winner is cancelled while
            // holding the semaphore, deadlocking all waiters for this container. The token is
            // already checked at the FrameById entry point before this code is reached.
            _DecompressionSemaphore.Wait(CancellationToken.None);
            try
            {
                _ = payloadOwner;
                decompressed = BlfContainer.Decompress(
                    payloadData,
                    compressionMethod,
                    uncompressedSize,
                    _Options.MaxUncompressedContainerSize);
            }
            catch (Exception ex) when (
                ex is BlfException or OutOfMemoryException or BlfDecompressionLimitExceededException)
            {
                failure = ex;
            }
            finally
            {
                _DecompressionSemaphore.Release();
            }
        }

        // Publish result under the lock: update cache / error, remove sentinel, signal waiters.
        // All three steps are atomic relative to other lock holders so no waiter can observe
        // a partially published state.
        lock (_ContainerCacheLock)
        {
            if (failure is null)
            {
                _ContainerCache.Put(containerFileOffset, decompressed!);
            }
            else
            {
                work.Error = failure;
            }

            _PendingDecompressions.Remove(containerFileOffset);
            work.Ready.Set();
        }

        // The semaphore counting the failure happens outside the lock to keep the critical
        // section as short as possible. Waiters that woke up on Ready.Set() above have not
        // yet observed _RandomAccessFailureCount; the increment is logically associated with
        // this specific decompression attempt and is safe to do outside the lock because
        // Interlocked guarantees atomic visibility.
        if (failure is not null)
        {
            Interlocked.Increment(ref _RandomAccessFailureCount);
        }

        if (failure is BlfDecompressionLimitExceededException)
        {
            throw failure;
        }

        if (decompressed is null)
        {
            return [];
        }

        return decompressed;
    }

    /// <summary>
    /// Gets decompressed container data for the sequential read path, using the 2Q cache.
    /// Reports decompression and parse failures via <see cref="_HandleSkip"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only from the single-threaded <see cref="NextFrame"/> path.
    /// The cache lock is still taken to remain consistent with parallel
    /// <see cref="_TryGetContainerData"/> calls arriving from <see cref="FrameById"/> on
    /// other threads.
    /// </para>
    /// <para>
    /// Deduplication: shares the same <see cref="_PendingDecompressions"/> dictionary as
    /// <see cref="_TryGetContainerData"/>, so a container that a random-access thread has
    /// already started decompressing will not be re-decompressed by the sequential path;
    /// this path waits and then calls <see cref="_HandleSkip"/> if the other thread failed.
    /// </para>
    /// </remarks>
    private byte[] _GetContainerData(long containerFileOffset, int frameIndex, CancellationToken cancellationToken = default)
    {
        ContainerDecompressionWork work;
        bool isWinner;

        lock (_ContainerCacheLock)
        {
            if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
            {
                return cached;
            }

            if (_PendingDecompressions.TryGetValue(containerFileOffset, out work!))
            {
                isWinner = false;
            }
            else
            {
                work = new();
                _PendingDecompressions[containerFileOffset] = work;
                isWinner = true;
            }
        }

        if (!isWinner)
        {
            // A random-access thread is already decompressing this container. Wait for it.
            work.Ready.Wait(cancellationToken);

            lock (_ContainerCacheLock)
            {
                if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
                {
                    return cached;
                }

                if (work.Error is BlfDecompressionLimitExceededException limit)
                {
                    throw limit;
                }
            }

            // Winner failed — report via _HandleSkip so the sequential path surfaces the error.
            _HandleSkip(new FrameReadErrorEventArgs
            {
                FrameIndex = frameIndex,
                FileOffset = containerFileOffset,
                Kind = FrameReadErrorKind.DecompressionFailure,
                Message = $"Container decompression failed at offset {containerFileOffset}: " +
                          $"{(work.Error is OutOfMemoryException ? "OutOfMemoryException" : work.Error?.Message ?? "unknown error")}",
            });
            return [];
        }

        // Winner path: read headers, acquire semaphore, decompress.
        byte[]? decompressed = null;
        Exception? failure = null;
        FrameReadErrorEventArgs? errorArgs = null;

        if (!_TryReadContainerPayload(containerFileOffset, frameIndex, useRandomAccessSlots: false,
            out ReadOnlySpan<byte> payloadData, out ReadOnlyMemory<byte> payloadOwner,
            out ushort compressionMethod, out uint uncompressedSize,
            out FrameReadErrorKind errorKind, out string? errorMessage))
        {
            failure = new BlfException(errorMessage ?? $"Failed to parse container headers at offset {containerFileOffset}.");
            errorArgs = new FrameReadErrorEventArgs
            {
                FrameIndex = frameIndex,
                FileOffset = containerFileOffset,
                Kind = errorKind,
                Message = failure.Message,
            };
        }
        else
        {
            // CancellationToken.None is intentional: forwarding the caller token would leave the
            // work entry dangling in _PendingDecompressions if the winner is cancelled while
            // holding the semaphore, deadlocking all waiters for this container. The token is
            // already checked at the NextFrame call chain entry point before this code is reached.
            _DecompressionSemaphore.Wait(CancellationToken.None);
            try
            {
                _ = payloadOwner;
                decompressed = BlfContainer.Decompress(
                    payloadData,
                    compressionMethod,
                    uncompressedSize,
                    _Options.MaxUncompressedContainerSize);
            }
            catch (Exception ex) when (
                ex is BlfException or OutOfMemoryException or BlfDecompressionLimitExceededException)
            {
                failure = ex;
                errorArgs = new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = containerFileOffset,
                    Kind = FrameReadErrorKind.DecompressionFailure,
                    Message = $"Container decompression failed at offset {containerFileOffset}: " +
                              $"{(ex is OutOfMemoryException ? "OutOfMemoryException" : ex.Message)}",
                };
            }
            finally
            {
                _DecompressionSemaphore.Release();
            }
        }

        // Publish result under the lock: update cache / error, remove sentinel, signal waiters.
        lock (_ContainerCacheLock)
        {
            if (failure is null)
            {
                _ContainerCache.Put(containerFileOffset, decompressed!);
            }
            else
            {
                work.Error = failure;
            }

            _PendingDecompressions.Remove(containerFileOffset);
            work.Ready.Set();
        }

        if (failure is BlfDecompressionLimitExceededException)
        {
            throw failure;
        }

        // Report errors outside the lock to keep the critical section short.
        if (errorArgs is not null)
        {
            _HandleSkip(errorArgs);
        }

        if (decompressed is null)
        {
            return [];
        }

        return decompressed;
    }

    /// <summary>
    /// Copies a container object that starts in one decompressed blob and continues in the
    /// next LOG_CONTAINER blobs. Used when <c>object_length</c> overruns the first container.
    /// </summary>
    private byte[]? _TryMaterializeSpanningObject(
        long firstContainerOffset,
        int objectOffset,
        int objectLength,
        int mmapSlotFrameId,
        bool useRandomAccessSlots,
        bool reportErrors,
        CancellationToken cancellationToken)
    {
        if (objectOffset < 0 || objectLength <= 0 || objectLength > BlfConstants.MaxBlockReadSize)
        {
            if (reportErrors)
            {
                _HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = mmapSlotFrameId,
                    FileOffset = firstContainerOffset,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Split container object at offset {firstContainerOffset} has invalid bounds.",
                });
            }

            return null;
        }

        byte[] result = new byte[objectLength];
        int filled = 0;
        long containerOffset = firstContainerOffset;
        int skipInContainer = objectOffset;

        while (filled < objectLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] decoded = useRandomAccessSlots
                ? _TryGetContainerData(containerOffset, mmapSlotFrameId, cancellationToken)
                : _GetContainerData(containerOffset, mmapSlotFrameId, cancellationToken);
            if (decoded.Length == 0)
            {
                return null;
            }

            if (skipInContainer > decoded.Length)
            {
                if (reportErrors)
                {
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = mmapSlotFrameId,
                        FileOffset = containerOffset,
                        Kind = FrameReadErrorKind.CorruptedBlock,
                        Message = $"Split object inner offset {skipInContainer} exceeds container size {decoded.Length}.",
                    });
                }

                return null;
            }

            ReadOnlySpan<byte> slice = decoded.AsSpan(skipInContainer);
            int copy = Math.Min(slice.Length, objectLength - filled);
            slice[..copy].CopyTo(result.AsSpan(filled));
            filled += copy;
            skipInContainer = 0;

            if (filled >= objectLength)
            {
                break;
            }

            if (!_TryFindNextLogContainerOffset(containerOffset, out long nextOffset))
            {
                if (reportErrors)
                {
                    _HandleSkip(new FrameReadErrorEventArgs
                    {
                        FrameIndex = mmapSlotFrameId,
                        FileOffset = containerOffset,
                        Kind = FrameReadErrorKind.TruncatedStream,
                        Message = $"Split object at offset {firstContainerOffset} has no following log container.",
                    });
                }

                return null;
            }

            containerOffset = nextOffset;
        }

        return result;
    }

    /// <summary>
    /// Finds the next file-level LOG_CONTAINER after the object at
    /// <paramref name="currentContainerOffset"/> using skip-then-1-byte <c>LOBJ</c> scan.
    /// </summary>
    private bool _TryFindNextLogContainerOffset(long currentContainerOffset, out long nextOffset)
    {
        nextOffset = 0;
        long fileSize = _Backend.FileSize;
        if (currentContainerOffset + BlfConstants.BlockHeaderSize > fileSize)
        {
            return false;
        }

        int headerFetch = (int)Math.Min(BlfConstants.BlockHeaderSize, fileSize - currentContainerOffset);
        ReadOnlySpan<byte> headerSpan = _Backend.GetSpan(currentContainerOffset, headerFetch);
        if (!BlfBlockHeader.TryParse(headerSpan, out BlfBlockHeader currentHeader, out _))
        {
            return false;
        }

        long skip = Math.Max(
            Math.Max((long)BlfConstants.BlockHeaderSize, currentHeader.ObjectLength.Value),
            currentHeader.HeaderSize.Value);
        long searchFrom = currentContainerOffset + skip;

        while (searchFrom + 4 <= fileSize)
        {
            int chunkSize = (int)Math.Min(_ScanForMagicChunkSize, fileSize - searchFrom);
            ReadOnlySpan<byte> chunk = _Backend.GetSpan(searchFrom, chunkSize);
            int found = chunk.IndexOf(BlfConstants.ObjectMagicBytes);
            if (found < 0)
            {
                searchFrom += Math.Max(1, chunkSize - 3);
                continue;
            }

            long pos = searchFrom + found;
            if (pos + BlfConstants.BlockHeaderSize > fileSize)
            {
                return false;
            }

            ReadOnlySpan<byte> nextHeader = _Backend.GetSpan(pos, BlfConstants.BlockHeaderSize);
            if (!BlfBlockHeader.TryParse(nextHeader, out BlfBlockHeader block, out _)
                || block.Signature.Value != BlfConstants.ObjectMagic)
            {
                searchFrom = pos + 1;
                continue;
            }

            if (block.ObjectType.Value == BlfConstants.ObjTypeLogContainer)
            {
                nextOffset = pos;
                return true;
            }

            long objSkip = Math.Max(
                Math.Max((long)BlfConstants.BlockHeaderSize, block.ObjectLength.Value),
                block.HeaderSize.Value);
            if (objSkip > BlfConstants.MaxBlockReadSize)
            {
                searchFrom = pos + 1;
                continue;
            }

            searchFrom = pos + objSkip;
        }

        return false;
    }

    /// <summary>4 MiB window with 3-byte overlap when locating the next file-level LOBJ.</summary>
    private const int _ScanForMagicChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the raw compressed payload from the backend for the container block at
    /// <paramref name="containerFileOffset"/> and returns the parse results needed to
    /// decompress it. All backend I/O and header parsing is done here; no decompression
    /// is performed.
    /// </summary>
    private bool _TryReadContainerPayload(
        long containerFileOffset,
        int mmapSlotFrameId,
        bool useRandomAccessSlots,
        out ReadOnlySpan<byte> payloadData,
        out ReadOnlyMemory<byte> payloadOwner,
        out ushort compressionMethod,
        out uint uncompressedSize,
        out FrameReadErrorKind errorKind,
        out string? errorMessage)
    {
        payloadData = ReadOnlySpan<byte>.Empty;
        payloadOwner = ReadOnlyMemory<byte>.Empty;
        compressionMethod = 0;
        uncompressedSize = 0;

        if (containerFileOffset >= _Backend.FileSize)
        {
            errorKind = FrameReadErrorKind.CorruptedBlock;
            errorMessage = $"Container offset {containerFileOffset} exceeds file size {_Backend.FileSize}.";
            return false;
        }

        ReadOnlySpan<byte> blockData;
        ReadOnlyMemory<byte> headerMemory = default;
        if (useRandomAccessSlots)
        {
            headerMemory = _Backend.ReadRegion(
                mmapSlotFrameId,
                containerFileOffset,
                (int)Math.Min(_Backend.FileSize - containerFileOffset, BlfConstants.BlockHeaderSize));
            blockData = headerMemory.Span;
        }
        else
        {
            int headerFetch = (int)Math.Min(
                BlfConstants.BlockHeaderSize,
                _Backend.FileSize - containerFileOffset);
            blockData = _Backend.GetSpan(containerFileOffset, headerFetch);
        }

        if (!BlfBlockHeader.TryParse(blockData, out BlfBlockHeader blockHeader, out _))
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Failed to parse container block header at offset {containerFileOffset}.";
            return false;
        }

        ushort headerSize = blockHeader.HeaderSize.Value;
        uint objectLength = blockHeader.ObjectLength.Value;
        long totalSizeLong = Math.Max(Math.Max((long)BlfConstants.BlockHeaderSize, objectLength), headerSize);
        if (totalSizeLong > int.MaxValue)
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Container at offset {containerFileOffset} claims size {totalSizeLong} which exceeds the addressable span range.";
            return false;
        }

        if (totalSizeLong > BlfConstants.MaxBlockReadSize)
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Container at offset {containerFileOffset} claims size {totalSizeLong} which exceeds the {BlfConstants.MaxBlockReadSize} byte block-read cap.";
            return false;
        }

        int totalSize = (int)totalSizeLong;
        if (containerFileOffset + totalSize > _Backend.FileSize)
        {
            errorKind = FrameReadErrorKind.CorruptedBlock;
            errorMessage = $"Container at offset {containerFileOffset} with size {totalSize} exceeds file size {_Backend.FileSize}.";
            return false;
        }

        ReadOnlySpan<byte> fullObjectData;
        if (useRandomAccessSlots)
        {
            payloadOwner = _Backend.ReadRegion(mmapSlotFrameId, containerFileOffset, totalSize);
            if (payloadOwner.Length < totalSize)
            {
                errorKind = FrameReadErrorKind.CorruptedBlock;
                errorMessage = $"Container at offset {containerFileOffset} with size {totalSize} could not be read.";
                payloadOwner = ReadOnlyMemory<byte>.Empty;
                return false;
            }

            fullObjectData = payloadOwner.Span;
        }
        else
        {
            fullObjectData = _Backend.GetSpan(containerFileOffset, totalSize);
        }

        int containerHeaderOffset = Math.Max((int)headerSize, BlfConstants.BlockHeaderSize);
        int containerPayloadOffset = containerHeaderOffset + BlfConstants.ContainerHeaderSize;
        if (containerHeaderOffset + BlfConstants.ContainerHeaderSize > fullObjectData.Length)
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Invalid container header offset {containerHeaderOffset} at file offset {containerFileOffset}.";
            return false;
        }

        if (!BlfContainerHeader.TryParse(fullObjectData[containerHeaderOffset..], out BlfContainerHeader containerHeader, out _))
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Failed to parse container header at offset {containerFileOffset}.";
            return false;
        }

        if (fullObjectData.Length <= containerPayloadOffset)
        {
            errorKind = FrameReadErrorKind.CorruptedBlock;
            errorMessage = $"Container at offset {containerFileOffset} has no payload data.";
            return false;
        }

        if (useRandomAccessSlots)
        {
            payloadOwner = payloadOwner[containerPayloadOffset..];
            payloadData = payloadOwner.Span;
        }
        else
        {
            payloadData = fullObjectData[containerPayloadOffset..];
        }

        compressionMethod = containerHeader.CompressionMethod.Value;
        uncompressedSize = containerHeader.UncompressedSize.Value;
        errorKind = FrameReadErrorKind.Other;
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Handles a skipped frame by updating statistics and raising the event.
    /// In strict mode, sets the abort flag so subsequent NextFrame calls return null.
    /// </summary>
    private void _HandleSkip(FrameReadErrorEventArgs error)
    {
        _SkippedFrameCount.Increment();
        _ErrorCount.Increment();

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // sets _Aborted so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            _Aborted = true;
        }
    }

    /// <summary>
    /// Gets or registers a frame interface for the given object type and channel.
    /// Uses discovered channel names from AppText when available.
    /// Thread-safe via locking.
    /// </summary>
    /// <param name="objectType">BLF object type.</param>
    /// <param name="channel">BLF channel index.</param>
    /// <param name="registry">
    /// Caller-supplied registry snapshot. Must be the value obtained via
    /// <c>Volatile.Read</c> immediately before the call so we
    /// never re-read <c>_Registry</c> inside the lock (TOCTOU race with
    /// <see cref="Dispose"/> nulling <c>_Registry</c>).
    /// </param>
    private FrameInterfaceId _GetOrRegisterInterface(uint objectType, ushort channel, FrameInterfaceRegistry registry)
    {
        (uint ObjectType, ushort Channel) key = (objectType, channel);

        lock (_InterfaceLock)
        {
            if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existingId))
            {
                return existingId;
            }

            string busName = _GetBusName(objectType);
            string interfaceName = _TryGetChannelName(objectType, channel)
                ?? $"{busName} {channel}";
            LinkType linkType = _GetLinkTypeForObjectType(objectType);

            FrameInterfaceId id = registry.Register(
                _SourceId, interfaceName, null, linkType,
                new Dictionary<string, object>
                {
                    [FrameInterfacePropertyKeys.BlfChannel] = (long)channel,
                    [FrameInterfacePropertyKeys.BlfObjectType] = objectType,
                    [FrameInterfacePropertyKeys.BlfBusType] = _GetBusTypeForObjectType(objectType),
                });
            _InterfaceMap[key] = id;
            return id;
        }
    }

    /// <summary>
    /// Tries to find a channel name from AppText channel name discovery.
    /// Maps the BLF object type to the AppText bus type for lookup.
    /// </summary>
    private string? _TryGetChannelName(uint objectType, ushort channel)
    {
        if (_ChannelNames is null || _ChannelNames.Count == 0)
        {
            return null;
        }

        byte busType = _GetBusTypeForObjectType(objectType);
        if (busType == 0)
        {
            return null;
        }

        // AppText channel numbers are 0-based
        if (!_ChannelNames.TryGetValue((busType, (byte)channel), out string? name))
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// Returns the link type for a given BLF object type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LinkType _GetLinkTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => LinkType.Ethernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => LinkType.CanSocketcan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2
            => LinkType.Lin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => LinkType.Flexray,

        _ => LinkType.Null,
    };

    /// <summary>
    /// Returns a bus name string for interface naming.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string _GetBusName(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => "Ethernet",

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 => "CAN",

        BlfConstants.ObjTypeCanFdMessage or BlfConstants.ObjTypeCanFdMessage64
            or BlfConstants.ObjTypeCanFdError64 => "CAN FD",

        BlfConstants.ObjTypeCanXlChannelFrame => "CAN XL",

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => "LIN",

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => "FlexRay",

        _ => "Unknown",
    };

    /// <summary>
    /// Maps a BLF object type to the AppText bus type constant for channel name lookup.
    /// Returns 0 if the object type has no corresponding bus type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte _GetBusTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => BlfConstants.BusTypeEthernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => BlfConstants.BusTypeCan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => BlfConstants.BusTypeLin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => BlfConstants.BusTypeFlexRay,

        _ => 0,
    };

    #endregion
}
