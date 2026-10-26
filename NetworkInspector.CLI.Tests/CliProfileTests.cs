// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Profiles in the command line: they are applied to stacks, named by a strict rule, and managed by <c>ni profile</c>.</summary>
internal sealed class CliProfileTests
{
    #region Helpers

    private const string _RelativeSetting = "tcp.relative_sequence_numbers";

    private static string[] _Snapshot(string folder) =>
        !Directory.Exists(folder)
            ? []
            : [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/') + (Directory.Exists(path) ? "/" : File.ReadAllText(path)))
                .Order(StringComparer.Ordinal)];

    #endregion

    #region Applied profiles

    [Test]
    public async Task AProfileWithAbsoluteSequenceNumbersChangesTheExport()
    {
        using CliSandbox sandbox = new();
        string trace = sandbox.Combine("trace.pcapng");
        CliTraceWriter.WriteTcpPcapng(trace, 10);

        CliResult created = await sandbox.RunAsync("profile", "create", "CAN");
        CliResult set = await sandbox.RunAsync("settings", "set", _RelativeSetting, "false", "--profile", "CAN");
        CliResult withProfile = await sandbox.RunAsync("export", trace, "-o", sandbox.Combine("can.json"), "--profile", "CAN");
        CliResult withoutProfile = await sandbox.RunAsync("export", trace, "-o", sandbox.Combine("default.json"));

        await Assert.That(created.Exit).IsEqualTo(0);
        await Assert.That(set.Exit).IsEqualTo(0);
        JsonObject stored = JsonNode.Parse(File.ReadAllText(Path.Combine(sandbox.Base, "CAN", "tcp.json"))) as JsonObject
            ?? throw new InvalidOperationException("tcp.json is not an object.");
        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(stored[_RelativeSetting]?.GetValue<bool>()).IsFalse();
        await Assert.That(withProfile.Exit).IsEqualTo(0);
        await Assert.That(withoutProfile.Exit).IsEqualTo(0);

        // The trace is a SYN followed by 10 data segments; the SYN starts the stream, so relative numbers exist.
        ExportedPackets absolute = ExportedPackets.Read(sandbox.Combine("can.json"));
        ExportedPackets relative = ExportedPackets.Read(sandbox.Combine("default.json"));
        await Assert.That(absolute.Count).IsEqualTo(11);
        await Assert.That(relative.Count).IsEqualTo(11);
        for (int i = 0; i < 11; i++)
        {
            await Assert.That(absolute.Number(i, "tcp.seq")).IsEqualTo(absolute.Number(i, "tcp.seq_raw"));
            await Assert.That(relative.Number(i, "tcp.seq") ?? long.MaxValue).IsLessThan(relative.Number(i, "tcp.seq_raw") ?? 0);
        }
    }

    #endregion

    #region Names

    [Test]
    public async Task ReservedAndInvalidNamesAreRefusedEverywhereWithoutCreatingAnything()
    {
        string longName = new('p', 257);
        (string Name, string Rule)[] cases =
        [
            ("application", "application is reserved"),
            ("LOGS", "LOGS is reserved"),
            ("plugins-data", "plugins-data is reserved"),
            ("App", "App is reserved"),
            ("../x", "letters, digits, space, dot, dash, and underscore only"),
            ("a/b", "letters, digits, space, dot, dash, and underscore only"),
            (" a", "must not start or end with a space or a dot"),
            ("a.", "must not start or end with a space or a dot"),
            ("NUL", "NUL is reserved by the operating system"),
            ("nul.txt", "nul.txt is reserved by the operating system"),
            (longName, "at most 256 characters"),
        ];

        foreach ((string name, string rule) in cases)
        {
            using CliSandbox sandbox = new();
            string trace = sandbox.Combine("trace.pcapng");
            CliTraceWriter.WriteTcpPcapng(trace, 1);
            CliResult[] results =
            [
                await sandbox.RunAsync("export", trace, "-o", sandbox.Combine("out.json"), "--profile", name),
                await sandbox.RunAsync("settings", "get", _RelativeSetting, "--profile", name),
                await sandbox.RunAsync("plugins", "list", "--profile", name),
                await sandbox.RunAsync("profile", "create", name),
            ];

            foreach (CliResult result in results)
            {
                await Assert.That(result.Exit).IsEqualTo(1);
                await Assert.That(result.ErrorMessages.Length).IsEqualTo(1);
                await Assert.That(result.ErrorMessages[0]).Contains($"Profile name '{name}': {rule}.");
            }

            await Assert.That(Directory.Exists(sandbox.Base)).IsFalse();
        }
    }

    [Test]
    public async Task ALongProfileNameWorksEndToEndThroughItsStorageName()
    {
        using CliSandbox sandbox = new();
        string name = (string.Concat(Enumerable.Repeat("ab cd ", 50))[..255] + "x");
        string storage = StorageNames.ToStorageName(name);

        CliResult spaced = await sandbox.RunAsync("profile", "create", "Ethernet lab");
        CliResult spacedSet = await sandbox.RunAsync("settings", "set", _RelativeSetting, "false", "--profile", "Ethernet lab");
        CliResult created = await sandbox.RunAsync("profile", "create", name);
        CliResult set = await sandbox.RunAsync("settings", "set", _RelativeSetting, "false", "--profile", name);
        CliResult get = await sandbox.RunAsync("settings", "get", _RelativeSetting, "--profile", name);
        CliResult list = await sandbox.RunAsync("profile", "list");

        await Assert.That(spaced.Exit).IsEqualTo(0);
        await Assert.That(spacedSet.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "Ethernet lab", "tcp.json"))).IsTrue();
        await Assert.That(Directory.EnumerateFiles(Path.Combine(sandbox.Base), "*.name").Any()).IsTrue();
        await Assert.That(created.Exit).IsEqualTo(0);
        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(storage.Length).IsEqualTo(100 + 1 + 16);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, storage, "tcp.json"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(sandbox.Base, storage + ".name"))).IsEqualTo(name);
        await Assert.That(get.OutLines).IsEquivalentTo(["false"], CollectionOrdering.Matching);
        await Assert.That(list.OutLines).Contains(name);
        foreach (string entry in Directory.EnumerateFileSystemEntries(sandbox.Base, "*", SearchOption.AllDirectories))
        {
            await Assert.That(Path.GetFileName(entry).Length).IsLessThanOrEqualTo(128);
        }
    }

    #endregion

    #region Default and unknown profiles

    [Test]
    public async Task CommandsThatStartTogetherOnAFreshBaseAllGetTheDefaultProfile()
    {
        using CliSandbox sandbox = new();
        const int commands = 8;
        using Barrier barrier = new(commands);
        CliSettingsOptions options = CliSettingsOptions.Defaults with { SettingsPath = sandbox.Base };

        // Eight first uses race to create the profile; one create wins and none of the others may fail because of it.
        Task<(bool Resolved, string Error)>[] tasks = [.. Enumerable.Range(0, commands).Select(index => Task.Run(() =>
        {
            StringWriter error = new();
            barrier.SignalAndWait();
            bool resolved = CliPluginSession.TryResolveProfile(options, new CliIo(new StringWriter(), error), out _, out string profile, out _);
            return (resolved && profile == "default", error.ToString());
        }))];

        (bool Resolved, string Error)[] results = await Task.WhenAll(tasks);

        await Assert.That(results.Count(static result => result.Resolved)).IsEqualTo(commands);
        await Assert.That(results.All(static result => result.Error.Length == 0)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(sandbox.Base, "default"))).IsTrue();
    }

    [Test]
    public async Task TheDefaultProfileIsCreatedOnFirstUseAndAnUnknownNamedProfileIsAnError()
    {
        using CliSandbox sandbox = new();
        string trace = sandbox.Combine("trace.pcapng");
        CliTraceWriter.WriteTcpPcapng(trace, 1);

        CliResult set = await sandbox.RunAsync("settings", "set", _RelativeSetting, "false");
        CliResult get = await sandbox.RunAsync("settings", "get", _RelativeSetting);
        string[] dirsBefore = [.. Directory.EnumerateDirectories(sandbox.Base).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        CliResult other = await sandbox.RunAsync("settings", "get", _RelativeSetting, "--profile", "Other");
        CliResult export = await sandbox.RunAsync("export", trace, "-o", sandbox.Combine("out.json"), "--profile", "Other");

        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "default", "tcp.json"))).IsTrue();
        await Assert.That(get.OutLines).IsEquivalentTo(["false"], CollectionOrdering.Matching);
        await Assert.That(other.Exit).IsEqualTo(1);
        await Assert.That(other.ErrorMessages).IsEquivalentTo(
            ["Error: Profile 'Other' does not exist; create it with ni profile create Other."], CollectionOrdering.Matching);
        await Assert.That(export.Exit).IsEqualTo(1);
        await Assert.That(export.ErrorMessages).IsEquivalentTo(
            ["Error: Profile 'Other' does not exist; create it with ni profile create Other."], CollectionOrdering.Matching);
        await Assert.That(Directory.Exists(Path.Combine(sandbox.Base, "Other"))).IsFalse();
        await Assert.That(File.Exists(sandbox.Combine("out.json"))).IsFalse();

        // The refused runs created no profile: the folders of the base are the ones from before.
        string[] dirsAfter = [.. Directory.EnumerateDirectories(sandbox.Base).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
        await Assert.That(dirsAfter).IsEquivalentTo(dirsBefore, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AGroupFileDirectlyInTheBaseIsNotAProfile()
    {
        using CliSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Base);
        File.WriteAllText(Path.Combine(sandbox.Base, "tcp.json"), """{"tcp.max_tracked_streams": 5}""");

        CliResult get = await sandbox.RunAsync("settings", "get", "tcp.max_tracked_streams");

        await Assert.That(get.Exit).IsEqualTo(0);
        await Assert.That(get.OutLines).IsEquivalentTo(["10000"], CollectionOrdering.Matching);
    }

    #endregion

    #region Profile commands

    [Test]
    public async Task ProfilesCanBeCreatedCopiedRenamedAndDeleted()
    {
        using CliSandbox sandbox = new();

        CliResult can = await sandbox.RunAsync("profile", "create", "CAN");
        CliResult set = await sandbox.RunAsync("settings", "set", _RelativeSetting, "false", "--profile", "CAN");
        CliResult lab = await sandbox.RunAsync("profile", "create", "Lab", "--copy-from", "CAN");
        CliResult labGet = await sandbox.RunAsync("settings", "get", _RelativeSetting, "--profile", "Lab");
        CliResult defaultSet = await sandbox.RunAsync("settings", "get", _RelativeSetting);
        CliResult list = await sandbox.RunAsync("profile", "list");
        CliResult rename = await sandbox.RunAsync("profile", "rename", "Lab", "Bench");
        CliResult afterRename = await sandbox.RunAsync("profile", "list");
        CliResult benchGet = await sandbox.RunAsync("settings", "get", _RelativeSetting, "--profile", "Bench");
        CliResult delete = await sandbox.RunAsync("profile", "delete", "Bench");
        CliResult afterDelete = await sandbox.RunAsync("profile", "list");

        await Assert.That(can.OutLines).IsEquivalentTo(["Profile 'CAN' created."], CollectionOrdering.Matching);
        await Assert.That(set.Exit).IsEqualTo(0);
        await Assert.That(lab.Exit).IsEqualTo(0);
        await Assert.That(labGet.OutLines).IsEquivalentTo(["false"], CollectionOrdering.Matching);
        await Assert.That(defaultSet.Exit).IsEqualTo(0);
        await Assert.That(list.OutLines).IsEquivalentTo(["CAN", "default", "Lab"], CollectionOrdering.Matching);
        await Assert.That(rename.OutLines).IsEquivalentTo(["Profile 'Lab' renamed to 'Bench'."], CollectionOrdering.Matching);
        await Assert.That(afterRename.OutLines).IsEquivalentTo(["Bench", "CAN", "default"], CollectionOrdering.Matching);
        await Assert.That(benchGet.OutLines).IsEquivalentTo(["false"], CollectionOrdering.Matching);
        await Assert.That(delete.OutLines).IsEquivalentTo(["Profile 'Bench' deleted."], CollectionOrdering.Matching);
        await Assert.That(afterDelete.OutLines).IsEquivalentTo(["CAN", "default"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProfileRefusalsAreOneLineAndLeaveTheBaseUnchanged()
    {
        using CliSandbox sandbox = new();
        await sandbox.RunAsync("profile", "create", "CAN");
        string[] before = _Snapshot(sandbox.Base);

        CliResult again = await sandbox.RunAsync("profile", "create", "CAN");
        CliResult renameMissing = await sandbox.RunAsync("profile", "rename", "Nope", "X");
        CliResult deleteMissing = await sandbox.RunAsync("profile", "delete", "Nope");
        CliResult copyMissing = await sandbox.RunAsync("profile", "create", "Lab", "--copy-from", "Nope");
        CliResult deleteLast = await sandbox.RunAsync("profile", "delete", "CAN");
        CliResult frob = await sandbox.RunAsync("profile", "frob");

        await Assert.That(again.ErrorLines).IsEquivalentTo(["Error: Profile 'CAN' already exists."], CollectionOrdering.Matching);
        await Assert.That(renameMissing.ErrorLines).IsEquivalentTo(["Error: Profile 'Nope' does not exist."], CollectionOrdering.Matching);
        await Assert.That(deleteMissing.ErrorLines).IsEquivalentTo(["Error: Profile 'Nope' does not exist."], CollectionOrdering.Matching);
        await Assert.That(copyMissing.ErrorLines).IsEquivalentTo(["Error: Profile 'Nope' does not exist."], CollectionOrdering.Matching);
        await Assert.That(deleteLast.ErrorLines).IsEquivalentTo(
            ["Error: Profile 'CAN' is the last profile and cannot be deleted."], CollectionOrdering.Matching);
        foreach (CliResult result in new[] { again, renameMissing, deleteMissing, copyMissing, deleteLast, frob })
        {
            await Assert.That(result.Exit).IsEqualTo(1);
        }

        await Assert.That(frob.Error).Contains("Usage: ni profile");
        await Assert.That(_Snapshot(sandbox.Base)).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AFileSystemFailureOfAProfileCommandIsARuntimeErrorAndARuleRefusalStaysAUsageError()
    {
        using CliSandbox sandbox = new();
        string blocker = sandbox.Combine("blocker");
        File.WriteAllText(blocker, "a file where the settings base folder should be");

        // The base cannot be created below a file: an I/O failure of the environment, exit code 3 like every other runtime failure.
        CliResult failed = await CliSandbox.RunWithoutBaseAsync(["profile", "create", "Work", "--settings-path", blocker], CancellationToken.None);
        CliResult first = await sandbox.RunAsync("profile", "create", "Work");
        CliResult duplicate = await sandbox.RunAsync("profile", "create", "work");

        await Assert.That(failed.Exit).IsEqualTo(3);
        await Assert.That(failed.ErrorMessages.Length).IsEqualTo(1);
        await Assert.That(failed.ErrorMessages[0]).Contains("cannot be created");
        await Assert.That(first.Exit).IsEqualTo(0);
        await Assert.That(duplicate.Exit).IsEqualTo(1);
    }

    [Test]
    public async Task ProfileCommandsLoadNoPlugins()
    {
        using CliSandbox sandbox = new();

        CliResult created = await sandbox.RunAsync("profile", "create", "CAN");

        await Assert.That(created.Exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(sandbox.Base, "logs"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(sandbox.Base, "plugin-scan-cache.json"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(sandbox.Base, "application"))).IsFalse();
    }

    #endregion
}
