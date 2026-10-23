// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Starts loaded plugins in load order and stops them in reverse order, identically for the CLI, the host, and the app: a
/// plugin that throws or takes too long is marked failed (start) or logged (stop), and nothing else stops. There is one
/// lifecycle so that the three applications cannot drift apart.
/// <para>
/// Isolation: every call into a plugin runs on a thread-pool thread and is awaited with a timeout, so a plugin that blocks its
/// thread, never completes, throws, or registers a throwing callback on a token it received fails only itself. The application
/// thread is never blocked by plugin code. A hanging plugin keeps its thread until the process ends.
/// </para>
/// <para>
/// Thread safety: not thread-safe; one call at a time. Each plugin runs sequentially: a start never overlaps another start.
/// </para>
/// </summary>
public static class PluginLifecycle
{
    #region Public API

    /// <summary>
    /// Starts every Loaded plugin with the context of its tier, each bounded by <paramref name="startTimeout"/> (the setting
    /// <c>plugins.startTimeoutSeconds</c>). A throw or a timeout logs one error through the plugin's context, calls
    /// <paramref name="disable"/> with the plugin name and the reason <c>plugin &lt;Name&gt; failed to start</c>, and turns the
    /// record into Failed; a failed plugin is not stopped later. A plugin that ignores its token and never completes, or that
    /// blocks the thread it was started on, does not block the others: the wait ends at the timeout (the plugin's task keeps
    /// running until the process ends). A context that cannot be made fails that plugin only.
    /// </summary>
    /// <param name="report">The load report; it is not changed.</param>
    /// <param name="disable">Refuses the contributions of a plugin (usually <see cref="PluginRegistry.Disable"/>).</param>
    /// <param name="contextFor">Creates or finds the context of a plugin's tier.</param>
    /// <param name="startTimeout">Longest start time per plugin; positive.</param>
    /// <param name="cancellationToken">Cancels the whole start (the application is shutting down).</param>
    /// <returns>A new report in which failed plugins have state Failed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<PluginLoadReport> StartAsync(PluginLoadReport report, Action<string, string> disable,
        Func<PluginRecord, IPluginContext> contextFor, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(contextFor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startTimeout, TimeSpan.Zero);

        PluginRecord[] records = [.. report.Plugins];
        for (int index = 0; index < records.Length; index++)
        {
            // A cancelled start (the application is shutting down) stops before the next plugin begins.
            cancellationToken.ThrowIfCancellationRequested();
            PluginRecord record = records[index];
            if (record.State != PluginLoadState.Loaded || record.Instance is null)
            {
                continue;
            }

            // The context touches the file system; a context that cannot be made fails this plugin and not the whole start.
            IPluginContext? context = null;
            string? reason = null;
            Exception? failure = null;
            try
            {
                context = contextFor(record);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reason = "context could not be created: " + PluginActivator.Describe(ex);
                failure = ex;
            }

            if (context is not null)
            {
                (reason, failure) = await _StartOneAsync(record.Name, record.Instance, context, startTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (reason is not null)
            {
                // The contributions are refused first, so a failing log cannot leave a failed plugin's source or format usable.
                disable(record.Name, Invariant($"plugin {record.Name} failed to start"));

                // One error entry through the plugin's own log; the stack trace is not part of the text.
                _TryLog(context, reason, failure);
                records[index] = record with { State = PluginLoadState.Failed, Reason = reason, Instance = null };
            }
        }

        return report with { Plugins = records };
    }

    /// <summary>
    /// Shutdown: signals <see cref="PluginContext.SignalStopping"/> (for contexts of that type) of every Loaded plugin, then
    /// stops them in reverse load order, each bounded by <paramref name="stopTimeout"/> (the setting
    /// <c>plugins.stopTimeoutSeconds</c>). A throw or a timeout logs one error entry; the shutdown always continues with the
    /// next plugin. Plugins that failed to start or were never loaded are not stopped. A context that cannot be found costs the
    /// stop signal and the log line of that plugin, not its stop.
    /// </summary>
    /// <param name="report">The report returned by <see cref="StartAsync"/>.</param>
    /// <param name="contextFor">Creates or finds the context of a plugin's tier.</param>
    /// <param name="stopTimeout">Longest stop time per plugin; positive.</param>
    /// <returns>A task that completes when every plugin was stopped or timed out.</returns>
    public static async ValueTask StopAsync(PluginLoadReport report, Func<PluginRecord, IPluginContext> contextFor, TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(contextFor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);

        // 1. Every plugin hears "stopping" before the first StopAsync runs, so cooperative loops can end early.
        List<(PluginRecord Record, IPlugin Instance)> running = [];
        foreach (PluginRecord record in report.Plugins)
        {
            if (record.State == PluginLoadState.Loaded && record.Instance is not null)
            {
                running.Add((record, record.Instance));
                if (_TryContext(contextFor, record) is PluginContext context)
                {
                    context.SignalStopping();
                }
            }
        }

        // 2. Reverse load order: a plugin may depend on the plugins that were loaded before it.
        for (int index = running.Count - 1; index >= 0; index--)
        {
            (PluginRecord record, IPlugin instance) = running[index];
            await _StopOneAsync(record.Name, instance, _TryContext(contextFor, record), stopTimeout).ConfigureAwait(false);
        }
    }

    #endregion

    #region Private helpers

    // Starts one plugin. Returns the reason and the exception when it failed, or (null, null) when it started.
    private static async ValueTask<(string? Reason, Exception? Failure)> _StartOneAsync(string name, IPlugin instance, IPluginContext context,
        TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        // The timeout token is cancelled by hand (see _Cancel) and not by CancelAfter: a timer would run the callbacks that the
        // plugin registered on its token on a timer thread, where a throwing callback ends the process.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Task.Run is deliberate: the call into the plugin starts on a thread-pool thread, so a plugin that blocks its thread
            // before it returns a task (sync-over-async, a lock, Thread.Sleep) cannot block the application. The token reaches
            // cooperative plugins; WaitAsync ends the wait at the timeout even when the plugin ignores it.
            await Task.Run(() => instance.StartAsync(context, timeout.Token).AsTask(), CancellationToken.None)
                .WaitAsync(startTimeout, cancellationToken).ConfigureAwait(false);
            return (null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _Cancel(timeout, context);
            return (LimitMessages.StartTimeout(name, startTimeout), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: whatever it throws marks that plugin as failed and nothing else.
            return ("StartAsync failed: " + PluginActivator.Describe(ex), ex);
        }
    }

    // Stops one plugin; every problem is a log entry, never an exception.
    private static async ValueTask _StopOneAsync(string name, IPlugin instance, IPluginContext? context, TimeSpan stopTimeout)
    {
        using CancellationTokenSource timeout = new();
        try
        {
            await Task.Run(() => instance.StopAsync(timeout.Token).AsTask(), CancellationToken.None)
                .WaitAsync(stopTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _Cancel(timeout, context);
            _TryLog(context, LimitMessages.StopTimeout(name, stopTimeout), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: the shutdown continues whatever one plugin throws.
            _TryLog(context, "StopAsync failed: " + PluginActivator.Describe(ex), ex);
        }
    }

    // Cancels the token handed to a plugin. Callbacks that the plugin registered run inside Cancel and a throwing one surfaces as
    // an AggregateException; it becomes log entries of that plugin and never an exception of the lifecycle.
    private static void _Cancel(CancellationTokenSource source, IPluginContext? context)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException ex)
        {
            foreach (Exception inner in ex.InnerExceptions)
            {
                _TryLog(context, "A cancellation callback failed: " + PluginActivator.Describe(inner), inner);
            }
        }
    }

    // The context of a record, or null when the application cannot give one; the caller then goes on without it.
    private static IPluginContext? _TryContext(Func<PluginRecord, IPluginContext> contextFor, PluginRecord record)
    {
        try
        {
            return contextFor(record);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without a context the plugin is still stopped; only its stop signal and its log lines are lost.
            return null;
        }
    }

    // Writes one error entry; a log that fails must not stop the lifecycle of the other plugins.
    private static void _TryLog(IPluginContext? context, string text, Exception? exception)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            context.Log.Error(text, exception);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The entry is lost; the reason is also part of the plugin's record.
        }
    }

    #endregion
}
