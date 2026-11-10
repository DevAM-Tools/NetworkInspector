// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Centralized throw helpers. Prevents the JIT from inlining exception-throwing
/// code into hot-path methods, improving branch prediction and code density.
/// <para>
/// Why: a <c>throw</c> expression (and the exception construction before it) adds code and a call to the method that
/// contains it, which can push a tiny hot method such as an indexer or a field append over the JIT's inlining budget. Moving
/// the throw into a <see cref="MethodImplOptions.NoInlining"/> method keeps the guarded method small and inlinable and
/// places the cold code out of line. Every helper is marked <see cref="DoesNotReturnAttribute"/> so flow analysis knows
/// the guarded path ends there. The generic <c>Throw...&lt;T&gt;</c> variants return <c>T</c> only so they can
/// stand in a <c>return</c> statement of a value-returning getter; they never return a value. Thread-safe (stateless).
/// </para>
/// </summary>
internal static class ThrowHelpers
{
    /// <summary>
    /// Throws a <see cref="Errors.FieldAppendException"/> wrapping the given parse error.
    /// Called when a field tree mutation fails (e.g., maximum field count exceeded).
    /// </summary>
    /// <param name="error">The error that stopped the mutation; carried by the exception as its <c>ParseError</c>.</param>
    /// <exception cref="Errors.FieldAppendException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowFieldAppend(ParseError error)
        => throw Errors.FieldAppendException.FromError(error);

    /// <summary>
    /// Throws when accessing Value on a failed ParseResult. Callers that must not throw use the success check first
    /// (expected parse failures are results, not exceptions); this guards only the misuse of reading the wrong side.
    /// </summary>
    /// <typeparam name="T">The type the caller returns; no value is ever produced.</typeparam>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowParseResultNoValue<T>()
        => throw new InvalidOperationException("Cannot access Value on a failed ParseResult.");

    /// <summary>Throws when accessing Error on a successful ParseResult.</summary>
    /// <typeparam name="T">The type the caller returns; no value is ever produced.</typeparam>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowParseResultNoError<T>()
        => throw new InvalidOperationException("Cannot access Error on a successful ParseResult.");

    /// <summary>
    /// Throws when the custom text of a field changes after the packet was sealed (see <c>Packet.SetFieldCustomText</c>).
    /// This is a broken contract of the calling dissector, not an expected failure: custom text is final at append, and a
    /// sealed packet is read by other threads. The message names the rule and the remedy.
    /// </summary>
    /// <param name="fieldId">The field whose text was changed; named in the message.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowCustomTextAfterSeal(FieldId fieldId)
        => throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Custom text cannot change after the packet is sealed (field id {fieldId.Value}). Set the text when appending the field."));

    /// <summary>
    /// Throws when a skip-tree cursor is read outside the synthetic root or an in-flight lazy populator.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowSkipFieldTreeValue()
        => throw new InvalidOperationException(
            "Field values are not available on a packet parsed with FieldTreeMode.Skip except at the root and during an "
            + "in-flight skip-tree lazy populator.");

    /// <summary>Throws when a parse factory receives a <see cref="FieldTreeMode"/> other than Build or Skip.</summary>
    /// <param name="fieldTree">The rejected mode; reported as the actual value of the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowInvalidFieldTreeMode(FieldTreeMode fieldTree)
        => throw new ArgumentOutOfRangeException(
            nameof(fieldTree),
            fieldTree,
            "fieldTree must be FieldTreeMode.Build or FieldTreeMode.Skip.");
}
