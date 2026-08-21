// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

#region Constants and presence

/// <summary>A literal <c>true</c> or <c>false</c>.</summary>
internal sealed class BoolConstantNode(bool value, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The constant value.</summary>
    public bool Value { get; } = value;
}

/// <summary>
/// A bare name used as a presence test: a protocol (<c>tcp</c>), a canonical field
/// (<c>tcp.flags</c>) or an alias group (<c>eth.addr</c>).
/// </summary>
internal sealed class PresenceNode(string name, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The referenced protocol, field or alias name.</summary>
    public string Name { get; } = name;
}

#endregion

#region Boolean composition

/// <summary>Logical negation.</summary>
internal sealed class NotNode(FilterNode operand, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The negated predicate.</summary>
    public FilterNode Operand { get; } = operand;
}

/// <summary>Short-circuiting <c>&amp;&amp;</c> / <c>||</c>.</summary>
internal sealed class LogicalNode(LogicalOp op, FilterNode left, FilterNode right, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The connective.</summary>
    public LogicalOp Op { get; } = op;

    /// <summary>Left operand, always evaluated first.</summary>
    public FilterNode Left { get; } = left;

    /// <summary>Right operand, evaluated only when the connective does not short-circuit.</summary>
    public FilterNode Right { get; } = right;
}

#endregion

#region Value predicates

/// <summary>A comparison between an operand and a literal, e.g. <c>udp.port == 53</c>.</summary>
internal sealed class CompareNode(OperandNode left, CompareOp op, FieldValueData right, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The value-producing left-hand side.</summary>
    public OperandNode Left { get; } = left;

    /// <summary>The comparison operator.</summary>
    public CompareOp Op { get; } = op;

    /// <summary>The literal right-hand side.</summary>
    public FieldValueData Right { get; } = right;
}

/// <summary>A set membership test, e.g. <c>tcp.port in {80, 443}</c>.</summary>
internal sealed class InSetNode(OperandNode left, FieldValueData[] values, int position, int length)
    : FilterNode(position, length)
{
    private readonly FieldValueData[] _Values = values;

    /// <summary>The value-producing left-hand side.</summary>
    public OperandNode Left { get; } = left;

    /// <summary>The candidate values.</summary>
    public ReadOnlySpan<FieldValueData> Values => _Values;

    /// <summary>The candidate values as the backing array (used by the code generator).</summary>
    public FieldValueData[] ValueArray => _Values;
}

/// <summary>An inclusive range test, e.g. <c>tcp.port in 1024..65535</c>.</summary>
internal sealed class InRangeNode(
    OperandNode left,
    FieldValueData low,
    FieldValueData high,
    int position,
    int length)
    : FilterNode(position, length)
{
    /// <summary>The value-producing left-hand side.</summary>
    public OperandNode Left { get; } = left;

    /// <summary>Inclusive lower bound.</summary>
    public FieldValueData Low { get; } = low;

    /// <summary>Inclusive upper bound.</summary>
    public FieldValueData High { get; } = high;
}

/// <summary>A text predicate, e.g. <c>http.host contains "foo"</c>.</summary>
internal sealed class StringPredicateNode(
    OperandNode left,
    StringOp op,
    string pattern,
    int position,
    int length)
    : FilterNode(position, length)
{
    /// <summary>The value-producing left-hand side.</summary>
    public OperandNode Left { get; } = left;

    /// <summary>The text predicate kind.</summary>
    public StringOp Op { get; } = op;

    /// <summary>The substring or regular-expression pattern.</summary>
    public string Pattern { get; } = pattern;
}

#endregion

#region Scope

/// <summary>
/// A subtree scope: <c>$Name { F }</c> or <c>$Name[i] { F }</c>.
/// <para>
/// <see cref="Occurrence"/> is <see langword="null"/> for the existential form (any BFS hit
/// whose subtree satisfies <see cref="Body"/>), or a 0-based BFS hit index for the
/// bracketed form. A bracketed index beyond the number of hits evaluates to
/// <see langword="false"/> rather than raising an error.
/// </para>
/// </summary>
internal sealed class ScopeNode(string name, int? occurrence, FilterNode body, int position, int length)
    : FilterNode(position, length)
{
    /// <summary>The anchor name to locate with breadth-first search.</summary>
    public string Name { get; } = name;

    /// <summary>Optional 0-based BFS hit index.</summary>
    public int? Occurrence { get; } = occurrence;

    /// <summary>The predicate evaluated with the hit's subtree as the active domain.</summary>
    public FilterNode Body { get; } = body;
}

#endregion
