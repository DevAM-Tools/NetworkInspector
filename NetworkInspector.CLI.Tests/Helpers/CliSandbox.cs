// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>What one command run printed and returned.</summary>
/// <param name="Exit">The exit code.</param>
/// <param name="Out">Everything written to the output writer.</param>
/// <param name="Error">Everything written to the error writer.</param>
internal sealed record CliResult(int Exit, string Out, string Error)
{
    /// <summary>The non-empty lines of the output writer.</summary>
    internal string[] OutLines => _Lines(Out);

    /// <summary>The non-empty lines of the error writer.</summary>
    internal string[] ErrorLines => _Lines(Error);

    /// <summary>The lines of the error writer that start with <c>Error:</c> (plugin log lines start with a level word).</summary>
    internal string[] ErrorMessages => [.. ErrorLines.Where(static line => line.StartsWith("Error:", StringComparison.Ordinal))];

    private static string[] _Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A private world for one test: a temporary folder with its own settings base and plugin folders, and a way to run the command
/// line in it with captured writers. Tests run in parallel, so nothing is shared. Not thread-safe; one test uses it.
/// </summary>
internal sealed class CliSandbox : IDisposable
{
    #region Lifecycle

    /// <summary>Creates the folder.</summary>
    internal CliSandbox()
    {
        Root = Directory.CreateTempSubdirectory("ni-cli-").FullName;
        Base = Path.Combine(Root, "settings");
    }

    /// <summary>Deletes the folder with everything in it; failures are ignored because the operating system cleans the temp area.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scanner or an antivirus may hold a file briefly; the leftover folder is harmless.
        }
    }

    #endregion

    #region Properties

    /// <summary>The folder of the test.</summary>
    internal string Root { get; }

    /// <summary>The settings base of the test.</summary>
    internal string Base { get; }

    #endregion

    #region Public API

    /// <summary>Combines the folder of the test with a name.</summary>
    /// <param name="name">File or folder name.</param>
    /// <returns>The full path.</returns>
    internal string Combine(string name) => Path.Combine(Root, name);

    /// <summary>Runs the command line with <c>--settings-path</c> pointing at the settings base of the test.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>What the run printed and returned.</returns>
    internal Task<CliResult> RunAsync(params string[] args) => RunAsync(args, CancellationToken.None);

    /// <summary>Runs the command line with <c>--settings-path</c> and a cancellation token.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run printed and returned.</returns>
    internal async Task<CliResult> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string[] withBase = [.. args, "--settings-path", Base];

        // Relative path arguments resolve against the folder of the test, so no test has to change the process directory.
        return await RunWithoutBaseAsync(withBase, cancellationToken, Root).ConfigureAwait(false);
    }

    /// <summary>Runs the command line exactly as given.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="baseDirectory">The folder for relative path arguments; the process directory when <see langword="null"/>.</param>
    /// <returns>What the run printed and returned.</returns>
    internal static async Task<CliResult> RunWithoutBaseAsync(string[] args, CancellationToken cancellationToken, string? baseDirectory = null)
    {
        // Plugins may log from other threads, so the writers are synchronized.
        StringWriter output = new();
        StringWriter error = new();
        CliIo io = new(TextWriter.Synchronized(output), TextWriter.Synchronized(error));
        if (baseDirectory is not null)
        {
            io = io with { BaseDirectory = baseDirectory };
        }

        int exit = await CliEntry.RunAsync(args, io, cancellationToken).ConfigureAwait(false);
        return new CliResult(exit, output.ToString(), error.ToString());
    }

    /// <summary>Copies a test plugin assembly into a folder of this test and returns the folder.</summary>
    /// <param name="plugin">Project name of the plugin (<c>Sample.Protocol</c> or <c>NetworkInspector.Plugins.Tests.TestPlugins</c>).</param>
    /// <param name="folderName">Name of the folder under the root of the test.</param>
    /// <returns>The full path of the folder.</returns>
    internal string CopyPlugin(string plugin, string folderName)
    {
        string folder = Combine(folderName);
        Directory.CreateDirectory(folder);
        string source = Path.Combine(AppContext.BaseDirectory, "TestPlugins", plugin, plugin + ".dll");
        File.Copy(source, Path.Combine(folder, plugin + ".dll"), overwrite: true);
        return folder;
    }

    /// <summary>Sets <c>plugins.paths</c> to the given full paths through the command line.</summary>
    /// <param name="folders">Plugin files or folders.</param>
    /// <returns>The result of the settings command.</returns>
    internal Task<CliResult> SetPluginPathsAsync(params string[] folders) =>
        RunAsync("settings", "set", "plugins.paths", JsonSerializer.Serialize(folders), "--scope", "application");

    #endregion
}
