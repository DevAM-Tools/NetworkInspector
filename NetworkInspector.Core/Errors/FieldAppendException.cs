// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Exception thrown when a field tree mutation fails (e.g., maximum field count exceeded).
/// <para>
/// This is an <em>unexpected</em> failure — under normal operation, field appends always succeed.
/// The only realistic trigger is exceeding the per-packet field limit (65 534).
/// </para>
/// <para>
/// Caught at parse boundaries (<see cref="Packet.ParseFrameInternal"/>,
/// <see cref="Packet.MaterializeLazyField"/>)
/// and converted into a packet-level error string.
/// </para>
/// </summary>
public sealed class FieldAppendException : Exception
{
    #region Properties

    /// <summary>The underlying parse error that triggered this exception.</summary>
    public ParseError ParseError
    {
        get;
    }

    #endregion

    #region Constructors

    /// <summary>Creates a new field append exception wrapping the given parse error.</summary>
    private FieldAppendException(ParseError error)
        : base(error.ToString())
    {
        ParseError = error;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a <see cref="FieldAppendException"/> from the given parse error.</summary>
    internal static FieldAppendException FromError(ParseError error) => new(error);

    #endregion
}
