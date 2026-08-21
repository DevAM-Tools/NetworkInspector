// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using BindingFlags = System.Reflection.BindingFlags;
using MethodInfo = System.Reflection.MethodInfo;

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// Default back end: lowers the AST to a LINQ expression tree and JIT-compiles it into a
/// <see cref="FilterEvalFn"/>.
/// <para>
/// <b>Shape of the generated code.</b> Only boolean structure is emitted —
/// <c>AndAlso</c>, <c>OrElse</c> and <c>Not</c> — with every leaf lowered to a static call into
/// <see cref="FilterRuntime"/>. All operands (accessors, literals, value sets, compiled regular
/// expressions, flank and scope state objects) are resolved once here and captured as constants,
/// so the per-packet path contains no dictionary lookups, no string handling and no reflection.
/// </para>
/// <para>
/// <b>Short-circuiting.</b> <c>AndAlso</c>/<c>OrElse</c> give the language its documented
/// left-to-right evaluation order, which matters because cheap presence tests are usually written
/// before expensive value tests.
/// </para>
/// <para>
/// <b>Scopes.</b> Each scope body compiles into its own delegate with its own context parameter.
/// The body therefore observes whatever domain the enclosing <see cref="FilterRuntime.Scope"/>
/// call pushed, which is what makes nested scopes work without re-entrancy tricks.
/// </para>
/// </summary>
internal sealed class ExpressionTreeCodegen : IFilterCodegen
{
    #region Fields

    /// <summary>Default bound on <c>matches</c> evaluation; overridable per compilation.</summary>
    private static readonly TimeSpan _DefaultRegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly MethodInfo _HasProtocolMethod = _Method(nameof(FilterRuntime.HasProtocol));
    private static readonly MethodInfo _HasFieldMethod = _Method(nameof(FilterRuntime.HasField));
    private static readonly MethodInfo _CompareMethod = _Method(nameof(FilterRuntime.Compare));
    private static readonly MethodInfo _InSetMethod = _Method(nameof(FilterRuntime.InSet));
    private static readonly MethodInfo _InRangeMethod = _Method(nameof(FilterRuntime.InRange));
    private static readonly MethodInfo _ContainsMethod = _Method(nameof(FilterRuntime.Contains));
    private static readonly MethodInfo _MatchesMethod = _Method(nameof(FilterRuntime.Matches));
    private static readonly MethodInfo _FlankMethod = _Method(nameof(FilterRuntime.Flank));
    private static readonly MethodInfo _ScopeMethod = _Method(nameof(FilterRuntime.Scope));

    private readonly List<FlankRuntime> _Flanks = [];

    private SymbolResolver _Resolver = null!;
    private TimeSpan _RegexTimeout = _DefaultRegexTimeout;
    private FilterError? _Error;

    #endregion

    #region Entry point

    /// <inheritdoc />
    public FilterResult<CompiledFilterProgram> Compile(
        FilterProgram program,
        SymbolResolver resolver,
        FilterCompileOptions? options)
    {
        _Resolver = resolver;
        _RegexTimeout = options?.RegexTimeout ?? _DefaultRegexTimeout;
        _Error = null;
        _Flanks.Clear();

        ParameterExpression context = Expression.Parameter(typeof(FilterEvalContext), "context");
        Expression body = _Emit(program.Root, context);

        if (_Error is not null)
        {
            return FilterResult.Fail<CompiledFilterProgram>(_Error);
        }

        // Every emitter path either produces a well-typed boolean expression or records an error
        // above, so lambda compilation cannot fail on user input at this point.
        FilterEvalFn root = Expression.Lambda<FilterEvalFn>(body, context).Compile();
        return new CompiledFilterProgram(root, [.. _Flanks]);
    }

    #endregion

    #region Emission

    private Expression _Emit(FilterNode node, ParameterExpression context)
    {
        switch (node)
        {
            case BoolConstantNode constant:
                return Expression.Constant(constant.Value);

            case NotNode not:
                return Expression.Not(_Emit(not.Operand, context));

            case LogicalNode logical:
            {
                Expression left = _Emit(logical.Left, context);
                Expression right = _Emit(logical.Right, context);
                return logical.Op == LogicalOp.And
                    ? Expression.AndAlso(left, right)
                    : Expression.OrElse(left, right);
            }

            case PresenceNode presence:
                return _EmitPresence(presence, context);

            case CompareNode compare:
                return _EmitCompare(compare, context);

            case InSetNode inSet:
                return _EmitCall(
                    inSet.Left,
                    context,
                    _InSetMethod,
                    Expression.Constant(inSet.ValueArray));

            case InRangeNode inRange:
                return _EmitCall(
                    inRange.Left,
                    context,
                    _InRangeMethod,
                    Expression.Constant(inRange.Low, typeof(FieldValueData)),
                    Expression.Constant(inRange.High, typeof(FieldValueData)));

            case StringPredicateNode stringPredicate:
                return _EmitStringPredicate(stringPredicate, context);

            case FlankNode flank:
                return _EmitFlank(flank, context);

            case ScopeNode scope:
                return _EmitScope(scope, context);

            default:
                return _Fail(FilterError.Compiler($"Unsupported node '{node.GetType().Name}'"));
        }
    }

    private Expression _EmitPresence(PresenceNode presence, ParameterExpression context)
    {
        FilterResult<FilterSymbol> resolved =
            _Resolver.ResolveAny(presence.Name, presence.Position, presence.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return _Fail(resolved.Error);
        }

        if (symbol.Kind == FilterSymbolKind.Protocol)
        {
            return Expression.Call(
                _HasProtocolMethod,
                context,
                Expression.Constant(symbol.ProtocolId, typeof(ProtocolId)),
                Expression.Constant(symbol.ContainerField, typeof(FieldId)));
        }

        return Expression.Call(_HasFieldMethod, context, Expression.Constant(symbol.Fields));
    }

    private Expression _EmitCompare(CompareNode compare, ParameterExpression context) =>
        _EmitCall(
            compare.Left,
            context,
            _CompareMethod,
            Expression.Constant(compare.Op, typeof(CompareOp)),
            Expression.Constant(compare.Right, typeof(FieldValueData)));

    private Expression _EmitStringPredicate(StringPredicateNode node, ParameterExpression context)
    {
        if (node.Op == StringOp.Contains)
        {
            return _EmitCall(node.Left, context, _ContainsMethod, Expression.Constant(node.Pattern));
        }

        // The parser rejects unparsable patterns before code generation, so construction here
        // cannot fail on syntax; only the timeout differs between the two.
        Regex regex = new(node.Pattern, RegexOptions.CultureInvariant, _RegexTimeout);
        return _EmitCall(node.Left, context, _MatchesMethod, Expression.Constant(regex));
    }

    private Expression _EmitFlank(FlankNode flank, ParameterExpression context)
    {
        FilterResult<FilterSymbol> resolved =
            _Resolver.ResolveValue(flank.FieldName, flank.Position, flank.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return _Fail(resolved.Error);
        }

        if (flank.By is not null)
        {
            FilterError? typeError = _Resolver.CheckIntegerFields(
                symbol,
                flank.FieldName,
                flank.Position,
                flank.Length);
            if (typeError is not null)
            {
                return _Fail(typeError);
            }
        }

        FlankRuntime runtime = new(
            ValueAccessor.Direct(symbol.Fields),
            flank.From,
            flank.To,
            flank.By,
            flank.IsAnyChange,
            flank.Window);

        if (flank.When is FilterNode gate)
        {
            ParameterExpression gateContext = Expression.Parameter(typeof(FilterEvalContext), "context");
            Expression gateBody = _Emit(gate, gateContext);
            if (_Error is not null)
            {
                return Expression.Constant(false);
            }
            runtime.When = Expression.Lambda<FilterEvalFn>(gateBody, gateContext).Compile();
        }

        _Flanks.Add(runtime);
        return Expression.Call(_FlankMethod, context, Expression.Constant(runtime));
    }

    private Expression _EmitScope(ScopeNode scope, ParameterExpression context)
    {
        FilterResult<FilterSymbol> resolved = _Resolver.ResolveAny(scope.Name, scope.Position, scope.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return _Fail(resolved.Error);
        }

        ParameterExpression bodyContext = Expression.Parameter(typeof(FilterEvalContext), "context");
        Expression bodyExpression = _Emit(scope.Body, bodyContext);
        if (_Error is not null)
        {
            return Expression.Constant(false);
        }

        FilterEvalFn body = Expression.Lambda<FilterEvalFn>(bodyExpression, bodyContext).Compile();
        ScopeRuntime runtime = _BuildScopeRuntime(scope, symbol, body);
        return Expression.Call(_ScopeMethod, context, Expression.Constant(runtime));
    }

    /// <summary>
    /// Anchors a protocol scope on the protocol's own container field when it has one, so that
    /// <c>$udp[1]</c> counts UDP layers rather than every field UDP owns. Protocols without a
    /// container field fall back to an owner match.
    /// </summary>
    private static ScopeRuntime _BuildScopeRuntime(ScopeNode scope, FilterSymbol symbol, FilterEvalFn body)
    {
        if (symbol.Kind != FilterSymbolKind.Protocol)
        {
            return new ScopeRuntime(scope.Name, symbol.Fields, ProtocolId.Invalid, scope.Occurrence, body);
        }

        // Keep ProtocolId even when matching on the container field so FindAnchors can
        // consult the packet index and skip the BFS when the protocol is absent.
        if (symbol.ContainerField.IsValid)
        {
            return new ScopeRuntime(
                scope.Name,
                [symbol.ContainerField],
                symbol.ProtocolId,
                scope.Occurrence,
                body);
        }

        return new ScopeRuntime(scope.Name, [], symbol.ProtocolId, scope.Occurrence, body);
    }

    #endregion

    #region Helpers

    private Expression _EmitCall(
        OperandNode operand,
        ParameterExpression context,
        MethodInfo method,
        params Expression[] extraArguments)
    {
        FilterResult<ValueAccessor> accessor = _BuildAccessor(operand);
        if (!accessor.TryGetValue(out ValueAccessor? value))
        {
            return _Fail(accessor.Error);
        }

        Expression[] arguments = new Expression[2 + extraArguments.Length];
        arguments[0] = context;
        arguments[1] = Expression.Constant(value);
        Array.Copy(extraArguments, 0, arguments, 2, extraArguments.Length);
        return Expression.Call(method, arguments);
    }

    private FilterResult<ValueAccessor> _BuildAccessor(OperandNode operand)
    {
        FilterResult<FilterSymbol> resolved =
            _Resolver.ResolveValue(operand.Name, operand.Position, operand.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return FilterResult.Fail<ValueAccessor>(resolved.Error);
        }

        return operand switch
        {
            SliceOperandNode slice => ValueAccessor.Slice(symbol.Fields, slice.Start, slice.End),
            LengthOperandNode => ValueAccessor.Length(symbol.Fields),
            _ => ValueAccessor.Direct(symbol.Fields),
        };
    }

    private ConstantExpression _Fail(FilterError error)
    {
        if (_Error is null)
        {
            _Error = error;
        }
        return Expression.Constant(false);
    }

    private static MethodInfo _Method(string name) =>
        typeof(FilterRuntime).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"FilterRuntime.{name} is missing.");

    #endregion
}
