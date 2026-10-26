// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// One in-flight use of a session <see cref="Stack"/>: while the lease is held, the session does not dispose the stack,
/// even when a <see cref="Session.Restart"/> has already swapped it out. Dispose the lease exactly once (a <c>using</c>
/// declaration) as soon as the parse that needed the stack is finished; the last release of a retired stack disposes it.
///
/// <para>
/// A <c>ref struct</c> on purpose (misuse prevention): it cannot be stored in a field, captured by a lambda or an async
/// method, boxed, or handed to another thread, so a lease cannot outlive the call that took it and cannot be released by a
/// thread that does not know it. Taking it allocates nothing. Code that must hold a lease across threads (tests) uses
/// <see cref="RetirableStack.TryAcquire()"/> and <see cref="RetirableStack.Release"/> directly.
/// </para>
///
/// <para>Thread-safety: a lease belongs to the thread that took it; the stack it guards is shared.</para>
/// </summary>
internal readonly ref struct StackLease
{
    #region Fields

    private readonly RetirableStack? _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Wraps an acquisition that <see cref="RetirableStack.TryAcquire()"/> counted.</summary>
    internal StackLease(RetirableStack owner) => _Owner = owner;

    /// <summary>Gives the lease back. Disposing the default lease (a refused acquisition) does nothing.</summary>
    internal void Dispose() => _Owner?.Release();

    #endregion

    #region Properties

    /// <summary>The leased stack. Only valid on a lease that was granted.</summary>
    internal Stack Stack => _Owner!.Stack; // Non-null: a granted lease always has an owner; the default lease is never read.

    #endregion
}

/// <summary>
/// Acquires a <see cref="StackLease"/> for a read. <see langword="false"/> when the session cannot serve reads right now
/// (restart or shutdown in progress); <paramref name="lease"/> is then the default lease.
/// </summary>
/// <param name="lease">The granted lease; dispose it when the parse is finished.</param>
internal delegate bool TryLeaseStack(out StackLease lease);
