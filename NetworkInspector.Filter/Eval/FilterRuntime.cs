// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// The intrinsics an emitted filter delegate calls.
/// <para>
/// Code generation only ever produces boolean composition (<c>&amp;&amp;</c>, <c>||</c>, <c>!</c>)
/// plus calls into this class, which keeps the expression trees small and moves all packet
/// knowledge into ordinary, debuggable, testable methods. Every operand is a compile-time
/// constant closed over by the tree.
/// </para>
/// </summary>
internal static class FilterRuntime
{
    #region Presence

    /// <summary>Protocol presence test.</summary>
    public static bool HasProtocol(FilterEvalContext context, ProtocolId protocolId, FieldId containerField) =>
        context.HasProtocol(protocolId, containerField);

    /// <summary>Field or alias-group presence test.</summary>
    public static bool HasField(FilterEvalContext context, FieldId[] fields) =>
        context.HasAnyField(fields);

    #endregion

    #region Value predicates

    /// <summary>Comparison against a literal.</summary>
    public static bool Compare(
        FilterEvalContext context,
        ValueAccessor accessor,
        CompareOp op,
        FieldValueData literal)
    {
        ComparePredicate predicate = new(op, literal);
        return context.AnyValueMatches(accessor, ref predicate);
    }

    /// <summary>Set membership.</summary>
    public static bool InSet(FilterEvalContext context, ValueAccessor accessor, FieldValueData[] values)
    {
        SetPredicate predicate = new(values);
        return context.AnyValueMatches(accessor, ref predicate);
    }

    /// <summary>Inclusive range membership.</summary>
    public static bool InRange(
        FilterEvalContext context,
        ValueAccessor accessor,
        FieldValueData low,
        FieldValueData high)
    {
        RangePredicate predicate = new(low, high);
        return context.AnyValueMatches(accessor, ref predicate);
    }

    /// <summary>Ordinal substring test.</summary>
    public static bool Contains(FilterEvalContext context, ValueAccessor accessor, string needle)
    {
        ContainsPredicate predicate = new(needle);
        return context.AnyValueMatches(accessor, ref predicate);
    }

    /// <summary>Regular-expression test.</summary>
    public static bool Matches(FilterEvalContext context, ValueAccessor accessor, Regex regex)
    {
        MatchesPredicate predicate = new(regex);
        try
        {
            return context.AnyValueMatches(accessor, ref predicate);
        }
        catch (RegexMatchTimeoutException ex)
        {
            context.SetError(FilterError.Runtime($"Regular expression timed out: {ex.Message}"));
            return false;
        }
    }

    #endregion

    #region Flank

    /// <summary>Evaluates one flank expression against the current packet.</summary>
    public static bool Flank(FilterEvalContext context, FlankRuntime flank)
    {
        if (flank.When is FilterEvalFn gate && !gate(context))
        {
            return false;
        }

        CapturePredicate capture = default;
        if (!context.AnyValueMatches(flank.Accessor, ref capture) || !capture.HasValue)
        {
            return false;
        }

        Packet packet = context.Packet;
        return flank.Advance(capture.Captured, packet.Timestamp.AsNanos, packet.Id.Value);
    }

    #endregion

    #region Scope

    /// <summary>Evaluates a scope body over the breadth-first anchor hits of the current domain.</summary>
    public static bool Scope(FilterEvalContext context, ScopeRuntime scope)
    {
        int limit = scope.Occurrence is int occurrence ? occurrence + 1 : 0;
        int count = context.FindAnchors(scope.AnchorFields, scope.AnchorProtocol, limit, out int hitsBase);

        try
        {
            if (scope.Occurrence is int selected)
            {
                if (selected >= count)
                {
                    return false;
                }
                return _EvaluateAt(context, scope, hitsBase, selected);
            }

            for (int i = 0; i < count; i++)
            {
                if (_EvaluateAt(context, scope, hitsBase, i))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            context.ReleaseHits(hitsBase);
        }
    }

    private static bool _EvaluateAt(FilterEvalContext context, ScopeRuntime scope, int hitsBase, int offset)
    {
        Field hit = context.HitAt(hitsBase, offset);
        Field previous = context.PushDomain(hit);
        try
        {
            return scope.Body(context);
        }
        finally
        {
            context.PopDomain(previous);
        }
    }

    #endregion
}
