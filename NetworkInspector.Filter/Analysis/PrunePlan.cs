// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// The live presence-pruning program of one stateless filter.
/// <para>
/// <b>Shape.</b> On first use with a <see cref="PacketIndex"/> the <see cref="DependencyNode"/> tree is resolved once
/// into a flat array of nodes: every leaf gets its bitmap references, and unresolved children are folded with the
/// rules of <see cref="CandidateBitmapBuilder"/> — a conjunction drops them (the superset only widens), a disjunction
/// becomes unresolved (an unknown disjunct may match anything). Per packet only <c>Contains</c> calls remain. The
/// evaluation short-circuits like the recursive walk it replaces: a conjunction stops at the first child that rules the
/// packet out, a disjunction at the first child that admits it, so no more bitmaps are probed than before; what goes is
/// the per-packet node-type switch and the per-packet bitmap lookup through the reader.
/// </para>
/// <para>
/// <b>Caching.</b> A packet index creates its bitmap objects once in its constructor and grows them in place, so a
/// resolution stays valid and live for that index instance; packets indexed later are visible to the next
/// <c>Contains</c>. Other reader types (test doubles, foreign implementations) are resolved on every call because
/// their answers may change between calls.
/// </para>
/// <para><b>Thread-safety:</b> not thread-safe; owned by one single-threaded <see cref="Filter"/>.</para>
/// </summary>
internal sealed class PrunePlan
{
    #region Types

    private enum NodeKind : byte
    {
        Leaf = 0,
        All = 1,
        Any = 2,
    }

    /// <summary>
    /// One resolved node. Leaf: any of the bitmaps at <c>Start</c>..<c>Start + Count</c> contains the id. All/Any: the
    /// children are the node indexes at <c>Start</c>..<c>Start + Count</c> of the child table.
    /// </summary>
    private readonly record struct Node(NodeKind Kind, int Start, int Count);

    /// <summary>
    /// A resolved tree. The root index is stored explicitly: nodes of a sub-tree that turned out unresolved (for
    /// example a disjunction whose second child failed) stay in the array unused, so the root is not always the last
    /// node.
    /// </summary>
    private sealed class Resolution(Node[] nodes, int[] children, ReadOnlyRoaringBitmap[] bitmaps, int root)
    {
        internal Node[] Nodes { get; } = nodes;

        internal int[] Children { get; } = children;

        internal ReadOnlyRoaringBitmap[] Bitmaps { get; } = bitmaps;

        internal int Root { get; } = root;
    }

    /// <summary>Growable buffers of one resolution run.</summary>
    private sealed class Builder
    {
        internal List<Node> Nodes { get; } = [];

        internal List<int> Children { get; } = [];

        internal List<ReadOnlyRoaringBitmap> Bitmaps { get; } = [];
    }

    #endregion

    #region Fields

    private readonly DependencyNode _Root;

    /// <summary>Index instance the cached resolution belongs to; null before the first resolution.</summary>
    private PacketIndex? _ResolvedFor;

    /// <summary>Cached resolution for <see cref="_ResolvedFor"/>; null while it is set means that index cannot prune.</summary>
    private Resolution? _Resolution;

    #endregion

    #region Construction

    private PrunePlan(DependencyNode root) => _Root = root;

    /// <summary>Returns a plan for <paramref name="root"/>, or null when the tree can never prune (a lone unknown node).</summary>
    internal static PrunePlan? Create(DependencyNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root is DependencyUnknown
            ? null
            : new PrunePlan(root);
    }

    #endregion

    #region Evaluation

    /// <summary>
    /// Tests one packet id. Returns false (caller evaluates the packet) when this index cannot prune; otherwise
    /// <paramref name="isCandidate"/> is the verdict. The caller has checked that the index belongs to the filter's
    /// stack.
    /// </summary>
    internal bool TryIsCandidate<TIndex>(TIndex index, uint packetId, out bool isCandidate)
        where TIndex : IPacketIndexReader
    {
        // Known index types resolve once per instance; the struct view unwraps to its source without boxing.
        PacketIndex? concrete = null;
        if (index is PacketIndex packetIndex)
        {
            concrete = packetIndex;
        }
        else if (index is PacketIndexReaderView view)
        {
            concrete = view.Source;
        }

        Resolution? resolution;
        if (concrete is not null)
        {
            if (!ReferenceEquals(concrete, _ResolvedFor))
            {
                _Resolution = _Resolve(concrete);
                _ResolvedFor = concrete;
            }

            resolution = _Resolution;
        }
        else
        {
            resolution = _Resolve(index);
        }

        if (resolution is null)
        {
            isCandidate = true;
            return false;
        }

        isCandidate = _Evaluate(resolution, resolution.Root, packetId);
        return true;
    }

    /// <summary>Short-circuit evaluation; recursion depth is the dependency depth (bounded by the parser's nesting limit).</summary>
    private static bool _Evaluate(Resolution resolution, int nodeIndex, uint packetId)
    {
        Node node = resolution.Nodes[nodeIndex];
        int end = node.Start + node.Count;
        switch (node.Kind)
        {
            case NodeKind.Leaf:
            {
                ReadOnlyRoaringBitmap[] bitmaps = resolution.Bitmaps;
                for (int i = node.Start; i < end; i++)
                {
                    if (bitmaps[i].Contains(packetId))
                    {
                        return true;
                    }
                }

                return false;
            }

            case NodeKind.All:
            {
                int[] children = resolution.Children;
                for (int i = node.Start; i < end; i++)
                {
                    if (!_Evaluate(resolution, children[i], packetId))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
            {
                int[] children = resolution.Children;
                for (int i = node.Start; i < end; i++)
                {
                    if (_Evaluate(resolution, children[i], packetId))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    #endregion

    #region Resolution

    /// <summary>
    /// Resolves the tree against <paramref name="index"/>: null when the index cannot prune. Allocates; runs once per
    /// <see cref="PacketIndex"/> instance and per call only for other reader types.
    /// </summary>
    private Resolution? _Resolve<TIndex>(TIndex index)
        where TIndex : IPacketIndexReader
    {
        Builder builder = new();
        int root = _ResolveNode(_Root, index, builder);
        if (root < 0)
        {
            return null;
        }

        return new Resolution([.. builder.Nodes], [.. builder.Children], [.. builder.Bitmaps], root);
    }

    /// <summary>Appends the resolved form of <paramref name="node"/> and returns its node index, or -1 when unresolved.</summary>
    private static int _ResolveNode<TIndex>(DependencyNode node, TIndex index, Builder builder)
        where TIndex : IPacketIndexReader
    {
        switch (node)
        {
            case DependencyLeaf leaf:
                return _ResolveLeaf(leaf.Symbol, index, builder);

            case DependencyAll all:
            {
                // Conjunction: unresolved children are dropped; nothing resolved means unresolved; one child stands alone.
                int[] resolved = new int[all.Children.Length];
                int count = 0;
                foreach (DependencyNode child in all.Children)
                {
                    int childIndex = _ResolveNode(child, index, builder);
                    if (childIndex >= 0)
                    {
                        resolved[count++] = childIndex;
                    }
                }

                return count switch
                {
                    0 => -1,
                    1 => resolved[0],
                    _ => _AddParent(NodeKind.All, resolved, count, builder),
                };
            }

            case DependencyAny any:
            {
                // Disjunction: one unresolved child makes the whole disjunction unresolved.
                if (any.Children.Length == 0)
                {
                    return -1;
                }

                int[] resolved = new int[any.Children.Length];
                for (int i = 0; i < any.Children.Length; i++)
                {
                    resolved[i] = _ResolveNode(any.Children[i], index, builder);
                    if (resolved[i] < 0)
                    {
                        return -1;
                    }
                }

                return resolved.Length == 1
                    ? resolved[0]
                    : _AddParent(NodeKind.Any, resolved, resolved.Length, builder);
            }

            default:
                return -1;
        }
    }

    private static int _ResolveLeaf<TIndex>(FilterSymbol symbol, TIndex index, Builder builder)
        where TIndex : IPacketIndexReader
    {
        List<ReadOnlyRoaringBitmap> bitmaps = builder.Bitmaps;
        int start = bitmaps.Count;
        if (symbol.Kind == FilterSymbolKind.Protocol)
        {
            if (!index.TryGetProtocolBitmap(symbol.ProtocolId, out ReadOnlyRoaringBitmap protocol))
            {
                return -1;
            }

            bitmaps.Add(protocol);
            return _AddNode(new Node(NodeKind.Leaf, start, 1), builder);
        }

        // Canonical fields try their shared group bitmap first, exactly like the materialized builder.
        if (symbol.Kind == FilterSymbolKind.Field
            && symbol.IndexGroup.IsValid
            && index.TryGetGroupBitmap(symbol.IndexGroup, out ReadOnlyRoaringBitmap group))
        {
            bitmaps.Add(group);
            return _AddNode(new Node(NodeKind.Leaf, start, 1), builder);
        }

        // Every member field must resolve; one miss (or no member at all) leaves the symbol unresolved.
        if (symbol.Fields.Length == 0)
        {
            return -1;
        }

        foreach (FieldId fieldId in symbol.Fields)
        {
            if (!index.TryGetFieldBitmap(fieldId, out ReadOnlyRoaringBitmap field))
            {
                bitmaps.RemoveRange(start, bitmaps.Count - start);
                return -1;
            }

            bitmaps.Add(field);
        }

        return _AddNode(new Node(NodeKind.Leaf, start, symbol.Fields.Length), builder);
    }

    private static int _AddParent(NodeKind kind, int[] children, int count, Builder builder)
    {
        int start = builder.Children.Count;
        for (int i = 0; i < count; i++)
        {
            builder.Children.Add(children[i]);
        }

        return _AddNode(new Node(kind, start, count), builder);
    }

    private static int _AddNode(Node node, Builder builder)
    {
        builder.Nodes.Add(node);
        return builder.Nodes.Count - 1;
    }

    #endregion
}
