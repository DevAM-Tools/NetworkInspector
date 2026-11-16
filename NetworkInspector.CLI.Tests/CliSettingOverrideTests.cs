// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary><c>--set key=value</c>: one run, no write, the loader limits before the first file, refusals before any work, and the removed flags.</summary>
internal sealed class CliSettingOverrideTests
{
    #region One run

    [Test]
    public async Task AnOverrideIsInEffectForTheRunAndNothingIsWritten()
    {
        using CliSandbox sandbox = new();
        CliResult warm = await sandbox.RunAsync("settings", "get", "settings.maxJsonDepth");
        string[] before = [.. Directory.EnumerateFiles(sandbox.Base, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

        CliResult once = await sandbox.RunAsync("settings", "get", "settings.maxJsonDepth", "--set", "settings.maxJsonDepth=10");
        CliResult twice = await sandbox.RunAsync(
            "settings", "get", "settings.maxJsonDepth", "--set", "settings.maxJsonDepth=10", "--set", "settings.maxJsonDepth=12");
        CliResult after = await sandbox.RunAsync("settings", "get", "settings.maxJsonDepth");

        string[] files = [.. Directory.EnumerateFiles(sandbox.Base, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        await Assert.That(warm.OutLines).IsEquivalentTo(["64"], CollectionOrdering.Matching);
        await Assert.That(once.OutLines).IsEquivalentTo(["10"], CollectionOrdering.Matching);
        await Assert.That(twice.OutLines).IsEquivalentTo(["12"], CollectionOrdering.Matching);
        await Assert.That(after.OutLines).IsEquivalentTo(["64"], CollectionOrdering.Matching);
        await Assert.That(files).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnOverrideOfAProfileSettingShowsInTheListOfTheRun()
    {
        using CliSandbox sandbox = new();

        CliResult list = await sandbox.RunAsync("settings", "list", "--group", "tcp", "--set", "tcp.max_tracked_streams=777");

        await Assert.That(list.Exit).IsEqualTo(0);
        await Assert.That(list.OutLines).Contains("profile  tcp.max_tracked_streams  U64  777");
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "default", "tcp.json"))).IsFalse();
    }

    [Test]
    [Category("Slow")]
    public async Task AnOverrideOfAPluginSettingIsInEffectBeforeThePluginStarts()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));

        CliResult list = await sandbox.RunAsync("plugins", "list", "--set", "sample.greeting=Moin");
        CliResult get = await sandbox.RunAsync("settings", "get", "sample.greeting");

        await Assert.That(list.ErrorLines).Contains("Info Sample.Protocol: started; greeting=Moin");
        await Assert.That(get.OutLines).IsEquivalentTo(["Hello from Sample.Protocol"], CollectionOrdering.Matching);
    }

    #endregion

    #region Loader limits before the first file

    [Test]
    public async Task AnOverrideOfTheFileLimitLetsTheRunLoadAFileThatTheStoredLimitSkips()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000");
        File.AppendAllText(Path.Combine(sandbox.Base, "default", "tcp.json"), new string(' ', 2048));
        await sandbox.RunAsync("settings", "set", "settings.maxFileSize", "1KiB");

        CliResult skipped = await sandbox.RunAsync("settings", "get", "tcp.max_tracked_streams");
        CliResult loaded = await sandbox.RunAsync("settings", "get", "tcp.max_tracked_streams", "--set", "settings.maxFileSize=1MiB");

        await Assert.That(skipped.OutLines).IsEquivalentTo(["10000"], CollectionOrdering.Matching);
        await Assert.That(skipped.Error).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
        await Assert.That(loaded.OutLines).IsEquivalentTo(["5000"], CollectionOrdering.Matching);
        await Assert.That(loaded.Error).DoesNotContain("larger than allowed");
    }

    [Test]
    public async Task AnOverrideOfTheFileLimitBoundsTheLoadOfAFileAboveIt()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000");
        File.AppendAllText(Path.Combine(sandbox.Base, "default", "tcp.json"), new string(' ', 2048));

        CliResult result = await sandbox.RunAsync("settings", "get", "tcp.max_tracked_streams", "--set", "settings.maxFileSize=1KiB");

        await Assert.That(result.OutLines).IsEquivalentTo(["10000"], CollectionOrdering.Matching);
        await Assert.That(result.Error).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
    }

    [Test]
    public async Task TheOverrideDoesNotBecomeTheDefaultOfTheLimit()
    {
        using CliSandbox sandbox = new();

        CliResult describe = await sandbox.RunAsync(
            "settings", "describe", "settings.maxFileSize", "--set", "settings.maxFileSize=1GiB");

        await Assert.That(describe.OutLines).Contains("  Value:    1 GiB");
        await Assert.That(describe.OutLines).Contains("  Default:  64 MiB (fixed)");
    }

    #endregion

    #region Refusals before any work

    [Test]
    public async Task AnUnknownKeyIsRefusedWithASuggestion()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "list", "--set", "settings.maxFileSiz=1MiB");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: Unknown setting 'settings.maxFileSiz'; did you mean: settings.maxFileSize?"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AValueWithADecimalUnitIsRefusedWithTheBinarySpelling()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "list", "--set", "settings.maxFileSize=2MB");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages.Single()).Contains("use 2 MiB");
    }

    [Test]
    public async Task AnOverrideWithoutAnEqualsSignOrKeyIsRefused()
    {
        using CliSandbox sandbox = new();

        CliResult noEquals = await sandbox.RunAsync("settings", "list", "--set", "settings.maxFileSize");
        CliResult noKey = await sandbox.RunAsync("settings", "list", "--set", "=5");

        await Assert.That(noEquals.Exit).IsEqualTo(1);
        await Assert.That(noEquals.ErrorMessages.Single()).StartsWith("Error: --set expects key=value");
        await Assert.That(noKey.Exit).IsEqualTo(1);
    }

    [Test]
    public async Task AValueOfAProfileSettingInTheWrongFormIsRefused()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "list", "--set", "tcp.max_tracked_streams=abc");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages.Single()).StartsWith("Error: tcp.max_tracked_streams: ");
    }

    [Test]
    public async Task AConvertRunWithARefusedOverrideCreatesNoOutputFile()
    {
        using CliSandbox sandbox = new();
        string output = Path.Combine(sandbox.Base, "out.pcapng");

        int code = await CliRun.ConvertAsync(
            ["random:count=2,mode=udp4", "-o", output, "-n", "2", "--settings-path", sandbox.Base, "--set", "settings.maxFileSiz=1MiB"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
        await Assert.That(File.Exists(output)).IsFalse();
    }

    [Test]
    public async Task SetAndResetRefuseAnOverrideBecauseItWouldBeStoredOrContradicted()
    {
        using CliSandbox sandbox = new();

        CliResult set = await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000", "--set", "settings.maxJsonDepth=10");
        CliResult reset = await sandbox.RunAsync("settings", "reset", "tcp.max_tracked_streams", "--set", "settings.maxJsonDepth=10");

        await Assert.That(set.Exit).IsEqualTo(1);
        await Assert.That(reset.Exit).IsEqualTo(1);
        await Assert.That(set.ErrorMessages).IsEquivalentTo(["Error: --set applies to one run; store values with ni settings set."], CollectionOrdering.Matching);
        await Assert.That(reset.ErrorMessages).IsEquivalentTo(set.ErrorMessages, CollectionOrdering.Matching);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "default", "tcp.json"))).IsFalse();
    }

    #endregion

    #region Removed flags

    [Test]
    [Arguments("--settings-max-config-file-bytes", "5", "--set settings.maxFileSize=5")]
    [Arguments("--settings-max-json-depth", "10", "--set settings.maxJsonDepth=10")]
    public async Task ARemovedFlagIsRefusedAndNamesTheReplacement(string flag, string value, string replacement)
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "list", flag, value);

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo([Invariant($"Error: {flag} was replaced: use {replacement}.")], CollectionOrdering.Matching);
    }

    #endregion
}
