// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Test <see cref="IFrameSource"/> and <see cref="IRandomAccessFrameSource"/> that yields
/// a configurable number of frames, then returns <see langword="null"/> repeatedly until
/// <see cref="Release"/> is called or the instance is disposed. Used for testing
/// <see cref="ISession.TryUnsubscribe"/> on active sources.
///
/// <para>
/// After all initial frames are consumed, <see cref="NextFrame"/> returns
/// <see langword="null"/>. Because <c>RunSourceLoop</c> exits on <c>null</c>,
/// the source completes normally. To keep the source alive and blocking,
/// the source returns frames in a slow drip after the initial batch, stopping
/// only when cancelled or released.
/// </para>
/// </summary>
internal sealed class BlockingTestFrameSource : IRandomAccessFrameSource
{
    private readonly int _InitialFrameCount;

    // Signalled when the source should stop producing frames.
    private readonly ManualResetEventSlim _ReleaseGate = new(false);

    private int _NextIndex;
    private FrameInterfaceId _InterfaceId;
    private FrameInterfaceRegistry? _Registry;

    // Stores produced frames by FrameId.Value for random-access retrieval.
    private readonly Dictionary<int, Frame> _ProducedFrames = [];

    /// <summary>
    /// Creates a blocking test source that delivers <paramref name="initialFrameCount"/>
    /// frames quickly, then drip-feeds one frame every 50 ms until <see cref="Release"/>
    /// is called.
    /// </summary>
    internal BlockingTestFrameSource(int initialFrameCount)
    {
        _InitialFrameCount = initialFrameCount;
    }

    /// <inheritdoc/>
    public string UiName => "BlockingTestSource";

    /// <inheritdoc/>
    public string? Description => "Test source that drip-feeds frames after initial batch";

    /// <inheritdoc/>
    public int? EstimatedFrameCount => null;

    /// <inheritdoc/>
    public bool IsRunning => _Registry is not null;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _Registry = registry;
        _InterfaceId = registry.Register(sourceId, "test_eth", null, LinkType.Ethernet);
        _NextIndex = 0;
    }

    /// <summary>
    /// Signals the source to stop producing frames. The next <see cref="NextFrame"/>
    /// call returns <see langword="null"/>, causing the source loop to exit normally.
    /// </summary>
    internal void Release() => _ReleaseGate.Set();

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // After initial batch, check if released or wait between drip frames.
        if (_NextIndex >= _InitialFrameCount)
        {
            // Check if we should stop.
            if (_ReleaseGate.IsSet)
            {
                return null;
            }

            // Drip-feed: wait 50 ms, then produce another frame.
            // This keeps the source thread alive (RunSourceLoop's while loop
            // keeps iterating) while being responsive to cancellation.
            // RunSourceLoop checks ct.IsCancellationRequested BEFORE calling
            // NextFrame, so cancellation takes effect within ~50 ms.
            _ReleaseGate.Wait(50, cancellationToken);
            if (_ReleaseGate.IsSet)
            {
                return null;
            }
        }

        int idx = _NextIndex++;
        byte[] data = TestHarness.GenerateUdpFrame(64);

        Frame frame = Frame.Create(
            new FrameId(idx),
            Timestamp.FromNanos(idx * 1_000_000L),
            data,
            LinkType.Ethernet,
            _InterfaceId,
            _Registry!).Value;

        _ProducedFrames[idx] = frame;

        return frame;
    }

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_ProducedFrames.TryGetValue(id.Value, out Frame frame))
        {
            return frame;
        }
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Safe for double-dispose: Set() is idempotent, and we guard the
        // ManualResetEventSlim.Dispose() with a volatile flag.
        _ReleaseGate.Set();
        if (Interlocked.Exchange(ref _Disposed, 1) == 0)
        {
            _ReleaseGate.Dispose();
        }
        _Registry = null;
    }

    private int _Disposed;
}
