// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Errors;

/// <summary>
/// Describes a filter compilation or evaluation failure together with the source position it
/// applies to.
/// <para>
/// Filter errors are values, never exceptions: every public filter API reports failures through
/// <see cref="FilterResult{T}"/> or a <c>Try*</c> pattern so the hot path stays exception-free.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction; safe to share across threads.</para>
/// </summary>
public sealed class FilterError
{
    #region Construction

    /// <summary>Creates a filter error with optional position information.</summary>
    private FilterError(FilterErrorKind kind, string message, int position, int length)
    {
        Kind = kind;
        Message = message;
        Position = position;
        Length = length;
    }

    #endregion

    #region Properties

    /// <summary>The error category.</summary>
    public FilterErrorKind Kind { get; }

    /// <summary>Human-readable error description.</summary>
    public string Message { get; }

    /// <summary>Character offset into the filter expression, or -1 when unknown.</summary>
    public int Position { get; }

    /// <summary>Span length in characters, or -1 when unknown.</summary>
    public int Length { get; }

    /// <summary>Whether <see cref="Position"/> and <see cref="Length"/> carry usable values.</summary>
    public bool HasPosition => Position >= 0;

    #endregion

    #region Factories

    /// <summary>Creates a lexer error at a specific position.</summary>
    public static FilterError Lexer(string message, int position, int length) =>
        new(FilterErrorKind.LexerError, message, position, length);

    /// <summary>Creates a syntax error at a specific position.</summary>
    public static FilterError Syntax(string message, int position, int length) =>
        new(FilterErrorKind.SyntaxError, message, position, length);

    /// <summary>Creates an invalid-literal error at a specific position.</summary>
    public static FilterError InvalidValue(string message, int position, int length) =>
        new(FilterErrorKind.InvalidValue, message, position, length);

    /// <summary>Creates an error for a language construct removed in v1.</summary>
    public static FilterError UnsupportedFeature(string feature, int position, int length) =>
        new(
            FilterErrorKind.UnsupportedFeature,
            $"'{feature}' was removed from the filter language in v1",
            position,
            length);

    /// <summary>Creates an error for a field name that is unknown on the compile-time stack.</summary>
    public static FilterError UnknownField(string name, int position, int length) =>
        new(FilterErrorKind.UnknownField, $"Unknown field or protocol '{name}'", position, length);

    /// <summary>Creates an error for a protocol name that is unknown on the compile-time stack.</summary>
    public static FilterError UnknownProtocol(string name, int position, int length) =>
        new(FilterErrorKind.UnknownProtocol, $"Unknown protocol '{name}'", position, length);

    /// <summary>Creates an operand/operator mismatch error.</summary>
    public static FilterError TypeMismatch(string message, int position, int length) =>
        new(FilterErrorKind.TypeMismatch, message, position, length);

    /// <summary>Creates the error returned when a non-empty expression is compiled without a stack.</summary>
    public static FilterError StackRequired() =>
        new(
            FilterErrorKind.StackRequired,
            "A non-empty filter expression requires a protocol stack. Use Filter.Compile(expression, stack).",
            -1,
            -1);

    /// <summary>Creates an internal code-generation error.</summary>
    public static FilterError Compiler(string message) =>
        new(FilterErrorKind.CompilerError, message, -1, -1);

    /// <summary>Creates the error raised when a user-supplied compile callback throws.</summary>
    public static FilterError CallbackFailed(string message) =>
        new(FilterErrorKind.CallbackFailed, $"Filter compile callback threw: {message}", -1, -1);

    /// <summary>Creates a packet-evaluation error.</summary>
    public static FilterError Runtime(string message) =>
        new(FilterErrorKind.RuntimeError, message, -1, -1);

    /// <summary>Creates the error raised when a stateful filter is queried out of ascending packet order.</summary>
    public static FilterError OutOfOrder(int packetId, int highestEvaluatedId) =>
        new(
            FilterErrorKind.OutOfOrder,
            $"Stateful filter requires ascending packet order: packet {packetId} follows {highestEvaluatedId}. " +
            "Call ResetState() before replaying earlier packets.",
            -1,
            -1);

    #endregion

    #region Formatting

    /// <inheritdoc />
    public override string ToString()
    {
        if (HasPosition)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{Kind}] at {Position} (length {Length}): {Message}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"[{Kind}]: {Message}");
    }

    #endregion
}
