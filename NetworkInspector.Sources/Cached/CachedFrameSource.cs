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
/// </summary>
public sealed class CachedFrameSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Constants

    private const int _ChunkShift = 14;

    #endregion

    #region Fields

    private readonly IFrameSource _Inner;

    /// <summary>Inner source cast to IErrorTolerantFrameSource, or null if not supported.</summary>
    private readonly IErrorTolerantFrameSource? _InnerErrorTolerant;

    private readonly IRandomAccessFrameSource? _InnerRandomAccess;

    private readonly Core.Collections.ChunkedOuterArray<Frame[]> _FrameChunks;
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

    /// <summary>Highest cached frame id. Written by the source thread, read by estimators.</summary>
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
    /// not once per unique array.
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

        Frame? frame = _Inner.NextFrame(cancellationToken);

        if (frame is not null)
        {
            _CacheFrame(frame.Value);
        }

        return frame;
    }

    /// <inheritdoc/>
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

        Frame? held = _TryGetHeldFrame(id);
        if (held is not null)
        {
            return held;
        }

        return _TryInnerFrameById(id, cancellationToken);
    }

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Frame? _TryInnerFrameById(FrameId id, CancellationToken cancellationToken)
    {
        if (_InnerRandomAccess is null)
        {
            return null;
        }

        return _InnerRandomAccess.FrameById(id, cancellationToken);
    }

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

        bool[]? validChunk = valid.GetChunk(chunkIdx);
        if (validChunk is null || !Volatile.Read(ref validChunk[slotIdx]))
        {
            return null;
        }

        return chunk[slotIdx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _CacheFrame(Frame frame)
    {
        if (!frame.Id.IsValid || _CacheCapped)
        {
            return;
        }

        Core.Collections.ChunkedOuterArray<Frame[]> frames = _FrameChunks;
        Core.Collections.ChunkedOuterArray<bool[]> valid = _ValidChunks;
        (int chunkIdx, int slotIdx) = frames.DecomposeIndex(frame.Id.Value);

        try
        {
            Frame[] chunk = frames.GetOrAllocateChunk(
                chunkIdx,
                () => new Frame[frames.ChunkSize]);

            bool[] validChunk = valid.GetOrAllocateChunk(
                chunkIdx,
                () => new bool[valid.ChunkSize]);

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
            _CacheCapped = true;
        }
    }

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
