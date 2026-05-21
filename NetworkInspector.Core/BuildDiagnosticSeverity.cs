// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
