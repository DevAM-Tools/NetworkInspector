// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Compile throughput of four representative expressions (compound, scoped, flank, set). Compilation runs on every
/// filter derive after a restart, on every listener subscription with an expression, and on every editor keystroke
/// that compiles, so it is measured on its own.
/// </summary>
internal sealed class FilterCompileScenario : IProfilingScenario
{
    #region Constants

    /// <summary>Compilations per <see cref="Run"/> call.</summary>
    internal const int CompilesPerIteration = 100;

    private static readonly string[] _Expressions =
    [
        "(udp.port >= 50000 && udp.port <= 80) || (eth.src == 66:77:88:99:aa:bb && udp.port == 12345)",
        "$udp[0] { udp.port == 12345 }",
        "flank(udp.srcport, from: < 100, to: >= 200, within: 50packets)",
        "tcp.port in {80, 443, 8080} && !udp",
    ];

    #endregion

    #region Fields

    private Stack? _Stack;

    #endregion

    #region Scenario shape

    /// <inheritdoc/>
    public string Name => "filter-compile";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"Filter.Compile of four expressions in rotation, {CompilesPerIteration} compiles per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => CompilesPerIteration;

    /// <inheritdoc/>
    public string WorkUnitName => "compiles";

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        for (int i = 0; i < CompilesPerIteration; i++)
        {
            string expression = _Expressions[i & 3];
            FilterResult<PacketFilter> compiled = PacketFilter.Compile(expression, stack);
            if (!compiled.IsSuccess)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Failed to compile '{expression}': {compiled.Error.Message}"));
            }
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
    }

    #endregion
}
