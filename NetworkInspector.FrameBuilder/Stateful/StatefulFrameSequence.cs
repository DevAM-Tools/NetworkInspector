// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Iterator over the frames produced by a single
/// <see cref="Session{TStack,TTrailer,TInterceptor}.NextPacket(System.ReadOnlySpan{byte})"/>
/// call when the cons-list contains at least one <see cref="IStatefulLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Runs the same build engine as <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>;
/// the only difference is that the write walk receives the session's
/// <see cref="SessionState"/> so stateful layer heads can read and update their slot.
/// </para>
/// <para>
/// Single-frame fast path: when the unfragmented frame fits the smallest MTU
/// asserted along the cons-list, the iterator emits exactly one frame —
/// <see cref="MoveNext"/> returns <c>true</c> once, then <c>false</c>.
/// </para>
/// <para>
/// Multi-fragment path: the stateful write walk runs once into a per-thread
/// scratch buffer, so per-flow counters such as IPv4 Identification or TCP
/// sequence advance exactly once per logical packet, not once per fragment.
/// </para>
/// <para>
/// <see cref="MoveNext"/> is guaranteed throw-free.  All expected runtime
/// situations are surfaced via <see cref="Status"/> and a <c>false</c> return:
/// <see cref="BuildStatus.BufferTooSmall"/>,
/// <see cref="BuildStatus.FragmentationRequired"/>,
/// <see cref="BuildStatus.StackTooDeep"/>,
/// <see cref="BuildStatus.InvalidLayerState"/>,
/// <see cref="BuildStatus.SessionDisposed"/>.  Callers detect normal
/// completion versus failure by reading <see cref="Status"/> after
/// <see cref="MoveNext"/> returns <c>false</c>: a value of
/// <see cref="BuildStatus.Success"/> means the sequence drained cleanly,
/// any other value identifies the failure.  A failure before the first frame of
/// the packet leaves the session counters unchanged.
/// </para>
/// <para>
/// Lifetime: declare the sequence with <c>using</c> (see
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>).  Disposing the
/// owning session ends the sequence: the next <see cref="MoveNext"/> returns
/// <c>false</c> with <see cref="BuildStatus.SessionDisposed"/> and does not
/// touch the state of a session that re-rented the pooled internals.
/// </para>
/// <para>Thread safety: instance is single-use and not thread-safe.</para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list type carried into the iterator.</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public ref struct StatefulFrameSequence<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    #region Fields

    /// <summary>Hard upper bound on cons-list depth (matches the stateless sequence).</summary>
    public const int MaxSupportedDepth = FrameLimits.MaxSupportedDepth;

    /// <summary>Shared build state machine bound to the session's state.</summary>
    private FrameEngine<TStack, TTrailer, TInterceptor, SessionSequenceState> _Engine;

    #endregion

    #region Lifecycle

    /// <summary>Creates a new iterator for the stateful path.</summary>
    /// <param name="values">Cons-list values of the session.</param>
    /// <param name="trailer">Trailer of the session.</param>
    /// <param name="interceptor">Interceptor of the session.</param>
    /// <param name="state">Session-owned state; mutated during the write walk.</param>
    /// <param name="version">Live version counter of the session internals (stale detection).</param>
    /// <param name="payload">Packet payload.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StatefulFrameSequence(
        TStack values,
        TTrailer trailer,
        TInterceptor interceptor,
        ref SessionState state,
        ref readonly uint version,
        ReadOnlySpan<byte> payload)
        => _Engine = new(values, trailer, interceptor, new SessionSequenceState(ref state, in version), payload);

    /// <inheritdoc cref="FrameSequence{TStack,TTrailer,TInterceptor}.Dispose"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => _Engine.Dispose();

    #endregion

    #region Public API

    /// <summary>
    /// Outcome of the build operation.  Read after <see cref="MoveNext"/>
    /// returns <c>false</c> to distinguish normal completion
    /// (<see cref="BuildStatus.Success"/>) from any of the surfaced error
    /// conditions.
    /// </summary>
    public readonly BuildStatus Status => _Engine.Status;

    /// <inheritdoc cref="FrameSequence{TStack,TTrailer,TInterceptor}.MoveNext"/>
    public bool MoveNext(Span<byte> dst, out int bytesWritten) => _Engine.MoveNext(dst, out bytesWritten);

    #endregion
}
