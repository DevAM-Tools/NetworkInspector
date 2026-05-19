// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a UI (display) name is invalid —
/// either empty or contains control characters or line breaks.
/// </summary>
/// <remarks>Creates a new invalid-UI-name registration exception.</remarks>
public sealed class InvalidUiNameRegistrationException(string uiName)
    : RegistrationException($"Invalid UI name: '{uiName}'. UI names must be non-empty, single-line text without control characters.")
{
    #region Properties

    /// <summary>The invalid UI name that failed validation.</summary>
    public string UiName { get; } = uiName;

    #endregion

    #region Factory Methods

    /// <summary>Creates an invalid-UI-name exception for the given UI name.</summary>
    internal static InvalidUiNameRegistrationException For(string uiName) => new(uiName);

    #endregion
}