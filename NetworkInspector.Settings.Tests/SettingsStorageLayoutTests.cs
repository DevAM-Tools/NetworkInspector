// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>The storage layout of an application: the names it writes, the names it refuses, and the folders it resolves.</summary>
internal sealed class SettingsStorageLayoutTests
{
    #region Helpers

    /// <summary>A second layout, for tests that compare two layouts.</summary>
    private static SettingsStorageLayout _Beta { get; } =
        SettingsStorageLayout.Create("Beta", ["plugins"], ".beta.lock", ".beta-owned");

    /// <summary>A layout for tests with names that are not the defaults of the library.</summary>
    private static SettingsStorageLayout _Alpha { get; } =
        SettingsStorageLayout.Create("Alpha", ["logs", "cache"], ".alpha.lock", ".alpha-owned");

    /// <summary>Saves one changed setting of the group <c>demo</c> into <paramref name="folder"/> with the given layout.</summary>
    /// <param name="folder">The storage folder.</param>
    /// <param name="layout">The layout of the manager.</param>
    private static void _SaveOneValue(string folder, SettingsStorageLayout layout)
    {
        using SettingsManager manager = new(folder, layout);
        Setting setting = Setting.String("demo.name", "Name", "demo", "default");
        manager.RegisterSetting(setting);
        manager.Load();
        setting.SetPendingValue(SettingValue.String("changed"));
        manager.ApplyChanges();
        manager.Save();
    }

    /// <summary>Lists the file names of a folder, sorted, for one comparison.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The file names without the folder path.</returns>
    private static string[] _FileNames(string folder) =>
        [.. Directory.EnumerateFiles(folder).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    #endregion

    #region Names of the files

    [Test]
    public async Task AManagerWritesTheLockAndTheManifestNamesOfItsLayoutAndNoOthers()
    {
        using SettingsTempFolder alpha = new();
        using SettingsTempFolder beta = new();

        _SaveOneValue(alpha.Path, _Alpha);
        _SaveOneValue(beta.Path, _Beta);

        await Assert.That(_FileNames(alpha.Path)).IsEquivalentTo([".alpha-owned", ".alpha.lock", "demo.json"]);
        await Assert.That(_FileNames(beta.Path)).IsEquivalentTo([".beta-owned", ".beta.lock", "demo.json"]);
    }

    [Test]
    public async Task ATrashFileNamedLikeTheDefaultNamesOfTheLibraryIsLeftAlone()
    {
        using SettingsTempFolder folder = new();
        folder.Write(SettingsStorageLayout.DefaultOwnedManifestFileName, "[\"stale.json\"]");
        folder.Write("stale.json", "{}");

        _SaveOneValue(folder.Path, _Alpha);

        // Alpha owns nothing that the default manifest lists, so it neither reads nor deletes it.
        await Assert.That(File.Exists(folder.Combine("stale.json"))).IsTrue();
        await Assert.That(File.ReadAllText(folder.Combine(SettingsStorageLayout.DefaultOwnedManifestFileName))).IsEqualTo("[\"stale.json\"]");
    }

    [Test]
    public async Task TheProfileCopySkipsTheLockFileOfItsOwnLayoutAndKeepsAFileNamedLikeAnotherLayoutsLockFile()
    {
        using SettingsTempFolder folder = new();
        Directory.CreateDirectory(folder.Combine("source"));
        folder.Write(Path.Combine("source", ".alpha.lock"), string.Empty);
        folder.Write(Path.Combine("source", ".beta.lock"), "kept");
        folder.Write(Path.Combine("source", "demo.json"), "{}");

        bool created = SettingsProfiles.TryCreate(folder.Path, _Alpha, "copy", "source", out string? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(_FileNames(folder.Combine("copy"))).IsEquivalentTo([".beta.lock", "demo.json"]);
    }

    #endregion

    #region Invalid layouts

    [Test]
    [Arguments("", "applicationName")]
    [Arguments("   ", "applicationName")]
    [Arguments("a/b", "applicationName")]
    [Arguments("a\\b", "applicationName")]
    [Arguments("a..b", "applicationName")]
    [Arguments("CON", "applicationName")]
    [Arguments("name.", "applicationName")]
    public async Task AnInvalidApplicationNameIsRefusedAndTheMessageNamesTheArgument(string applicationName, string argument)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create(applicationName, []));

        await Assert.That(exception.ParamName).IsEqualTo(argument);
    }

    [Test]
    public async Task AnApplicationNameOfMoreThanOneHundredTwentyCharactersIsRefusedAndOneHundredTwentyIsAccepted()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create(new string('a', 121), []));
        SettingsStorageLayout accepted = SettingsStorageLayout.Create(new string('a', 120), []);

        await Assert.That(exception.ParamName).IsEqualTo("applicationName");
        await Assert.That(accepted.ApplicationName.Length).IsEqualTo(120);
    }

    [Test]
    [Arguments("alpha.lock", "lockFileName")]
    [Arguments(".a/b", "lockFileName")]
    [Arguments(".a..b", "lockFileName")]
    [Arguments(".settings.json", "lockFileName")]
    [Arguments(".settings.JSON", "lockFileName")]
    public async Task AnInvalidLockFileNameIsRefused(string lockFileName, string argument)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", [], lockFileName));

        await Assert.That(exception.ParamName).IsEqualTo(argument);
    }

    [Test]
    [Arguments("alpha-owned", "ownedManifestFileName")]
    [Arguments(".owned.json", "ownedManifestFileName")]
    [Arguments(".a\\b", "ownedManifestFileName")]
    public async Task AnInvalidManifestFileNameIsRefused(string manifestFileName, string argument)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", [], ".alpha.lock", manifestFileName));

        await Assert.That(exception.ParamName).IsEqualTo(argument);
    }

    [Test]
    public async Task TheLockFileAndTheManifestNeedTwoDifferentNames()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", [], ".same", ".SAME"));

        await Assert.That(exception.ParamName).IsEqualTo("ownedManifestFileName");
    }

    [Test]
    [Arguments("bad/name")]
    [Arguments("bad:name")]
    [Arguments(" lead")]
    public async Task AReservedNameThatNoProfileCouldHaveIsRefused(string reserved)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", [reserved]));

        await Assert.That(exception.ParamName).IsEqualTo("reservedFolderNames");
    }

    [Test]
    public async Task TheSameReservedNameTwiceIsRefusedIgnoringCase()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", ["logs", "LOGS"]));

        await Assert.That(exception.ParamName).IsEqualTo("reservedFolderNames");
        await Assert.That(exception.Message).Contains("LOGS");
    }

    [Test]
    public async Task ApplicationAsAReservedNameIsAcceptedAndNotDoubledButTwiceIsRefused()
    {
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["Application", "logs"]);
        ArgumentException twice = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", ["application", "APPLICATION"]));

        await Assert.That(layout.ReservedFolderNames.Count(static name => string.Equals(name, "application", StringComparison.OrdinalIgnoreCase)))
            .IsEqualTo(1);
        await Assert.That(layout.ReservedFolderNames).Contains("logs");
        await Assert.That(twice.ParamName).IsEqualTo("reservedFolderNames");
    }

    [Test]
    public async Task ANullListIsRefusedWithTheNullExceptionAndANullElementWithTheArgumentException()
    {
        await Assert.That(() => SettingsStorageLayout.Create("Alpha", null!)).Throws<ArgumentNullException>(); // null! is the input under test

        string?[] withNull = ["logs", null];
        // The elements are the input under test.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SettingsStorageLayout.Create("Alpha", withNull!));

        await Assert.That(exception.Message).Contains("index 1");
    }

    [Test]
    public async Task TheDefaultFileNamesAreUsedWhenALayoutChoosesNone()
    {
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", []);

        await Assert.That(layout.LockFileName).IsEqualTo(SettingsStorageLayout.DefaultLockFileName);
        await Assert.That(layout.OwnedManifestFileName).IsEqualTo(SettingsStorageLayout.DefaultOwnedManifestFileName);
        await Assert.That(layout.ReservedFolderNames).IsEquivalentTo(["application"]);
    }

    [Test]
    public async Task TwoLayoutsWithTheSameNamesAreEqual()
    {
        SettingsStorageLayout one = SettingsStorageLayout.Create("Alpha", ["logs"], ".alpha.lock", ".alpha-owned");
        SettingsStorageLayout two = SettingsStorageLayout.Create("Alpha", ["logs"], ".alpha.lock", ".alpha-owned");

        await Assert.That(one).IsEqualTo(two);
        await Assert.That(one.GetHashCode()).IsEqualTo(two.GetHashCode());
        await Assert.That(one).IsNotEqualTo(_Alpha);
    }

    [Test]
    public async Task APersistingManagerCannotBeCreatedWithoutALayout()
    {
        await Assert.That(() => new SettingsManager("folder", null!)).Throws<ArgumentNullException>(); // null! is the input under test
        // null! is the input under test
        await Assert.That(() => new SettingsManager("folder", null!, SettingsLimitValues.Default)).Throws<ArgumentNullException>();
        using SettingsManager inMemory = new();

        await Assert.That(inMemory.Layout).IsNull();
        await Assert.That(inMemory.StoragePath).IsNull();
    }

    #endregion

    #region Reserved profile names

    [Test]
    [Arguments("Logs")]
    [Arguments("LOGS")]
    [Arguments("logs")]
    [Arguments("application")]
    [Arguments("PRESETS")]
    public async Task AReservedFolderNameIsNoProfileInAnyLetterCase(string name)
    {
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "presets"]);

        bool valid = ProfileNames.TryValidate(name, layout, out string? error);

        await Assert.That(valid).IsFalse();
        await Assert.That(error).IsEqualTo(name + " is reserved");
    }

    [Test]
    public async Task AProfileNameThatTheLayoutDoesNotReserveIsAccepted()
    {
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "presets"]);

        await Assert.That(ProfileNames.TryValidate("work", layout, out string? error)).IsTrue();
        await Assert.That(error).IsNull();

        // "plugins" is reserved by NetworkInspector's layout and not by this one.
        await Assert.That(ProfileNames.TryValidate("plugins", layout, out _)).IsTrue();
    }

    [Test]
    public async Task ALayoutWithoutExtraNamesStillReservesTheApplicationFolder()
    {
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", []);

        await Assert.That(ProfileNames.TryValidate("Application", layout, out string? error)).IsFalse();
        await Assert.That(error).IsEqualTo("Application is reserved");
    }

    [Test]
    public async Task CreatingAndRenamingToAReservedNameIsRefusedWithTheNameInTheMessage()
    {
        using SettingsTempFolder folder = new();
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "presets"]);
        SettingsProfiles.TryCreate(folder.Path, layout, "work", null, out _);

        bool created = SettingsProfiles.TryCreate(folder.Path, layout, "Logs", null, out string? createError);
        bool renamed = SettingsProfiles.TryRename(folder.Path, layout, "work", "PRESETS", out string? renameError);

        await Assert.That(created).IsFalse();
        await Assert.That(createError).IsEqualTo("Profile name 'Logs': Logs is reserved.");
        await Assert.That(renamed).IsFalse();
        await Assert.That(renameError).IsEqualTo("Profile name 'PRESETS': PRESETS is reserved.");
        await Assert.That(Directory.Exists(folder.Combine("Logs"))).IsFalse();
    }

    [Test]
    public async Task TheListShowsNeitherReservedFoldersNorHiddenWorkFolders()
    {
        using SettingsTempFolder folder = new();
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "presets"]);
        Directory.CreateDirectory(folder.Combine("work"));
        Directory.CreateDirectory(folder.Combine("logs"));
        Directory.CreateDirectory(folder.Combine("Presets"));
        Directory.CreateDirectory(folder.Combine("application"));
        Directory.CreateDirectory(folder.Combine(".work-1234"));

        IReadOnlyList<string> profiles = SettingsProfiles.List(folder.Path, layout);

        await Assert.That(profiles).IsEquivalentTo(["work"]);
    }

    [Test]
    public async Task AFolderThatIsReservedByOneLayoutIsAProfileForAnother()
    {
        using SettingsTempFolder folder = new();
        Directory.CreateDirectory(folder.Combine("plugins"));

        await Assert.That(SettingsProfiles.List(folder.Path, _Beta)).IsEmpty();
        await Assert.That(SettingsProfiles.List(folder.Path, _Alpha)).IsEquivalentTo(["plugins"]);
    }

    #endregion

    #region Default base folder

    [Test]
    public async Task WithoutAPathTheApplicationDataFolderPlusTheApplicationNameIsTheBase()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alpha");

        await Assert.That(SettingsManagerFactory.ResolvePath(_Alpha)).IsEqualTo(expected);
    }

    [Test]
    public async Task AnExplicitPathWinsAndAProfileAddsItsStorageNameAsSubFolder()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "alpha-base");

        await Assert.That(SettingsManagerFactory.ResolvePath(_Alpha, basePath)).IsEqualTo(basePath);
        await Assert.That(SettingsManagerFactory.ResolvePath(_Alpha, basePath, "work")).IsEqualTo(Path.Combine(basePath, "work"));
    }

    [Test]
    public async Task ALongProfileNameMapsToTheHashedStorageName()
    {
        string basePath = Path.Combine(Path.GetTempPath(), "alpha-base");
        string longName = new('x', 200);

        string path = SettingsManagerFactory.ResolvePath(_Alpha, basePath, longName);

        await Assert.That(path).IsEqualTo(Path.Combine(basePath, StorageNames.ToStorageName(longName)));
        await Assert.That(Path.GetFileName(path)).IsNotEqualTo(longName);
    }

    [Test]
    public async Task TwoLayoutsWithDifferentApplicationNamesResolveToDifferentFolders()
    {
        SettingsStorageLayout beta = SettingsStorageLayout.Create("Beta", []);

        await Assert.That(SettingsManagerFactory.ResolvePath(_Alpha)).IsNotEqualTo(SettingsManagerFactory.ResolvePath(beta));
    }

    [Test]
    public async Task TheFactoryRefusesANullLayoutAndPassesTheLayoutToTheManager()
    {
        using SettingsTempFolder folder = new();

        await Assert.That(() => SettingsManagerFactory.ResolvePath(null!)).Throws<ArgumentNullException>(); // null! is the input under test

        using SettingsManager manager = SettingsManagerFactory.Create(_Alpha, folder.Path, "work");

        await Assert.That(manager.Layout).IsEqualTo(_Alpha);
        await Assert.That(manager.StoragePath).IsEqualTo(Path.Combine(folder.Path, "work"));
    }

    #endregion
}
