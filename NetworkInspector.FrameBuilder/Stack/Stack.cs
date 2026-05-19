// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Generic cons-list node holding one layer (<typeparamref name="THead"/>) plus
/// the rest of the stack (<typeparamref name="TTail"/>).
/// </summary>
/// <remarks>
/// <para>
/// <typeparamref name="THead"/> is the most-recently-added layer (the innermost
/// of those added so far during the fluent build).  <typeparamref name="TTail"/>
/// holds the previously-added (outer) layers.
/// </para>
/// <para>
/// Used when the stack contains at least one <see cref="IStatefulLayer"/>
/// or has been mixed up so it cannot prove statelessness statically.  For
/// fully-stateless stacks the parallel <see cref="StatelessStack{THead,TTail}"/>
/// type is used instead so the type system can enforce statelessness at the
/// call site of <c>FrameStack.Build</c>.
/// </para>
/// </remarks>
/// <typeparam name="THead">Most-recently-added layer.</typeparam>
/// <typeparam name="TTail">Previously-added outer layers.</typeparam>
public readonly struct Stack<THead, TTail> : IStackNode
    where THead : struct, IProtocolLayer
    where TTail : struct, IStackNode
{
    /// <summary>The layer at this cons-list node (innermost of the layers added so far).</summary>
    internal readonly THead Head;

    /// <summary>The remainder of the cons-list (outer layers).</summary>
    internal readonly TTail Tail;

    /// <summary>Creates a new cons-list node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Stack(in THead head, in TTail tail)
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
        get => StackHelpers.TryCast<THead, IProvidesMtu>(in Head, out IProvidesMtu? mtu)
            ? Math.Min(mtu.LinkMtu, Tail.MaxFrameLength)
            : Tail.MaxFrameLength;
    }

    /// <inheritdoc />
    public bool HasFragmentable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StackHelpers.Is<THead, IFragmentable>(in Head) || Tail.HasFragmentable;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets, int frameLength, scoped ref PostFixContext ctx)
    {
        Tail.ApplyPostFix(phase, frame, offsets, frameLength, ref ctx);

        int myOffset = offsets[Tail.Depth];
        Head.ApplyPostFix(phase, frame, myOffset, frameLength - myOffset, ref ctx);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor,
        scoped ref SessionState state)
        where TInterceptor : struct, IFrameInterceptor
    {
        // Walk outer→inner: tail first, then this Head.
        int afterTail = Tail.WriteHeaders(dst, offset, offsets, ref interceptor, ref state);

        int headOffset = afterTail;
        offsets[Tail.Depth] = headOffset;
        Span<byte> headerSlice = dst.Slice(headOffset, Head.HeaderSize);

        // JIT-folded dispatch: per concrete THead the JIT picks one branch.
        if (StackHelpers.TryCast<THead, IStatefulLayer>(in Head, out IStatefulLayer? stateful))
        {
            stateful.WriteHeader(headerSlice, ref state);
        }
        else if (StackHelpers.TryCast<THead, IStatelessLayer>(in Head, out IStatelessLayer? stateless))
        {
            stateless.WriteHeader(headerSlice);
        }

        interceptor.OnHeaderWritten(in Head, headerSlice);

        if (Tail.Depth > 0 && StackHelpers.TryCast<THead, IProvidesProtocolType>(in Head, out IProvidesProtocolType? pt))
        {
            int outerOffset = offsets[Tail.Depth - 1];
            Tail.PatchHeadAsOuterFromInner(dst, outerOffset, pt.ProtocolType);
        }

        return headOffset + Head.HeaderSize;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeStatefulState(ref SessionState state)
    {
        // Walk outer→inner: tail first, then Head.
        Tail.InitializeStatefulState(ref state);
        if (StackHelpers.TryCast<THead, IStatefulLayer>(in Head, out IStatefulLayer? statefulInit))
        {
            statefulInit.InitializeState(ref state);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchHeadAsOuterFromInner(scoped Span<byte> frame, int myHeadOffset, ushort innerProtocol)
    {
        if (StackHelpers.TryCast<THead, IConsumesNextProtocolValue>(in Head, out IConsumesNextProtocolValue? provider))
        {
            provider.PatchNextProtocol(frame, myHeadOffset, innerProtocol);
        }
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
        if (StackHelpers.TryCast<THead, IFragmentable>(in Head, out IFragmentable? f))
        {
            int myOffset = offsets[Tail.Depth];
            headerOffset = myOffset;
            headerEndOffset = myOffset + Head.HeaderSize;
            canFragment = f.CanFragment;
            kind = f.FragmentationKind;
            alignment = f.FragmentAlignment;
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
        if (StackHelpers.TryCast<THead, IFragmentable>(in Head, out IFragmentable? pf) && pf.FragmentationKind == activeKind)
        {
            int myOffset = offsets[Tail.Depth];
            pf.PatchFragmentHeader(frame, myOffset, frameDataLength - myOffset, fragmentPayloadOffset, moreFragments);
        }
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

