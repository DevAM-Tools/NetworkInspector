// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Runs a <c>RegisterOptions</c> action on a fresh manager and sets typed values, the way a request does before it calls
/// <c>Create</c>. Test helper; the real text path with the Core codec is added together with the codec.
/// </summary>
internal static class OptionManager
{
    #region Fields

    // Shared by all requests: the session settings of a test request are always empty, and a manager is thread-safe.
    private static readonly SettingsManager _EmptySessionSettings = new();

    #endregion

    #region Public API

    /// <summary>Creates a manager holding the options of a registration at their defaults.</summary>
    /// <param name="registerOptions">The registration's option action.</param>
    /// <returns>The manager; the caller disposes it.</returns>
    internal static SettingsManager Create(Action<SettingsRegistrar> registerOptions)
    {
        SettingsManager manager = new();
        try
        {
            registerOptions(new SettingsRegistrar(manager));
            return manager;
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    /// <summary>Sets a typed value as pending and applies it, so that readers see it.</summary>
    /// <param name="manager">The option manager.</param>
    /// <param name="name">Setting name.</param>
    /// <param name="value">The value to set.</param>
    /// <exception cref="ValidationSettingsException">The value is outside the bounds.</exception>
    /// <exception cref="TypeMismatchSettingsException">The value has the wrong type.</exception>
    internal static void Set(SettingsManager manager, string name, SettingValue value)
    {
        Setting setting = manager.GetSetting(name) ?? throw new InvalidOperationException("Unknown option " + name);
        setting.SetPendingValue(value);
        manager.ApplyChanges();
    }

    /// <summary>Creates a source request over the manager's current values with the given paths.</summary>
    /// <param name="manager">The option manager.</param>
    /// <param name="paths">The file paths of the request.</param>
    /// <returns>The request; session settings are an empty view.</returns>
    internal static SourceRequest SourceRequest(SettingsManager manager, params string[] paths) =>
        new(paths, manager.ReadOnly, _EmptySessionSettings.ReadOnly);

    #endregion
}
