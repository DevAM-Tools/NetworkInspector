// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Stateless variant of <see cref="Stack{THead,TTail}"/>: every component is an
/// <see cref="IStatelessLayer"/>.  Carries <see cref="IStatelessStack"/> so the
/// stateless-only <c>CreatedStack&lt;TStack,…&gt;.Build(...)</c> overloads can constrain against it
/// at compile time.
/// </summary>
/// <typeparam name="THead">Most-recently-added stateless layer.</typeparam>
/// <typeparam name="TTail">Previously-added stateless cons-list.</typeparam>
public readonly struct StatelessStack<THead, TTail> : IStackNode, IStatelessStack
    where THead : struct, IStatelessLayer
    where TTail : struct, IStackNode, IStatelessStack
{
    /// <summary>The layer at this cons-list node (innermost of the layers added so far).</summary>
    internal readonly THead Head;

    /// <summary>The remainder of the cons-list (outer stateless layers).</summary>
    internal readonly TTail Tail;

    /// <summary>Creates a new stateless cons-list node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StatelessStack(in THead head, in TTail tail)
    {
        Head = head;
        Tail = tail;
    }

    /// <inheritdoc />
    public int Depth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Tail.Depth + 1;
    }

    /// <inheritdoc />
    public int TotalHeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Head.HeaderSize + Tail.TotalHeaderSize;
    }

    /// <inheritdoc />
    public int MaxFrameLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StackHelpers.TryGetLinkMtu(in Head, out ushort linkMtu)
            ? Math.Min(linkMtu, Tail.MaxFrameLength)
            : Tail.MaxFrameLength;
    }

    /// <inheritdoc />
    public bool HasFragmentable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StackHelpers.IsFragmentable(in Head) || Tail.HasFragmentable;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TInterceptor : struct, IFrameInterceptor
    {
        // Walk the tail first so the OUTER layers are written before this one.
        int afterTail = Tail.WriteHeaders(dst, offset, offsets, ref interceptor);

        // This node's header sits at afterTail.  Tail.Depth gives our outer→inner index.
        int headOffset = afterTail;
        offsets[Tail.Depth] = headOffset;
        Span<byte> headerSlice = dst.Slice(headOffset, Head.HeaderSize);
        Head.WriteHeader(headerSlice);

        // Per-layer interceptor hook.  For TInterceptor == NoInterceptor the
        // JIT erases the whole call.  For typed interceptors the layer value
        // is passed by-reference so 'typeof(TLayer) == typeof(SomeLayer)'
        // becomes a JIT-time constant inside the specialised code.
        interceptor.OnHeaderWritten(in Head, headerSlice);

        // Patch the immediately-outer layer's next-protocol field with Head's
        // protocol type.  The probe folds to a constant per concrete THead and
        // never boxes; see the StackHelpers remarks for the required shape.
        if (Tail.Depth > 0 && StackHelpers.TryGetProtocolType(in Head, out ushort protocolType))
        {
            int outerOffset = offsets[Tail.Depth - 1];
            Tail.PatchHeadAsOuterFromInner(dst, outerOffset, protocolType);
        }

        return headOffset + Head.HeaderSize;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets, int frameLength, scoped ref PostFixContext ctx)
    {
        // Walk outer→inner: tail first (outer), then this Head.
        Tail.ApplyPostFix(phase, frame, offsets, frameLength, ref ctx);

        int myOffset = offsets[Tail.Depth];
        Head.ApplyPostFix(phase, frame, myOffset, frameLength - myOffset, ref ctx);
    }

    /// <summary>
    /// Finalises this stateless cons-list as a
    /// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> without trailer
    /// or interceptor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CreatedStack<StatelessStack<THead, TTail>, NoTrailer, NoInterceptor> CreateWithFixedValues()
        => new(in this, default, default);

    /// <summary>
    /// Finalises this stateless cons-list with an explicit interceptor instance and no trailer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CreatedStack<StatelessStack<THead, TTail>, NoTrailer, TInterceptor> CreateWithFixedValues<TInterceptor>(in TInterceptor interceptor)
        where TInterceptor : struct, IFrameInterceptor
        => new(in this, default, in interceptor);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchHeadAsOuterFromInner(scoped Span<byte> frame, int myHeadOffset, ushort innerProtocol)
        => StackHelpers.PatchNextProtocol(in Head, frame, myHeadOffset, innerProtocol);

    /// <inheritdoc />
    /// <remarks>
    /// Stateless cons-list participating in a session walk: ignores
    /// <paramref name="state"/> and forwards to the optimal stateless overload
    /// so the JIT can erase the per-node 'is IStatefulLayer' branch entirely.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor,
        scoped ref SessionState state)
        where TInterceptor : struct, IFrameInterceptor
        => WriteHeaders(dst, offset, offsets, ref interceptor);

    /// <inheritdoc />
    /// <remarks>No-op: a fully-stateless cons-list has no per-layer state.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeStatefulState(ref SessionState state)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetFragmentableInfo(
        scoped ReadOnlySpan<int> offsets,
        out int headerOffset, out int headerEndOffset,
        out bool canFragment, out FragmentationKind kind, out int alignment)
    {
        // Walk outer→inner so the innermost fragmentable wins.
        bool found = Tail.TryGetFragmentableInfo(offsets, out headerOffset, out headerEndOffset, out canFragment, out kind, out alignment);
        if (StackHelpers.TryGetFragmentation(in Head, out bool headCanFragment, out FragmentationKind headKind, out int headAlignment))
        {
            int myOffset = offsets[Tail.Depth];
            headerOffset = myOffset;
            headerEndOffset = myOffset + Head.HeaderSize;
            canFragment = headCanFragment;
            kind = headKind;
            alignment = headAlignment;
            found = true;
        }
        return found;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentable(
        scoped Span<byte> frame, scoped ReadOnlySpan<int> offsets,
        int frameDataLength, int fragmentPayloadOffset,
        bool moreFragments, FragmentationKind activeKind)
    {
        Tail.PatchFragmentable(frame, offsets, frameDataLength, fragmentPayloadOffset, moreFragments, activeKind);
        int myOffset = offsets[Tail.Depth];
        StackHelpers.PatchFragmentHeader(in Head, frame, myOffset, frameDataLength - myOffset, fragmentPayloadOffset, moreFragments, activeKind);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFixUpTo(
        FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets,
        int frameLength, scoped ref PostFixContext ctx, int maxHeaderEndOffset)
    {
        Tail.ApplyPostFixUpTo(phase, frame, offsets, frameLength, ref ctx, maxHeaderEndOffset);

        int myOffset = offsets[Tail.Depth];
        if (myOffset + Head.HeaderSize <= maxHeaderEndOffset)
        {
            Head.ApplyPostFix(phase, frame, myOffset, frameLength - myOffset, ref ctx);
        }
    }
}
