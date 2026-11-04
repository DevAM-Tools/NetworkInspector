// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Cached;

/// <summary>
/// The caching path of <see cref="CachedFrameSource"/> runs for every frame, so it must not allocate once the first chunk
/// exists: the chunk factories are cached static delegates, not capturing lambdas.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class CachedFrameSourceAllocationTests
{
    #region Helpers

    /// <summary>Replays frames that were built up front, so the source itself allocates nothing per call.</summary>
    private sealed class ReplayFrameSource(Frame[] frames) : IFrameSource
    {
        private int _Next;

        public string UiName => "replay";

        public string? Description => null;

        public int? EstimatedFrameCount => frames.Length;

        public bool IsRunning => true;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default)
        {
            if (_Next >= frames.Length)
            {
                return null;
            }

            return frames[_Next++];
        }

        public void Dispose()
        {
        }
    }

    private static Frame[] _BuildFrames(int count)
    {
        using RandomFrameSource random = new(count, seed: 7);
        SourceTestFixture.InitializeAndStartSource(random);
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = random.NextFrame()!.Value;
        }

        return frames;
    }

    #endregion

    [Test]
    public async Task CachingFramesOfAnAllocatedChunkAllocatesNothing()
    {
        const int count = 2_000;
        Frame[] frames = _BuildFrames(count);
        using CachedFrameSource cached = new(new ReplayFrameSource(frames));
        SourceTestFixture.InitializeAndStartSource(cached);

        // The first frame allocates the 16 384-slot chunk of frames and flags; every later frame of that chunk must not.
        _ = cached.NextFrame();
        _ = cached.NextFrame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2; i < count; i++)
        {
            _ = cached.NextFrame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(cached.FrameById(count - 1)).IsNotNull();
    }
}
