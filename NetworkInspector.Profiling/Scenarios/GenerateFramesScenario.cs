// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that measures raw frame generation throughput of the
/// <see cref="RandomFrameSource"/> in <see cref="RandomFrameMode.UdpIPv6"/> mode.
///
/// <para>
/// <b>Hot path:</b> <see cref="RandomFrameSource.NextFrame"/> loop including
/// SIMD-accelerated random data generation and header patching.
/// No stack or parsing is involved.
/// </para>
/// </summary>
internal sealed class GenerateFramesScenario : IProfilingScenario
{
    /// <summary>Number of frames generated per iteration.</summary>
    private const int FrameCount = 100_000;

    /// <inheritdoc/>
    public string Name => "generate-frames";

    /// <inheritdoc/>
    public string Description =>
        $"Generate {FrameCount:N0} random IPv6/UDP frames via RandomFrameSource per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        // No setup needed — the source is created fresh each iteration.
    }

    /// <inheritdoc/>
    public void Run()
    {
        // Create a fresh source per iteration to profile the full lifecycle.
        FrameInterfaceRegistry registry = new();

        using RandomFrameSource source = new(
            count: FrameCount,
            seed: 42,
            mode: RandomFrameMode.UdpIPv6);

        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        while (source.NextFrame() is not null)
        {
            // Consume frames without processing — measure generation throughput only.
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        // Nothing to clean up.
    }
}
