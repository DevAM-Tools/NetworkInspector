// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// A compiled packet filter.
/// <para>
/// <b>Threading.</b> <see cref="Filter.AlwaysMatch"/> is immutable and safe to share across threads.
/// Every other filter instance is single-threaded: it owns mutable evaluation scratch space, flank
/// state and a match cache, so concurrent calls on one instance are not supported. Use
/// <see cref="TryDerive"/> or a second <see cref="Filter.Compile(string, IStack, FilterCompileOptions?)"/>
/// to obtain an independent instance per worker.
/// </para>
/// <para>
/// <b>Errors.</b> Nothing on this interface throws for an expected failure. Compilation problems
/// are reported by <see cref="Filter.Compile(string, IStack, FilterCompileOptions?)"/>; runtime
/// faults recorded in the eval context (for example regex timeouts) surface through
/// <see cref="TryIsMatch"/> and sticky-poison the instance for classic and flank filters alike.
/// Unexpected exceptions from the JIT root are caught only when <see cref="IsStateful"/> is
/// <see langword="true"/>, so flank state cannot continue dirty; on a classic filter they
/// propagate after the eval context is unbound.
/// </para>
/// </summary>
public interface IFilter
{
    #region Properties

    /// <summary>The expression this filter was compiled from.</summary>
    string Expression
    {
        get;
    }

    /// <summary>Whether this filter accepts every packet without evaluating anything.</summary>
    bool IsAlwaysMatch
    {
        get;
    }

    /// <summary>Whether evaluation carries state across packets and therefore requires ascending packet order.</summary>
    bool IsStateful
    {
        get;
    }

    /// <summary>Whether a runtime error has disabled this filter until <see cref="ResetState"/> is called.</summary>
    bool IsPoisoned
    {
        get;
    }

    /// <summary>The error that poisoned this filter, if any.</summary>
    FilterError? PoisonError
    {
        get;
    }

    /// <summary>The stack this filter was compiled against; <see langword="null"/> for the always-match filter.</summary>
    IStack? Stack
    {
        get;
    }

    #endregion

    #region Evaluation

    /// <summary>
    /// Evaluates one packet without a presence index.
    /// <para>
    /// Verdicts are cached per packet id, so re-querying a packet is cheap and, for stateful
    /// filters, does not replay the state machine. Use <see cref="TryIsMatch{TIndex}"/> when an
    /// index is available so protocol presence stays O(1).
    /// </para>
    /// </summary>
    /// <param name="packet">The packet to test.</param>
    /// <param name="matched">Receives the verdict.</param>
    /// <param name="failure">Receives the error when evaluation failed.</param>
    /// <returns><see langword="true"/> when a verdict was produced.</returns>
    bool TryIsMatch(Packet packet, out bool matched, [NotNullWhen(false)] out FilterError? failure);

    /// <summary>
    /// Evaluates one packet against a typed index reader without boxing struct views.
    /// <para>
    /// Pass <see cref="PacketIndex"/> or <see cref="PacketIndexReaderView"/> as
    /// <typeparamref name="TIndex"/>. Do not cast a view to <see cref="IPacketIndexReader"/>
    /// first — that boxes. The index is ignored when it was not built for the same stack as
    /// <paramref name="packet"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="TIndex">
    /// Concrete reader type. Constrained so <see cref="PacketIndexReaderView"/> stays unboxed
    /// (<c>constrained.callvirt</c>).
    /// </typeparam>
    bool TryIsMatch<TIndex>(Packet packet, TIndex? index, out bool matched, [NotNullWhen(false)] out FilterError? failure)
        where TIndex : IPacketIndexReader;

    /// <summary>
    /// Builds a materialized candidate bitmap by copying presence bitmaps and combining them.
    /// Use only when <paramref name="index"/> is no longer growing — each call clones bitmaps.
    /// While capture is live, use <see cref="TryIsPresenceCandidate{TIndex}"/> instead.
    /// <para>
    /// Do not pass a <see cref="PacketIndexReaderView"/> as <see cref="IPacketIndexReader"/> —
    /// that boxes. Let <typeparamref name="TIndex"/> be the struct or <see cref="PacketIndex"/>.
    /// </para>
    /// </summary>
    bool TryBuildCandidates<TIndex>(TIndex index, [NotNullWhen(true)] out RoaringBitmap? candidates)
        where TIndex : IPacketIndexReader;

    /// <summary>
    /// Tests whether <paramref name="packetId"/> can possibly match using live presence bitmaps.
    /// No bitmap is copied; newly committed packets become visible on later calls without
    /// obtaining a new view. Returns <see langword="false"/> when the expression offers nothing
    /// to prune on (including every stateful filter). When this returns <see langword="true"/>,
    /// <paramref name="isCandidate"/> is the prune verdict — packets with
    /// <paramref name="isCandidate"/> false need not reach <see cref="TryIsMatch"/>.
    /// <para>
    /// Do not pass a <see cref="PacketIndexReaderView"/> as <see cref="IPacketIndexReader"/> —
    /// that boxes. Let <typeparamref name="TIndex"/> be the struct or <see cref="PacketIndex"/>.
    /// </para>
    /// </summary>
    bool TryIsPresenceCandidate<TIndex>(TIndex index, uint packetId, out bool isCandidate)
        where TIndex : IPacketIndexReader;

    #endregion

    #region Lifecycle

    /// <summary>Clears flank state, the match cache and any poison.</summary>
    void ResetState();

    /// <summary>
    /// Re-binds the already parsed expression to another stack and returns a fresh instance with
    /// empty state. The source filter is left untouched, including its poison flag.
    /// </summary>
    bool TryDerive(IStack stack, [NotNullWhen(true)] out Filter? derived, [NotNullWhen(false)] out FilterError? failure);

    #endregion
}
