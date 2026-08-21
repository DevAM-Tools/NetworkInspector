// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// Thin host for the <c>ni</c> global tool. Application logic lives in
/// <see cref="CliEntry"/> (<c>NetworkInspector.CLI.Core</c>) so ExitPointGaps can gate coverage.
/// </summary>
internal static class Program
{
    /// <summary>Application entry point.</summary>
    internal static int Main(string[] args) => CliEntry.Run(args);
}
