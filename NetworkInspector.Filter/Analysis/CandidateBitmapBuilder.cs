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
/// <see cref="TryBuild{TIndex}"/> copies bitmaps and is for a stable index.
/// <see cref="TryIsCandidate{TIndex}"/> uses live <see cref="ReadOnlyRoaringBitmap.Contains"/> and
/// does not copy — that is the path for a growing <see cref="PacketIndex"/>.
/// Both are generic so a <see cref="PacketIndexReaderView"/> is not boxed.
/// </para>
/// </summary>
internal static class CandidateBitmapBuilder
{
    #region Entry point

    /// <summary>Builds a candidate bitmap, or reports that pruning is not possible.</summary>
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

    /// <summary>
    /// Tests whether <paramref name="packetId"/> can possibly match, using live
    /// <see cref="ReadOnlyRoaringBitmap.Contains"/> on index bitmaps. No bitmap is copied.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when pruning applied; <paramref name="isCandidate"/> is then the verdict.
    /// <see langword="false"/> when the tree cannot prune (caller must evaluate the packet).
    /// </returns>
    public static bool TryIsCandidate<TIndex>(DependencyNode node, TIndex index, uint packetId, out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        return _TryIsCandidate(node, index, packetId, out isCandidate);
    }

    #endregion

    #region Recursion

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

            accumulated.OrWith(fieldBitmap.ToBitmap());
        }

        candidates = accumulated;
        return accumulated is not null;
    }

    private static bool _TryIsCandidate<TIndex>(
        DependencyNode node,
        TIndex index,
        uint packetId,
        out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        switch (node)
        {
            case DependencyLeaf leaf:
                return _TryIsLeafCandidate(leaf.Symbol, index, packetId, out isCandidate);

            case DependencyAll all:
                return _TryIsAllCandidate(all.Children, index, packetId, out isCandidate);

            case DependencyAny any:
                return _TryIsAnyCandidate(any.Children, index, packetId, out isCandidate);

            default:
                isCandidate = true;
                return false;
        }
    }

    private static bool _TryIsAllCandidate<TIndex>(
        DependencyNode[] children,
        TIndex index,
        uint packetId,
        out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        bool anyResolved = false;
        isCandidate = true;
        foreach (DependencyNode child in children)
        {
            if (!_TryIsCandidate(child, index, packetId, out bool childCandidate))
            {
                continue;
            }

            anyResolved = true;
            if (!childCandidate)
            {
                isCandidate = false;
                return true;
            }
        }

        if (!anyResolved)
        {
            isCandidate = true;
            return false;
        }

        return true;
    }

    private static bool _TryIsAnyCandidate<TIndex>(
        DependencyNode[] children,
        TIndex index,
        uint packetId,
        out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        isCandidate = false;
        foreach (DependencyNode child in children)
        {
            if (!_TryIsCandidate(child, index, packetId, out bool childCandidate))
            {
                isCandidate = true;
                return false;
            }

            if (childCandidate)
            {
                isCandidate = true;
            }
        }

        return true;
    }

    private static bool _TryIsLeafCandidate<TIndex>(
        FilterSymbol symbol,
        TIndex index,
        uint packetId,
        out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        if (symbol.Kind == FilterSymbolKind.Protocol)
        {
            if (index.TryGetProtocolBitmap(symbol.ProtocolId, out ReadOnlyRoaringBitmap protocolBitmap))
            {
                isCandidate = protocolBitmap.Contains(packetId);
                return true;
            }

            isCandidate = true;
            return false;
        }

        bool anyField = false;
        isCandidate = false;
        foreach (FieldId fieldId in symbol.Fields)
        {
            if (!index.TryGetFieldBitmap(fieldId, out ReadOnlyRoaringBitmap fieldBitmap))
            {
                isCandidate = true;
                return false;
            }

            anyField = true;
            if (fieldBitmap.Contains(packetId))
            {
                isCandidate = true;
            }
        }

        if (!anyField)
        {
            isCandidate = true;
            return false;
        }

        return true;
    }

    #endregion
}
