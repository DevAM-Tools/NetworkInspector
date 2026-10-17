// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// <see cref="ISequenceState"/> for stateless stacks driven by
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>: no counters, nothing to
/// snapshot, never disposed.
/// </summary>
/// <remarks>Thread safety: stateless and immutable.</remarks>
internal readonly struct NoSessionState : ISequenceState
{
    /// <inheritdoc />
    public bool IsSessionLive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginPacket(int payloadLength)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RollbackPacket()
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every node of a stateless cons-list forwards the stateful overload to its
    /// stateless walk and never reads the state, so a throw-away default value is
    /// enough.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WriteHeaders<TStack, TInterceptor>(
        in TStack values,
        scoped Span<byte> dst,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TStack : struct, IStackNode
        where TInterceptor : struct, IFrameInterceptor
    {
        SessionState unused = default;
        return values.WriteHeaders(dst, 0, offsets, ref interceptor, ref unused);
    }
}
