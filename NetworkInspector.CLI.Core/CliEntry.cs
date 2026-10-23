// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Entry point for the Network Inspector CLI tool.
/// Routes to sub-commands: convert, export, plugins, settings, profile.
/// </summary>
/// <remarks>
/// Exit codes:
/// <list type="table">
///   <listheader><term>Code</term><description>Meaning</description></listheader>
///   <item><term>0</term><description>Success.</description></item>
///   <item><term>1</term><description>Bad arguments, missing options, or validation failure.</description></item>
///   <item><term>2</term><description>Failed to open one or more source files.</description></item>
///   <item><term>3</term><description>Runtime failure during processing (IO, serialisation, etc.).</description></item>
/// </list>
/// Lives in <c>NetworkInspector.CLI.Core</c> so ExitPointGaps can gate coverage; the <c>ni</c>
/// executable forwards to <see cref="RunAsync(string[])"/>.
/// </remarks>
internal static class CliEntry
{
    #region Public API

    /// <summary>Application entry point used by the <c>ni</c> host; the writers are the console.</summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> RunAsync(string[] args) => RunAsync(args, CliIo.FromConsole(), CancellationToken.None);

    /// <summary>Entry point used by tests: the writers are injected, so parallel tests do not share the console.</summary>
    /// <param name="args">The command line arguments.</param>
    /// <param name="io">Output and error writers.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string[] args, CliIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);

        // UTF-8 once per process start (a legacy Windows code page would mangle names in the output).
        Console.OutputEncoding = Encoding.UTF8;

        // Use invariant culture for all parsing/formatting on the CLI thread so that command-line numbers and timestamps are
        // interpreted identically regardless of the host machine's regional settings.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
        {
            _PrintUsage(io.Error);
            return (int)ExitCode.ArgumentError;
        }

        try
        {
            return args[0].ToUpperInvariant() switch
            {
                "CONVERT" => await ConvertCommand.RunAsync(args[1..], io, cancellationToken).ConfigureAwait(false),
                "EXPORT" => await ExportCommand.RunAsync(args[1..], io, cancellationToken).ConfigureAwait(false),
                "PLUGINS" => await PluginsCommand.RunAsync(args[1..], io, cancellationToken).ConfigureAwait(false),
                "SETTINGS" => await SettingsCommand.RunAsync(args[1..], io, cancellationToken).ConfigureAwait(false),
                "PROFILE" => ProfileCommand.Run(args[1..], io),
                "--HELP" or "-H" => _PrintUsageAndReturn(io.Error, (int)ExitCode.Success),
                _ => _PrintUnknownCommand(io.Error, args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            // User pressed Ctrl+C before a command's own catch could observe it.
            io.Error.WriteLine("Operation cancelled.");
            return (int)ExitCode.Success;
        }
        catch (Exception ex)
        {
            // Top-level safety net: surface the unhandled error rather than crashing
            // with an opaque .NET stack trace. Per README §Exit Codes, 3 indicates
            // a runtime failure that aborted the run.
            io.Error.WriteLine($"Fatal ({ex.GetType().Name}): {ex.Message}");
            Exception? inner = ex.InnerException;
            while (inner is not null)
            {
                io.Error.WriteLine($"  Caused by ({inner.GetType().Name}): {inner.Message}");
                inner = inner.InnerException;
            }

            return (int)ExitCode.RuntimeError;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Prints usage information to the error writer.</summary>
    private static void _PrintUsage(TextWriter error)
    {
        error.WriteLine("Network Inspector CLI");
        error.WriteLine();
        error.WriteLine("Usage: ni <command> [options]");
        error.WriteLine();
        error.WriteLine("Commands:");
        error.WriteLine("  convert   Convert capture files between formats (frame-level)");
        error.WriteLine("  export    Parse and export packets to analysis formats");
        error.WriteLine("  plugins   List the plugins of this run");
        error.WriteLine("  settings  List, read, change, and reset application and profile settings");
        error.WriteLine("  profile   List, create, rename, and delete settings profiles");
        error.WriteLine();
        error.WriteLine("Use 'ni <command> --help' for more information about a command.");
    }

    /// <summary>Prints usage and returns the given exit code.</summary>
    private static int _PrintUsageAndReturn(TextWriter error, int exitCode)
    {
        _PrintUsage(error);
        return exitCode;
    }

    /// <summary>Prints an error message for an unknown command.</summary>
    private static int _PrintUnknownCommand(TextWriter error, string command)
    {
        error.WriteLine($"Unknown command: '{command}'");
        error.WriteLine();
        _PrintUsage(error);
        return (int)ExitCode.ArgumentError;
    }

    #endregion
}
