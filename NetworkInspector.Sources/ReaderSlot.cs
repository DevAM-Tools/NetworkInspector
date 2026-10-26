// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources;

/// <summary>
/// The record of one thread's read section: which <see cref="ReaderGate"/> the thread is inside, if any. One slot per
/// thread that ever entered a gate, shared by all gates, in a process-wide list that <see cref="ReaderGate.Close"/>
/// walks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout (40 bytes on 64-bit):</b> object header 16 B, <see cref="_Gate"/>, <see cref="_Owner"/>,
/// <see cref="_Next"/> 8 B each. Nested sections live in a thread-static list that exists only after a nesting.
/// </para>
/// <para>
/// <b>Lifetime:</b> a new thread's slot is pushed to the list under <see cref="_RegistryLock"/>; the same call unlinks
/// slots whose thread has finished (a finished thread is never inside a section, because sections end in
/// <c>finally</c>). A walker on an unlinked slot still follows its unchanged <see cref="_Next"/>.
/// </para>
/// <para><b>Thread-safety:</b> <see cref="_Gate"/> is written only by the owning thread and read by any thread; the list is
/// written under a lock and read lock-free.</para>
/// </remarks>
internal sealed class ReaderSlot
{
    #region Constants

    /// <summary>Bytes of one slot on 64-bit (header plus three references); tests and the source guide cite it.</summary>
    internal const int SizeInBytes = 40;

    #endregion

    #region Fields

    /// <summary>
    /// The calling thread's slot. A plain thread static (no <see cref="ThreadLocal{T}"/> indirection) because
    /// <see cref="Current"/> runs on every read section.
    /// </summary>
    [ThreadStatic]
    private static ReaderSlot? _Current;

    /// <summary>Gates of the calling thread's nested sections; null until the first nesting (no allocation on the normal path).</summary>
    [ThreadStatic]
    private static List<ReaderGate>? _Overflow;

    /// <summary>Serialises writers of the slot list (registration and pruning). Readers walk the list without it.</summary>
    private static readonly Lock _RegistryLock = new();

    /// <summary>
    /// Newest slot of the process-wide list. Volatile so a lock-free walker that reads a newly published head also sees
    /// that slot's <see cref="_Next"/>, which is assigned before the head is.
    /// </summary>
    private static volatile ReaderSlot? _Head;

    /// <summary>
    /// The gate the owning thread is currently inside, or null. Written only by the owning thread (publish with a full
    /// fence, clear with a release store); read by <see cref="ReaderGate.Close"/> on any thread.
    /// </summary>
    private volatile ReaderGate? _Gate;

    /// <summary>The thread this slot belongs to; <see cref="_PruneFinished"/> unlinks the slot once the thread is not alive.</summary>
    private readonly Thread _Owner;

    /// <summary>Next (older) slot of the list. Changes only when a later registration prunes a finished successor.</summary>
    private volatile ReaderSlot? _Next;

    #endregion

    #region Constructors

    /// <summary>Creates the unlinked slot of <paramref name="owner"/>; <see cref="_Register"/> links it.</summary>
    private ReaderSlot(Thread owner) => _Owner = owner;

    #endregion

    #region Properties

    /// <summary>The calling thread's slot, registered on first use.</summary>
    internal static ReaderSlot Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Current
            ?? _Register();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Publishes <paramref name="gate"/> in this (the calling thread's) slot with a full fence. False when the slot
    /// already holds a section (nesting). Owning thread only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPublish(ReaderGate gate)
    {
        if (_Gate is not null)
        {
            return false;
        }

        // Interlocked.Exchange instead of a volatile store: it is a full fence, so the closed-flag read that the
        // caller performs next cannot be reordered before the publish (store-load ordering, see ReaderGate remarks).
        _ = Interlocked.Exchange(ref _Gate, gate);
        return true;
    }

    /// <summary>Ends the slot's section (release). Owning thread only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Clear() => _Gate = null;

    /// <summary>True when the calling thread is inside a section of <paramref name="gate"/> (slot or overflow).</summary>
    internal static bool CurrentHolds(ReaderGate gate)
    {
        if (ReferenceEquals(_Current?._Gate, gate))
        {
            return true;
        }

        List<ReaderGate>? overflow = _Overflow;
        return overflow is not null && overflow.Contains(gate);
    }

    /// <summary>Spins until no other thread's slot names <paramref name="gate"/>. Any thread.</summary>
    internal static void WaitWhileOtherThreadsHold(ReaderGate gate)
    {
        // Lock-free walk over the registry. A slot registered after the walk began belongs to a thread whose first
        // TryEnter reads the closed flag after that registration, so it sees the gate closed and never enters.
        // The caller's own slot is skipped: Close already rejected a caller that is inside a section of this gate.
        ReaderSlot? self = _Current;
        for (ReaderSlot? slot = _Head; slot is not null; slot = slot._Next)
        {
            if (ReferenceEquals(slot, self))
            {
                continue;
            }

            // Wait out a section that started before the flag was set; sections are short (no user code inside).
            SpinWait spin = default;
            while (ReferenceEquals(slot._Gate, gate))
            {
                spin.SpinOnce();
            }
        }
    }

    /// <summary>Records a nested (overflow) section of the calling thread.</summary>
    internal static void PushOverflow(ReaderGate gate) => (_Overflow ??= []).Add(gate);

    /// <summary>Removes the calling thread's newest overflow record of <paramref name="gate"/> (LIFO).</summary>
    internal static void PopOverflow(ReaderGate gate)
    {
        List<ReaderGate> overflow = _Overflow!;
        int last = overflow.LastIndexOf(gate);
        overflow.RemoveAt(last);
    }

    /// <summary>True when <paramref name="thread"/> has a slot in the registry. Tests.</summary>
    internal static bool IsRegistered(Thread thread)
    {
        for (ReaderSlot? slot = _Head; slot is not null; slot = slot._Next)
        {
            if (ReferenceEquals(slot._Owner, thread))
            {
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Private helpers

    /// <summary>Creates and links the calling thread's slot, unlinking slots of finished threads first.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReaderSlot _Register()
    {
        // Registration happens once per thread, so the lock and the pruning walk are off the per-section path.
        // Pruning here keeps the list (and so the Close walk) proportional to the live reader threads instead of
        // every thread that ever read. The link is assigned before the head: a walker that sees the new head sees a
        // complete list behind it.
        ReaderSlot slot = new(Thread.CurrentThread);
        lock (_RegistryLock)
        {
            _PruneFinished();
            slot._Next = _Head;
            _Head = slot;
        }

        _Current = slot;
        return slot;
    }

    /// <summary>Unlinks slots whose thread finished. Caller holds <see cref="_RegistryLock"/>.</summary>
    private static void _PruneFinished()
    {
        while (_Head is not null && !_Head._Owner.IsAlive)
        {
            _Head = _Head._Next;
        }

        for (ReaderSlot? slot = _Head; slot?._Next is not null;)
        {
            ReaderSlot next = slot._Next;
            if (next._Owner.IsAlive)
            {
                slot = next;
                continue;
            }

            slot._Next = next._Next;
        }
    }

    #endregion
}
