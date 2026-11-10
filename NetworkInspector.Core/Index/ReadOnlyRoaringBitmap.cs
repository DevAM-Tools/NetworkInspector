// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// A read-only view over a <see cref="RoaringBitmap"/>.
/// <para>
/// Exposes query and set operations only — the underlying bitmap cannot be mutated
/// through this wrapper. Set operations (<see cref="And"/>, <see cref="Or"/>,
/// <see cref="AndNot"/>, <see cref="Xor"/>) produce a new <see cref="ReadOnlyRoaringBitmap"/>
/// backed by a freshly computed <see cref="RoaringBitmap"/> without touching the original.
/// Exception: when an operand is <see cref="Empty"/> and the result equals the other operand
/// (<see cref="Or"/> and <see cref="Xor"/> with either operand empty, <see cref="AndNot"/> with an empty right-hand
/// side), that operand's view is returned as is, without a copy. That result is still a live view of the original bitmap, not a snapshot.
/// Set operations read both operands without the seqlock, so neither may be appended to while one runs.
/// </para>
/// <para>
/// Use <see cref="RoaringBitmap.AsReadOnly"/> to obtain a read-only view of an existing
/// bitmap without copying (zero-allocation). Use <see cref="ToBitmap"/> to obtain a detached,
/// mutable copy.
/// </para>
/// <para>
/// <see cref="Empty"/> is <c>default</c> (null inner). It is not a shared object identity —
/// do not use <see cref="object.ReferenceEquals"/> for empty checks; use <see cref="IsEmpty"/>.
/// Callers that need a nullable "unset" distinct from <see cref="Empty"/> should use
/// <c>ReadOnlyRoaringBitmap?</c> (as <see cref="PresenceQuery"/> does).
/// </para>
/// <para>
/// <b>Thread-safety:</b> This is a live view of the wrapped <see cref="RoaringBitmap"/>.
/// Concurrent readers may keep the same instance and call <see cref="Contains"/> while a
/// single writer appends to the underlying bitmap (for example a <see cref="PacketIndex"/>
/// that is still capturing). New values become visible on this view; a second
/// <see cref="RoaringBitmap.AsReadOnly"/> is not required. Concurrent mutation of the wrapped
/// bitmap through this type is not possible. Use <see cref="ToBitmap"/> only when a detached
/// snapshot is required (the index is no longer growing, or the caller will mutate a copy).
/// </para>
/// </summary>
public readonly struct ReadOnlyRoaringBitmap
{
    #region Wrapped bitmap

    /// <summary>
    /// Shared empty mutable bitmap used only when <see cref="Inner"/> must return a non-null
    /// instance for in-assembly set ops against <see cref="Empty"/>. Never mutated.
    /// Why a shared instance: <see cref="Empty"/> is <c>default</c> (null inner) to stay allocation-free, and the
    /// in-place operations of <see cref="RoaringBitmap"/> need a non-null operand. It is only ever read, so sharing it
    /// between threads is safe.
    /// </summary>
    private static readonly RoaringBitmap _SharedEmpty = new();

    /// <summary>
    /// The wrapped bitmap. Null only for <c>default</c> / <see cref="Empty"/>. Every other instance wraps a live
    /// <see cref="RoaringBitmap"/>, so reads see later appends.
    /// </summary>
    private readonly RoaringBitmap? _Inner;

    /// <summary>Empty read-only bitmap. Equivalent to <c>default</c>; zero allocation.</summary>
    public static ReadOnlyRoaringBitmap Empty => default;

    /// <summary>Creates a read-only view over <paramref name="inner"/>.</summary>
    /// <param name="inner">The live bitmap to view; not <see langword="null"/>; not copied.</param>
    internal ReadOnlyRoaringBitmap(RoaringBitmap inner)
    {
        _Inner = inner;
    }

    /// <summary>
    /// Internal accessor for the wrapped <see cref="RoaringBitmap"/>. Used by
    /// in-assembly fast paths (e.g. <see cref="PresenceQuery"/>) that need to
    /// pass the underlying bitmap to in-place set operations. External callers
    /// must use <see cref="ToBitmap"/> to obtain a detached, mutable copy.
    /// <para>
    /// For <see cref="Empty"/>, returns a shared empty bitmap (never <see langword="null"/>).
    /// </para>
    /// </summary>
    internal RoaringBitmap Inner => _Inner
        ?? _SharedEmpty;

    #endregion

    #region Size and bounds

    /// <summary>Total number of values stored; 0 for <see cref="Empty"/>.</summary>
    public long Cardinality => _Inner?.Cardinality
        ?? 0L;

    /// <summary>Whether the bitmap contains no values.</summary>
    public bool IsEmpty => _Inner is null || _Inner.IsEmpty;

    /// <summary>
    /// Minimum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Min => Inner.Min;

    /// <summary>
    /// Maximum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Max => Inner.Max;

    /// <summary>
    /// Tries to get the minimum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    /// <param name="value">The smallest value on success; 0 when the bitmap is empty.</param>
    /// <returns><see langword="true"/> when the bitmap holds at least one value.</returns>
    public bool TryGetMin(out uint value)
    {
        if (_Inner is null)
        {
            value = 0;
            return false;
        }

        return _Inner.TryGetMin(out value);
    }

    /// <summary>
    /// Tries to get the maximum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    /// <param name="value">The largest value on success; 0 when the bitmap is empty.</param>
    /// <returns><see langword="true"/> when the bitmap holds at least one value.</returns>
    public bool TryGetMax(out uint value)
    {
        if (_Inner is null)
        {
            value = 0;
            return false;
        }

        return _Inner.TryGetMax(out value);
    }

    #endregion

    #region Queries

    /// <summary>Returns whether <paramref name="value"/> is present in the bitmap. Safe while a single writer appends to the viewed bitmap.</summary>
    /// <param name="value">The value to look up.</param>
    /// <returns><see langword="true"/> when the bitmap holds <paramref name="value"/>; always <see langword="false"/> for <see cref="Empty"/>.</returns>
    public bool Contains(uint value) => _Inner is not null && _Inner.Contains(value);

    /// <summary>
    /// Returns the number of values ≤ <paramref name="value"/> in the bitmap.
    /// </summary>
    /// <param name="value">The upper bound, inclusive; it need not be present in the bitmap.</param>
    /// <returns>The count of stored values that are less than or equal to <paramref name="value"/>; 0 for <see cref="Empty"/>.</returns>
    public long Rank(uint value) => _Inner?.Rank(value)
        ?? 0L;

    /// <summary>
    /// Returns the 0-based <paramref name="position"/>-th smallest value in the bitmap,
    /// or <see langword="null"/> if fewer than (<paramref name="position"/> + 1) values exist.
    /// </summary>
    /// <param name="position">The 0-based position in ascending order; a negative position selects nothing.</param>
    /// <returns>The value at <paramref name="position"/>, or <see langword="null"/> when no such value exists.</returns>
    public uint? Select(long position) => _Inner?.Select(position);

    #endregion

    #region Detached copy

    /// <summary>
    /// Returns a new mutable <see cref="RoaringBitmap"/> that is a detached copy of the underlying
    /// bitmap. Mutations to the returned copy do not affect this view or the original bitmap.
    /// </summary>
    /// <returns>A new bitmap that holds the values this view holds now; empty for <see cref="Empty"/>.</returns>
    public RoaringBitmap ToBitmap() => _Inner is null
        ? new RoaringBitmap()
        : _Inner.Clone();

    #endregion

    #region Set operations

    /// <summary>
    /// Returns the intersection of this bitmap and <paramref name="other"/> as a new
    /// <see cref="ReadOnlyRoaringBitmap"/>. Neither operand is modified.
    /// </summary>
    /// <param name="other">The bitmap to intersect with.</param>
    /// <returns>A view of a new bitmap that holds the values present in both operands; <see cref="Empty"/> when either is empty.</returns>
    public ReadOnlyRoaringBitmap And(ReadOnlyRoaringBitmap other)
    {
        // x AND empty is empty: skip the merge and allocate nothing.
        if (_Inner is null || other._Inner is null)
        {
            return Empty;
        }

        return new(_Inner.And(other._Inner));
    }

    /// <summary>
    /// Returns the union of this bitmap and <paramref name="other"/> as a new
    /// <see cref="ReadOnlyRoaringBitmap"/>. Neither operand is modified.
    /// </summary>
    /// <param name="other">The bitmap to unite with.</param>
    /// <returns>
    /// A view of a new bitmap that holds the values present in either operand; when one operand is empty, the other operand's
    /// live view without a copy.
    /// </returns>
    public ReadOnlyRoaringBitmap Or(ReadOnlyRoaringBitmap other)
    {
        // Empty identity: the union equals the other view, so it is returned without a copy (still a live view).
        if (_Inner is null)
        {
            return other;
        }

        if (other._Inner is null)
        {
            return this;
        }

        return new(_Inner.Or(other._Inner));
    }

    /// <summary>
    /// Returns the difference (this AND NOT other) as a new <see cref="ReadOnlyRoaringBitmap"/>.
    /// Neither operand is modified.
    /// </summary>
    /// <param name="other">The bitmap whose values are removed from this one.</param>
    /// <returns>
    /// A view of a new bitmap that holds the values of this bitmap that are absent from <paramref name="other"/>;
    /// <see cref="Empty"/> when this bitmap is empty, this live view without a copy when <paramref name="other"/> is empty.
    /// </returns>
    public ReadOnlyRoaringBitmap AndNot(ReadOnlyRoaringBitmap other)
    {
        // empty AND NOT x is empty; x AND NOT empty is x, returned as the same live view without a copy.
        if (_Inner is null)
        {
            return Empty;
        }

        if (other._Inner is null)
        {
            return this;
        }

        return new(_Inner.AndNot(other._Inner));
    }

    /// <summary>
    /// Returns the symmetric difference (XOR) as a new <see cref="ReadOnlyRoaringBitmap"/>.
    /// Neither operand is modified.
    /// </summary>
    /// <param name="other">The bitmap to combine with.</param>
    /// <returns>
    /// A view of a new bitmap that holds the values present in exactly one operand; when one operand is empty, the other
    /// operand's live view without a copy.
    /// </returns>
    public ReadOnlyRoaringBitmap Xor(ReadOnlyRoaringBitmap other)
    {
        // Empty identity, as in Or: the other view is returned without a copy.
        if (_Inner is null)
        {
            return other;
        }

        if (other._Inner is null)
        {
            return this;
        }

        return new(_Inner.Xor(other._Inner));
    }

    #endregion
}
