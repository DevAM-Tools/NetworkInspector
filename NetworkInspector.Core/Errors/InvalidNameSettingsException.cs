// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a machine-readable name or human-readable UI name is invalid
/// (e.g., contains illegal characters or is empty).
/// </summary>
/// <remarks>Creates a new invalid-name settings exception.</remarks>
public sealed class InvalidNameSettingsException(string name, string message)
    : SettingsException(message)
{
    #region Properties

    /// <summary>The invalid name that caused the error.</summary>
    public string Name { get; } = name;

    #endregion

    #region Factory Methods

    /// <summary>Creates an exception for an invalid machine-readable name.</summary>
    internal static InvalidNameSettingsException ForName(string name) =>
        new(name, $"Invalid name: {name}");

    /// <summary>Creates an exception for an invalid human-readable UI name.</summary>
    internal static InvalidNameSettingsException ForUiName(string name) =>
        new(name, $"Invalid UI name: {name}");

    #endregion
}