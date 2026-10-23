// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The core-tier context of one plugin (used by the CLI). It forwards the <c>SettingsApplied</c> events of the application
/// and the profile settings manager; each handler runs in its own try block and a throwing handler becomes one error entry
/// <c>SettingsApplied handler failed: …</c> in the plugin's log, never an exception for the caller that applied the settings.
/// The host and the app implement their own contexts.
/// <para>
/// Thread safety: members are thread-safe. The event keeps an immutable handler array that is swapped with
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, so subscribing, unsubscribing, and raising never block each other;
/// a raise runs on the thread that applied the settings.
/// </para>
/// </summary>
public sealed class PluginContext : IPluginContext, IDisposable
{
    #region Fields

    private readonly SettingsManager _ApplicationManager;

    private readonly SettingsManager _ProfileManager;

    private readonly string _DataDirectory;

    // Set when the data folder could not be created; the read of DataDirectory reports it. Assigned only in the constructor.
    private readonly string? _DataDirectoryProblem;

    private readonly CancellationTokenSource _StoppingSource = new();

    private EventHandler<SettingsAppliedEventArgs>[] _Handlers = [];

    private int _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Creates the context, creates the data directory when possible, and subscribes to both settings managers. A data folder
    /// that cannot be created is logged once as a warning and reported by <see cref="DataDirectory"/> when a plugin reads it.
    /// </summary>
    /// <param name="pluginName">Name of the plugin.</param>
    /// <param name="log">Log of the plugin.</param>
    /// <param name="dataDirectory">Fully qualified path of the plugin's data folder; it is created when missing.</param>
    /// <param name="applicationSettings">The application settings manager.</param>
    /// <param name="profileSettings">The settings manager of the active profile.</param>
    /// <exception cref="ArgumentException">The name is empty or the data directory is not fully qualified.</exception>
    /// <exception cref="ArgumentNullException">A log or manager is <see langword="null"/>.</exception>
    public PluginContext(string pluginName, IPluginLog log, string dataDirectory, SettingsManager applicationSettings,
        SettingsManager profileSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(applicationSettings);
        ArgumentNullException.ThrowIfNull(profileSettings);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The data directory must be a fully qualified path.", nameof(dataDirectory));
        }

        PluginName = pluginName;
        Log = log;
        _DataDirectory = dataDirectory;
        _ApplicationManager = applicationSettings;
        _ProfileManager = profileSettings;
        ApplicationSettings = applicationSettings.ReadOnly;
        ProfileSettings = profileSettings.ReadOnly;
        Stopping = _StoppingSource.Token;

        // The folder is created when possible. A plugin that never reads DataDirectory (all built-ins) must not stop the
        // application because of it; a plugin that does read it gets the problem as an IOException from that call.
        try
        {
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _DataDirectoryProblem = exception.Message;
            log.Warning("The data folder '" + dataDirectory + "' cannot be created: " + exception.Message);
        }

        // Subscribe last: the constructor can no longer fail after this point, so no subscription is left behind.
        _ApplicationManager.SettingsApplied += _OnSettingsApplied;
        _ProfileManager.SettingsApplied += _OnSettingsApplied;
    }

    /// <summary>Unsubscribes from both managers and releases the stop signal; idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _ApplicationManager.SettingsApplied -= _OnSettingsApplied;
        _ProfileManager.SettingsApplied -= _OnSettingsApplied;
        _StoppingSource.Dispose();
    }

    #endregion

    #region IPluginContext

    /// <inheritdoc/>
    public string PluginName { get; }

    /// <inheritdoc/>
    public IPluginLog Log { get; }

    /// <inheritdoc/>
    /// <exception cref="IOException">The folder could not be created when the context was made.</exception>
    public string DataDirectory =>
        _DataDirectoryProblem is null
            ? _DataDirectory
            : throw new IOException("The data folder '" + _DataDirectory + "' does not exist: " + _DataDirectoryProblem);

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView ApplicationSettings { get; }

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView ProfileSettings { get; }

    /// <inheritdoc/>
    public CancellationToken Stopping { get; }

    /// <inheritdoc/>
    public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied
    {
        add
        {
            if (value is null)
            {
                return;
            }

            EventHandler<SettingsAppliedEventArgs>[] current;
            EventHandler<SettingsAppliedEventArgs>[] updated;
            do
            {
                current = Volatile.Read(ref _Handlers);
                updated = [.. current, value];
            }
            while (Interlocked.CompareExchange(ref _Handlers, updated, current) != current);
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            EventHandler<SettingsAppliedEventArgs>[] current;
            EventHandler<SettingsAppliedEventArgs>[] updated;
            do
            {
                current = Volatile.Read(ref _Handlers);
                int position = Array.IndexOf(current, value);
                if (position < 0)
                {
                    return;
                }

                updated = [.. current[..position], .. current[(position + 1)..]];
            }
            while (Interlocked.CompareExchange(ref _Handlers, updated, current) != current);
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Cancels <see cref="Stopping"/>; idempotent and a no-op after <see cref="Dispose"/>. A callback of the plugin that throws
    /// becomes one error entry in the plugin's log and never an exception for the caller.
    /// </summary>
    public void SignalStopping()
    {
        if (Volatile.Read(ref _Disposed) != 0)
        {
            return;
        }

        try
        {
            _StoppingSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the call: the plugin is gone, nobody waits for the signal.
        }
        catch (AggregateException ex)
        {
            // Plugin code: callbacks registered on Stopping run inside Cancel, which runs all of them and then throws the failures
            // together. One failing callback must not stop the shutdown of the other plugins.
            foreach (Exception inner in ex.InnerExceptions)
            {
                Log.Error("A Stopping callback failed: " + PluginActivator.Describe(inner), inner);
            }
        }
    }

    #endregion

    #region Private helpers

    // Forwards the event of either manager. A handler that throws is reported once and does not stop the others or the caller.
    private void _OnSettingsApplied(object? sender, SettingsAppliedEventArgs args)
    {
        foreach (EventHandler<SettingsAppliedEventArgs> handler in Volatile.Read(ref _Handlers))
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Plugin code: report it in the plugin's own log and keep going.
                Log.Error("SettingsApplied handler failed: " + PluginActivator.Describe(ex), ex);
            }
        }
    }

    #endregion
}
