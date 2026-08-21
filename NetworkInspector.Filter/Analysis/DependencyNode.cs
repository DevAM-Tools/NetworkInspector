// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

#region Base

/// <summary>
/// A conservative, presence-only abstraction of a filter expression.
/// <para>
/// Each node describes a <b>superset</b> of the packets the corresponding sub-expression can
/// match, expressed purely in terms of protocol and field presence. Turning the tree into a
/// bitmap therefore never drops a matching packet, which is what makes index pruning safe.
/// </para>
/// </summary>
internal abstract class DependencyNode
{
}

#endregion

#region Nodes

/// <summary>Requires a protocol, field or alias group to be present.</summary>
internal sealed class DependencyLeaf(FilterSymbol symbol) : DependencyNode
{
    /// <summary>The required symbol.</summary>
    public FilterSymbol Symbol { get; } = symbol;
}

/// <summary>All children must hold (conjunction).</summary>
internal sealed class DependencyAll(DependencyNode[] children) : DependencyNode
{
    /// <summary>The conjuncts.</summary>
    public DependencyNode[] Children { get; } = children;
}

/// <summary>At least one child must hold (disjunction).</summary>
internal sealed class DependencyAny(DependencyNode[] children) : DependencyNode
{
    /// <summary>The disjuncts.</summary>
    public DependencyNode[] Children { get; } = children;
}

/// <summary>
/// Nothing can be said about this sub-expression — for example a negation, a boolean constant,
/// or a field with no index group. Treated as "every packet may match".
/// </summary>
internal sealed class DependencyUnknown : DependencyNode
{
    /// <summary>The shared instance.</summary>
    public static readonly DependencyUnknown Instance = new();

    private DependencyUnknown()
    {
    }
}

#endregion
