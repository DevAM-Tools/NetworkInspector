// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Tests for <see cref="ConvertCommand.RunConvertLoop"/>, the frame-level copy loop behind
/// <c>ni convert</c>. Covers the unfiltered fast path, filtered conversion, and the two abort
/// conditions that must not leave a half-written output behind.
/// </summary>
/// <remarks>
/// Each test builds its own <see cref="FrameInterfaceRegistry"/> and <see cref="Stack"/> so tests
/// stay independent and can run in parallel.
/// </remarks>
internal sealed class ConvertCommandLoopTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>Builds a <see cref="Stack"/> wired to <paramref name="registry"/>; caller disposes.</summary>
    private static Stack _BuildStack(FrameInterfaceRegistry registry)
    {
        using SettingsManager settingsManager = new();
        StackBuilder stackBuilder = new(settingsManager, registry);
        stackBuilder.RegisterStandardProtocols();
        return stackBuilder.Build();
    }

    /// <summary>Creates a started <see cref="RandomFrameSource"/> registered with <paramref name="registry"/>.</summary>
    private static RandomFrameSource _CreateAndStartSource(int count, FrameInterfaceRegistry registry)
    {
        RandomFrameSource source = new(count: count, seed: 42, mode: RandomFrameMode.UdpIPv4);
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return source;
    }

    /// <summary>Compiles an expression, failing the test when it does not compile.</summary>
    private static PacketFilter _Compile(Stack stack, string expression)
    {
        FilterResult<PacketFilter> result = PacketFilter.Compile(expression, stack);
        if (!result.TryGetValue(out PacketFilter? filter))
        {
            throw new InvalidOperationException($"Expected '{expression}' to compile but got {result.Error}.");
        }

        return filter;
    }

    /// <summary>Runs the convert loop with a single non-splitting capturing exporter.</summary>
    private static int _RunLoop(
        List<IFrameSource> sources,
        CapturingFrameExporter exporter,
        Stack? stack,
        IFilter? filter,
        out int filesWritten)
    {
        SplitOutputManager split = new("capture.pcapng", maxSize: 0, maxCount: 0);
        return ConvertCommand.RunConvertLoop(
            sources,
            _ => exporter,
            split,
            stack,
            filter,
            maxFrames: 0,
            progressInterval: 0,
            tolerant: false,
            CancellationToken.None,
            out filesWritten);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────

    /// <summary>Without a filter the loop copies every frame and finalizes exactly one output.</summary>
    [Test]
    public async Task RunConvertLoop_NoFilter_CopiesEveryFrame()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        int written = _RunLoop(sources, exporter, stack: null, filter: null, out int filesWritten);

        await Assert.That(written).IsEqualTo(10);
        await Assert.That(exporter.FrameCount).IsEqualTo(10);
        await Assert.That(filesWritten).IsEqualTo(1);
        await Assert.That(exporter.Finished).IsTrue();
    }

    /// <summary>A filter that accepts every frame must not change the outcome of the loop.</summary>
    [Test]
    public async Task RunConvertLoop_FilterMatchesEverything_CopiesEveryFrame()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        int written = _RunLoop(sources, exporter, stack, _Compile(stack, "udp"), out int filesWritten);

        await Assert.That(written).IsEqualTo(10);
        await Assert.That(exporter.FrameCount).IsEqualTo(10);
        await Assert.That(filesWritten).IsEqualTo(1);
    }

    /// <summary>
    /// A filter that matches nothing must leave no output behind at all: the exporter is only
    /// created once a frame has actually been accepted.
    /// </summary>
    [Test]
    public async Task RunConvertLoop_FilterMatchesNothing_WritesNoOutput()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        int written = _RunLoop(sources, exporter, stack, _Compile(stack, "tcp"), out int filesWritten);

        await Assert.That(written).IsEqualTo(0);
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
        await Assert.That(filesWritten).IsEqualTo(0);
        await Assert.That(exporter.Finished).IsFalse();
    }

    /// <summary>
    /// A filter that cannot produce a verdict must abort the conversion rather than write a file
    /// whose contents silently depend on where evaluation stopped.
    /// </summary>
    [Test]
    public async Task RunConvertLoop_FilterEvaluationFails_Throws()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource poisonSource = _CreateAndStartSource(count: 1, registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        // Dense first parses (0 then 1); evaluating 1 then 0 poisons a stateful filter.
        PacketFilter filter = _Compile(stack, "flank(ip.ttl, changed, within: 1s)");
        Frame poisonFrame = poisonSource.NextFrame()!.Value;
        _ = Packet.ParseFrame(new PacketId(0), stack, poisonFrame);
        _ = filter.TryIsMatch(Packet.ParseFrame(new PacketId(1), stack, poisonFrame), out _, out _);

        InvalidOperationException? caught = null;
        try
        {
            _ = _RunLoop(sources, exporter, stack, filter, out _);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("Filter evaluation failed");
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
    }

    /// <summary>
    /// Size-based splitting needs a live byte estimate from the exporter. An exporter that cannot
    /// report one must fail loudly instead of writing one unbounded file.
    /// </summary>
    [Test]
    public async Task RunConvertLoop_SizeSplitWithoutByteProgress_Throws()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 4, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];
        SplitOutputManager split = new("capture.pcapng", maxSize: 1024, maxCount: 0);

        InvalidOperationException? caught = null;
        try
        {
            _ = ConvertCommand.RunConvertLoop(
                sources,
                _ => exporter,
                split,
                stack: null,
                filter: null,
                maxFrames: 0,
                progressInterval: 0,
                tolerant: false,
                CancellationToken.None,
                out _);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("--split-size");
        await Assert.That(exporter.FrameCount).IsEqualTo(0);
    }

    // ── Supporting types ──────────────────────────────────────────────────────────────

    /// <summary>Counts the frames handed to it and records whether the loop finalized it.</summary>
    private sealed class CapturingFrameExporter : IFrameListener
    {
        /// <inheritdoc/>
        public string UiName => "capturing-frames";

        /// <inheritdoc/>
        public string? Description => null;

        /// <summary>Gets the number of frames received via <see cref="OnFrame"/>.</summary>
        public int FrameCount { get; private set; }

        /// <summary>Gets a value indicating whether <see cref="OnFinish"/> was called.</summary>
        public bool Finished { get; private set; }

        /// <inheritdoc/>
        public bool OnFrame(Frame frame)
        {
            FrameCount++;
            return true;
        }

        /// <inheritdoc/>
        public void OnFinish() => Finished = true;
    }
}
