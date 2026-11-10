// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Grow-only chunked store: a packed prefix <c>0 .. Count-1</c> published by
/// <see cref="Append"/> / <see cref="AppendRange"/>. Lazy inner-chunk allocation; outer chunk
/// pointer array grows on demand via <see cref="ChunkedOuterArray{TChunk}"/>. One writer, any number of lock-free readers.
/// </summary>
/// <typeparam name="T">
/// Value stored per slot. Unset / unpublished slots hold the constructor sentinel
/// (<see langword="default"/> of <typeparamref name="T"/> unless the caller passes another one).
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Thread-safety:</b> one thread may call <see cref="Append"/> or <see cref="AppendRange"/>; a second append throws
/// <see cref="InvalidOperationException"/>. <see cref="Get"/> and <see cref="TryGet"/> of published slots are lock-free
/// for reference types and for blittable values of size 1, 2, 4, or 8 bytes. Larger structs, and structs that contain
/// references, are assigned plainly. A reader on another thread may read such a slot only below a count that it loaded
/// <b>before</b> the read: <see cref="Count"/> of this store, or the count of an owner that publishes its own count after
/// the append (<c>ValueCacheSeries</c> does). The slot is written completely before that count is published and is never
/// written again. A read of those types that is not gated by such a count is not supported. <see cref="Clear"/> may run
/// during <see cref="Append"/>; the append then retries onto the live prefix.
/// </para>
/// <para>
/// Dense, index-addressed view of the shared slot engine (<c>ChunkedSlotStore</c>): values are read by index with
/// <see cref="Get"/> / <see cref="TryGet"/>. Chunking instead of one growing array means growth never copies existing
/// values and never moves them, so readers need no lock while the writer extends the store; a chunk is allocated lazily
/// on the first append that reaches it.
/// </para>
/// <para>
/// Chunks are allocated through <see cref="ChunkedOuterArray{TChunk}"/>. The single appender is its only
/// allocator, so a chunk it receives stays the live chunk of its index and a slot write needs no re-check.
/// </para>
/// <para>
/// Slots of the lock-free shapes are published with <see cref="Volatile"/> writes. <see cref="Get"/> / <see cref="TryGet"/>
/// of <c>index &gt;= Count</c> or an unallocated chunk return the unset sentinel / <see langword="false"/>.
/// </para>
/// <para>
/// <b><see cref="Clear"/>:</b> replaces the chunks and <see cref="Count"/> together with one empty
/// generation (one volatile write). A concurrent <see cref="Append"/> retries onto the live generation.
/// A concurrent <see cref="Get"/> / <see cref="TryGet"/> that already loaded the previous generation may
/// still return that snapshot; a load after <see cref="Clear"/> observes unset / false. An append costs
/// one interlocked operation (the single-writer gate); the publish is a volatile write of the count.
/// </para>
/// </remarks>
public sealed class ChunkedGrowOnlyStore<T>
{
    #region Configuration and state

    /// <summary>Shared engine; this type only narrows its surface to the dense, index-addressed operations.</summary>
    private readonly ChunkedSlotStore<T> _Store;

    /// <summary>
    /// Creates a store with the given chunk size (<c>1 &lt;&lt; chunkShift</c> slots per inner array)
    /// and <see langword="default"/> as the unset slot value.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyStore(int chunkShift)
        : this(chunkShift, unsetValue: default!) // T is unconstrained; default is the documented unset value, so the compiler cannot prove it.
    {
    }

    /// <summary>
    /// Creates a store with the given chunk size and unset sentinel.
    /// </summary>
    /// <param name="chunkShift">Log₂ of slots per chunk; must be in [4, 20].</param>
    /// <param name="unsetValue">
    /// Value written into every slot of a newly allocated chunk when it differs from
    /// <see langword="default"/> of <typeparamref name="T"/>. Also returned by <see cref="Get"/>
    /// and <see cref="TryGet"/> when the index is invalid or the inner chunk is unallocated.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    public ChunkedGrowOnlyStore(int chunkShift, T unsetValue) =>
        _Store = new(chunkShift, unsetValue);

    #endregion

    #region Appending, reading, and clearing

    /// <summary>Number of entries published by <see cref="Append"/> / <see cref="AppendRange"/>.</summary>
    public int Count => _Store.Count;

    /// <summary>Appends one entry at <see cref="Count"/>. Single-writer only.</summary>
    /// <param name="item">The entry to store; passed by <c>in</c> reference to avoid copying large structs.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store reached
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(in T item) =>
        _Store.Append(in item);

    /// <summary>
    /// <see cref="Append"/> without the concurrent-writer check. The caller guarantees that every append and
    /// <see cref="Clear"/> of this store is serialized by the caller; <c>ValueCacheSeries</c> calls it while holding
    /// its series gate. Readers stay lock-free. The gate is skipped because the series already serializes its writers,
    /// so a second compare-exchange per appended value would be pure overhead; without that serialization the call is unsafe.
    /// </summary>
    /// <param name="item">The entry to store.</param>
    /// <exception cref="InvalidOperationException">The store reached <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendOwned(in T item) =>
        _Store.AppendOwned(in item);

    /// <summary>
    /// Appends <paramref name="values"/> as one publish (readers see all or none of them). Empty span is a no-op.
    /// </summary>
    /// <param name="values">Entries to append in order.</param>
    /// <exception cref="InvalidOperationException">
    /// Concurrent <see cref="Append"/> / <see cref="AppendRange"/>, or the store would exceed
    /// <see cref="Ids.ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    public void AppendRange(ReadOnlySpan<T> values) =>
        _Store.AppendRange(values);

    /// <summary>
    /// Reads the value at <paramref name="index"/>.
    /// An invalid index, an index <c>&gt;= Count</c>, or an unallocated chunk returns the
    /// unset sentinel from the constructor. That sentinel is null for a reference type
    /// only when the caller left it at the default.
    /// </summary>
    /// <param name="index">Slot index.</param>
    /// <returns>The stored value, or the unset sentinel.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    public T Get(int index) =>
        _Store.Get(index);

    /// <summary>
    /// Reads a value at <paramref name="index"/>.
    /// Returns <see langword="false"/> when the index is invalid, <c>index &gt;= Count</c>,
    /// or the inner chunk is unallocated.
    /// An allocated slot may still hold the unset sentinel.
    /// </summary>
    /// <param name="index">Slot index.</param>
    /// <param name="value">The stored value, or the unset sentinel on a miss.</param>
    /// <returns><see langword="true"/> when the slot is published and its chunk exists.</returns>
    public bool TryGet(int index, out T value) =>
        _Store.TryGet(index, out value);

    /// <summary>
    /// Drops all chunk references by swapping in an empty generation (readers that already loaded the old one finish on it).
    /// </summary>
    public void Clear() =>
        _Store.Clear();

    /// <summary>
    /// Returns a published inner chunk as a span clipped to the lesser of
    /// <paramref name="publishedCount"/> and <see cref="Count"/>.
    /// Readers pass their loaded committed count so staged-but-uncommitted slots stay hidden.
    /// </summary>
    /// <param name="chunkIndex">Zero-based inner-chunk index.</param>
    /// <param name="publishedCount">Exclusive upper bound of readable slots (typically a series committed count).</param>
    /// <param name="span">Slice of the inner array covering published slots in this chunk.</param>
    /// <returns><see langword="true"/> when the chunk exists and overlaps the published range.</returns>
    public bool TryGetPublishedChunk(int chunkIndex, int publishedCount, out ReadOnlySpan<T> span) =>
        _Store.TryGetPublishedChunk(chunkIndex, publishedCount, out span);

    #endregion
}
