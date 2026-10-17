// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// <see cref="ISequenceState"/> for <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/>:
/// a reference to the <see cref="SessionState"/> owned by the session's pooled
/// internals plus the version that detects a disposed or re-rented session.
/// </summary>
/// <remarks>
/// Thread safety: not thread-safe; lives on the stack of the thread that drives the
/// sequence, like the owning <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </remarks>
internal ref struct SessionSequenceState : ISequenceState
{
    #region Fields

    /// <summary>Session-owned state; mutated by the write walk.</summary>
    private readonly ref SessionState _State;

    /// <summary>Live version counter of the owning pooled internals.</summary>
    private readonly ref readonly uint _Version;

    /// <summary>Version captured when the sequence was created.</summary>
    private readonly uint _OpenVersion;

    /// <summary>Counters captured by <see cref="BeginPacket"/> for <see cref="RollbackPacket"/>.</summary>
    private SessionState _Snapshot;

    #endregion

    #region Lifecycle

    /// <summary>Binds the state of the session that is creating the sequence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SessionSequenceState(ref SessionState state, ref readonly uint version)
    {
        _State = ref state;
        _Version = ref version;
        _OpenVersion = version;
        _Snapshot = default;
    }

    #endregion

    #region ISequenceState

    /// <inheritdoc />
    public readonly bool IsSessionLive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Version == _OpenVersion;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginPacket(int payloadLength)
    {
        // Stateful layers (TCP sequence, TcpStream) advance by the payload length.
        _State.CurrentPayloadLength = payloadLength;
        _Snapshot = _State;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void RollbackPacket() => _State = _Snapshot;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int WriteHeaders<TStack, TInterceptor>(
        in TStack values,
        scoped Span<byte> dst,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TStack : struct, IStackNode
        where TInterceptor : struct, IFrameInterceptor
        => values.WriteHeaders(dst, 0, offsets, ref interceptor, ref _State);

    #endregion
}
