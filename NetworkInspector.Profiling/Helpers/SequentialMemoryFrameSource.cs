// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Forward-only in-memory source over a pre-built frame array. Not random-access, so a
/// <see cref="Session"/> wraps it in <see cref="CachedFrameSource"/>.
/// </summary>
internal sealed class SequentialMemoryFrameSource : IFrameSource
{
    #region Fields

    private readonly Frame[] _Frames;
    private int _Next;
    private volatile bool _Started;
    private volatile int _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>Creates a source that yields <paramref name="frames"/> once in order.</summary>
    internal SequentialMemoryFrameSource(Frame[] frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Length);
        _Frames = frames;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Interlocked.Exchange(ref _Disposed, 1);
        _Started = false;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string UiName => "SequentialMemoryFrames";

    /// <inheritdoc/>
    public string? Description =>
        FormattableString.Invariant($"Sequential in-memory frames ({_Frames.Length} frames).");

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _Frames.Length;

    /// <inheritdoc/>
    public bool IsRunning => _Started && _Disposed == 0;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        ArgumentNullException.ThrowIfNull(registry);
        _ = sourceId;
        _Next = 0;
        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_Started)
        {
            throw new InvalidOperationException(
                "SequentialMemoryFrameSource.Start() must be called before NextFrame().");
        }

        if (_Next >= _Frames.Length)
        {
            return null;
        }

        Frame frame = _Frames[_Next];
        _Next++;
        return frame;
    }

    #endregion
}
