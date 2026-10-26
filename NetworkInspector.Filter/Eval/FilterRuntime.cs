// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// The shared runtime helpers of the evaluation nodes: regular expressions (with timeout handling), flank trackers,
/// and scopes. Presence and value tests are evaluated by their nodes directly against
/// <see cref="FilterEvalContext"/>.
/// <para>
/// Stateless static helpers; all mutable state lives in the passed <see cref="FilterEvalContext"/> and runtime objects,
/// which belong to one single-threaded filter. Runtime failures are recorded on the context (no exception crosses the
/// per-packet boundary), so evaluation stays on the exception-free hot path.
/// </para>
/// </summary>
internal static class FilterRuntime
{
    #region Value predicates

    /// <summary>Regular-expression test; a timeout is recorded on the context and yields false.</summary>
    public static bool Matches(FilterEvalContext context, ValueAccessor accessor, Regex regex)
    {
        // The predicate is a struct passed by ref into a generic walk: specialized per predicate, no boxing, no delegate.
        MatchesPredicate predicate = new(regex);

        // Only a regex that needed the backtracking engine can time out (see FilterBinder.TryBuildRegex). The timeout is
        // an expected failure, so it becomes a recorded runtime error and a non-match instead of an escaping exception;
        // the filter reads the recorded error after the evaluation and surfaces it as a runtime failure.
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
        // The when: gate decides first: a packet that fails it is neither read nor fed to the tracker, so it cannot arm,
        // fire, or advance the window.
        if (flank.When is FilterEvalFn gate && !gate(context))
        {
            return false;
        }

        // Take the first value of the tracked field (the capture predicate stops the walk at the first value). A packet
        // without the field gives no sample and is skipped without touching the tracker.
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
        // Collect only as many anchors as needed: "$x[i]" needs the first i + 1 hits, so the breadth-first search stops
        // there; the existential form (no index) collects all (limit 0).
        // The parser bounds Occurrence to ArrayIndexIdRange.MaxValue (< int.MaxValue), so "+ 1" cannot overflow.
        int limit = scope.Occurrence is int occurrence ? occurrence + 1 : 0;
        int count = context.FindAnchors(scope.AnchorFields, scope.AnchorProtocol, limit, out int hitsBase);

        // The hits live in a shared stack-like buffer of the context (no per-scope list), so the region above hitsBase must
        // be released on every exit, including an exception from the body; nested scopes rely on that stack discipline.
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

    /// <summary>
    /// Runs the scope body with the domain narrowed to one hit's subtree. The previous domain is restored in
    /// <c>finally</c> so a throwing body cannot leave later siblings (or the caller) evaluating inside the wrong subtree.
    /// </summary>
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
