// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Cached;

/// <summary>
/// A decorator that wraps any <see cref="IFrameSource"/> and adds random-access
/// capability by caching all frames read through <see cref="NextFrame"/> in a
/// lock-free chunked array.
///
/// <para>
/// <b>Use case:</b> Sources that do not natively support random access (e.g., streams,
/// pipes, live captures) can be wrapped to enable <see cref="IRandomAccessFrameSource.FrameById"/>.
/// The session uses this wrapper to provide <c>GetPacket</c> random-access even for
/// stream-based sources.
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item><see cref="NextFrame"/> is single-threaded (called by the source job thread).</item>
///   <item><see cref="FrameById"/> is thread-safe for concurrent reads from any thread
///         (UI, export, random access) via <see cref="Volatile"/> reads.</item>
///   <item>No locks are used. The only synchronisation is <see cref="Volatile.Write{T}"/>
///         for publishing new chunks and <see cref="Volatile.Read{T}"/> for consuming them.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Memory layout:</b>
/// Frames are stored in a chunked array identical in structure to <c>PacketToFrameMap</c>.
/// Each chunk holds 16384 frames. Chunks are allocated lazily on first write.
/// With 8192 maximum chunks, the cache supports up to ~134 million frames.
/// A frame reference is 8 bytes (reference to boxed <see cref="Frame"/>? or struct wrapper),
/// so each chunk consumes ~128 KB of reference storage.
/// The actual frame data (<see cref="ReadOnlyMemory{T}"/>) is owned by the underlying source.
/// </para>
///
/// <para>
/// <b>Cache cap:</b> When more than
/// <c>MaxChunks × ChunkSize</c> (≈ 134 217 728) frames are observed, additional frames are
/// still returned by <see cref="NextFrame"/> (delegated to the inner source) but cannot
/// be cached. <see cref="FrameById"/> for those IDs returns <see langword="null"/>, and
/// <see cref="IsCacheCapped"/> becomes <see langword="true"/> on the first overflow so
/// hosts can surface a diagnostic. This avoids a silent contract violation while keeping
/// the steady-state cache lock-free.
/// </para>
/// </summary>
public sealed class CachedFrameSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Constants

    // 2^14 = 16384 slots per chunk, ~128 KB of references per chunk.
    private const int ChunkShift = 14;
    private const int ChunkSize = 1 << ChunkShift;  // 16384
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxChunks = 8192;              // 8192 × 16384 = ~134M frames

    /// <summary>
    /// Default maximum memory budget for cached frame structs: 512 MiB on 64-bit
    /// processes, 64 MiB on 32-bit to avoid address-space exhaustion.
    /// The budget covers only the <see cref="Frame"/> structs stored in the chunk
    /// arrays (8 bytes each), not the underlying packet data referenced by
    /// <see cref="Frame.Data"/>.
    /// </summary>
    public static long DefaultMaxCacheMemoryBytes { get; } =
        Environment.Is64BitProcess ? 512L * 1024 * 1024 : 64L * 1024 * 1024;

    /// <summary>Approximate byte size of a <see cref="Frame"/> struct reference stored in a chunk slot.</summary>
    private const int FrameSlotBytes = 8;

    #endregion

    #region Fields

    private readonly IFrameSource _Inner;

    /// <summary>Inner source cast to IErrorTolerantFrameSource, or null if not supported.</summary>
    private readonly IErrorTolerantFrameSource? _InnerErrorTolerant;

    // Outer array of chunk references. Inner arrays are allocated lazily.
    // Writes: source thread only (single writer per chunk slot).
    // Reads: any thread via Volatile.Read on the chunk reference + validity flag.
    private readonly Frame[][] _Chunks = new Frame[MaxChunks][];
    private readonly bool[][] _Valid = new bool[MaxChunks][];

    /// <summary>Maximum number of frame-struct bytes to cache before switching to pass-through mode.</summary>
    private readonly long _MaxCacheMemoryBytes;

    /// <summary>Running count of bytes allocated for frame structs in chunk arrays.</summary>
    private long _CacheMemoryBytes;

    // ── Own lifecycle state (Volatile R/W for cross-thread visibility) ─────────

    /// <summary>Whether <see cref="Start"/> has been called on this wrapper.</summary>
    private bool _Started;

    /// <summary>Whether <see cref="Dispose"/> has been called on this wrapper.</summary>
    private bool _Disposed;

    /// <summary>
    /// Set to <see langword="true"/> the first time <see cref="CacheFrame"/> is asked to
    /// store a frame whose <see cref="FrameId"/> exceeds the cache capacity
    /// (<see cref="MaxChunks"/> × <see cref="ChunkSize"/>), or when the memory budget
    /// (<see cref="_MaxCacheMemoryBytes"/>) is reached, or when an
    /// <see cref="OutOfMemoryException"/> occurs during chunk allocation.
    /// Inspected via <see cref="IsCacheCapped"/>.
    /// </summary>
    private bool _CacheCapped;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="CachedFrameSource"/> wrapping the given source.
    /// </summary>
    /// <param name="inner">
    /// The underlying frame source to wrap. Must not be <see langword="null"/>.
    /// Must not already implement <see cref="IRandomAccessFrameSource"/>
    /// (use the original source directly instead).
    /// </param>
    /// <param name="maxCacheMemoryBytes">
    /// Maximum number of bytes to use for cached <see cref="Frame"/> structs.
    /// Defaults to <see cref="DefaultMaxCacheMemoryBytes"/> when <c>null</c>.
    /// </param>
    public CachedFrameSource(IFrameSource inner, long? maxCacheMemoryBytes = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (inner is IRandomAccessFrameSource)
        {
            throw new ArgumentException(
                $"The source '{inner.UiName}' already supports random access. " +
                "Wrapping it in CachedFrameSource is unnecessary — use the source directly.",
                nameof(inner));
        }

        long budget = maxCacheMemoryBytes ?? DefaultMaxCacheMemoryBytes;
        if (budget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCacheMemoryBytes), budget, "Cache memory budget must be positive.");
        }

        _Inner = inner;
        _InnerErrorTolerant = inner as IErrorTolerantFrameSource;
        _MaxCacheMemoryBytes = budget;
    }
    #endregion

    #region IFrameSource Implementation
    // ── IFrameSource delegation ───────────────────────────────────────────────

    /// <inheritdoc/>
    public string UiName => _Inner.UiName;

    /// <inheritdoc/>
    public string? Description => _Inner.Description;

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    /// <inheritdoc/>
    public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);

    /// <summary>
    /// <see langword="true"/> if at least one frame has been observed whose
    /// <see cref="FrameId"/> exceeds the cache capacity (~134 M). In that case
    /// <see cref="FrameById"/> for the affected IDs returns <see langword="null"/>;
    /// <see cref="NextFrame"/> still delivers them.
    /// </summary>
    public bool IsCacheCapped => Volatile.Read(ref _CacheCapped);

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);
        ArgumentNullException.ThrowIfNull(registry);

        _Inner.Start(sourceId, registry);
        Volatile.Write(ref _Started, true);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the next frame from the underlying source and caches it for later
    /// random access via <see cref="FrameById"/>. The frame is stored in a
    /// lock-free chunked array indexed by <see cref="FrameId"/>.
    /// </remarks>
    public Frame? NextFrame()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
        {
            throw new InvalidOperationException("CachedFrameSource.Start() must be called before NextFrame().");
        }

        Frame? frame = _Inner.NextFrame();

        if (frame is not null)
        {
            CacheFrame(frame.Value);
        }

        return frame;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Disposes the inner source via <see cref="IDisposable"/> (inherited from <see cref="IFrameSource"/>).
    /// This ensures that file handles, memory-mapped views, and other native
    /// resources held by the wrapped source are released properly.
    /// </remarks>
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
        {
            return;
        }

        Volatile.Write(ref _Disposed, true);
        // GC.SuppressFinalize is called before the inner-source disposal so it executes
        // even if _Inner.Dispose() throws, preserving finalizer suppression.
        GC.SuppressFinalize(this);
        _Inner.Dispose();
    }

    #endregion

    #region IErrorTolerantFrameSource Implementation
    // ── IErrorTolerantFrameSource delegation ──────────────────────────────────

    /// <inheritdoc/>
    public long ReadFrameCount => _InnerErrorTolerant?.ReadFrameCount ?? 0;

    /// <inheritdoc/>
    public long SkippedFrameCount => _InnerErrorTolerant?.SkippedFrameCount ?? 0;

    /// <inheritdoc/>
    public long ErrorCount => _InnerErrorTolerant?.ErrorCount ?? 0;

    /// <inheritdoc/>
    public bool HasErrors => _InnerErrorTolerant?.HasErrors ?? false;

    /// <inheritdoc/>
    public ErrorToleranceMode ErrorTolerance
    {
        get => _InnerErrorTolerant?.ErrorTolerance ?? ErrorToleranceMode.Tolerant;
        set
        {
            if (_InnerErrorTolerant is not null)
            {
                _InnerErrorTolerant.ErrorTolerance = value;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> when the wrapped source does
    /// not implement <see cref="IErrorTolerantFrameSource"/>: silently no-op'ing
    /// add/remove would let callers believe their handler is registered when it
    /// can never fire, which violates the "never fail silently" rule.
    /// </remarks>
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped
    {
        add
        {
            if (_InnerErrorTolerant is null)
            {
                throw new InvalidOperationException(
                    "The wrapped frame source does not implement IErrorTolerantFrameSource; "
                    + "FrameSkipped subscriptions would never fire.");
            }
            _InnerErrorTolerant.FrameSkipped += value;
        }
        remove
        {
            if (_InnerErrorTolerant is null)
            {
                throw new InvalidOperationException(
                    "The wrapped frame source does not implement IErrorTolerantFrameSource; "
                    + "FrameSkipped subscriptions would never fire.");
            }
            _InnerErrorTolerant.FrameSkipped -= value;
        }
    }
    #endregion

    #region IRandomAccessFrameSource Implementation
    // ── IRandomAccessFrameSource ──────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Retrieves a previously cached frame by its <see cref="FrameId"/>.
    /// Lock-free: uses <see cref="Volatile.Read{T}"/> on the chunk reference and
    /// a validity flag to ensure cross-thread visibility.
    /// Returns <see langword="null"/> if the frame was never read through
    /// <see cref="NextFrame"/> or the ID is invalid.
    /// </remarks>
    public Frame? FrameById(FrameId id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!id.IsValid)
        {
            return null;
        }

        int chunkIdx = id.Value >> ChunkShift;
        int slotIdx = id.Value & ChunkMask;

        // Bounds check: FrameId beyond supported range.
        if ((uint)chunkIdx >= MaxChunks)
        {
            return null;
        }

        // Read the chunk reference with acquire semantics.
        Frame[]? chunk = Volatile.Read(ref _Chunks[chunkIdx]);
        if (chunk is null)
        {
            return null;
        }

        // Read the validity flag. The write side ensures the frame data is fully
        // visible before the flag is set (Volatile.Write acts as a release fence).
        bool[]? validChunk = Volatile.Read(ref _Valid[chunkIdx]);
        if (validChunk is null || !Volatile.Read(ref validChunk[slotIdx]))
        {
            return null;
        }

        return chunk[slotIdx];
    }
    #endregion

    #region Private Helpers
    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Stores a frame in the chunked cache. Called only from the source thread
    /// (single writer). Chunks are allocated lazily on first access.
    ///
    /// <para>
    /// <b>Memory ordering:</b> The frame struct is written to the slot first,
    /// then the validity flag is set with <see cref="Volatile.Write{T}(ref T, T)"/>
    /// which acts as a release fence. Readers see the flag only after the frame
    /// data is fully committed.
    /// </para>
    ///
    /// <para>
    /// <b>Memory cap:</b> Once <see cref="_MaxCacheMemoryBytes"/> is exceeded,
    /// or an <see cref="OutOfMemoryException"/> is thrown during chunk allocation,
    /// <see cref="_CacheCapped"/> is set and all subsequent frames are forwarded
    /// without being stored. <see cref="FrameById"/> for uncached IDs returns
    /// <see langword="null"/>.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheFrame(Frame frame)
    {
        // Guard against invalid frame IDs (negative values would produce wrong chunk index)
        if (!frame.Id.IsValid)
        {
            return;
        }

        // Once capped, skip all caching to keep the pass-through path fast.
        if (Volatile.Read(ref _CacheCapped))
        {
            return;
        }

        int chunkIdx = frame.Id.Value >> ChunkShift;
        int slotIdx = frame.Id.Value & ChunkMask;

        // Beyond supported range — the wrapped source returned a frame whose ID
        // is past the cache capacity (~134 M frames). The frame itself is still
        // delivered to the caller of NextFrame() unchanged; only the cache copy
        // is dropped. Surface IsCacheCapped so hosts can warn the user.
        if ((uint)chunkIdx >= MaxChunks)
        {
            Volatile.Write(ref _CacheCapped, true);
            return;
        }

        Frame[]? chunk = _Chunks[chunkIdx];
        bool[]? validChunk = _Valid[chunkIdx];

        if (chunk is null)
        {
            // Check memory budget before allocating a new chunk.
            // Each chunk holds ChunkSize frames; account for both the frame array and
            // the parallel validity boolean array.
            long chunkBytes = (long)ChunkSize * (FrameSlotBytes + sizeof(bool));
            if (_CacheMemoryBytes + chunkBytes > _MaxCacheMemoryBytes)
            {
                Volatile.Write(ref _CacheCapped, true);
                return;
            }

            // Catch OutOfMemoryException during lazy chunk allocation.
            // Rather than crashing the source job, switch to pass-through mode.
            try
            {
                // Lazy allocation. Single writer per source thread, so no CAS needed.
                chunk = new Frame[ChunkSize];
                validChunk = new bool[ChunkSize];
            }
            catch (OutOfMemoryException)
            {
                Volatile.Write(ref _CacheCapped, true);
                return;
            }

            _CacheMemoryBytes += chunkBytes;

            // Publish the chunks atomically so readers see fully initialised arrays.
            Volatile.Write(ref _Chunks[chunkIdx], chunk);
            Volatile.Write(ref _Valid[chunkIdx], validChunk);
        }

        // Write the frame data first, then set the validity flag (release fence).
        // Readers check the flag before accessing the frame data.
        chunk[slotIdx] = frame;
        Volatile.Write(ref validChunk![slotIdx], true);
    }

    #endregion
}
