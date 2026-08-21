// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// Derives a presence-only <see cref="DependencyNode"/> tree from a parsed program.
/// <para>
/// The mapping is deliberately lossy and always errs towards <see cref="DependencyUnknown"/>:
/// </para>
/// <list type="bullet">
///   <item><description>Presence tests and value predicates become leaves for the symbol they read.</description></item>
///   <item><description><c>&amp;&amp;</c> becomes <see cref="DependencyAll"/>, <c>||</c> becomes <see cref="DependencyAny"/>.</description></item>
///   <item><description><c>!</c> and boolean constants become <see cref="DependencyUnknown"/>: a negated
///     predicate matches packets that lack the field, so its presence tells us nothing.</description></item>
///   <item><description>A scope requires both its anchor and everything its body reads, because a
///     subtree hit is always a subset of the packet.</description></item>
///   <item><description><c>flank</c> becomes <see cref="DependencyUnknown"/>. Stateful filters must
///     observe every packet in order, so they are never pruned.</description></item>
/// </list>
/// </summary>
internal static class DependencyAnalyzer
{
    #region Entry point

    /// <summary>
    /// Analyzes a program. Stateful programs always return <see cref="DependencyUnknown"/> so a
    /// caller cannot accidentally skip packets that a flank tracker needs to observe.
    /// </summary>
    public static DependencyNode Analyze(FilterProgram program, SymbolResolver resolver)
    {
        if (program.IsStateful)
        {
            return DependencyUnknown.Instance;
        }
        return _Visit(program.Root, resolver);
    }

    #endregion

    #region Visitor

    private static DependencyNode _Visit(FilterNode node, SymbolResolver resolver)
    {
        switch (node)
        {
            case PresenceNode presence:
                return _Leaf(presence.Name, resolver);

            case CompareNode compare:
                return _Leaf(compare.Left.Name, resolver);

            case InSetNode inSet:
                return _Leaf(inSet.Left.Name, resolver);

            case InRangeNode inRange:
                return _Leaf(inRange.Left.Name, resolver);

            case StringPredicateNode stringPredicate:
                return _Leaf(stringPredicate.Left.Name, resolver);

            case LogicalNode logical:
            {
                DependencyNode left = _Visit(logical.Left, resolver);
                DependencyNode right = _Visit(logical.Right, resolver);
                return logical.Op == LogicalOp.And
                    ? new DependencyAll([left, right])
                    : new DependencyAny([left, right]);
            }

            case ScopeNode scope:
            {
                DependencyNode anchor = _Leaf(scope.Name, resolver);
                DependencyNode body = _Visit(scope.Body, resolver);
                return new DependencyAll([anchor, body]);
            }

            default:
                return DependencyUnknown.Instance;
        }
    }

    private static DependencyNode _Leaf(string name, SymbolResolver resolver)
    {
        FilterSymbol? symbol = resolver.Resolve(name);
        if (symbol is null)
        {
            return DependencyUnknown.Instance;
        }
        return new DependencyLeaf(symbol);
    }

    #endregion
}
