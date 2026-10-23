// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Runs the convert and export commands for tests that only look at the exit code and the files they asked for: the writers are
/// captured (so tests do not print into each other's console) and, unless the test names a settings base itself, a private base
/// folder is used and deleted afterwards (so no test touches the settings of the person who runs the tests). Stateless;
/// thread-safe.
/// </summary>
internal static class CliRun
{
    #region Public API

    /// <summary>Runs <c>ni convert</c> with the given arguments after the command name.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> ConvertAsync(string[] args) => _RunAsync(args, ConvertCommand.RunAsync);

    /// <summary>Runs <c>ni export</c> with the given arguments after the command name.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> ExportAsync(string[] args) => _RunAsync(args, ExportCommand.RunAsync);

    #endregion

    #region Private Helpers

    private static async Task<int> _RunAsync(string[] args, Func<string[], CliIo, CancellationToken, Task<int>> command)
    {
        string? privateBase = args.Contains("--settings-path", StringComparer.OrdinalIgnoreCase)
            ? null
            : Directory.CreateTempSubdirectory("ni-cli-base-").FullName;
        try
        {
            string[] effective = privateBase is null
                ? args
                : [.. args, "--settings-path", privateBase];
            CliIo io = new(TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(new StringWriter()));
            return await command(effective, io, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (privateBase is not null)
            {
                try
                {
                    Directory.Delete(privateBase, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The operating system cleans the temp area; a leftover folder is harmless.
                }
            }
        }
    }

    #endregion
}
