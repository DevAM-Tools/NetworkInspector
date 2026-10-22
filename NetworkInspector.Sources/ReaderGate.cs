// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources;

/// <summary>
/// Lets any number of threads read a resource that another thread may release (the memory-mapped view of a capture,
/// the state of a source) without a counter or lock that all readers write. A reader marks its own thread's
/// <see cref="ReaderSlot"/>; <see cref="Close"/> waits until no other thread's slot names this gate. The caller
/// releases the resource after <see cref="Close"/> returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> a per-call reference count or a reader lock writes one cache line shared by all readers; four reader
/// threads were no faster than one. A slot is written only by its own thread.
/// </para>
/// <para>
/// <b>Protocol (Dekker with full fences on both sides):</b> a reader publishes the gate in its slot with
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> (full fence) and then reads the closed flag; <see cref="Close"/>
/// sets the flag with <see cref="Interlocked.Exchange(ref int, int)"/> (full fence) and then reads every slot. One of
/// the two always sees the other: the reader sees the gate closed and leaves, or <see cref="Close"/> sees the slot and
/// waits for <see cref="Exit"/>.
/// </para>
/// <para>
/// <b>Nesting:</b> a slot holds one gate. A second section on the same thread (no caller nests today) uses the gate's
/// shared overflow counter and is recorded per thread, so <see cref="Close"/> still recognises its own thread.
/// </para>
/// <para>
/// <b>Scope rule:</b> no user code may run inside a section (no events, no callbacks). A thread inside a section that
/// calls <see cref="Close"/> on the same gate is a bug; it throws instead of waiting for itself.
/// </para>
/// <para><b>Memory:</b> two <see cref="int"/> fields per gate; one 40-byte <see cref="ReaderSlot"/> per reading thread.</para>
/// <para><b>Thread-safety:</b> all members are thread-safe. <see cref="TryEnter"/>/<see cref="Exit"/> pairs run on one thread in LIFO order.</para>
/// </remarks>
internal sealed class ReaderGate
{
    #region Fields

    /// <summary>0 = open, 1 = closed. Set once by <see cref="Close"/> with a full fence.</summary>
    private volatile int _Closed;

    /// <summary>Sections that did not fit a slot (nesting). <see cref="Interlocked"/> only.</summary>
    private int _OverflowReaders;

    #endregion

    #region Properties

    /// <summary>True once <see cref="Close"/> started. Any thread.</summary>
    internal bool IsClosed => _Closed != 0;

    #endregion

    #region Public API

    /// <summary>
    /// Starts a read section on the calling thread. False once the gate is closed; nothing is held then and
    /// <see cref="Exit"/> must not be called. Every true result needs exactly one <see cref="Exit"/> on this thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryEnter(out ReaderGateToken token)
    {
        ReaderSlot slot = ReaderSlot.Current;
        if (!slot.TryPublish(this))
        {
            return _TryEnterOverflow(out token);
        }

        // The publish above is a full fence, so this read cannot move before it (see the protocol in the remarks).
        if (_Closed != 0)
        {
            slot.Clear();
            token = default;
            return false;
        }

        token = default;
        return true;
    }

    /// <summary>Ends the section started by the <see cref="TryEnter"/> that returned <paramref name="token"/>. Same thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Exit(in ReaderGateToken token)
    {
        if (token.IsOverflow)
        {
            ReaderSlot.PopOverflow(this);
            _ = Interlocked.Decrement(ref _OverflowReaders);
            return;
        }

        ReaderSlot.Current.Clear();
    }

    /// <summary>
    /// Closes the gate: no new section starts, and the call returns once no other thread is inside a section. The
    /// caller then releases the protected resource. Called once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The gate is already closed, or the calling thread is inside a section of this gate (it would wait for itself).
    /// </exception>
    internal void Close()
    {
        if (Interlocked.Exchange(ref _Closed, 1) != 0)
        {
            throw new InvalidOperationException("The reader gate is already closed.");
        }

        // A section of this thread would never end while this thread waits: a broken caller invariant, not a race.
        if (ReaderSlot.CurrentHolds(this))
        {
            throw new InvalidOperationException("A thread inside a read section cannot close the same reader gate.");
        }

        // Sections that started before the flag was set: slots first, then the overflow counter.
        ReaderSlot.WaitWhileOtherThreadsHold(this);
        SpinWait spin = default;
        while (Volatile.Read(ref _OverflowReaders) != 0)
        {
            spin.SpinOnce();
        }
    }

    #endregion

    #region Private helpers

    /// <summary>Nested section: shared counter (full fence), then the closed check, as for a slot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool _TryEnterOverflow(out ReaderGateToken token)
    {
        _ = Interlocked.Increment(ref _OverflowReaders);
        if (_Closed != 0)
        {
            _ = Interlocked.Decrement(ref _OverflowReaders);
            token = default;
            return false;
        }

        ReaderSlot.PushOverflow(this);
        token = new ReaderGateToken(isOverflow: true);
        return true;
    }

    #endregion
}

/// <summary>Proof of one entered section of a <see cref="ReaderGate"/>; pass it to <see cref="ReaderGate.Exit"/>.</summary>
/// <param name="isOverflow">True when the section uses the gate's overflow counter instead of the thread's slot.</param>
internal readonly struct ReaderGateToken(bool isOverflow)
{
    /// <summary>True when the section uses the gate's overflow counter.</summary>
    internal bool IsOverflow { get; } = isOverflow;
}
