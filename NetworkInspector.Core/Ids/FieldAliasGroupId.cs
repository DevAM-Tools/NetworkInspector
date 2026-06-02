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
/// <remarks>Creates a new <see cref="FieldAliasGroupId"/> with the specified raw value.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct FieldAliasGroupId(int value) : IEquatable<FieldAliasGroupId>, IComparable<FieldAliasGroupId>
{
    #region Constants

    /// <summary>Sentinel value representing an invalid/unassigned field alias group ID.</summary>
    public static readonly FieldAliasGroupId Invalid = new(-1);

    #endregion

    #region Fields

    private readonly int _Value = value;

    #endregion

    #region Properties

    /// <summary>The raw numeric value of this identifier.</summary>
    public int Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value;
    }

    /// <summary>Whether this ID represents a valid (assigned) identifier.</summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value >= 0;
    }

    #endregion

    #region Equality & Formatting

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(FieldAliasGroupId other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FieldAliasGroupId other) => _Value == other._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is FieldAliasGroupId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => _Value.ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Operators

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value < right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value > right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value <= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value >= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value == right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FieldAliasGroupId left, FieldAliasGroupId right) => left._Value != right._Value;

    #endregion
}
