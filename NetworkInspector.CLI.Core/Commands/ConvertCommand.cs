// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Frame-level format conversion command.
/// Reads frames from one or more sources and writes them to a target format
/// (PCAPNG, BLF, ASC, or a plugin's frame format) without protocol parsing. Sources and formats come from the plugin registry.
/// A frame is parsed only when <c>--filter</c> is set; without a filter the frames are handed to the exporter as they were read.
/// Stateless; each run owns its sources, listener, and plugin session, and its frame loop is single-threaded.
/// </summary>
internal static class ConvertCommand
{
    #region Types

    /// <summary>
    /// The parsed arguments of one run. Units: <c>MaxFrames</c> and <c>SplitCount</c> count frames and <c>ProgressInterval</c> is
    /// a frame interval (zero disables each); <c>SplitSizeBytes</c> and <c>BlfCacheBudgetBytes</c> are bytes, already converted
    /// from the MiB the user typed (zero disables the split / leaves the BLF default).
    /// </summary>
    private sealed record ConvertArguments(
        List<string> SourceSpecs,
        string OutputPath,
        string? OutputFormatSpec,
        string? FilterExpression,
        CliSettingsOptions Settings,
        int MaxFrames,
        long SplitSizeBytes,
        int SplitCount,
        int ProgressInterval,
        int BlfCacheBudgetBytes,
        bool Tolerant,
        bool Overwrite);

    #endregion

    #region Public API

    /// <summary>Runs the convert command on the console (the real standard output and error, no cancellation from outside).</summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsync(string[] args) => RunAsync(args, CliIo.FromConsole(), CancellationToken.None);

    /// <summary>Runs the convert command with injected writers.</summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsync(string[] args, CliIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        return CliArgumentParsing.RunWithArgumentGuardAsync(() => _RunCoreAsync(args, io, cancellationToken), io.Error);
    }

    /// <summary>
    /// Reads frames round-robin from <paramref name="sources"/> and hands the accepted ones to
    /// exporters created on demand by <paramref name="createExporter"/>. Returns the number of
    /// frames written and reports how many outputs were finalized in <paramref name="filesWritten"/>.
    /// </summary>
    /// <remarks>
    /// A frame is judged before an output is opened, so a filter that matches nothing leaves no
    /// empty file behind. When <paramref name="filter"/> is <see langword="null"/> the loop stays
    /// frame-level and never parses; <paramref name="stack"/> is then unused and may be
    /// <see langword="null"/>. A filter that cannot produce a verdict aborts the loop rather than
    /// writing an output whose contents silently depend on where filtering stopped. A cancelled or failed
    /// conversion ends without <c>OnFinish</c>: the listener is disposed and its output is deleted, so no partial file looks
    /// like a result. Thread safety: single-threaded; the sources, the filter, and the listeners are driven from the calling thread
    /// only (none of them is thread-safe).
    /// </remarks>
    /// <param name="sources">Active, already-started frame sources; read round-robin, one frame per source per turn.</param>
    /// <param name="createExporter">Opens the listener for an output path; called for the first output and for every split.</param>
    /// <param name="splitManager">The split policy and the numbering of output paths.</param>
    /// <param name="stack">The stack for parsing; needed only when <paramref name="filter"/> is set.</param>
    /// <param name="filter">Optional packet filter; frames of rejected packets are not written.</param>
    /// <param name="maxFrames">Stop after this many frames were written (0 = unlimited).</param>
    /// <param name="progressInterval">Print a progress line every N written frames (0 = silent).</param>
    /// <param name="tolerant">
    /// When <see langword="true"/>, an exception from reading a frame is reported as a warning and the loop moves on to the next
    /// source. A source that fails again before it delivered a frame is dropped with a warning that names it, so a source that
    /// fails on every read cannot keep the loop running forever. It does not cover parse or filter errors, and a failing writer
    /// always aborts.
    /// </param>
    /// <param name="error">Receives progress lines and warnings.</param>
    /// <param name="cancellationToken">Passed to the sources; a cancelled read throws <see cref="OperationCanceledException"/>.</param>
    /// <param name="filesWritten">Number of output files that were finished (<c>OnFinish</c> called).</param>
    /// <param name="listenerStopped">
    /// <see langword="true"/> when the loop ended early because a listener returned <see langword="false"/> from <c>OnFrame</c>
    /// (it unsubscribed itself, for example after an error); the open output was finished normally, but the input was not
    /// converted completely. A listener that stops because of a cancellation is not reported here: that run ends as cancelled.
    /// </param>
    /// <returns>
    /// The number of frames handed to the listener. A <see langword="long"/>: several sources can together deliver more than
    /// <see cref="int.MaxValue"/> frames when no frame limit is set.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The filter or a parse failed, size splitting is requested for an exporter without byte progress, or an output could not be
    /// prepared; the unfinished output was discarded.
    /// </exception>
    /// <exception cref="CliUsageException"><paramref name="createExporter"/> found an invalid option text; the unfinished output was discarded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the unfinished output was discarded.</exception>
    internal static long RunConvertLoop(
        List<IFrameSource> sources,
        Func<string, IFrameListener> createExporter,
        SplitOutputManager splitManager,
        Stack? stack,
        IFilter? filter,
        int maxFrames,
        int progressInterval,
        bool tolerant,
        TextWriter error,
        CancellationToken cancellationToken,
        out int filesWritten,
        out bool listenerStopped)
    {
        // Loop state. exporter and currentPath describe the output that is open right now (and only that one): they are cleared
        // as soon as an output is finished, so the cleanup in the catch block can never delete a finished output.
        // filterPacketId counts every parsed frame, not only the written ones: packet ids must be dense and in order.
        // The frame counters are 64 bits wide: with no frame limit and several sources the total can pass int.MaxValue, and a
        // wrapped 32-bit counter would turn negative, break the progress interval, and compare wrongly against the split count.
        long totalFrames = 0;
        long fileFrames = 0;
        int filterPacketId = 0;
        filesWritten = 0;
        listenerStopped = false;
        IFrameListener? exporter = null;
        string? currentPath = null;
        IExportByteProgress? byteProgress = null;

        // The index exists only for a filter. One index serves the whole run: the filter reads protocol presence from it in O(1)
        // instead of walking the field tree of each packet. Without a filter no index (and no parsing) costs anything.
        PacketIndex? filterIndex = filter is not null && stack is not null ? new PacketIndex(stack) : null;

        RoundRobinSourceIterator iterator = new(sources);

        try
        {
            while (iterator.HasActive && (maxFrames == 0 || totalFrames < maxFrames))
            {
                // Read the next frame of the current source. A cancelled read must reach the catch block below; any other read
                // error is skipped only in tolerant mode.
                IFrameSource source = iterator.Current;
                Frame? frame;
                try
                {
                    frame = source.NextFrame(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (tolerant)
                    {
                        // A source that fails again before it delivered a frame cannot move past the problem: it is dropped, so
                        // that a source which throws on every read cannot keep the run alive forever.
                        bool dropped = iterator.RecordReadFailureAndAdvance();
                        error.WriteLine(dropped
                            ? Invariant($"Warning: Giving up on {source.UiName}: it failed again without delivering a frame: {ex.Message}")
                            : Invariant($"Warning: Skipping frame from {source.UiName}: {ex.Message}"));
                        continue;
                    }

                    throw;
                }

                // A null frame is the end of that source: it leaves the rotation and the others carry on.
                if (frame is null)
                {
                    iterator.MarkCurrentExhaustedAndAdvance();
                    continue;
                }

                iterator.RecordFrameRead();

                // Judged before an output is opened, so a filter that matches nothing leaves no
                // empty file behind. This is the only place where a frame is parsed during a conversion. The packet is parsed with
                // the shared index so that the filter finds protocol presence in O(1); the capacity check comes first so that a
                // run over too many packets ends with an error that names the packets. Each frame is parsed into a new packet
                // here (the export loop reuses one packet instead).
                if (filter is not null && stack is not null && filterIndex is not null)
                {
                    ArrayIndexIdRange.ThrowIfInvalidNextIndex(filterPacketId, "packet");
                    ParseOptions filterOptions = new(index: filterIndex);
                    if (!Packet.TryParse(
                        new PacketId(filterPacketId++),
                        stack,
                        frame.Value,
                        in filterOptions,
                        out Packet? packet,
                        out ParseFailure filterFailure))
                    {
                        throw new InvalidOperationException(filterFailure.ToString());
                    }
                    if (!CliFilter.TryMatch(filter, packet!, filterIndex, error, out bool matched))
                    {
                        throw new InvalidOperationException(
                            "Filter evaluation failed; the conversion was aborted to avoid writing a partially filtered output.");
                    }

                    // A rejected frame still consumes the source's turn, so a selective filter does not let one source starve the
                    // others.
                    if (!matched)
                    {
                        iterator.Advance();
                        continue;
                    }
                }

                // Open the first output, or rotate to the next one when the split policy asks for it. The size comes from an
                // in-memory counter of the exporter (no file-system probe), which is cheap enough to read for every frame.
                long estimatedBytes = byteProgress?.EstimatedOutputBytes ?? 0;
                if (exporter is null
                    || (splitManager.IsSplitting
                        && splitManager.NeedsSplit(estimatedBytes, fileFrames)))
                {
                    // Finish the previous output first: OnFinish completes it, Dispose releases the file handle.
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        filesWritten++;

                        // Finished: from here on nothing of it is cleaned up, even when the next output cannot be opened.
                        exporter = null;
                        currentPath = null;
                    }

                    // currentPath is set only after the factory returned, so a factory that fails leaves nothing to discard here
                    // (the factory removes what its own attempt left behind).
                    string nextPath = splitManager.NextPath();
                    exporter = createExporter(nextPath);
                    currentPath = nextPath;

                    // Size splitting reads the exporter's live byte counter; an exporter without one cannot be split by size, which
                    // is refused here instead of silently never splitting.
                    byteProgress = exporter as IExportByteProgress;
                    if (splitManager.IsSizeSplitting && byteProgress is null)
                    {
                        throw new InvalidOperationException(
                            "Size-based splitting (--split-size) requires an exporter that reports " +
                            "IExportByteProgress.EstimatedOutputBytes.");
                    }

                    fileFrames = 0;
                }

                // Write the frame, then count it. Counters change only after the write, so a failing write is not counted. false
                // means the listener unsubscribed itself (for example an exporter in strict mode after its first recoverable
                // error): reading on would hand frames to a listener that no longer takes them and drop them silently. A listener
                // that stops because the run was cancelled makes the loop end as cancelled, so its output is discarded.
                bool continueConvert = exporter.OnFrame(frame.Value);
                if (!continueConvert)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                totalFrames++;
                fileFrames++;

                if (progressInterval > 0 && totalFrames % progressInterval == 0)
                {
                    error.WriteLine(Invariant($"Progress: {totalFrames} frames written"));
                }

                if (!continueConvert)
                {
                    listenerStopped = true;
                    break;
                }

                if (maxFrames > 0 && totalFrames >= maxFrames)
                {
                    break;
                }

                iterator.Advance();
            }

            // Normal end (sources exhausted or limit reached): finish the last output. This is the only path that finalizes the
            // output that is still open; every other exit discards it in the catch block.
            if (exporter is not null)
            {
                exporter.OnFinish();
                (exporter as IDisposable)?.Dispose();
                filesWritten++;
                exporter = null;
                currentPath = null;
            }

            return totalFrames;
        }
        catch
        {
            // Cancelled or failed: the output was not finished, so it is disposed and deleted instead of finalized. A cancellation
            // arrives here as the OperationCanceledException of a source read; the caller maps it to its own exit code.
            CliCommandSupport.DiscardUnfinished(ref exporter, ref currentPath, splitManager.IsDirectoryOutput, error);
            throw;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Parses arguments and executes conversion; throws <see cref="ArgumentException"/> on bad args (the caller maps it to exit
    /// code 1).
    /// </summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> _RunCoreAsync(string[] args, CliIo io, CancellationToken cancellationToken)
    {
        // Help is only recognized as the first argument; no arguments at all is an error with the same usage text.
        if (args.Length == 0 || CliArgumentParsing.IsHelpFlag(args[0]))
        {
            _PrintUsage(io.Error);
            if (args.Length > 0 && CliArgumentParsing.IsHelpFlag(args[0]))
            {
                return (int)ExitCode.Success;
            }

            return (int)ExitCode.ArgumentError;
        }

        List<string> sourceSpecs = [];
        string? outputPath = null;
        string? outputFormatSpec = null;
        string? filterExpression = null;
        CliSettingsOptionsBuilder settings = new();
        int maxFrames = 0;
        long splitSize = 0;          // MiB; converted to bytes before use
        int splitCount = 0;          // frames
        int progressInterval = 0;
        long blfCacheSize = 0;        // MiB; for BLF sources
        bool tolerant = false;
        bool overwrite = false;

        // One pass over the arguments. Settings options are offered first so that they behave the same in every command; a
        // value-taking option consumes the next argument (GetNextArg advances i). Everything that is not an option is a source
        // spec, including a mistyped option such as "--outptu": it is then rejected by the source parsing with its own message.
        for (int i = 0; i < args.Length; i++)
        {
            if (CliArgumentParsing.ParseSettingsOption(args, ref i, settings))
            {
                continue;
            }

            switch (args[i].ToUpperInvariant())
            {
                case "-O" or "--OUTPUT":
                    outputPath = CliArgumentParsing.GetNextArg(args, ref i, "--output");
                    break;
                case "--OUTPUT-FORMAT" or "--FORMAT" or "-F":
                    outputFormatSpec = CliArgumentParsing.GetNextArg(args, ref i, "--output-format");
                    break;
                case "--FILTER":
                    filterExpression = CliArgumentParsing.GetNextArg(args, ref i, "--filter");
                    break;
                case "-N" or "--MAX-FRAMES":
                    maxFrames = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--max-frames"),
                        "--max-frames");
                    break;
                case "--SPLIT-SIZE":
                    splitSize = CliArgumentParsing.ParseNonNegativeLong(
                        CliArgumentParsing.GetNextArg(args, ref i, "--split-size"));
                    break;
                case "--SPLIT-COUNT":
                    splitCount = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--split-count"),
                        "--split-count");
                    break;
                case "--PROGRESS":
                    progressInterval = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--progress"),
                        "--progress");
                    break;
                case "--TOLERANT":
                    tolerant = true;
                    break;
                case "--OVERWRITE":
                    overwrite = true;
                    break;
                case "--BLF-CACHE-SIZE":
                    blfCacheSize = CliArgumentParsing.ParseNonNegativeLong(
                        CliArgumentParsing.GetNextArg(args, ref i, "--blf-cache-size"));
                    break;
                default:
                    sourceSpecs.Add(args[i]);
                    break;
            }
        }

        // Required parts, checked before anything is opened. "-" (standard output) is refused as a target: every output is a
        // path that the run prepares, finishes, and, when the run does not complete, deletes or restores.
        if (sourceSpecs.Count == 0)
        {
            io.Error.WriteLine("Error: No source files specified.");
            return (int)ExitCode.ArgumentError;
        }

        if (string.IsNullOrEmpty(outputPath) || outputPath == "-")
        {
            io.Error.WriteLine(
                "Error: Output file path required (-o / --output). Stdout ('-') is not supported.");
            return (int)ExitCode.ArgumentError;
        }

        // MiB to bytes with overflow checks, so a typo such as 99999999999 is a usage error here and not a wrapped negative size.
        int blfCacheBudgetBytes = 0;
        if (blfCacheSize > 0)
        {
            blfCacheBudgetBytes = CliArgumentParsing.MiBToCacheBudgetBytes(
                blfCacheSize, "--blf-cache-size");
        }

        long splitSizeBytes = CliArgumentParsing.MiBToSplitSizeBytes(splitSize, "--split-size");

        return await _ExecuteAsync(
            new ConvertArguments(sourceSpecs, outputPath, outputFormatSpec, filterExpression, settings.Build(), maxFrames,
                splitSizeBytes, splitCount, progressInterval, blfCacheBudgetBytes, tolerant, overwrite),
            io,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the conversion pipeline inside a plugin session. The session (plugins started, settings loaded) is open for the
    /// whole run and disposed after it, so the plugins are stopped even when the run fails or is cancelled; Ctrl+C cancels the
    /// token for both the plugin start and the frame loop.
    /// </summary>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">The outer token; linked with Ctrl+C.</param>
    /// <returns>The exit code.</returns>
    private static Task<int> _ExecuteAsync(ConvertArguments arguments, CliIo io, CancellationToken cancellationToken) =>
        CliCommandSupport.RunCancellableAsync(io, async token =>
        {
            // A session that cannot open has already printed its error line; its exit code ends the command.
            (CliPluginSession? opened, int openExit) = await CliPluginSession.OpenAsync(arguments.Settings, io, token)
                .ConfigureAwait(false);
            if (opened is null)
            {
                return openExit;
            }

            // The conversion itself is synchronous (sources and listeners are blocking APIs); only the session open and close are
            // asynchronous.
            await using (opened.ConfigureAwait(false))
            {
                return _Run(opened, arguments, io, token);
            }
        }, cancellationToken);

    /// <summary>
    /// Resolves the specs, builds the stack, opens the sources, and runs the loop. The steps are ordered from cheap to
    /// expensive so that a mistake of the user is reported before any source is opened or any file is touched.
    /// </summary>
    /// <param name="session">The open plugin session.</param>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="token">Cancels the run (Ctrl+C).</param>
    /// <returns>The exit code.</returns>
    private static int _Run(CliPluginSession session, ConvertArguments arguments, CliIo io, CancellationToken token)
    {
        // Specs resolve through the registry, so plugin kinds and formats work exactly like built-in ones. A bad spec is a
        // usage error (exit 1), not a source error.
        FrameFormatConfig format;
        List<SourceConfig> configs;
        try
        {
            format = arguments.OutputFormatSpec is { Length: > 0 } formatSpec
                ? FrameFormatConfig.Parse(formatSpec, session.Registry)
                : FrameFormatConfig.FromExtension(Path.GetExtension(arguments.OutputPath), session.Registry);
            configs = arguments.SourceSpecs.ConvertAll(spec => CliCommandSupport.WithBlfCache(SourceConfig.Parse(spec, session.Registry),
                arguments.BlfCacheBudgetBytes));
        }
        catch (ArgumentException ex)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return (int)ExitCode.ArgumentError;
        }

        // Unknown or mistyped option texts are refused before any work; the registration judges the rest when it opens.
        using (SettingsManager options = RegistrationOptions.CreateDefaults(format.Registration.RegisterOptions))
        {
            if (!RegistrationOptions.TryApplyTexts(options, format.OptionTexts, out PluginError? optionError))
            {
                io.Error.WriteLine("Error: " + optionError.Value.Message);
                return (int)ExitCode.ArgumentError;
            }
        }

        // An existing output is refused (or accepted with --overwrite) before a long run starts; nothing is deleted yet. The
        // check uses a manager of its own, so the manager of the run still starts at its first path. Only the first path is
        // checked here; a later split path that exists is refused when its output is opened.
        SplitOutputManager firstPathManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount);
        // The refusal names the path that was checked. In a split run that is the first split file (base_00001.ext), not the base
        // name the user typed, which is never written: naming the typed path would point at a file that does not exist.
        string firstPath = firstPathManager.NextPath();
        if (!ExportTargets.TryCheck(firstPath, isDirectory: false, arguments.Overwrite, out _, out PluginError? targetError))
        {
            io.Error.WriteLine("Error: " + CliCommandSupport.TargetText(targetError, firstPath));
            return (int)ExitCode.ArgumentError;
        }

        // An exporter request carries the stack, so conversion always builds one (a few milliseconds); frames are parsed only
        // when a filter is set.
        FrameInterfaceRegistry frames = new();
        if (!session.TryBuildStack(frames, out Stack? stack, out int stackExit))
        {
            return stackExit;
        }

        // The stack outlives the sources: sources are disposed in the inner finally, the stack by the using around it. The filter
        // is compiled before the first source opens, so an invalid expression ends the command without touching any input.
        List<IFrameSource> sources = [];
        using (stack)
        {
            try
            {
                IFilter? filter = null;
                if (CliFilter.IsActive(arguments.FilterExpression)
                    && !CliFilter.TryCompile(arguments.FilterExpression, stack, io.Error, out filter))
                {
                    return (int)ExitCode.ArgumentError;
                }

                // Sources are plugin code: a failure to open one ends the command with the exit code of OpenSources; the sources
                // opened so far are in the list and are disposed below.
                int sourceExit = CliCommandSupport.OpenSources(configs, stack, frames, sources, io);
                if (sourceExit != (int)ExitCode.Success)
                {
                    return sourceExit;
                }

                return _RunLoop(arguments, format, sources, stack, filter, io, token);
            }
            finally
            {
                CliSourceLifetime.DisposeSources(sources, io.Error);
            }
        }
    }

    /// <summary>
    /// Runs the convert loop and prints the summary on the error writer (the conversion has no result for standard output).
    /// The exceptions of the loop become exit codes here: a cancellation is a success (0) because it is the user's decision, a
    /// <see cref="CliUsageException"/> is a usage error (1), everything else a runtime failure (3).
    /// </summary>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="format">The output format.</param>
    /// <param name="sources">The opened sources.</param>
    /// <param name="stack">The stack of the session.</param>
    /// <param name="filter">The compiled filter, or <see langword="null"/>.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="token">Cancels the run (Ctrl+C).</param>
    /// <returns>The exit code.</returns>
    private static int _RunLoop(ConvertArguments arguments, FrameFormatConfig format, List<IFrameSource> sources, Stack stack,
        IFilter? filter, CliIo io, CancellationToken token)
    {
        // Old outputs that --overwrite moved aside; they are settled in the finally block below, whatever the run did.
        List<TargetReplacement> replacements = [];
        try
        {
            SplitOutputManager splitManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount);
            long totalFrames = RunConvertLoop(
                sources,
                path => _CreateListener(format, path, stack, arguments.Overwrite, replacements, token),
                splitManager,
                stack,
                filter,
                arguments.MaxFrames,
                arguments.ProgressInterval,
                arguments.Tolerant,
                io.Error,
                token,
                out int filesWritten,
                out bool listenerStopped);

            // The loop ended early because the writer stopped taking frames: that is a failure, not a complete conversion.
            if (listenerStopped)
            {
                io.Error.WriteLine(Invariant(
                    $"Error during conversion: the output format stopped after {totalFrames} frames, before the input was converted completely."));
                return (int)ExitCode.RuntimeError;
            }

            io.Error.WriteLine(Invariant($"Conversion complete: {totalFrames} frames written to {filesWritten} file(s)."));
            return (int)ExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            io.Error.WriteLine("Conversion cancelled.");
            return (int)ExitCode.Success;
        }
        catch (CliUsageException ex)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return (int)ExitCode.ArgumentError;
        }
        catch (Exception ex)
        {
            io.Error.WriteLine($"Error during conversion: {ex.Message}");
            return (int)ExitCode.RuntimeError;
        }
        finally
        {
            // A new output that survived the run replaced the old one for good; a missing one (cancelled, failed, never
            // opened) gets the old one back.
            CliCommandSupport.SettleReplacements(replacements, io.Error);
        }
    }

    /// <summary>
    /// Prepares the target (an existing output is replaced only with <c>--overwrite</c>: it is moved aside and added to
    /// <paramref name="replacements"/>) and creates the listener. Problems of the user's input become a
    /// <see cref="CliUsageException"/>; the rest are errors of the run. A listener that cannot be created gives the old output
    /// back after the run.
    /// </summary>
    /// <param name="format">The output format.</param>
    /// <param name="path">The path of the output (a split path when splitting).</param>
    /// <param name="stack">The stack of the session.</param>
    /// <param name="overwrite">Whether an existing output may be replaced.</param>
    /// <param name="replacements">Collects the old outputs that were moved aside; settled after the run.</param>
    /// <param name="token">Cancels the export.</param>
    /// <returns>The listener.</returns>
    /// <exception cref="InvalidOperationException">The target cannot be used or the listener cannot be created.</exception>
    /// <exception cref="CliUsageException">An option text of the format is invalid.</exception>
    private static IFrameListener _CreateListener(FrameFormatConfig format, string path, Stack stack, bool overwrite,
        List<TargetReplacement> replacements, CancellationToken token)
    {
        if (!ExportTargets.TryPrepare(path, isDirectory: false, overwrite, out string? fullPath, out TargetReplacement? replacement,
            out PluginError? targetError))
        {
            throw new InvalidOperationException(CliCommandSupport.TargetText(targetError, path));
        }

        if (replacement is not null)
        {
            replacements.Add(replacement);
        }

        if (!format.TryCreate(fullPath, stack, token, out IFrameListener? listener, out PluginError? error))
        {
            // Nothing was written: the leftover is removed, and the settling after the run gives the old output back.
            string message = CliCommandSupport.AbandonTarget(fullPath, isDirectory: false, error.Value.Message);
            throw error.Value.Code == PluginErrorCode.InvalidOption
                ? new CliUsageException(message)
                : new InvalidOperationException(message);
        }

        return listener;
    }

    /// <summary>Prints usage information for the convert command.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: ni convert <sources...> -o <output> [options]");
        error.WriteLine();
        error.WriteLine("Convert capture files between formats (frame-level, no protocol parsing).");
        error.WriteLine();
        error.WriteLine("Arguments:");
        error.WriteLine("  <sources...>          One or more source files or specs");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  -o, --output          Output file path (required)");
        error.WriteLine("  --output-format, -f   Output format spec (see below; overrides extension)");
        error.WriteLine("  --overwrite           Replace an existing output (otherwise an existing output is an error)");
        error.WriteLine("  -n, --max-frames      Maximum number of frames to convert");
        error.WriteLine("  --split-size <MB>     Split when exporter EstimatedOutputBytes reaches this size (MiB; live, no FS probe)");
        error.WriteLine("  --split-count <N>     Split output every N frames");
        CliFilter.PrintUsageLines(error);
        error.WriteLine("                        (frames are parsed only when a filter is set)");
        error.WriteLine("  --profile <name>      Settings profile (must exist; 'default' is created on first use)");
        error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        error.WriteLine("  --settings-max-config-file-bytes <bytes>");
        error.WriteLine("                        Max settings/referenced config JSON size in bytes (default 1073741824). <=0 disables.");
        error.WriteLine("  --settings-max-json-depth <n>");
        error.WriteLine("                        Max JSON nesting depth for settings/referenced config (default 1024). <=0 disables.");
        error.WriteLine("  --blf-cache-size <MB> Container cache budget for BLF sources (MiB)");
        error.WriteLine("  --progress <N>        Report progress every N frames");
        error.WriteLine("  --tolerant            Skip malformed frames instead of aborting");
        error.WriteLine("  -h, --help            Show this help message");
        error.WriteLine();
        error.WriteLine("Output format is auto-detected from the output extension (plugins add more; see 'ni plugins list'):");
        error.WriteLine("  .pcapng / other       PCAPNG (default)");
        error.WriteLine("  .blf                  BLF with default compression");
        error.WriteLine("  .asc                  CANalyzer ASCII log (CAN, CAN FD, CAN XL, LIN, FlexRay, Ethernet)");
        error.WriteLine("                        AFDX data frames are read. AFDX status, AFDX bus statistics, ETH STAT, and ETH RxEr are not read.");
        error.WriteLine();
        error.WriteLine("Output format specifications (--output-format):");
        error.WriteLine("  pcapng                PCAPNG format");
        error.WriteLine("  blf                   BLF, default compression");
        error.WriteLine("  blf:compression=off   BLF, no compression");
        error.WriteLine("  blf:compression=fast  BLF, fast compression");
        error.WriteLine("  blf:compression=best  BLF, best compression ratio");
        error.WriteLine("  asc                   CANalyzer ASCII log (CAN, CAN FD, CAN XL, LIN, FlexRay, Ethernet)");
        error.WriteLine("                        AFDX data frames are read. AFDX status, AFDX bus statistics, ETH STAT, and ETH RxEr are not read.");
        error.WriteLine();
        error.WriteLine("Source specifications (plugins add more kinds; see 'ni plugins list'):");
        error.WriteLine("  capture.pcap[ng]      Auto-detected PCAP/PCAPNG");
        error.WriteLine("  data.blf              Auto-detected BLF");
        error.WriteLine("  log.asc               Auto-detected CANalyzer ASC");
        error.WriteLine("  pcap:path=<file>      Explicit PCAP spec; pcapng:path=<file>, blf:path=<file>, asc:path=<file>");
        error.WriteLine("  random:count=N,seed=S,mode=udp4|udp6|random  Synthetic frames");
        error.WriteLine();
        error.WriteLine("Examples:");
        error.WriteLine("  ni convert capture.pcap -o output.pcapng");
        error.WriteLine("  ni convert input.blf -o output.pcapng --progress 1000");
        error.WriteLine("  ni convert input.pcap -o output.blf --output-format blf:compression=best");
        error.WriteLine("  ni convert big.pcapng -o split.pcapng --split-count 10000");
        error.WriteLine("  ni convert big.blf -o out.pcapng --blf-cache-size 256 --split-size 512");
        error.WriteLine("  ni convert input.pcap -o filtered.pcapng --filter \"udp.dstport == 53\"");
        error.WriteLine("  ni convert input.pcap -o output.pcapng --overwrite");
    }

    #endregion
}
