// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// A <see cref="BuildDiagnostic"/> for an exception thrown by a protocol during
/// <see cref="NetworkInspector.Core.Protocols.IProtocol.OnStart(Stack)"/>.
/// These errors are collected during <see cref="StackBuilder.Build"/> and exposed via
/// <see cref="IStack.BuildDiagnostics"/> so callers can inspect them explicitly.
/// <para>
/// The failure is isolated per protocol: <see cref="StackBuilder.Build"/> catches the exception, records it here, and
/// continues starting the remaining protocols, so one faulty protocol cannot stop the whole stack from being built.
/// The protocol stays registered, so it may be only partially initialized.
/// </para>
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
        // Only the exception message is used so the text stays short; the full exception (stack trace, inner
        // exceptions) remains available through the Exception property.
        string.Create(
            CultureInfo.InvariantCulture,
            $"Protocol '{ProtocolName}' failed during startup: {Exception.Message}");

    #endregion

    #region Formatting

    /// <inheritdoc/>
    /// <remarks>Replaces the compiler-generated record <c>ToString</c> with the compact "[Severity] message" form.</remarks>
    public override string ToString() => $"[{Severity}] {Message}";

    #endregion
}
