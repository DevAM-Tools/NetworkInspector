// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Stateful flank match: deterministic UDP source-port spikes every 50 packets fire
/// <c>from: &lt; 100 → to: &gt;= 200</c> within a 50-packet window.
/// </summary>
/// <remarks>
/// Pure <c>flank</c> is presence-unknown, so the index cannot prune — every packet must be
/// observed in order to keep tracker state. Match rate is about 1 / <see cref="SpikePeriod"/>.
/// </remarks>
internal sealed class FilterFlankScenario : FilterScenarioBase
{
    /// <summary>Packets between high-port spikes in the synthetic stream.</summary>
    internal const int SpikePeriod = 50;

    /// <inheritdoc/>
    public override string Name => "filter-flank";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Flank match \"{Expression}\" with srcport spikes every {SpikePeriod} packets.");

    /// <inheritdoc/>
    protected override string Expression =>
        "flank(udp.srcport, from: < 100, to: >= 200, within: 50packets)";

    /// <inheritdoc/>
    protected override bool IsStateful => true;

    /// <inheritdoc/>
    protected override Frame[] CreateFrames(int count, Stack stack) =>
        FrameHelper.CreateFlankUdpFrames(count, stack, SpikePeriod, enableSpikes: true);
}
