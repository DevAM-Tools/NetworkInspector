// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that uses a <see cref="RandomFrameSource"/> (UdpIPv6) to directly
/// parse and materialise every frame without involving a session pipeline.
///
/// <para>
/// <b>Hot path:</b> RandomFrameSource.NextFrame -> Packet.ParseFrame -> MaterializeAll.
/// Single-threaded, no session or listener overhead.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class RandomSourceParseScenario : IProfilingScenario
{
    /// <summary>Number of frames generated and parsed per <see cref="Run"/> call.</summary>
    private const int _FrameCount = 10_000;

    /// <summary>Fixed PRNG seed so results are reproducible across iterations.</summary>
    private const ulong _Seed = 42;

    /// <summary>Minimum frame size in bytes.</summary>
    private const int _MinFrameSize = 128;

    /// <summary>Maximum frame size in bytes.</summary>
    private const int _MaxFrameSize = 1024;

    private Stack? _Stack;

    /// <inheritdoc/>
    public string Name => "random-source-parse";

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"Direct parse: RandomFrameSource(UdpIPv6) -> ParseFrame -> MaterializeAll, {_FrameCount:N0} frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;

        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = _FrameCount,
            Seed = _Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = _MinFrameSize,
            MaxFrameSize = _MaxFrameSize,
        });

        // Register the source to obtain a valid FrameSourceId, then start it.
        // We use the stack's registry directly — no session involved.
        FrameSourceId sourceId = stack.FrameInterfaceRegistry.RegisterSource(source);
        source.Start(sourceId, stack.FrameInterfaceRegistry);

        int packetId = 0;
        Frame? next;
        while ((next = source.NextFrame()) is not null)
        {
            Frame frame = next.Value;
            Packet packet = Packet.ParseFrame(new PacketId(packetId++), stack, frame);
            packet.MaterializeAll();
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
    }
}
