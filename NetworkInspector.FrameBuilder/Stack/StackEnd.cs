// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Terminating node of every cons-list.  All recursive operations bottom out here.
/// </summary>
public readonly struct StackEnd : IStackNode, IStatelessStack
{
    /// <inheritdoc />
    public int Depth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 0;
    }

    /// <inheritdoc />
    public int TotalHeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 0;
    }

    /// <inheritdoc />
    public int MaxFrameLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => int.MaxValue;
    }

    /// <inheritdoc />
    public bool HasFragmentable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => false;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TInterceptor : struct, IFrameInterceptor
        => offset;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets, int frameLength, scoped ref PostFixContext ctx)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchHeadAsOuterFromInner(scoped Span<byte> frame, int myHeadOffset, ushort innerProtocol)
    {
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
        => offset;

    /// <inheritdoc />
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
        headerOffset = 0;
        headerEndOffset = 0;
        canFragment = false;
        kind = FragmentationKind.NetworkLayer;
        alignment = 8;
        return false;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PatchFragmentable(
        scoped Span<byte> frame, scoped ReadOnlySpan<int> offsets,
        int frameDataLength, int fragmentPayloadOffset,
        bool moreFragments, FragmentationKind activeKind)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFixUpTo(
        FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets,
        int frameLength, scoped ref PostFixContext ctx, int maxHeaderEndOffset)
    {
    }
}
