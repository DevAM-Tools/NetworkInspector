// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// A <see cref="BuildDiagnostic"/> for an unresolved deferred callback registered via
/// <c>WhenProtocolRegistered</c>, <c>WhenFieldRegistered</c>, or <c>WhenProtocolTableRegistered</c>.
/// Indicates that a protocol, field, or dispatch table was referenced before or without
/// ever being registered during <see cref="StackBuilder.Build"/>.
/// <para>
/// The same record also reports the missing required frame protocol
/// (<see cref="BuildCallbackWarningKind.MissingFrameProtocol"/>), which is not backed by a deferred callback.
/// Both cases are non-fatal: the stack is still built, so a host can decide whether the gap matters for it.
/// This type is an immutable record and therefore safe to share between threads.
/// </para>
/// </summary>
/// <param name="EntityKind">The type of entity that was never registered.</param>
/// <param name="Name">
/// The name of the entity (protocol name, field name, or table name) that was referenced
/// by the unresolved callback.
/// </param>
/// <param name="CallbackCount">
/// Number of deferred callbacks registered for <paramref name="Name"/> that never fired.
/// Always 0 for <see cref="BuildCallbackWarningKind.MissingFrameProtocol"/>, because no callback is involved there.
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
        // The frame-protocol case has its own wording because it carries no callback count and states the
        // consequence (link-layer parsing is skipped). Invariant culture keeps the text independent of the host locale.
        EntityKind == BuildCallbackWarningKind.MissingFrameProtocol
            ? string.Create(CultureInfo.InvariantCulture,
                $"Required protocol '{Name}' was not registered; link-layer parsing will be skipped.")
            : string.Create(CultureInfo.InvariantCulture,
                $"Unresolved deferred callback(s) ({CallbackCount}) for {EntityKind} '{Name}'.");

    #endregion

    #region Formatting

    /// <inheritdoc/>
    /// <remarks>
    /// Replaces the compiler-generated record <c>ToString</c> (which would print every member, including the
    /// callback count) with the compact "[Severity] message" form used for diagnostics output.
    /// </remarks>
    public override string ToString() => $"[{Severity}] {Message}";

    #endregion
}
