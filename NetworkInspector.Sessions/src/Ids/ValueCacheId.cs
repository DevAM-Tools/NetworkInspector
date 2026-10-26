// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Ids;

/// <summary>
/// Strongly-typed identifier for a session value-cache subscription.
/// Wraps an <see cref="int"/> with value semantics. Independent of <see cref="ListenerId"/>: value caches have their
/// own id sequence, so a <see cref="ValueCacheId"/> and a <see cref="ListenerId"/> with the same number are unrelated.
/// <para>
/// The session issues value-cache ids densely from zero, and ids are used as array indexes, so the valid range is the
/// one defined by <see cref="Core.Ids.ArrayIndexIdRange"/>. The only negative value accepted is <see cref="Invalid"/>.
/// Caution: <c>default(ValueCacheId)</c> has value 0, which is a <b>valid</b> id, not <see cref="Invalid"/>; compare
/// against <see cref="Invalid"/> or test <see cref="IsValid"/> instead of relying on <see langword="default"/>.
/// </para>
/// <para>Immutable value type; thread safety is not applicable.</para>
/// </summary>
/// <param name="value">The raw id; <c>-1</c> or an index in <c>0 … ArrayIndexIdRange.MaxValue</c>.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative but not <c>-1</c>, or above the maximum index.</exception>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct ValueCacheId(int value) : IEquatable<ValueCacheId>, IComparable<ValueCacheId>
{
    #region Fields

    /// <summary>Sentinel value representing an invalid or unassigned value-cache ID.</summary>
    public static readonly ValueCacheId Invalid = new(-1);

    /// <summary>The raw numeric value of this identifier.</summary>
    public int Value { get; } = _StoreValidated(value);

    /// <summary>Whether this ID represents a valid (assigned) identifier.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Core.Ids.ArrayIndexIdRange.IsValidIndex(Value);
    }

    #endregion

    #region API

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(ValueCacheId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ValueCacheId other) => Value == other.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is ValueCacheId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(ValueCacheId left, ValueCacheId right) => left.Value == right.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(ValueCacheId left, ValueCacheId right) => left.Value != right.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(ValueCacheId left, ValueCacheId right) => left.Value < right.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(ValueCacheId left, ValueCacheId right) => left.Value > right.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(ValueCacheId left, ValueCacheId right) => left.Value <= right.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(ValueCacheId left, ValueCacheId right) => left.Value >= right.Value;

    /// <summary>
    /// Constructor guard: lets the <see cref="Invalid"/> sentinel through and rejects every other out-of-range value, so
    /// an id that exists is always either the sentinel or a usable array index. Inlined because it runs on every
    /// construction; the throw path is out of line in <see cref="Core.Ids.ArrayIndexIdRange"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int _StoreValidated(int value)
    {
        if (!Core.Ids.ArrayIndexIdRange.IsInvalidSentinel(value))
        {
            Core.Ids.ArrayIndexIdRange.ValidateIndexOrThrow(value, nameof(value));
        }

        return value;
    }

    #endregion
}
