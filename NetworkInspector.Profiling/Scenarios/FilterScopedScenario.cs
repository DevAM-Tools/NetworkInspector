// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Scoped UDP-port match against fresh, indexed packets.
/// </summary>
internal sealed class FilterScopedScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-scoped";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Scoped match \"{Expression}\" over fresh lazily parsed, indexed packets.");

    /// <inheritdoc/>
    protected override string Expression => "$udp[0] { udp.port == 12345 }";
}
