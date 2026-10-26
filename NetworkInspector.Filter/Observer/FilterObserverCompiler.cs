// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// Walks a parsed filter program into a <see cref="FilterObserver"/>.
/// Does not construct a <see cref="Filter"/>, a match cache, or the tree interpreter.
/// <para>
/// Why a second back end exists: the field-tree <see cref="Filter"/> needs a built field tree, but a skip-tree parse
/// (<c>FieldTreeMode.Skip</c>) delivers only the watched field values. The observer therefore flattens the boolean
/// structure into arrays of leaf tests and boolean nodes that the parse can latch field by field. Operand binding
/// (accessors, regular expressions, flank trackers) is shared with the field-tree code generator through
/// <see cref="FilterBinder"/> so both back ends give identical verdicts for one expression.
/// </para>
/// Thread safety: stateless (static); each call builds its own emitter.
/// </summary>
internal static class FilterObserverCompiler
{
    /// <summary>Binds <paramref name="program"/> to <paramref name="stack"/> as an observer.</summary>
    /// <remarks>
    /// Compile options are consumed by the parser (span callback, regex timeout); the observer itself has no
    /// match cache, so nothing else of <see cref="FilterCompileOptions"/> applies here.
    /// </remarks>
    /// <param name="expression">The source text, kept on the observer for diagnostics. Not re-parsed.</param>
    /// <param name="program">The parsed program. Must not be <see langword="null"/>; not validated (internal caller).</param>
    /// <param name="stack">The stack the field names are resolved against. Not validated (internal caller).</param>
    /// <returns>
    /// The observer, or an error: <see cref="FilterErrorKind.NeedsFieldTree"/> for a scope or a protocol-presence
    /// test the tree-free parse cannot answer, or a binding error such as an unknown field name.
    /// </returns>
    public static FilterResult<FilterObserver> Compile(
        string expression,
        FilterProgram program,
        IStack stack)
    {
        // A subtree scope selects part of the field tree, which a skip-tree parse never builds. Refuse up front
        // (rather than silently evaluating it wrongly) so callers can fall back to a tree-based Filter.
        if (_HasScope(program.Root))
        {
            return FilterError.NeedsFieldTree();
        }

        // Emit the node/leaf tables bottom-up, then freeze them into the immutable observer layout.
        Emitter emitter = new(stack);
        FilterResult<int> root = emitter.Emit(program.Root);
        if (!root.TryGetValue(out int rootIndex))
        {
            return root.Error;
        }

        return emitter.Build(expression, rootIndex);
    }

    /// <summary>
    /// Whether a scope node occurs anywhere below <paramref name="node"/>. Only the node kinds that can contain other
    /// filter nodes (negation, logical operators and a flank's <c>when:</c> gate) are descended into; every other kind
    /// is a leaf for this purpose. Recursion depth equals the expression nesting depth, which the parser bounds.
    /// </summary>
    private static bool _HasScope(FilterNode node)
    {
        if (node is ScopeNode)
        {
            return true;
        }

        if (node is NotNode not)
        {
            return _HasScope(not.Operand);
        }

        if (node is LogicalNode logical)
        {
            return _HasScope(logical.Left) || _HasScope(logical.Right);
        }

        if (node is FlankNode flank && flank.When is FilterNode when)
        {
            return _HasScope(when);
        }

        return false;
    }

    /// <summary>
    /// Builds the observer's flat tables in one recursive pass over the AST. Nodes and leaves refer to each other by
    /// index into <see cref="_Nodes"/> and <see cref="_Leaves"/> (no object graph), and children are always emitted
    /// before their parent, so a node's child indexes are smaller than its own index. Thread safety: single-use and
    /// not thread-safe; used by one <see cref="Compile"/> call.
    /// </summary>
    private sealed class Emitter(IStack stack)
    {
        private readonly IStack _Stack = stack;
        private readonly SymbolResolver _Resolver = new(stack);

        /// <summary>Leaf tests, in emission order. The observer latches a hit flag in each one per frame.</summary>
        private readonly List<Leaf> _Leaves = [];

        /// <summary>Boolean nodes over the leaves; the root index is returned by <see cref="Emit"/>.</summary>
        private readonly List<BitNode> _Nodes = [];

        /// <summary>
        /// Watched fields in first-use order. The position of a field here is its "slot": the index into the
        /// per-slot leaf lists and the value stored in the dense slot-by-field-id table built in <see cref="Build"/>.
        /// </summary>
        private readonly List<FieldId> _FieldOrder = [];

        /// <summary>Raw <c>FieldId.Value</c> → indexes of the leaves that must be tested when that field is delivered.</summary>
        private readonly Dictionary<int, List<int>> _ByField = [];
        private FlankRuntime? _Flank;
        private int _FlankCount;
        private int _WhenRoot = -1;
        private int _FlankLeaf = -1;
        private FieldId[] _FlankIds = [];

        /// <summary>
        /// Freezes the emitted tables into a <see cref="FilterObserver"/>. The observer's per-field work runs for
        /// every delivered field of every packet, so everything it needs is precomputed here as dense arrays indexed
        /// by <c>FieldId.Value</c> instead of dictionary lookups or list copies at match time.
        /// </summary>
        /// <param name="expression">Source text for diagnostics.</param>
        /// <param name="root">Index of the root node in the node table.</param>
        public FilterResult<FilterObserver> Build(string expression, int root)
        {
            // Per slot, the leaves to test: copied into fixed-size arrays so the observer holds tables that can no
            // longer grow and walks them with a plain indexed loop instead of List<T>.
            FieldId[] ids = [.. _FieldOrder];
            int[][] indexes = new int[ids.Length][];
            for (int i = 0; i < ids.Length; i++)
            {
                indexes[i] = [.. _ByField[ids[i].Value]];
            }

            // Dense FieldId.Value → watch slot, replacing a dictionary lookup per delivered field with one array
            // read. Sized by the stack's field count so every valid id fits; the unsigned compare also rejects
            // negative (invalid) ids. -1 means the field is not tested by any leaf.
            int[] slotByFieldId = new int[_Stack.FieldCount];
            Array.Fill(slotByFieldId, -1);
            for (int i = 0; i < ids.Length; i++)
            {
                int raw = ids[i].Value;
                if ((uint)raw < (uint)slotByFieldId.Length)
                {
                    slotByFieldId[raw] = i;
                }
            }

            // Same dense-table idea for the flank sample field: the observer must know for every delivered field
            // whether it feeds the flank tracker, even when no leaf tests that field.
            bool[] flankByFieldId = new bool[_Stack.FieldCount];
            for (int i = 0; i < _FlankIds.Length; i++)
            {
                int raw = _FlankIds[i].Value;
                if ((uint)raw < (uint)flankByFieldId.Length)
                {
                    flankByFieldId[raw] = true;
                }
            }

            // The watch tells the parser which fields to deliver at all; FieldWatch.Only throws on an empty or
            // invalid id list, so an expression without any field (constants only) uses None.
            FieldWatch watch = ids.Length == 0
                ? FieldWatch.None
                : FieldWatch.Only(_Stack, ids);
            return new FilterObserver(
                expression,
                _Stack,
                watch,
                indexes,
                [.. _Leaves],
                [.. _Nodes],
                root,
                _Flank,
                _WhenRoot,
                _FlankLeaf,
                flankByFieldId,
                slotByFieldId);
        }

        /// <summary>
        /// Emits <paramref name="node"/> and its children and returns the index of the resulting boolean node.
        /// Both operands of <c>and</c> / <c>or</c> are always emitted: the observer latches leaf hits as fields arrive
        /// in parse order, so the boolean tree cannot short-circuit at compile time and is evaluated after the parse.
        /// A node kind with no observer equivalent is reported as a compiler error instead of being skipped.
        /// </summary>
        public FilterResult<int> Emit(FilterNode node)
        {
            return node switch
            {
                BoolConstantNode constant => (FilterResult<int>)_Const(constant.Value),
                NotNode not => _Unary(BitOp.Not, not.Operand),
                LogicalNode logical => _Binary(logical.Op == LogicalOp.And ? BitOp.And : BitOp.Or, logical.Left, logical.Right),
                CompareNode compare => _Compare(compare),
                InSetNode set => _Set(set),
                InRangeNode range => _Range(range),
                StringPredicateNode text => _Text(text),
                PresenceNode presence => _Presence(presence),
                FlankNode flank => _EmitFlank(flank),
                _ => (FilterResult<int>)FilterError.Compiler($"Observer compile does not support {node.GetType().Name}."),
            };
        }

        private FilterResult<int> _Compare(CompareNode node)
        {
            FilterResult<ValueAccessor> accessor = _Accessor(node.Left);
            if (!accessor.TryGetValue(out ValueAccessor? valueAccessor))
            {
                return accessor.Error;
            }

            // A direct field compares the delivered value as is; a slice or length operand needs the accessor to
            // derive the compared value first. The accessor is stored only for that case so the common comparison
            // takes no extra indirection per delivered field.
            LeafKind kind = valueAccessor.Kind == ValueAccessorKind.Direct ? LeafKind.Compare : LeafKind.Transform;
            return _AddLeaf(
                new Leaf
                {
                    Kind = kind,
                    Op = node.Op,
                    Literal = node.Right,
                    Accessor = kind == LeafKind.Transform ? valueAccessor : null,
                },
                valueAccessor.Fields);
        }

        private FilterResult<int> _Set(InSetNode node)
        {
            FilterResult<ValueAccessor> accessor = _Accessor(node.Left);
            if (!accessor.TryGetValue(out ValueAccessor? valueAccessor))
            {
                return accessor.Error;
            }

            return _AddLeaf(
                new Leaf { Kind = LeafKind.Set, Set = node.ValueArray, Accessor = valueAccessor },
                valueAccessor.Fields);
        }

        private FilterResult<int> _Range(InRangeNode node)
        {
            FilterResult<ValueAccessor> accessor = _Accessor(node.Left);
            if (!accessor.TryGetValue(out ValueAccessor? valueAccessor))
            {
                return accessor.Error;
            }

            return _AddLeaf(
                new Leaf
                {
                    Kind = LeafKind.Range,
                    Literal = node.Low,
                    LiteralHigh = node.High,
                    Accessor = valueAccessor,
                },
                valueAccessor.Fields);
        }

        private FilterResult<int> _Text(StringPredicateNode node)
        {
            FilterResult<ValueAccessor> accessor = _Accessor(node.Left);
            if (!accessor.TryGetValue(out ValueAccessor? valueAccessor))
            {
                return accessor.Error;
            }

            // Contains is an ordinal substring test (the needle is stored as-is); matches reuses the regular
            // expression the parser already compiled, so the regex timeout from the compile options applies.
            Leaf leaf = new() { Accessor = valueAccessor };
            if (node.Op == StringOp.Contains)
            {
                leaf.Kind = LeafKind.Contains;
                leaf.Needle = node.Pattern;
            }
            else
            {
                // The parser compiled and validated the pattern once; a matches node without it is a bug.
                if (node.Regex is null)
                {
                    return FilterError.Compiler("A 'matches' predicate reached the observer compiler without a compiled pattern.");
                }

                leaf.Kind = LeafKind.Matches;
                leaf.Regex = node.Regex;
            }

            return _AddLeaf(leaf, valueAccessor.Fields);
        }

        private FilterResult<int> _Presence(PresenceNode node)
        {
            FilterSymbol? symbol = _Resolver.Resolve(node.Name);
            if (symbol is null)
            {
                return _Resolver.UnknownName(node.Name, node.Position, node.Length);
            }

            // Skip-tree observers can only latch delivered fields. A protocol without a
            // container field needs an owner scan over a field tree — refuse here instead of
            // passing FieldId.Invalid into FieldWatch.Only (which throws).
            if (symbol.Kind == FilterSymbolKind.Protocol)
            {
                if (!symbol.ContainerField.IsValid)
                {
                    return FilterError.NeedsFieldTree();
                }

                return _AddLeaf(new Leaf { Kind = LeafKind.Presence }, [symbol.ContainerField]);
            }

            return _AddLeaf(new Leaf { Kind = LeafKind.Presence }, symbol.Fields);
        }

        private FilterResult<int> _EmitFlank(FlankNode node)
        {
            // One tracker per observer: a second flank would overwrite Arm/Next state.
            if (_FlankCount > 0)
            {
                return FilterError.Syntax(
                    "FilterObserver supports at most one flank() expression",
                    node.Position,
                    node.Length);
            }

            FilterResult<FlankRuntime> tracker = FilterBinder.BuildFlank(_Resolver, node, out FilterSymbol? field);
            if (!tracker.TryGetValue(out FlankRuntime? flank))
            {
                return tracker.Error;
            }

            // BuildFlank always sets 'field' when it succeeds (the null-forgiving operator relies on that).
            _Flank = flank;
            _FlankIds = field!.Fields;
            _FlankCount = 1;

            // The flank samples its field on every frame, but no leaf tests that field. Register it with an empty
            // leaf list so it still lands in the watch (the parse delivers it) and in the slot table; the observer
            // captures the sample before it looks up any leaf for that field.
            for (int i = 0; i < field.Fields.Length; i++)
            {
                int raw = field.Fields[i].Value;
                if (!_ByField.ContainsKey(raw))
                {
                    _ByField[raw] = [];
                    _FieldOrder.Add(field.Fields[i]);
                }
            }

            // The optional when: gate is an ordinary boolean sub-tree in the same tables. Its leaves latch during the
            // parse like any other; the observer evaluates it from its root at commit time to decide whether the
            // frame is visible to the tracker.
            if (node.When is FilterNode when)
            {
                FilterResult<int> gate = Emit(when);
                if (!gate.TryGetValue(out int whenRoot))
                {
                    return gate.Error;
                }

                _WhenRoot = whenRoot;
            }

            // The flank is a leaf of the bit tree whose hit the observer writes at commit time, so the
            // surrounding boolean tree (A && flank, A || flank, !flank) is evaluated like any other tree.
            // The leaf is registered under no field, so OnField never tests it.
            _FlankLeaf = _Leaves.Count;
            _Leaves.Add(new Leaf { Kind = LeafKind.Flank });
            int nodeIndex = _Nodes.Count;
            _Nodes.Add(new BitNode { Op = BitOp.Leaf, LeafIndex = _FlankLeaf, Left = -1, Right = -1 });
            return nodeIndex;
        }

        private FilterResult<ValueAccessor> _Accessor(OperandNode node) => FilterBinder.BuildAccessor(_Resolver, node);

        /// <summary>
        /// Registers a leaf test and a boolean leaf node for it, and subscribes the leaf to every field that can
        /// satisfy it (an operand may map to several canonical fields when it names an alias group). The leaf is
        /// latched when any of those fields is delivered.
        /// </summary>
        /// <returns>The index of the new boolean node.</returns>
        private int _AddLeaf(Leaf leaf, FieldId[] fields)
        {
            int leafIndex = _Leaves.Count;
            _Leaves.Add(leaf);
            for (int i = 0; i < fields.Length; i++)
            {
                int raw = fields[i].Value;
                if (!_ByField.TryGetValue(raw, out List<int>? list))
                {
                    list = [];
                    _ByField[raw] = list;
                    _FieldOrder.Add(fields[i]);
                }

                list.Add(leafIndex);
            }

            int nodeIndex = _Nodes.Count;
            _Nodes.Add(new BitNode { Op = BitOp.Leaf, LeafIndex = leafIndex, Left = -1, Right = -1 });
            return nodeIndex;
        }

        private FilterResult<int> _Unary(BitOp op, FilterNode operand)
        {
            FilterResult<int> child = Emit(operand);
            if (!child.TryGetValue(out int childIndex))
            {
                return child.Error;
            }

            int index = _Nodes.Count;
            _Nodes.Add(new BitNode { Op = op, Left = childIndex, Right = -1, LeafIndex = -1 });
            return index;
        }

        private FilterResult<int> _Binary(BitOp op, FilterNode left, FilterNode right)
        {
            FilterResult<int> leftResult = Emit(left);
            if (!leftResult.TryGetValue(out int leftIndex))
            {
                return leftResult.Error;
            }

            FilterResult<int> rightResult = Emit(right);
            if (!rightResult.TryGetValue(out int rightIndex))
            {
                return rightResult.Error;
            }

            int index = _Nodes.Count;
            _Nodes.Add(new BitNode { Op = op, Left = leftIndex, Right = rightIndex, LeafIndex = -1 });
            return index;
        }

        private int _Const(bool value)
        {
            int index = _Nodes.Count;
            _Nodes.Add(new BitNode { Op = BitOp.Const, LeafIndex = value ? 1 : 0, Left = -1, Right = -1 });
            return index;
        }
    }
}
