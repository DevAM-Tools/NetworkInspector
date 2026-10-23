// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// What a started plugin gets from the application. The host passes an <c>IHostContext</c>, the app an <c>IAppContext</c>
/// (both derive from this interface). Thread safety: members are thread-safe; events are raised on one application thread
/// (CLI: the command thread; host: the host queue thread) and a throwing handler becomes one error log entry instead of an
/// exception for the raising caller.
/// </summary>
public interface IPluginContext
{
    /// <summary>Name of the plugin this context belongs to.</summary>
    string PluginName { get; }

    /// <summary>Log of the plugin (CLI: error output and log file; host: host messages).</summary>
    IPluginLog Log { get; }

    /// <summary>
    /// Existing folder <c>&lt;settings base&gt;/plugins-data/&lt;storage name&gt;/</c> for the plugin's own files, when it could be
    /// created. The same folder is used by the CLI, the host, and the app.
    /// </summary>
    /// <exception cref="System.IO.IOException">The folder could not be created; reading the property reports why. The application keeps
    /// running and only the plugin that needs the folder fails (usually its <see cref="IPlugin.StartAsync"/>).</exception>
    string DataDirectory { get; }

    /// <summary>Application settings, including the plugin's own application-scope settings.</summary>
    ReadOnlySettingsManagerView ApplicationSettings { get; }

    /// <summary>Settings of the active profile (CLI: the <c>--profile</c> profile; host: the host profile manager).</summary>
    ReadOnlySettingsManagerView ProfileSettings { get; }

    /// <summary>Raised after application- or profile-scope settings were applied (Core event arguments).</summary>
    event EventHandler<SettingsAppliedEventArgs>? SettingsApplied;

    /// <summary>Cancelled when the shutdown starts, before <see cref="IPlugin.StopAsync"/> is called.</summary>
    CancellationToken Stopping { get; }
}
