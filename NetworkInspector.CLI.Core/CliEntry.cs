// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Entry point for the Network Inspector CLI tool.
/// Routes to sub-commands: convert, export.
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
/// executable forwards to <see cref="Run"/>.
/// </remarks>
internal static class CliEntry
{
    #region Public API

    /// <summary>Application entry point used by the <c>ni</c> host and tests.</summary>
    internal static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Use invariant culture for all parsing/formatting on the CLI thread so that
        // command-line numbers and timestamps are interpreted identically regardless of
        // the host machine's regional settings.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
        {
            _PrintUsage();
            return (int)ExitCode.ArgumentError;
        }

        try
        {
            return args[0].ToUpperInvariant() switch
            {
                "CONVERT" => ConvertCommand.Run(args[1..]),
                "EXPORT" => ExportCommand.Run(args[1..]),
                "--HELP" or "-H" => _PrintUsageAndReturn((int)ExitCode.Success),
                _ => _PrintUnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            // User pressed Ctrl+C before a command's own catch could observe it.
            Console.Error.WriteLine("Operation cancelled.");
            return (int)ExitCode.Success;
        }
        catch (Exception ex)
        {
            // Top-level safety net: surface the unhandled error rather than crashing
            // with an opaque .NET stack trace. Per README §Exit Codes, 3 indicates
            // a runtime failure that aborted the run.
            Console.Error.WriteLine($"Fatal ({ex.GetType().Name}): {ex.Message}");
            Exception? inner = ex.InnerException;
            while (inner is not null)
            {
                Console.Error.WriteLine($"  Caused by ({inner.GetType().Name}): {inner.Message}");
                inner = inner.InnerException;
            }

            return (int)ExitCode.RuntimeError;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Prints usage information to stderr.</summary>
    private static void _PrintUsage()
    {
        Console.Error.WriteLine("Network Inspector CLI");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: ni <command> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Commands:");
        Console.Error.WriteLine("  convert   Convert capture files between formats (frame-level)");
        Console.Error.WriteLine("  export    Parse and export packets to analysis formats");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Use 'ni <command> --help' for more information about a command.");
    }

    /// <summary>Prints usage and returns the given exit code.</summary>
    private static int _PrintUsageAndReturn(int exitCode)
    {
        _PrintUsage();
        return exitCode;
    }

    /// <summary>Prints an error message for an unknown command.</summary>
    private static int _PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'");
        Console.Error.WriteLine();
        _PrintUsage();
        return (int)ExitCode.ArgumentError;
    }

    #endregion
}
