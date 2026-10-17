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

    // Phase is written only by the lifecycle-controlling thread, read by any thread.
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
    /// </summary>
    internal void SetPhase(SessionPhase phase)
        => _Phase = (int)phase;

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
                    FormattableString.Invariant($"Maximum {what} ID count exceeded (valid range 0..{maxId.ToString(CultureInfo.InvariantCulture)})."));
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
