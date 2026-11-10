// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Packed-prefix helpers for <see cref="ChunkedAppendOnlyStore{T}"/> entries that expose
/// <see cref="ISortKeyed"/>.
/// </summary>
public static class ChunkedAppendOnlyStoreExtensions
{
    /// <summary>
    /// Binary-searches the packed prefix for the entry whose <see cref="ISortKeyed.SortKey"/>
    /// equals <paramref name="sortKey"/>. Valid only when entries were appended in strictly
    /// ascending key order.
    /// </summary>
    /// <typeparam name="T">Entry type exposing its key through <see cref="ISortKeyed"/>.</typeparam>
    /// <param name="store">The store to search.</param>
    /// <param name="sortKey">Key to locate.</param>
    /// <returns>Entry index, or <c>-1</c> when no published entry has the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public static int BinarySearch<T>(this ChunkedAppendOnlyStore<T> store, int sortKey)
        where T : ISortKeyed
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.BinarySearch(sortKey, SortKeyCache<T>.Getter);
    }

    /// <summary>Holds the cached sort-key getter of one entry type (see <see cref="Getter"/>).</summary>
    /// <typeparam name="T">Entry type exposing its key through <see cref="ISortKeyed"/>.</typeparam>
    private static class SortKeyCache<T>
        where T : ISortKeyed
    {
        /// <summary>
        /// Cached sort-key getter so packed binary search does not allocate a delegate per call (a per-call lambda or
        /// method-group conversion would allocate on every search). One instance per entry type, created on first use.
        /// </summary>
        public static readonly GetSortKey<T> Getter = static (in T item) => item.SortKey;
    }
}
