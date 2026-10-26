// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// Turns a <see cref="DependencyNode"/> tree into a candidate <see cref="RoaringBitmap"/> using
/// only the presence bitmaps of a <see cref="PacketIndex"/>.
/// <para>
/// The result is a <b>superset</b> of the matching packets: packets outside it provably cannot
/// match, packets inside it still need full evaluation. Returning <see langword="false"/> means
/// "no useful pruning" and the caller must evaluate every packet.
/// </para>
/// <para>
/// Combination rules:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="DependencyAll"/> intersects the children it can resolve and
///     ignores the rest — dropping a conjunct only widens the superset, so this stays sound.</description></item>
///   <item><description><see cref="DependencyAny"/> unions its children, but a single unresolvable
///     child makes the whole union unresolvable: an unknown disjunct may match anything.</description></item>
/// </list>
/// <para>
/// <see cref="TryBuild{TIndex}"/> copies bitmaps and is for a stable index. It is generic so a
/// <see cref="PacketIndexReaderView"/> is not boxed. The live per-packet path for a growing
/// <see cref="PacketIndex"/> is <see cref="PrunePlan"/>, which applies the same combination rules once per index and
/// then tests each packet with <see cref="ReadOnlyRoaringBitmap.Contains"/> without copying.
/// </para>
/// </summary>
internal static class CandidateBitmapBuilder
{
    #region Entry point

    /// <summary>Builds a candidate bitmap, or reports that pruning is not possible. Thread-safe only while the index does not grow.</summary>
    /// <typeparam name="TIndex">
    /// Reader type; kept generic so a struct view is passed without boxing and its calls can be inlined.
    /// </typeparam>
    /// <param name="node">The dependency tree.</param>
    /// <param name="index">The index to read presence bitmaps from.</param>
    /// <param name="candidates">Receives the candidate superset on success.</param>
    /// <returns><see langword="true"/> when a usable candidate set was produced.</returns>
    public static bool TryBuild<TIndex>(
        DependencyNode node,
        TIndex index,
        [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        return _TryBuild(node, index, out candidates);
    }

    #endregion

    #region Recursion

    /// <summary>
    /// Dispatches on the node type. Invariant of every <c>_TryBuild*</c> method: a returned bitmap is a fresh, privately
    /// owned copy, never a view of the index, because the parent combines it in place (<c>AndWith</c> / <c>OrWith</c>).
    /// Recursion depth equals the tree depth, which the filter parser bounds.
    /// </summary>
    private static bool _TryBuild<TIndex>(
        DependencyNode node,
        TIndex index,
        [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        switch (node)
        {
            case DependencyLeaf leaf:
                return _TryBuildLeaf(leaf.Symbol, index, out candidates);

            case DependencyAll all:
                return _TryBuildAll(all.Children, index, out candidates);

            case DependencyAny any:
                return _TryBuildAny(any.Children, index, out candidates);

            default:
                candidates = null;
                return false;
        }
    }

    /// <summary>
    /// Conjunction: intersect every child that resolves, skip the others. Skipping is sound because a dropped conjunct only
    /// widens the superset. Fails only when no child resolves (then nothing can be pruned).
    /// </summary>
    private static bool _TryBuildAll<TIndex>(
        DependencyNode[] children,
        TIndex index,
        [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        RoaringBitmap? accumulated = null;
        foreach (DependencyNode child in children)
        {
            if (!_TryBuild(child, index, out RoaringBitmap? childBitmap))
            {
                continue;
            }

            // The first resolved child is adopted as the accumulator (it is an owned copy, see _TryBuild), which saves
            // one clone; later children are intersected into it in place instead of allocating a result bitmap per step.
            if (accumulated is null)
            {
                accumulated = childBitmap;
                continue;
            }

            accumulated.AndWith(childBitmap);
        }

        candidates = accumulated;
        return accumulated is not null;
    }

    /// <summary>
    /// Disjunction: union of all children. One unresolvable child fails the whole node, because that disjunct may match
    /// any packet, so no bitmap short of "everything" would still be a superset. An empty child list also fails.
    /// </summary>
    private static bool _TryBuildAny<TIndex>(
        DependencyNode[] children,
        TIndex index,
        [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        RoaringBitmap? accumulated = null;
        foreach (DependencyNode child in children)
        {
            if (!_TryBuild(child, index, out RoaringBitmap? childBitmap))
            {
                candidates = null;
                return false;
            }

            if (accumulated is null)
            {
                accumulated = childBitmap;
                continue;
            }

            accumulated.OrWith(childBitmap);
        }

        candidates = accumulated;
        return accumulated is not null;
    }

    /// <summary>
    /// Leaf: the packets that contain the protocol, the field's index group, or any member field of an alias. A missing
    /// bitmap means "the index cannot tell" (id out of range, or the field has no index group), not "no packet has it",
    /// so it is reported as unresolvable instead of as an empty set: treating it as empty would drop matching packets.
    /// </summary>
    private static bool _TryBuildLeaf<TIndex>(
        FilterSymbol symbol,
        TIndex index,
        [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader
    {
        if (symbol.Kind == FilterSymbolKind.Protocol)
        {
            if (index.TryGetProtocolBitmap(symbol.ProtocolId, out ReadOnlyRoaringBitmap protocolBitmap))
            {
                candidates = protocolBitmap.ToBitmap();
                return true;
            }

            candidates = null;
            return false;
        }

        // Canonical fields often share one presence group — one bitmap instead of N field clones.
        if (symbol.Kind == FilterSymbolKind.Field
            && symbol.IndexGroup.IsValid
            && index.TryGetGroupBitmap(symbol.IndexGroup, out ReadOnlyRoaringBitmap groupBitmap))
        {
            candidates = groupBitmap.ToBitmap();
            return true;
        }

        // Alias group (or a field without a usable group bitmap): the union of its members. Every member must resolve, for
        // the same soundness reason as in _TryBuildAny.
        RoaringBitmap? accumulated = null;
        foreach (FieldId fieldId in symbol.Fields)
        {
            if (!index.TryGetFieldBitmap(fieldId, out ReadOnlyRoaringBitmap fieldBitmap))
            {
                candidates = null;
                return false;
            }

            if (accumulated is null)
            {
                accumulated = fieldBitmap.ToBitmap();
                continue;
            }

            // Only the first member is cloned; later members are OR-ed straight from the view.
            accumulated.OrWith(fieldBitmap);
        }

        candidates = accumulated;
        return accumulated is not null;
    }

    #endregion
}
