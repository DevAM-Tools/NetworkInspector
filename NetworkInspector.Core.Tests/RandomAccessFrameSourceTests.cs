// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Covers the default implementation of <see cref="IRandomAccessFrameSource.FramesById"/>: a source that implements only
/// <see cref="IRandomAccessFrameSource.FrameById"/> answers a range read with the same frames, writes exactly the span it
/// gets, treats ids past the valid range as missing, and observes cancellation at least once per 64 ids.
/// </summary>
internal sealed class RandomAccessFrameSourceTests
{
    #region Constants

    private const int _SentinelId = 999_999;

    #endregion

    #region Default implementation

    [Test]
    [Arguments(0, 0)]
    [Arguments(3, 1)]
    [Arguments(0, 10)]
    [Arguments(2, 7)]
    [Arguments(-1, 4)]
    [Arguments(995, 10)]
    [Arguments(0, 200)]
    public async Task RangeReadFillsEveryEntryLikeFrameByIdAndLeavesTheRestUntouched(int first, int length)
    {
        // Arrange: frames at ids 0-999 except 5; the range is a slice of a larger buffer full of sentinels.
        FakeSource source = new(skip: 5);
        IRandomAccessFrameSource batch = source;
        Frame?[] buffer = _SentinelBuffer(length + 6);
        Span<Frame?> range = buffer.AsSpan(3, length);

        // Act
        int found = batch.FramesById(new FrameId(first), range);

        // Assert
        int expectedFound = 0;
        List<string> mismatches = [];
        for (int i = 0; i < length; i++)
        {
            int id = first + i;
            Frame? expected = id >= 0 ? source.FrameById(new FrameId(id)) : null;
            expectedFound += expected is null ? 0 : 1;
            if (_IdOf(buffer[3 + i]) != _IdOf(expected))
            {
                mismatches.Add(FormattableString.Invariant($"entry {i}"));
            }
        }

        await Assert.That(mismatches).IsEmpty();
        await Assert.That(found).IsEqualTo(expectedFound);
        foreach (int outside in new[] { 0, 1, 2, length + 3, length + 4, length + 5 })
        {
            await Assert.That(_IdOf(buffer[outside])).IsEqualTo(_SentinelId);
        }
    }

    [Test]
    public async Task RangePastTheLargestValidIdReadsNullEntriesWithoutThrowing()
    {
        // Arrange: the range starts at the largest valid id and runs four ids past it.
        FakeSource source = new(skip: -1);
        IRandomAccessFrameSource batch = source;
        Frame?[] frames = _SentinelBuffer(5);

        // Act
        int found = batch.FramesById(new FrameId(ArrayIndexIdRange.MaxValue), frames);

        // Assert: the fake source has no frame that high, and the ids past the range are never requested.
        await Assert.That(found).IsEqualTo(0);
        await Assert.That(frames.All(static frame => frame is null)).IsTrue();
        await Assert.That(source.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyRangeReadsNothing()
    {
        FakeSource source = new(skip: -1);
        IRandomAccessFrameSource batch = source;

        int found = batch.FramesById(new FrameId(3), Span<Frame?>.Empty);

        await Assert.That(found).IsEqualTo(0);
        await Assert.That(source.Reads).IsEqualTo(0);
    }

    #endregion

    #region Cancellation

    [Test]
    public async Task ACancelledTokenThrowsBeforeAnythingIsWritten()
    {
        FakeSource source = new(skip: -1);
        IRandomAccessFrameSource batch = source;
        Frame?[] frames = _SentinelBuffer(2);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException? thrown = null;
        try
        {
            _ = batch.FramesById(new FrameId(0), frames, cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            thrown = exception;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(_IdOf(frames[0])).IsEqualTo(_SentinelId);
        await Assert.That(source.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationDuringTheRangeStopsAtTheNextSixtyFourthId()
    {
        // The source cancels the token while it reads id 10 of 200; the default loop checks the token every 64 ids.
        using CancellationTokenSource cancellation = new();
        FakeSource source = new(skip: -1) { OnRead = id => { if (id == 10) { cancellation.Cancel(); } } };
        IRandomAccessFrameSource batch = source;
        Frame?[] frames = _SentinelBuffer(200);

        OperationCanceledException? thrown = null;
        try
        {
            _ = batch.FramesById(new FrameId(0), frames, cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            thrown = exception;
        }

        await Assert.That(thrown).IsNotNull();
        for (int i = 0; i <= 10; i++)
        {
            await Assert.That(_IdOf(frames[i])).IsEqualTo(i);
        }

        await Assert.That(source.Reads).IsLessThanOrEqualTo(64);
        await Assert.That(_IdOf(frames[64])).IsEqualTo(_SentinelId);
    }

    #endregion

    #region Helpers

    private static int _IdOf(Frame? frame) =>
        frame is { } value
            ? value.Id.Value
            : -1;

    private static Frame?[] _SentinelBuffer(int length)
    {
        Frame sentinel = _MakeFrame(_SentinelId);
        Frame?[] frames = new Frame?[length];
        Array.Fill(frames, sentinel);
        return frames;
    }

    private static Frame _MakeFrame(int id) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(0),
            new byte[] { 1, 2, 3, 4 },
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            new()).Value;

    /// <summary>A random-access source that implements only the single-frame read: frames for ids 0-999 except one.</summary>
    private sealed class FakeSource(int skip) : IRandomAccessFrameSource
    {
        private readonly Frame[] _Frames = [.. Enumerable.Range(0, 1_000).Select(_MakeFrame)];

        public int Reads { get; private set; }

        public Action<int>? OnRead { get; init; }

        public string UiName => "fake";

        public string? Description => null;

        public int? EstimatedFrameCount => null;

        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
        {
            Reads++;
            OnRead?.Invoke(id.Value);
            return id.Value >= 0 && id.Value < _Frames.Length && id.Value != skip
                ? _Frames[id.Value]
                : null;
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
