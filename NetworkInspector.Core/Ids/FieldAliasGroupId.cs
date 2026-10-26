// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for a registered field alias group.
/// Wraps an <see cref="int"/> with value semantics.
/// <para>
/// Field alias groups are protocol-owned metadata entities that name a set of canonical
/// member field IDs (e.g., "eth.addr" -> { eth.dst, eth.src }). Alias group identifiers
/// live in their own ID space, independent of <see cref="FieldId"/> and
/// <see cref="ProtocolTableId"/>; an alias name therefore never resolves through
/// <see cref="IStack.GetFieldId(string)"/>.
/// </para>
/// </summary>
/// <remarks>
/// Creates a new <see cref="FieldAliasGroupId"/> with the specified raw value.
/// <para>
/// Valid values are <c>0</c> to <see cref="ArrayIndexIdRange.MaxValue"/> (the id is used as an array index) plus the single
/// sentinel <c>-1</c> (<see cref="Invalid"/>). <c>default(FieldAliasGroupId)</c> is id <c>0</c>, which is a valid id and not
/// <see cref="Invalid"/>; use <see cref="Invalid"/> explicitly to mean "no alias group".
/// </para>
/// </remarks>
/// <param name="value">The raw id: <c>-1</c> for <see cref="Invalid"/>, otherwise an index in the valid range.</param>
/// <exception cref="ArgumentOutOfRangeException">
/// <c>value</c> is negative but not <c>-1</c>, or is above <see cref="ArrayIndexIdRange.MaxValue"/>.
/// </exception>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct FieldAliasGroupId(int value) : IEquatable<FieldAliasGroupId>, IComparable<FieldAliasGroupId>
{
    #region Constants

    /// <summary>Sentinel value representing an invalid/unassigned field alias group ID.</summary>
    public static readonly FieldAliasGroupId Invalid = new(-1);

    #endregion

    #region Properties

    /// <summary>The raw numeric value of this identifier.</summary>
    public int Value { get; } = _StoreValidated(value);


    /// <summary>Whether this ID represents a valid (assigned) identifier.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ArrayIndexIdRange.IsValidIndex(Value);
    }

    #endregion

    #region Equality & Formatting

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FieldAliasGroupId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FieldAliasGroupId other) => Value == other.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is FieldAliasGroupId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Operators

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value < right.Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value > right.Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value <= right.Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value >= right.Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value == right.Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FieldAliasGroupId left, FieldAliasGroupId right) => left.Value != right.Value;

    #endregion

    #region Private helpers

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

    #endregion
}
