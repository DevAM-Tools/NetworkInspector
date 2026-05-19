// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// Centralized throw helpers. Prevents the JIT from inlining exception-throwing
/// code into hot-path methods, improving branch prediction and code density.
/// </summary>
internal static class ThrowHelpers
{
    #region Guard Helpers

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowArgumentOutOfRange(string paramName)
        => throw new ArgumentOutOfRangeException(paramName);

    /// <summary>Throws <see cref="ArgumentNullException"/>.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowArgumentNull(string paramName)
        => throw new ArgumentNullException(paramName);

    /// <summary>Throws <see cref="InvalidOperationException"/>.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowInvalidOperation(string message)
        => throw new InvalidOperationException(message);

    /// <summary>
    /// Throws a <see cref="Errors.FieldAppendException"/> wrapping the given parse error.
    /// Called when a field tree mutation fails (e.g., maximum field count exceeded).
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowFieldAppend(ParseError error)
        => throw Errors.FieldAppendException.FromError(error);

    /// <summary>Throws when accessing Value on a failed ParseResult.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowParseResultNoValue<T>()
        => throw new InvalidOperationException("Cannot access Value on a failed ParseResult.");

    /// <summary>Throws when accessing Error on a successful ParseResult.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowParseResultNoError<T>()
        => throw new InvalidOperationException("Cannot access Error on a successful ParseResult.");

    /// <summary>Throws when a non-finite F64 value is encountered during JSON serialization.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowNonFiniteF64(double value)
        => throw new InvalidOperationException($"F64 setting value must be finite, got {value}.");

    #endregion

}