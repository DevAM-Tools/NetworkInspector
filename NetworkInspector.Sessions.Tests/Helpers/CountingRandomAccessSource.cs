// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Random-access source that counts <see cref="IRandomAccessFrameSource.FrameById"/> calls, so a test can prove that a
/// read path skipped the re-read (and therefore the re-parse) of an id.
/// </summary>
internal sealed class CountingRandomAccessSource : IRandomAccessFrameSource
{
    #region Fields

    private readonly TestFrameSource _Inner;
    private int _FrameByIdCalls;

    #endregion

    #region Constructors

    internal CountingRandomAccessSource(TestFrameSource inner) => _Inner = inner;

    #endregion

    #region Properties

    /// <summary>Number of <see cref="FrameById"/> calls so far.</summary>
    internal int FrameByIdCalls => Volatile.Read(ref _FrameByIdCalls);

    public string UiName => _Inner.UiName;

    public string? Description => _Inner.Description;

    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    public bool IsRunning => _Inner.IsRunning;

    #endregion

    #region IRandomAccessFrameSource

    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => _Inner.Start(sourceId, registry);

    public Frame? NextFrame(CancellationToken cancellationToken = default) => _Inner.NextFrame(cancellationToken);

    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _FrameByIdCalls);
        return _Inner.FrameById(id, cancellationToken);
    }

    public void Dispose() => _Inner.Dispose();

    #endregion
}
