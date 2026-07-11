// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that reads a pre-generated PCAPNG file by iterating all frames
/// via <see cref="PcapSource"/>.
///
/// <para>
/// <b>Hot path:</b> <see cref="PcapSource.NextFrame"/> — block parsing, section/interface
/// tracking, and frame materialisation from the PCAPNG binary format.
/// The sample file is generated once in <see cref="IProfilingScenario.Setup"/>.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ReadPcapngScenario : ReadScenarioBase
{
    private const int _Count = 50_000;

    /// <inheritdoc/>
    protected override int FrameCount => _Count;

    /// <inheritdoc/>
    protected override string CreateSampleFile(Frame[] frames)
        => SampleFileHelper.CreatePcapngFile(frames);

    /// <inheritdoc/>
    public override string Name => "read-pcapng";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant($"Read {_Count:N0} frames from a PCAPNG file per iteration.");

    /// <inheritdoc/>
    protected override void RunIteration(string filePath)
    {
        using PcapSource source = PcapSource.Open(filePath);

        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        while (source.NextFrame() is not null)
        {
            // Consume frames — measure read/parse throughput only.
        }
    }
}
