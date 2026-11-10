// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Thrown when a setting name or a group name is invalid (empty where a name is needed, or containing characters outside the
/// dot-separated identifier grammar). An invalid user-interface name is reported by <see cref="InvalidUiNameSettingsException"/>.
/// </summary>
/// <param name="name">The rejected name.</param>
/// <param name="message">Message text; built by the factory method.</param>
/// <seealso cref="InvalidUiNameSettingsException"/>
public sealed class InvalidNameSettingsException(string name, string message)
    : SettingsException(message)
{
    #region Rejected name

    /// <summary>The invalid name that caused the error.</summary>
    public string Name { get; } = name;

    #endregion

    #region Creating the exception

    /// <summary>Creates an exception for an invalid machine-readable name.</summary>
    /// <param name="name">The rejected name.</param>
    internal static InvalidNameSettingsException ForName(string name) =>
        new(name, Invariant($"Invalid name: {name}"));

    #endregion
}
