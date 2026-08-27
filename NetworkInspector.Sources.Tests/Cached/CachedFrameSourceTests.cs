// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Cached;

/// <summary>
/// Tests for <see cref="CachedFrameSource"/> — lock-free chunked caching decorator.
/// Verifies caching, random access via FrameById, disposal delegation, and concurrent reads.
/// </summary>
internal sealed class CachedFrameSourceTests
{
    /// <summary>Creates a CachedFrameSource wrapping a RandomFrameSource with a known seed.</summary>
    /// <remarks>
    /// The <see cref="CachedFrameSource"/> takes ownership of the inner source
    /// and disposes it when itself is disposed (CA2000 suppressed intentionally).
    /// </remarks>
    private static CachedFrameSource _CreateCached(int count, ulong seed = 42)
    {
#pragma warning disable CA2000 // CachedFrameSource takes ownership of inner
        SequentialOnlyFrameSource inner = new(new RandomFrameSource(count, seed));
#pragma warning restore CA2000
        return new CachedFrameSource(inner);
    }



    // ========================================================================
    // Basic caching
    // ========================================================================

    [Test]
    public async Task NextFrame_CachesFrames_FrameByIdReturns()
    {
        using CachedFrameSource source = _CreateCached(5);
        SourceTestFixture.InitializeAndStartSource(source);

        // Read all frames
        Frame[] frames = new Frame[5];
        for (int i = 0; i < 5; i++)
        {
            Frame? f = source.NextFrame();
            await Assert.That(f).IsNotNull();
            frames[i] = f!.Value;
        }

        // Verify all cached frames can be retrieved by ID
        for (int i = 0; i < 5; i++)
        {
            Frame? cached = source.FrameById(frames[i].Id);
            await Assert.That(cached).IsNotNull();
            await Assert.That(cached!.Value.Id).IsEqualTo(frames[i].Id);
            await Assert.That(cached.Value.Data.Span.SequenceEqual(frames[i].Data.Span)).IsTrue();
        }
    }

    [Test]
    public async Task FrameById_InvalidId_ReturnsNull()
    {
        using CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);
        source.NextFrame();

        // Out-of-range FrameId
        Frame? result = source.FrameById(new FrameId(999));
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FrameById_NotYetRead_ReturnsNull()
    {
        using CachedFrameSource source = _CreateCached(5);
        SourceTestFixture.InitializeAndStartSource(source);

        // Read only the first frame
        source.NextFrame();

        // Frame with ID 4 hasn't been read yet
        Frame? result = source.FrameById(new FrameId(4));
        await Assert.That(result).IsNull();
    }

    // ========================================================================
    // Delegation
    // ========================================================================

    [Test]
    public async Task UiName_DelegatedToInner()
    {
#pragma warning disable CA2000 // CachedFrameSource takes ownership of inner
        SequentialOnlyFrameSource inner = new(new RandomFrameSource(1, seed: 42, uiName: "TestInner"));
#pragma warning restore CA2000
        using CachedFrameSource source = new(inner);
        await Assert.That(source.UiName).IsEqualTo("TestInner");
    }

    [Test]
    public async Task EstimatedFrameCount_DelegatedToInner()
    {
        using CachedFrameSource source = _CreateCached(100);
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(100);
    }

    // ========================================================================
    // Disposal delegation
    // ========================================================================

    [Test]
    public async Task Dispose_DisposesInner()
    {
        CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);
        source.NextFrame();

        source.Dispose();

        // After dispose, the inner source is disposed — IsRunning becomes false
        await Assert.That(source.IsRunning).IsFalse();
    }

    // ========================================================================
    // Rejects sources that already support random access
    // ========================================================================

    [Test]
    public async Task Constructor_RejectsRandomAccessSource()
    {
        byte[] pcapData;
        using (Tests.Generators.PcapNgTestWriter writer = new())
        {
            pcapData = writer.Build();
        }

        using NetworkInspector.Sources.Pcapng.PcapSource inner =
            NetworkInspector.Sources.Pcapng.PcapSource.FromData(pcapData, "test.pcapng");

        await Assert.That(() => new CachedFrameSource(inner)).Throws<ArgumentException>();
    }

    // ========================================================================
    // Concurrent FrameById reads
    // ========================================================================

    [Test]
    public async Task ConcurrentFrameById_ReturnsCorrectFrames()
    {
        const int count = 100;
        using CachedFrameSource source = _CreateCached(count);
        SourceTestFixture.InitializeAndStartSource(source);

        // Read all frames first
        Frame[] frames = new Frame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = source.NextFrame()!.Value;
        }

        // Concurrent reads from multiple threads
        List<Task> tasks = [];
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    Frame? cached = source.FrameById(frames[i].Id);
                    if (cached is null)
                    {
                        throw new InvalidOperationException($"FrameById returned null for frame {i}");
                    }

                    if (!cached.Value.Data.Span.SequenceEqual(frames[i].Data.Span))
                    {
                        throw new InvalidOperationException($"Frame data mismatch at index {i}");
                    }
                }
            }));
        }

        // Should complete without exceptions
        await Assert.That(async () => await Task.WhenAll(tasks).ConfigureAwait(false)).ThrowsNothing();
    }

    // ========================================================================
    // Exhaustion
    // ========================================================================

    [Test]
    public async Task NextFrame_AfterExhaustion_ReturnsNull()
    {
        using CachedFrameSource source = _CreateCached(2);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.NextFrame()).IsNotNull();
        await Assert.That(source.NextFrame()).IsNotNull();
        await Assert.That(source.NextFrame()).IsNull();
        await Assert.That(source.NextFrame()).IsNull();
    }

    // ========================================================================
    // Lifecycle (C-01 / M-01 / M-02 regression guards)
    // ========================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);

        source.Dispose();

        // Second Dispose must be idempotent and not throw.
        await Assert.That(() => source.Dispose()).ThrowsNothing();
    }

    [Test]
    public async Task IsRunning_FalseBeforeStart()
    {
        using CachedFrameSource source = _CreateCached(1);
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task IsRunning_TrueAfterStart_FalseAfterDispose()
    {
        CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);

        await Assert.That(source.IsRunning).IsTrue();

        source.Dispose();

        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task NextFrame_AfterDispose_ThrowsObjectDisposedException()
    {
        CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);
        source.Dispose();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task FrameById_AfterDispose_ThrowsObjectDisposedException()
    {
        CachedFrameSource source = _CreateCached(1);
        SourceTestFixture.InitializeAndStartSource(source);

        Frame? f = source.NextFrame();
        await Assert.That(f).IsNotNull();

        source.Dispose();

        await Assert.That(() => source.FrameById(f!.Value.Id)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Start_NullRegistry_ThrowsArgumentNullException()
    {
#pragma warning disable CA2000 // CachedFrameSource takes ownership of inner
        SequentialOnlyFrameSource inner = new(new RandomFrameSource(1));
#pragma warning restore CA2000
        using CachedFrameSource source = new(inner);

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => source.Start(sourceId, null!)).Throws<ArgumentNullException>();
    }

    // ========================================================================
    // Inner-source exception propagation (E3 regression guard)
    // ========================================================================

    /// <summary>
    /// Forward-only facade so <see cref="CachedFrameSource"/> can wrap a generator that already
    /// implements <see cref="IRandomAccessFrameSource"/>.
    /// </summary>
    private sealed class SequentialOnlyFrameSource : IFrameSource
    {
        private readonly IFrameSource _Inner;

        public SequentialOnlyFrameSource(IFrameSource inner) => _Inner = inner;

        public string UiName => _Inner.UiName;

        public string? Description => _Inner.Description;

        public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

        public bool IsRunning => _Inner.IsRunning;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) =>
            _Inner.Start(sourceId, registry);

        public Frame? NextFrame(CancellationToken cancellationToken = default) =>
            _Inner.NextFrame(cancellationToken);

        public void Dispose() => _Inner.Dispose();
    }

    private sealed class ThrowingFrameSource : IFrameSource
    {
        /// <inheritdoc/>
        public string UiName => "ThrowingSource";

        /// <inheritdoc/>
        public string? Description => null;

        /// <inheritdoc/>
        public int? EstimatedFrameCount => null;

        /// <inheritdoc/>
        public bool IsRunning => true;

        /// <inheritdoc/>
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }

        /// <inheritdoc/>
        public Frame? NextFrame(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Simulated inner source failure.");

        /// <inheritdoc/>
        public void Dispose() { }
    }

    [Test]
    public async Task NextFrame_InnerThrows_ExceptionPropagates()
    {
#pragma warning disable CA2000 // CachedFrameSource takes ownership of inner
        ThrowingFrameSource inner = new();
#pragma warning restore CA2000
        using CachedFrameSource source = new(inner);
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        await Assert.That(() => source.NextFrame()).Throws<InvalidOperationException>();
    }
}
