// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The texts of the two plugin timeouts. Every limit that a user can hit says what happened, the value, the setting, and how
/// to change it, in one fixed form: <c>&lt;event&gt;. Limit: &lt;value&gt; (setting &lt;key&gt;). Change it in Settings ▸
/// &lt;group&gt; ▸ &lt;name&gt; or with ni settings set &lt;key&gt; &lt;value&gt;.</c> The CLI, the host, and the app use the same lifecycle
/// and therefore the same texts. Stateless; thread-safe.
/// </summary>
internal static class LimitMessages
{
    #region Internal API

    /// <summary>The text for a plugin whose start did not finish in time.</summary>
    /// <param name="pluginName">Name of the plugin.</param>
    /// <param name="timeout">The limit that was exceeded.</param>
    /// <returns>The one-line English text.</returns>
    internal static string StartTimeout(string pluginName, TimeSpan timeout) =>
        _Message(pluginName, "start", timeout, PluginTimeoutSettings.StartName, "Start timeout (s)");

    /// <summary>The text for a plugin whose stop did not finish in time.</summary>
    /// <param name="pluginName">Name of the plugin.</param>
    /// <param name="timeout">The limit that was exceeded.</param>
    /// <returns>The one-line English text.</returns>
    internal static string StopTimeout(string pluginName, TimeSpan timeout) =>
        _Message(pluginName, "stop", timeout, PluginTimeoutSettings.StopName, "Stop timeout (s)");

    #endregion

    #region Private helpers

    private static string _Message(string pluginName, string verb, TimeSpan timeout, string key, string settingName)
    {
        // Seconds print invariant with up to three decimals ("30", "0.2"), the same way in every language and on every machine.
        string seconds = timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return Invariant(
            $"Plugin '{pluginName}' did not {verb} within {seconds} s. Limit: {seconds} (setting {key}). Change it in Settings ▸ Plugins ▸ {settingName} or with ni settings set {key} <value>.");
    }

    #endregion
}
