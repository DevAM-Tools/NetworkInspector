// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Scoped TCP filter on UDP-only traffic. Index pruning should reject every packet before the
/// scope BFS runs.
/// </summary>
internal sealed class FilterScopedNomatchScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-scoped-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Scoped no-match \"{Expression}\" on UDP-only traffic (index prune).");

    /// <inheritdoc/>
    protected override string Expression => "$tcp[0] { tcp.port == 80 }";
}
