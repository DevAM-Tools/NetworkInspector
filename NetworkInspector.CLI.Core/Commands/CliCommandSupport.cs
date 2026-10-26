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

    /// <summary>
    /// Applies the BLF cache option (<c>--blf-cache-size</c>) to BLF sources that do not set it themselves. The command-line
    /// option is only a default: a <c>cacheBudgetBytes</c> given in the source spec wins, and sources of other kinds never see it.
    /// </summary>
    /// <param name="config">The parsed source.</param>
    /// <param name="blfCacheBudgetBytes">The budget in bytes; 0 leaves the source as it is.</param>
    /// <returns>The source with the default option, or <paramref name="config"/> itself.</returns>
    internal static SourceConfig WithBlfCache(SourceConfig config, int blfCacheBudgetBytes) =>
        string.Equals(config.Registration.Kind, "BLF", StringComparison.OrdinalIgnoreCase) && blfCacheBudgetBytes > 0
            ? config.WithDefaultOption("cacheBudgetBytes", blfCacheBudgetBytes.ToString(CultureInfo.InvariantCulture))
            : config;

    /// <summary>
    /// Creates, registers, and starts every source in the order given (the order is the round-robin order of the loops); one
    /// error line and an exit code when one fails, and the sources after it are not opened.
    /// </summary>
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
                // An expected failure comes back as an error value: an invalid option text is the user's mistake (exit 1), anything
                // else (missing file, bad header) is a source that cannot be opened (exit 2).
                if (!config.TryCreateSource(stack.Settings, out IFrameSource? source, out PluginError? error))
                {
                    io.Error.WriteLine(error.Value.Code == PluginErrorCode.InvalidOption
                        ? "Error: " + error.Value.Message
                        : "Error opening source: " + error.Value.Message);
                    return error.Value.Code == PluginErrorCode.InvalidOption
                        ? (int)ExitCode.ArgumentError
                        : (int)ExitCode.SourceOpenError;
                }

                // The source goes into the caller's list before it is registered and started, so the caller disposes it even
                // when RegisterSource or Start throws. The registry gives the source its id; Start needs the id and the registry.
                sources.Add(source);
                FrameSourceId sourceId = frames.RegisterSource(source);
                source.Start(sourceId, frames);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A plugin's source is plugin code: whatever it throws ends this command with an error line, never the process.
                // A cancellation is excluded on purpose: it is not an open failure and propagates to the cancelling command.
                io.Error.WriteLine($"Error opening source: {ex.Message}");
                return (int)ExitCode.SourceOpenError;
            }
        }

        return (int)ExitCode.Success;
    }

    /// <summary>The text for a target that cannot be used; an existing output also says how to replace it.</summary>
    /// <param name="error">The error of <see cref="ExportTargets.TryPrepare"/> or <see cref="ExportTargets.TryCheck"/>.</param>
    /// <param name="outputPath">The path that was checked or prepared (a split path in a split run), as the caller built it.</param>
    /// <returns>One line of English text.</returns>
    internal static string TargetText(PluginError? error, string outputPath)
    {
        // Only the plain "Output '...' exists." refusal gets the hint. The other refusals share the error code but are not cured by
        // --overwrite (a folder where a file is wanted, a foreign non-empty folder), so they keep their own text. The text is the
        // only thing that tells them apart, hence the suffix test.
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

        // Ctrl+C is turned into a cooperative cancellation: e.Cancel keeps the process alive so that the command can dispose its
        // listener, delete the unfinished output, and give replaced outputs back. Without it the runtime
        // would kill the process in the middle of a write and leave a partial output behind. The handler runs on a console
        // thread, not on the command's thread.
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            _Cancel(cts, io.Error);
            WriteLocked(io.Error, "Cancellation requested...");
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await body(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            // The handler is removed before the token source is disposed (end of the using); a key press in between is absorbed
            // by the ObjectDisposedException guard of _Cancel.
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Writes one line to the error writer from a thread that is not the command's thread (the console thread of Ctrl+C). The
    /// plugin log and the session lock the error writer itself, because a text writer is not thread-safe and only the console
    /// writer is synchronized by the runtime; an injected writer is not. Without the same lock the notice could interleave with
    /// a plugin's line and tear it. Only the write happens under the lock.
    /// </summary>
    /// <param name="error">The error writer of the command; the lock object shared with <see cref="CliPluginLog"/>.</param>
    /// <param name="line">The line without line break.</param>
    internal static void WriteLocked(TextWriter error, string line)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(line);
        lock (error)
        {
            error.WriteLine(line);
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
        // The state is passed by reference and reset at the end, so the caller cannot discard the same output twice (the call is
        // safe to repeat); a null listener means there is nothing unfinished.
        if (listener is null)
        {
            return;
        }

        // Dispose first: the Core exporters complete their file on Dispose, so only after it is there a final file to delete.
        try
        {
            (listener as IDisposable)?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plugin code: a listener that cannot close does not stop the cleanup of its file.
            error.WriteLine($"Warning: closing the unfinished output failed: {ex.Message}");
        }

        // Then delete whatever is at the output path. A finished output is never passed here: the loops reset the path when they
        // finish a listener, so only the output still being written is removed.
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
    /// Call it from a <c>finally</c> block, so the old output is never left under a hidden backup name, whatever the run did.
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
            WriteLocked(error, "Warning: a cancellation callback failed: " + ex.InnerException?.Message);
        }
        catch (ObjectDisposedException)
        {
            // The command finished between the key press and the call: nothing is left to cancel.
        }
    }

    #endregion
}
