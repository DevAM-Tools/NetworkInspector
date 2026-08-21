// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Cached;

/// <summary>
/// A decorator that wraps any <see cref="IFrameSource"/> and adds random-access
/// capability by caching all frames read through <see cref="NextFrame"/> in a
/// lock-free chunked array.
///
/// <para>
/// <b>Capacity:</b> Supports all valid <see cref="FrameId"/> values
/// (<c>0 … Array.MaxLength - 1</c>). Chunks are allocated lazily on demand.
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

    private readonly Core.Collections.ChunkedOuterArray<Frame[]> _FrameChunks = new(_ChunkShift);
    private readonly Core.Collections.ChunkedOuterArray<bool[]> _ValidChunks = new(_ChunkShift);

    /// <summary>Whether <see cref="Start"/> has been called on this wrapper.</summary>
    private volatile bool _Started;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>
    /// Set when an <see cref="OutOfMemoryException"/> occurs during chunk allocation.
    /// Inspected via <see cref="IsCacheCapped"/>.
    /// </summary>
    private volatile bool _CacheCapped;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="CachedFrameSource"/> wrapping the given source.
    /// </summary>
    /// <param name="inner">
    /// The underlying frame source to wrap. Must not be <see langword="null"/>.
    /// Must not already implement <see cref="IRandomAccessFrameSource"/>.
    /// </param>
    public CachedFrameSource(IFrameSource inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (inner is IRandomAccessFrameSource)
        {
            throw new ArgumentException(
                $"The source '{inner.UiName}' already supports random access. " +
                "Wrapping it in CachedFrameSource is unnecessary — use the source directly.",
                nameof(inner));
        }

        _Inner = inner;
        _InnerErrorTolerant = inner as IErrorTolerantFrameSource;
    }

    #endregion

    #region IFrameSource Implementation

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

        (int chunkIdx, int slotIdx) = _FrameChunks.DecomposeIndex(id.Value);

        Frame[]? chunk = _FrameChunks.GetChunk(chunkIdx);
        if (chunk is null)
        {
            return null;
        }

        bool[]? validChunk = _ValidChunks.GetChunk(chunkIdx);
        if (validChunk is null || !Volatile.Read(ref validChunk[slotIdx]))
        {
            return null;
        }

        return chunk[slotIdx];
    }

    #endregion

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _CacheFrame(Frame frame)
    {
        if (!frame.Id.IsValid || _CacheCapped)
        {
            return;
        }

        (int chunkIdx, int slotIdx) = _FrameChunks.DecomposeIndex(frame.Id.Value);

        try
        {
            Frame[] chunk = _FrameChunks.GetOrAllocateChunk(
                chunkIdx,
                () => new Frame[_FrameChunks.ChunkSize]);

            bool[] validChunk = _ValidChunks.GetOrAllocateChunk(
                chunkIdx,
                () => new bool[_ValidChunks.ChunkSize]);

            chunk[slotIdx] = frame;
            Volatile.Write(ref validChunk[slotIdx], true);
        }
        catch (OutOfMemoryException)
        {
            _CacheCapped = true;
        }
    }

    #endregion
}
