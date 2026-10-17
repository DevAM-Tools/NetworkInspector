// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Test <see cref="IFrameSource"/> and <see cref="IRandomAccessFrameSource"/> that yields
/// a configurable number of frames built from raw byte arrays. After all frames are consumed,
/// <see cref="NextFrame"/> returns <see langword="null"/>.
///
/// <para>
/// All produced frames are stored for random-access retrieval via <see cref="FrameById"/>,
/// enabling reparse tests (the session can re-read frames by ID during a stack-swap reparse).
/// </para>
/// </summary>
internal sealed class TestFrameSource : IRandomAccessFrameSource
{
    private readonly byte[][] _RawFrames;
    private readonly LinkType _LinkType;
    private int _NextIndex;
    private FrameInterfaceId _InterfaceId;
    private FrameInterfaceRegistry? _Registry;

    /// <summary>When set, <see cref="Dispose"/> throws (for shutdown error-path tests).</summary>
    internal bool ThrowOnDispose
    {
        get; set;
    }

    // Written by NextFrame (source thread), published for FrameById (listener threads).
    private readonly Frame[] _ProducedFrames;
    private readonly int[] _Published;

    /// <summary>
    /// Creates a test source that will yield the given raw frames in order.
    /// </summary>
    internal TestFrameSource(byte[][] rawFrames, LinkType linkType = LinkType.Ethernet)
    {
        _RawFrames = rawFrames;
        _LinkType = linkType;
        _ProducedFrames = new Frame[rawFrames.Length];
        _Published = new int[rawFrames.Length];
    }

    /// <summary>
    /// Creates a test source that yields <paramref name="count"/> identical UDP frames.
    /// </summary>
    internal static TestFrameSource WithUdpFrames(int count, int frameSize = 64)
    {
        byte[][] frames = new byte[count][];
        byte[] template = TestHarness.GenerateUdpFrame(frameSize);
        for (int i = 0; i < count; i++)
        {
            frames[i] = (byte[])template.Clone();
        }
        return new TestFrameSource(frames);
    }

    /// <inheritdoc/>
    public string UiName => "TestSource";

    /// <inheritdoc/>
    public string? Description => "Test frame source for unit tests";

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _RawFrames.Length;

    /// <inheritdoc/>
    public bool IsRunning => _Registry is not null;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _Registry = registry;
        _InterfaceId = registry.Register(sourceId, "test_eth", null, _LinkType);
        _NextIndex = 0;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_NextIndex >= _RawFrames.Length)
        {
            return null;
        }

        int idx = _NextIndex++;
        byte[] data = _RawFrames[idx];

        Frame frame = Frame.Create(
            new FrameId(idx),
            Timestamp.FromNanos(idx * 1_000_000L), // 1 ms apart
            data,
            _LinkType,
            _InterfaceId,
            _Registry!).Value;

        _ProducedFrames[idx] = frame;
        Volatile.Write(ref _Published[idx], 1);

        return frame;
    }

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int index = id.Value;
        if ((uint)index >= (uint)_ProducedFrames.Length)
        {
            return null;
        }

        if (Volatile.Read(ref _Published[index]) == 0)
        {
            return null;
        }

        return _ProducedFrames[index];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Dispose failed for test.");
        }

        _Registry = null;
    }
}

/// <summary>
/// Random-access source that yields every frame on <see cref="IFrameSource.NextFrame"/>
/// but returns <see langword="null"/> from <see cref="IRandomAccessFrameSource.FrameById"/>
/// for one index, so Restart / PullFill can observe a mid-range miss.
/// </summary>
internal sealed class HoleRandomAccessSource : IRandomAccessFrameSource
{
    private readonly TestFrameSource _Inner;
    private readonly int _HoleFrameIndex;

    internal HoleRandomAccessSource(TestFrameSource inner, int holeFrameIndex)
    {
        _Inner = inner;
        _HoleFrameIndex = holeFrameIndex;
    }

    public string UiName => _Inner.UiName;

    public string? Description => _Inner.Description;

    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    public bool IsRunning => _Inner.IsRunning;

    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) =>
        _Inner.Start(sourceId, registry);

    public Frame? NextFrame(CancellationToken cancellationToken = default) =>
        _Inner.NextFrame(cancellationToken);

    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        if (id.Value == _HoleFrameIndex)
        {
            return null;
        }

        return _Inner.FrameById(id, cancellationToken);
    }

    public void Dispose() => _Inner.Dispose();
}

/// <summary>
/// Random-access source that runs <see cref="OnFrameById"/> before every
/// <see cref="IRandomAccessFrameSource.FrameById"/> call, on the calling thread. A test uses it to pause the
/// thread that re-reads a frame (a value-cache fill or a restart re-parse) at a known point, without sleeps.
/// </summary>
internal sealed class HookRandomAccessSource : IRandomAccessFrameSource
{
    private readonly TestFrameSource _Inner;
    // Set by the test thread, read by session threads.
    private volatile Action<FrameId>? _OnFrameById;

    internal HookRandomAccessSource(TestFrameSource inner) => _Inner = inner;

    /// <summary>Called with the requested id before the frame is read. Null runs nothing.</summary>
    internal Action<FrameId>? OnFrameById
    {
        get => _OnFrameById;
        set => _OnFrameById = value;
    }

    public string UiName => _Inner.UiName;

    public string? Description => _Inner.Description;

    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    public bool IsRunning => _Inner.IsRunning;

    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) =>
        _Inner.Start(sourceId, registry);

    public Frame? NextFrame(CancellationToken cancellationToken = default) =>
        _Inner.NextFrame(cancellationToken);

    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        _OnFrameById?.Invoke(id);
        return _Inner.FrameById(id, cancellationToken);
    }

    public void Dispose() => _Inner.Dispose();
}

/// <summary>
/// One-frame source whose <see cref="NextFrame"/> ignores cancellation: it signals <c>entered</c>, blocks until
/// <c>release</c> is set, and then returns a frame. Models a capture driver that returns late, after
/// <see cref="Session.Shutdown"/> with a timeout has already finished.
/// </summary>
internal sealed class StubbornFrameSource : IRandomAccessFrameSource
{
    private readonly ManualResetEventSlim _Release;
    private readonly ManualResetEventSlim _Entered;
    private FrameInterfaceRegistry? _Registry;
    private FrameInterfaceId _InterfaceId;
    private Frame _Produced;
    private int _Yielded;
    // Store the frame first, then publish this flag (release) for FrameById on other threads.
    private int _Published;

    internal StubbornFrameSource(ManualResetEventSlim release, ManualResetEventSlim entered)
    {
        _Release = release;
        _Entered = entered;
    }

    public string UiName => "Stubborn";

    public string? Description => null;

    public int? EstimatedFrameCount => 1;

    public bool IsRunning => _Registry is not null;

    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _Registry = registry;
        _InterfaceId = registry.Register(sourceId, "test_eth", null, LinkType.Ethernet);
    }

    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _Yielded, 1) != 0)
        {
            return null;
        }

        // Deliberately not cancellable: the frame arrives after the session stopped waiting for it.
        _Entered.Set();
        _Release.Wait(CancellationToken.None);
        _Produced = Frame.Create(
            new FrameId(0),
            Timestamp.FromNanos(0),
            TestHarness.GenerateUdpFrame(64),
            LinkType.Ethernet,
            _InterfaceId,
            _Registry!).Value;
        Volatile.Write(ref _Published, 1);
        return _Produced;
    }

    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) =>
        id.Value == 0 && Volatile.Read(ref _Published) != 0
            ? _Produced
            : null;

    /// <summary>Keeps the registry: the session disposes sources at shutdown while this one may still be inside <see cref="NextFrame"/>.</summary>
    public void Dispose()
    {
    }
}
