// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

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
/// bitmap without copying. Use <see cref="ToBitmap"/> to obtain a detached, mutable copy.
/// </para>
/// <para>
/// This type is not thread-safe. Caller synchronization is required when the same instance
/// is shared across threads.
/// </para>
/// </summary>
public sealed class ReadOnlyRoaringBitmap
{
    /// <summary>Shared empty read-only bitmap. Zero-allocation shortcut for empty results.</summary>
    public static readonly ReadOnlyRoaringBitmap Empty = new(new RoaringBitmap());

    private readonly RoaringBitmap _Inner;

    /// <summary>Creates a read-only view over <paramref name="inner"/>.</summary>
    internal ReadOnlyRoaringBitmap(RoaringBitmap inner)
    {
        _Inner = inner;
    }

    /// <summary>
    /// Internal accessor for the wrapped <see cref="RoaringBitmap"/>. Used by
    /// in-assembly fast paths (e.g. <see cref="PresenceQuery"/>) that need to
    /// pass the underlying bitmap to in-place set operations. External callers
    /// must use <see cref="ToBitmap"/> to obtain a detached, mutable copy.
    /// </summary>
    internal RoaringBitmap Inner => _Inner;

    #region Properties

    /// <summary>Total number of values stored.</summary>
    public long Cardinality => _Inner.Cardinality;

    /// <summary>Whether the bitmap contains no values.</summary>
    public bool IsEmpty => _Inner.IsEmpty;

    /// <summary>
    /// Minimum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Min => _Inner.Min;

    /// <summary>
    /// Maximum value in the bitmap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bitmap is empty.</exception>
    public uint Max => _Inner.Max;

    /// <summary>
    /// Tries to get the minimum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMin(out uint value) => _Inner.TryGetMin(out value);

    /// <summary>
    /// Tries to get the maximum value in the bitmap. Returns <see langword="false"/> when the
    /// bitmap is empty (<paramref name="value"/> is set to 0).
    /// </summary>
    public bool TryGetMax(out uint value) => _Inner.TryGetMax(out value);

    #endregion

    #region Query methods

    /// <summary>Returns whether <paramref name="value"/> is present in the bitmap.</summary>
    public bool Contains(uint value) => _Inner.Contains(value);

    /// <summary>
    /// Returns the number of values ≤ <paramref name="value"/> in the bitmap.
    /// </summary>
    public long Rank(uint value) => _Inner.Rank(value);

    /// <summary>
    /// Returns the 0-based <paramref name="position"/>-th smallest value in the bitmap,
    /// or <see langword="null"/> if fewer than (<paramref name="position"/> + 1) values exist.
    /// </summary>
    public uint? Select(long position) => _Inner.Select(position);

    #endregion

    #region Copy

    /// <summary>
    /// Returns a new mutable <see cref="RoaringBitmap"/> that is a detached copy of the underlying
    /// bitmap. Mutations to the returned copy do not affect this view or the original bitmap.
    /// </summary>
    public RoaringBitmap ToBitmap() => _Inner.Clone();

    #endregion

    #region Set operations

    /// <summary>
    /// Returns the intersection of this bitmap and <paramref name="other"/> as a new
    /// <see cref="ReadOnlyRoaringBitmap"/>. Neither operand is modified.
    /// </summary>
    public ReadOnlyRoaringBitmap And(ReadOnlyRoaringBitmap other) => new(_Inner.And(other._Inner));

    /// <summary>
    /// Returns the union of this bitmap and <paramref name="other"/> as a new
    /// <see cref="ReadOnlyRoaringBitmap"/>. Neither operand is modified.
    /// </summary>
    public ReadOnlyRoaringBitmap Or(ReadOnlyRoaringBitmap other) => new(_Inner.Or(other._Inner));

    /// <summary>
    /// Returns the difference (this AND NOT other) as a new <see cref="ReadOnlyRoaringBitmap"/>.
    /// Neither operand is modified.
    /// </summary>
    public ReadOnlyRoaringBitmap AndNot(ReadOnlyRoaringBitmap other) => new(_Inner.AndNot(other._Inner));

    /// <summary>
    /// Returns the symmetric difference (XOR) as a new <see cref="ReadOnlyRoaringBitmap"/>.
    /// Neither operand is modified.
    /// </summary>
    public ReadOnlyRoaringBitmap Xor(ReadOnlyRoaringBitmap other) => new(_Inner.Xor(other._Inner));

    #endregion
}
