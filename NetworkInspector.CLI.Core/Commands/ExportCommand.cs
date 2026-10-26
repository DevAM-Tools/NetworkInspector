// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parse and export command.
/// Reads frames from sources, parses them into packets with the full protocol stack, and exports to an analysis format
/// (CSV, JSON, PBF, text, Parquet, DuckDB, or a plugin's format). Sources and formats come from the plugin registry.
/// Unlike <see cref="ConvertCommand"/> every frame is parsed, because the exporters write packets. Stateless; each run owns its
/// sources, listener, and plugin session, and its packet loop is single-threaded.
/// </summary>
internal static class ExportCommand
{
    #region Types

    /// <summary>
    /// The parsed arguments of one run. Units: <c>MaxPackets</c> and <c>SplitCount</c> count packets and <c>ProgressInterval</c>
    /// is a packet interval (zero disables each); <c>SplitSizeBytes</c> and <c>BlfCacheBudgetBytes</c> are bytes, already
    /// converted from the MiB the user typed (zero disables the split / leaves the BLF default).
    /// </summary>
    private sealed record ExportArguments(
        List<string> SourceSpecs,
        string OutputPath,
        string? FormatSpec,
        string? FilterExpression,
        CliSettingsOptions Settings,
        int MaxPackets,
        long SplitSizeBytes,
        int SplitCount,
        int ProgressInterval,
        int BlfCacheBudgetBytes,
        bool Tolerant,
        bool Overwrite);

    #endregion

    #region Public API

    /// <summary>Runs the export command on the console.</summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsync(string[] args) => RunAsync(args, CliIo.FromConsole(), CancellationToken.None);

    /// <summary>Runs the export command with injected writers.</summary>
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
    /// Drives the frame-read → parse → export loop across all active sources,
    /// rotating outputs when <paramref name="splitManager"/> requests a split.
    /// </summary>
    /// <remarks>
    /// Performance: the loop parses every frame into one reused <see cref="Packet"/> (a recycle), which keeps the field storage
    /// and the packet object out of the allocator for the whole run; a new packet is created only when a recycle is refused.
    /// The assumption is that a listener is finished with the packet when <c>OnPacket</c> returns (it must copy what it keeps),
    /// because the next iteration overwrites it in place. A filter is judged on the parsed packet before any output is opened,
    /// so a filter that matches nothing leaves no file behind. Thread safety: single-threaded; the sources, the filter, the
    /// recycled packet, and the listeners are driven from the calling thread only.
    /// </remarks>
    /// <param name="sources">Active, already-started frame sources in round-robin order.</param>
    /// <param name="createExporter">Factory that opens a new file/directory exporter for a path.</param>
    /// <param name="splitManager">Split policy (size and/or packet count); may disable splitting.</param>
    /// <param name="stack">Protocol stack that owns parsed packets.</param>
    /// <param name="filter">
    /// Optional packet filter. When set, packets it rejects consume a packet id (every parsed frame does) but are never handed
    /// to the exporter and do not count towards <paramref name="maxPackets"/>; an evaluation failure aborts the loop with an
    /// <see cref="InvalidOperationException"/> because a filter that cannot decide must not
    /// silently keep or drop data.
    /// </param>
    /// <param name="maxPackets">Stop after this many packets were handed to the exporter (0 = unlimited).</param>
    /// <param name="progressInterval">Print progress every N exported packets to the error writer (0 = silent).</param>
    /// <param name="tolerant">
    /// When <see langword="true"/>, an exception from reading a frame is reported as a warning and the loop moves on to the next
    /// source. A source that fails again before it delivered a frame is dropped with a warning that names it, so a source that
    /// fails on every read cannot keep the loop running forever. It does not cover parse or filter errors, and a failing exporter
    /// always aborts.
    /// </param>
    /// <param name="error">Receives progress lines and warnings (the error writer of the command).</param>
    /// <param name="packetIdCounter">
    /// Monotonic packet-id allocator. Pass 0 for a fresh loop; each frame consumes the current
    /// value and then increments, so first-parse ids are dense <c>0, 1, 2, …</c>. Ids must be dense and in order because the
    /// stack accepts a first parse of an id only when it does not jump past the next expected one.
    /// </param>
    /// <param name="cancellationToken">
    /// Cooperative cancellation. A cancelled loop stops, discards the output that was still open (it is not finished, so it must
    /// not stay behind), and returns normally; the returned count still includes the packets of the discarded output. A source
    /// that observes the token may instead throw <see cref="OperationCanceledException"/>, which discards the output the same way.
    /// </param>
    /// <param name="outputsWritten">Number of output files or dataset directories finalized.</param>
    /// <param name="listenerStopped">
    /// <see langword="true"/> when the loop ended early because a listener returned <see langword="false"/> from <c>OnPacket</c>
    /// (it unsubscribed itself, for example after an error); the open output was finished normally, but the input was not
    /// exported completely. A listener that stops because of a cancellation is not reported here.
    /// </param>
    /// <returns>
    /// Number of packets handed to the exporter. The loop also ends early, without an exception, when a listener returns
    /// <see langword="false"/> from <c>OnPacket</c> (it unsubscribed itself); <paramref name="listenerStopped"/> tells the caller,
    /// which reports it as a failure.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A parse or the filter failed, size splitting is requested for an exporter without byte progress, or an output could not be
    /// prepared; the unfinished output was discarded.
    /// </exception>
    /// <exception cref="CliUsageException"><paramref name="createExporter"/> found an invalid option text; the unfinished output was discarded.</exception>
    internal static int RunExportLoop(
        List<IFrameSource> sources,
        Func<string, IPacketListener> createExporter,
        SplitOutputManager splitManager,
        Stack stack,
        IFilter? filter,
        int maxPackets,
        int progressInterval,
        bool tolerant,
        TextWriter error,
        ref int packetIdCounter,
        CancellationToken cancellationToken,
        out int outputsWritten,
        out bool listenerStopped)
    {
        // Loop state. exporter and currentPath describe the output that is open right now (and only that one): they are cleared
        // as soon as an output is finished, so the cleanup in the catch block can never delete a finished output.
        // recyclePacket is the one packet object that every frame is parsed into (see the remarks).
        int packetCount = 0;
        int filePackets = 0;
        outputsWritten = 0;
        listenerStopped = false;
        Packet? recyclePacket = null;
        IPacketListener? exporter = null;
        string? currentPath = null;
        IExportByteProgress? byteProgress = null;

        // The index exists only for a filter. One index serves the whole run: the filter reads protocol presence from it in O(1)
        // instead of walking the field tree of each packet, and the parse fills it. Without a filter no index is kept.
        PacketIndex? filterIndex = filter is not null ? new PacketIndex(stack) : null;

        RoundRobinSourceIterator iterator = new(sources);

        try
        {
            // The cancellation token is checked here and not only inside the sources: a source that ignores the token must not
            // be able to keep the loop running after Ctrl+C.
            while (iterator.HasActive
                && !cancellationToken.IsCancellationRequested
                && (maxPackets == 0 || packetCount < maxPackets))
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

                // Parse the frame. The id is taken (and the counter advanced) before the capacity check, so the id of a frame is
                // never reused; the check runs before the id is used and names the packets in its error. A recycle reuses the
                // storage of the previous packet in place (no allocation per packet); when the stack refuses the recycle (for
                // example a materializer still holds the packet) the same frame is parsed into a new packet, which then becomes
                // the next recycle target. A parse that fails in both ways aborts the run.
                int idValue = packetIdCounter++;
                ArrayIndexIdRange.ThrowIfInvalidNextIndex(idValue, "packet");

                PacketId pid = new(idValue);
                ParseOptions exportOptions = new(index: filterIndex);
                Packet packet;
                if (recyclePacket is not null
                    && Packet.TryParse(recyclePacket, pid, stack, frame.Value, in exportOptions, out _))
                {
                    packet = recyclePacket;
                }
                else if (!Packet.TryParse(pid, stack, frame.Value, in exportOptions, out Packet? created, out ParseFailure exportFailure))
                {
                    throw new InvalidOperationException(exportFailure.ToString());
                }
                else
                {
                    packet = created!;
                }

                recyclePacket = packet;

                // Judged before an output is opened, so a filter that matches nothing leaves no
                // empty file or dataset directory behind. A rejected packet still consumes the source's turn, so a selective
                // filter does not let one source starve the others.
                if (filter is not null)
                {
                    if (!CliFilter.TryMatch(filter, packet, filterIndex, error, out bool matched))
                    {
                        throw new InvalidOperationException(
                            "Filter evaluation failed; the export was aborted to avoid writing a partially filtered output.");
                    }

                    if (!matched)
                    {
                        iterator.Advance();
                        continue;
                    }
                }

                // Open the first output, or rotate to the next one when the split policy asks for it. The size comes from an
                // in-memory counter of the exporter (no file-system probe), which is cheap enough to read for every packet.
                long estimatedBytes = byteProgress?.EstimatedOutputBytes ?? 0;
                if (exporter is null
                    || (splitManager.IsSplitting
                        && splitManager.NeedsSplit(estimatedBytes, filePackets)))
                {
                    // Finish the previous output first: OnFinish completes it, Dispose releases the file handle.
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        outputsWritten++;

                        // Finished: from here on nothing of it is cleaned up, even when the next output cannot be opened.
                        exporter = null;
                        currentPath = null;
                    }

                    // currentPath is set only after the factory returned, so a factory that fails leaves nothing to discard here
                    // (the factory used by the command removes what its own attempt left behind).
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

                    filePackets = 0;
                }

                // Hand the packet over. false means the listener unsubscribed itself (for example an exporter in strict mode after
                // its first recoverable error): the loop ends after this packet and the output is finished normally below. The
                // caller learns of it through listenerStopped and reports a failure, because the input was not exported completely
                // (a cancellation also makes exporters return false; that case is the cancelled run, not a failure).
                bool continueExport = exporter.OnPacket(packet);
                packetCount++;
                filePackets++;

                if (progressInterval > 0 && packetCount % progressInterval == 0)
                {
                    error.WriteLine($"Progress: {packetCount} packets exported");
                }

                iterator.Advance();

                if (!continueExport)
                {
                    listenerStopped = !cancellationToken.IsCancellationRequested;
                    break;
                }

                if (maxPackets > 0 && packetCount >= maxPackets)
                {
                    break;
                }
            }

            // A cancelled export is unfinished: the open output goes away instead of looking like a complete result. The loop
            // condition above ended the run on the cancelled token, so this is a return, not an exception.
            if (cancellationToken.IsCancellationRequested && exporter is not null)
            {
                CliCommandSupport.DiscardUnfinished(ref exporter, ref currentPath, splitManager.IsDirectoryOutput, error);
                return packetCount;
            }

            // Normal end (sources exhausted, limit reached, or the listener unsubscribed): finish the last output. This is the
            // only path that finalizes the output that is still open; every other exit discards it.
            if (exporter is not null)
            {
                exporter.OnFinish();
                (exporter as IDisposable)?.Dispose();
                outputsWritten++;
                exporter = null;
                currentPath = null;
            }

            return packetCount;
        }
        catch
        {
            // An error ends the export without OnFinish: dispose the listener, then delete what it left behind.
            CliCommandSupport.DiscardUnfinished(ref exporter, ref currentPath, splitManager.IsDirectoryOutput, error);
            throw;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Parses arguments and executes export; throws <see cref="ArgumentException"/> on bad args (the caller maps it to exit
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
        string? formatSpec = null;
        string? filterExpression = null;
        CliSettingsOptionsBuilder settings = new();
        int maxPackets = 0;
        long splitSize = 0;          // MiB
        int splitCount = 0;         // packets
        int progressInterval = 0;
        long blfCacheSize = 0;  // MiB
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
                case "-F" or "--FORMAT":
                    formatSpec = CliArgumentParsing.GetNextArg(args, ref i, "--format");
                    break;
                case "-N" or "--MAX-PACKETS":
                    maxPackets = CliArgumentParsing.ParseNonNegativeInt(
                        CliArgumentParsing.GetNextArg(args, ref i, "--max-packets"),
                        "--max-packets");
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
                case "--FILTER":
                    filterExpression = CliArgumentParsing.GetNextArg(args, ref i, "--filter");
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
        // path (file or dataset folder) that the run prepares, finishes, and, when the run does not complete, deletes or restores.
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
            new ExportArguments(sourceSpecs, outputPath, formatSpec, filterExpression, settings.Build(), maxPackets,
                splitSizeBytes, splitCount, progressInterval, blfCacheBudgetBytes, tolerant, overwrite),
            io,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the export pipeline inside a plugin session. The session (plugins started, settings loaded) is open for the whole
    /// run and disposed after it, so the plugins are stopped even when the run fails or is cancelled; Ctrl+C cancels the token
    /// for both the plugin start and the packet loop.
    /// </summary>
    /// <param name="arguments">The parsed arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">The outer token; linked with Ctrl+C.</param>
    /// <returns>The exit code.</returns>
    private static Task<int> _ExecuteAsync(ExportArguments arguments, CliIo io, CancellationToken cancellationToken) =>
        CliCommandSupport.RunCancellableAsync(io, async token =>
        {
            // A session that cannot open has already printed its error line; its exit code ends the command.
            (CliPluginSession? opened, int openExit) = await CliPluginSession.OpenAsync(arguments.Settings, io, token)
                .ConfigureAwait(false);
            if (opened is null)
            {
                return openExit;
            }

            // The export itself is synchronous (sources and listeners are blocking APIs); only the session open and close are
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
    private static int _Run(CliPluginSession session, ExportArguments arguments, CliIo io, CancellationToken token)
    {
        // Specs resolve through the registry, so plugin kinds and formats work exactly like built-in ones.
        PacketFormatConfig format;
        List<SourceConfig> configs;
        try
        {
            format = arguments.FormatSpec is { Length: > 0 } formatSpec
                ? PacketFormatConfig.Parse(formatSpec, session.Registry)
                : PacketFormatConfig.FromExtension(Path.GetExtension(arguments.OutputPath), session.Registry);
            configs = arguments.SourceSpecs.ConvertAll(spec => CliCommandSupport.WithBlfCache(SourceConfig.Parse(spec, session.Registry),
                arguments.BlfCacheBudgetBytes));
        }
        catch (ArgumentException ex)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return (int)ExitCode.ArgumentError;
        }

        // Unknown or mistyped option texts are refused before any work; the registration judges the rest when it opens.
        if (!_OptionTextsAreValid(format, io))
        {
            return (int)ExitCode.ArgumentError;
        }

        // An existing output is refused (or accepted with --overwrite) before a long run starts; nothing is deleted yet. The
        // check uses a manager of its own, so the manager of the run still starts at its first path. Only the first path is
        // checked here; a later split path that exists is refused when its output is opened.
        SplitOutputManager firstPathManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount,
            format.IsDirectoryOutput);
        // The refusal names the path that was checked. In a split run that is the first split file (base_00001.ext), not the base
        // name the user typed, which is never written: naming the typed path would point at a file that does not exist.
        string firstPath = firstPathManager.NextPath();
        if (!ExportTargets.TryCheck(firstPath, format.IsDirectoryOutput, arguments.Overwrite, out _, out PluginError? targetError))
        {
            io.Error.WriteLine("Error: " + CliCommandSupport.TargetText(targetError, firstPath));
            return (int)ExitCode.ArgumentError;
        }

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
    /// Runs the export loop and prints the summary on the error writer (the export has no result for standard output). The
    /// exceptions of the loop become exit codes here: a cancellation is a success (0) because it is the user's decision, a
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
    private static int _RunLoop(ExportArguments arguments, PacketFormatConfig format, List<IFrameSource> sources, Stack stack, IFilter? filter,
        CliIo io, CancellationToken token)
    {
        // Old outputs that --overwrite moved aside; they are settled in the finally block below, whatever the run did.
        List<TargetReplacement> replacements = [];
        try
        {
            SplitOutputManager splitManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount,
                format.IsDirectoryOutput);
            int packetIdCounter = 0;
            int packetCount = RunExportLoop(
                sources,
                path => _CreateListener(format, path, stack, arguments.Overwrite, replacements, token),
                splitManager,
                stack,
                filter,
                arguments.MaxPackets,
                arguments.ProgressInterval,
                arguments.Tolerant,
                io.Error,
                ref packetIdCounter,
                token,
                out int outputsWritten,
                out bool listenerStopped);

            // The loop returns normally on a cancelled token (it has already discarded the open output), so the token is
            // checked here to report the cancellation instead of a summary.
            if (token.IsCancellationRequested)
            {
                io.Error.WriteLine("Export cancelled.");
                return (int)ExitCode.Success;
            }

            // The loop ended early because the exporter stopped taking packets: that is a failure, not a complete export, and a
            // script must not read "Export complete" and exit code 0 for an output that lacks the rest of the input.
            if (listenerStopped)
            {
                io.Error.WriteLine(Invariant(
                    $"Error during export: the exporter stopped after {packetCount} packets, before the input was exported completely."));
                return (int)ExitCode.RuntimeError;
            }

            string outputUnit = format.IsDirectoryOutput ? "dataset(s)" : "file(s)";
            io.Error.WriteLine($"Export complete: {packetCount} packets written to {outputsWritten} {outputUnit}.");
            return (int)ExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            io.Error.WriteLine("Export cancelled.");
            return (int)ExitCode.Success;
        }
        catch (CliUsageException ex)
        {
            io.Error.WriteLine("Error: " + ex.Message);
            return (int)ExitCode.ArgumentError;
        }
        catch (Exception ex)
        {
            io.Error.WriteLine($"Error during export: {ex.Message}");
            return (int)ExitCode.RuntimeError;
        }
        finally
        {
            // A new output that survived the run replaced the old one for good; a missing one (cancelled, failed, never
            // opened) gets the old one back.
            CliCommandSupport.SettleReplacements(replacements, io.Error);
        }
    }

    /// <summary>Checks the option texts of the format against its option settings, without creating anything.</summary>
    /// <param name="format">The output format with its option texts.</param>
    /// <param name="io">Receives the error line.</param>
    /// <returns><see langword="true"/> when every text is accepted.</returns>
    private static bool _OptionTextsAreValid(PacketFormatConfig format, CliIo io)
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(format.Registration.RegisterOptions);
        if (RegistrationOptions.TryApplyTexts(options, format.OptionTexts, out PluginError? error))
        {
            return true;
        }

        io.Error.WriteLine("Error: " + error.Value.Message);
        return false;
    }

    /// <summary>
    /// Prepares the target (an existing output is replaced only with <c>--overwrite</c>: it is moved aside and added to
    /// <paramref name="replacements"/>) and creates the listener. Problems of the user's input become a
    /// <see cref="CliUsageException"/>; the rest are errors of the run. A listener that cannot be created gives the old output
    /// back after the run.
    /// </summary>
    /// <param name="format">The output format.</param>
    /// <param name="path">The path of the output (a split path when splitting).</param>
    /// <param name="stack">The stack that parsed the packets.</param>
    /// <param name="overwrite">Whether an existing output may be replaced.</param>
    /// <param name="replacements">Collects the old outputs that were moved aside; settled after the run.</param>
    /// <param name="token">Cancels the export.</param>
    /// <returns>The listener.</returns>
    /// <exception cref="InvalidOperationException">The target cannot be used or the listener cannot be created.</exception>
    /// <exception cref="CliUsageException">An option text of the format is invalid.</exception>
    private static IPacketListener _CreateListener(PacketFormatConfig format, string path, Stack stack, bool overwrite,
        List<TargetReplacement> replacements, CancellationToken token)
    {
        if (!ExportTargets.TryPrepare(path, format.IsDirectoryOutput, overwrite, out string? fullPath, out TargetReplacement? replacement,
            out PluginError? targetError))
        {
            throw new InvalidOperationException(CliCommandSupport.TargetText(targetError, path));
        }

        if (replacement is not null)
        {
            replacements.Add(replacement);
        }

        if (!format.TryCreate(fullPath, stack, token, out IPacketListener? listener, out PluginError? error))
        {
            // Nothing was written: the leftover is removed, and the settling after the run gives the old output back.
            string message = CliCommandSupport.AbandonTarget(fullPath, format.IsDirectoryOutput, error.Value.Message);
            throw error.Value.Code == PluginErrorCode.InvalidOption
                ? new CliUsageException(message)
                : new InvalidOperationException(message);
        }

        // A dataset folder is marked, so that a later --overwrite may replace it and nothing else. The mark comes right after the
        // listener exists and before the first packet; when it cannot be written the listener is disposed and the folder removed,
        // so no unmarked half-dataset stays behind.
        if (format.IsDirectoryOutput)
        {
            string? markProblem = ExportTargets.MarkDataset(fullPath);
            if (markProblem is not null)
            {
                (listener as IDisposable)?.Dispose();
                throw new InvalidOperationException(CliCommandSupport.AbandonTarget(fullPath, isDirectory: true, markProblem));
            }
        }

        return listener;
    }

    /// <summary>Prints usage information for the export command.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Usage: ni export <sources...> -o <output> [options]");
        error.WriteLine();
        error.WriteLine("Parse and export packets to analysis formats.");
        error.WriteLine();
        error.WriteLine("Arguments:");
        error.WriteLine("  <sources...>          One or more source files or specs");
        error.WriteLine();
        error.WriteLine("Options:");
        error.WriteLine("  -o, --output          Output file path (required)");
        error.WriteLine("  -f, --format          Export format spec (see below; overrides extension)");
        error.WriteLine("  --overwrite           Replace an existing output (otherwise an existing output is an error)");
        error.WriteLine("  -n, --max-packets     Maximum number of packets to export");
        error.WriteLine("  --split-size <MB>     Split when exporter EstimatedOutputBytes reaches this size (MiB; live, no FS probe)");
        error.WriteLine("  --split-count <N>     Split output every N packets (files, or sibling Parquet directories)");
        CliFilter.PrintUsageLines(error);
        error.WriteLine("  --profile <name>      Settings profile (must exist; 'default' is created on first use)");
        error.WriteLine("  --settings-path <dir> Base directory for settings storage");
        error.WriteLine("  --settings-max-config-file-bytes <bytes>");
        error.WriteLine("                        Max settings/referenced config JSON size in bytes (default 1073741824). <=0 disables.");
        error.WriteLine("  --settings-max-json-depth <n>");
        error.WriteLine("                        Max JSON nesting depth for settings/referenced config (default 1024). <=0 disables.");
        error.WriteLine("  --blf-cache-size <MB> Container cache budget for BLF sources (MiB)");
        error.WriteLine("  --progress <N>        Report progress every N packets");
        error.WriteLine("  --tolerant            Skip malformed frames instead of aborting");
        error.WriteLine("  -h, --help            Show this help message");
        error.WriteLine();
        error.WriteLine("Source specifications (plugins add more kinds; see 'ni plugins list'):");
        error.WriteLine("  capture.pcap[ng]      Auto-detected PCAP/PCAPNG file");
        error.WriteLine("  data.blf              Auto-detected BLF file");
        error.WriteLine("  log.asc               Auto-detected CANalyzer ASC file");
        error.WriteLine("  pcap:path=<file>      Explicit PCAP spec; pcapng:path=<file>, blf:path=<file>, asc:path=<file>");
        error.WriteLine("  random:count=N,seed=S,mode=udp4|udp6|tcp4|tcp6|tcpstream4|tcpstream6|ethernet|ipv4|ipv6|can|canfd|fullrandom");
        error.WriteLine("                        Synthetic frames");
        error.WriteLine("  <kind>:key=value,...  Any source kind; options are Core settings (see the documentation of the kind)");
        error.WriteLine();
        error.WriteLine("Format specifications (plugins add more formats; see 'ni plugins list'):");
        error.WriteLine("  csv                   Comma-separated values (default for .csv); No., Time, Info, Length");
        error.WriteLine("  csv:columns=ip.src;udp.dstport,delimiter=;,header=false,bom=false");
        error.WriteLine("                        Field columns, delimiter, header row, and byte order mark");
        error.WriteLine("  csv:neutralizeFormulas=false");
        error.WriteLine("                        Write text cells unchanged (default: a text cell that starts with = + - @ tab or CR gets a");
        error.WriteLine("                        leading ' so that a spreadsheet does not run it as a formula)");
        error.WriteLine("  json                  Compact JSON (default for .json)");
        error.WriteLine("  json:style=compact    Compact JSON");
        error.WriteLine("  json:style=pretty     Pretty-printed JSON");
        error.WriteLine("  json:style=array      JSON array format");
        error.WriteLine("  pbf                   Standard PBF");
        error.WriteLine("  pbf:format=standard   Standard (row-oriented) PBF");
        error.WriteLine("  pbf:format=columnar   Columnar PBF");
        error.WriteLine("  pbf:format=columnar,compressed  Columnar PBF with LZ4");
        error.WriteLine("  text                  Human-readable protocol tree (default for .txt)");
        error.WriteLine("  text:level=summary    Protocol containers only (no field values)");
        error.WriteLine("  text:level=standard   All fields except raw bytes (default)");
        error.WriteLine("  text:level=full       All fields including raw bytes");
        error.WriteLine("  text:truncate=N       Truncate values at N characters (0 = unlimited, default: 256)");
        error.WriteLine("  text:level=full,truncate=0  Full detail without any truncation");
        error.WriteLine("  parquet               Columnar Parquet directory (requires -o <dir>)");
        error.WriteLine("  duckdb                DuckDB database file (requires -o <file>.duckdb)");
        error.WriteLine();
        error.WriteLine("Examples:");
        error.WriteLine("  ni export capture.pcap -o output.json -f json:style=pretty");
        error.WriteLine("  ni export input.pcap -o data.pbf -f pbf:format=columnar,compressed");
        error.WriteLine("  ni export input.pcap -o output.json -n 100 --progress 10");
        error.WriteLine("  ni export random:count=1000,mode=udp4 -o out.json -f json");
        error.WriteLine("  ni export capture.pcap -o out.txt -f text");
        error.WriteLine("  ni export capture.pcap -o out.csv -f csv:columns=ip.src;udp.dstport");
        error.WriteLine("  ni export capture.pcap -o split.json --split-count 10000");
        error.WriteLine("  ni export capture.pcap -o out_parquet -f parquet");
        error.WriteLine("  ni export capture.pcap -o out_parquet -f parquet --split-count 50000");
        error.WriteLine("  ni export capture.pcap -o out.duckdb -f duckdb");
        error.WriteLine("  ni export capture.pcap -o out.duckdb -f duckdb --split-count 50000");
        error.WriteLine("  ni export capture.pcap -o dns.json --filter \"udp.dstport == 53\"");
        error.WriteLine("  ni export capture.pcap -o out.json --overwrite");
    }

    #endregion
}
