// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Lock-free immutable-snapshot list.
/// Reads are O(1) and allocation-free (return current array reference via <see cref="System.Threading.Volatile"/> Read).
/// Writes (Add / Remove) are rare O(n) copy-on-write operations protected by a CAS retry loop.
///
/// <para>
/// Intended for the listener-job registry and source-entry registry, which are written
/// very rarely (at subscribe / unsubscribe time) but read on every frame.
/// </para>
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
internal sealed class SnapshotList<T>
{
    private T[] _Snapshot = [];

    /// <summary>
    /// The current snapshot as a span. O(1), no allocation, no lock.
    /// </summary>
    internal ReadOnlySpan<T> Current => Volatile.Read(ref _Snapshot);

    /// <summary>
    /// The current snapshot array reference. O(1), no copy.
    /// Treat as immutable for the lifetime of this snapshot; a concurrent write may publish a newer array.
    /// </summary>
    internal T[] CurrentSnapshot => Volatile.Read(ref _Snapshot);

    /// <summary>
    /// Appends <paramref name="item"/> to the list. Thread-safe via CAS retry loop.
    /// </summary>
    internal void Add(T item)
    {
        T[] current;
        T[] updated;
        do
        {
            current = Volatile.Read(ref _Snapshot);
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
            current = Volatile.Read(ref _Snapshot);
            int idx = Array.IndexOf(current, item);
            if (idx < 0)
            {
                return false;
            }

            updated = new T[current.Length - 1];
            current.AsSpan(0, idx).CopyTo(updated);
            current.AsSpan(idx + 1).CopyTo(updated.AsSpan(idx));
        }
        while (Interlocked.CompareExchange(ref _Snapshot, updated, current) != current);
        return true;
    }

    /// <summary>
    /// Replaces the snapshot with an empty array.
    /// Not safe to call concurrently with Add/Remove — intended for use during
    /// session restart when no source jobs are active.
    /// </summary>
    internal void Clear() => Volatile.Write(ref _Snapshot, []);

    /// <summary>
    /// Returns the number of elements in the current snapshot.
    /// </summary>
    internal int Count => Volatile.Read(ref _Snapshot).Length;
}
