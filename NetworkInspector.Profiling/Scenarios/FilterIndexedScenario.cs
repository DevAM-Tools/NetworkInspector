// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Same classic <c>udp.port</c> match as <see cref="FilterSimpleScenario"/>, but with candidate
/// prune and <see cref="PacketIndex"/> passed into every evaluation.
/// </summary>
internal sealed class FilterIndexedScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-indexed";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Classic match \"{Expression}\" with index prune + PacketIndex on eval.");

    /// <inheritdoc/>
    protected override string Expression => "udp.port == 12345";

    /// <inheritdoc/>
    protected override bool UseIndex => true;
}
