// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// A <see cref="BuildDiagnostic"/> for an unresolved deferred callback registered via
/// <c>WhenProtocolRegistered</c>, <c>WhenFieldRegistered</c>, or <c>WhenProtocolTableRegistered</c>.
/// Indicates that a protocol, field, or dispatch table was referenced before or without
/// ever being registered during <see cref="StackBuilder.Build"/>.
/// </summary>
/// <param name="EntityKind">The type of entity that was never registered.</param>
/// <param name="Name">
/// The name of the entity (protocol name, field name, or table name) that was referenced
/// by the unresolved callback.
/// </param>
/// <param name="CallbackCount">
/// Number of deferred callbacks registered for <paramref name="Name"/> that never fired.
/// </param>
public sealed record BuildCallbackWarning(
    BuildCallbackWarningKind EntityKind,
    string Name,
    int CallbackCount) : BuildDiagnostic
{
    #region BuildDiagnostic

    /// <inheritdoc/>
    public override BuildDiagnosticSeverity Severity => BuildDiagnosticSeverity.Warning;

    /// <inheritdoc/>
    public override string Message =>
        $"Unresolved deferred callback(s) ({CallbackCount}) for {EntityKind} '{Name}'.";

    #endregion

    #region Formatting

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Message}";

    #endregion
}
