// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Classic <c>udp.port</c> value miss without index. Every packet is evaluated; the presence
/// index would not help anyway because UDP is present on all frames.
/// </summary>
internal sealed class FilterSimpleNomatchScenario : FilterScenarioBase
{
    /// <inheritdoc/>
    public override string Name => "filter-simple-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Classic no-match \"{Expression}\" without index (full eval, value miss).");

    /// <inheritdoc/>
    protected override string Expression => "udp.port == 99999";

    /// <inheritdoc/>
    protected override bool UseIndex => false;
}
