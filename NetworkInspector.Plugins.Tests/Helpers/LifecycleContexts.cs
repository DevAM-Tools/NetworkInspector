// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Creates one <see cref="PluginContext"/> with a capturing log per plugin record, the way an application does for the
/// lifecycle, over private settings managers and a private data folder. Not thread-safe; used by one test.
/// </summary>
internal sealed class LifecycleContexts : IDisposable
{
    #region Fields

    private readonly Dictionary<string, PluginContext> _Contexts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, CapturingPluginLog> _Logs = new(StringComparer.OrdinalIgnoreCase);

    private readonly SettingsManager _ApplicationSettings = new();

    private readonly SettingsManager _ProfileSettings = new();

    private readonly TempFolder _Folder = new("contexts");

    #endregion

    #region Lifecycle

    /// <summary>Disposes every context, both managers, and the folder.</summary>
    public void Dispose()
    {
        foreach (PluginContext context in _Contexts.Values)
        {
            context.Dispose();
        }

        _ApplicationSettings.Dispose();
        _ProfileSettings.Dispose();
        _Folder.Dispose();
    }

    #endregion

    #region Public API

    /// <summary>The application settings manager the contexts read.</summary>
    internal SettingsManager ApplicationSettings => _ApplicationSettings;

    /// <summary>The context of a record (created on first use); the delegate the lifecycle calls.</summary>
    /// <param name="record">The plugin record.</param>
    /// <returns>The context.</returns>
    internal IPluginContext For(PluginRecord record)
    {
        if (!_Contexts.TryGetValue(record.Name, out PluginContext? context))
        {
            CapturingPluginLog log = new();
            context = new PluginContext(record.Name, log, Path.Combine(_Folder.Path, record.Name), _ApplicationSettings, _ProfileSettings);
            _Contexts[record.Name] = context;
            _Logs[record.Name] = log;
        }

        return context;
    }

    /// <summary>The captured log entries of a plugin (empty when its context was never created).</summary>
    /// <param name="pluginName">Plugin name.</param>
    /// <returns>The entries in call order.</returns>
    internal IReadOnlyList<LogEntry> LogOf(string pluginName) =>
        _Logs.TryGetValue(pluginName, out CapturingPluginLog? log)
            ? log.Entries
            : [];

    #endregion
}
