// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// The stack of one session generation together with its number, as the session handed it out. Immutable: a view of
/// generation N stays a view of N after a restart
/// (<see cref="ISession.TryRestart(Func{FrameInterfaceRegistry, Stack}, out RestartReport?, out SessionFailure?)"/>);
/// the caller decides when to take the next one.
///
/// <para>
/// <b>Why a value and not a forwarding proxy:</b> a proxy that re-points to the new stack would make a field id resolved on
/// the old stack silently valid against the new one (ids are dense counters per stack, so id 17 names another field after a
/// restart) and would reintroduce a time-of-check/time-of-use gap between <c>GetFieldId</c> and <c>GetField</c>. An immutable
/// pair cannot drift. Lookups on <see cref="Stack"/> stay valid after the stack was disposed (its tables are immutable); a
/// parse needs the session's lease, see
/// <see cref="ISessionReader.TryGetPacket(PacketId, in ParseOptions, StackView, Packet?, out Packet?, out SessionFailure?)"/>.
/// </para>
///
/// <para>
/// <b>Residual risk:</b> a caller that keeps a view for a long time and parses with its stack outside the session holds no
/// lease; the stack may be disposed once a restart replaced it. The view documents that; it does not prevent it.
/// </para>
///
/// <para>Thread-safety: immutable value; safe to copy and share.</para>
/// </summary>
/// <param name="Stack">The stack of this generation. Never null for a view the session returned.</param>
/// <param name="Generation">The generation number: 0 for the constructor stack, +1 per successful swap.</param>
public readonly record struct StackView(Stack Stack, int Generation)
{
    #region API

    /// <summary>
    /// Whether this view still is the current generation of <paramref name="session"/>: the session's current stack and
    /// generation (one volatile load of its holder, no allocation) equal this view's. A default view, and a view that was taken
    /// from another session, are never current: comparing the stack as well as the number is what makes that so (two sessions
    /// both start at generation 0).
    /// </summary>
    /// <param name="session">The session to compare with.</param>
    /// <returns><see langword="true"/> when the session has not swapped its stack since this view was taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public bool IsCurrent(ISessionReader session)
    {
        ArgumentNullException.ThrowIfNull(session);
        StackView current = session.CurrentStack;
        return current.Generation == Generation && ReferenceEquals(current.Stack, Stack);
    }

    #endregion
}
