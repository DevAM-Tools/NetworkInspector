// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a settings group or individual setting with the same name
/// has already been registered.
/// </summary>
/// <remarks>Creates a new duplicate-name settings exception.</remarks>
public sealed class DuplicateNameSettingsException(string name, string message)
    : SettingsException(message)
{
    #region Properties

    /// <summary>The duplicate name that caused the error.</summary>
    public string Name { get; } = name;

    #endregion

    #region Factory Methods

    /// <summary>Creates an exception for a duplicate group name.</summary>
    internal static DuplicateNameSettingsException ForGroup(string name) =>
        new(name, $"Group '{name}' already exists");

    /// <summary>Creates an exception for a duplicate setting name.</summary>
    internal static DuplicateNameSettingsException ForSetting(string name) =>
        new(name, $"Setting '{name}' already exists");

    #endregion
}
