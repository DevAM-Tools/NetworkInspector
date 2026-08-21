// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Compound classic expression that requires TCP. On UDP-only traffic the presence index should
/// prune the entire pool.
/// </summary>
internal sealed class FilterComplexNomatchScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-complex-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        "Compound classic no-match on UDP-only traffic (index prune via tcp).";

    /// <inheritdoc/>
    protected override string Expression =>
        "(tcp.port == 80 && udp.port == 12345) || (tcp && eth.src == 66:77:88:99:aa:bb)";
}
