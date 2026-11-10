// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary><c>ni settings describe</c> for one key and for every limit, and the unit forms of <c>get</c>, <c>set</c>, and <c>list</c>.</summary>
internal sealed class SettingsDescribeTests
{
    private static readonly string[] _Headings =
        ["What it limits", "Why it exists", "How it helps you", "Smaller values", "Larger values", "Off"];

    #region One limit

    [Test]
    public async Task DescribeOneLimitPrintsTheHeaderAndEveryParagraphInOrder()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "settings.maxFileSize");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.OutLines[0]).IsEqualTo("Largest settings file");
        await Assert.That(result.OutLines).Contains("  Key:      settings.maxFileSize");
        await Assert.That(result.OutLines).Contains("  Unit:     bytes");
        await Assert.That(result.OutLines).Contains("  Value:    64 MiB (default)");
        await Assert.That(result.OutLines).Contains("  Default:  64 MiB (fixed)");
        await Assert.That(result.OutLines).Contains("  Range:    1 KiB – 1 TiB, or off");
        await Assert.That(result.OutLines).Contains("  Scope:    application");
        int previous = -1;
        foreach (string heading in _Headings)
        {
            int index = Array.IndexOf(result.OutLines, heading);
            await Assert.That(index).IsGreaterThan(previous);
            await Assert.That(result.OutLines[index + 1].Trim().Length).IsGreaterThan(0);
            previous = index;
        }
    }

    [Test]
    public async Task DescribeShowsAChangedValueWithoutTheDefaultNote()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "settings.maxJsonDepth", "16");

        CliResult result = await sandbox.RunAsync("settings", "describe", "settings.maxJsonDepth");

        await Assert.That(result.OutLines).Contains("  Value:    16");
        await Assert.That(result.OutLines).Contains("  Default:  64 (fixed)");
    }

    [Test]
    public async Task DescribeOfAnUnknownKeySuggestsTheNearestNames()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "settings.maxFileSiz");

        await Assert.That(result.Exit).IsEqualTo(1);
        await Assert.That(result.ErrorMessages).IsEquivalentTo(
            ["Error: Unknown setting 'settings.maxFileSiz'; did you mean: settings.maxFileSize?"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DescribeOfASettingThatIsNoLimitPrintsTheHeaderWithoutLimitParagraphs()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "tcp.max_tracked_streams");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.OutLines).Contains("  Key:      tcp.max_tracked_streams");
        await Assert.That(result.OutLines).Contains("  Scope:    profile");
        await Assert.That(result.OutLines.Any(static line => line is "Why it exists" or "Smaller values")).IsFalse();
    }

    [Test]
    public async Task DescribeJsonHasOneNamedFieldPerItemAndTheExactNumbers()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "settings.maxFileSize", "--format", "json");

        using JsonDocument document = JsonDocument.Parse(result.Out);
        JsonElement root = document.RootElement;
        string[] fields = [.. root.EnumerateObject().Select(static property => property.Name)];
        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(fields).IsEquivalentTo(
        [
            "key", "group", "scope", "title", "unit", "value", "valueText", "default", "defaultText", "defaultOrigin", "minimum",
            "maximum", "rangeText", "allowOff", "readOnly", "description", "reason", "benefit", "smallerValues", "largerValues",
            "offEffect",
        ]);
        await Assert.That(root.GetProperty("value").GetInt64()).IsEqualTo(64L << 20);
        await Assert.That(root.GetProperty("valueText").GetString()).IsEqualTo("64 MiB");
        await Assert.That(root.GetProperty("defaultOrigin").GetString()).IsEqualTo("fixed");
        await Assert.That(root.GetProperty("minimum").GetInt64()).IsEqualTo(1L << 10);
        await Assert.That(root.GetProperty("maximum").GetInt64()).IsEqualTo(1L << 40);
        await Assert.That(root.GetProperty("allowOff").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ALimitThatIsOffIsWrittenAsTheWordOffInJson()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "settings.maxFileSize", "off");

        CliResult result = await sandbox.RunAsync("settings", "describe", "settings.maxFileSize", "--format", "json");

        using JsonDocument document = JsonDocument.Parse(result.Out);
        await Assert.That(document.RootElement.GetProperty("value").GetString()).IsEqualTo("off");
        await Assert.That(document.RootElement.GetProperty("valueText").GetString()).IsEqualTo("off");
    }

    #endregion

    #region Every limit

    /// <summary>The key of every limit that <c>ni</c> offers, sorted as the listing sorts them: the settings library and Core.</summary>
    private static string[] _AllLimitKeys() =>
    [
        .. NetworkInspector.Settings.SettingsLimits.All
            .Concat(NetworkInspector.Core.Limits.CoreLimits.All)
            .Select(static definition => definition.Key)
            .Order(StringComparer.Ordinal),
    ];

    [Test]
    public async Task DescribeLimitsListsOneRowPerLimitSortedByKey()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "--limits");

        string[] keys = [.. result.OutLines.Skip(1).Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])];
        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.OutLines[0]).StartsWith("Key");
        await Assert.That(keys).IsEquivalentTo(_AllLimitKeys(), CollectionOrdering.Matching);
        await Assert.That(result.OutLines.Single(static line => line.StartsWith("settings.maxFileSize", StringComparison.Ordinal)))
            .Contains("Largest settings file");
    }

    [Test]
    public async Task DescribeLimitsJsonIsAnArrayOfTheObjectsOfOneKey()
    {
        using CliSandbox sandbox = new();

        CliResult list = await sandbox.RunAsync("settings", "describe", "--limits", "--format", "json");
        CliResult one = await sandbox.RunAsync("settings", "describe", "settings.maxGroupFiles", "--format", "json");

        using JsonDocument array = JsonDocument.Parse(list.Out);
        using JsonDocument single = JsonDocument.Parse(one.Out);
        JsonElement fromList = array.RootElement.EnumerateArray().Single(
            static item => item.GetProperty("key").GetString() == "settings.maxGroupFiles");
        await Assert.That(array.RootElement.GetArrayLength()).IsEqualTo(_AllLimitKeys().Length);

        // The two forms differ only in indentation, so the fields are compared one by one.
        string[] listed = [.. fromList.EnumerateObject().Select(static property => property.Name + "=" + property.Value.GetRawText())];
        string[] alone = [.. single.RootElement.EnumerateObject().Select(static property => property.Name + "=" + property.Value.GetRawText())];
        await Assert.That(listed).IsEquivalentTo(alone, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DescribeLimitsMarkdownIsOneTableAndOneSectionPerLimit()
    {
        using CliSandbox sandbox = new();

        CliResult result = await sandbox.RunAsync("settings", "describe", "--limits", "--format", "markdown");

        await Assert.That(result.Exit).IsEqualTo(0);
        await Assert.That(result.OutLines[0]).IsEqualTo("| Key | Title | Default | Unit | Range |");
        await Assert.That(result.OutLines.Count(static line => line.StartsWith("## `", StringComparison.Ordinal))).IsEqualTo(_AllLimitKeys().Length);
        await Assert.That(result.Out).Contains("**Why it exists.** ");
    }

    [Test]
    public async Task MarkdownIsRefusedForOneKeyAndFormatNeedsAKnownWord()
    {
        using CliSandbox sandbox = new();

        CliResult markdown = await sandbox.RunAsync("settings", "describe", "settings.maxFileSize", "--format", "markdown");
        CliResult unknown = await sandbox.RunAsync("settings", "describe", "--limits", "--format", "html");

        await Assert.That(markdown.Exit).IsEqualTo(1);
        await Assert.That(markdown.ErrorMessages).IsEquivalentTo(["Error: --format markdown is available only with --limits."], CollectionOrdering.Matching);
        await Assert.That(unknown.Exit).IsEqualTo(1);
        await Assert.That(unknown.Error).Contains("Unknown format 'html'");
    }

    [Test]
    public async Task DescribeNeedsExactlyOneKeyOrTheLimitsFlag()
    {
        using CliSandbox sandbox = new();

        CliResult none = await sandbox.RunAsync("settings", "describe");
        CliResult both = await sandbox.RunAsync("settings", "describe", "settings.maxFileSize", "--limits");

        await Assert.That(none.Exit).IsEqualTo(1);
        await Assert.That(both.Exit).IsEqualTo(1);
    }

    #endregion

    #region Units in get, set, and list

    [Test]
    public async Task SetGetAndListUseTheUnitFormAndOff()
    {
        using CliSandbox sandbox = new();

        CliResult set = await sandbox.RunAsync("settings", "set", "settings.maxFileSize", "256MiB");
        CliResult get = await sandbox.RunAsync("settings", "get", "settings.maxFileSize");
        CliResult list = await sandbox.RunAsync("settings", "list", "--group", "settings");
        CliResult off = await sandbox.RunAsync("settings", "set", "settings.maxFileSize", "off");
        CliResult getOff = await sandbox.RunAsync("settings", "get", "settings.maxFileSize");
        CliResult reset = await sandbox.RunAsync("settings", "reset", "settings.maxFileSize");

        await Assert.That(set.OutLines).IsEquivalentTo(["settings.maxFileSize = 256 MiB"], CollectionOrdering.Matching);
        await Assert.That(get.OutLines).IsEquivalentTo(["256 MiB"], CollectionOrdering.Matching);
        await Assert.That(list.OutLines).Contains("application  settings.maxFileSize  Limit  256 MiB");
        await Assert.That(off.Exit).IsEqualTo(0);
        await Assert.That(getOff.OutLines).IsEquivalentTo(["off"], CollectionOrdering.Matching);
        await Assert.That(reset.OutLines).IsEquivalentTo(["settings.maxFileSize = 64 MiB (default)"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "application", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task AStoredLoaderLimitBoundsTheFilesOfTheNextRun()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("settings", "set", "tcp.max_tracked_streams", "5000");
        File.AppendAllText(Path.Combine(sandbox.Base, "default", "tcp.json"), new string(' ', 2048));
        await sandbox.RunAsync("settings", "set", "settings.maxFileSize", "1KiB");

        CliResult list = await sandbox.RunAsync("settings", "list", "--scope", "profile", "--group", "tcp");

        await Assert.That(list.Exit).IsEqualTo(0);
        await Assert.That(list.Error).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
    }

    #endregion
}
