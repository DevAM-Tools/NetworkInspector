// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

    #endregion
}