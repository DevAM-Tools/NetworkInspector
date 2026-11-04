// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>Error codes of <see cref="FieldSelection.TryCreate"/>.</summary>
public enum FieldSelectionErrorCode
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>No field was requested.</summary>
    Empty = 1,

    /// <summary>The name is empty, whitespace, or not registered in the stack.</summary>
    UnknownField = 2,

    /// <summary>The field is a container (protocol, group) and has no value to export.</summary>
    NoValue = 3,

    /// <summary>The same field was requested twice.</summary>
    Duplicate = 4,
}
