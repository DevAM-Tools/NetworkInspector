// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.CLI;

/// <summary>
/// Entry point for the Network Inspector CLI tool.
/// Routes to sub-commands: convert, export.
/// </summary>
internal static class Program
{
    /// <summary>Application entry point.</summary>
    internal static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Use invariant culture for all parsing/formatting on the CLI thread so that
        // command-line numbers and timestamps are interpreted identically regardless of
        // the host machine's regional settings.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToUpperInvariant() switch
            {
                "CONVERT" => ConvertCommand.Run(args[1..]),
                "EXPORT" => ExportCommand.Run(args[1..]),
                "--HELP" or "-H" => PrintUsageAndReturn(0),
                _ => PrintUnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            // User pressed Ctrl+C before a command's own catch could observe it.
            Console.Error.WriteLine("Operation cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            // Top-level safety net: surface the unhandled error rather than crashing
            // with an opaque .NET stack trace. Per README \u00a7Exit Codes, 3 indicates
            // a runtime failure that aborted the run.
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            return 3;
        }
    }

    /// <summary>Prints usage information to stderr.</summary>
    private static void PrintUsage()
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
    private static int PrintUsageAndReturn(int exitCode)
    {
        PrintUsage();
        return exitCode;
    }

    /// <summary>Prints an error message for an unknown command.</summary>
    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'");
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }
}
