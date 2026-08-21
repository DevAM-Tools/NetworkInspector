// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

#region Contract

/// <summary>
/// A test applied to one transformed field value.
/// <para>
/// Implementations are <see langword="readonly"/> structs and are passed to
/// <see cref="FilterEvalContext.AnyValueMatches{TPredicate}"/> through a generic type parameter,
/// so the runtime specializes the walk per predicate and the test inlines without boxing or a
/// delegate call.
/// </para>
/// </summary>
internal interface IValuePredicate
{
    /// <summary>Tests one value.</summary>
    bool Test(in FieldValueData value);
}

#endregion

#region Predicates

/// <summary>Relational or equality comparison against a literal.</summary>
internal readonly struct ComparePredicate(CompareOp op, FieldValueData literal) : IValuePredicate
{
    private readonly CompareOp _Op = op;
    private readonly FieldValueData _Literal = literal;

    /// <inheritdoc />
    public bool Test(in FieldValueData value) => FilterCompare.Apply(value, _Op, _Literal);
}

/// <summary>Membership in an explicit value set.</summary>
internal readonly struct SetPredicate(FieldValueData[] values) : IValuePredicate
{
    private readonly FieldValueData[] _Values = values;

    /// <inheritdoc />
    public bool Test(in FieldValueData value)
    {
        foreach (FieldValueData candidate in _Values)
        {
            if (FilterCompare.Compare(value, candidate) == 0)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Membership in an inclusive range.</summary>
internal readonly struct RangePredicate(FieldValueData low, FieldValueData high) : IValuePredicate
{
    private readonly FieldValueData _Low = low;
    private readonly FieldValueData _High = high;

    /// <inheritdoc />
    public bool Test(in FieldValueData value) =>
        FilterCompare.Compare(value, _Low) >= 0 && FilterCompare.Compare(value, _High) <= 0;
}

/// <summary>Ordinal substring test on string values.</summary>
internal readonly struct ContainsPredicate(string needle) : IValuePredicate
{
    private readonly string _Needle = needle;

    /// <inheritdoc />
    public bool Test(in FieldValueData value) =>
        value.Type == FieldType.String
        && value.TryGetAsString(out string text)
        && text.Contains(_Needle, StringComparison.Ordinal);
}

/// <summary>Regular-expression test on string values.</summary>
internal readonly struct MatchesPredicate(Regex regex) : IValuePredicate
{
    private readonly Regex _Regex = regex;

    /// <inheritdoc />
    public bool Test(in FieldValueData value) =>
        value.Type == FieldType.String
        && value.TryGetAsString(out string text)
        && _Regex.IsMatch(text);
}

/// <summary>Captures the first value it sees and always reports a match.</summary>
internal struct CapturePredicate : IValuePredicate
{
    /// <summary>The captured value; only meaningful when <see cref="HasValue"/> is set.</summary>
    public FieldValueData Captured;

    /// <summary>Whether a value was captured.</summary>
    public bool HasValue;

    /// <inheritdoc />
    public bool Test(in FieldValueData value)
    {
        Captured = value;
        HasValue = true;
        return true;
    }
}

#endregion
