// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>Discriminant for the kind of parse error.</summary>
public enum ParseErrorKind : byte
{
    #region Enum Values

    /// <summary>Not enough data to parse the protocol header.</summary>
    InsufficientData = 0,
    /// <summary>Data is present but structurally invalid.</summary>
    InvalidData = 1,
    /// <summary>Protocol-specific custom error.</summary>
    Custom = 2,
    /// <summary>Internal logic error (should not occur).</summary>
    InternalError = 3,
    /// <summary>Failed to append a field to the packet tree.</summary>
    FieldAppendFailed = 4,
    /// <summary>Expected field type does not match actual.</summary>
    FieldTypeMismatch = 5,
    /// <summary>
    /// The dispatch table id is invalid or the table is not registered on this stack.
    /// Distinct from a table lookup that finds zero protocols for a key.
    /// </summary>
    ProtocolTableMissing = 6,

    #endregion
}
