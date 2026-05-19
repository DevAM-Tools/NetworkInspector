// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
internal sealed class RandomSourceParseScenario : IProfilingScenario
{
    /// <summary>Number of frames generated and parsed per <see cref="Run"/> call.</summary>
    private const int FrameCount = 10_000;

    /// <summary>Fixed PRNG seed so results are reproducible across iterations.</summary>
    private const ulong Seed = 42;

    /// <summary>Minimum frame size in bytes.</summary>
    private const int MinFrameSize = 128;

    /// <summary>Maximum frame size in bytes.</summary>
    private const int MaxFrameSize = 1024;

    private Stack? _Stack;

    /// <inheritdoc/>
    public string Name => "random-source-parse";

    /// <inheritdoc/>
    public string Description =>
        $"Direct parse: RandomFrameSource(UdpIPv6) -> ParseFrame -> MaterializeAll, " +
        $"{FrameCount:N0} frames per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

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
            FrameCount = FrameCount,
            Seed = Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = MinFrameSize,
            MaxFrameSize = MaxFrameSize,
        });

        // Register the source to obtain a valid FrameSourceId, then start it.
        // We use the stack's registry directly — no session involved.
        FrameSourceId sourceId = stack.FrameInterfaceRegistry.RegisterSource(source);
        source.Start(sourceId, stack.FrameInterfaceRegistry);

        int packetId = 0;
        while (source.NextFrame() is { } frame)
        {
            Packet packet = Packet.ParseFrame(packetId++, stack, frame);
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
