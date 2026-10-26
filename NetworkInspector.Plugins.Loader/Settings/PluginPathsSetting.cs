// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The application setting <c>plugins.paths</c>: the folders and files plugins are loaded from. Entries are full paths;
/// applications that accept relative input (the CLI) resolve it against the current directory before storing. Stateless;
/// <see cref="Read"/> is thread-safe. <see cref="Register"/> checks and then registers, which is not atomic: call it from one
/// thread per settings manager (the loading thread).
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
    /// <exception cref="ArgumentNullException"><paramref name="applicationSettings"/> is <see langword="null"/>.</exception>
    public static void Register(SettingsManager applicationSettings)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        PluginSettingsGroup.EnsureRegistered(applicationSettings);

        // Check then register, so the CLI, the host, and the app (and the registry's probe) can all call this. Not atomic: call
        // it from one thread per manager (the loading thread). Activation Restart: plugins are loaded once at start and cannot
        // be unloaded, so a changed path list takes effect at the next start. The default is empty (no plugin folder) so that
        // nothing foreign is loaded until the user adds a path (secure by default; the setting is the opt-in).
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
    /// <exception cref="ArgumentNullException"><paramref name="warn"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Read(ReadOnlySettingsManagerView applicationSettings, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        // The view is a struct (nothing to null-check); a setting that is not registered reads as null and means "no paths".
        string[] configured = applicationSettings.GetStringArraySetting(Name) ?? [];
        List<string> paths = new(configured.Length);
        foreach (string entry in configured)
        {
            // Blank entries are dropped silently (for example an empty line in a list), anything else that is unusable is
            // reported once.
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A relative path would resolve against whatever the working directory is at the moment, so the same setting
            // would mean a different folder for the CLI, the host, and the app. Only full paths are accepted.
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
