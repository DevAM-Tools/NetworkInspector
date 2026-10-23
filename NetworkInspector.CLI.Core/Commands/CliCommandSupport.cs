// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// The session helpers that <c>export</c> and <c>convert</c> share: Ctrl+C handling, opening sources, the BLF cache option,
/// the text of a refused target, the cleanup of an unfinished output, and the settling of replaced outputs. One copy, so a
/// change of cancellation or overwrite behavior reaches both commands. Stateless; thread-safe.
/// </summary>
internal static class CliCommandSupport
{
    #region Internal API

    /// <summary>Applies the BLF cache option to BLF sources that do not set it themselves.</summary>
    /// <param name="config">The parsed source.</param>
    /// <param name="blfCacheBudgetBytes">The budget in bytes; 0 leaves the source as it is.</param>
    /// <returns>The source with the default option, or <paramref name="config"/> itself.</returns>
    internal static SourceConfig WithBlfCache(SourceConfig config, int blfCacheBudgetBytes) =>
        string.Equals(config.Registration.Kind, "BLF", StringComparison.OrdinalIgnoreCase) && blfCacheBudgetBytes > 0
            ? config.WithDefaultOption("cacheBudgetBytes", blfCacheBudgetBytes.ToString(CultureInfo.InvariantCulture))
            : config;

    /// <summary>Creates, registers, and starts every source; one error line and an exit code when one fails.</summary>
    /// <param name="configs">The parsed sources.</param>
    /// <param name="stack">The stack whose settings the sources read.</param>
    /// <param name="frames">The registry that gives each source its id.</param>
    /// <param name="sources">Receives the sources that were created (the caller disposes them, also after a failure).</param>
    /// <param name="io">Receives the error line.</param>
    /// <returns><see cref="ExitCode.Success"/> or the exit code of the failure.</returns>
    internal static int OpenSources(List<SourceConfig> configs, Stack stack, FrameInterfaceRegistry frames, List<IFrameSource> sources,
        CliIo io)
    {
        foreach (SourceConfig config in configs)
        {
            try
            {
                if (!config.TryCreateSource(stack.Settings, out IFrameSource? source, out PluginError? error))
                {
                    io.Error.WriteLine(error.Value.Code == PluginErrorCode.InvalidOption
                        ? "Error: " + error.Value.Message
                        : "Error opening source: " + error.Value.Message);
                    return error.Value.Code == PluginErrorCode.InvalidOption
                        ? (int)ExitCode.ArgumentError
                        : (int)ExitCode.SourceOpenError;
                }

                sources.Add(source);
                FrameSourceId sourceId = frames.RegisterSource(source);
                source.Start(sourceId, frames);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A plugin's source is plugin code: whatever it throws ends this command with an error line, never the process.
                io.Error.WriteLine($"Error opening source: {ex.Message}");
                return (int)ExitCode.SourceOpenError;
            }
        }

        return (int)ExitCode.Success;
    }

    /// <summary>The text for a target that cannot be used; an existing output also says how to replace it.</summary>
    /// <param name="error">The error of <see cref="ExportTargets.TryPrepare"/> or <see cref="ExportTargets.TryCheck"/>.</param>
    /// <param name="outputPath">The path as the user typed it.</param>
    /// <returns>One line of English text.</returns>
    internal static string TargetText(PluginError? error, string outputPath)
    {
        if (error is { Code: PluginErrorCode.TargetExists } existing && existing.Message.EndsWith(" exists.", StringComparison.Ordinal))
        {
            return Invariant($"Output '{Path.GetFullPath(outputPath)}' exists; pass --overwrite to replace it.");
        }

        return error is null
            ? string.Empty
            : error.Value.Message;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a token that Ctrl+C cancels (the process then continues and cleans up instead of being
    /// killed). A cancellation callback that throws is reported as a warning; it never ends the process from the console thread.
    /// </summary>
    /// <param name="io">Receives the cancellation notice.</param>
    /// <param name="body">The command; it receives the combined token.</param>
    /// <param name="cancellationToken">The outer token; cancelling it cancels the body's token as well.</param>
    /// <returns>The exit code of <paramref name="body"/>.</returns>
    internal static async Task<int> RunCancellableAsync(CliIo io, Func<CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            _Cancel(cts, io.Error);
            io.Error.WriteLine("Cancellation requested...");
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await body(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Disposes an unfinished listener and deletes its output (the Core exporters finish their file on <c>Dispose</c>, so the
    /// delete comes after it). A problem while deleting is a warning; it must not hide the failure that led here.
    /// </summary>
    /// <typeparam name="TListener">The listener type of the command (frame or packet listener).</typeparam>
    /// <param name="listener">The open listener; <see langword="null"/> afterwards.</param>
    /// <param name="currentPath">The path of its output; <see langword="null"/> afterwards.</param>
    /// <param name="isDirectory"><see langword="true"/> for a directory output.</param>
    /// <param name="error">Receives the warnings.</param>
    internal static void DiscardUnfinished<TListener>(ref TListener? listener, ref string? currentPath, bool isDirectory, TextWriter error)
        where TListener : class
    {
        if (listener is null)
        {
            return;
        }

        try
        {
            (listener as IDisposable)?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: a listener that cannot close does not stop the cleanup of its file.
            error.WriteLine($"Warning: closing the unfinished output failed: {ex.Message}");
        }

        if (currentPath is not null)
        {
            string? problem = ExportTargets.DeletePartial(Path.GetFullPath(currentPath), isDirectory);
            if (problem is not null)
            {
                error.WriteLine($"Warning: the unfinished output '{currentPath}' could not be removed: {problem}");
            }
        }

        listener = null;
        currentPath = null;
    }

    /// <summary>
    /// Ends the replacement of old outputs after a run: a new output that survived replaced the old one for good, a missing one
    /// (cancelled, failed, never opened) gets the old one back. Problems are warnings that name where the old output is kept.
    /// </summary>
    /// <param name="replacements">The replacements that <see cref="ExportTargets.TryPrepare"/> returned during the run.</param>
    /// <param name="error">Receives the warnings.</param>
    internal static void SettleReplacements(List<TargetReplacement> replacements, TextWriter error)
    {
        foreach (TargetReplacement replacement in replacements)
        {
            string? problem = replacement.Settle();
            if (problem is not null)
            {
                error.WriteLine("Warning: " + problem);
            }
        }
    }

    /// <summary>
    /// Takes back a prepared target whose listener could not be created: whatever the failed attempt left at the path is removed,
    /// so that <see cref="SettleReplacements"/> restores the old output. The text names a leftover that could not be removed.
    /// </summary>
    /// <param name="fullPath">The prepared full path.</param>
    /// <param name="isDirectory"><see langword="true"/> for a directory output.</param>
    /// <param name="message">The reason the listener was not created.</param>
    /// <returns>The reason, extended by the problem of the removal when there was one.</returns>
    internal static string AbandonTarget(string fullPath, bool isDirectory, string message)
    {
        // The path was free (or the old output was moved aside) when it was prepared, so anything there now is from this attempt.
        string? problem = ExportTargets.DeletePartial(fullPath, isDirectory);
        return problem is null
            ? message
            : message + " (" + problem + ")";
    }

    #endregion

    #region Private helpers

    // Cancels from the console thread. Plugin code may have registered callbacks on the token (exporters receive it); a throwing
    // callback surfaces here as an AggregateException and must not end the process.
    private static void _Cancel(CancellationTokenSource source, TextWriter error)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException ex)
        {
            error.WriteLine("Warning: a cancellation callback failed: " + ex.InnerException?.Message);
        }
        catch (ObjectDisposedException)
        {
            // The command finished between the key press and the call: nothing is left to cancel.
        }
    }

    #endregion
}
