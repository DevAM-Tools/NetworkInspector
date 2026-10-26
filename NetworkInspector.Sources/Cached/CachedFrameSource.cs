// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Cached;

/// <summary>
/// A decorator that wraps any <see cref="IFrameSource"/> and adds random-access
/// capability by caching all frames read through <see cref="NextFrame"/>.
/// Each published slot holds the inner <see cref="Frame"/> value (payload memory is
/// whatever the inner source published — per-frame arrays, mmap slices, …).
///
/// <para>
/// <b>Capacity:</b> Supports all valid <see cref="FrameId"/> values
/// (<c>0 … Array.MaxLength - 1</c>). Chunks are allocated lazily on demand.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="NextFrame"/> is the single cache writer (the source job thread).
/// Any number of threads may call <see cref="FrameById"/> concurrently, including while
/// <see cref="NextFrame"/> is still caching. A slot is published with a <see cref="Volatile"/>
/// valid-flag write after the <see cref="Frame"/> is stored. Do not share this instance’s
/// writer role across threads.
/// </para>
/// <para>
/// <b>Memory:</b> the cache holds one slot (the <see cref="Frame"/> struct) per frame read plus a one-byte valid flag, and
/// it keeps every frame's payload reachable. There is no byte budget and no configurable limit: the only bound is host
/// memory. When a chunk allocation throws <see cref="OutOfMemoryException"/> the cache stops caching for the rest of the
/// lifetime (<see cref="IsCacheCapped"/>); frames read afterwards are still returned by <see cref="NextFrame"/> but are no
/// longer retrievable through <see cref="FrameById"/> (unless the inner source can serve them). Nothing is evicted and
/// the cache is not cleared by <see cref="Dispose"/>; it is released when the wrapper is collected.
/// </para>
/// </summary>
public sealed class CachedFrameSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Constants

    /// <summary>
    /// Log₂ of the slots per chunk: 2^14 = 16 384 frames per <c>Frame[]</c> chunk (and per <c>bool[]</c> valid chunk).
    /// Chunks are allocated lazily as the frame id grows, so the cache never needs one huge contiguous array or a
    /// copy-on-grow of all frames; the cost is one extra indirection per lookup (<c>ChunkedOuterArray</c>). A fixed
    /// tuning value, not configurable.
    /// </summary>
    private const int _ChunkShift = 14;

    /// <summary>
    /// Chunk factories as cached static delegates: <see cref="_CacheFrame"/> runs for every cached frame and passes one to
    /// <c>GetOrAllocateChunk</c>; a lambda that captures the arrays (to read their chunk size) would allocate a closure and a
    /// delegate on every frame. Both arrays use <see cref="_ChunkShift"/>, so the size is a constant.
    /// </summary>
    private static readonly Func<Frame[]> _NewFrameChunk = static () => new Frame[1 << _ChunkShift];

    /// <summary>Factory of the valid-flag chunks; see <see cref="_NewFrameChunk"/>.</summary>
    private static readonly Func<bool[]> _NewValidChunk = static () => new bool[1 << _ChunkShift];

    #endregion

    #region Fields

    /// <summary>The wrapped source; its frames are pulled only by <see cref="NextFrame"/> (the single source thread).</summary>
    private readonly IFrameSource _Inner;

    /// <summary>Inner source cast to IErrorTolerantFrameSource, or null if not supported.</summary>
    private readonly IErrorTolerantFrameSource? _InnerErrorTolerant;

    /// <summary>
    /// Inner source cast to <see cref="IRandomAccessFrameSource"/>, or null. Only set when the caller opted in with
    /// <c>allowRandomAccessInner</c>; <see cref="FrameById"/> falls back to it for ids that are not cached.
    /// </summary>
    private readonly IRandomAccessFrameSource? _InnerRandomAccess;

    /// <summary>
    /// Frame slots by frame id, in lazily allocated chunks. Written only by the source thread (single allocator contract
    /// of <c>ChunkedOuterArray</c>); read lock-free by any thread.
    /// </summary>
    private readonly Core.Collections.ChunkedOuterArray<Frame[]> _FrameChunks;

    /// <summary>
    /// Publication flags parallel to <see cref="_FrameChunks"/>: slot <c>i</c> is readable only when its flag is set. The
    /// flag is the release/acquire handshake that makes the non-atomic <see cref="Frame"/> struct write safe to read.
    /// </summary>
    private readonly Core.Collections.ChunkedOuterArray<bool[]> _ValidChunks;

    /// <summary>Whether <see cref="Start"/> has been called on this wrapper.</summary>
    private volatile bool _Started;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>
    /// Set when an <see cref="OutOfMemoryException"/> occurs during chunk allocation.
    /// Inspected via <see cref="IsCacheCapped"/>.
    /// </summary>
    private volatile bool _CacheCapped;

    /// <summary>
    /// Highest cached frame id (-1 = none). Written by the source thread (single writer, so a plain compare-then-store is
    /// safe), read by the estimators; <see langword="volatile"/> only so those readers see a recent value.
    /// </summary>
    private volatile int _HighestFrameId = -1;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="CachedFrameSource"/> wrapping the given source.
    /// The inner source must not already implement <see cref="IRandomAccessFrameSource"/>.
    /// </summary>
    /// <param name="inner">
    /// The underlying frame source to wrap. Must not be <see langword="null"/>.
    /// Must not already implement <see cref="IRandomAccessFrameSource"/>.
    /// Must not already be a <see cref="CachedFrameSource"/>.
    /// </param>
    public CachedFrameSource(IFrameSource inner)
        : this(inner, allowRandomAccessInner: false)
    {
    }

    /// <summary>
    /// Creates a cache wrapper.
    /// When <paramref name="allowRandomAccessInner"/> is <see langword="false"/>, an inner
    /// <see cref="IRandomAccessFrameSource"/> is rejected (same as the single-argument constructor).
    /// Session opt-in caching of file sources passes <see langword="true"/>.
    /// </summary>
    /// <param name="inner">Source to wrap. Must not be <see langword="null"/> or a <see cref="CachedFrameSource"/>.</param>
    /// <param name="allowRandomAccessInner">
    /// When <see langword="true"/>, wrapping an <see cref="IRandomAccessFrameSource"/> is allowed.
    /// </param>
    public CachedFrameSource(IFrameSource inner, bool allowRandomAccessInner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (inner is CachedFrameSource)
        {
            throw new ArgumentException(
                "A CachedFrameSource cannot wrap another CachedFrameSource.",
                nameof(inner));
        }

        if (inner is IRandomAccessFrameSource && !allowRandomAccessInner)
        {
            throw new ArgumentException(
                $"The source '{inner.UiName}' already supports random access. " +
                "Wrapping it in CachedFrameSource is unnecessary — use the source directly.",
                nameof(inner));
        }

        _Inner = inner;
        _InnerErrorTolerant = inner as IErrorTolerantFrameSource;
        _InnerRandomAccess = inner as IRandomAccessFrameSource;
        _FrameChunks = new(_ChunkShift);
        _ValidChunks = new(_ChunkShift);
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string UiName => _Inner.UiName;

    /// <inheritdoc/>
    public string? Description => _Inner.Description;

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    /// <inheritdoc/>
    public bool IsRunning => _Started && _Disposed == 0;

    /// <summary>
    /// <see langword="true"/> if caching was disabled after an
    /// <see cref="OutOfMemoryException"/> during chunk allocation.
    /// </summary>
    public bool IsCacheCapped => _CacheCapped;

    /// <summary>
    /// Sum of published <see cref="Frame.Data"/> lengths. Shared backing is counted once per slot,
    /// not once per unique array. Walks every id up to the highest cached one (O(n) in the number of frames), so call it
    /// for occasional diagnostics, not per frame.
    /// </summary>
    public long EstimatedPayloadBytes => _EstimateHoldPayloadBytes();

    /// <summary>
    /// Allocated index backing: <see cref="Frame"/> slots at
    /// <c>Unsafe.SizeOf&lt;Frame&gt;()</c> plus valid flags.
    /// </summary>
    public long EstimatedIndexBytes => _EstimateHoldIndexBytes();

    #endregion

    #region IFrameSource Implementation

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        ArgumentNullException.ThrowIfNull(registry);

        _Inner.Start(sourceId, registry);
        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!_Started)
        {
            throw new InvalidOperationException("CachedFrameSource.Start() must be called before NextFrame().");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Pull the next frame from the inner source, then remember it. The frame itself is returned unchanged: the cache
        // stores the same Frame value (same payload memory), it never copies payload bytes.
        Frame? frame = _Inner.NextFrame(cancellationToken);

        if (frame is not null)
        {
            _CacheFrame(frame.Value);
        }

        return frame;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent (interlocked latch). Disposes the inner source; the cached frames stay referenced by this wrapper until it
    /// is collected, so a concurrent <see cref="FrameById"/> that already passed its disposed check still reads valid data.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
        _Inner.Dispose();
    }

    #endregion

    #region IErrorTolerantFrameSource Implementation

    /// <inheritdoc/>
    public int ReadFrameCount
    {
        get
        {
            if (_InnerErrorTolerant is null)
            {
                return 0;
            }

            return _InnerErrorTolerant.ReadFrameCount;
        }
    }

    /// <inheritdoc/>
    public int SkippedFrameCount
    {
        get
        {
            if (_InnerErrorTolerant is null)
            {
                return 0;
            }

            return _InnerErrorTolerant.SkippedFrameCount;
        }
    }

    /// <inheritdoc/>
    public int ErrorCount
    {
        get
        {
            if (_InnerErrorTolerant is null)
            {
                return 0;
            }

            return _InnerErrorTolerant.ErrorCount;
        }
    }

    /// <inheritdoc/>
    public bool HasErrors
    {
        get
        {
            if (_InnerErrorTolerant is null)
            {
                return false;
            }

            return _InnerErrorTolerant.HasErrors;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Forwarded to the inner source. When the inner source is not error tolerant the getter reports
    /// <see cref="ErrorToleranceMode.Tolerant"/> and the setter is ignored (there is nothing to configure).
    /// </remarks>
    public ErrorToleranceMode ErrorTolerance
    {
        get
        {
            if (_InnerErrorTolerant is null)
            {
                return ErrorToleranceMode.Tolerant;
            }

            return _InnerErrorTolerant.ErrorTolerance;
        }
        set
        {
            if (_InnerErrorTolerant is not null)
            {
                _InnerErrorTolerant.ErrorTolerance = value;
            }
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);

        if (!id.IsValid)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Lock-free lookup in the cache first (safe while NextFrame is still caching); only an uncached id (not read yet,
        // or after the cache was capped) may fall through to an opted-in random-access inner source.
        Frame? held = _TryGetHeldFrame(id);
        if (held is not null)
        {
            return held;
        }

        return _TryInnerFrameById(id, cancellationToken);
    }

    #endregion

    #region Private Helpers

    /// <summary>Delegates to the inner random-access source when there is one; null otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Frame? _TryInnerFrameById(FrameId id, CancellationToken cancellationToken)
    {
        if (_InnerRandomAccess is null)
        {
            return null;
        }

        return _InnerRandomAccess.FrameById(id, cancellationToken);
    }

    /// <summary>
    /// Reader side of the publication handshake: returns the cached frame for <paramref name="id"/>, or null when the id
    /// has not been (fully) published. Safe on any thread, concurrently with the writer in <see cref="_CacheFrame"/>.
    /// </summary>
    private Frame? _TryGetHeldFrame(FrameId id)
    {
        Core.Collections.ChunkedOuterArray<Frame[]> frames = _FrameChunks;
        Core.Collections.ChunkedOuterArray<bool[]> valid = _ValidChunks;
        (int chunkIdx, int slotIdx) = frames.DecomposeIndex(id.Value);
        Frame[]? chunk = frames.GetChunk(chunkIdx);
        if (chunk is null)
        {
            return null;
        }

        // Acquire-read the valid flag BEFORE touching the slot: the writer sets it (release) only after the whole Frame
        // struct is stored, so a true flag guarantees chunk[slotIdx] is complete. Reading the slot first could see a torn
        // multi-field struct.
        bool[]? validChunk = valid.GetChunk(chunkIdx);
        if (validChunk is null || !Volatile.Read(ref validChunk[slotIdx]))
        {
            return null;
        }

        return chunk[slotIdx];
    }

    /// <summary>
    /// Writer side of the publication handshake; called only from <see cref="NextFrame"/> on the single source thread.
    /// Order matters: allocate the chunks, store the frame, update the highest id, and only then set the valid flag
    /// (release). If an <see cref="OutOfMemoryException"/> interrupts it, the flag was never set, so no reader can see a
    /// half-cached slot and the cache simply stops growing (<see cref="IsCacheCapped"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _CacheFrame(Frame frame)
    {
        // A frame without a valid id has no slot to live in; once capped, caching is off for the rest of the lifetime.
        if (!frame.Id.IsValid || _CacheCapped)
        {
            return;
        }

        Core.Collections.ChunkedOuterArray<Frame[]> frames = _FrameChunks;
        Core.Collections.ChunkedOuterArray<bool[]> valid = _ValidChunks;
        (int chunkIdx, int slotIdx) = frames.DecomposeIndex(frame.Id.Value);

        try
        {
            // Static factories: a capturing lambda here allocated a closure and a delegate for every cached frame, although a
            // chunk is created only once per 16 384 frames.
            Frame[] chunk = frames.GetOrAllocateChunk(chunkIdx, _NewFrameChunk);
            bool[] validChunk = valid.GetOrAllocateChunk(chunkIdx, _NewValidChunk);

            // Plain (non-atomic) store of the Frame struct: it is not visible to readers until the flag below is set.
            chunk[slotIdx] = frame;
            int id = frame.Id.Value;
            if (id > _HighestFrameId)
            {
                _HighestFrameId = id;
            }

            Volatile.Write(ref validChunk[slotIdx], true);
        }
        catch (OutOfMemoryException)
        {
            // Memory is exhausted: stop caching instead of failing the whole read. The frame is still returned by
            // NextFrame; only its later random access is lost. Accepted residual risk: the cache itself has no size
            // limit, so it keeps allocating until the host runs out of memory.
            _CacheCapped = true;
        }
    }

    /// <summary>
    /// Sums the payload lengths of all published slots. Reader-side walk (any thread), so it applies the same flag-before-slot
    /// order as <see cref="_TryGetHeldFrame"/>; a slot being published concurrently is simply counted or not.
    /// O(highest id): it visits every id, including ids that were never cached.
    /// </summary>
    private long _EstimateHoldPayloadBytes()
    {
        Core.Collections.ChunkedOuterArray<Frame[]> frames = _FrameChunks;
        Core.Collections.ChunkedOuterArray<bool[]> valid = _ValidChunks;
        long total = 0;
        int highest = _HighestFrameId;
        for (int id = 0; id <= highest; id++)
        {
            (int chunkIdx, int slotIdx) = frames.DecomposeIndex(id);
            bool[]? validChunk = valid.GetChunk(chunkIdx);
            if (validChunk is null || !Volatile.Read(ref validChunk[slotIdx]))
            {
                continue;
            }

            Frame[]? chunk = frames.GetChunk(chunkIdx);
            if (chunk is null)
            {
                continue;
            }

            Frame stored = chunk[slotIdx];
            if (stored.IsValid)
            {
                total += stored.Data.Length;
            }
        }

        return total;
    }

    /// <summary>
    /// Sums the size of the allocated chunks (slot array plus flag array) up to the chunk of the highest cached id. Counts
    /// whole chunks, so a partly filled last chunk is included in full; cost is O(number of chunks).
    /// </summary>
    private long _EstimateHoldIndexBytes()
    {
        Core.Collections.ChunkedOuterArray<Frame[]> frames = _FrameChunks;
        Core.Collections.ChunkedOuterArray<bool[]> valid = _ValidChunks;
        long total = 0;
        int highest = _HighestFrameId;
        if (highest < 0)
        {
            return 0;
        }

        (int maxOuter, _) = frames.DecomposeIndex(highest);
        for (int outer = 0; outer <= maxOuter; outer++)
        {
            Frame[]? chunk = frames.GetChunk(outer);
            if (chunk is not null)
            {
                total += (long)chunk.Length * Unsafe.SizeOf<Frame>();
            }

            bool[]? validChunk = valid.GetChunk(outer);
            if (validChunk is not null)
            {
                total += validChunk.Length;
            }
        }

        return total;
    }

    #endregion
}
