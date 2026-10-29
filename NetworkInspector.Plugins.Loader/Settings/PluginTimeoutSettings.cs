// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The application settings <c>plugins.startTimeoutSeconds</c> and <c>plugins.stopTimeoutSeconds</c>: how long a plugin may
/// take to start and to stop. The upper bound of both is the range of <see cref="CancellationTokenSource.CancelAfter(int)"/>
/// in seconds, 2,147,483 s, kept from an earlier implementation that cancelled with that method (the lifecycle now cancels by
/// hand and waits with <c>WaitAsync</c>, so the bound is a kept constant and not a limit of the current implementation);
/// within it the user decides. Every application calls the same helper. Stateless; <c>Read*</c> is thread-safe,
/// <see cref="Register"/> checks and then registers, so call it from one thread per settings manager.
/// </summary>
/// <remarks>
/// Limits (both settings): range 1 to 2,147,483 s, configurable by the end user in the application settings. Threat: a plugin
/// that hangs would block the start or the exit of the application forever. Posture: open by default (long defaults, shorter
/// values are opt-in), because a slow plugin on a slow machine must not be cut by a guess. Residual risk: with the defaults a
/// hanging plugin delays the start by up to 120 s and the exit by up to 60 s per plugin.
/// </remarks>
public static class PluginTimeoutSettings
{
    #region Constants

    /// <summary>Name of the start timeout setting (I64, default 120, 1 – 2,147,483).</summary>
    public const string StartName = "plugins.startTimeoutSeconds";

    /// <summary>Name of the stop timeout setting (I64, default 60, 1 – 2,147,483).</summary>
    public const string StopName = "plugins.stopTimeoutSeconds";

    // Relaxed defaults (owner decision): long enough for a slow plugin on a slow machine, short enough that a hanging plugin
    // cannot block the start or the exit of the application forever. The user tightens or widens them on the Settings page.
    private const long _DefaultStartSeconds = 120;

    private const long _DefaultStopSeconds = 60;

    // Upper bound kept from the earlier CancelAfter range (milliseconds as int): 2,147,483 s, about 24.8 days.
    private const long _MaxSeconds = 2_147_483;

    #endregion

    #region Public API

    /// <summary>Registers the group <c>plugins</c> and both settings on the application settings manager; repeated calls do nothing.</summary>
    /// <param name="applicationSettings">The application settings manager.</param>
    /// <exception cref="ArgumentNullException"><paramref name="applicationSettings"/> is <see langword="null"/>.</exception>
    public static void Register(SettingsManager applicationSettings)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        PluginSettingsGroup.EnsureRegistered(applicationSettings);

        // Each setting is checked on its own, so a manager that has one of them already gets the other. The range (min 1, max
        // the constant below) is part of the setting, so every way of changing it is validated the same way.
        if (applicationSettings.GetSetting(StartName) is null)
        {
            applicationSettings.RegisterSetting(Setting.I64(StartName, "Start timeout (s)", PluginPathsSetting.GroupName, _DefaultStartSeconds, min: 1,
                max: _MaxSeconds, description: "Longest time a plugin may take to start; a plugin that takes longer is marked failed."));
        }

        if (applicationSettings.GetSetting(StopName) is null)
        {
            applicationSettings.RegisterSetting(Setting.I64(StopName, "Stop timeout (s)", PluginPathsSetting.GroupName, _DefaultStopSeconds, min: 1,
                max: _MaxSeconds, description: "Longest time a plugin may take to stop; shutdown continues after it."));
        }
    }

    /// <summary>The start timeout.</summary>
    /// <param name="applicationSettings">The application settings view.</param>
    /// <returns>The configured time, 120 seconds when the setting is not registered.</returns>
    /// <remarks>The setting's minimum is 1 second; <see cref="PluginLifecycle"/> requires a positive timeout.</remarks>
    public static TimeSpan ReadStart(ReadOnlySettingsManagerView applicationSettings) =>
        TimeSpan.FromSeconds(applicationSettings.GetI64Setting(StartName) ?? _DefaultStartSeconds);

    /// <summary>The stop timeout.</summary>
    /// <param name="applicationSettings">The application settings view.</param>
    /// <returns>The configured time, 60 seconds when the setting is not registered.</returns>
    /// <remarks>The setting's minimum is 1 second; <see cref="PluginLifecycle"/> requires a positive timeout.</remarks>
    public static TimeSpan ReadStop(ReadOnlySettingsManagerView applicationSettings) =>
        TimeSpan.FromSeconds(applicationSettings.GetI64Setting(StopName) ?? _DefaultStopSeconds);

    #endregion
}
