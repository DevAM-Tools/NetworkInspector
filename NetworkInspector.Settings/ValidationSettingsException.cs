// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Thrown when a setting value fails a validation constraint
/// (e.g., out of min/max range, invalid enum value, or invalid factory parameters).
/// The text is prefixed with "Validation error: ".
/// </summary>
/// <param name="message">Description of the violated constraint, without the prefix.</param>
public sealed class ValidationSettingsException(string message)
    : SettingsException(Invariant($"Validation error: {message}"))
{
    #region Creating the exception

    /// <summary>Creates a validation exception with the specified message.</summary>
    /// <param name="message">Description of the violated constraint.</param>
    internal static ValidationSettingsException For(string message) => new(message);

    #endregion
}
