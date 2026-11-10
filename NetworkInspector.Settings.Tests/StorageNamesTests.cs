// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>Storage names, sidecar files, device names, and the profile name rule.</summary>
internal sealed class StorageNamesTests
{
    #region ToStorageName

    [Test]
    public async Task NamesUpToTheDirectLengthAreTheirOwnStorageName()
    {
        string one = "a";
        string long119 = new('b', 119);
        string long120 = new('c', 120);

        await Assert.That(StorageNames.ToStorageName(one)).IsEqualTo(one);
        await Assert.That(StorageNames.ToStorageName(long119)).IsEqualTo(long119);
        await Assert.That(StorageNames.ToStorageName(long120)).IsEqualTo(long120);
    }

    [Test]
    public async Task LongerNamesAreMappedToAPrefixAndASixteenDigitHash()
    {
        string long121 = new('d', 121);
        string long256 = new string('e', 99) + " .x" + new string('f', 154);

        string mapped121 = StorageNames.ToStorageName(long121);
        string mapped256 = StorageNames.ToStorageName(long256);

        await Assert.That(mapped121.Length).IsEqualTo(100 + 1 + 16);
        await Assert.That(mapped121[..100]).IsEqualTo(new string('d', 100));
        await Assert.That(mapped121[100]).IsEqualTo('~');
        await Assert.That(StorageNames.IsMapped(mapped121)).IsTrue();
        await Assert.That(StorageNames.IsMapped(mapped256)).IsTrue();

        // Trailing spaces and dots of the cut prefix are removed so the folder name stays valid on Windows.
        await Assert.That(mapped256.Length).IsEqualTo(99 + 1 + 16);
        await Assert.That(StorageNames.IsMapped(long121)).IsFalse();
    }

    [Test]
    public async Task TheMappingIsStableAndIgnoresCase()
    {
        string lower = new string('a', 130);
        string upper = new string('A', 130);

        await Assert.That(StorageNames.ToStorageName(lower)).IsEqualTo(StorageNames.ToStorageName(lower));
        await Assert.That(StorageNames.ToStorageName(lower)).IsEqualTo(StorageNames.ToStorageName(upper));
        await Assert.That(StorageNames.ToStorageName(lower + "x")).IsNotEqualTo(StorageNames.ToStorageName(lower + "y"));
    }

    [Test]
    public async Task DeviceNamesAreMappedAndOrdinaryNamesThatLookSimilarAreNot()
    {
        await Assert.That(StorageNames.IsMapped(StorageNames.ToStorageName("CON"))).IsTrue();
        await Assert.That(StorageNames.IsMapped(StorageNames.ToStorageName("con.backup"))).IsTrue();
        await Assert.That(StorageNames.IsMapped(StorageNames.ToStorageName("LPT9"))).IsTrue();
        await Assert.That(StorageNames.ToStorageName("CONSOLE")).IsEqualTo("CONSOLE");
        await Assert.That(StorageNames.ToStorageName("COM10")).IsEqualTo("COM10");
        await Assert.That(StorageNames.IsDeviceName("nul.txt")).IsTrue();
        await Assert.That(StorageNames.IsDeviceName("Com3")).IsTrue();
        await Assert.That(StorageNames.IsDeviceName("COM0")).IsFalse();
    }

    [Test]
    public async Task InvalidLengthsAreRefused()
    {
        await Assert.That(() => StorageNames.ToStorageName(string.Empty)).Throws<ArgumentException>();
        await Assert.That(() => StorageNames.ToStorageName(new string('x', 257))).Throws<ArgumentException>();
        await Assert.That(() => StorageNames.ToStorageName(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Sidecar

    [Test]
    public async Task AMappedNameRoundTripsThroughItsSidecar()
    {
        using SettingsTempFolder folder = new();
        string name = new string('n', 150);
        string storage = StorageNames.ToStorageName(name);

        bool written = StorageNames.TryWriteNameFile(folder.Path, name, out string? problem);
        bool read = StorageNames.TryReadName(folder.Path, storage, out string? back);

        await Assert.That(written).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(read).IsTrue();
        await Assert.That(back).IsEqualTo(name);
        await Assert.That(File.Exists(folder.Combine(storage + StorageNames.NameFileExtension))).IsTrue();
    }

    [Test]
    public async Task ADirectNameWritesNoSidecar()
    {
        using SettingsTempFolder folder = new();

        bool written = StorageNames.TryWriteNameFile(folder.Path, "Ethernet lab", out _);

        await Assert.That(written).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Any()).IsFalse();
    }

    [Test]
    public async Task UnusableSidecarsAreNotNames()
    {
        using SettingsTempFolder folder = new();
        string name = new string('n', 150);
        string storage = StorageNames.ToStorageName(name);
        string sidecar = folder.Combine(storage + StorageNames.NameFileExtension);

        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out _)).IsFalse();

        File.WriteAllText(sidecar, "another name entirely");
        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out _)).IsFalse();

        File.WriteAllText(sidecar, string.Empty);
        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out _)).IsFalse();

        File.WriteAllText(sidecar, new string('n', 1025));
        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out _)).IsFalse();

        File.WriteAllBytes(sidecar, [0xFF, 0xFE, 0xFD]);
        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out _)).IsFalse();
    }

    [Test]
    public async Task ACreatorDoesNotRewriteTheSidecarOfAnExistingNameThatDiffersInCase()
    {
        using SettingsTempFolder folder = new();
        string lower = new('a', 130);
        string upper = new('A', 130);
        StorageNames.TryWriteNameFile(folder.Path, lower, out _);

        bool written = StorageNames.TryWriteNameFile(folder.Path, upper, StorageNames.ToStorageName, overwriteCaseOnlyDifference: false, out string? problem);
        bool read = StorageNames.TryReadName(folder.Path, StorageNames.ToStorageName(lower), out string? name);

        await Assert.That(written).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(read).IsTrue();
        await Assert.That(name).IsEqualTo(lower);
    }

    [Test]
    public async Task ARenameRewritesTheSidecarOfAnExistingNameThatDiffersInCase()
    {
        using SettingsTempFolder folder = new();
        string lower = new('a', 130);
        string upper = new('A', 130);
        StorageNames.TryWriteNameFile(folder.Path, lower, out _);

        bool written = StorageNames.TryWriteNameFile(folder.Path, upper, out string? problem);
        StorageNames.TryReadName(folder.Path, StorageNames.ToStorageName(lower), out string? name);

        await Assert.That(written).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(name).IsEqualTo(upper);
    }

    [Test]
    public async Task ASidecarOfAnotherNameWithTheSameStorageNameIsACollision()
    {
        using SettingsTempFolder folder = new();

        // A mapping that sends every name to one storage name forces the case that real hashes make practically impossible.
        Func<string, string> everything = static _ => "collide~0000000000000000";
        bool first = StorageNames.TryWriteNameFile(folder.Path, "First name", everything, overwriteCaseOnlyDifference: true, out string? firstProblem);
        bool second = StorageNames.TryWriteNameFile(folder.Path, "Second name", everything, overwriteCaseOnlyDifference: true, out string? secondProblem);

        await Assert.That(first).IsTrue();
        await Assert.That(firstProblem).IsNull();
        await Assert.That(second).IsFalse();
        await Assert.That(secondProblem).Contains("collides with the existing name 'First name'");
        await Assert.That(File.ReadAllText(folder.Combine("collide~0000000000000000.name"))).IsEqualTo("First name");
    }

    [Test]
    public async Task EightThreadsWritingOneSidecarLeaveOneCompleteFile()
    {
        using SettingsTempFolder folder = new();
        string name = new string('t', 200);
        string storage = StorageNames.ToStorageName(name);
        using Barrier barrier = new(8);

        Task[] tasks = [.. Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 20 + worker * 0; i++)
            {
                StorageNames.TryWriteNameFile(folder.Path, name, out _);
            }
        }))];
        await Task.WhenAll(tasks);

        await Assert.That(StorageNames.TryReadName(folder.Path, storage, out string? back)).IsTrue();
        await Assert.That(back).IsEqualTo(name);
        await Assert.That(Directory.EnumerateFiles(folder.Path).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteNameFileIgnoresAMissingSidecar()
    {
        using SettingsTempFolder folder = new();
        string name = new string('d', 130);
        StorageNames.TryWriteNameFile(folder.Path, name, out _);
        string storage = StorageNames.ToStorageName(name);

        StorageNames.DeleteNameFile(folder.Path, storage);
        StorageNames.DeleteNameFile(folder.Path, storage);

        await Assert.That(Directory.EnumerateFileSystemEntries(folder.Path).Any()).IsFalse();
    }

    #endregion

    #region Profile names

    [Test]
    public async Task ProfileNamesAcceptTheDocumentedForms()
    {
        foreach (string name in new[] { "CAN", "Ethernet lab", "x.y-z_1", new string('p', 256), "application" })
        {
            await Assert.That(ProfileNames.TryValidateSyntax(name, out string? syntaxError)).IsTrue();
            await Assert.That(syntaxError).IsNull();
        }

        foreach (string name in new[] { "CAN", "Ethernet lab", "x.y-z_1", new string('p', 256) })
        {
            await Assert.That(ProfileNames.TryValidate(name, TestLayout.Default, out string? error)).IsTrue();
            await Assert.That(error).IsNull();
        }
    }

    [Test]
    public async Task ProfileNamesRefuseTheReservedAndMalformedOnesWithTheRule()
    {
        (string? Name, string Error)[] cases =
        [
            (null, "a profile name is needed"),
            (string.Empty, "a profile name is needed"),
            (new string('p', 257), "at most 256 characters"),
            ("../x", "letters, digits, space, dot, dash, and underscore only"),
            ("a/b", "letters, digits, space, dot, dash, and underscore only"),
            ("é", "letters, digits, space, dot, dash, and underscore only"),
            (" a", "must not start or end with a space or a dot"),
            ("a.", "must not start or end with a space or a dot"),
            ("..", "must not start or end with a space or a dot"),
            ("NUL", "NUL is reserved by the operating system"),
            ("nul.txt", "nul.txt is reserved by the operating system"),
            ("application", "application is reserved"),
            ("LOGS", "LOGS is reserved"),
            ("plugins-data", "plugins-data is reserved"),
            ("App", "App is reserved"),
        ];

        foreach ((string? name, string error) in cases)
        {
            bool valid = ProfileNames.TryValidate(name, TestLayout.Default, out string? actual);
            await Assert.That(Invariant($"{name}:{valid}:{actual}")).IsEqualTo(Invariant($"{name}:False:{error}"));
        }
    }

    [Test]
    public async Task TheReservedNamesOfNetworkInspectorAreExactlyTheDocumentedOnes()
    {
        await Assert.That(TestLayout.Default.ReservedFolderNames).IsEquivalentTo(
            ["application", "logs", "plugins", "plugins-data", "plugin-scan-cache", "host", "app", "presets"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolvePathUsesTheStorageNameOfTheProfile()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "base");
        string longName = new('x', 200);

        await Assert.That(SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, "Ethernet lab")).IsEqualTo(Path.Combine(basePath, "Ethernet lab"));
        await Assert.That(SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, "application")).IsEqualTo(Path.Combine(basePath, "application"));
        await Assert.That(SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, longName))
            .IsEqualTo(Path.Combine(basePath, StorageNames.ToStorageName(longName)));
    }

    #endregion
}
