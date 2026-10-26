// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// The output of code generation: the root delegate plus the mutable runtime state it closes over.
/// <para>
/// <b>Thread-safety:</b> not thread-safe. The flank trackers hold per-filter state, so one program belongs to exactly one
/// <see cref="Filter"/> instance (a derived filter compiles its own program).
/// </para>
/// </summary>
internal sealed class CompiledFilterProgram(FilterEvalFn root, FlankRuntime[] flanks)
{
    #region Properties

    /// <summary>The compiled root predicate.</summary>
    public FilterEvalFn Root { get; } = root;

    /// <summary>Every flank tracker in the program, in emission order.</summary>
    public FlankRuntime[] Flanks { get; } = flanks;

    /// <summary>
    /// Whether evaluation carries state across packets: exactly when the program contains a flank tracker. This is the
    /// source of <see cref="Filter.IsStateful"/> and decides ordering checks, caching and pruning.
    /// </summary>
    public bool IsStateful => Flanks.Length != 0;

    #endregion

    #region State

    /// <summary>Clears all flank trackers.</summary>
    public void ResetState()
    {
        foreach (FlankRuntime flank in Flanks)
        {
            flank.Reset();
        }
    }

    #endregion
}
