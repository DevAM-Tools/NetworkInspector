// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for a captured frame.
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
/// <remarks>Creates a new <see cref="FrameId"/> with the specified raw value.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct FrameId(int value) : IEquatable<FrameId>, IComparable<FrameId>
{
    #region Constants

    /// <summary>Sentinel value representing an invalid/unassigned frame ID.</summary>
    public static readonly FrameId Invalid = new(-1);

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
    public int CompareTo(FrameId other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FrameId other) => _Value == other._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is FrameId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => _Value.ToString();

    #endregion

    #region Operators

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FrameId left, FrameId right) => left._Value < right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FrameId left, FrameId right) => left._Value > right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FrameId left, FrameId right) => left._Value <= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FrameId left, FrameId right) => left._Value >= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FrameId left, FrameId right) => left._Value == right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FrameId left, FrameId right) => left._Value != right._Value;

    /// <summary>Implicitly converts an <see cref="int"/> to a <see cref="FrameId"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator FrameId(int value) => new(value);

    /// <summary>Implicitly converts a <see cref="FrameId"/> to an <see cref="int"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator int(FrameId id) => id._Value;

    #endregion
}