// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Frame-level format conversion command.
/// Reads frames from one or more sources and writes them to a target format
/// (PCAPNG, BLF, ASC, or a plugin's frame format) without protocol parsing. Sources and formats come from the plugin registry.
/// </summary>
internal static class ConvertCommand
{
    #region Types

    /// <summary>The parsed arguments of one run.</summary>
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

    /// <summary>Runs the convert command on the console.</summary>
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
    /// like a result.
    /// </remarks>
    internal static int RunConvertLoop(
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
        out int filesWritten)
    {
        int totalFrames = 0;
        int fileFrames = 0;
        int filterPacketId = 0;
        filesWritten = 0;
        IFrameListener? exporter = null;
        string? currentPath = null;
        IExportByteProgress? byteProgress = null;
        PacketIndex? filterIndex = filter is not null && stack is not null ? new PacketIndex(stack) : null;

        RoundRobinSourceIterator iterator = new(sources);

        try
        {
            while (iterator.HasActive && (maxFrames == 0 || totalFrames < maxFrames))
            {
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
                        error.WriteLine(
                            $"Warning: Skipping frame from {source.UiName}: {ex.Message}");
                        iterator.Advance();
                        continue;
                    }

                    throw;
                }

                if (frame is null)
                {
                    iterator.MarkCurrentExhaustedAndAdvance();
                    continue;
                }

                // Judged before an output is opened, so a filter that matches nothing leaves no
                // empty file behind.
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

                    if (!matched)
                    {
                        iterator.Advance();
                        continue;
                    }
                }

                long estimatedBytes = byteProgress?.EstimatedOutputBytes ?? 0;
                if (exporter is null
                    || (splitManager.IsSplitting
                        && splitManager.NeedsSplit(estimatedBytes, fileFrames)))
                {
                    if (exporter is not null)
                    {
                        exporter.OnFinish();
                        (exporter as IDisposable)?.Dispose();
                        filesWritten++;

                        // Finished: from here on nothing of it is cleaned up, even when the next output cannot be opened.
                        exporter = null;
                        currentPath = null;
                    }

                    string nextPath = splitManager.NextPath();
                    exporter = createExporter(nextPath);
                    currentPath = nextPath;
                    byteProgress = exporter as IExportByteProgress;
                    if (splitManager.IsSizeSplitting && byteProgress is null)
                    {
                        throw new InvalidOperationException(
                            "Size-based splitting (--split-size) requires an exporter that reports " +
                            "IExportByteProgress.EstimatedOutputBytes.");
                    }

                    fileFrames = 0;
                }

                exporter.OnFrame(frame.Value);
                totalFrames++;
                fileFrames++;

                if (progressInterval > 0 && totalFrames % progressInterval == 0)
                {
                    error.WriteLine($"Progress: {totalFrames} frames written");
                }

                if (maxFrames > 0 && totalFrames >= maxFrames)
                {
                    break;
                }

                iterator.Advance();
            }

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
            // Cancelled or failed: the output was not finished, so it is disposed and deleted instead of finalized.
            CliCommandSupport.DiscardUnfinished(ref exporter, ref currentPath, splitManager.IsDirectoryOutput, error);
            throw;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Parses arguments and executes conversion; throws <see cref="ArgumentException"/> on bad args.</summary>
    private static async Task<int> _RunCoreAsync(string[] args, CliIo io, CancellationToken cancellationToken)
    {
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

    /// <summary>Executes the conversion pipeline inside a plugin session.</summary>
    private static Task<int> _ExecuteAsync(ConvertArguments arguments, CliIo io, CancellationToken cancellationToken) =>
        CliCommandSupport.RunCancellableAsync(io, async token =>
        {
            (CliPluginSession? opened, int openExit) = await CliPluginSession.OpenAsync(arguments.Settings, io, token)
                .ConfigureAwait(false);
            if (opened is null)
            {
                return openExit;
            }

            await using (opened.ConfigureAwait(false))
            {
                return _Run(opened, arguments, io, token);
            }
        }, cancellationToken);

    /// <summary>Resolves the specs, builds the stack, opens the sources, and runs the loop.</summary>
    private static int _Run(CliPluginSession session, ConvertArguments arguments, CliIo io, CancellationToken token)
    {
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

        // An existing output is refused (or accepted with --overwrite) before a long run starts; nothing is deleted yet.
        SplitOutputManager firstPathManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount);
        if (!ExportTargets.TryCheck(firstPathManager.NextPath(), isDirectory: false, arguments.Overwrite, out _, out PluginError? targetError))
        {
            io.Error.WriteLine("Error: " + CliCommandSupport.TargetText(targetError, arguments.OutputPath));
            return (int)ExitCode.ArgumentError;
        }

        // An exporter request carries the stack, so conversion always builds one (a few milliseconds); frames are parsed only
        // when a filter is set.
        FrameInterfaceRegistry frames = new();
        if (!session.TryBuildStack(frames, out Stack? stack, out int stackExit))
        {
            return stackExit;
        }

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

    /// <summary>Runs the convert loop and prints the summary.</summary>
    private static int _RunLoop(ConvertArguments arguments, FrameFormatConfig format, List<IFrameSource> sources, Stack stack,
        IFilter? filter, CliIo io, CancellationToken token)
    {
        // Old outputs that --overwrite moved aside; they are settled in the finally block below, whatever the run did.
        List<TargetReplacement> replacements = [];
        try
        {
            SplitOutputManager splitManager = new(arguments.OutputPath, arguments.SplitSizeBytes, arguments.SplitCount);
            int totalFrames = RunConvertLoop(
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
                out int filesWritten);

            io.Error.WriteLine($"Conversion complete: {totalFrames} frames written to {filesWritten} file(s).");
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
