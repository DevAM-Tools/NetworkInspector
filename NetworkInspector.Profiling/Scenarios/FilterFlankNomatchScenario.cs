// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Same flank expression as <see cref="FilterFlankScenario"/>, but the synthetic stream never
/// crosses the threshold (all source ports stay below 100). Measures stateful evaluation cost
/// when the flank never fires. The presence index still cannot prune a pure flank.
/// </summary>
internal sealed class FilterFlankNomatchScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-flank-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Flank no-match \"{Expression}\" (ports stay &lt; 100; full stateful eval).");

    /// <inheritdoc/>
    protected override string Expression =>
        "flank(udp.srcport, from: < 100, to: >= 200, within: 50packets)";

    /// <inheritdoc/>
    protected override bool IsStateful => true;

    /// <inheritdoc/>
    protected override Frame[] CreateFrames(int count, Stack stack) =>
        FrameHelper.CreateFlankUdpFrames(
            count,
            stack,
            FilterFlankScenario.SpikePeriod,
            enableSpikes: false);
}
