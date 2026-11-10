// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Exception thrown when a field tree mutation fails (e.g., maximum field count exceeded).
/// <para>
/// This is an <em>unexpected</em> failure — under normal operation, field appends always succeed.
/// The only realistic trigger is exceeding the per-packet field limit: 65 534 fields including the root. Regular appends stop
/// one slot earlier, at 65 533, so that the error field describing the failure still fits into the tree.
/// </para>
/// <para>
/// Caught at the dispatch boundary of <c>PacketProtocol.Parse</c>, in <see cref="Packet.MaterializeLazyField"/>, or by the
/// outer guard <c>Packet._ParseAndSeal</c>; each records it as a <c>packet.error</c> field.
/// </para>
/// <para>
/// Because an append failure is unexpected, it is signaled by an exception that unwinds the parser to the nearest
/// parse boundary rather than by a result every append call site would have to check. The wrapped
/// <see cref="ParseError"/> keeps the structured reason. The type has a private constructor and an internal factory, so
/// only the core can raise it.
/// </para>
/// </summary>
public sealed class FieldAppendException : Exception
{
    /// <summary>The underlying parse error that triggered this exception.</summary>
    public ParseError ParseError
    {
        get;
    }

    /// <summary>Creates a new field append exception wrapping the given parse error.</summary>
    /// <param name="error">The structured reason; its text becomes the exception message.</param>
    private FieldAppendException(ParseError error)
        : base(error.ToString())
    {
        ParseError = error;
    }

    /// <summary>Creates a <see cref="FieldAppendException"/> from the given parse error.</summary>
    /// <param name="error">The structured reason the append failed.</param>
    /// <returns>The exception, ready to be thrown by the core.</returns>
    internal static FieldAppendException FromError(ParseError error) => new(error);
}
