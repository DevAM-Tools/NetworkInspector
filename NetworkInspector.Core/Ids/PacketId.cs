// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for a parsed packet.
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
/// <remarks>Creates a new <see cref="PacketId"/> with the specified raw value.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct PacketId(int value) : IEquatable<PacketId>, IComparable<PacketId>
{
    #region Constants

    /// <summary>Sentinel value representing an invalid/unassigned packet ID.</summary>
    public static readonly PacketId Invalid = new(-1);

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
    public int CompareTo(PacketId other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PacketId other) => _Value == other._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is PacketId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => _Value.ToString();

    #endregion

    #region Operators

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(PacketId left, PacketId right) => left._Value < right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(PacketId left, PacketId right) => left._Value > right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(PacketId left, PacketId right) => left._Value <= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(PacketId left, PacketId right) => left._Value >= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(PacketId left, PacketId right) => left._Value == right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(PacketId left, PacketId right) => left._Value != right._Value;

    /// <summary>Implicitly converts an <see cref="int"/> to a <see cref="PacketId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator PacketId(int value) => new(value);

    /// <summary>Implicitly converts a <see cref="PacketId"/> to an <see cref="int"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator int(PacketId id) => id._Value;

    #endregion
}
