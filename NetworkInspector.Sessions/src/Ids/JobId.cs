// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Ids;

/// <summary>
/// Strongly-typed identifier for a session job.
/// Wraps an <see cref="int"/> with value semantics.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
public readonly struct JobId(int value) : IEquatable<JobId>, IComparable<JobId>
{
    /// <summary>Sentinel value representing an invalid or unassigned job ID.</summary>
    public static readonly JobId Invalid = new(-1);

    private readonly int _Value = value;

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

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(JobId other) => _Value.CompareTo(other._Value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(JobId other) => _Value == other._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is JobId other && Equals(other);

    /// <inheritdoc/>
    public override string ToString() => _Value.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(JobId left, JobId right) => left._Value == right._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(JobId left, JobId right) => left._Value != right._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(JobId left, JobId right) => left._Value < right._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(JobId left, JobId right) => left._Value > right._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(JobId left, JobId right) => left._Value <= right._Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(JobId left, JobId right) => left._Value >= right._Value;
}
