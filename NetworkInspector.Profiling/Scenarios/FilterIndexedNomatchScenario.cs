// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Classic presence miss on UDP-only traffic with index enabled. The candidate bitmap is empty,
/// so every packet is skipped before <see cref="IFilter.TryIsMatch"/>.
/// </summary>
internal sealed class FilterIndexedNomatchScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-indexed-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Classic no-match \"{Expression}\" with index prune on UDP-only traffic.");

    /// <inheritdoc/>
    protected override string Expression => "tcp.port == 80";

    /// <inheritdoc/>
    protected override bool UseIndex => true;
}
