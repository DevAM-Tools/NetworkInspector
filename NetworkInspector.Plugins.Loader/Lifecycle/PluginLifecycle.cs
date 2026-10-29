// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Starts loaded plugins in load order and stops them in reverse order, identically for every application: a plugin that
/// throws or takes too long is marked failed (start) or logged (stop), and nothing else stops. There is one lifecycle so that
/// applications cannot drift apart.
/// <para>
/// Isolation: every call into a plugin runs on a thread-pool thread and is awaited with a timeout, so a plugin that blocks its
/// thread, never completes, throws, or registers a throwing callback on a token it received fails only itself. The application
/// thread is never blocked by plugin code. A hanging plugin keeps its thread until the process ends: .NET cannot abort a
/// running thread, so a timeout abandons the call instead of stopping it. Nobody waits for the abandoned call again; only if it
/// ends with an exception later, that exception is logged once through the plugin's log (and thereby observed).
/// </para>
/// <para>
/// Thread safety: not thread-safe; one call at a time. Plugins are started one after the other, so a start waits for the
/// previous one (or its timeout) and never overlaps it, except that the abandoned call of a timed-out plugin may still be
/// running. Stops are sequential in the same way.
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
    /// <param name="contextFor">
    /// Creates or finds the context of a plugin's tier. It must return the same context for the same plugin on every call:
    /// the <c>Stopping</c> token handed over at the start is the one that the stop signal cancels, and the stop looks the
    /// context up again.
    /// </param>
    /// <param name="startTimeout">Longest start time per plugin; positive.</param>
    /// <param name="cancellationToken">Cancels the whole start (the application is shutting down).</param>
    /// <returns>A new report in which failed plugins have state Failed.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startTimeout"/> is zero or negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<PluginLoadReport> StartAsync(PluginLoadReport report, Action<string, string> disable,
        Func<PluginRecord, IPluginContext> contextFor, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(contextFor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(startTimeout, TimeSpan.Zero);

        // Work on a copy: the result is a new report and the caller's report stays unchanged (records are replaced by index).
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

            // The start of one plugin is awaited before the next one begins: load order is start order, and a plugin may rely on
            // the plugins before it.
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
    /// <param name="contextFor">Creates or finds the context of a plugin's tier; the same context as at the start.</param>
    /// <param name="stopTimeout">Longest stop time per plugin; positive.</param>
    /// <returns>A task that completes when every plugin was stopped or timed out.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stopTimeout"/> is zero or negative.</exception>
    public static async ValueTask StopAsync(PluginLoadReport report, Func<PluginRecord, IPluginContext> contextFor, TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(contextFor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);

        // 1. Every plugin hears "stopping" before the first StopAsync runs, so cooperative loops can end early. Only contexts of
        //    the loader's own type are signaled here; a context of another type (host, app) is not touched by this method.
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

    /// <summary>Starts one plugin. Returns the reason and the exception when it failed, or (null, null) when it started.</summary>
    /// <param name="name">Name of the plugin, for the timeout text.</param>
    /// <param name="instance">The plugin object.</param>
    /// <param name="context">The context handed to <see cref="IPlugin.StartAsync"/>.</param>
    /// <param name="startTimeout">Longest start time.</param>
    /// <param name="cancellationToken">Cancels the whole start; the plugin sees it through its token.</param>
    /// <returns>The failure reason and exception, or (null, null) on success.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    private static async ValueTask<(string? Reason, Exception? Failure)> _StartOneAsync(string name, IPlugin instance, IPluginContext context,
        TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        // The timeout token is cancelled by hand (see _Cancel) and not by CancelAfter: a timer would run the callbacks that the
        // plugin registered on its token on a timer thread, where a throwing callback ends the process. It is linked to the
        // application's token, so a shutdown during the start also reaches the plugin.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run is deliberate: the call into the plugin starts on a thread-pool thread, so a plugin that blocks its thread
        // before it returns a task (sync-over-async, a lock, Thread.Sleep) cannot block the application. The token reaches
        // cooperative plugins; the bounded wait below ends at the timeout even when the plugin ignores it. Task.Run itself gets
        // CancellationToken.None: cancellation is observed by the wait and by the plugin through its token. After a timeout
        // (or a cancelled start) the plugin's task is abandoned: it keeps running, and only a later failure of it is looked at
        // once (see _Abandon).
        Task pluginTask = Task.Run(() => instance.StartAsync(context, timeout.Token).AsTask(), CancellationToken.None);
        try
        {
            if (await _TryAwaitWithinAsync(pluginTask, startTimeout, cancellationToken).ConfigureAwait(false))
            {
                return (null, null);
            }

            // The wait timed out, and the timeout is known from the wait itself and not from the exception type: a plugin may throw
            // a TimeoutException of its own, which is a failure of the plugin (the catch below) and not a timeout. Cancel the
            // token so that cooperative plugins can give up; callbacks they registered on it run inside Cancel and are
            // contained there.
            _Abandon(pluginTask, "StartAsync", context);
            _Cancel(timeout, context);
            return (LimitMessages.StartTimeout(name, startTimeout), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application cancelled the start (shutdown): not a plugin failure, the cancellation continues to the caller. An
            // OperationCanceledException that the plugin throws on its own does not match the filter and fails only the plugin.
            _Abandon(pluginTask, "StartAsync", context);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: whatever it throws marks that plugin as failed and nothing else.
            return ("StartAsync failed: " + PluginActivator.Describe(ex), ex);
        }
    }

    /// <summary>Stops one plugin; every problem is a log entry, never an exception.</summary>
    /// <param name="name">Name of the plugin, for the timeout text.</param>
    /// <param name="instance">The plugin object.</param>
    /// <param name="context">The context for the log entries; <see langword="null"/> when none could be found.</param>
    /// <param name="stopTimeout">Longest stop time.</param>
    private static async ValueTask _StopOneAsync(string name, IPlugin instance, IPluginContext? context, TimeSpan stopTimeout)
    {
        // The stop token is not linked to anything: it is cancelled only at the timeout (the shutdown signal itself is the
        // context's Stopping token). The wait has no cancellation token either: the shutdown must run to its end.
        using CancellationTokenSource timeout = new();

        // Same pool-thread start as at the start, for the same reason: a StopAsync that blocks before it returns a task must not
        // block the shutdown.
        Task pluginTask = Task.Run(() => instance.StopAsync(timeout.Token).AsTask(), CancellationToken.None);
        try
        {
            if (!await _TryAwaitWithinAsync(pluginTask, stopTimeout, CancellationToken.None).ConfigureAwait(false))
            {
                // A timeout is known from the wait and not from the exception type: a TimeoutException that the plugin throws
                // itself is a failure of the plugin (the catch below).
                _Abandon(pluginTask, "StopAsync", context);
                _Cancel(timeout, context);
                _TryLog(context, LimitMessages.StopTimeout(name, stopTimeout), null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: the shutdown continues whatever one plugin throws.
            _TryLog(context, "StopAsync failed: " + PluginActivator.Describe(ex), ex);
        }
    }

    /// <summary>
    /// Waits for a plugin call for at most <paramref name="timeout"/> and tells a timeout from a failure of the plugin by what
    /// the wait itself saw, not by the type of an exception: <c>Task.WaitAsync</c> would throw the same <c>TimeoutException</c>
    /// for its own timer and for a plugin that throws one, and the plugin would then be reported as too slow although it
    /// failed.
    /// </summary>
    /// <param name="pluginTask">The running plugin call.</param>
    /// <param name="timeout">Longest wait.</param>
    /// <param name="cancellationToken">Cancels the wait (the application is shutting down).</param>
    /// <returns>
    /// <see langword="true"/> when the call finished within the timeout, <see langword="false"/> when the timeout ended the wait.
    /// A failure of the plugin is not returned but thrown, as the exception of the plugin.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, or the plugin was cancelled.</exception>
    private static async ValueTask<bool> _TryAwaitWithinAsync(Task pluginTask, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The timer is linked to the caller's token, so a cancelled wait ends the timer as well. It is cancelled by hand when the
        // call finishes first, so that no timer outlives the wait.
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, timer.Token);
        Task finished = await Task.WhenAny(pluginTask, delay).ConfigureAwait(false);

        // A call that finishes in the same moment as the timer wins: it was in time, and its outcome is not thrown away.
        if (!ReferenceEquals(finished, pluginTask) && !pluginTask.IsCompleted)
        {
            // The timer ends the wait either because the application cancelled it (the cancellation continues to the caller) or
            // because the time is up.
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        await timer.CancelAsync().ConfigureAwait(false);

        // Awaiting the finished call rethrows what the plugin threw, exactly as it was thrown (a TimeoutException of the plugin
        // stays a failure of the plugin).
        await pluginTask.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Looks once at the outcome of a call that was given up (timeout or cancelled start). Nobody awaits such a task again, so a
    /// failure that it ends with later would stay unobserved; this logs it once through the plugin's log instead, which is the
    /// only place left to say that a plugin which was reported as timed out has failed meanwhile. A call that ends normally or
    /// by cancellation (a cooperative plugin that gave up at the cancelled token) is not reported.
    /// </summary>
    /// <param name="pluginTask">The abandoned plugin call.</param>
    /// <param name="label">The call, for the text (<c>StartAsync</c> or <c>StopAsync</c>).</param>
    /// <param name="context">The context for the log entry; <see langword="null"/> when none could be found.</param>
    private static void _Abandon(Task pluginTask, string label, IPluginContext? context)
    {
        // Pool thread, not the thread that completes the plugin's task: the log is application code and must not run inside the
        // plugin's own completion. The continuation task itself never fails (Describe and _TryLog contain everything).
        _ = pluginTask.ContinueWith(
            task =>
            {
                // Reading Exception is what marks the failure as observed.
                Exception? failure = task.Exception?.GetBaseException();
                if (failure is not null)
                {
                    _TryLog(context, label + " failed after its call was abandoned: " + PluginActivator.Describe(failure), failure);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels the token handed to a plugin. Callbacks that the plugin registered run inside Cancel and a throwing one surfaces
    /// as an AggregateException; it becomes log entries of that plugin and never an exception of the lifecycle.
    /// </summary>
    /// <param name="source">The source that backs the plugin's token.</param>
    /// <param name="context">The context for the log entries; <see langword="null"/> when none could be found.</param>
    private static void _Cancel(CancellationTokenSource source, IPluginContext? context)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException ex)
        {
            // Cancel runs every callback and then reports the failures together; one entry per failure keeps them readable.
            foreach (Exception inner in ex.InnerExceptions)
            {
                _TryLog(context, "A cancellation callback failed: " + PluginActivator.Describe(inner), inner);
            }
        }
    }

    /// <summary>The context of a record, or <see langword="null"/> when the application cannot give one; the caller then goes on without it.</summary>
    /// <param name="contextFor">The application's context factory.</param>
    /// <param name="record">The plugin record.</param>
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

    /// <summary>Writes one error entry; a log that fails must not stop the lifecycle of the other plugins.</summary>
    /// <param name="context">The context whose log receives the entry; <see langword="null"/> drops the entry.</param>
    /// <param name="text">The entry text.</param>
    /// <param name="exception">The exception behind the entry, or <see langword="null"/>.</param>
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
            // The entry is lost. For a failed start the reason is also part of the plugin's record; a lost stop entry has no copy.
        }
    }

    #endregion
}
