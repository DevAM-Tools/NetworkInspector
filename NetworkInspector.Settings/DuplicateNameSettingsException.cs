// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Thrown when a settings group or individual setting with the same name
/// has already been registered.
/// </summary>
/// <param name="name">The duplicate group or setting name.</param>
/// <param name="message">Message text; built by the factory methods so group and setting duplicates read differently.</param>
public sealed class DuplicateNameSettingsException(string name, string message)
    : SettingsException(message)
{
    #region Duplicate name

    /// <summary>The duplicate name that caused the error.</summary>
    public string Name { get; } = name;

    #endregion

    #region Creating the exception

    /// <summary>Creates an exception for a duplicate group name.</summary>
    /// <param name="name">The group name that already exists.</param>
    internal static DuplicateNameSettingsException ForGroup(string name) =>
        new(name, Invariant($"Group '{name}' already exists"));

    /// <summary>Creates an exception for a duplicate setting name.</summary>
    /// <param name="name">The setting name that already exists.</param>
    internal static DuplicateNameSettingsException ForSetting(string name) =>
        new(name, Invariant($"Setting '{name}' already exists"));

    #endregion
}
