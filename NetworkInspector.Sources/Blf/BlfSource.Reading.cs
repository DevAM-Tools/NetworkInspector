// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

public sealed partial class BlfSource
{
    #region Private Helpers

    /// <summary>
    /// Performs a full scan of the entire BLF file, populating the index completely.
    /// </summary>
    private void ScanFull()
    {
        _Scanner = new BlfIncrementalScanner(_Backend, _FileInfo, _Index, _Options.MaxUncompressedContainerSize);
        _Scanner.ScanToEnd();
        _Index.ShrinkToFit();
        Volatile.Write(ref _FullyScanned, true);
    }

    /// <summary>
    /// Initializes the scanner for lazy/incremental scanning.
    /// Points _ChannelNames at the scanner's live dictionary so channel names
    /// discovered during lazy scanning are available for interface registration.
    /// </summary>
    private void InitializeLazyScanner()
    {
        _Scanner = new BlfIncrementalScanner(_Backend, _FileInfo, _Index, _Options.MaxUncompressedContainerSize);
        _ChannelNames = _Scanner.ChannelNames;
    }

    /// <summary>
    /// Pure (read-only) variant of <see cref="BuildFrame"/> for random-access callers.
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
    /// <see cref="GetOrRegisterInterface"/>; callers must never re-read <c>_Registry</c>
    /// inside the same logical operation.</para>
    /// </remarks>
    private Frame? TryBuildFrame(int frameIndex)
    {
        ref readonly BlfFrameEntry entry = ref _Index.GetEntry(frameIndex);

        byte[]? frameData = TryExtractFrameData(in entry);
        if (frameData is null)
        {
            return null;
        }

        // Snapshot _Registry once (see snapshot invariant in XML remarks above).
        // Dispose() can null _Registry between the disposed check in FrameById() and here.
        FrameInterfaceRegistry? registry = Volatile.Read(ref _Registry);
        if (registry is null)
        {
            return null;
        }

        LinkType linkType = GetLinkTypeForObjectType(entry.ObjectType);
        FrameInterfaceId interfaceId = GetOrRegisterInterface(entry.ObjectType, entry.Channel, registry);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(entry.TimestampNanos),
            frameData,
            linkType,
            interfaceId,
            registry);

        return result.IsSuccess ? result.Value : null;
    }

    /// <summary>
    /// Pure (read-only) variant of <see cref="ExtractFrameData"/> for random-access callers.
    /// Returns <c>null</c> on any failure without invoking <see cref="HandleSkip"/>.
    /// </summary>
    private byte[]? TryExtractFrameData(in BlfFrameEntry entry)
    {
        ReadOnlySpan<byte> objectData;

        if (entry.ObjectOffset >= 0)
        {
            byte[] containerData = TryGetContainerData(entry.ContainerOffset);
            if (containerData.Length == 0)
            {
                return null;
            }

            if (entry.ObjectOffset + entry.ObjectLength > containerData.Length)
            {
                return null;
            }

            objectData = containerData.AsSpan(entry.ObjectOffset, entry.ObjectLength);
        }
        else
        {
            if (entry.ContainerOffset + entry.ObjectLength > _Backend.FileSize)
            {
                return null;
            }

            objectData = _Backend.GetSpan(entry.ContainerOffset, entry.ObjectLength);
        }

        if (!BlfObjectHeaderParser.TryParse(objectData, entry.ContainerOffset, out BlfObjectInfo objInfo, out _))
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
    /// Reports errors via <see cref="HandleSkip"/> when frame construction fails.
    /// </summary>
    private Frame? BuildFrame(int frameIndex)
    {
        ref readonly BlfFrameEntry entry = ref _Index.GetEntry(frameIndex);

        // Get or decompress the container data
        byte[]? frameData = ExtractFrameData(in entry, frameIndex);
        if (frameData is null)
        {
            // Error already reported in ExtractFrameData
            return null;
        }

        // Snapshot _Registry; Dispose() can null it between the disposed check in
        // NextFrame() and this Frame.Create call.
        FrameInterfaceRegistry? registry = Volatile.Read(ref _Registry);
        if (registry is null)
        {
            return null;
        }

        // Determine link type from object type
        LinkType linkType = GetLinkTypeForObjectType(entry.ObjectType);

        // Get or register the interface, passing the snapshotted registry so we never
        // re-read _Registry inside the lock (TOCTOU race with Dispose() nulling it).
        FrameInterfaceId interfaceId = GetOrRegisterInterface(entry.ObjectType, entry.Channel, registry);

        ParseResult<Frame> result = Frame.Create(
            new FrameId(frameIndex),
            new Timestamp(entry.TimestampNanos),
            frameData,
            linkType,
            interfaceId,
            registry);

        if (result.IsSuccess)
        {
            return result.Value;
        }

        HandleSkip(new FrameReadErrorEventArgs
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
    /// For raw objects: reads directly from file data.
    /// Reports errors via <see cref="HandleSkip"/> on failure.
    /// </summary>
    private byte[]? ExtractFrameData(in BlfFrameEntry entry, int frameIndex)
    {
        ReadOnlySpan<byte> objectData;

        if (entry.ObjectOffset >= 0)
        {
            // Container object: get decompressed container data
            byte[] containerData = GetContainerData(entry.ContainerOffset, frameIndex);
            if (containerData.Length == 0)
            {
                // Error already reported in GetContainerData
                return null;
            }

            if (entry.ObjectOffset + entry.ObjectLength > containerData.Length)
            {
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = entry.ContainerOffset,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Object at offset {entry.ObjectOffset} with length {entry.ObjectLength} exceeds container size {containerData.Length}."
                });
                return null;
            }

            objectData = containerData.AsSpan(entry.ObjectOffset, entry.ObjectLength);
        }
        else
        {
            // Raw file object
            if (entry.ContainerOffset + entry.ObjectLength > _Backend.FileSize)
            {
                HandleSkip(new FrameReadErrorEventArgs
                {
                    FrameIndex = frameIndex,
                    FileOffset = entry.ContainerOffset,
                    Kind = FrameReadErrorKind.CorruptedBlock,
                    Message = $"Raw object at offset {entry.ContainerOffset} with length {entry.ObjectLength} exceeds file size {_Backend.FileSize}."
                });
                return null;
            }

            objectData = _Backend.GetSpan(entry.ContainerOffset, entry.ObjectLength);
        }

        // Re-parse the object to extract the frame
        if (!BlfObjectHeaderParser.TryParse(objectData, entry.ContainerOffset, out BlfObjectInfo objInfo, out _))
        {
            HandleSkip(new FrameReadErrorEventArgs
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
            HandleSkip(new FrameReadErrorEventArgs
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
    /// Pure (read-only) variant of <see cref="GetContainerData"/> for random-access callers.
    /// Returns the cached decompressed container if present, otherwise decompresses it silently
    /// (no <see cref="HandleSkip"/>, no sequential counter mutation).
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
    private byte[] TryGetContainerData(long containerFileOffset)
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
                work = new ContainerDecompressionWork();
                _PendingDecompressions[containerFileOffset] = work;
                isWinner = true;
            }
        }

        if (!isWinner)
        {
            // Another thread is already decompressing this container. Wait for it to finish.
            work.Ready.Task.Wait();

            lock (_ContainerCacheLock)
            {
                // Winner succeeded: result is in the cache.
                if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
                {
                    return cached;
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

        if (!TryReadContainerPayload(containerFileOffset, out ReadOnlySpan<byte> payloadData,
            out ushort compressionMethod, out uint uncompressedSize, out _, out _))
        {
            failure = new BlfException($"Failed to parse container headers at offset {containerFileOffset}.");
        }
        else
        {
            _DecompressionSemaphore.Wait();
            try
            {
                // BlfDecompressionLimitExceededException is intentionally not caught here
                // — it propagates to FrameById so the caller can react.
                decompressed = BlfContainer.Decompress(
                    payloadData,
                    compressionMethod,
                    uncompressedSize,
                    _Options.MaxUncompressedContainerSize);
            }
            catch (Exception ex) when (ex is BlfException or OutOfMemoryException)
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
            work.Ready.SetResult(true);
        }

        // The semaphore counting the failure happens outside the lock to keep the critical
        // section as short as possible. Waiters that woke up on Ready.SetResult() above have not
        // yet observed _RandomAccessFailureCount; the increment is logically associated with
        // this specific decompression attempt and is safe to do outside the lock because
        // Interlocked guarantees atomic visibility.
        if (failure is not null)
        {
            Interlocked.Increment(ref _RandomAccessFailureCount);
        }

        return decompressed ?? [];
    }

    /// <summary>
    /// Gets decompressed container data for the sequential read path, using the 2Q cache.
    /// Reports decompression and parse failures via <see cref="HandleSkip"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only from the single-threaded <see cref="NextFrame"/> path.
    /// The cache lock is still taken to remain consistent with parallel
    /// <see cref="TryGetContainerData"/> calls arriving from <see cref="FrameById"/> on
    /// other threads.
    /// </para>
    /// <para>
    /// Deduplication: shares the same <see cref="_PendingDecompressions"/> dictionary as
    /// <see cref="TryGetContainerData"/>, so a container that a random-access thread has
    /// already started decompressing will not be re-decompressed by the sequential path;
    /// this path waits and then calls <see cref="HandleSkip"/> if the other thread failed.
    /// </para>
    /// </remarks>
    private byte[] GetContainerData(long containerFileOffset, int frameIndex)
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
                work = new ContainerDecompressionWork();
                _PendingDecompressions[containerFileOffset] = work;
                isWinner = true;
            }
        }

        if (!isWinner)
        {
            // A random-access thread is already decompressing this container. Wait for it.
            work.Ready.Task.Wait();

            lock (_ContainerCacheLock)
            {
                if (_ContainerCache.TryGet(containerFileOffset, out byte[] cached))
                {
                    return cached;
                }
            }

            // Winner failed — report via HandleSkip so the sequential path surfaces the error.
            HandleSkip(new FrameReadErrorEventArgs
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

        if (!TryReadContainerPayload(containerFileOffset, out ReadOnlySpan<byte> payloadData,
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
            _DecompressionSemaphore.Wait();
            try
            {
                // BlfDecompressionLimitExceededException is intentionally not caught here
                // — it propagates to NextFrame so the caller can react.
                decompressed = BlfContainer.Decompress(
                    payloadData,
                    compressionMethod,
                    uncompressedSize,
                    _Options.MaxUncompressedContainerSize);
            }
            catch (Exception ex) when (ex is BlfException or OutOfMemoryException)
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
            work.Ready.SetResult(true);
        }

        // Report errors outside the lock to keep the critical section short.
        if (errorArgs is not null)
        {
            HandleSkip(errorArgs);
        }

        return decompressed ?? [];
    }

    /// <summary>
    /// Reads the raw compressed payload from the backend for the container block at
    /// <paramref name="containerFileOffset"/> and returns the parse results needed to
    /// decompress it. All backend I/O and header parsing is done here; no decompression
    /// is performed.
    /// </summary>
    /// <param name="containerFileOffset">Absolute file offset of the container LOBJ block.</param>
    /// <param name="payloadData">
    /// On success, a span over the compressed payload bytes inside the backend.
    /// The span is valid for the lifetime of the current call stack (the caller holds
    /// either the <see cref="_LifetimeLock"/> read lock for random-access paths, or the
    /// single-threaded sequential contract for <see cref="NextFrame"/>).
    /// On failure, <see cref="ReadOnlySpan{T}.Empty"/>.
    /// </param>
    /// <param name="compressionMethod">BLF compression method code (0 = none, 1 = LZ4, 2 = zlib).</param>
    /// <param name="uncompressedSize">Expected size after decompression as declared in the container header.</param>
    /// <param name="errorKind">Populated when the method returns <c>false</c>.</param>
    /// <param name="errorMessage">Human-readable diagnostic populated when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> on success; <c>false</c> if any header is malformed or out of bounds.</returns>
    private bool TryReadContainerPayload(
        long containerFileOffset,
        out ReadOnlySpan<byte> payloadData,
        out ushort compressionMethod,
        out uint uncompressedSize,
        out FrameReadErrorKind errorKind,
        out string? errorMessage)
    {
        payloadData = ReadOnlySpan<byte>.Empty;
        compressionMethod = 0;
        uncompressedSize = 0;

        if (containerFileOffset >= _Backend.FileSize)
        {
            errorKind = FrameReadErrorKind.CorruptedBlock;
            errorMessage = $"Container offset {containerFileOffset} exceeds file size {_Backend.FileSize}.";
            return false;
        }

        ReadOnlySpan<byte> blockData = _Backend.GetSpan(containerFileOffset,
            (int)Math.Min(_Backend.FileSize - containerFileOffset, int.MaxValue));

        if (!BlfBlockHeader.TryParse(blockData, out BlfBlockHeader blockHeader, out _))
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Failed to parse container block header at offset {containerFileOffset}.";
            return false;
        }

        ushort headerSize = blockHeader.HeaderSize.Value;
        uint objectLength = blockHeader.ObjectLength.Value;
        // Use long arithmetic to avoid uint overflow: objectLength comes from untrusted
        // file data and can exceed int.MaxValue, wrapping to a negative int.
        long totalSizeLong = Math.Max(Math.Max((long)BlfConstants.BlockHeaderSize, objectLength), headerSize);
        if (totalSizeLong > int.MaxValue)
        {
            errorKind = FrameReadErrorKind.MalformedHeader;
            errorMessage = $"Container at offset {containerFileOffset} claims size {totalSizeLong} which exceeds the addressable span range.";
            return false;
        }

        int totalSize = (int)totalSizeLong;
        if (containerFileOffset + totalSize > _Backend.FileSize)
        {
            errorKind = FrameReadErrorKind.CorruptedBlock;
            errorMessage = $"Container at offset {containerFileOffset} with size {totalSize} exceeds file size {_Backend.FileSize}.";
            return false;
        }

        ReadOnlySpan<byte> fullObjectData = _Backend.GetSpan(containerFileOffset, totalSize);

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

        payloadData = fullObjectData[containerPayloadOffset..];
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
    private void HandleSkip(FrameReadErrorEventArgs error)
    {
        Interlocked.Increment(ref _SkippedFrameCount);
        Interlocked.Increment(ref _ErrorCount);

        // Always signal the error so subscribers can log the first offending block
        // regardless of the tolerance mode. In strict mode the source additionally
        // sets _Aborted so the next NextFrame() call returns null.
        FrameSkipped?.Invoke(this, error);

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Aborted, true);
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
    private FrameInterfaceId GetOrRegisterInterface(uint objectType, ushort channel, FrameInterfaceRegistry registry)
    {
        (uint ObjectType, ushort Channel) key = (objectType, channel);

        lock (_InterfaceLock)
        {
            if (_InterfaceMap.TryGetValue(key, out FrameInterfaceId existingId))
            {
                return existingId;
            }

            string busName = GetBusName(objectType);
            string interfaceName = TryGetChannelName(objectType, channel)
                ?? $"{busName} {channel}";
            LinkType linkType = GetLinkTypeForObjectType(objectType);

            FrameInterfaceId id = registry.Register(
                _SourceId, interfaceName, null, linkType,
                new Dictionary<string, object>
                {
                    [FrameInterfacePropertyKeys.BlfChannel] = (long)channel,
                    [FrameInterfacePropertyKeys.BlfObjectType] = objectType,
                    [FrameInterfacePropertyKeys.BlfBusType] = GetBusTypeForObjectType(objectType),
                });
            _InterfaceMap[key] = id;
            return id;
        }
    }

    /// <summary>
    /// Tries to find a channel name from AppText channel name discovery.
    /// Maps the BLF object type to the AppText bus type for lookup.
    /// </summary>
    private string? TryGetChannelName(uint objectType, ushort channel)
    {
        if (_ChannelNames is null || _ChannelNames.Count == 0)
        {
            return null;
        }

        byte busType = GetBusTypeForObjectType(objectType);
        if (busType == 0)
        {
            return null;
        }

        // AppText channel numbers are 0-based
        return _ChannelNames.TryGetValue((busType, (byte)channel), out string? name) ? name : null;
    }

    /// <summary>
    /// Returns the link type for a given BLF object type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LinkType GetLinkTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => LinkType.Ethernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
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
    private static string GetBusName(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => "Ethernet",

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 => "CAN",

        BlfConstants.ObjTypeCanFdMessage or BlfConstants.ObjTypeCanFdMessage64
            or BlfConstants.ObjTypeCanFdError64 => "CAN FD",

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
    private static byte GetBusTypeForObjectType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetRxError => BlfConstants.BusTypeEthernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
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
