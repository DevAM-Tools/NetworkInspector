// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="CliEntry.Run"/>.
/// <para>
/// Only short-circuit paths that return immediately (no I/O or network activity) are tested
/// here. Paths that require open files or running sources are left to integration testing.
/// </para>
/// <para>
/// Exit-code contract (from NetworkInspector.CLI/README.md):
/// <list type="table">
/// <item><term>0</term><description>Success</description></item>
/// <item><term>1</term><description>Usage/validation error</description></item>
/// <item><term>2</term><description>I/O or resource error</description></item>
/// <item><term>3</term><description>Runtime failure</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class ProgramTests
{
    // === Main — exit-code contract ===

    /// <summary>
    /// Data source for <see cref="Main_ExitCode_MatchesExpected"/>.
    /// Covers: no args, --help/-h (case-insensitive), unknown command,
    /// sub-command --help paths, and sub-command with no arguments.
    /// </summary>
    public static IEnumerable<Func<(string[] Args, int Expected, string Because)>> Main_ExitCode_Data()
    {
        yield return () => ([], 1, "missing command is a usage error");
        yield return () => (["--help"], 0, "--help prints usage");
        yield return () => (["-h"], 0, "-h prints usage");
        yield return () => (["--Help"], 0, "flags are matched case-insensitively via ToUpperInvariant()");
        yield return () => (["unknown-command-xyz"], 1, "unrecognised command is a usage error");
        yield return () => (["convert", "--help"], 0, "convert --help prints usage");
        yield return () => (["export", "--help"], 0, "export --help prints usage");
        yield return () => (["convert"], 1, "convert with no source is a usage error");
        yield return () => (["export"], 1, "export with no source is a usage error");
    }

    [Test]
    [MethodDataSource(nameof(Main_ExitCode_Data))]
    public async Task Main_ExitCode_MatchesExpected(string[] args, int expected, string because)
    {
        int exitCode = CliEntry.Run(args);

        await Assert.That(exitCode).IsEqualTo(expected).Because(because);
    }
}
