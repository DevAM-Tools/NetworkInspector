// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Classic <c>udp.port</c> match without index prune / presence shortcuts
/// (<see cref="FilterScenarioBase.UseIndex"/> is <see langword="false"/>).
/// </summary>
internal sealed class FilterSimpleScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-simple";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Classic match \"{Expression}\" without index (null PacketIndex on eval).");

    /// <inheritdoc/>
    protected override string Expression => "udp.port == 12345";

    /// <inheritdoc/>
    protected override bool UseIndex => false;
}
