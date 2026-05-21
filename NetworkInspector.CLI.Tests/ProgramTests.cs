// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="Program.Main"/>.
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
    // === No arguments → usage (exit 1) ===

    [Test]
    public async Task Main_NoArgs_ReturnsOne()
    {
        int exitCode = Program.Main([]);

        await Assert.That(exitCode).IsEqualTo(1).Because("missing command is a usage error");
    }

    // === --help / -h → usage (exit 0) ===

    [Test]
    public async Task Main_HelpLongFlag_ReturnsZero()
    {
        int exitCode = Program.Main(["--help"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Main_HelpShortFlag_ReturnsZero()
    {
        int exitCode = Program.Main(["-h"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Main_HelpFlagMixedCase_ReturnsZero()
    {
        // Flags are matched case-insensitively via ToUpperInvariant()
        int exitCode = Program.Main(["--Help"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    // === Unknown command → usage (exit 1) ===

    [Test]
    public async Task Main_UnknownCommand_ReturnsOne()
    {
        int exitCode = Program.Main(["unknown-command-xyz"]);

        await Assert.That(exitCode).IsEqualTo(1).Because("unrecognised command is a usage error");
    }

    // === Sub-command --help paths ===

    [Test]
    public async Task Main_ConvertHelp_ReturnsZero()
    {
        // 'ni convert --help' → ConvertCommand returns 0 for the help flag
        int exitCode = Program.Main(["convert", "--help"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Main_ExportHelp_ReturnsZero()
    {
        int exitCode = Program.Main(["export", "--help"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    // === Sub-command with no arguments → usage error (exit 1) ===

    [Test]
    public async Task Main_ConvertNoArgs_ReturnsOne()
    {
        int exitCode = Program.Main(["convert"]);

        await Assert.That(exitCode).IsEqualTo(1).Because("convert with no source is a usage error");
    }

    [Test]
    public async Task Main_ExportNoArgs_ReturnsOne()
    {
        int exitCode = Program.Main(["export"]);

        await Assert.That(exitCode).IsEqualTo(1).Because("export with no source is a usage error");
    }
}
