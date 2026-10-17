// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Iterator over the frames produced by a single
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}.Build(System.ReadOnlySpan{byte})"/>
/// call.  Yields one frame per <see cref="MoveNext"/> for the non-fragmenting
/// path and one frame per fragment for stacks that contain an
/// <see cref="IFragmentable"/> layer with <see cref="IFragmentable.CanFragment"/>
/// set.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MoveNext"/> is guaranteed throw-free.  Erroneous runtime
/// situations are surfaced via <see cref="Status"/> and a <c>false</c> return;
/// after <c>false</c>, <see cref="BuildStatus.Success"/> means the sequence
/// drained cleanly.
/// </para>
/// <para>
/// Fragmentation strategy ("build once, slice many"): the unfragmented frame
/// is materialised exactly once into a per-thread scratch buffer.  For
/// <see cref="FragmentationKind.NetworkLayer"/> the inner-of-fragmentable
/// checksums (UDP/TCP) cover the complete datagram and travel in fragment 0;
/// each further <see cref="MoveNext"/> copies the cached headers, appends the
/// next payload slice and re-runs the length / outer-checksum / trailer phases.
/// For <see cref="FragmentationKind.ApplicationSegmentation"/> every emitted
/// segment is a complete datagram with its own lengths and inner checksum.
/// </para>
/// <para>
/// Lifetime: declare the sequence with <c>using</c>.  A fragmenting build
/// reserves the calling thread's pooled scratch until the last fragment is
/// emitted; <see cref="Dispose"/> releases it when the caller stops early.
/// Without it an abandoned fragmenting build keeps the scratch reserved and
/// every later fragmenting build on that thread allocates a private buffer.
/// </para>
/// <para>Thread safety: instance is single-use and not thread-safe.</para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list type carried into the iterator.</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public ref struct FrameSequence<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode, IStatelessStack
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    #region Fields

    /// <summary>
    /// Hard upper bound on cons-list depth.  Stacks deeper than this set
    /// <see cref="BuildStatus.StackTooDeep"/> instead of allocating.
    /// </summary>
    /// <remarks>Mirrors <see cref="FrameLimits.MaxSupportedDepth"/>.</remarks>
    internal const int MaxSupportedDepth = FrameLimits.MaxSupportedDepth;

    /// <summary>Shared build state machine; stateless stacks carry no per-flow state.</summary>
    private FrameEngine<TStack, TTrailer, TInterceptor, NoSessionState> _Engine;

    #endregion

    #region Lifecycle

    /// <summary>Creates a new iterator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FrameSequence(TStack values, TTrailer trailer, TInterceptor interceptor, ReadOnlySpan<byte> payload)
        => _Engine = new(values, trailer, interceptor, default, payload);

    /// <summary>
    /// Ends the sequence and releases the per-thread scratch reservation of a
    /// fragmenting build that was not drained.  Idempotent; a drained sequence has
    /// already released it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => _Engine.Dispose();

    #endregion

    #region Public API

    /// <summary>Outcome of the build operation; set during <see cref="MoveNext"/>.</summary>
    public readonly BuildStatus Status => _Engine.Status;

    /// <summary>
    /// Writes the next frame into <paramref name="dst"/>.  Returns <c>true</c>
    /// if a frame was written and sets <paramref name="bytesWritten"/> to the
    /// frame length.  Returns <c>false</c> when the sequence is exhausted or
    /// <see cref="Status"/> indicates an error.
    /// </summary>
    /// <remarks>
    /// Guaranteed throw-free.  All expected runtime situations are surfaced via
    /// <see cref="Status"/>: <see cref="BuildStatus.BufferTooSmall"/>,
    /// <see cref="BuildStatus.FragmentationRequired"/>,
    /// <see cref="BuildStatus.StackTooDeep"/>,
    /// <see cref="BuildStatus.InvalidLayerState"/>.
    /// </remarks>
    /// <param name="dst">Destination buffer for the frame.</param>
    /// <param name="bytesWritten">Frame length in bytes when the call returns <c>true</c>.</param>
    public bool MoveNext(Span<byte> dst, out int bytesWritten) => _Engine.MoveNext(dst, out bytesWritten);

    #endregion
}
