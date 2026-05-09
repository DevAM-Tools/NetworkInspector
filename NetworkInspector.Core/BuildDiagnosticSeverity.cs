// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core;

/// <summary>Severity level of a <see cref="BuildDiagnostic"/> produced during <see cref="StackBuilder.Build"/>.</summary>
public enum BuildDiagnosticSeverity
{
    #region Enum Values

    /// <summary>A non-fatal configuration issue that may require attention but does not prevent the stack from functioning.</summary>
    Warning,

    /// <summary>A protocol startup exception that may indicate a broken protocol.</summary>
    Error,

    #endregion
}
