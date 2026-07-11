// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Session-internal mutable state: lifecycle phase and ID generators.
/// All operations are lock-free (Volatile / Interlocked).
/// </summary>
internal sealed class SessionState
{
    // Phase is written only by the lifecycle-controlling thread, read by any thread.
    private int _Phase = (int)SessionPhase.Idle;

    // ID generators — Interlocked.Increment, no lock needed.
    private long _NextListenerId;
    private long _NextJobId;

    /// <summary>Current session phase. Volatile read — always up to date.</summary>
    internal SessionPhase Phase => (SessionPhase)Volatile.Read(ref _Phase);

    /// <summary>
    /// Transitions the session phase.
    /// Called from the session coordinator and from source-job completion when the last source finishes.
    /// </summary>
    internal void SetPhase(SessionPhase phase)
        => Volatile.Write(ref _Phase, (int)phase);

    /// <summary>Allocates the next unique job ID. Thread-safe.</summary>
    internal JobId AllocateJobId()
    {
        long next = Interlocked.Increment(ref _NextJobId);
        if (next > int.MaxValue)
        {
            throw new InvalidOperationException("Maximum job ID count exceeded.");
        }

        return new((int)next - 1);
    }

    /// <summary>Allocates the next unique listener ID. Thread-safe.</summary>
    internal ListenerId AllocateListenerId()
    {
        long next = Interlocked.Increment(ref _NextListenerId);
        if (next > int.MaxValue)
        {
            throw new InvalidOperationException("Maximum listener ID count exceeded.");
        }

        return new((int)next - 1);
    }
}
