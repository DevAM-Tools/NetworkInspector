// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// Walks a parsed filter program into a <see cref="FilterObserver"/>.
/// Does not construct a <see cref="Filter"/>, a match cache, or the tree interpreter.
/// </summary>
internal static class FilterObserverCompiler
{
    private static readonly TimeSpan _DefaultRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Binds <paramref name="program"/> to <paramref name="stack"/> as an observer.</summary>
    public static FilterResult<FilterObserver> Compile(
        string expression,
        FilterProgram program,
        IStack stack,
        FilterCompileOptions? options)
    {
        if (_HasScope(program.Root))
        {
            return FilterError.NeedsFieldTree();
        }

        Emitter emitter = new(stack, options);
        FilterResult<int> root = emitter.Emit(program.Root);
        if (!root.TryGetValue(out int rootIndex))
        {
            return root.Error;
        }

        return emitter.Build(expression, rootIndex);
    }

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

    private sealed class Emitter(IStack stack, FilterCompileOptions? options)
    {
        private readonly IStack _Stack = stack;
        private readonly SymbolResolver _Resolver = new SymbolResolver(stack);
        private readonly TimeSpan _RegexTimeout = options?.RegexTimeout ?? _DefaultRegexTimeout;
        private readonly List<Leaf> _Leaves = [];
        private readonly List<BitNode> _Nodes = [];
        private readonly List<FieldId> _FieldOrder = [];
        private readonly Dictionary<int, List<int>> _ByField = [];
        private FlankRuntime? _Flank;
        private int _WhenRoot = -1;
        private FieldId[] _FlankIds = [];

        public FilterResult<FilterObserver> Build(string expression, int root)
        {
            FieldId[] ids = _FieldOrder.ToArray();
            int[][] indexes = new int[ids.Length][];
            for (int i = 0; i < ids.Length; i++)
            {
                indexes[i] = _ByField[ids[i].Value].ToArray();
            }

            FieldWatch watch = ids.Length == 0
                ? FieldWatch.None
                : FieldWatch.Only(_Stack, ids);
            return new FilterObserver(
                expression,
                _Stack,
                watch,
                ids,
                indexes,
                _Leaves.ToArray(),
                _Nodes.ToArray(),
                root,
                _Flank,
                _WhenRoot,
                _FlankIds);
        }

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

            Leaf leaf = new() { Accessor = valueAccessor };
            if (node.Op == StringOp.Contains)
            {
                leaf.Kind = LeafKind.Contains;
                leaf.Needle = node.Pattern;
            }
            else
            {
                leaf.Kind = LeafKind.Matches;
                leaf.Regex = new Regex(node.Pattern, RegexOptions.CultureInvariant, _RegexTimeout);
            }

            return _AddLeaf(leaf, valueAccessor.Fields);
        }

        private FilterResult<int> _Presence(PresenceNode node)
        {
            FilterSymbol? symbol = _Resolver.Resolve(node.Name);
            if (symbol is null)
            {
                return FilterError.UnknownField(node.Name, node.Position, node.Length);
            }

            FieldId[] fields = symbol.Kind == FilterSymbolKind.Protocol
                ? [symbol.ContainerField]
                : symbol.Fields;
            return _AddLeaf(new Leaf { Kind = LeafKind.Presence }, fields);
        }

        private FilterResult<int> _EmitFlank(FlankNode node)
        {
            FilterResult<FilterSymbol> symbol = _Resolver.ResolveValue(node.FieldName, node.Position, node.Length);
            if (!symbol.TryGetValue(out FilterSymbol? field))
            {
                return symbol.Error;
            }

            ValueAccessor accessor = ValueAccessor.Direct(field.Fields);
            _Flank = new FlankRuntime(accessor, node.From, node.To, node.By, node.IsAnyChange, node.Window);
            _FlankIds = field.Fields;
            for (int i = 0; i < field.Fields.Length; i++)
            {
                int raw = field.Fields[i].Value;
                if (!_ByField.ContainsKey(raw))
                {
                    _ByField[raw] = [];
                    _FieldOrder.Add(field.Fields[i]);
                }
            }
            if (node.When is FilterNode when)
            {
                FilterResult<int> gate = Emit(when);
                if (!gate.TryGetValue(out int whenRoot))
                {
                    return gate.Error;
                }

                _WhenRoot = whenRoot;
            }

            return _Const(false);
        }

        private FilterResult<ValueAccessor> _Accessor(OperandNode node)
        {
            FilterResult<FilterSymbol> symbol = _Resolver.ResolveValue(node.Name, node.Position, node.Length);
            if (!symbol.TryGetValue(out FilterSymbol? field))
            {
                return symbol.Error;
            }

            if (node is SliceOperandNode slice)
            {
                return ValueAccessor.Slice(field.Fields, slice.Start, slice.End);
            }

            if (node is LengthOperandNode)
            {
                return ValueAccessor.Length(field.Fields);
            }

            return ValueAccessor.Direct(field.Fields);
        }

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
