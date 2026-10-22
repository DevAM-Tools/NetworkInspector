// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Four distinct fields, no index, no match: every packet evaluates all four value tests over the whole packet.
/// Measures how often the evaluator walks the field list per packet.
/// </summary>
internal sealed class FilterMultiNomatchScenario : FilterScenarioBase
{
    #region Properties

    /// <inheritdoc/>
    public override string Name => "filter-multi-nomatch";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant($"Four-field no-match \"{Expression}\" without index (full eval of every term).");

    /// <inheritdoc/>
    protected override string Expression =>
        "eth.type == 2048 || ipv6.hlim == 1 || udp.dstport == 1 || eth.dst == 00:00:00:00:00:01";

    /// <inheritdoc/>
    protected override bool UseIndex => false;

    #endregion
}
