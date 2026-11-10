// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Single source of truth for array-indexed identifier value ranges.
/// Valid indices are <c>0 … Array.MaxLength - 1</c>; <see cref="InvalidValue"/> is the only
/// permitted negative value for sentinel IDs.
/// </summary>
/// <remarks>
/// The id structs (<see cref="FieldId"/>, <see cref="IndexGroupId"/>, <see cref="PacketId"/>, and the others in this
/// namespace that wrap an <see cref="int"/>) are used directly as indexes into arrays and slabs. Their valid range is therefore
/// bounded by what a single .NET array can address, and that bound lives here once instead of being repeated in each id type.
/// The range check is a single unsigned comparison, so the id constructors and <c>IsValid</c> can run it on every creation.
/// Stateless static class, safe to call from any thread.
/// </remarks>
public static class ArrayIndexIdRange
{
    /// <summary>Sentinel value for invalid/unassigned array-indexed identifiers.</summary>
    public const int InvalidValue = -1;

    /// <summary>
    /// Largest valid array index for a single <see cref="Array"/> in the current runtime
    /// (<c>Array.MaxLength - 1</c>). Read from the runtime rather than hard-coded, so it follows the runtime's own limit.
    /// </summary>
    public static int MaxValue => Array.MaxLength - 1;

    /// <summary>
    /// Maximum number of valid IDs in the range 0 … <see cref="MaxValue"/>
    /// (= <see cref="Array.MaxLength"/>). Proven safe: <see cref="Array.MaxLength"/> is always
    /// less than <see cref="int.MaxValue"/> on current .NET runtimes.
    /// </summary>
    public static int MaxCount => MaxValue + 1;

    /// <summary>Whether <paramref name="value"/> is the invalid sentinel (<see cref="InvalidValue"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInvalidSentinel(int value) => value == InvalidValue;

    /// <summary>
    /// Whether <paramref name="value"/> is a valid dense array index (0 … <see cref="MaxValue"/>).
    /// Uses an unsigned compare so both negative values and values above <see cref="MaxValue"/>
    /// are rejected in one branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidIndex(int value) => (uint)value <= (uint)MaxValue; // MaxValue is positive, so the cast is exact.

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
    /// ID constructors remain the secondary safety net via <see cref="ValidateIndexOrThrow"/>.
    /// Checking before the id is issued lets the caller report a capacity error that names the entity
    /// instead of the generic out-of-range error an id constructor would raise.
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

    /// <summary>Throws the out-of-range error of <see cref="ValidateIndexOrThrow"/>; never returns.</summary>
    /// <param name="value">The rejected value; reported as the actual value of the exception.</param>
    /// <param name="paramName">The parameter name reported by the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always; the message names the valid range.</exception>
    /// <remarks>
    /// The throw helpers are NoInlining so the exception construction and message formatting stay out of the inlined validation
    /// methods above, which keeps the inlined fast path small.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowIndexOutOfRange(int value, string paramName) =>
        throw new ArgumentOutOfRangeException(
            paramName,
            value,
            Invariant($"Value must be in the range 0..{MaxValue.ToString(CultureInfo.InvariantCulture)} ") +
            Invariant($"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)})."));

    /// <summary>Throws the capacity error of <see cref="ThrowIfInvalidNextIndex"/>; never returns.</summary>
    /// <param name="entityName">Singular noun that the message uses to name the counted entity.</param>
    /// <exception cref="InvalidOperationException">Always; the message names the entity and the valid id range.</exception>
    /// <remarks>NoInlining for the same reason as <see cref="_ThrowIndexOutOfRange"/>.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowCapacityExceeded(string entityName) =>
        throw new InvalidOperationException(
            Invariant($"Maximum {entityName} count exceeded. Valid {entityName} IDs are 0..{MaxValue.ToString(CultureInfo.InvariantCulture)} ") +
            Invariant($"(Array.MaxLength={Array.MaxLength.ToString(CultureInfo.InvariantCulture)})."));
}
