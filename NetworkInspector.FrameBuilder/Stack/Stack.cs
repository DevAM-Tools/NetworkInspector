// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
        StackHelpers.WriteHeader(in Head, headerSlice, ref state);

        interceptor.OnHeaderWritten(in Head, headerSlice);

        if (Tail.Depth > 0 && StackHelpers.TryGetProtocolType(in Head, out ushort protocolType))
        {
            int outerOffset = offsets[Tail.Depth - 1];
            Tail.PatchHeadAsOuterFromInner(dst, outerOffset, protocolType);
        }

        return headOffset + Head.HeaderSize;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeStatefulState(ref SessionState state)
    {
        // Walk outer→inner: tail first, then Head.
        Tail.InitializeStatefulState(ref state);
        StackHelpers.InitializeState(in Head, ref state);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchHeadAsOuterFromInner(scoped Span<byte> frame, int myHeadOffset, ushort innerProtocol)
        => StackHelpers.PatchNextProtocol(in Head, frame, myHeadOffset, innerProtocol);

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

