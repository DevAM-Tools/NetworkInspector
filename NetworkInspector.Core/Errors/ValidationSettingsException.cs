// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
