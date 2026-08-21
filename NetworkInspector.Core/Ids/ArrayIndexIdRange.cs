// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Single source of truth for array-indexed identifier value ranges.
/// Valid indices are <c>0 … Array.MaxLength - 1</c>; <see cref="InvalidValue"/> is the only
/// permitted negative value for sentinel IDs.
/// </summary>
public static class ArrayIndexIdRange
{
    #region Constants

    /// <summary>Sentinel value for invalid/unassigned array-indexed identifiers.</summary>
    public const int InvalidValue = -1;

    #endregion

    #region Properties

    /// <summary>
    /// Largest valid array index for a single <see cref="Array"/> in the current runtime
    /// (<c>Array.MaxLength - 1</c>).
    /// </summary>
    public static int MaxValue => Array.MaxLength - 1;

    /// <summary>
    /// Maximum number of valid IDs in the range 0 … <see cref="MaxValue"/>
    /// (= <see cref="Array.MaxLength"/>). Proven safe: <see cref="Array.MaxLength"/> is always
    /// less than <see cref="int.MaxValue"/> on current .NET runtimes.
    /// </summary>
    public static int MaxCount => MaxValue + 1;

    #endregion

    #region Validation

    /// <summary>Whether <paramref name="value"/> is the invalid sentinel (<see cref="InvalidValue"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInvalidSentinel(int value) => value == InvalidValue;

    /// <summary>
    /// Whether <paramref name="value"/> is a valid dense array index (0 … <see cref="MaxValue"/>).
    /// Uses an unsigned compare so both negative values and values above <see cref="MaxValue"/>
    /// are rejected in one branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidIndex(int value) => (uint)value <= (uint)MaxValue;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="value"/> is not a valid index.
    /// </summary>
    /// <param name="value">Candidate index.</param>
    /// <param name="paramName">Parameter name for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is not in the range 0 … <see cref="MaxValue"/>
    /// (all negatives, including <see cref="InvalidValue"/>, are rejected).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ValidateIndexOrThrow(int value, string paramName)
    {
        if (!IsValidIndex(value))
        {
            _ThrowIndexOutOfRange(value, paramName);
        }
    }

    /// <summary>
    /// Primary capacity guard for frame/packet allocation paths.
    /// Throws when <paramref name="nextIndex"/> is not a valid array-index ID.
    /// ID constructors remain the secondary fangnetz via <see cref="ValidateIndexOrThrow"/>.
    /// </summary>
    /// <param name="nextIndex">Next ID that would be issued (0 … <see cref="MaxValue"/>).</param>
    /// <param name="entityName">Singular noun for the message (e.g. <c>frame</c>, <c>packet</c>).</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="nextIndex"/> is outside 0 … <see cref="MaxValue"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfInvalidNextIndex(int nextIndex, string entityName)
    {
        if (!IsValidIndex(nextIndex))
        {
            _ThrowCapacityExceeded(entityName);
        }
    }

    #endregion

    #region Private helpers

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIndexOutOfRange(int value, string paramName) =>
        throw new ArgumentOutOfRangeException(
            paramName,
            value,
            $"Value must be in the range 0..{MaxValue.ToString(CultureInfo.InvariantCulture)} " +
            $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowCapacityExceeded(string entityName) =>
        throw new InvalidOperationException(
            $"Maximum {entityName} count exceeded. Valid {entityName} IDs are 0..{MaxValue.ToString(CultureInfo.InvariantCulture)} " +
            $"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)}).");

    #endregion
}
