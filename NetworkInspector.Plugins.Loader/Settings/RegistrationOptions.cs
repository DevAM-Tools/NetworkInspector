// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The option managers of source and exporter requests. Options are Core settings, so every application uses the same names,
/// types, bounds, and messages. Stateless; thread-safe.
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
    /// <exception cref="Exception">Whatever <paramref name="registerOptions"/> throws, unchanged.</exception>
    public static SettingsManager CreateDefaults(Action<SettingsRegistrar> registerOptions)
    {
        ArgumentNullException.ThrowIfNull(registerOptions);

        // A fresh manager per call without a storage path: options belong to one request, are never persisted, and must not
        // share Setting instances (a setting binds to the first manager that registers it, so the action creates new ones).
        SettingsManager options = new();
        try
        {
            registerOptions(new SettingsRegistrar(options));
            return options;
        }
        catch
        {
            // The manager must not leak when the plugin's action fails; the original exception continues unchanged. Not
            // contained here on purpose: at load time the registry's probe lets it reach the loader's failure boundary, which
            // fails the plugin, and any other caller decides how to report it.
            options.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies option texts with <see cref="SettingValueText"/>, the one text form of every application: all
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
        // in the settings; it is still reported instead of leaving a half-applied manager unannounced. All-or-nothing is the
        // point of the two phases: a request with one bad text must not leave the options of the others applied, because the
        // caller would then open a source or write an export with a mix of the old and the new options.
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
    /// <param name="options">The manager of the registration's options.</param>
    /// <param name="key">The option name that was given.</param>
    private static string _UnknownOptionMessage(SettingsManager options, string key)
    {
        // One suggestion is enough for a typo; the full list only when nothing is close, so the message stays one line. The
        // key is user input and appears in the text verbatim, in the same form for the command line, MCP, and the UI.
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
