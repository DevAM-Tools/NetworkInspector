// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Packed append-only log of <typeparamref name="T"/> entries with lock-free readers.
/// Random-index slot writes are not available on this type; packed growth is
/// <see cref="Append"/> / <see cref="AppendRange"/> only.
/// </summary>
/// <typeparam name="T">Packed entry type.</typeparam>
/// <remarks>
/// <para>
/// <b>Thread-safety:</b> one thread may call <see cref="Append"/> or <see cref="AppendRange"/>; a second append throws
/// <see cref="InvalidOperationException"/>. <see cref="TryReadPublished"/> and <see cref="ItemRef"/> of published slots are
/// lock-free for reference types and for blittable values of size 1, 2, 4, or 8 bytes. Larger structs, and structs that
/// contain references, are assigned plainly. A reader on another thread may read such a slot only below a count that it
/// loaded <b>before</b> the read: <see cref="Count"/> of this store, or the count of an owner that publishes its own count
/// after the append (<c>ValueCacheSeries</c> does). The append writes the slot completely before that count is published and
/// the store never writes it again; a caller that mutates a slot through <see cref="ItemRef"/> owns the ordering of that
/// mutation. A read of those types that is not gated by such a count is not supported. <see cref="Clear"/> may run during
/// <see cref="Append"/>; the append then retries onto the live prefix.
/// </para>
/// <para>
/// Packed, ordered view of the shared slot engine (<c>ChunkedSlotStore</c>): entries are addressed by their position
/// and, when appended in ascending key order, found by <see cref="BinarySearch"/>. Readers get a copy of an entry
/// (<see cref="TryReadPublished"/>) or a reference into the live slot (<see cref="ItemRef"/>) so large entry structs
/// need not be copied to read one field.
/// </para>
/// <para>
/// The writer writes the live slot first, then publishes with a volatile write of <see cref="Count"/>.
/// Mutation through <see cref="ItemRef"/> is single-writer.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> replaces the chunks and <see cref="Count"/> together with one empty
/// generation (one volatile write). Packed readers read one generation; when <see cref="Clear"/>
/// replaced it before they return, they observe a miss (<c>-1</c> / <see langword="false"/>). They never
/// mix entries of the cleared log with entries appended after <see cref="Clear"/>. <see cref="ItemRef"/>
/// throws <see cref="ArgumentOutOfRangeException"/> when the index is not published in the live generation.
/// A concurrent <see cref="Append"/> retries onto the empty generation.
/// </para>
/// <para>
/// Reference-type fields inside a value-type <typeparamref name="T"/> that the writer mutates after
/// <see cref="Append"/> must be published by the caller with <see cref="Volatile"/> writes
/// on the live slot; readers must <see cref="ReadVolatileRefField{TField}"/> that location.
/// </para>
/// </remarks>
public sealed class ChunkedAppendOnlyStore<T>
{
    #region Configuration and state

    /// <summary>Shared engine; this type only narrows its surface to the packed, append-only operations.</summary>
    private readonly ChunkedSlotStore<T> _Store;

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array)
    /// and <see langword="default"/> as the unset slot value.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedAppendOnlyStore(int chunkShift)
        : this(chunkShift, unsetValue: default!) // T is unconstrained; default is the documented unset value, so the compiler cannot prove it.
    {
    }

    /// <summary>
    /// Creates a store with the given chunk size and unset sentinel.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <param name="unsetValue">Sentinel returned by miss paths.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedAppendOnlyStore(int chunkShift, T unsetValue) =>
        _Store = new(chunkShift, unsetValue);

    #endregion

    #region Appending, reading, and clearing

    /// <summary>
    /// Number of entries published by <see cref="Append"/>. Volatile read; acquires the packed prefix.
    /// </summary>
    public int Count => _Store.Count;

    /// <summary>
    /// Appends one entry to the packed prefix. Single-writer only.
    /// A concurrent <see cref="Clear"/> causes the publish to fail and this method retries from the live prefix.
    /// </summary>
    /// <param name="item">
    /// Entry stored at the current <see cref="Count"/>. When <see cref="BinarySearch"/> is used,
    /// keys must be appended in strictly ascending order.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>
    /// Appends every value in <paramref name="values"/> to the packed prefix as one publish.
    /// Single-writer only. Concurrent <see cref="Append"/> or <see cref="AppendRange"/> throws.
    /// The span length is independent of the inner chunk size. An empty span is a no-op.
    /// A concurrent <see cref="Clear"/> causes the publish to fail and this method retries from the live prefix.
    /// </summary>
    /// <param name="values">Entries to append in order; readers see all or none of them.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store would exceed
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void AppendRange(ReadOnlySpan<T> values) =>
        _Store.AppendRange(values);

    /// <summary>
    /// Returns a reference to the packed entry at <paramref name="index"/>. Readers must treat the
    /// reference as read-only; only the single writer may mutate through it.
    /// </summary>
    /// <param name="index">Published entry index (0 … <see cref="Count"/> − 1).</param>
    /// <returns>A reference to the live slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not published.</exception>
    public ref T ItemRef(int index) =>
        ref _Store.ItemRef(index);

    /// <summary>
    /// Copies the packed entry at <paramref name="index"/> when it is still published in the live generation.
    /// Returns <see langword="false"/> when the index is outside <see cref="Count"/>, the chunk is
    /// missing, or <see cref="Clear"/> raced.
    /// </summary>
    /// <param name="index">Published entry index.</param>
    /// <param name="value">A copy of the entry, or the unset sentinel on a miss.</param>
    /// <returns><see langword="true"/> when the entry is published in the live generation.</returns>
    public bool TryReadPublished(int index, out T value) =>
        _Store.TryReadPublished(index, out value);

    /// <summary>
    /// Binary-searches the packed prefix for the entry whose sort key equals
    /// <paramref name="sortKey"/>. Valid only when entries were appended in strictly ascending
    /// key order. Returns <c>-1</c> when the key is missing or <see cref="Clear"/> raced.
    /// </summary>
    /// <param name="sortKey">Key to locate.</param>
    /// <param name="getSortKey">
    /// Extracts the sort key from an entry. Pass a cached static delegate; do not allocate per call.
    /// </param>
    /// <returns>Entry index, or <c>-1</c> when no published entry has the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="getSortKey"/> is <see langword="null"/>.</exception>
    public int BinarySearch(int sortKey, GetSortKey<T> getSortKey) =>
        _Store.BinarySearch(sortKey, getSortKey);

    /// <summary>
    /// <see cref="Volatile"/> read of a class field on the live packed slot.
    /// Use this instead of copying the surrounding struct when the field is published with
    /// <see cref="Volatile"/> writes after <see cref="Append"/>.
    /// </summary>
    /// <typeparam name="TField">Class type of the field.</typeparam>
    /// <param name="index">Published entry index.</param>
    /// <param name="getter">Returns a reference to the field inside the live slot; pass a cached static delegate.</param>
    /// <returns>The field, or <see langword="null"/> when the index is unpublished or <see cref="Clear"/> raced.</returns>
    public TField? ReadVolatileRefField<TField>(int index, GetRefField<T, TField> getter)
        where TField : class? =>
        _Store.ReadVolatileRefField(index, getter);

    /// <summary>
    /// Drops all chunk references and resets the packed prefix in one generation swap. Concurrent packed
    /// readers that loaded the pre-clear generation miss; they do not observe the refilled log under that snapshot.
    /// </summary>
    public void Clear() =>
        _Store.Clear();

    #endregion
}
