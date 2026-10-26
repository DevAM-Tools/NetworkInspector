// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Thin host for the <c>ni</c> global tool. Application logic lives in
/// <see cref="CliEntry"/> (<c>NetworkInspector.CLI.Core</c>) so ExitPointGaps can gate coverage. Keep it free of logic: anything
/// added here is outside the coverage gate and outside the injected-writer tests.
/// </summary>
internal static class Program
{
    /// <summary>Application entry point; the returned value is the process exit code (see <see cref="ExitCode"/>).</summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The exit code.</returns>
    internal static Task<int> Main(string[] args) => CliEntry.RunAsync(args);
}
