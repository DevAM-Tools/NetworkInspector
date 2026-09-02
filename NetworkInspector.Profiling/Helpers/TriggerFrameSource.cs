// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Blocks in <see cref="NextFrame"/> until <see cref="Release"/>, then yields one frame
/// and blocks again until cancellation. Keeps the session Running after a finite source drains
/// so an on-demand value-cache slot can backfill stored packets.
/// </summary>
internal sealed class TriggerFrameSource : IFrameSource
{
    #region Fields

    private readonly Frame _Frame;
    private readonly ManualResetEventSlim _Release = new(false);
    private readonly ManualResetEventSlim _StayOpen = new(false);
    private volatile int _Emitted;
    private volatile bool _Started;
    private volatile int _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>Creates a source that emits <paramref name="frame"/> once after <see cref="Release"/>.</summary>
    internal TriggerFrameSource(Frame frame) => _Frame = frame;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _Started = false;
        _Release.Set();
        _StayOpen.Set();
        _Release.Dispose();
        _StayOpen.Dispose();
    }

    #endregion

    #region Public API

    /// <summary>Unblocks <see cref="NextFrame"/> so the single trigger frame can be emitted.</summary>
    internal void Release() => _Release.Set();

    /// <inheritdoc/>
    public string UiName => "TriggerFrame";

    /// <inheritdoc/>
    public string? Description => "Emits one frame after Release(), then blocks until cancel.";

    /// <inheritdoc/>
    public int? EstimatedFrameCount => null;

    /// <inheritdoc/>
    public bool IsRunning => _Started && _Disposed == 0;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        ArgumentNullException.ThrowIfNull(registry);
        _ = sourceId;
        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        _Release.Wait(cancellationToken);
        if (Interlocked.Exchange(ref _Emitted, 1) == 0)
        {
            return _Frame;
        }

        _StayOpen.Wait(cancellationToken);
        return null;
    }

    #endregion
}
