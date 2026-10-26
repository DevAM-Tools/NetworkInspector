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
    private static long _RunLoop(
        List<IFrameSource> sources,
        CapturingFrameExporter exporter,
        Stack? stack,
        IFilter? filter,
        out int filesWritten,
        out bool listenerStopped)
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
            TextWriter.Null,
            CancellationToken.None,
            out filesWritten,
            out listenerStopped);
    }

    /// <summary>Runs the convert loop in tolerant mode without a filter and returns the number of frames written.</summary>
    private static long _RunTolerantLoop(List<IFrameSource> sources, CapturingFrameExporter exporter, TextWriter error) =>
        ConvertCommand.RunConvertLoop(sources, _ => exporter, new SplitOutputManager("capture.pcapng", maxSize: 0, maxCount: 0),
            stack: null, filter: null, maxFrames: 0, progressInterval: 0, tolerant: true, error, CancellationToken.None, out _, out _);

    // ── Tests ─────────────────────────────────────────────────────────────────────────

    /// <summary>Without a filter the loop copies every frame and finalizes exactly one output.</summary>
    [Test]
    public async Task RunConvertLoop_NoFilter_CopiesEveryFrame()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        long written = _RunLoop(sources, exporter, stack: null, filter: null, out int filesWritten, out _);

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

        long written = _RunLoop(sources, exporter, stack, _Compile(stack, "udp"), out int filesWritten, out _);

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

        long written = _RunLoop(sources, exporter, stack, _Compile(stack, "tcp"), out int filesWritten, out _);

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
        ParseOptions options = new();
        PacketId firstId = new(0);
        if (!Packet.TryParse(firstId, stack, poisonFrame, in options, out _, out ParseFailure firstFailure))
        {
            throw new InvalidOperationException(firstFailure.ToString());
        }

        PacketId secondId = new(1);
        if (!Packet.TryParse(secondId, stack, poisonFrame, in options, out Packet? second, out ParseFailure secondFailure)
            || second is null)
        {
            throw new InvalidOperationException(secondFailure.ToString());
        }

        _ = filter.TryIsMatch(second, out _, out _);

        InvalidOperationException? caught = null;
        try
        {
            _ = _RunLoop(sources, exporter, stack, filter, out _, out _);
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
                TextWriter.Null,
                CancellationToken.None,
                out _,
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

    /// <summary>
    /// A listener that returns false stopped taking frames: the loop must end after that frame instead of reading on and dropping
    /// the rest, finish the open output, and tell the caller that the input was not converted completely.
    /// </summary>
    [Test]
    public async Task RunConvertLoopEndsWhenTheListenerStopsAndReportsIt()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingFrameExporter exporter = new(stopAfter: 3);
        List<IFrameSource> sources = [source];

        long written = _RunLoop(sources, exporter, stack: null, filter: null, out int filesWritten, out bool listenerStopped);

        await Assert.That(written).IsEqualTo(3L);
        await Assert.That(exporter.FrameCount).IsEqualTo(3);
        await Assert.That(listenerStopped).IsTrue();
        await Assert.That(filesWritten).IsEqualTo(1);
        await Assert.That(exporter.Finished).IsTrue();
    }

    /// <summary>A run that reaches the end of its input is not a stopped listener.</summary>
    [Test]
    public async Task RunConvertLoopDoesNotReportAStoppedListenerForACompleteRun()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 4, registry);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];

        _ = _RunLoop(sources, exporter, stack: null, filter: null, out _, out bool listenerStopped);

        await Assert.That(listenerStopped).IsFalse();
    }

    /// <summary>
    /// A listener that returns false because the run was cancelled makes the run a cancelled one: the unfinished output is
    /// discarded and not finished, as for every other cancelled conversion.
    /// </summary>
    [Test]
    public async Task RunConvertLoopTreatsAListenerThatStopsOnCancellationAsCancelled()
    {
        using CliSandbox sandbox = new();
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        using CancellationTokenSource cancel = new();
        CapturingFrameExporter exporter = new(stopAfter: 2, cancelOnStop: cancel);
        List<IFrameSource> sources = [source];

        // The output path is private to the test: the cleanup of a cancelled run deletes whatever is at the path.
        await Assert.That(() => ConvertCommand.RunConvertLoop(sources, _ => exporter,
            new SplitOutputManager(sandbox.Combine("capture.pcapng"), 0, 0),
            stack: null, filter: null, maxFrames: 0, progressInterval: 0, tolerant: false, TextWriter.Null, cancel.Token, out _, out _))
            .Throws<OperationCanceledException>();

        await Assert.That(exporter.Finished).IsFalse();
    }

    /// <summary>
    /// A source that fails once and then reads again loses only the failed read: the other frames are converted, and the
    /// warning names the source.
    /// </summary>
    [Test]
    public async Task RunConvertLoopTolerantSkipsOneFailedReadAndKeepsTheSource()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource random = _CreateAndStartSource(count: 5, registry);
        using FailingFrameSource source = new(random, failAtRead: 2, failCount: 1);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [source];
        StringWriter error = new();

        long written = _RunTolerantLoop(sources, exporter, error);

        await Assert.That(written).IsEqualTo(5L);
        await Assert.That(error.ToString()).Contains("Skipping frame from failing-source: read failed");
        await Assert.That(error.ToString()).DoesNotContain("Giving up");
    }

    /// <summary>
    /// A source that fails on every read must not keep the loop alive: it is dropped when it fails again before it delivered a
    /// frame, with a warning that names it, and the other sources are converted completely.
    /// </summary>
    [Test]
    public async Task RunConvertLoopTolerantDropsASourceThatFailsOnEveryRead()
    {
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource random = _CreateAndStartSource(count: 5, registry);
        using FailingFrameSource broken = new(inner: null, failAtRead: 1, failCount: int.MaxValue);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [broken, random];
        StringWriter error = new();

        long written = _RunTolerantLoop(sources, exporter, error);

        await Assert.That(written).IsEqualTo(5L);
        await Assert.That(broken.Reads).IsEqualTo(2);
        await Assert.That(error.ToString()).Contains("Skipping frame from failing-source: read failed");
        await Assert.That(error.ToString()).Contains("Giving up on failing-source: it failed again without delivering a frame: read failed");
    }

    /// <summary>The same rule when the failing source is the only one: the run ends instead of looping.</summary>
    [Test]
    public async Task RunConvertLoopTolerantEndsWhenTheOnlySourceFailsOnEveryRead()
    {
        using FailingFrameSource broken = new(inner: null, failAtRead: 1, failCount: int.MaxValue);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [broken];

        long written = _RunTolerantLoop(sources, exporter, new StringWriter());

        await Assert.That(written).IsEqualTo(0L);
        await Assert.That(broken.Reads).IsEqualTo(2);
    }

    /// <summary>Without tolerance the first failed read still aborts the conversion.</summary>
    [Test]
    public async Task RunConvertLoopStrictAbortsOnTheFirstFailedRead()
    {
        using FailingFrameSource broken = new(inner: null, failAtRead: 1, failCount: 1);
        CapturingFrameExporter exporter = new();
        List<IFrameSource> sources = [broken];

        await Assert.That(() => _RunLoop(sources, exporter, stack: null, filter: null, out _, out _)).Throws<IOException>();
    }

    // ── Supporting types ──────────────────────────────────────────────────────────────

    /// <summary>Counts the frames handed to it and records whether the loop finalized it.</summary>
    /// <param name="stopAfter">Return <see langword="false"/> from <see cref="OnFrame"/> at this frame (0 = never).</param>
    /// <param name="cancelOnStop">Cancelled when the listener stops, to model a listener that stops because of a cancellation.</param>
    private sealed class CapturingFrameExporter(int stopAfter = 0, CancellationTokenSource? cancelOnStop = null) : IFrameListener
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
            if (stopAfter == 0 || FrameCount < stopAfter)
            {
                return true;
            }

            cancelOnStop?.Cancel();
            return false;
        }

        /// <inheritdoc/>
        public void OnFinish() => Finished = true;
    }
}
