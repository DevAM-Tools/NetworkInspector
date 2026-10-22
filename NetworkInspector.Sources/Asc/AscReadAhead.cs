// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Per-thread read-ahead windows of disk-backed <see cref="AscSource"/>s. A thread that reads frames in id order
/// (<c>NextFrame</c>, or <c>FrameById</c> forward or backward, as a session does) gets its records from one
/// <see cref="BufferSize"/> positional read per window instead of one read per frame. Random access is not slowed down:
/// a window is filled only on the second consecutive id of a walk, so a random <c>FrameById</c> reads exactly its own
/// record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per thread:</b> several consumers read one source at once, each walking its own region. A window per thread
/// needs no lock and no shared mutable state, and one walk never evicts another thread's window. Up to
/// <see cref="Ways"/> sources share a thread's windows (one way per source); a buffer is allocated only when a walk
/// starts on that way, so a thread that reads at random never allocates one.
/// </para>
/// <para>
/// <b>Correctness:</b> the file cannot change while the source is open (Windows denies writers; Linux and macOS latch a
/// change and the source then fails closed), so a window read once stays exact for the source's lifetime. Ways are
/// keyed by a process-unique owner id, never by reference: the cache keeps no source alive and a new source can never
/// match an old window.
/// </para>
/// <para><b>Thread-safety:</b> an instance is only reachable from its own thread (<c>[ThreadStatic]</c>).</para>
/// </remarks>
internal sealed class AscReadAhead
{
    #region Constants

    /// <summary>Bytes per window: 64 KiB, below the large-object threshold; about 800 CAN or 14 Ethernet records.</summary>
    internal const int BufferSize = 64 * 1024;

    /// <summary>
    /// Sources whose walks one thread keeps at the same time. Must be a power of two: the victim way wraps with
    /// <c>(way + 1) &amp; (Ways − 1)</c>. Constant (tuning value, not configurable).
    /// </summary>
    internal const int Ways = 4;

    #endregion

    #region Fields

    [ThreadStatic]
    private static AscReadAhead? _Current;

    /// <summary>Last issued owner id; ids start at 1, so 0 marks an empty way.</summary>
    private static long _LastOwner;

    private readonly Entry[] _Entries = new Entry[Ways];
    private int _Victim;

    #endregion

    #region Public API

    /// <summary>A process-unique owner id for a new source.</summary>
    internal static long NewOwner() => Interlocked.Increment(ref _LastOwner);

    /// <summary>
    /// Serves <c>[offset, offset + count)</c>, clipped at the length of the guarded file, from the calling thread's window
    /// of <paramref name="owner"/>. Refills the window when the access continues an id-order walk (forward: the window
    /// starts at <paramref name="offset"/>; backward: it ends at the end of the request). Returns false when the access
    /// is not part of a walk and the window does not cover it; the caller then reads exactly the bytes it needs.
    /// </summary>
    /// <param name="owner">The source's id from <see cref="NewOwner"/>.</param>
    /// <param name="frameIndex">Id of the requested frame; consecutive ids detect a walk.</param>
    /// <param name="offset">File offset of the record; below the file length.</param>
    /// <param name="count">Bytes the caller needs; 1 to <see cref="BufferSize"/> for a window to be used.</param>
    /// <param name="guard">The source's file guard; the read goes through its handle.</param>
    /// <param name="data">The served bytes; valid until the next call on this thread.</param>
    internal static bool TryRead(long owner, int frameIndex, long offset, int count, CaptureFileGuard guard, out ReadOnlySpan<byte> data)
    {
        _Current ??= new();
        return _Current._TryRead(owner, frameIndex, offset, count, guard, out data);
    }

    #endregion

    #region Private helpers

    private bool _TryRead(long owner, int frameIndex, long offset, int count, CaptureFileGuard guard, out ReadOnlySpan<byte> data)
    {
        data = default;

        // 1. The requested range, clipped at the end of the file as a direct read would clip it.
        long fileLength = guard.Length;
        if (count < 1 || count > BufferSize || offset < 0 || offset >= fileLength)
        {
            return false;
        }

        int wanted = (int)Math.Min(count, fileLength - offset);
        ref Entry entry = ref _Entries[_FindOrClaim(owner)];
        int previousFrame = entry.LastFrame;
        entry.LastFrame = frameIndex;

        // 2. Served from the current window (also on a random jump that lands inside it).
        if (entry.Buffer is not null && offset >= entry.Start && offset + wanted <= entry.Start + entry.Length)
        {
            data = entry.Buffer.AsSpan((int)(offset - entry.Start), wanted);
            return true;
        }

        // 3. A walk refills the window in its direction; anything else is random access and reads exactly.
        bool forward = frameIndex == previousFrame + 1;
        bool backward = frameIndex == previousFrame - 1;
        if (!forward && !backward)
        {
            return false;
        }

        entry.Buffer ??= new byte[BufferSize];
        long start = forward
            ? offset
            : Math.Max(0, offset + wanted - BufferSize);
        int length = (int)Math.Min(BufferSize, fileLength - start);
        int read = AscSource.ReadAt(guard, start, entry.Buffer.AsSpan(0, length));
        entry.Start = start;
        entry.Length = read;
        if (offset + wanted > start + read)
        {
            // Short read (the file ended early): no window; the caller reads directly and reports what it finds.
            entry.Length = 0;
            return false;
        }

        data = entry.Buffer.AsSpan((int)(offset - start), wanted);
        return true;
    }

    /// <summary>The way of <paramref name="owner"/>; else the next victim, reset for it (its buffer is kept for reuse).</summary>
    private int _FindOrClaim(long owner)
    {
        Entry[] entries = _Entries;
        for (int way = 0; way < entries.Length; way++)
        {
            if (entries[way].Owner == owner)
            {
                return way;
            }
        }

        int victim = _Victim;
        _Victim = (victim + 1) & (Ways - 1);
        entries[victim].Owner = owner;
        // −2: neither neighbour (−1, −3) is a valid id, so the first access of a new owner never counts as a walk.
        entries[victim].LastFrame = -2;
        entries[victim].Start = 0;
        entries[victim].Length = 0;
        return victim;
    }

    #endregion

    #region Nested types

    /// <summary>One way: the owner, its last requested id, and its window <c>[Start, Start + Length)</c> in <c>Buffer</c>.</summary>
    private struct Entry
    {
        public long Owner;
        public int LastFrame;
        public long Start;
        public int Length;
        public byte[]? Buffer;
    }

    #endregion
}
