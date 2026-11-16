// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>The plugins command: the text and JSON list, the pre-scan cache, and the plugin paths setting.</summary>
internal sealed class CliPluginsListTests
{
    #region Helpers

    private const string _TestPluginsName = "NetworkInspector.Plugins.Tests.TestPlugins";

    #endregion

    #region List

    [Test]
    [Category("Slow")]
    public async Task TheTextListShowsTheSummaryAndOneRowPerPlugin()
    {
        using CliSandbox sandbox = new();
        string sample = sandbox.CopyPlugin("Sample.Protocol", "sample");
        string testPlugins = sandbox.CopyPlugin(_TestPluginsName, "test-plugins");
        await sandbox.SetPluginPathsAsync(sample, testPlugins);

        CliResult text = await sandbox.RunAsync("plugins", "list");
        CliResult json = await sandbox.RunAsync("plugins", "list", "--json");

        await Assert.That(text.Exit).IsEqualTo(0);
        await Assert.That(text.OutLines[0]).StartsWith("Pre-scan: 2 candidate files, 2 inspected, 0 from cache, 2 plugin assemblies,");
        string[] builtIns = ["NetworkInspector.StandardProtocols", "NetworkInspector.FileSources", "NetworkInspector.RandomSource", "NetworkInspector.Exporters"];
        foreach (string name in builtIns)
        {
            await Assert.That(text.OutLines.Any(line => line.StartsWith(name + "  ", StringComparison.Ordinal) && line.Contains("  core  Loaded  ", StringComparison.Ordinal)))
                .IsTrue();
        }

        string sampleRow = text.OutLines.Single(static line => line.StartsWith("Sample.Protocol  ", StringComparison.Ordinal));
        await Assert.That(sampleRow).StartsWith("Sample.Protocol  0.1.0  core  Loaded  -  ");
        await Assert.That(sampleRow).Contains("Protocol 2, Heuristic 1, PostParser 1, Setting 1, Source 1, Exporter 1");
        string startRow = text.OutLines.Single(static line => line.StartsWith("Test.StartThrows  ", StringComparison.Ordinal));
        await Assert.That(startRow).Contains("  core  Failed  StartAsync failed: InvalidOperationException: start boom  ");

        // The JSON run lists the same plugins with the same states and reasons.
        using JsonDocument document = JsonDocument.Parse(json.Out);
        JsonElement[] plugins = [.. document.RootElement.GetProperty("plugins").EnumerateArray()];
        string[] jsonNames = [.. plugins.Select(static plugin => plugin.GetProperty("name").GetString() ?? string.Empty)];
        string[] textNames = [.. text.OutLines.Skip(1).Select(static line => line.Split("  ")[0])];
        await Assert.That(jsonNames).IsEquivalentTo(textNames, CollectionOrdering.Matching);
        foreach (JsonElement plugin in plugins)
        {
            foreach (string property in new[] { "name", "version", "tier", "state", "reason", "path", "builtIn", "license", "loadMs", "dependencies", "contributions" })
            {
                await Assert.That(plugin.TryGetProperty(property, out _)).IsTrue();
            }

            await Assert.That(plugin.GetProperty("tier").GetString()).IsEqualTo("core");
        }

        string[] failed = [.. plugins.Where(static plugin => plugin.GetProperty("state").GetString() == "Failed")
            .Select(static plugin => plugin.GetProperty("name").GetString() ?? string.Empty)];
        await Assert.That(failed).Contains("Test.StartThrows");
        await Assert.That(failed.Length).IsGreaterThan(3);
    }

    [Test]
    public async Task AColumnIsOneLineWhateverTheReasonSays()
    {
        await Assert.That(PluginsCommand.Cell(null)).IsEqualTo("-");
        await Assert.That(PluginsCommand.Cell(string.Empty)).IsEqualTo("-");
        await Assert.That(PluginsCommand.Cell("first line\r\nsecond\u001b[0m")).IsEqualTo("first line  second [0m");
        await Assert.That(PluginsCommand.Cell("plain")).IsEqualTo("plain");
    }

    [Test]
    [Category("Slow")]
    public async Task TheSecondRunReadsTheScanCache()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        await sandbox.RunAsync("settings", "get", "plugins.paths");

        CliResult first = await sandbox.RunAsync("plugins", "list", "--json");
        CliResult second = await sandbox.RunAsync("plugins", "list", "--json");

        using JsonDocument firstDocument = JsonDocument.Parse(first.Out);
        using JsonDocument secondDocument = JsonDocument.Parse(second.Out);
        JsonElement firstScan = firstDocument.RootElement.GetProperty("prescan");
        JsonElement secondScan = secondDocument.RootElement.GetProperty("prescan");
        await Assert.That(firstScan.GetProperty("candidateFiles").GetInt32()).IsEqualTo(1);
        await Assert.That(secondScan.GetProperty("inspectedFiles").GetInt32()).IsEqualTo(0);
        await Assert.That(secondScan.GetProperty("cachedFiles").GetInt32()).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "plugin-scan-cache.json"))).IsTrue();
    }

    #endregion

    #region Plugin paths

    [Test]
    public async Task ARelativePathIsStoredAsAFullPathAndFoundInNestedFolders()
    {
        using CliSandbox sandbox = new();
        string nested = Path.Combine(sandbox.Root, "plugins", "sample", "x");
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestPlugins", "Sample.Protocol", "Sample.Protocol.dll"),
            Path.Combine(nested, "Sample.Protocol.dll"));
        string relative = Path.Combine("plugins", "sample");

        // The sandbox resolves relative arguments against its own folder (CliIo.BaseDirectory); the process directory stays as it is.
        CliResult set = await sandbox.RunAsync("settings", "set", "plugins.paths", relative, "--scope", "application");

        CliResult list = await sandbox.RunAsync("plugins", "list");
        string expected = Path.GetFullPath(relative, sandbox.Root);

        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(set.OutLines).IsEquivalentTo([Invariant($"plugins.paths = [{JsonSerializer.Serialize(expected)}]")], CollectionOrdering.Matching);
        await Assert.That(list.Out).Contains("Sample.Protocol  0.1.0  core  Loaded");
    }

    [Test]
    public async Task ARelativeEntryInTheFileIsSkippedWithOneWarningAndTheOthersLoad()
    {
        using CliSandbox sandbox = new();
        string sample = sandbox.CopyPlugin("Sample.Protocol", "sample");
        Directory.CreateDirectory(Path.Combine(sandbox.Base, "application"));
        File.WriteAllText(Path.Combine(sandbox.Base, "application", "plugins.json"),
            JsonSerializer.Serialize(new Dictionary<string, string[]> { ["plugins.paths"] = ["relative/folder", sample] }));

        CliResult list = await sandbox.RunAsync("plugins", "list");

        await Assert.That(list.Exit).IsEqualTo(0);
        string[] warnings = [.. list.ErrorLines.Where(static line => line.StartsWith("Warning settings:", StringComparison.Ordinal))];
        await Assert.That(warnings.Length).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("'relative/folder' is not a full path and is ignored");
        await Assert.That(list.Out).Contains("Sample.Protocol  0.1.0  core  Loaded");
    }

    [Test]
    public async Task AJsonArrayValueAllowsCommasAndMixesFoldersAndFiles()
    {
        using CliSandbox sandbox = new();
        string sample = sandbox.CopyPlugin("Sample.Protocol", "sample, with comma");
        string testPluginsFolder = sandbox.CopyPlugin(_TestPluginsName, "test-plugins");
        string testPluginsFile = Path.Combine(testPluginsFolder, _TestPluginsName + ".dll");

        CliResult set = await sandbox.RunAsync("settings", "set", "plugins.paths", JsonSerializer.Serialize(new[] { sample, testPluginsFile }),
            "--scope", "application");
        CliResult list = await sandbox.RunAsync("plugins", "list");

        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(list.Out).Contains("Sample.Protocol  0.1.0  core  Loaded");
        await Assert.That(list.Out).Contains("Test.Healthy  1.0.0  core  Loaded");
    }

    #endregion
}
