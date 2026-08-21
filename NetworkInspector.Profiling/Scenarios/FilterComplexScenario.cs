// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Compound classic expression with expected matches on the synthetic UDP frames.
/// </summary>
internal sealed class FilterComplexScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-complex";

    /// <inheritdoc/>
    public override string Description =>
        "Compound classic match over fresh lazily parsed, indexed packets.";

    /// <inheritdoc/>
    protected override string Expression =>
        "(udp.port >= 50000 && udp.port <= 80) || " +
        "(eth.src == 66:77:88:99:aa:bb && udp.port == 12345)";
}
