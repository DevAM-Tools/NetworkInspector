// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// One node of a compiled filter. Logic nodes short-circuit left to right; leaves call the evaluation context or the
/// shared <see cref="FilterRuntime"/> helpers. Every operand is a typed field resolved at compile time.
/// <para>
/// <b>Why a node tree.</b> The previous back end generated code from an expression tree at every compile, which dominated
/// the cost of compiling a filter. Building these objects is plain allocation, and evaluation stays as fast: every node is
/// <c>sealed</c> and holds its operands in typed fields, so the call is direct, and <see cref="ValueEvalNode{TPredicate}"/> is
/// generic over a struct predicate so the JIT inlines the test into the value walk. See
/// <see cref="EvalTreeCodegen"/> for the shape and the evaluation order.
/// </para>
/// <para><b>Thread-safety:</b> immutable after compilation; flank and scope leaves carry the single-threaded state of
/// their filter.</para>
/// </summary>
internal abstract class EvalNode
{
    #region Public API

    /// <summary>Evaluates the node for the packet bound to <paramref name="context"/>.</summary>
    public abstract bool Evaluate(FilterEvalContext context);

    #endregion
}

/// <summary>Conjunction; the right side runs only when the left side is true.</summary>
internal sealed class AndEvalNode(EvalNode left, EvalNode right) : EvalNode
{
    #region Fields

    private readonly EvalNode _Left = left;
    private readonly EvalNode _Right = right;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => _Left.Evaluate(context) && _Right.Evaluate(context);

    #endregion
}

/// <summary>Disjunction; the right side runs only when the left side is false.</summary>
internal sealed class OrEvalNode(EvalNode left, EvalNode right) : EvalNode
{
    #region Fields

    private readonly EvalNode _Left = left;
    private readonly EvalNode _Right = right;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => _Left.Evaluate(context) || _Right.Evaluate(context);

    #endregion
}

/// <summary>Negation.</summary>
internal sealed class NotEvalNode(EvalNode operand) : EvalNode
{
    #region Fields

    private readonly EvalNode _Operand = operand;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => !_Operand.Evaluate(context);

    #endregion
}

/// <summary>A boolean constant; the two instances are shared.</summary>
internal sealed class ConstEvalNode : EvalNode
{
    #region Constants

    /// <summary>The constant <c>true</c>.</summary>
    internal static readonly ConstEvalNode True = new(value: true);

    /// <summary>The constant <c>false</c>.</summary>
    internal static readonly ConstEvalNode False = new(value: false);

    #endregion

    #region Fields

    private readonly bool _Value;

    #endregion

    #region Constructors

    private ConstEvalNode(bool value) => _Value = value;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => _Value;

    #endregion
}

/// <summary>Protocol presence (index, container field, or owner scan; see <see cref="FilterEvalContext.HasProtocol"/>).</summary>
internal sealed class ProtocolEvalNode(ProtocolId protocolId, FieldId containerField) : EvalNode
{
    #region Fields

    private readonly ProtocolId _ProtocolId = protocolId;
    private readonly FieldId _ContainerField = containerField;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => context.HasProtocol(_ProtocolId, _ContainerField);

    #endregion
}

/// <summary>Field or alias-group presence.</summary>
internal sealed class FieldPresenceEvalNode(FieldId[] fields) : EvalNode
{
    #region Fields

    private readonly FieldId[] _Fields = fields;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => context.HasAnyField(_Fields);

    #endregion
}

/// <summary>
/// A value test (comparison, set, range, contains): one operand accessor and one predicate, evaluated over every value
/// the operand produces in the current domain. Specialized on the predicate type, so the value walk inlines the test.
/// </summary>
/// <typeparam name="TPredicate">The predicate; an immutable struct.</typeparam>
internal sealed class ValueEvalNode<TPredicate>(ValueAccessor accessor, TPredicate predicate) : EvalNode
    where TPredicate : struct, IValuePredicate
{
    #region Fields

    private readonly ValueAccessor _Accessor = accessor;

    // Not readonly: AnyValueMatches takes the predicate by ref. Every predicate used here is immutable.
    private TPredicate _Predicate = predicate;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => context.AnyValueMatches(_Accessor, ref _Predicate);

    #endregion
}

/// <summary>A regular-expression test; a timeout is recorded on the context by <see cref="FilterRuntime.Matches"/>.</summary>
internal sealed class MatchesEvalNode(ValueAccessor accessor, Regex regex) : EvalNode
{
    #region Fields

    private readonly ValueAccessor _Accessor = accessor;
    private readonly Regex _Regex = regex;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => FilterRuntime.Matches(context, _Accessor, _Regex);

    #endregion
}

/// <summary>A flank tracker; its <c>when:</c> gate is a node delegate on the runtime.</summary>
internal sealed class FlankEvalNode(FlankRuntime flank) : EvalNode
{
    #region Fields

    private readonly FlankRuntime _Flank = flank;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => FilterRuntime.Flank(context, _Flank);

    #endregion
}

/// <summary>A scope; its body is a node delegate on the runtime.</summary>
internal sealed class ScopeEvalNode(ScopeRuntime scope) : EvalNode
{
    #region Fields

    private readonly ScopeRuntime _Scope = scope;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public override bool Evaluate(FilterEvalContext context) => FilterRuntime.Scope(context, _Scope);

    #endregion
}
