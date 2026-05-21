// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a setting value fails a validation constraint
/// (e.g., out of min/max range, invalid enum value, or invalid factory parameters).
/// </summary>
/// <remarks>Creates a new validation settings exception.</remarks>
public sealed class ValidationSettingsException(string message)
    : SettingsException($"Validation error: {message}")
{
    #region Factory Methods

    /// <summary>Creates a validation exception with the specified message.</summary>
    internal static ValidationSettingsException For(string message) => new(message);

    #endregion
}
