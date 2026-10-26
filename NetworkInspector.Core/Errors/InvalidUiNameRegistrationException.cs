// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a UI (display) name is invalid —
/// either empty or contains control characters or line breaks. UI names (also interface names read from capture
/// files and sources, which are untrusted input) are shown as single-line text, so they are rejected at registration
/// instead of being sanitized by every consumer later.
/// </summary>
/// <param name="uiName">The rejected UI name; also reported in the message.</param>
public sealed class InvalidUiNameRegistrationException(string uiName)
    : RegistrationException($"Invalid UI name: '{uiName}'. UI names must be non-empty, single-line text without control characters.")
{
    #region Properties

    /// <summary>The invalid UI name that failed validation.</summary>
    public string UiName { get; } = uiName;

    #endregion

    #region Factory Methods

    /// <summary>Creates an invalid-UI-name exception for the given UI name.</summary>
    /// <param name="uiName">The rejected UI name.</param>
    internal static InvalidUiNameRegistrationException For(string uiName) => new(uiName);

    #endregion
}
