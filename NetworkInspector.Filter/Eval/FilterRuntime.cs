// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// The shared runtime helpers of the evaluation nodes: regular expressions (with timeout handling), flank trackers,
/// and scopes. Presence and value tests are evaluated by their nodes directly against
/// <see cref="FilterEvalContext"/>.
/// </summary>
internal static class FilterRuntime
{
    #region Value predicates

    /// <summary>Regular-expression test; a timeout is recorded on the context and yields false.</summary>
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
        // The parser bounds Occurrence to ArrayIndexIdRange.MaxValue (< int.MaxValue), so "+ 1" cannot overflow.
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
