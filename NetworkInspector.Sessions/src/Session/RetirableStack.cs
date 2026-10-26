// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// A <see cref="Stack"/> of a <see cref="Session"/> together with the count of operations that are using it right now (its
/// leases) and the rule for when a replaced stack may be disposed: as soon as it is retired and the last lease ended.
///
/// <para>
/// <b>Why:</b> <see cref="Session.Restart"/> replaces the stack while lock-free reads (<c>TryGetPacket</c>,
/// <c>ReadPackets</c>, listener and value-cache pulls) may still be re-parsing on the old one. <see cref="Stack.Dispose"/>
/// runs every protocol's <c>OnShutdown</c> and releases the settings locks, so a parse that is still running would use shut
/// down protocol instances and a disposed lock. Disposing under the parse mutex does not help (readers do not take it), and
/// keeping every retired stack until <c>Shutdown</c> would hold its memory and delay <c>OnShutdown</c> for the whole session.
/// Counting the users closes the gap at the price of two interlocked operations per read call.
/// </para>
///
/// <para>
/// <b>State word:</b> one <see cref="int"/>: bit 30 is the retired flag, bits 0..29 count the leases. Acquire increments,
/// release decrements, retire sets the flag; each is one atomic read-modify-write, so "retired and no lease" is exactly the
/// value of the flag alone and exactly one transition can observe it. An acquisition that finds the flag set is refused and
/// undoes its increment (that undo may be the last release, which then disposes). The disposal itself runs behind a separate
/// once gate, so racing last-releasers, a retire and a forced disposal still dispose exactly once. The count of leases is
/// bounded by the number of threads that are inside a read (a lease never outlives its call), far below 2^30, so the count
/// cannot spill into the flag bit.
/// </para>
///
/// <para>
/// <b>Rejected alternatives:</b> a mutex or reader-writer lock around the use (takes a lock on every read, and a lock must
/// not be held while a protocol parses); an epoch scheme (needs per-thread registration and a reclaimer thread for a rare
/// event); a compare-exchange loop for the acquire (an extra retry under contention for no gain, because the undo path is
/// rare).
/// </para>
///
/// <para>
/// <b>Thread-safety:</b> every member is safe from any thread. The disposal runs on the thread that makes the last transition
/// (a reader, the restarting thread or the shutting down thread), outside every session lock, and never throws.
/// </para>
/// </summary>
internal sealed class RetirableStack
{
    #region Fields

    // The retired flag. Above the lease bits, and not the sign bit, so a lease count that is read as an int stays positive.
    private const int _RetiredBit = 1 << 30;

    // Bits 0..29: the number of leases (including a refused acquisition's increment until it is undone).
    private const int _LeaseMask = _RetiredBit - 1;

    // Lease count and retired flag, see the type remarks. Only changed with Interlocked (volatile for the plain reads).
    private volatile int _State;

    // 0 = not disposed, 1 = disposal started. The once gate: the last release, a retire on an idle stack and a forced
    // disposal may all race for the disposal, exactly one wins.
    private volatile int _Disposed;

    // Monitor and flag behind WaitUntilDisposed: the flag is set (and waiters pulsed) after the disposal ran and was reported.
    // Only Shutdown ever waits (kernel wait with its time budget, no polling). A monitor instead of a ManualResetEventSlim,
    // because an event is IDisposable and a waiter may still be inside Wait when the last user lets go, so it could never be
    // disposed safely. The section is a flag store; a disposing reader holds it for nanoseconds and waits for nothing.
    private readonly object _DoneGate = new();
    private bool _DisposalDone; // Guarded by _DoneGate.

    // What disposes the stack: Stack.Dispose in production, a probe in tests.
    private readonly Action<Stack> _DisposeStack;

    // Told once, after the disposal, with everything the disposal reported (empty when it was clean).
    private readonly Action<RetirableStack, Exception[]> _OnDisposed;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Wraps <paramref name="stack"/>.
    /// </summary>
    /// <param name="stack">The stack to guard.</param>
    /// <param name="ownedBySession">
    /// <see langword="true"/> when the session created the stack (a restart factory built it) and therefore disposes it;
    /// <see langword="false"/> for the stack the caller passed to the session constructor, which the caller disposes.
    /// </param>
    /// <param name="onDisposed">
    /// Called exactly once after the stack was disposed, with the exceptions of the disposal (empty when none); not called
    /// for a stack the session does not own. Runs on the disposing thread and must not block.
    /// </param>
    /// <param name="disposeStack">What disposes the stack; <see langword="null"/> uses <see cref="Stack.Dispose"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> or <paramref name="onDisposed"/> is <see langword="null"/>.</exception>
    internal RetirableStack(
        Stack stack,
        bool ownedBySession,
        Action<RetirableStack, Exception[]> onDisposed,
        Action<Stack>? disposeStack = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(onDisposed);
        Stack = stack;
        OwnedBySession = ownedBySession;
        _OnDisposed = onDisposed;
        _DisposeStack = disposeStack ?? _DefaultDispose;
    }

    #endregion

    #region Properties

    /// <summary>The guarded stack. Usable by whoever holds a lease, and for lookups at any time (the stack tables are immutable).</summary>
    internal Stack Stack { get; }

    /// <summary>Whether the session disposes <see cref="Stack"/> (it built it) or the caller does.</summary>
    internal bool OwnedBySession { get; }

    /// <summary>Whether the stack was retired: no further lease is granted.</summary>
    internal bool IsRetired => (_State & _RetiredBit) != 0;

    /// <summary>Whether the disposal started (finished or running).</summary>
    internal bool IsDisposed => _Disposed != 0;

    /// <summary>The number of leases right now. Diagnostic: it can include an acquisition that is about to be undone.</summary>
    internal int LeaseCount => _State & _LeaseMask;

    #endregion

    #region Leases

    /// <summary>
    /// Takes a lease unless the stack is retired. One interlocked increment, no allocation, no lock.
    /// </summary>
    /// <param name="lease">The lease; dispose it when the use ended. The default lease when the stack is retired.</param>
    /// <returns><see langword="false"/> when the stack is retired (the caller re-reads the session's current stack).</returns>
    internal bool TryAcquire(out StackLease lease)
    {
        if (!TryAcquire())
        {
            lease = default;
            return false;
        }

        lease = new StackLease(this);
        return true;
    }

    /// <summary>
    /// Takes a lease without the <see cref="StackLease"/> wrapper, for a holder that must keep it across threads. The caller
    /// calls <see cref="Release"/> exactly once.
    /// </summary>
    /// <returns><see langword="false"/> when the stack is retired.</returns>
    internal bool TryAcquire()
    {
        // Optimistic: increment first, look at the flag in the result. A retire that comes later sees this lease and defers
        // the disposal to its release; a retire that came earlier shows in the result and the lease is refused.
        int after = Interlocked.Increment(ref _State);
        if ((after & _RetiredBit) == 0)
        {
            return true;
        }

        // Retired: nobody may start using the stack. The increment is undone, and the undo can be the one that makes the
        // stack idle (the real users may have left in the meantime), in which case it disposes.
        Release();
        return false;
    }

    /// <summary>
    /// Ends a lease. When the stack is retired and this was the last lease, disposes it on the calling thread.
    /// </summary>
    internal void Release()
    {
        int after = Interlocked.Decrement(ref _State);

        // Retired flag set and not a single lease left: exactly the value of the flag. A concurrent acquire that already
        // incremented again is a refused one (it undoes itself and reaches this line with the same result).
        if (after == _RetiredBit)
        {
            _DisposeOnce();
        }
    }

    #endregion

    #region Retirement

    /// <summary>
    /// Marks the stack retired (no further lease is granted) and disposes it now when no lease is held; otherwise the last
    /// release disposes it. Idempotent.
    /// </summary>
    internal void Retire()
    {
        // The returned value is the state before the flag: 0 means "not retired, no lease", the only case in which nobody
        // else will ever see the idle state, so this call has to dispose. With a lease held, its release will; when the flag
        // was already set, the first retire dealt with it.
        int before = Interlocked.Or(ref _State, _RetiredBit);
        if (before == 0)
        {
            _DisposeOnce();
        }
    }

    /// <summary>
    /// Disposes the stack now whether leases are held or not. The last resort of a shutdown whose time budget ran out:
    /// a read that is still inside the stack then uses a disposed one (see <see cref="Session.Shutdown"/> for the residual
    /// risk). Idempotent; a release that comes later does not dispose again.
    /// </summary>
    internal void DisposeNow()
    {
        _ = Interlocked.Or(ref _State, _RetiredBit);
        _DisposeOnce();
    }

    /// <summary>
    /// Waits until the disposal ran and was reported.
    /// </summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits without limit.</param>
    /// <returns><see langword="true"/> when the stack is disposed.</returns>
    internal bool WaitUntilDisposed(TimeSpan timeout)
    {
        // Deadline once, so a spurious or early wake-up waits only for what is left. An infinite timeout never expires.
        long start = Stopwatch.GetTimestamp();
        lock (_DoneGate)
        {
            while (!_DisposalDone)
            {
                TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                    ? Timeout.InfiniteTimeSpan
                    : timeout - Stopwatch.GetElapsedTime(start);
                if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                _ = Monitor.Wait(_DoneGate, remaining);
            }

            return true;
        }
    }

    #endregion

    #region Private helpers

    /// <summary>The production disposal.</summary>
    private static void _DefaultDispose(Stack stack) => stack.Dispose();

    /// <summary>
    /// Disposes the stack once and reports what the disposal reported. Runs outside every session lock; a failure is
    /// converted to a reported error because the thread that happens to run it (a reader that finishes a read) cannot handle it.
    /// </summary>
    private void _DisposeOnce()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // The signal is sent even when the report throws, so a shutdown that waits for this stack is never left hanging.
        try
        {
            if (OwnedBySession)
            {
                _OnDisposed(this, _DisposeAndCollect());
            }
        }
        finally
        {
            lock (_DoneGate)
            {
                _DisposalDone = true;
                Monitor.PulseAll(_DoneGate);
            }
        }
    }

    /// <summary>
    /// Runs the disposal and gathers its failures: an exception that escaped <see cref="Stack.Dispose"/>, and the protocol
    /// shutdown errors the stack kept in <see cref="Stack.ShutdownDiagnostics"/> (the stack cannot throw them, see CA1065).
    /// </summary>
    private Exception[] _DisposeAndCollect()
    {
        Exception? escaped = null;
        try
        {
            _DisposeStack(Stack);
        }
        catch (Exception ex)
        {
            // Top-level boundary of a disposal that may run on any thread: the exception is not lost, it is handed to the
            // session, which reports it from Shutdown.
            escaped = ex;
        }

        ReadOnlySpan<Exception> diagnostics = Stack.ShutdownDiagnostics.Span;
        if (escaped is null && diagnostics.IsEmpty)
        {
            return [];
        }

        int extra = escaped is null
            ? 0
            : 1;
        Exception[] errors = new Exception[diagnostics.Length + extra];
        diagnostics.CopyTo(errors);
        if (escaped is not null)
        {
            errors[^1] = escaped;
        }

        return errors;
    }

    #endregion
}
