// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Flank expression gated by a missing protocol (<c>tcp &amp;&amp; flank(…)</c>).
/// Stateful programs never get a candidate bitmap (flanks must observe every packet), but the
/// JIT still short-circuits on <c>tcp</c> presence before updating flank state — cheaper than a
/// pure flank no-match on the same UDP-only workload.
/// </summary>
internal sealed class FilterFlankPruneScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-flank-prune";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Flank with tcp gate \"{Expression}\" on UDP-only traffic (eval short-circuit, not candidate prune).");

    /// <inheritdoc/>
    protected override string Expression =>
        "tcp && flank(udp.srcport, from: < 100, to: >= 200, within: 50packets)";

    /// <inheritdoc/>
    protected override bool IsStateful => true;

    /// <inheritdoc/>
    protected override Frame[] CreateFrames(int count, Stack stack) =>
        FrameHelper.CreateFlankUdpFrames(
            count,
            stack,
            FilterFlankScenario.SpikePeriod,
            enableSpikes: true);
}
