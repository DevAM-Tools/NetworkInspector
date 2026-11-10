// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for a captured frame.
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
/// <remarks>
/// Creates a new <see cref="FrameId"/> with the specified raw value.
/// <para>
/// Valid values are <c>0</c> to <see cref="ArrayIndexIdRange.MaxValue"/> (the id is used as an array index) plus the single
/// sentinel <c>-1</c> (<see cref="Invalid"/>). <c>default(FrameId)</c> is id <c>0</c>, which is a valid id and not
/// <see cref="Invalid"/>; use <see cref="Invalid"/> explicitly to mean "no frame".
/// </para>
/// </remarks>
/// <param name="value">The raw id: <c>-1</c> for <see cref="Invalid"/>, otherwise an index in the valid range.</param>
/// <exception cref="ArgumentOutOfRangeException">
/// <c>value</c> is negative but not <c>-1</c>, or is above <see cref="ArrayIndexIdRange.MaxValue"/>.
/// </exception>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct FrameId(int value) : IEquatable<FrameId>, IComparable<FrameId>
{
    /// <summary>Sentinel value representing an invalid/unassigned frame ID.</summary>
    public static readonly FrameId Invalid = new(-1);

    /// <summary>The raw numeric value of this identifier.</summary>
    public int Value { get; } = _StoreValidated(value);

    /// <summary>Whether this ID represents a valid (assigned) identifier.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ArrayIndexIdRange.IsValidIndex(Value);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FrameId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FrameId other) => Value == other.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is FrameId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the first id is smaller than the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is smaller than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FrameId left, FrameId right) => left.Value < right.Value;

    /// <summary>Whether the first id is larger than the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is larger than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FrameId left, FrameId right) => left.Value > right.Value;

    /// <summary>Whether the first id is smaller than or equal to the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is not larger than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FrameId left, FrameId right) => left.Value <= right.Value;

    /// <summary>Whether the first id is larger than or equal to the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is not smaller than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FrameId left, FrameId right) => left.Value >= right.Value;

    /// <summary>Whether two ids have the same value. Ids of different stacks compare equal when their numbers match.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when both hold the same number.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FrameId left, FrameId right) => left.Value == right.Value;

    /// <summary>Whether two ids have different values. Ids of different stacks compare equal when their numbers match.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the numbers differ.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FrameId left, FrameId right) => left.Value != right.Value;

    /// <summary>
    /// Implicitly converts a <see cref="FrameId"/> to an <see cref="int"/> (for indexing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator int(FrameId id) => id.Value;

    /// <summary>
    /// Implicitly converts an <see cref="int"/> to a <see cref="FrameId"/>, so <c>source.FrameById(7)</c> and
    /// <c>FrameId id = 7;</c> read like the plain index they are.
    /// </summary>
    /// <remarks>
    /// The conversion runs the same range check as <c>new FrameId(int)</c> (motivation: an id is an array index and every
    /// existing id must be usable as one, so there is no unchecked path). Unlike most implicit conversions it therefore
    /// <b>can throw</b>: a value that is negative but not <c>-1</c>, or above <see cref="ArrayIndexIdRange.MaxValue"/>,
    /// throws <see cref="ArgumentOutOfRangeException"/>. Accepted risk: a computed <c>int</c> that is out of range fails at the
    /// conversion instead of at a later array access, which is the earlier and clearer failure. Compare with a literal
    /// (<c>id == 5</c>) uses the <see cref="FrameId"/> operators, so the literal is range-checked as well.
    /// </remarks>
    /// <param name="value">The raw id: <c>-1</c> for <see cref="Invalid"/>, otherwise an index in the valid range.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is negative but not <c>-1</c>, or is above <see cref="ArrayIndexIdRange.MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator FrameId(int value) => new(value);

    /// <summary>
    /// Validates the raw value once, at construction, so every existing id is either <see cref="Invalid"/> or a usable
    /// array index and consumers do not re-check the range. Throws <see cref="ArgumentOutOfRangeException"/> otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _StoreValidated(int value)
    {
        // The sentinel is the only negative value allowed; everything else must be a valid array index.
        if (!ArrayIndexIdRange.IsInvalidSentinel(value))
        {
            ArrayIndexIdRange.ValidateIndexOrThrow(value, nameof(value));
        }

        return value;
    }
}
