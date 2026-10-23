// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="CliEntry.RunAsync(string[], CliIo, CancellationToken)"/>.
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
        yield return () => (["plugins", "--help"], 0, "plugins --help prints usage");
        yield return () => (["settings", "--help"], 0, "settings --help prints usage");
        yield return () => (["profile", "--help"], 0, "profile --help prints usage");
        yield return () => (["plugins"], 1, "plugins without a sub-command is a usage error");
        yield return () => (["settings", "frob"], 1, "an unknown settings sub-command is a usage error");
        yield return () => (["profile"], 1, "profile without a sub-command is a usage error");
    }

    [Test]
    [MethodDataSource(nameof(Main_ExitCode_Data))]
    public async Task Main_ExitCode_MatchesExpected(string[] args, int expected, string because)
    {
        CliResult result = await CliSandbox.RunWithoutBaseAsync(args, CancellationToken.None);

        await Assert.That(result.Exit).IsEqualTo(expected).Because(because);
    }

    [Test]
    public async Task UsageListsAllFiveCommands()
    {
        CliResult result = await CliSandbox.RunWithoutBaseAsync(["--help"], CancellationToken.None);

        foreach (string command in new[] { "convert", "export", "plugins", "settings", "profile" })
        {
            await Assert.That(result.Error).Contains("  " + command + " ");
        }
    }
}
