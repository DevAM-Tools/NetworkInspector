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

    // The writable managers are kept only to subscribe and unsubscribe; plugins get the read-only views (see the properties).
    private readonly SettingsManager _ApplicationManager;

    private readonly SettingsManager _ProfileManager;

    private readonly string _DataDirectory;

    // Set when the data folder could not be created; the read of DataDirectory reports it. Assigned only in the constructor.
    private readonly string? _DataDirectoryProblem;

    // Backs Stopping. Cancelled by hand in SignalStopping, inside a try, and never by a timer: callbacks that the plugin
    // registered on the token run inside Cancel, and on a timer thread a throwing callback would end the process.
    private readonly CancellationTokenSource _StoppingSource = new();

    // Copy on write: the array is never changed after it is published. Subscribe and unsubscribe swap it with a CAS loop; a
    // raise reads it once, so it iterates a stable snapshot. An array (not a multicast delegate) so that the raise can run
    // every handler in its own try; invoking a delegate list directly would stop at the first handler that throws.
    private EventHandler<SettingsAppliedEventArgs>[] _Handlers = [];

    // 0 = live, 1 = disposed. Interlocked/Volatile only; makes Dispose idempotent and SignalStopping a no-op afterwards.
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
    /// <exception cref="ArgumentException">
    /// The name or the data directory is <see langword="null"/>, empty, or white space, or the data directory is not fully
    /// qualified.
    /// </exception>
    /// <exception cref="ArgumentNullException">A log or manager is <see langword="null"/>.</exception>
    public PluginContext(string pluginName, IPluginLog log, string dataDirectory, SettingsManager applicationSettings,
        SettingsManager profileSettings)
    {
        // Arguments first, state afterwards: nothing is subscribed or created before every argument is known to be valid.
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(applicationSettings);
        ArgumentNullException.ThrowIfNull(profileSettings);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The data directory must be a fully qualified path.", nameof(dataDirectory));
        }

        // Plugins receive read-only views, so they can read settings but cannot write them through the context. Only the token
        // leaves this class; the source that can cancel it stays private.
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

    /// <summary>
    /// Unsubscribes from both managers and releases the stop signal; idempotent and thread-safe. It does not cancel
    /// <see cref="Stopping"/>: call <see cref="SignalStopping"/> first when the plugin has to hear the shutdown.
    /// </summary>
    public void Dispose()
    {
        // Exactly-once: a second Dispose must neither unsubscribe twice nor touch the disposed source.
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

    // Subscribing and unsubscribing are rare (typically once per plugin), raising happens for every applied settings change:
    // so the write side copies the whole array and the raise side only reads it, without any lock.
    /// <inheritdoc/>
    public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied
    {
        add
        {
            if (value is null)
            {
                return;
            }

            // CAS loop: when another thread swapped the array between the read and the exchange, rebuild from the new array,
            // so no concurrent subscription is lost.
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

            // Same CAS loop; removes the first occurrence of the handler and ignores a handler that is not subscribed.
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
    /// becomes one error entry in the plugin's log and never an exception for the caller; when the log fails too, the entry is
    /// lost and the call still returns normally.
    /// </summary>
    public void SignalStopping()
    {
        // Cheap early exit; the check races with Dispose, which the catch below absorbs.
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
            // together. One failing callback must not stop the shutdown of the other plugins. Each failure gets its own log
            // entry in the log of this plugin, so the author can see which callback failed.
            foreach (Exception inner in ex.InnerExceptions)
            {
                _TryLogError("A Stopping callback failed: " + PluginActivator.Describe(inner), inner);
            }
        }
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Forwards the event of either manager. A handler that throws is reported once and does not stop the others or the caller.
    /// It runs on the thread that applied the settings, which belongs to the application and must not be broken by plugin code.
    /// </summary>
    /// <param name="sender">The settings manager that raised the event; not passed on.</param>
    /// <param name="args">The event arguments, passed on unchanged.</param>
    private void _OnSettingsApplied(object? sender, SettingsAppliedEventArgs args)
    {
        // One read of the array: handlers added or removed while this loop runs take effect from the next raise on.
        foreach (EventHandler<SettingsAppliedEventArgs> handler in Volatile.Read(ref _Handlers))
        {
            try
            {
                // The sender is this context, not the manager that raised the event: the manager is writable and plugins only
                // get the read-only views of it.
                handler(this, args);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Plugin code: report it in the plugin's own log and keep going.
                _TryLogError("SettingsApplied handler failed: " + PluginActivator.Describe(ex), ex);
            }
        }
    }

    /// <summary>
    /// Writes one error entry to the plugin's log. The log is supplied by the application and can fail (a full disk, a closed
    /// file); the caller is the shutdown loop or the thread that applies settings, and a failing log must not end either of them,
    /// so the entry is lost instead. The failure of the log itself is not reported again: it would go to the same log.
    /// </summary>
    /// <param name="text">The entry text.</param>
    /// <param name="exception">The exception behind the entry.</param>
    private void _TryLogError(string text, Exception exception)
    {
        try
        {
            Log.Error(text, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The entry is lost; there is no other place to write it to.
        }
    }

    #endregion
}
