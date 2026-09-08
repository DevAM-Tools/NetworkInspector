// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Centralized throw helpers. Prevents the JIT from inlining exception-throwing
/// code into hot-path methods, improving branch prediction and code density.
/// </summary>
internal static class ThrowHelpers
{
    #region Guard Helpers

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
        => throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"F64 setting value must be finite, got {value}."));

    /// <summary>
    /// Throws when a skip-tree cursor is read outside the synthetic root or an in-flight lazy populator.
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowSkipFieldTreeValue()
        => throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Field values are not available on a packet parsed with FieldTreeMode.Skip except at the root and during an in-flight skip-tree lazy populator."));

    /// <summary>Throws when a parse factory receives a <see cref="FieldTreeMode"/> other than Build or Skip.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowInvalidFieldTreeMode(FieldTreeMode fieldTree)
        => throw new ArgumentOutOfRangeException(
            nameof(fieldTree),
            fieldTree,
            "fieldTree must be FieldTreeMode.Build or FieldTreeMode.Skip.");

    #endregion

}
