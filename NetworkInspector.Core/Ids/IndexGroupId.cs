// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for an index group (used by PacketIndex).
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
/// <remarks>
/// Creates a new <see cref="IndexGroupId"/> with the specified raw value.
/// <para>
/// Valid values are <c>0</c> to <see cref="ArrayIndexIdRange.MaxValue"/> (the id is used as an array index) plus the single
/// sentinel <c>-1</c> (<see cref="Invalid"/>). <c>default(IndexGroupId)</c> is id <c>0</c>, which is a valid id and not
/// <see cref="Invalid"/>. A field that belongs to no index group reports <see cref="Invalid"/>, so test <see cref="IsValid"/>
/// before using a group id.
/// </para>
/// </remarks>
/// <param name="value">The raw id: <c>-1</c> for <see cref="Invalid"/>, otherwise an index in the valid range.</param>
/// <exception cref="ArgumentOutOfRangeException">
/// <c>value</c> is negative but not <c>-1</c>, or is above <see cref="ArrayIndexIdRange.MaxValue"/>.
/// </exception>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct IndexGroupId(int value) : IEquatable<IndexGroupId>, IComparable<IndexGroupId>
{
    /// <summary>Sentinel value representing an invalid/unassigned index group ID.</summary>
    public static readonly IndexGroupId Invalid = new(-1);

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
    public int CompareTo(IndexGroupId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(IndexGroupId other) => Value == other.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is IndexGroupId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the first id is smaller than the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is smaller than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(IndexGroupId left, IndexGroupId right) => left.Value < right.Value;

    /// <summary>Whether the first id is larger than the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is larger than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(IndexGroupId left, IndexGroupId right) => left.Value > right.Value;

    /// <summary>Whether the first id is smaller than or equal to the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is not larger than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(IndexGroupId left, IndexGroupId right) => left.Value <= right.Value;

    /// <summary>Whether the first id is larger than or equal to the second. Compares the numbers only.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the number of <paramref name="left"/> is not smaller than that of <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(IndexGroupId left, IndexGroupId right) => left.Value >= right.Value;

    /// <summary>Whether two ids have the same value. Ids of different stacks compare equal when their numbers match.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when both hold the same number.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(IndexGroupId left, IndexGroupId right) => left.Value == right.Value;

    /// <summary>Whether two ids have different values. Ids of different stacks compare equal when their numbers match.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when the numbers differ.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(IndexGroupId left, IndexGroupId right) => left.Value != right.Value;

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
