// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// In-memory random-access frame source over a pre-built frame array.
/// Used by session redissect scenarios so ingest and listeners share the same frames
/// as <c>parse-random-frames</c> without regenerating bytes.
/// </summary>
internal sealed class MemoryFrameSource : IRandomAccessFrameSource
{
    #region Fields

    private readonly Frame[] _Frames;
    private readonly bool _Loop;
    private volatile int _NextIndex;
    private volatile bool _Started;
    private volatile int _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates a source that yields <paramref name="frames"/> in order.
    /// When <paramref name="loop"/> is <see langword="true"/>, <see cref="NextFrame"/>
    /// cycles the array and never returns <see langword="null"/>. Payloads are identical
    /// across the template set, so wrapping <see cref="FrameId"/> values stay valid for redissect.
    /// </summary>
    internal MemoryFrameSource(Frame[] frames, bool loop = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfZero(frames.Length);
        _Frames = frames;
        _Loop = loop;
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
    public string UiName => "MemoryFrames";

    /// <inheritdoc/>
    public string? Description =>
        FormattableString.Invariant($"In-memory frame source ({_Frames.Length} frames).");

    /// <inheritdoc/>
    public int? EstimatedFrameCount
    {
        get
        {
            if (_Loop)
            {
                return null;
            }

            return _Frames.Length;
        }
    }

    /// <inheritdoc/>
    public bool IsRunning => _Started && _Disposed == 0;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        ArgumentNullException.ThrowIfNull(registry);
        _ = sourceId;
        _NextIndex = 0;
        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_Started)
        {
            throw new InvalidOperationException("MemoryFrameSource.Start() must be called before NextFrame().");
        }

        int index = Interlocked.Increment(ref _NextIndex) - 1;
        if (index < 0)
        {
            return null;
        }

        if (!_Loop)
        {
            if ((uint)index >= (uint)_Frames.Length)
            {
                return null;
            }

            return _Frames[index];
        }

        int slot = (int)((uint)index % (uint)_Frames.Length);
        return _Frames[slot];
    }

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        int index = id.Value;
        if (index < 0)
        {
            return null;
        }

        if ((uint)index < (uint)_Frames.Length)
        {
            return _Frames[index];
        }

        if (!_Loop)
        {
            return null;
        }

        int slot = (int)((uint)index % (uint)_Frames.Length);
        return _Frames[slot];
    }

    #endregion
}
