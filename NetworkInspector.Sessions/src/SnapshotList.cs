// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Lock-free immutable-snapshot list.
/// Reads are O(1) and allocation-free (they return the current array reference with one volatile field read).
/// Writes (Add / Remove) are rare O(n) copy-on-write operations protected by a CAS retry loop.
///
/// <para>
/// Intended for the session registries (sources, listener and value-cache slots, public infos, jobs), which are written
/// very rarely (at subscribe / unsubscribe time) but read on every frame by the source threads that notify the slots.
/// Why copy-on-write: a reader on the per-frame path then takes no lock, allocates nothing and iterates a stable array
/// even while a writer replaces it; the O(n) copy is paid by the rare writer.
/// </para>
/// <para>
/// <b>Thread safety:</b> any number of readers and writers. Each published array is a new object that is never
/// modified afterwards, so a CAS on the reference cannot suffer an ABA problem, and the volatile read / CAS pair gives
/// readers a fully written array. Equality for <see cref="Remove"/> is that of <see cref="Array.IndexOf{T}(T[], T)"/>
/// (the default equality comparer of <typeparamref name="T"/>).
/// </para>
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
internal sealed class SnapshotList<T>
{
    #region Fields

    // Always a complete, never-mutated array; replaced as a whole. Volatile so readers see the latest published one.
    private volatile T[] _Snapshot = [];

    // The read-only view of the snapshot it was built from, for ReadOnlyView. Replaced as a whole; volatile so a reader
    // sees a fully constructed pair.
    private volatile SnapshotView? _View;

    #endregion

    #region Nested types

    /// <summary>
    /// A snapshot array together with its read-only wrapper. Immutable; thread safety is not applicable. The array is kept
    /// only to tell whether the view still belongs to the current snapshot.
    /// </summary>
    private sealed class SnapshotView(T[] items)
    {
        internal T[] Items { get; } = items;

        internal System.Collections.ObjectModel.ReadOnlyCollection<T> View { get; } = new(items);
    }

    #endregion

    #region API

    /// <summary>
    /// The current snapshot as a span. O(1), no allocation, no lock.
    /// </summary>
    internal ReadOnlySpan<T> Current => _Snapshot;

    /// <summary>
    /// The current snapshot array reference. O(1), no copy.
    /// Treat as immutable for the lifetime of this snapshot; a concurrent write may publish a newer array.
    /// The array is shared, not copied: writing to it would corrupt what every other reader sees.
    /// </summary>
    internal T[] CurrentSnapshot => _Snapshot;

    /// <summary>
    /// The current snapshot as a read-only list that is not the array. One view object per snapshot: the first call after a
    /// write wraps the new array, every later call returns that same object (no allocation per call).
    /// </summary>
    /// <remarks>
    /// Why not hand out the array as <c>IReadOnlyList&lt;T&gt;</c>: a caller could cast it back to <c>T[]</c> and overwrite
    /// elements that every other reader (including the per-frame notify loops) shares. The wrapper exposes no setter and no
    /// way back to the array. Two threads that miss the cached view at once each build one; both wrap the same array, the
    /// last store wins, and no caller can tell them apart, so no lock is needed.
    /// </remarks>
    internal IReadOnlyList<T> ReadOnlyView
    {
        get
        {
            T[] snapshot = _Snapshot;
            SnapshotView? cached = _View;
            if (cached is not null && ReferenceEquals(cached.Items, snapshot))
            {
                return cached.View;
            }

            SnapshotView created = new(snapshot);
            _View = created;
            return created.View;
        }
    }

    /// <summary>
    /// Appends <paramref name="item"/> to the list. Thread-safe via CAS retry loop.
    /// </summary>
    internal void Add(T item)
    {
        // Copy, append, publish with a CAS on the reference. If another writer published first, the CAS fails and the
        // loop rebuilds from the new snapshot, so no concurrent Add or Remove is lost. The copy is garbage for the GC,
        // which is acceptable because writes are rare (see the type remarks).
        T[] current;
        T[] updated;
        do
        {
            current = _Snapshot;
            updated = new T[current.Length + 1];
            current.CopyTo(updated, 0);
            updated[current.Length] = item;
        }
        while (Interlocked.CompareExchange(ref _Snapshot, updated, current) != current);
    }

    /// <summary>
    /// Removes the first occurrence of <paramref name="item"/> from the list.
    /// Thread-safe via CAS retry loop.
    /// Returns <see langword="false"/> if the item was not found.
    /// </summary>
    internal bool Remove(T item)
    {
        T[] current;
        T[] updated;
        do
        {
            // The search is repeated on every retry: the item may have been removed (or its position changed) by the
            // writer that won the CAS, so an index found on an older snapshot must not be reused.
            current = _Snapshot;
            int idx = Array.IndexOf(current, item);
            if (idx < 0)
            {
                return false;
            }

            // Order-preserving removal: copy the part before and the part after the index.
            updated = new T[current.Length - 1];
            current.AsSpan(0, idx).CopyTo(updated);
            current.AsSpan(idx + 1).CopyTo(updated.AsSpan(idx));
        }
        while (Interlocked.CompareExchange(ref _Snapshot, updated, current) != current);
        return true;
    }

    #endregion
}
