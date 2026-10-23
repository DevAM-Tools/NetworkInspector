// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Runs a source or exporter registration the way an application does: registers its options on a fresh manager with the
/// loader's <c>RegistrationOptions.CreateDefaults</c>, sets typed values, applies them, and calls <c>Create</c>. Test helper;
/// thread-safe because it keeps no state.
/// </summary>
internal static class RegistrationRunner
{
    #region Public API

    /// <summary>Opens a source.</summary>
    /// <param name="registration">The source registration.</param>
    /// <param name="paths">The paths of the request.</param>
    /// <param name="options">Option values to set; options not listed keep their default.</param>
    /// <returns>The source on success, otherwise the error; exactly one of the two is set.</returns>
    internal static (IFrameSource? Source, PluginError? Error) Open(SourceRegistration registration, string[] paths,
        params (string Name, SettingValue Value)[] options)
    {
        using SettingsManager manager = _Apply(registration.RegisterOptions, options);
        using SettingsManager session = new();
        return registration.Create(new SourceRequest(paths, manager.ReadOnly, session.ReadOnly), out IFrameSource? source,
            out PluginError? error)
            ? (source, null)
            : (null, error);
    }

    /// <summary>Creates an exporter.</summary>
    /// <typeparam name="TListener">The listener type of the registration.</typeparam>
    /// <param name="registration">The exporter registration.</param>
    /// <param name="target">Target path (a directory for Parquet).</param>
    /// <param name="stack">The stack that parsed the packets.</param>
    /// <param name="options">Option values to set; options not listed keep their default.</param>
    /// <returns>The listener on success, otherwise the error; exactly one of the two is set.</returns>
    internal static (TListener? Listener, PluginError? Error) Create<TListener>(ExporterRegistration<TListener> registration,
        string target, IStack stack, params (string Name, SettingValue Value)[] options)
        where TListener : class
    {
        using SettingsManager manager = _Apply(registration.RegisterOptions, options);
        ExportRequest request = new(target, manager.ReadOnly, stack, 0, CancellationToken.None);
        return registration.Create(request, out TListener? listener, out PluginError? error)
            ? (listener, null)
            : (null, error);
    }

    #endregion

    #region Private Helpers

    /// <summary>Creates the manager at its defaults and applies the given values.</summary>
    private static SettingsManager _Apply(Action<SettingsRegistrar> registerOptions, (string Name, SettingValue Value)[] values)
    {
        SettingsManager manager = RegistrationOptions.CreateDefaults(registerOptions);
        try
        {
            foreach ((string name, SettingValue value) in values)
            {
                OptionManager.Set(manager, name, value);
            }

            return manager;
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    #endregion
}
