// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The two text writers of a command run: <see cref="Out"/> for results the user asked for (lists, values, JSON) and
/// <see cref="Error"/> for diagnostics (errors, warnings, progress, plugin log lines). Tests inject their own writers, so
/// parallel tests never share the console.
/// </summary>
/// <param name="Out">Receives results.</param>
/// <param name="Error">Receives diagnostics.</param>
internal sealed record CliIo(TextWriter Out, TextWriter Error)
{
    /// <summary>The folder against which relative path arguments are resolved; the process directory unless a test sets another.</summary>
    internal string BaseDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Creates the writers of the real console.</summary>
    /// <returns>An instance over <see cref="Console.Out"/> and <see cref="Console.Error"/>.</returns>
    internal static CliIo FromConsole() => new(Console.Out, Console.Error);
}
