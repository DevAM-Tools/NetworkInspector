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

        long result = ExportCommand.RunExportLoop(
            sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(10L);
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

        ExportCommand.RunExportLoop(
            sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

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

        long result = ExportCommand.RunExportLoop(
            sources, exporter, stack, 5, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(5L);
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

        long result = ExportCommand.RunExportLoop(
            sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(0L);
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

        long result = ExportCommand.RunExportLoop(
            sources, exporter, stack, 0, 0, false, ref counter, CancellationToken.None);

        await Assert.That(result).IsEqualTo(3L);
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

        long result = ExportCommand.RunExportLoop(
            sources, exporter, stack, 0, 0, false, ref counter, cancelled);

        await Assert.That(result).IsEqualTo(0L);
        await Assert.That(exporter.CapturedIds.Count).IsEqualTo(0);
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
