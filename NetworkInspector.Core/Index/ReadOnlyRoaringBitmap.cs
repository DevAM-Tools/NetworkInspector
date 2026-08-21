// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// A read-only view over a <see cref="RoaringBitmap"/>.
/// <para>
/// Exposes query and set operations only — the underlying bitmap cannot be mutated
/// through this wrapper. Set operations (<see cref="And"/>, <see cref="Or"/>,
/// <see cref="AndNot"/>, <see cref="Xor"/>) produce a new <see cref="ReadOnlyRoaringBitmap"/>
/// backed by a freshly computed <see cref="RoaringBitmap"/> without touching the original.
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
    #region Fields

    /// <summary>
    /// Shared empty mutable bitmap used only when <see cref="Inner"/> must return a non-null
    /// instance for in-assembly set ops against <see cref="Empty"/>. Never mutated.
    /// </summary>
    private static readonly RoaringBitmap _SharedEmpty = new();

    private readonly RoaringBitmap? _Inner;

    #endregion

    #region Lifecycle

    /// <summary>Empty read-only bitmap. Equivalent to <c>default</c>; zero allocation.</summary>
    public static ReadOnlyRoaringBitmap Empty => default;

    /// <summary>Creates a read-only view over <paramref name="inner"/>.</summary>
    internal ReadOnlyRoaringBitmap(RoaringBitmap inner)
    {
        _Inner = inner;
    }

    #endregion

    #region Internal

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

    #region Properties

    /// <summary>Total number of values stored.</summary>
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

    #region Query methods

    /// <summary>Returns whether <paramref name="value"/> is present in the bitmap.</summary>
    public bool Contains(uint value) => _Inner is not null && _Inner.Contains(value);

    /// <summary>
    /// Returns the number of values ≤ <paramref name="value"/> in the bitmap.
    /// </summary>
    public long Rank(uint value) => _Inner?.Rank(value)
        ?? 0L;

    /// <summary>
    /// Returns the 0-based <paramref name="position"/>-th smallest value in the bitmap,
    /// or <see langword="null"/> if fewer than (<paramref name="position"/> + 1) values exist.
    /// </summary>
    public uint? Select(long position) => _Inner?.Select(position);

    #endregion

    #region Copy

    /// <summary>
    /// Returns a new mutable <see cref="RoaringBitmap"/> that is a detached copy of the underlying
    /// bitmap. Mutations to the returned copy do not affect this view or the original bitmap.
    /// </summary>
    public RoaringBitmap ToBitmap() => _Inner is null
        ? new RoaringBitmap()
        : _Inner.Clone();

    #endregion

    #region Set operations

    /// <summary>
    /// Returns the intersection of this bitmap and <paramref name="other"/> as a new
    /// <see cref="ReadOnlyRoaringBitmap"/>. Neither operand is modified.
    /// </summary>
    public ReadOnlyRoaringBitmap And(ReadOnlyRoaringBitmap other)
    {
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
    public ReadOnlyRoaringBitmap Or(ReadOnlyRoaringBitmap other)
    {
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
    public ReadOnlyRoaringBitmap AndNot(ReadOnlyRoaringBitmap other)
    {
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
    public ReadOnlyRoaringBitmap Xor(ReadOnlyRoaringBitmap other)
    {
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
