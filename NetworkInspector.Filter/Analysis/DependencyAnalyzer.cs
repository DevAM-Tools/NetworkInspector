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
    /// <param name="program">The parsed program.</param>
    /// <param name="resolver">Resolver of the current compilation; unknown names become <see cref="DependencyUnknown"/>.</param>
    /// <returns>The presence tree, which is a conservative superset description (see <see cref="DependencyNode"/>).</returns>
    public static DependencyNode Analyze(FilterProgram program, SymbolResolver resolver)
    {
        // A flank tracker keeps state across packets, so skipping any packet would change what later packets see.
        // Pruning is therefore switched off for the whole program, not just for the flank sub-expression.
        if (program.IsStateful)
        {
            return DependencyUnknown.Instance;
        }
        return _Visit(program.Root, resolver);
    }

    #endregion

    #region Visitor

    /// <summary>
    /// Maps one AST node. Every case must return a superset of the packets the node can match; anything not listed
    /// falls through to <see cref="DependencyUnknown"/>, which is always sound. Recursion depth is the expression depth
    /// (bounded by the parser's nesting limit).
    /// </summary>
    private static DependencyNode _Visit(FilterNode node, SymbolResolver resolver)
    {
        switch (node)
        {
            case PresenceNode presence:
                return _Leaf(presence.Name, resolver);

            // Value predicates hold when ANY occurrence of the field satisfies them, so even != and "not in" need the
            // field to be present: a packet without the field cannot match, and presence is a valid necessary condition.
            case CompareNode compare:
                return _Leaf(compare.Left.Name, resolver);

            case InSetNode inSet:
                return _Leaf(inSet.Left.Name, resolver);

            case InRangeNode inRange:
                return _Leaf(inRange.Left.Name, resolver);

            case StringPredicateNode stringPredicate:
                return _Leaf(stringPredicate.Left.Name, resolver);

            // Short-circuit order does not matter here: the tree describes which packets can match, not how to evaluate.
            case LogicalNode logical:
            {
                DependencyNode left = _Visit(logical.Left, resolver);
                DependencyNode right = _Visit(logical.Right, resolver);
                return logical.Op == LogicalOp.And
                    ? new DependencyAll([left, right])
                    : new DependencyAny([left, right]);
            }

            // The scope matches only inside a hit of its anchor, and the hit's subtree is part of the same packet, so the
            // packet must contain the anchor and everything the body reads. The occurrence index [i] only narrows further.
            case ScopeNode scope:
            {
                DependencyNode anchor = _Leaf(scope.Name, resolver);
                DependencyNode body = _Visit(scope.Body, resolver);
                return new DependencyAll([anchor, body]);
            }

            // NotNode, BoolConstantNode and FlankNode land here: negation inverts "needs the field" into "may lack it",
            // constants say nothing about presence, and flank is stateful (handled in Analyze).
            default:
                return DependencyUnknown.Instance;
        }
    }

    /// <summary>
    /// Leaf for one name. A name the resolver does not know yields <see cref="DependencyUnknown"/> instead of an error:
    /// binding reports unknown names, the analyzer only has to stay sound.
    /// </summary>
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
