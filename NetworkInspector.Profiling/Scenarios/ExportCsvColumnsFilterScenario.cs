// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// The CSV columns scenario behind a tree filter that keeps one packet in four (<see cref="SampleFrames.ExportMixFilter"/>).
/// <para>
/// <b>Why:</b> the rework lets a filter run without a field tree. This scenario is today's reference for filter plus export:
/// every frame is parsed with a tree, the filter walks it, and only the matching 25 % reach the exporter.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ExportCsvColumnsFilterScenario : ExportCsvColumnsScenario
{
    /// <inheritdoc/>
    public override string Name => "export-csv-columns-filter";

    /// <inheritdoc/>
    public override string Description =>
        Invariant($"Parse {FrameCount:N0} export-mix frames, keep 25 % with \"{SampleFrames.ExportMixFilter}\", write CSV with field columns.");

    /// <inheritdoc/>
    protected override string? FilterExpression => SampleFrames.ExportMixFilter;
}
