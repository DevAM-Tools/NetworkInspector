// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The two text writers of a command run: <see cref="Out"/> for results the user asked for (lists, values, JSON) and
/// <see cref="Error"/> for diagnostics (errors, warnings, progress, plugin log lines). Tests inject their own writers, so
/// parallel tests never share the console. The record is immutable; the writers are not thread-safe, so code that can write from
/// several threads (the plugin log) locks <see cref="Error"/> itself before it writes.
/// </summary>
/// <param name="Out">Receives results.</param>
/// <param name="Error">Receives diagnostics.</param>
internal sealed record CliIo(TextWriter Out, TextWriter Error)
{
    /// <summary>
    /// The folder against which relative path arguments that are stored are resolved (<c>ni settings set plugins.paths</c> stores
    /// full paths); the process directory at creation unless a test sets another. Output paths are not affected: they resolve
    /// against the current directory like any file argument.
    /// </summary>
    internal string BaseDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Creates the writers of the real console.</summary>
    /// <returns>An instance over <see cref="Console.Out"/> and <see cref="Console.Error"/>.</returns>
    internal static CliIo FromConsole() => new(Console.Out, Console.Error);
}
