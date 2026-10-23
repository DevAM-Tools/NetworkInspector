// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The option managers of source and exporter requests. Options are Core settings, so every application — the CLI, MCP, and
/// the UI — uses the same names, types, bounds, and messages. Stateless; thread-safe.
/// </summary>
public static class RegistrationOptions
{
    #region Public API

    /// <summary>
    /// Creates a manager without storage path that holds the options of a registration at their defaults. The caller owns the
    /// manager and disposes it. It throws what <paramref name="registerOptions"/> throws — a plugin bug that the registry's
    /// probe already caught at load time.
    /// </summary>
    /// <param name="registerOptions">The option action of a source or exporter registration.</param>
    /// <returns>A new settings manager.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registerOptions"/> is <see langword="null"/>.</exception>
    public static SettingsManager CreateDefaults(Action<SettingsRegistrar> registerOptions)
    {
        ArgumentNullException.ThrowIfNull(registerOptions);
        SettingsManager options = new();
        try
        {
            registerOptions(new SettingsRegistrar(options));
            return options;
        }
        catch
        {
            // The manager must not leak when the plugin's action fails; the original exception continues unchanged.
            options.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies option texts with <see cref="SettingValueText"/>, the one text form of the command line, MCP, and the UI: all
    /// texts are parsed and validated first, then set and applied, so a refusal leaves <paramref name="options"/> unchanged.
    /// Refusals are <see cref="PluginErrorCode.InvalidOption"/> with <see cref="PluginError.Option"/> set to the key:
    /// an unknown key (<c>Unknown option 'cout'; did you mean: count?</c> or <c>Unknown option 'x'; options: a, b.</c>), a key
    /// given twice (<c>Option 'count' is given twice.</c>), and every <see cref="SettingProblem"/> (<c>port: allowed 1 – 65535</c>).
    /// </summary>
    /// <param name="options">The manager of a registration's options (see <see cref="CreateDefaults"/>).</param>
    /// <param name="texts">Option name and text pairs, in the order given.</param>
    /// <param name="error">Why the texts were refused; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when every text was applied.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryApplyTexts(SettingsManager options, IReadOnlyList<KeyValuePair<string, string>> texts,
        [NotNullWhen(false)] out PluginError? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(texts);

        // Phase one: resolve and parse everything. Nothing is stored until every text is acceptable.
        List<(Setting Setting, SettingValue Value)> parsed = new(texts.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in texts)
        {
            if (!options.TryGetSetting(pair.Key, out Setting? setting))
            {
                error = new PluginError(PluginErrorCode.InvalidOption, _UnknownOptionMessage(options, pair.Key), pair.Key);
                return false;
            }

            if (!seen.Add(pair.Key))
            {
                error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"Option '{pair.Key}' is given twice."), pair.Key);
                return false;
            }

            if (!SettingValueText.TryParse(setting, pair.Value, out SettingValue value, out SettingProblem? problem))
            {
                error = new PluginError(PluginErrorCode.InvalidOption, problem.ToString(), pair.Key);
                return false;
            }

            parsed.Add((setting, value));
        }

        // Phase two: store and apply. The values were validated against the same settings, so a refusal here would be a bug
        // in the settings; it is still reported instead of leaving a half-applied manager unannounced.
        foreach ((Setting setting, SettingValue value) in parsed)
        {
            if (!setting.TrySetPendingValue(value, out SettingProblem? problem))
            {
                options.ResetChanges();
                error = new PluginError(PluginErrorCode.InvalidOption, problem.ToString(), setting.Name);
                return false;
            }
        }

        options.ApplyChanges();
        error = null;
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>Builds the message for an option name that is not registered: the closest name, or the list of all names.</summary>
    private static string _UnknownOptionMessage(SettingsManager options, string key)
    {
        IReadOnlyList<string> similar = options.FindSimilar(key, 1);
        if (similar.Count > 0)
        {
            return Invariant($"Unknown option '{key}'; did you mean: {similar[0]}?");
        }

        string names = string.Join(", ", options.AllSettings.Select(static setting => setting.Name));
        return names.Length == 0
            ? Invariant($"Unknown option '{key}'; this registration has no options.")
            : Invariant($"Unknown option '{key}'; options: {names}.");
    }

    #endregion
}
