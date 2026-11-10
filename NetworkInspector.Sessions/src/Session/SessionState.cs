// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Session-internal mutable state: lifecycle phase and ID generators.
/// All operations are lock-free (Volatile / Interlocked); every allocator is safe to call from any thread.
/// </summary>
internal sealed class SessionState
{
    #region Fields

    // Written after the last ID (ArrayIndexIdRange.MaxValue) is handed out; any negative value means exhausted.
    private const int _IdsExhaustedSentinel = int.MinValue;

    // Phase is written by the lifecycle thread (Start, Restart, Shutdown) and by the thread of the last finishing
    // source job, and read by any thread. The lifecycle calls are sequenced by the session (lifecycle lock) and store with
    // SetPhase. A source thread, which runs concurrently with them, only ever moves Running to Stopped, and does it with a
    // compare-exchange (TryTransition) so it cannot overwrite a ShuttingDown or Restarting published a moment earlier.
    private volatile int _Phase = (int)SessionPhase.Idle;

    // Next ID to hand out (0 … ArrayIndexIdRange.MaxValue). Never wraps past MaxValue.
    // Not volatile: each counter is passed by ref to _Allocate, and a volatile field passed by ref loses its
    // volatile semantics (CS0420). Every access goes through Volatile.Read or Interlocked inside _Allocate,
    // which gives the same cross-thread visibility as the keyword.
    private int _NextListenerId;
    private int _NextJobId;
    private int _NextValueCacheId;

    #endregion

    #region API

    /// <summary>Current session phase. Volatile read — always up to date.</summary>
    internal SessionPhase Phase => (SessionPhase)_Phase;

    /// <summary>
    /// Transitions the session phase.
    /// Called from the session coordinator and from source-job completion when the last source finishes.
    /// The store is unconditional (no expected-value check), for callers that own the phase at that moment (the lifecycle
    /// calls under the lifecycle lock). A caller that may race with another phase writer uses
    /// <see cref="TryTransition"/> instead: a check of <see cref="Phase"/> followed by this store is not atomic.
    /// </summary>
    internal void SetPhase(SessionPhase phase)
        => _Phase = (int)phase;

    /// <summary>
    /// Moves the phase from <paramref name="expected"/> to <paramref name="next"/> in one atomic step, and only if the phase
    /// still is <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// Why: a check followed by <see cref="SetPhase"/> can overwrite a phase that another thread published in between (the
    /// last source thread would store Stopped over a ShuttingDown or Restarting that was just published). The conditional
    /// transition leaves the newer phase alone, and its owner then finishes the lifecycle step it started.
    /// </remarks>
    /// <returns><see langword="true"/> when the phase was <paramref name="expected"/> and is now <paramref name="next"/>.</returns>
    internal bool TryTransition(SessionPhase expected, SessionPhase next)
        => Interlocked.CompareExchange(ref _Phase, (int)next, (int)expected) == (int)expected;

    /// <summary>
    /// Enters <see cref="SessionPhase.ShuttingDown"/> from whatever phase is current, except <see cref="SessionPhase.Stopped"/>,
    /// which stays (a stopped session only needs its slots cancelled). Atomic against the last source thread storing Stopped.
    /// </summary>
    /// <returns><see langword="false"/> when the session was already Stopped and the phase was left unchanged.</returns>
    internal bool TryEnterShuttingDown()
    {
        // Retry on a lost race: the phase read below is only a candidate for the compare-exchange. A phase that changed
        // meanwhile (Running became Stopped) is re-read and judged again.
        while (true)
        {
            SessionPhase current = Phase;
            if (current == SessionPhase.Stopped)
            {
                return false;
            }

            if (TryTransition(current, SessionPhase.ShuttingDown))
            {
                return true;
            }
        }
    }

    /// <summary>Allocates the next unique job ID. Thread-safe.</summary>
    /// <exception cref="SessionException"><see cref="SessionErrorCode.JobIdExhausted"/> after the last valid ID.</exception>
    internal JobId AllocateJobId() => new(_Allocate(ref _NextJobId, SessionErrorCode.JobIdExhausted, "job"));

    /// <summary>Allocates the next unique listener ID. Thread-safe.</summary>
    /// <exception cref="SessionException"><see cref="SessionErrorCode.ListenerIdExhausted"/> after the last valid ID.</exception>
    internal ListenerId AllocateListenerId() =>
        new(_Allocate(ref _NextListenerId, SessionErrorCode.ListenerIdExhausted, "listener"));

    /// <summary>Allocates the next unique value-cache ID. Thread-safe. Independent of listener IDs.</summary>
    /// <exception cref="SessionException"><see cref="SessionErrorCode.ValueCacheIdExhausted"/> after the last valid ID.</exception>
    internal ValueCacheId AllocateValueCacheId() =>
        new(_Allocate(ref _NextValueCacheId, SessionErrorCode.ValueCacheIdExhausted, "value-cache"));

    #endregion

    #region Private helpers

    /// <summary>
    /// Hands out <paramref name="next"/> and advances it. The last valid ID flips the counter to the
    /// negative sentinel, so the allocation after it fails instead of wrapping into negative IDs.
    /// </summary>
    /// <param name="next">The counter of one ID sequence. Accessed only through Volatile.Read and Interlocked.</param>
    /// <param name="code">Error code thrown once the sequence is exhausted.</param>
    /// <param name="what">Name of the sequence for the error message.</param>
    private static int _Allocate(ref int next, SessionErrorCode code, string what)
    {
        // The ids index arrays (Array.MaxLength bound), so the last valid id is the shared range limit, not int.MaxValue.
        int maxId = Core.Ids.ArrayIndexIdRange.MaxValue;

        // CAS loop: a lost race re-reads the counter. After another thread claimed the last ID, the re-read
        // yields the sentinel and the loop exits through the exhaustion check.
        while (true)
        {
            int current = Volatile.Read(ref next);
            if (current < 0)
            {
                throw new SessionException(
                    code,
                    Invariant($"Maximum {what} ID count exceeded (valid range 0..{maxId.ToString(CultureInfo.InvariantCulture)})."));
            }

            int replacement = current == maxId ? _IdsExhaustedSentinel : current + 1;
            if (Interlocked.CompareExchange(ref next, replacement, current) == current)
            {
                return current;
            }
        }
    }

    #endregion
}
