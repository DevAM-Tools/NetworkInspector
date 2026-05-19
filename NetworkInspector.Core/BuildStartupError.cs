// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// A <see cref="BuildDiagnostic"/> for an exception thrown by a protocol during
/// <see cref="NetworkInspector.Core.Protocols.IProtocol.OnStart(Stack)"/>.
/// These errors are collected during <see cref="StackBuilder.Build"/> and exposed via
/// <see cref="IStack.BuildDiagnostics"/> so callers can inspect them explicitly.
/// </summary>
/// <param name="ProtocolId">The protocol whose startup hook threw.</param>
/// <param name="ProtocolName">Machine-readable protocol name.</param>
/// <param name="ProtocolUiName">Human-readable protocol name.</param>
/// <param name="Exception">The original exception thrown by the startup hook.</param>
public sealed record BuildStartupError(
    ProtocolId ProtocolId,
    string ProtocolName,
    string ProtocolUiName,
    Exception Exception) : BuildDiagnostic
{
    #region BuildDiagnostic

    /// <inheritdoc/>
    public override BuildDiagnosticSeverity Severity => BuildDiagnosticSeverity.Error;

    /// <inheritdoc/>
    public override string Message =>
        $"Protocol '{ProtocolName}' failed during startup: {Exception.Message}";

    #endregion

    #region Formatting

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Message}";

    #endregion
}