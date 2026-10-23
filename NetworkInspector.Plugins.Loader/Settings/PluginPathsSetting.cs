// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The application setting <c>plugins.paths</c>: the folders and files plugins are loaded from. Entries are full paths;
/// applications that accept relative input (the CLI) resolve it against the current directory before storing. Stateless;
/// thread-safe.
/// </summary>
public static class PluginPathsSetting
{
    #region Constants

    /// <summary>Name of the setting.</summary>
    public const string Name = "plugins.paths";

    /// <summary>Name of the settings group that holds the plugin settings.</summary>
    public const string GroupName = "plugins";

    #endregion

    #region Public API

    /// <summary>
    /// Registers the group <c>plugins</c> and the setting (a string array, empty by default) on the application settings
    /// manager. A second call does nothing, so the CLI, the host, and the app can all call it.
    /// </summary>
    /// <param name="applicationSettings">The application settings manager.</param>
    public static void Register(SettingsManager applicationSettings)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        PluginSettingsGroup.EnsureRegistered(applicationSettings);
        if (applicationSettings.GetSetting(Name) is null)
        {
            applicationSettings.RegisterSetting(Setting.StringArray(Name, "Plugin paths", GroupName, [],
                "Plugin files and folders (full paths); folders are searched recursively.", SettingActivation.Restart));
        }
    }

    /// <summary>
    /// Reads the configured paths: the non-blank, trimmed entries that are fully qualified. A relative entry (possible only
    /// by editing the settings file by hand) is reported once through <paramref name="warn"/> and skipped, because a relative
    /// path would mean a different folder for every working directory.
    /// </summary>
    /// <param name="applicationSettings">The application settings view.</param>
    /// <param name="warn">Receives one line per skipped entry.</param>
    /// <returns>The usable paths in configured order.</returns>
    public static IReadOnlyList<string> Read(ReadOnlySettingsManagerView applicationSettings, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        string[] configured = applicationSettings.GetStringArraySetting(Name) ?? [];
        List<string> paths = new(configured.Length);
        foreach (string entry in configured)
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(trimmed))
            {
                warn(Invariant($"{Name}: '{trimmed}' is not a full path and is ignored"));
                continue;
            }

            paths.Add(trimmed);
        }

        return paths;
    }

    #endregion
}
