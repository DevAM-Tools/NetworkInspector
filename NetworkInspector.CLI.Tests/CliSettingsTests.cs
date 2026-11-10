// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>The settings command: plugin settings across runs, listing, scopes and groups, reset, stored values, and errors.</summary>
internal sealed class CliSettingsTests
{
    #region Helpers

    private const string _TestPluginsName = "NetworkInspector.Plugins.Tests.TestPlugins";

    /// <summary>Every file of the application and profile folders with its bytes, so tests can prove that nothing changed.</summary>
    private static Dictionary<string, byte[]> _SettingsFiles(string settingsBase)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string folder in new[] { "application", "default", "CAN", "Other" })
        {
            string path = Path.Combine(settingsBase, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                // The lock and the manifest belong to the settings system, not to the settings themselves.
                string name = Path.GetFileName(file);
                if (name is ".ni-settings.lock" or ".ni-settings-owned")
                {
                    continue;
                }

                files[Path.GetRelativePath(settingsBase, file).Replace('\\', '/')] = File.ReadAllBytes(file);
            }
        }

        return files;
    }

    private static async Task _AssertOneErrorAndNoChangeAsync(CliSandbox sandbox, string expected, params string[] args)
    {
        Dictionary<string, byte[]> before = _SettingsFiles(sandbox.Base);

        CliResult result = await sandbox.RunAsync(args);

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo([expected], CollectionOrdering.Matching);
        Dictionary<string, byte[]> after = _SettingsFiles(sandbox.Base);
        await Assert.That(after.Keys.Order().ToArray()).IsEquivalentTo(before.Keys.Order().ToArray(), CollectionOrdering.Matching);
        foreach (KeyValuePair<string, byte[]> file in before)
        {
            await Assert.That(after[file.Key].SequenceEqual(file.Value)).IsTrue();
        }
    }

    #endregion

    #region Plugin settings

    [Test]
    public async Task APluginSettingSurvivesSeparateRuns()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));

        CliResult set = await sandbox.RunAsync("settings", "set", "sample.greeting", "Hi there");
        string stored = File.ReadAllText(Path.Combine(sandbox.Base, "application", "sample.json"));
        CliResult get = await sandbox.RunAsync("settings", "get", "sample.greeting");
        CliResult list = await sandbox.RunAsync("plugins", "list");
        await sandbox.RunAsync("settings", "set", "plugins.paths", string.Empty);
        string storedWithoutPlugin = File.ReadAllText(Path.Combine(sandbox.Base, "application", "sample.json"));
        CliResult withoutPlugin = await sandbox.RunAsync("plugins", "list");
        await sandbox.SetPluginPathsAsync(sandbox.Combine("sample"));
        CliResult restored = await sandbox.RunAsync("settings", "get", "sample.greeting");

        await Assert.That(set.OutLines).IsEquivalentTo(["sample.greeting = Hi there"], CollectionOrdering.Matching);
        JsonObject json = JsonNode.Parse(stored) as JsonObject ?? throw new InvalidOperationException("sample.json is not an object.");
        await Assert.That(json.Count).IsEqualTo(1);
        await Assert.That(json["sample.greeting"]?.GetValue<string>()).IsEqualTo("Hi there");
        await Assert.That(get.OutLines).IsEquivalentTo(["Hi there"], CollectionOrdering.Matching);
        await Assert.That(list.ErrorLines).Contains("Info Sample.Protocol: started; greeting=Hi there");
        await Assert.That(withoutPlugin.Out).DoesNotContain("Sample.Protocol");
        await Assert.That(storedWithoutPlugin).IsEqualTo(stored);
        await Assert.That(restored.OutLines).IsEquivalentTo(["Hi there"], CollectionOrdering.Matching);
    }

    #endregion

    #region Listing

    [Test]
    public async Task ListShowsOneLinePerSettingWithScopeTypeAndValue()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));

        CliResult list = await sandbox.RunAsync("settings", "list");
        CliResult application = await sandbox.RunAsync("settings", "list", "--scope", "application");
        CliResult tcp = await sandbox.RunAsync("settings", "list", "--group", "tcp");

        await Assert.That(list.Exit).IsEqualTo(0);
        await Assert.That(list.OutLines).Contains(Invariant($"application  plugins.paths  StringArray  {JsonSerializer.Serialize(new[] { sandbox.Combine("sample") })}"));
        await Assert.That(list.OutLines).Contains("application  plugins.startTimeoutSeconds  I64  120");
        await Assert.That(list.OutLines).Contains("application  sample.greeting  String  Hello from Sample.Protocol");
        await Assert.That(list.OutLines).Contains("profile  tcp.relative_sequence_numbers  Bool  true");
        await Assert.That(application.OutLines.All(static line => line.StartsWith("application  ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(tcp.OutLines.All(static line => line.StartsWith("profile  tcp.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(tcp.OutLines.Length).IsGreaterThan(2);

        // Application entries come first; each scope is sorted by name.
        int firstProfile = Array.FindIndex(list.OutLines, static line => line.StartsWith("profile  ", StringComparison.Ordinal));
        await Assert.That(list.OutLines[..firstProfile].All(static line => line.StartsWith("application  ", StringComparison.Ordinal))).IsTrue();
        string[] applicationNames = [.. list.OutLines[..firstProfile].Select(static line => line.Split("  ")[1])];
        await Assert.That(applicationNames).IsEquivalentTo([.. applicationNames.Order(StringComparer.Ordinal)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task JsonListHasTheDocumentedPropertiesAndStoredFlags()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000");

        CliResult json = await sandbox.RunAsync("settings", "list", "--json");

        using JsonDocument document = JsonDocument.Parse(json.Out);
        JsonElement[] items = [.. document.RootElement.EnumerateArray()];
        string[] properties = ["scope", "name", "uiName", "group", "type", "value", "default", "description", "activation", "stored", "orphan"];
        foreach (JsonElement item in items)
        {
            foreach (string property in properties)
            {
                await Assert.That(item.TryGetProperty(property, out _)).IsTrue();
            }
        }

        JsonElement streams = items.Single(static item => item.GetProperty("name").GetString() == "tcp.max_tracked_streams");
        JsonElement relative = items.Single(static item => item.GetProperty("name").GetString() == "tcp.relative_sequence_numbers");
        JsonElement paths = items.Single(static item => item.GetProperty("name").GetString() == "plugins.paths");
        await Assert.That(streams.GetProperty("value").GetInt64()).IsEqualTo(5000L);
        await Assert.That(streams.GetProperty("default").GetInt64()).IsEqualTo(10000L);
        await Assert.That(streams.GetProperty("stored").GetBoolean()).IsTrue();
        await Assert.That(relative.GetProperty("stored").GetBoolean()).IsFalse();
        await Assert.That(paths.GetProperty("activation").GetString()).IsEqualTo("Restart");
        await Assert.That(items[0].GetProperty("scope").GetString()).IsEqualTo("application");
    }

    [Test]
    public async Task AStringValueWithControlCharactersStaysOneListLineButGetPrintsItAsItIs()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        string value = "first\nforged  line\u001b[31m";
        await sandbox.RunAsync("settings", "set", "sample.greeting", value);

        CliResult list = await sandbox.RunAsync("settings", "list", "--group", "sample");
        CliResult get = await sandbox.RunAsync("settings", "get", "sample.greeting");

        await Assert.That(list.OutLines.Length).IsEqualTo(1);
        await Assert.That(list.OutLines[0]).EndsWith("first forged  line [31m");
        await Assert.That(list.Out.Contains('\u001b', StringComparison.Ordinal)).IsFalse();
        await Assert.That(get.Out.TrimEnd()).IsEqualTo(value);
    }

    [Test]
    public async Task TheStoredFlagsReadFilesWithTheSameLimitsAsTheLoad()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000");

        // The smallest limit is 1 KiB, so the stored file is padded with trailing white space (still valid JSON) to exceed it.
        File.AppendAllText(Path.Combine(sandbox.Base, "default", "tcp.json"), new string(' ', 2048));

        // A limit below the size of the file: Load skips it with a warning, and the stored flag must not read it either.
        CliResult limited = await sandbox.RunAsync("settings", "list", "--json", "--scope", "profile", "--set", "settings.maxFileSize=1KiB");
        CliResult unlimited = await sandbox.RunAsync("settings", "list", "--json", "--scope", "profile");

        using JsonDocument limitedDocument = JsonDocument.Parse(limited.Out);
        using JsonDocument unlimitedDocument = JsonDocument.Parse(unlimited.Out);
        JsonElement limitedStreams = limitedDocument.RootElement.EnumerateArray()
            .Single(static item => item.GetProperty("name").GetString() == "tcp.max_tracked_streams");
        JsonElement unlimitedStreams = unlimitedDocument.RootElement.EnumerateArray()
            .Single(static item => item.GetProperty("name").GetString() == "tcp.max_tracked_streams");
        await Assert.That(limited.Exit).IsEqualTo(0);
        await Assert.That(limited.Error).Contains("is larger than allowed");
        await Assert.That(limited.Error).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
        await Assert.That(limitedStreams.GetProperty("value").GetInt64()).IsEqualTo(10000L);
        await Assert.That(limitedStreams.GetProperty("stored").GetBoolean()).IsFalse();
        await Assert.That(unlimitedStreams.GetProperty("stored").GetBoolean()).IsTrue();
    }

    #endregion

    #region Scope and group

    [Test]
    public async Task ASettingChangesOnlyTheFileOfItsProfileAndScope()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("profile", "create", "CAN");
        await sandbox.RunAsync("profile", "create", "Other");
        File.WriteAllText(Path.Combine(sandbox.Base, "Other", "tcp.json"), """{"tcp.max_tracked_streams": 5000}""");
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin(_TestPluginsName, "test-plugins"));
        Dictionary<string, byte[]> before = _SettingsFiles(sandbox.Base);

        CliResult set = await sandbox.RunAsync("settings", "set", "tcp.relative_sequence_numbers", "false", "--profile", "CAN");
        Dictionary<string, byte[]> after = _SettingsFiles(sandbox.Base);
        CliResult list = await sandbox.RunAsync("settings", "list", "--group", "tcp", "--profile", "CAN");
        CliResult reset = await sandbox.RunAsync("settings", "reset", "tcp.relative_sequence_numbers", "--profile", "CAN");
        CliResult get = await sandbox.RunAsync("settings", "get", "tcp.relative_sequence_numbers", "--profile", "CAN");

        await Assert.That(set.Exit).IsEqualTo(0);
        string[] changed = [.. after.Keys.Where(key => !before.TryGetValue(key, out byte[]? old) || !old.SequenceEqual(after[key]))];
        await Assert.That(changed).IsEquivalentTo(["CAN/tcp.json"], CollectionOrdering.Matching);
        JsonObject can = JsonNode.Parse(after["CAN/tcp.json"]) as JsonObject ?? throw new InvalidOperationException("not an object");
        await Assert.That(can.Count).IsEqualTo(1);
        await Assert.That(list.OutLines).Contains("profile  tcp.relative_sequence_numbers  Bool  false");
        await Assert.That(reset.OutLines).IsEquivalentTo(["tcp.relative_sequence_numbers = true (default)"], CollectionOrdering.Matching);
        await Assert.That(get.OutLines).IsEquivalentTo(["true"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ASettingOfBothScopesIsAddressedByScopeAndGroup()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin(_TestPluginsName, "test-plugins"));

        CliResult application = await sandbox.RunAsync("settings", "get", "test.healthy.level", "--scope", "application");
        CliResult profile = await sandbox.RunAsync("settings", "get", "test.healthy.level", "--scope", "profile");
        CliResult set = await sandbox.RunAsync("settings", "set", "test.healthy.level", "3", "--scope", "profile", "--group", "test.healthy");

        await Assert.That(application.OutLines).IsEquivalentTo(["1"], CollectionOrdering.Matching);
        await Assert.That(profile.OutLines).IsEquivalentTo(["1"], CollectionOrdering.Matching);
        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "default", "test.healthy.json"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "application", "test.healthy.json"))).IsFalse();
    }

    #endregion

    #region Reset

    [Test]
    public async Task ResetRemovesTheStoredKeyInsteadOfPinningTheDefault()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("profile", "create", "CAN");
        await sandbox.RunAsync("settings", "set", "tcp.relative_sequence_numbers", "false", "--profile", "CAN");
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000", "--profile", "CAN");
        string file = Path.Combine(sandbox.Base, "CAN", "tcp.json");

        await sandbox.RunAsync("settings", "reset", "tcp.relative_sequence_numbers", "--profile", "CAN");
        JsonObject afterFirst = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidOperationException("not an object");
        await sandbox.RunAsync("settings", "reset", "tcp.max_tracked_streams", "--profile", "CAN");

        await Assert.That(afterFirst.Count).IsEqualTo(1);
        await Assert.That(afterFirst.ContainsKey("tcp.max_tracked_streams")).IsTrue();
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task ResettingAnUnstoredSettingCreatesNoFile()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("profile", "create", "CAN");
        string[] before = [.. Directory.EnumerateFileSystemEntries(Path.Combine(sandbox.Base, "CAN"), "*", SearchOption.AllDirectories)];

        CliResult reset = await sandbox.RunAsync("settings", "reset", "tcp.relative_sequence_numbers", "--profile", "CAN");

        await Assert.That(reset.Exit).IsEqualTo(0);
        await Assert.That(reset.OutLines).IsEquivalentTo(["tcp.relative_sequence_numbers = true (default)"], CollectionOrdering.Matching);
        string[] after = [.. Directory.EnumerateFileSystemEntries(Path.Combine(sandbox.Base, "CAN"), "*", SearchOption.AllDirectories)];
        await Assert.That(after).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResettingThePluginPathsRemovesThem()
    {
        using CliSandbox sandbox = new();
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));

        CliResult reset = await sandbox.RunAsync("settings", "reset", "plugins.paths", "--scope", "application");
        CliResult get = await sandbox.RunAsync("settings", "get", "plugins.paths");

        await Assert.That(reset.OutLines).IsEquivalentTo(["plugins.paths = [] (default)"], CollectionOrdering.Matching);
        await Assert.That(get.OutLines).IsEquivalentTo(["[]"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "application", "plugins.json"))).IsFalse();
    }

    #endregion

    #region Stored values of plugins that are not loaded

    [Test]
    public async Task AStoredValueWithoutItsPluginIsListedReadAndChangedAsJson()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(Path.Combine(sandbox.Base, "application"));
        File.WriteAllText(Path.Combine(sandbox.Base, "application", "sample.json"), """{"sample.greeting":"Hi"}""");

        CliResult list = await sandbox.RunAsync("settings", "list");
        CliResult json = await sandbox.RunAsync("settings", "list", "--json", "--group", "sample");
        CliResult get = await sandbox.RunAsync("settings", "get", "sample.greeting");
        CliResult setJson = await sandbox.RunAsync("settings", "set", "sample.greeting", "\"Hello\"");
        CliResult notJson = await sandbox.RunAsync("settings", "set", "sample.greeting", "Hello again");
        string stored = File.ReadAllText(Path.Combine(sandbox.Base, "application", "sample.json"));

        await Assert.That(list.OutLines).Contains("application  sample.greeting  (not loaded)  \"Hi\"");
        using JsonDocument document = JsonDocument.Parse(json.Out);
        JsonElement entry = document.RootElement.EnumerateArray().Single();
        await Assert.That(entry.GetProperty("orphan").GetBoolean()).IsTrue();
        await Assert.That(entry.GetProperty("type").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(get.OutLines).IsEquivalentTo(["\"Hi\""], CollectionOrdering.Matching);
        await Assert.That(setJson.Exit).IsEqualTo(0);
        await Assert.That(notJson.Exit).IsEqualTo(1);
        await Assert.That(notJson.ErrorMessages).IsEquivalentTo(
            ["Error: sample.greeting: 'Hello again' is not valid JSON; the plugin that registers this setting is not loaded."],
            CollectionOrdering.Matching);
        await Assert.That(stored).Contains("\"Hello\"");

        // With the plugin configured again the stored value applies; resetting removes it.
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin("Sample.Protocol", "sample"));
        CliResult applied = await sandbox.RunAsync("settings", "get", "sample.greeting");
        await Assert.That(applied.OutLines).IsEquivalentTo(["Hello"], CollectionOrdering.Matching);
        await sandbox.RunAsync("settings", "set", "plugins.paths", string.Empty);
        CliResult removed = await sandbox.RunAsync("settings", "reset", "sample.greeting");
        await Assert.That(removed.OutLines).IsEquivalentTo(["sample.greeting removed"], CollectionOrdering.Matching);
    }

    #endregion

    #region Errors

    [Test]
    public async Task EveryMistakeIsOneErrorLineAndChangesNoFile()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("profile", "create", "CAN");
        await sandbox.SetPluginPathsAsync(sandbox.CopyPlugin(_TestPluginsName, "test-plugins"));

        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: Unknown setting 'tcp.relativ'; did you mean: tcp.relative_sequence_numbers?",
            "settings", "get", "tcp.relativ", "--profile", "CAN");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: Unknown setting 'zzz.qqq'; see ni settings list.",
            "settings", "get", "zzz.qqq");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: Profile 'Lab' does not exist; create it with ni profile create Lab.",
            "settings", "list", "--profile", "Lab");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: Setting 'tcp.max_tracked_streams' is in group 'tcp', not 'udp'.",
            "settings", "set", "tcp.max_tracked_streams", "100", "--group", "udp");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: Setting 'test.healthy.level' exists in the application and the profile scope; add --scope.",
            "settings", "get", "test.healthy.level");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: tcp.max_tracked_streams: 'abc' is not a whole number; example: 10000",
            "settings", "set", "tcp.max_tracked_streams", "abc");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: plugins.startTimeoutSeconds: allowed 1 – 2147483",
            "settings", "set", "plugins.startTimeoutSeconds", "0");
        await _AssertOneErrorAndNoChangeAsync(sandbox,
            "Error: eth.assume_fcs: 'maybe' is not true or false",
            "settings", "set", "eth.assume_fcs", "maybe");
    }

    [Test]
    public async Task AnUnknownGroupListsTheGroups()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "list", "--group", "nope");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages.Length).IsEqualTo(1);
        await Assert.That(result.ErrorMessages[0]).StartsWith("Error: Unknown group 'nope'; groups: ");
        await Assert.That(result.ErrorMessages[0]).Contains("plugins, ");
        await Assert.That(result.ErrorMessages[0]).Contains(" tcp");
    }

    [Test]
    public async Task ABadJsonArrayAndAWrongArgumentCountAreUsageProblems()
    {
        using CliSandbox sandbox = new();

        CliResult json = await sandbox.RunAsync("settings", "set", "pdu_transport.udp_dispatch_ports", "[40001,");
        CliResult oneArgument = await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams");

        await Assert.That(json.Exit).IsEqualTo(1);
        await Assert.That(json.ErrorMessages.Length).IsEqualTo(1);
        await Assert.That(json.ErrorMessages[0]).Contains("is not a valid JSON array");
        await Assert.That(oneArgument.Exit).IsEqualTo(1);
        await Assert.That(oneArgument.Error).Contains("Usage: ni settings");
    }

    #endregion
}
