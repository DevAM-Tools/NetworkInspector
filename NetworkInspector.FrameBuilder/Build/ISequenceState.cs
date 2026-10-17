// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// How the shared build engine reaches per-flow state.  <see cref="NoSessionState"/>
/// drives stateless stacks; <see cref="SessionSequenceState"/> drives a
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// The engine is generic over the implementation (<c>allows ref struct</c>), so
/// every call is a constrained call the JIT specialises and inlines; there is no
/// interface dispatch and no boxing on the build path.
/// </remarks>
internal interface ISequenceState
{
    /// <summary>
    /// <c>false</c> once the session that produced the sequence was disposed (and
    /// possibly re-rented), so the engine must not touch its state any more.
    /// </summary>
    bool IsSessionLive
    {
        get;
    }

    /// <summary>
    /// Called once per logical packet, directly before the write walk: publishes the
    /// payload length to stateful layers and snapshots the counters so
    /// <see cref="RollbackPacket"/> can undo the walk.
    /// </summary>
    void BeginPacket(int payloadLength);

    /// <summary>
    /// Restores the counters captured by <see cref="BeginPacket"/>.  Called when the
    /// build fails before any frame of the packet was handed to the caller.
    /// </summary>
    void RollbackPacket();

    /// <summary>Runs the write walk of <paramref name="values"/> with this state.</summary>
    int WriteHeaders<TStack, TInterceptor>(
        in TStack values,
        scoped Span<byte> dst,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TStack : struct, IStackNode
        where TInterceptor : struct, IFrameInterceptor;
}
