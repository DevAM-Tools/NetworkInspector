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

    // Stores produced frames by FrameId.Value for random-access retrieval.
    // Written by NextFrame (single-threaded), read by FrameById (any thread).
    private readonly Dictionary<int, Frame> _ProducedFrames = [];

    /// <summary>
    /// Creates a test source that will yield the given raw frames in order.
    /// </summary>
    internal TestFrameSource(byte[][] rawFrames, LinkType linkType = LinkType.Ethernet)
    {
        _RawFrames = rawFrames;
        _LinkType = linkType;
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

        // Store for random-access retrieval during reparse.
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
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Dispose failed for test.");
        }

        _Registry = null;
    }
}
