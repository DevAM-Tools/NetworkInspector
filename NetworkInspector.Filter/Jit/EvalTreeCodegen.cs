// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// Default back end: lowers the AST to a tree of <see cref="EvalNode"/> objects and hands the root's
/// <see cref="EvalNode.Evaluate"/> to <see cref="CompiledFilterProgram"/> as the <see cref="FilterEvalFn"/>.
/// <para>
/// <b>Shape.</b> Logic nodes (<c>&amp;&amp;</c>, <c>||</c>, <c>!</c>) short-circuit left to right, which gives the
/// language its documented evaluation order. Every operand (accessors, literals, value sets, compiled regular
/// expressions, flank and scope state) is resolved once here and stored as a typed field, so the per-packet path has no
/// dictionary lookups, string handling, reflection, or closure casts. Building the tree is plain object construction:
/// no runtime code generation, which also keeps every compile cheap.
/// </para>
/// <para>
/// <b>Scopes and flank gates.</b> A scope body and a flank <c>when:</c> gate are node trees of their own; their root's
/// <see cref="EvalNode.Evaluate"/> becomes the delegate on <see cref="ScopeRuntime.Body"/> or
/// <see cref="FlankRuntime.When"/>. A body therefore observes whatever domain the enclosing
/// <see cref="FilterRuntime.Scope"/> call pushed, which makes nested scopes work without re-entrancy tricks.
/// </para>
/// <para><b>Thread-safety:</b> one instance compiles one program at a time; <see cref="Filter"/> creates a new instance
/// per bind.</para>
/// </summary>
internal sealed class EvalTreeCodegen : IFilterCodegen
{
    #region Fields

    private readonly List<FlankRuntime> _Flanks = [];

    private SymbolResolver _Resolver = null!;
    private FilterError? _Error;

    #endregion

    #region Entry point

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="options"/> is not read here: the regex timeout is applied by the parser, which compiles every
    /// <c>matches</c> pattern once into the AST.
    /// </remarks>
    public FilterResult<CompiledFilterProgram> Compile(
        FilterProgram program,
        SymbolResolver resolver,
        FilterCompileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(resolver);
        _Resolver = resolver;
        _Error = null;
        _Flanks.Clear();

        EvalNode root = _Emit(program.Root);
        if (_Error is not null)
        {
            return FilterResult.Fail<CompiledFilterProgram>(_Error);
        }

        return new CompiledFilterProgram(root.Evaluate, [.. _Flanks]);
    }

    #endregion

    #region Emission

    private EvalNode _Emit(FilterNode node)
    {
        switch (node)
        {
            case BoolConstantNode constant:
                return constant.Value
                    ? ConstEvalNode.True
                    : ConstEvalNode.False;

            case NotNode not:
                return new NotEvalNode(_Emit(not.Operand));

            case LogicalNode logical:
            {
                EvalNode left = _Emit(logical.Left);
                EvalNode right = _Emit(logical.Right);
                return logical.Op == LogicalOp.And
                    ? new AndEvalNode(left, right)
                    : new OrEvalNode(left, right);
            }

            case PresenceNode presence:
                return _EmitPresence(presence);

            case CompareNode compare:
                return _EmitValue(
                    compare.Left,
                    accessor => new ValueEvalNode<ComparePredicate>(accessor, new(compare.Op, compare.Right)));

            case InSetNode inSet:
                return _EmitValue(inSet.Left, accessor => new ValueEvalNode<SetPredicate>(accessor, new(inSet.ValueArray)));

            case InRangeNode inRange:
                return _EmitValue(
                    inRange.Left,
                    accessor => new ValueEvalNode<RangePredicate>(accessor, new(inRange.Low, inRange.High)));

            case StringPredicateNode text:
                return _EmitString(text);

            case FlankNode flank:
                return _EmitFlank(flank);

            case ScopeNode scope:
                return _EmitScope(scope);

            default:
                return _Fail(FilterError.Compiler($"Unsupported node '{node.GetType().Name}'"));
        }
    }

    private EvalNode _EmitPresence(PresenceNode presence)
    {
        FilterResult<FilterSymbol> resolved = _Resolver.ResolveAny(presence.Name, presence.Position, presence.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return _Fail(resolved.Error);
        }

        if (symbol.Kind == FilterSymbolKind.Protocol)
        {
            return new ProtocolEvalNode(symbol.ProtocolId, symbol.ContainerField);
        }

        return new FieldPresenceEvalNode(symbol.Fields);
    }

    private EvalNode _EmitValue(OperandNode operand, Func<ValueAccessor, EvalNode> create)
    {
        FilterResult<ValueAccessor> accessor = FilterBinder.BuildAccessor(_Resolver, operand);
        if (!accessor.TryGetValue(out ValueAccessor? value))
        {
            return _Fail(accessor.Error);
        }

        return create(value);
    }

    private EvalNode _EmitString(StringPredicateNode node)
    {
        if (node.Op == StringOp.Contains)
        {
            return _EmitValue(node.Left, accessor => new ValueEvalNode<ContainsPredicate>(accessor, new(node.Pattern)));
        }

        // The parser compiled and validated the pattern once (FilterBinder.TryBuildRegex); a matches node without it
        // would be a parser bug, reported as a compiler error rather than a crash.
        if (node.Regex is not Regex regex)
        {
            return _Fail(FilterError.Compiler("A 'matches' predicate reached code generation without a compiled pattern."));
        }

        FilterResult<ValueAccessor> accessor = FilterBinder.BuildAccessor(_Resolver, node.Left);
        if (!accessor.TryGetValue(out ValueAccessor? value))
        {
            return _Fail(accessor.Error);
        }

        return new MatchesEvalNode(value, regex);
    }

    private EvalNode _EmitFlank(FlankNode flank)
    {
        FilterResult<FlankRuntime> tracker = FilterBinder.BuildFlank(_Resolver, flank, out _);
        if (!tracker.TryGetValue(out FlankRuntime? runtime))
        {
            return _Fail(tracker.Error);
        }

        if (flank.When is FilterNode gate)
        {
            EvalNode gateNode = _Emit(gate);
            if (_Error is not null)
            {
                return ConstEvalNode.False;
            }

            runtime.When = gateNode.Evaluate;
        }

        _Flanks.Add(runtime);
        return new FlankEvalNode(runtime);
    }

    private EvalNode _EmitScope(ScopeNode scope)
    {
        FilterResult<FilterSymbol> resolved = _Resolver.ResolveAny(scope.Name, scope.Position, scope.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return _Fail(resolved.Error);
        }

        EvalNode body = _Emit(scope.Body);
        if (_Error is not null)
        {
            return ConstEvalNode.False;
        }

        return new ScopeEvalNode(_BuildScopeRuntime(scope, symbol, body.Evaluate));
    }

    /// <summary>
    /// Anchors a protocol scope on the protocol's own container field when it has one, so that <c>$udp[1]</c> counts
    /// UDP layers rather than every field UDP owns. Protocols without a container field fall back to an owner match.
    /// </summary>
    private static ScopeRuntime _BuildScopeRuntime(ScopeNode scope, FilterSymbol symbol, FilterEvalFn body)
    {
        if (symbol.Kind != FilterSymbolKind.Protocol)
        {
            return new ScopeRuntime(scope.Name, symbol.Fields, ProtocolId.Invalid, scope.Occurrence, body);
        }

        // Keep ProtocolId even when matching on the container field so FindAnchors can consult the packet index and
        // skip the BFS when the protocol is absent.
        if (symbol.ContainerField.IsValid)
        {
            return new ScopeRuntime(scope.Name, [symbol.ContainerField], symbol.ProtocolId, scope.Occurrence, body);
        }

        return new ScopeRuntime(scope.Name, [], symbol.ProtocolId, scope.Occurrence, body);
    }

    #endregion

    #region Helpers

    /// <summary>Records the first error and returns a constant-false node so emission can continue.</summary>
    private ConstEvalNode _Fail(FilterError error)
    {
        _Error ??= error;
        return ConstEvalNode.False;
    }

    #endregion
}
