// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// A live random-access source for the fault and ingest tests: the first <c>freeFrames</c> frames are handed out at once; every
/// later frame is handed out only after the test called <see cref="ReleaseNextFrame"/>, so ingest can be stopped and
/// resumed at exact points without sleeps. <see cref="FailFrameId"/> makes <see cref="FrameById"/> return
/// <see langword="null"/> for one id, which makes a restart's re-parse fail ("a frame that cannot be re-read").
///
/// <para>
/// Thread-safety: the frame store is guarded by a lock; the failure id is volatile; the gate is a semaphore. A source thread
/// blocked in <see cref="NextFrame"/> ends with <see cref="OperationCanceledException"/> when its token fires.
/// </para>
/// </summary>
internal sealed class GatedRandomAccessSource : IRandomAccessFrameSource
{
    private readonly int _FreeFrames;
    private readonly int _TotalFrames;
    private readonly SemaphoreSlim _Gate = new(0);
    private readonly Lock _Lock = new();
    private readonly Dictionary<int, Frame> _Produced = [];
    private FrameInterfaceRegistry? _Registry;
    private FrameInterfaceId _InterfaceId;
    private int _NextIndex;
    private volatile int _FailFrameId = -1;
    private int _HandedOut;

    /// <summary>Creates the source.</summary>
    /// <param name="freeFrames">Frames handed out without a release.</param>
    /// <param name="totalFrames">Frames the source ever produces; afterwards <see cref="NextFrame"/> returns <see langword="null"/>.</param>
    internal GatedRandomAccessSource(int freeFrames, int totalFrames)
    {
        _FreeFrames = freeFrames;
        _TotalFrames = totalFrames;
    }

    /// <summary>Raised on the source thread when it waits for a release (before it blocks).</summary>
    internal ManualResetEventSlim Waiting { get; } = new(false);

    /// <summary>The frame id <see cref="FrameById"/> answers with <see langword="null"/>, or -1 for none.</summary>
    internal int FailFrameId
    {
        get => _FailFrameId;
        set => _FailFrameId = value;
    }

    /// <summary>How many frames were handed out so far.</summary>
    internal int HandedOut => Volatile.Read(ref _HandedOut);

    /// <summary>Lets the source thread hand out one more gated frame.</summary>
    internal void ReleaseNextFrame() => _ = _Gate.Release();

    /// <inheritdoc/>
    public string UiName => "GatedSource";

    /// <inheritdoc/>
    public string? Description => "Hands out frames after the test released them";

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _TotalFrames;

    /// <inheritdoc/>
    public bool IsRunning => _Registry is not null;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _Registry = registry;
        _InterfaceId = registry.Register(sourceId, "test_eth", null, LinkType.Ethernet);
        _NextIndex = 0;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_NextIndex >= _TotalFrames)
        {
            return null;
        }

        if (_NextIndex >= _FreeFrames)
        {
            Waiting.Set();
            _Gate.Wait(cancellationToken);
            Waiting.Reset();
        }

        int index = _NextIndex++;
        Frame frame = Frame.Create(
            new FrameId(index),
            Timestamp.FromNanos(index * 1_000_000L),
            TestHarness.GenerateUdpFrame(64),
            LinkType.Ethernet,
            _InterfaceId,
            _Registry!).Value; // Non-null: Start ran before the first NextFrame.
        lock (_Lock)
        {
            _Produced[index] = frame;
        }

        _ = Interlocked.Increment(ref _HandedOut);
        return frame;
    }

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == _FailFrameId)
        {
            return null;
        }

        lock (_Lock)
        {
            return _Produced.TryGetValue(id.Value, out Frame frame)
                ? frame
                : null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Releases a source thread that is still waiting, so a disposed session never leaves a thread behind.
        _ = _Gate.Release(1);
    }
}

/// <summary>A value-cache listener that ignores everything; for the tests whose subject is the registration, not the rows.</summary>
internal sealed class NullValueCacheListener : IValueCacheListener
{
    /// <inheritdoc/>
    public string UiName => "null-cache";

    /// <inheritdoc/>
    public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
    {
    }
}
