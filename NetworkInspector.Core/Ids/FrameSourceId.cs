// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Strongly-typed identifier for a frame source.
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
/// <remarks>Creates a new <see cref="FrameSourceId"/> with the specified raw value.</remarks>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct FrameSourceId(int value) : IEquatable<FrameSourceId>, IComparable<FrameSourceId>
{
    #region Constants

    /// <summary>Sentinel value representing an invalid/unassigned frame source ID.</summary>
    public static readonly FrameSourceId Invalid = new(-1);

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
    public int CompareTo(FrameSourceId other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(FrameSourceId other) => _Value == other._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is FrameSourceId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => _Value.ToString();

    #endregion

    #region Operators

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(FrameSourceId left, FrameSourceId right) => left._Value < right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(FrameSourceId left, FrameSourceId right) => left._Value > right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(FrameSourceId left, FrameSourceId right) => left._Value <= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(FrameSourceId left, FrameSourceId right) => left._Value >= right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(FrameSourceId left, FrameSourceId right) => left._Value == right._Value;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(FrameSourceId left, FrameSourceId right) => left._Value != right._Value;

    #endregion
}
