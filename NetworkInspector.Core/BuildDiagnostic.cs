// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Base type for all non-fatal diagnostics produced during <see cref="StackBuilder.Build"/>.
/// <para>
/// Callers should inspect <see cref="IStack.BuildDiagnostics"/> after build to check for
/// configuration problems (<see cref="BuildCallbackWarning"/>) and protocol startup failures
/// (<see cref="BuildStartupError"/>).
/// </para>
/// </summary>
public abstract record BuildDiagnostic
{
    #region Properties

    /// <summary>Severity of this diagnostic.</summary>
    public abstract BuildDiagnosticSeverity Severity
    {
        get;
    }

    /// <summary>Human-readable description of the issue.</summary>
    public abstract string Message
    {
        get;
    }

    #endregion
}
