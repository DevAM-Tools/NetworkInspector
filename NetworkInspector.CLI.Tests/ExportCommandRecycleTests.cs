// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Tests for <see cref="ExportCommand.RunExportLoop"/> verifying that the packet-recycling
/// path produces the same observable results as a non-recycled run (same packet count, sequential
/// IDs) and that all boundary conditions are handled correctly.
/// </summary>
/// <remarks>
/// Each test builds its own <see cref="FrameInterfaceRegistry"/> and <see cref="Stack"/> so that
/// the shared-registry invariant required by <see cref="Packet.TryParseFrame"/> is satisfied
/// without cross-test interference, and so tests can run safely in parallel.
/// </remarks>
internal sealed class ExportCommandRecycleTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="Stack"/> wired to <paramref name="registry"/>.
    /// The caller owns the returned <see cref="Stack"/> and must dispose it.
    /// </summary>
    private static Stack _BuildStack(FrameInterfaceRegistry registry)
    {
        using SettingsManager settingsManager = new();
        StackBuilder stackBuilder = new(settingsManager, registry);
        stackBuilder.RegisterStandardProtocols();
        return stackBuilder.Build();
    }

    /// <summary>
    /// Creates a <see cref="RandomFrameSource"/> with the given frame count, registers it with
    /// <paramref name="registry"/>, and calls <see cref="IFrameSource.Start"/> so it is ready for
    /// <see cref="IFrameSource.NextFrame"/> calls.
    /// </summary>
    private static RandomFrameSource _CreateAndStartSource(int count, FrameInterfaceRegistry registry)
    {
        RandomFrameSource source = new(count: count, seed: 42, mode: RandomFrameMode.UdpIPv4);
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return source;
    }

    /// <summary>Runs the export loop with a single non-splitting capturing exporter.</summary>
    private static int _RunLoop(
        List<IFrameSource> sources,
        CapturingExporter exporter,
        Stack stack,
        int maxPackets,
        int progressInterval,
        bool tolerant,
        ref int counter,
        CancellationToken cancellationToken)
    {
        SplitOutputManager split = new("capture.json", maxSize: 0, maxCount: 0);
        return ExportCommand.RunExportLoop(
            sources,
            _ => exporter,
            split,
            stack,
            filter: null,
            maxPackets,
            progressInterval,
            tolerant,
            ref counter,
            cancellationToken,
            out _);
    }

    /// <summary>Runs the export loop with a filter and a single non-splitting capturing exporter.</summary>
    private static int _RunFilteredLoop(
        List<IFrameSource> sources,
        CapturingExporter exporter,
        Stack stack,
        IFilter filter,
        ref int counter,
        out int outputsWritten)
    {
        SplitOutputManager split = new("capture.json", maxSize: 0, maxCount: 0);
        return ExportCommand.RunExportLoop(
            sources,
            _ => exporter,
            split,
            stack,
            filter,
            maxPackets: 0,
            progressInterval: 0,
            tolerant: false,
            ref counter,
            CancellationToken.None,
            out outputsWritten);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ten frames, no packet limit — the return value and captured-ID count must both equal 10.
    /// Exercises the recycling path for all nine frames after the first.
    /// </summary>
    [Test]
    public async Task RunExportLoop_TenFrames_ReturnsPacketCount()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunLoop(sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(10);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(10);
    }

    /// <summary>
    /// Ten frames, no packet limit — packet IDs must be sequential starting at 1.
    /// Verifies that recycling does not disturb the monotonically increasing ID assignment.
    /// </summary>
    [Test]
    public async Task RunExportLoop_TenFrames_PacketIdsAreSequential()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        _RunLoop(sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(10);
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(exporter.CapturedIds[i]).IsEqualTo(i + 1).Because($"packet at index {i} must have ID {i + 1}");
        }
    }

    /// <summary>
    /// Ten frames, maxPackets=5 — the loop must stop after exporting five packets even though
    /// more frames remain in the source.
    /// </summary>
    [Test]
    public async Task RunExportLoop_MaxPackets_StopsExportEarly()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunLoop(sources, exporter, stack, 5, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(5);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(5);
    }

    /// <summary>
    /// Empty source — the loop must exit immediately without touching the parse path.
    /// Verifies the <c>recyclePacket == null</c> guard is never triggered and the return value is 0.
    /// </summary>
    [Test]
    public async Task RunExportLoop_EmptySource_ReturnsZero()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using EmptyFrameSource source = new();
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunLoop(sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Exporter returns <see langword="false"/> after the third packet — the loop must exit after
    /// emitting three packets even though more frames remain. Verifies the
    /// <c>recyclePacket = packet</c> assignment executes before the break.
    /// </summary>
    [Test]
    public async Task RunExportLoop_ExporterSignalsStop_LoopExitsEarly()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new(stopAfter: 3);
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunLoop(sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(3);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Pre-cancelled <see cref="CancellationToken"/> — the while-condition check prevents the
    /// loop body from executing at all. Return value must be 0.
    /// </summary>
    [Test]
    public async Task RunExportLoop_PreCancelledToken_ReturnsZeroPackets()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];
        CancellationToken cancelled = new(true);

        int result = _RunLoop(sources, exporter, stack, 0, 0, false, ref counter, cancelled);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A filter that accepts every frame the random source produces must not change the outcome
    /// of the loop.
    /// </summary>
    [Test]
    public async Task RunExportLoop_FilterMatchesEverything_ExportsAllPackets()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunFilteredLoop(sources, exporter, stack, _Compile(stack, "udp"), ref counter, out int outputs);

        await Assert.That(result).IsEqualTo(10);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(10);
        await Assert.That(outputs).IsEqualTo(1);
    }

    /// <summary>
    /// A filter that matches nothing must leave no output behind at all: the exporter is only
    /// created once a packet has actually been accepted.
    /// </summary>
    [Test]
    public async Task RunExportLoop_FilterMatchesNothing_WritesNoOutput()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        int result = _RunFilteredLoop(sources, exporter, stack, _Compile(stack, "tcp"), ref counter, out int outputs);

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
        await Assert.That(outputs).IsEqualTo(0);
    }

    /// <summary>
    /// A filter that cannot produce a verdict must abort the export rather than write a file whose
    /// contents silently depend on where evaluation stopped.
    /// </summary>
    [Test]
    public async Task RunExportLoop_FilterEvaluationFails_Throws()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource poisonSource = _CreateAndStartSource(count: 1, registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 10, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];

        // Evaluating a high packet id first makes the stateful filter reject the loop's ids as
        // out of order, which is the documented poisoning path.
        PacketFilter filter = _Compile(stack, "flank(ip.ttl, changed, within: 1s)");
        Frame frame = poisonSource.NextFrame()!.Value;
        _ = filter.TryIsMatch(Packet.ParseFrame(new PacketId(5000), stack, frame), out _, out _);

        bool aborted = false;
        string message = string.Empty;
        try
        {
            _ = _RunFilteredLoop(sources, exporter, stack, filter, ref counter, out _);
        }
        catch (InvalidOperationException exception)
        {
            aborted = true;
            message = exception.Message;
        }

        await Assert.That(aborted).IsTrue();
        await Assert.That(message).Contains("Filter evaluation failed");
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Size-based splitting needs a live byte estimate from the exporter. An exporter that cannot
    /// report one must fail loudly instead of writing one unbounded output.
    /// </summary>
    [Test]
    public async Task RunExportLoop_SizeSplitWithoutByteProgress_Throws()
    {
        FrameInterfaceRegistry registry = new();
        using Stack stack = _BuildStack(registry);
        using RandomFrameSource source = _CreateAndStartSource(count: 4, registry);
        CapturingExporter exporter = new();
        int counter = 0;
        List<IFrameSource> sources = [source];
        SplitOutputManager split = new("capture.json", maxSize: 1024, maxCount: 0);

        InvalidOperationException? caught = null;
        try
        {
            _ = ExportCommand.RunExportLoop(
                sources,
                _ => exporter,
                split,
                stack,
                filter: null,
                maxPackets: 0,
                progressInterval: 0,
                tolerant: false,
                ref counter,
                CancellationToken.None,
                out _);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("--split-size");
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
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

    // ── Supporting types ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures packet IDs in the order they are received via <see cref="OnPacket"/>.
    /// Optionally signals stop after a configurable number of packets to exercise the early-exit path.
    /// </summary>
    /// <param name="stopAfter">
    /// Return <see langword="false"/> from <see cref="OnPacket"/> after this many packets
    /// (0 = never stop early).
    /// </param>
    private sealed class CapturingExporter(int stopAfter = 0) : IPacketListener
    {
        /// <inheritdoc/>
        public string UiName => "capturing";

        /// <inheritdoc/>
        public string? Description => null;

        /// <summary>Gets the packet ID values received via <see cref="OnPacket"/>, in call order.</summary>
        public List<int> CapturedIds { get; } = [];

        /// <inheritdoc/>
        public bool OnPacket(Packet packet)
        {
            CapturedIds.Add(packet.Id.Value);
            return stopAfter == 0 || CapturedIds.Count < stopAfter;
        }

        /// <inheritdoc/>
        public void OnFinish() { }
    }

    /// <summary>
    /// A frame source that is immediately exhausted: <see cref="NextFrame"/> always returns
    /// <see langword="null"/>. Used to verify the zero-frame boundary path in
    /// <see cref="ExportCommand.RunExportLoop"/>.
    /// </summary>
    private sealed class EmptyFrameSource : IFrameSource
    {
        /// <inheritdoc/>
        public string UiName => "empty";

        /// <inheritdoc/>
        public string? Description => null;

        /// <inheritdoc/>
        public int? EstimatedFrameCount => 0;

        /// <inheritdoc/>
        public bool IsRunning => false;

        /// <inheritdoc/>
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }

        /// <inheritdoc/>
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
