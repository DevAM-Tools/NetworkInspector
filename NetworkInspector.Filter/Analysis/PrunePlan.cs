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
/// <c>Contains</c>. The plan holds indexes and resolutions only weakly (the index owns its resolution), so a long-lived
/// filter never keeps the index of an ended session in memory. Other reader types (test doubles, foreign implementations) are resolved on every call because
/// their answers may change between calls.
/// </para>
/// <para><b>Thread-safety:</b> not thread-safe; owned by one single-threaded <see cref="Filter"/>.</para>
/// </summary>
internal sealed class PrunePlan
{
    #region Types

    /// <summary>Node discriminant of the flattened tree; replaces a type switch over the class hierarchy per packet.</summary>
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

    /// <summary>
    /// Growable buffers of one resolution run. Resolution allocates freely because it runs once per index; the result is
    /// copied into exact-size arrays (the <see cref="Resolution"/>) so the per-packet path touches only flat arrays.
    /// </summary>
    private sealed class Builder
    {
        internal List<Node> Nodes { get; } = [];

        internal List<int> Children { get; } = [];

        internal List<ReadOnlyRoaringBitmap> Bitmaps { get; } = [];
    }

    #endregion

    #region Fields

    private readonly DependencyNode _Root;

    /// <summary>
    /// Resolution of every index instance this plan has seen, owned by the index: an entry lives exactly as long as its
    /// <see cref="PacketIndex"/>. The resolution holds the index's bitmap objects, so it must not outlive the index; a
    /// plain field would keep the last index (and its bitmaps) in memory after the session that owned it ended, for as
    /// long as the filter lives.
    /// </summary>
    private readonly ConditionalWeakTable<PacketIndex, Resolution> _Resolutions = new();

    /// <summary>
    /// Weak handle to the index of the last query; the fast path compares by reference without a table lookup. Weak so
    /// the plan never keeps an index alive. Created once and re-targeted, so a switch of index allocates nothing.
    /// </summary>
    private WeakReference<PacketIndex>? _LastIndex;

    /// <summary>
    /// Weak handle to the resolution of <see cref="_LastIndex"/>. Weak because the table owns it for the index's
    /// lifetime; a strong field would pin the bitmaps of an ended session.
    /// </summary>
    private WeakReference<Resolution>? _LastResolution;

    /// <summary>
    /// Stored for an index that cannot prune, so that outcome is cached too (the table holds no null values). Its root is
    /// negative; no real resolution has one.
    /// </summary>
    private static readonly Resolution _Unresolvable = new([], [], [], -1);

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
        // Known index types resolve once per instance; the struct view unwraps to its source without boxing. The type
        // tests on the generic parameter are resolved by the JIT per instantiation, so the struct case does not box.
        PacketIndex? concrete = null;
        if (index is PacketIndex packetIndex)
        {
            concrete = packetIndex;
        }
        else if (index is PacketIndexReaderView view)
        {
            concrete = view.Source;
        }

        // Resolve (or reuse the cached resolution). The cache is written without synchronization, which is why the plan
        // is single-threaded. An unresolvable result is cached too (null), so an index that cannot prune is probed once.
        Resolution? resolution;
        if (concrete is not null)
        {
            resolution = _ResolutionFor(concrete);
            if (resolution.Root < 0)
            {
                resolution = null;
            }
        }
        else
        {
            // Test doubles and foreign readers may answer differently between calls, so nothing is cached. This path
            // allocates per call and is not a production path: production indexes are always PacketIndex or its view.
            resolution = _Resolve(index);
        }

        // "Cannot prune": isCandidate stays true so a caller that ignores the return value still evaluates the packet.
        if (resolution is null)
        {
            isCandidate = true;
            return false;
        }

        isCandidate = _Evaluate(resolution, resolution.Root, packetId);
        return true;
    }

    /// <summary>
    /// The resolution of <paramref name="concrete"/>, resolving it on first sight. The common case (same index as the
    /// previous call) costs two weak-handle reads and a reference comparison and allocates nothing; resolving allocates
    /// and runs once per index instance. A real resolution never has a negative root; <see cref="_Unresolvable"/> does.
    /// </summary>
    private Resolution _ResolutionFor(PacketIndex concrete)
    {
        // Fast path: the index of the previous query. Both handles are weak, see the field comments for why.
        WeakReference<PacketIndex>? lastIndex = _LastIndex;
        WeakReference<Resolution>? lastResolution = _LastResolution;
        if (lastIndex is not null
            && lastResolution is not null
            && lastIndex.TryGetTarget(out PacketIndex? last)
            && ReferenceEquals(last, concrete)
            && lastResolution.TryGetTarget(out Resolution? cached))
        {
            return cached;
        }

        // Slow path: a new index (or one whose handles were cleared). The table keeps the resolution alive for as long as
        // the index lives, so switching back and forth between two live indexes resolves each only once.
        if (!_Resolutions.TryGetValue(concrete, out Resolution? resolution))
        {
            resolution = _Resolve(concrete)
                ?? _Unresolvable;
            _Resolutions.Add(concrete, resolution);
        }

        // Re-target the handles instead of replacing them, so a switch allocates nothing after the first call.
        if (lastIndex is null || lastResolution is null)
        {
            _LastIndex = new WeakReference<PacketIndex>(concrete);
            _LastResolution = new WeakReference<Resolution>(resolution);
        }
        else
        {
            lastIndex.SetTarget(concrete);
            lastResolution.SetTarget(resolution);
        }

        return resolution;
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

        // Freeze into exact-size arrays; the lists are only construction scaffolding.
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

    /// <summary>
    /// Resolves a symbol to a leaf holding one bitmap (protocol or field group) or one per member field (alias): the leaf
    /// holds when any of them contains the packet id. Mirrors <see cref="CandidateBitmapBuilder"/>; a missing bitmap
    /// means the index cannot tell, so the leaf is unresolved rather than empty.
    /// </summary>
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
                // Roll back the members added so far; the leaf node is not created, so they would be unreachable garbage.
                bitmaps.RemoveRange(start, bitmaps.Count - start);
                return -1;
            }

            bitmaps.Add(field);
        }

        return _AddNode(new Node(NodeKind.Leaf, start, symbol.Fields.Length), builder);
    }

    /// <summary>Appends an All/Any node whose children are the first <paramref name="count"/> entries of <paramref name="children"/>.</summary>
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
