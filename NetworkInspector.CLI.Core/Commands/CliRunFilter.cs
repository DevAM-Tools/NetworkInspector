// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// The <c>--filter</c> of one CLI run in the form the parse plan needs.
/// <para>
/// <b>Preferred form:</b> a <see cref="FilterObserver"/> that judges the packet during a tree-free parse. Its demand is
/// <see cref="FilterObserver.ParseDemand"/>: depth <see cref="ParseDepth.Packet"/> with the extraction watch, so no lazy
/// container can hide a field from it. A run with such a filter and a tree-free exporter builds no field tree at all.
/// </para>
/// <para>
/// <b>Fallback form:</b> the tree filter with a <see cref="PacketIndex"/>, used when the expression needs the field tree (a
/// subtree scope, or the presence of a protocol without a container field). Its demand is <see cref="ParseDepth.FieldTree"/>
/// and the run says why (<see cref="FallbackReason"/>), so a user can tell why an export is slow.
/// </para>
/// <para>
/// <b>Why the observer is tried first:</b> any observer compile failure (a subtree scope, a second <c>flank()</c>, a
/// protocol-presence term) falls back to the tree filter when that one compiles; a syntax error fails both and is reported once,
/// with the tree compiler's message (the one users know). A session export job applies the identical rule, so the CLI and a
/// session accept and judge the same expressions.
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; the one loop of the command owns it.</para>
/// </summary>
internal sealed class CliRunFilter
{
    #region Fields

    private readonly FilterObserver? _Observer;
    private readonly IFilter? _Tree;
    private readonly PacketIndex? _Index;

    #endregion

    #region Constructors

    private CliRunFilter(FilterObserver observer)
    {
        _Observer = observer;
        Demand = observer.ParseDemand;
    }

    private CliRunFilter(IFilter tree, PacketIndex index, string? fallbackReason)
    {
        _Tree = tree;
        _Index = index;
        FallbackReason = fallbackReason;

        // The tree filter reads protocol presence from the index the parse fills, so the index is part of the demand.
        Demand = new ParseDemand { Depth = ParseDepth.FieldTree, Index = index };
    }

    #endregion

    #region Properties

    /// <summary>
    /// The demand of this filter, placed first in every plan so that the filter sees each packet before the exporter does.
    /// Observer form: the filter observer's demand; tree form: a field tree with the index.
    /// </summary>
    internal ParseDemand Demand { get; }

    /// <summary>Why the run builds a field tree for the filter; <see langword="null"/> for the observer form.</summary>
    internal string? FallbackReason { get; }

    #endregion

    #region Internal API

    /// <summary>
    /// Compiles <paramref name="expression"/>; writes one error line and returns <see langword="false"/> when neither form
    /// compiles.
    /// </summary>
    /// <param name="expression">The filter expression; not blank.</param>
    /// <param name="stack">The stack whose protocols and fields the expression refers to.</param>
    /// <param name="error">Receives the one error line.</param>
    /// <param name="filter">The compiled filter; <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="filter"/> is set.</returns>
    internal static bool TryCompile(string expression, Stack stack, TextWriter error, [NotNullWhen(true)] out CliRunFilter? filter)
    {
        FilterObserverOptions observerOptions = FilterObserverOptions.Default;
        if (PacketFilter.TryCompile(expression, stack, in observerOptions, out FilterObserver? observer, out FilterError? observerFailure))
        {
            filter = new CliRunFilter(observer);
            return true;
        }

        if (!CliFilter.TryCompile(expression, stack, error, out IFilter? treeFilter))
        {
            filter = null;
            return false;
        }

        filter = new CliRunFilter(treeFilter, new PacketIndex(stack), observerFailure.Message);
        return true;
    }

    /// <summary>Wraps an already compiled tree filter (the form callers of the loops have always passed).</summary>
    /// <param name="tree">The compiled tree filter.</param>
    /// <param name="stack">The stack of the run; the index is created for it.</param>
    /// <returns>The run filter, in tree form without a fallback reason.</returns>
    internal static CliRunFilter FromTree(IFilter tree, Stack stack) => new(tree, new PacketIndex(stack), fallbackReason: null);

    /// <summary>
    /// Reads the verdict of the packet that was just parsed with the plan. Observer form: <c>TryReadMatch</c> (valid until the next
    /// <c>BeginPacket</c>); tree form: <see cref="CliFilter.TryMatch{TIndex}"/> with the index. A failure is written as one line and
    /// <see langword="false"/> is returned: the caller aborts, because a filter that cannot decide must not keep or drop data
    /// silently.
    /// </summary>
    /// <param name="packet">The packet parsed with the plan that contains <see cref="Demand"/>.</param>
    /// <param name="error">Receives the failure as one line.</param>
    /// <param name="matched">Whether the packet matches; <see langword="false"/> when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the filter produced a verdict.</returns>
    internal bool TryMatch(Packet packet, TextWriter error, out bool matched)
    {
        if (_Observer is not null)
        {
            if (_Observer.TryReadMatch(out matched, out FilterError? failure))
            {
                return true;
            }

            string reason = string.Empty;
            if (failure is not null)
            {
                reason = failure.Message;
            }

            error.WriteLine(Invariant($"Error: filter evaluation failed: {reason}"));
            matched = false;
            return false;
        }

        return CliFilter.TryMatch(_Tree!, packet, _Index, error, out matched); // Invariant: exactly one form is set.
    }

    #endregion
}
