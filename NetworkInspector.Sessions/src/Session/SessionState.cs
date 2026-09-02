// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Session-internal mutable state: lifecycle phase and ID generators.
/// All operations are lock-free (Volatile / Interlocked).
/// </summary>
internal sealed class SessionState
{
    // Written after the last ID (ArrayIndexIdRange.MaxValue) is handed out; any negative value means exhausted.
    private const int _IdsExhaustedSentinel = int.MinValue;

    // Phase is written only by the lifecycle-controlling thread, read by any thread.
    private volatile int _Phase = (int)SessionPhase.Idle;

    // Next ID to hand out (0 … ArrayIndexIdRange.MaxValue). Never wraps past MaxValue.
    private volatile int _NextListenerId;
    private volatile int _NextJobId;
    private volatile int _NextValueCacheId;

    /// <summary>Current session phase. Volatile read — always up to date.</summary>
    internal SessionPhase Phase => (SessionPhase)_Phase;

    /// <summary>
    /// Transitions the session phase.
    /// Called from the session coordinator and from source-job completion when the last source finishes.
    /// </summary>
    internal void SetPhase(SessionPhase phase)
        => _Phase = (int)phase;

    /// <summary>Allocates the next unique job ID. Thread-safe.</summary>
    internal JobId AllocateJobId()
    {
        int maxId = Core.Ids.ArrayIndexIdRange.MaxValue;

        while (true)
        {
            int current = _NextJobId;
            if (current < 0)
            {
                throw new SessionException(
                    SessionErrorCode.JobIdExhausted,
                    $"Maximum job ID count exceeded (valid range 0..{maxId.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (current == maxId)
            {
                if (Interlocked.CompareExchange(ref _NextJobId, _IdsExhaustedSentinel, maxId) == maxId)
                {
                    return new JobId(maxId);
                }

                // Another thread claimed the last ID. Re-reading now yields the sentinel, so the
                // loop exits through the exhaustion check above.
                continue;
            }

            if (Interlocked.CompareExchange(ref _NextJobId, current + 1, current) == current)
            {
                return new JobId(current);
            }
        }
    }

    /// <summary>Allocates the next unique listener ID. Thread-safe.</summary>
    internal ListenerId AllocateListenerId()
    {
        int maxId = Core.Ids.ArrayIndexIdRange.MaxValue;

        while (true)
        {
            int current = _NextListenerId;
            if (current < 0)
            {
                throw new SessionException(
                    SessionErrorCode.ListenerIdExhausted,
                    $"Maximum listener ID count exceeded (valid range 0..{maxId.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (current == maxId)
            {
                if (Interlocked.CompareExchange(ref _NextListenerId, _IdsExhaustedSentinel, maxId) == maxId)
                {
                    return new ListenerId(maxId);
                }

                // Another thread claimed the last ID. Re-reading now yields the sentinel, so the
                // loop exits through the exhaustion check above.
                continue;
            }

            if (Interlocked.CompareExchange(ref _NextListenerId, current + 1, current) == current)
            {
                return new ListenerId(current);
            }
        }
    }

    /// <summary>Allocates the next unique value-cache ID. Thread-safe. Independent of listener IDs.</summary>
    internal ValueCacheId AllocateValueCacheId()
    {
        int maxId = Core.Ids.ArrayIndexIdRange.MaxValue;

        while (true)
        {
            int current = _NextValueCacheId;
            if (current < 0)
            {
                throw new SessionException(
                    SessionErrorCode.ValueCacheIdExhausted,
                    $"Maximum value-cache ID count exceeded (valid range 0..{maxId.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (current == maxId)
            {
                if (Interlocked.CompareExchange(ref _NextValueCacheId, _IdsExhaustedSentinel, maxId) == maxId)
                {
                    return new ValueCacheId(maxId);
                }

                continue;
            }

            if (Interlocked.CompareExchange(ref _NextValueCacheId, current + 1, current) == current)
            {
                return new ValueCacheId(current);
            }
        }
    }
}
