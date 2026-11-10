// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// Tests for <see cref="SettingsManager"/> — registration, querying, groups,
/// dirty tracking, bulk operations, and orphaned value recovery.
/// </summary>
internal sealed class SettingsManagerTests
{
    // === Registration ===

    [Test]
    public async Task RegisterSettingBoolSettingSucceeds()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", true);
        SettingRegistrationResult result = mgr.RegisterSetting(s);
        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.NoPersistedValue);
        await Assert.That(result.IsDefault).IsTrue();
    }

    [Test]
    public async Task RegisterSettingDuplicateThrows()
    {
        using SettingsManager mgr = new();
        Setting s1 = Setting.Bool("test.flag", "Flag", "test", true);
        Setting s2 = Setting.Bool("test.flag", "Flag 2", "test", false);
        mgr.RegisterSetting(s1);
        DuplicateNameSettingsException ex =
            Assert.Throws<DuplicateNameSettingsException>(() => mgr.RegisterSetting(s2));
        await Assert.That(ex.Name).IsEqualTo("test.flag");
    }

    [Test]
    public async Task RegisterSettingInvalidNameThrows()
    {
        using SettingsManager mgr = new();
        // The Setting constructor validates names eagerly, so the exception is thrown
        // when creating the Setting, before RegisterSetting is even called.
        InvalidNameSettingsException ex = Assert.Throws<InvalidNameSettingsException>(
            () => mgr.RegisterSetting(Setting.Bool("invalid name!", "Flag", "test", true)));
        await Assert.That(ex.Name).IsEqualTo("invalid name!");
    }

    [Test]
    public void RegisterSettingNullThrowsArgumentNullException()
    {
        using SettingsManager mgr = new();
        _ = Assert.Throws<ArgumentNullException>(() => mgr.RegisterSetting(null!));
    }

    [Test]
    public void RegisterSettingInvalidUiNameThrows()
    {
        using SettingsManager mgr = new();
        // The Setting constructor validates UI names eagerly.
        _ = Assert.Throws<InvalidUiNameSettingsException>(
            () => mgr.RegisterSetting(Setting.Bool("test.flag", "Flag\n", "test", true)));
    }

    // === Querying ===

    [Test]
    public async Task GetSettingReturnsRegisteredSetting()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", true);
        mgr.RegisterSetting(s);
        Setting? found = mgr.GetSetting("test.flag");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Name).IsEqualTo("test.flag");
    }

    [Test]
    public async Task GetSettingNotRegisteredReturnsNull()
    {
        using SettingsManager mgr = new();
        Setting? found = mgr.GetSetting("nonexistent");
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task AllSettingsReturnsAllRegistered()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.a", "A", "test", true));
        mgr.RegisterSetting(Setting.Bool("test.b", "B", "test", false));
        IReadOnlyList<Setting> all = mgr.AllSettings;
        await Assert.That(all.Count).IsEqualTo(2);
    }

    // === Groups ===

    [Test]
    public async Task RegisterGroupExplicitSucceeds()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("network", "Network Settings");
        SettingGroup? group = mgr.GetGroup("network");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task RegisterGroupDuplicateThrows()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("network", "Network Settings");
        DuplicateNameSettingsException ex =
            Assert.Throws<DuplicateNameSettingsException>(() => mgr.RegisterGroup("network", "Network 2"));
        await Assert.That(ex.Name).IsEqualTo("network");
    }

    [Test]
    public async Task RegisterGroupUppercaseNameThrows()
    {
        // Group names must be lowercase — uppercase letters are rejected.
        using SettingsManager mgr = new();
        _ = Assert.Throws<InvalidNameSettingsException>(() => mgr.RegisterGroup("MyGroup", "My Group"));
    }

    [Test]
    public async Task RegisterGroupLowercaseNameSucceeds()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("my.group", "My Group");
        SettingGroup? group = mgr.GetGroup("my.group");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task RegisterSettingAutoCreatesGroup()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "mygroup", true));
        SettingGroup? group = mgr.GetGroup("mygroup");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task AllGroupsListsGroupNames()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "groupa", true));
        mgr.RegisterSetting(Setting.Bool("b.y", "Y", "groupb", false));
        IReadOnlyList<string> groups = mgr.AllGroups;
        await Assert.That(groups.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSettingsInGroupReturnsCorrectSettings()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g1", true));
        mgr.RegisterSetting(Setting.Bool("b.y", "Y", "g2", false));
        mgr.RegisterSetting(Setting.Bool("a.z", "Z", "g1", true));
        IReadOnlyList<Setting> inG1 = mgr.GetSettingsInGroup("g1");
        await Assert.That(inG1.Count).IsEqualTo(2);
    }

    // === Dirty Tracking & Bulk Operations ===

    [Test]
    public async Task HasDirtySettingsInitiallyFalse()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));
        await Assert.That(mgr.HasDirtySettings).IsFalse();
    }

    [Test]
    public async Task HasDirtySettingsTrueAfterPendingChange()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));
        await Assert.That(mgr.HasDirtySettings).IsTrue();
    }

    [Test]
    public async Task ApplyChangesReturnsChangedNames()
    {
        using SettingsManager mgr = new();
        Setting s1 = Setting.Bool("test.a", "A", "test", false);
        Setting s2 = Setting.Bool("test.b", "B", "test", true);
        mgr.RegisterSetting(s1);
        mgr.RegisterSetting(s2);

        s1.SetPendingValue(SettingValue.Bool(true));
        // s2 unchanged
        IReadOnlyList<string> changed = mgr.ApplyChanges();
        await Assert.That(changed.Count).IsEqualTo(1);
        await Assert.That(changed[0]).IsEqualTo("test.a");
        s1.Value.TryGetAsBool(out bool s1Bool);
        await Assert.That(s1Bool).IsTrue();
        await Assert.That(mgr.HasDirtySettings).IsFalse();
    }

    [Test]
    public async Task ResetChangesDiscardsAllPending()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.a", "A", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));
        await Assert.That(mgr.HasDirtySettings).IsTrue();

        mgr.ResetChanges();
        await Assert.That(mgr.HasDirtySettings).IsFalse();
        s.PendingValue.TryGetAsBool(out bool pendingBool);
        await Assert.That(pendingBool).IsFalse();
    }

    [Test]
    public async Task ResetAllToDefaultsResetsBothValues()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.a", "A", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));
        _ = s.Apply(); // current = true
        s.SetPendingValue(SettingValue.Bool(false)); // pending = false, current = true

        mgr.ResetAllToDefaults();
        s.Value.TryGetAsBool(out bool valBool);
        s.PendingValue.TryGetAsBool(out bool pendBool);
        await Assert.That(valBool).IsFalse(); // back to default
        await Assert.That(pendBool).IsFalse();
    }

    // === Default Group ===

    [Test]
    public async Task DefaultGroupEmptyStringName()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", SettingGroup.DefaultGroupName, true);
        mgr.RegisterSetting(s);
        SettingGroup? group = mgr.GetGroup(SettingGroup.DefaultGroupName);
        await Assert.That(group).IsNotNull();
    }

    // === Save without StoragePath ===

    [Test]
    public void SaveNoStoragePathThrows()
    {
        using SettingsManager mgr = new();
        Assert.Throws<PersistenceSettingsException>(() => mgr.Save());
    }

    // === Load without StoragePath ===

    [Test]
    public void LoadNoStoragePathThrows()
    {
        using SettingsManager mgr = new();
        _ = Assert.Throws<PersistenceSettingsException>(() => mgr.Load());
    }

    // === Load warnings ===

    [Test]
    public async Task LoadInvalidGroupNameReturnsWarningAndSkipsFile()
    {
        // A JSON file whose name contains uppercase letters produces an InvalidGroupName warning
        // and its contents are not loaded.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            // Write a file with uppercase group name "MyGroup.json" containing a valid setting
            await File.WriteAllTextAsync(Path.Combine(dir, "MyGroup.json"), """{"test.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Bool("test.flag", "Flag", "mygroup", false);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupName);
            await Assert.That(warnings[0].GroupName).IsEqualTo("MyGroup");
            await Assert.That(warnings[0].SettingName).IsEqualTo(string.Empty);

            // Setting should still have its default value because the file was skipped
            s.Value.TryGetAsBool(out bool skippedVal);
            await Assert.That(skippedVal).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadTypeMismatchReturnsWarning()
    {
        // A JSON value with an incompatible type (boolean stored for a numeric setting)
        // produces a TypeMismatch warning and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            // "default.json" maps to the default group (empty string)
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.count": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64("test.count", "Count", string.Empty, 42);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            await Assert.That(warnings[0].SettingName).IsEqualTo("test.count");

            // Setting keeps its default
            s.Value.TryGetAsU64(out ulong countVal);
            await Assert.That(countVal).IsEqualTo(42UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadOutOfRangeReturnsWarning()
    {
        // A numeric value from JSON that violates the min/max constraint
        // produces an OutOfRange warning and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.port": 99999}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64("test.port", "Port", string.Empty, 8080, min: 1, max: 65535);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
            await Assert.That(warnings[0].SettingName).IsEqualTo("test.port");

            // Setting keeps its default
            s.Value.TryGetAsU64(out ulong portVal);
            await Assert.That(portVal).IsEqualTo(8080UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadValidSettingsNoWarnings()
    {
        // A well-formed settings file produces no warnings.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Bool("test.flag", "Flag", string.Empty, false);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings).IsEmpty();
            s.Value.TryGetAsBool(out bool loadedBool);
            await Assert.That(loadedBool).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // === DeserializationError ===

    [Test]
    public async Task LoadInvalidBase64ForBytesSettingReturnsDeserializationErrorWarning()
    {
        // A JSON string value for a Bytes setting that is not valid base64
        // must produce a DeserializationError warning (not TypeMismatch),
        // and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            // "!!!" is not valid base64
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.data": "!!!not-valid-base64!!!"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Bytes("test.data", "Data", string.Empty, []);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.DeserializationError);
            await Assert.That(warnings[0].SettingName).IsEqualTo("test.data");

            // Setting keeps its default (empty bytes)
            bool bytesOk = s.Value.TryGetAsBytes(out byte[] defaultBytes);
            await Assert.That(bytesOk).IsTrue();
            await Assert.That(defaultBytes.Length).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadWrongJsonTypeForBytesSettingReturnsTypeMismatch()
    {
        // A JSON number value for a Bytes setting is a TypeMismatch (not DeserializationError)
        // because the JSON type itself is wrong, not the content.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.data": 42}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Bytes("test.data", "Data", string.Empty, []);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            await Assert.That(warnings[0].SettingName).IsEqualTo("test.data");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadValidBase64ForBytesSettingLoadsCorrectly()
    {
        // A valid base64-encoded string for a Bytes setting loads without warnings.
        byte[] expected = [0xDE, 0xAD, 0xBE, 0xEF];
        string base64 = Convert.ToBase64String(expected);

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                Invariant($$"""{"test.data": "{{base64}}"}""")).ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Bytes("test.data", "Data", string.Empty, []);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings).IsEmpty();
            bool loadOk = s.Value.TryGetAsBytes(out byte[] value);
            await Assert.That(loadOk).IsTrue();
            await Assert.That(value.Length).IsEqualTo(expected.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                await Assert.That(value[i]).IsEqualTo(expected[i]);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // === GetBytesSetting / GetEnumSetting ===

    [Test]
    public async Task GetBytesSettingReturnsCorrectValue()
    {
        using SettingsManager mgr = new();
        byte[] expected = [1, 2, 3];
        Setting s = Setting.Bytes("test.data", "Data", "test", expected);
        mgr.RegisterSetting(s);

        byte[]? result = mgr.GetBytesSetting("test.data");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(result[i]).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task GetBytesSettingUnregisteredNameReturnsNull()
    {
        using SettingsManager mgr = new();
        byte[]? result = mgr.GetBytesSetting("nonexistent");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetEnumSettingReturnsCorrectValue()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Enum("test.level", "Level", "test", 1UL,
            [("Low", 0UL), ("Medium", 1UL), ("High", 2UL)]);
        mgr.RegisterSetting(s);

        (string Name, ulong Value)? result = mgr.GetEnumSetting("test.level");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
        await Assert.That(result!.Value.Value).IsEqualTo(1UL);
    }

    [Test]
    public async Task GetEnumSettingUnregisteredNameReturnsNull()
    {
        using SettingsManager mgr = new();
        (string Name, ulong Value)? result = mgr.GetEnumSetting("nonexistent");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ReadOnlyGetBytesSettingReturnsDefensiveCopy()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bytes("test.data", "Data", "test", [1, 2, 3]));

        byte[]? result = mgr.ReadOnly.GetBytesSetting("test.data");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(3);
        await Assert.That(result[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task ReadOnlyGetEnumSettingReturnsNameAndValue()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Enum("test.level", "Level", "test", 1UL, [("Low", 0UL), ("Medium", 1UL)]));

        (string Name, ulong Value)? result = mgr.ReadOnly.GetEnumSetting("test.level");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
        await Assert.That(result.Value.Value).IsEqualTo(1UL);
    }

    // === SettingsApplied event ===

    [Test]
    public async Task SettingsAppliedFiredWithChangedNamesWhenDirtySettingsExist()
    {
        using SettingsManager mgr = new();
        Setting s1 = Setting.Bool("test.a", "A", "test", false);
        Setting s2 = Setting.Bool("test.b", "B", "test", true);
        mgr.RegisterSetting(s1);
        mgr.RegisterSetting(s2);

        s1.SetPendingValue(SettingValue.Bool(true));
        // s2 is not dirty

        IReadOnlyList<string>? eventArgs = null;
        mgr.SettingsApplied += (_, e) => eventArgs = e.ChangedSettingNames;

        _ = mgr.ApplyChanges();

        await Assert.That(eventArgs).IsNotNull();
        await Assert.That(eventArgs!.Count).IsEqualTo(1);
        await Assert.That(eventArgs[0]).IsEqualTo("test.a");
    }

    [Test]
    public async Task SettingsAppliedNotFiredWhenNoDirtySettings()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);
        // No pending changes

        bool eventFired = false;
        mgr.SettingsApplied += (_, _) => eventFired = true;

        _ = mgr.ApplyChanges();

        await Assert.That(eventFired).IsFalse();
    }

    [Test]
    public async Task SettingsAppliedEventArgsChangedNamesMatchReturnValue()
    {
        // The event's ChangedSettingNames must be identical to the return value of ApplyChanges().
        using SettingsManager mgr = new();
        Setting s1 = Setting.Bool("test.x", "X", "test", false);
        Setting s2 = Setting.Bool("test.y", "Y", "test", false);
        mgr.RegisterSetting(s1);
        mgr.RegisterSetting(s2);

        s1.SetPendingValue(SettingValue.Bool(true));
        s2.SetPendingValue(SettingValue.Bool(true));

        IReadOnlyList<string>? eventChangedNames = null;
        mgr.SettingsApplied += (_, e) => eventChangedNames = e.ChangedSettingNames;

        IReadOnlyList<string> returnedChangedNames = mgr.ApplyChanges();

        await Assert.That(eventChangedNames).IsNotNull();
        await Assert.That(eventChangedNames!.Count).IsEqualTo(returnedChangedNames.Count);
        await Assert.That(eventChangedNames[0]).IsEqualTo(returnedChangedNames[0]);
        await Assert.That(eventChangedNames[1]).IsEqualTo(returnedChangedNames[1]);
    }

    // === IReadOnlySettingsManager interface compliance ===

    [Test]
    public async Task IReadOnlySettingsManagerGetSettingReturnsIReadOnlySetting()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));

        ReadOnlySettingsManagerView readOnly = mgr.ReadOnly;
        ReadOnlySettingView? s = readOnly.GetSetting("test.flag");

        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Value.Name).IsEqualTo("test.flag");
        bool ifaceBool = s.Value.TryGetAsBool(out bool ifaceBoolVal);
        await Assert.That(ifaceBool).IsTrue();
        await Assert.That(ifaceBoolVal).IsTrue();
    }

    [Test]
    public async Task IReadOnlySettingsManagerGetGroupReturnsIReadOnlySettingGroup()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "mygroup", true));

        ReadOnlySettingsManagerView readOnly = mgr.ReadOnly;
        ReadOnlySettingGroupView? group = readOnly.GetGroup("mygroup");

        await Assert.That(group).IsNotNull();
        await Assert.That(group!.Value.Name).IsEqualTo("mygroup");
        await Assert.That(group.Value.SettingCount).IsEqualTo(1);
        IReadOnlyList<ReadOnlySettingView> groupSettings = group.Value.Settings;
        await Assert.That(groupSettings.Count).IsEqualTo(1);
        await Assert.That(groupSettings[0].Name).IsEqualTo("test.flag");
    }

    [Test]
    public async Task IReadOnlySettingsManagerAllSettingsReturnsIReadOnlyList()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.a", "A", "test", true));
        mgr.RegisterSetting(Setting.Bool("test.b", "B", "test", false));

        ReadOnlySettingsManagerView readOnly = mgr.ReadOnly;
        IReadOnlyList<ReadOnlySettingView> all = readOnly.AllSettings;

        await Assert.That(all.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IReadOnlySettingsManagerGetSettingsInGroupReturnsGroupSettings()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g1", true));
        mgr.RegisterSetting(Setting.Bool("a.y", "Y", "g1", true));
        mgr.RegisterSetting(Setting.Bool("b.z", "Z", "g2", false));

        ReadOnlySettingsManagerView readOnly = mgr.ReadOnly;
        IReadOnlyList<ReadOnlySettingView> inG1 = readOnly.GetSettingsInGroup("g1");

        await Assert.That(inG1.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSettingsInGroupEmptyGroupReturnsEmptyList()
    {
        // Querying a non-existent group should return an empty list, not null.
        using SettingsManager mgr = new();
        IReadOnlyList<Setting> result = mgr.GetSettingsInGroup("nonexistent");
        await Assert.That(result).IsEmpty();
    }

    // === Group lock disposal (regression for LOW-7) ===

    [Test]
    public async Task DisposeWithRegisteredGroupsDoesNotThrow()
    {
        // Regression for LOW-7: SettingsManager.Dispose must dispose the ReaderWriterLockSlim
        // held by each SettingGroup. Previously only the manager's own _Lock was disposed;
        // each group's lock was leaked. A post-dispose access should not be attempted here,
        // but the Dispose call itself must succeed without exception.
        Exception? ex = null;
        try
        {
            using SettingsManager mgr = new();
            mgr.RegisterGroup("mygroup", "My Group");
            // SettingsManager.Dispose is called by `using` — no exception expected.
        }
        catch (Exception e)
        {
            ex = e;
        }
        await Assert.That(ex).IsNull();
    }

    // === Typed convenience getters ===

    [Test]
    public async Task GetF64SettingReturnsValueOrNull()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.F64("test.rate", "Rate", "test", 1.25));
        await Assert.That(mgr.GetF64Setting("test.rate")).IsEqualTo(1.25);
        await Assert.That(mgr.GetF64Setting("missing")).IsNull();
        await Assert.That(mgr.GetF64Setting("test.rate")!.Value).IsEqualTo(1.25);
    }

    [Test]
    public async Task GetU64AndI64SettingsReturnValueOrNull()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.U64("test.u", "U", "test", 42));
        mgr.RegisterSetting(Setting.I64("test.i", "I", "test", -9));
        await Assert.That(mgr.GetU64Setting("test.u")).IsEqualTo(42UL);
        await Assert.That(mgr.GetI64Setting("test.i")).IsEqualTo(-9L);
        await Assert.That(mgr.GetU64Setting("nope")).IsNull();
        await Assert.That(mgr.GetI64Setting("nope")).IsNull();
    }

    // === Storage path and group count ===

    [Test]
    public async Task StoragePathAndGroupCount()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using SettingsManager mgr = new(dir, TestLayout.Default);
        await Assert.That(mgr.StoragePath).IsEqualTo(dir);
        mgr.RegisterGroup("g1", "G1");
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g2", true));
        await Assert.That(mgr.GroupCount).IsEqualTo(2);
    }

    [Test]
    public void RegisterGroupOnDisposedManagerThrows()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => mgr.RegisterGroup("x", "X"));
    }

    [Test]
    public void RegisterSettingOnDisposedManagerThrows()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(
            () => mgr.RegisterSetting(Setting.Bool("x.y", "Y", "g", false)));
    }

    // === Preload and orphaned values ===

    [Test]
    public async Task PreloadValueLoadsBeforeRegistration()
    {
        using SettingsManager mgr = new();
        mgr.PreloadValue("future.flag", SettingValue.Bool(true));

        SettingRegistrationResult result = mgr.RegisterSetting(
            Setting.Bool("future.flag", "Flag", string.Empty, false));

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.Success);
        result.TryGetAsBool(out bool value);
        await Assert.That(value).IsTrue();
    }

    [Test]
    public async Task OrphanedEntriesLoadClearAndApplyOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"orphan.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(1);

            SettingRegistrationResult result = mgr.RegisterSetting(
                Setting.Bool("orphan.flag", "Flag", string.Empty, false));
            await Assert.That(result.WasLoaded).IsTrue();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(0);

            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"leftover.flag": false}""").ConfigureAwait(false);
            mgr.Load();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(1);
            mgr.ClearOrphanedEntries();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void PreloadValueOnDisposedManagerThrows()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(
            () => mgr.PreloadValue("x", SettingValue.Bool(true)));
    }

    [Test]
    public async Task GetGroupUnregisteredReturnsNull()
    {
        using SettingsManager mgr = new();
        await Assert.That(mgr.GetGroup("missing")).IsNull();
    }

    // === Save round-trip all scalar types ===

    [Test]
    public async Task SaveAndLoadAllSettingTypesRoundtrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            using (SettingsManager mgr = new(dir, TestLayout.Default))
            {
                // Save writes only values that differ from their defaults, so the defaults differ from the values under test.
                mgr.RegisterSetting(Setting.Bool("s.bool", "Bool", string.Empty, false));
                mgr.RegisterSetting(Setting.String("s.str", "Str", string.Empty, string.Empty));
                mgr.RegisterSetting(Setting.F64("s.f64", "F64", string.Empty, 0));
                mgr.RegisterSetting(Setting.U64("s.u64", "U64", string.Empty, 0));
                mgr.RegisterSetting(Setting.I64("s.i64", "I64", string.Empty, 0));
                mgr.RegisterSetting(Setting.Bytes("s.bytes", "Bytes", string.Empty, []));
                mgr.RegisterSetting(Setting.Enum("s.enum", "Enum", string.Empty, 0UL,
                    [("Low", 0UL), ("High", 1UL)]));
                mgr.GetSetting("s.bool")?.SetPendingValue(SettingValue.Bool(true));
                mgr.GetSetting("s.str")?.SetPendingValue(SettingValue.String("text"));
                mgr.GetSetting("s.f64")?.SetPendingValue(SettingValue.F64(2.5));
                mgr.GetSetting("s.u64")?.SetPendingValue(SettingValue.U64(100));
                mgr.GetSetting("s.i64")?.SetPendingValue(SettingValue.I64(-5));
                mgr.GetSetting("s.bytes")?.SetPendingValue(SettingValue.Bytes([1, 2]));
                mgr.GetSetting("s.enum")?.SetPendingValue(SettingValue.Enum("High", 1UL));
                mgr.ApplyChanges();
                mgr.Save();
            }

            using SettingsManager mgr2 = new(dir, TestLayout.Default);
            Setting b = Setting.Bool("s.bool", "Bool", string.Empty, false);
            Setting st = Setting.String("s.str", "Str", string.Empty, "");
            Setting f = Setting.F64("s.f64", "F64", string.Empty, 0);
            Setting u = Setting.U64("s.u64", "U64", string.Empty, 0);
            Setting i = Setting.I64("s.i64", "I64", string.Empty, 0);
            Setting by = Setting.Bytes("s.bytes", "Bytes", string.Empty, []);
            Setting e = Setting.Enum("s.enum", "Enum", string.Empty, 0UL, [("Low", 0UL), ("High", 1UL)]);
            mgr2.RegisterSetting(b);
            mgr2.RegisterSetting(st);
            mgr2.RegisterSetting(f);
            mgr2.RegisterSetting(u);
            mgr2.RegisterSetting(i);
            mgr2.RegisterSetting(by);
            mgr2.RegisterSetting(e);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr2.Load();
            await Assert.That(warnings).IsEmpty();

            b.Value.TryGetAsBool(out bool bv);
            st.Value.TryGetAsString(out string sv);
            f.Value.TryGetAsF64(out double fv);
            u.Value.TryGetAsU64(out ulong uv);
            i.Value.TryGetAsI64(out long iv);
            by.Value.TryGetAsBytes(out byte[] bytes);
            e.Value.TryGetAsEnum(out (string Name, ulong Value) ev);

            await Assert.That(bv).IsTrue();
            await Assert.That(sv).IsEqualTo("text");
            await Assert.That(fv).IsEqualTo(2.5);
            await Assert.That(uv).IsEqualTo(100UL);
            await Assert.That(iv).IsEqualTo(-5L);
            for (int bi = 0; bi < bytes.Length; bi++)
            {
                await Assert.That(bytes[bi]).IsEqualTo((byte)(bi + 1));
            }
            await Assert.That(ev.Name).IsEqualTo("High");
            await Assert.That(ev.Value).IsEqualTo(1UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SaveAndLoadAllArraySettingTypesRoundtrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            using (SettingsManager mgr = new(dir, TestLayout.Default))
            {
                // Save writes only values that differ from their defaults, so the defaults differ from the values under test.
                mgr.RegisterSetting(Setting.BoolArray("s.bools", "Bools", string.Empty, []));
                mgr.RegisterSetting(Setting.StringArray("s.strs", "Strs", string.Empty, []));
                mgr.RegisterSetting(Setting.F64Array("s.f64s", "F64s", string.Empty, []));
                mgr.RegisterSetting(Setting.U64Array("s.u64s", "U64s", string.Empty, []));
                mgr.RegisterSetting(Setting.I64Array("s.i64s", "I64s", string.Empty, []));
                mgr.GetSetting("s.bools")?.SetPendingValue(SettingValue.BoolArray([true, false]));
                mgr.GetSetting("s.strs")?.SetPendingValue(SettingValue.StringArray(["a", "b"]));
                mgr.GetSetting("s.f64s")?.SetPendingValue(SettingValue.F64Array([1.5, 2.5]));
                mgr.GetSetting("s.u64s")?.SetPendingValue(SettingValue.U64Array([1UL, 2UL]));
                mgr.GetSetting("s.i64s")?.SetPendingValue(SettingValue.I64Array([-1L, 3L]));
                mgr.ApplyChanges();
                mgr.Save();
            }

            using SettingsManager mgr2 = new(dir, TestLayout.Default);
            Setting b = Setting.BoolArray("s.bools", "Bools", string.Empty, []);
            Setting st = Setting.StringArray("s.strs", "Strs", string.Empty, []);
            Setting f = Setting.F64Array("s.f64s", "F64s", string.Empty, []);
            Setting u = Setting.U64Array("s.u64s", "U64s", string.Empty, []);
            Setting i = Setting.I64Array("s.i64s", "I64s", string.Empty, []);
            mgr2.RegisterSetting(b);
            mgr2.RegisterSetting(st);
            mgr2.RegisterSetting(f);
            mgr2.RegisterSetting(u);
            mgr2.RegisterSetting(i);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr2.Load();
            await Assert.That(warnings).IsEmpty();

            b.TryGetAsBoolArray(out bool[] bv);
            st.TryGetAsStringArray(out string[] sv);
            f.TryGetAsF64Array(out double[] fv);
            u.TryGetAsU64Array(out ulong[] uv);
            i.TryGetAsI64Array(out long[] iv);

            await Assert.That(bv.Length).IsEqualTo(2);
            await Assert.That(bv[0]).IsTrue();
            await Assert.That(bv[1]).IsFalse();
            await Assert.That(sv[0]).IsEqualTo("a");
            await Assert.That(sv[1]).IsEqualTo("b");
            await Assert.That(fv[0]).IsEqualTo(1.5);
            await Assert.That(fv[1]).IsEqualTo(2.5);
            await Assert.That(uv[0]).IsEqualTo(1UL);
            await Assert.That(uv[1]).IsEqualTo(2UL);
            await Assert.That(iv[0]).IsEqualTo(-1L);
            await Assert.That(iv[1]).IsEqualTo(3L);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SaveAndLoadEmptyU64ArrayRoundtrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            using (SettingsManager mgr = new(dir, TestLayout.Default))
            {
                // The default is not empty, so the empty array is a stored value.
                mgr.RegisterSetting(Setting.U64Array("s.empty", "Empty", string.Empty, [9UL]));
                mgr.GetSetting("s.empty")?.SetPendingValue(SettingValue.U64Array([]));
                mgr.ApplyChanges();
                mgr.Save();
            }

            using SettingsManager mgr2 = new(dir, TestLayout.Default);
            Setting u = Setting.U64Array("s.empty", "Empty", string.Empty, [9UL]);
            mgr2.RegisterSetting(u);
            IReadOnlyList<SettingsLoadWarning> warnings = mgr2.Load();
            await Assert.That(warnings).IsEmpty();
            u.TryGetAsU64Array(out ulong[] uv);
            await Assert.That(uv.Length).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadU64ArrayScalarJsonTypeMismatchKeepsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.ports": 47290}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64Array("test.ports", "Ports", string.Empty, [1UL]);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            s.TryGetAsU64Array(out ulong[] current);
            await Assert.That(current.Length).IsEqualTo(1);
            await Assert.That(current[0]).IsEqualTo(1UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadU64ArrayMixedJsonTypeMismatchKeepsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.ports": [1, "x"]}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64Array("test.ports", "Ports", string.Empty, [1UL]);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            s.TryGetAsU64Array(out ulong[] current);
            await Assert.That(current[0]).IsEqualTo(1UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadU64ArrayNullJsonElementTypeMismatchKeepsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.ports": [1, null]}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64Array("test.ports", "Ports", string.Empty, [1UL]);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            s.TryGetAsU64Array(out ulong[] current);
            await Assert.That(current.Length).IsEqualTo(1);
            await Assert.That(current[0]).IsEqualTo(1UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadU64ArrayOutOfRangeElementKeepsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.ports": [1, 99]}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64Array("test.ports", "Ports", string.Empty, [2UL], min: 1UL, max: 10UL);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
            s.TryGetAsU64Array(out ulong[] current);
            await Assert.That(current.Length).IsEqualTo(1);
            await Assert.That(current[0]).IsEqualTo(2UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadF64ArrayNonFiniteElementTypeMismatchKeepsDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.vals": ["NaN"]}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.F64Array("test.vals", "Vals", string.Empty, [1.5]);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            s.TryGetAsF64Array(out double[] current);
            await Assert.That(current.Length).IsEqualTo(1);
            await Assert.That(current[0]).IsEqualTo(1.5);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task GetU64ArraySettingUnregisteredOrWrongTypeReturnsNull()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.U64("test.port", "Port", "test", 1));
        await Assert.That(mgr.GetU64ArraySetting("missing")).IsNull();
        await Assert.That(mgr.GetU64ArraySetting("test.port")).IsNull();
        await Assert.That(mgr.GetBoolArraySetting("missing")).IsNull();
        await Assert.That(mgr.GetBoolArraySetting("test.port")).IsNull();
        await Assert.That(mgr.GetStringArraySetting("test.port")).IsNull();
        await Assert.That(mgr.GetF64ArraySetting("test.port")).IsNull();
        await Assert.That(mgr.GetI64ArraySetting("test.port")).IsNull();
    }

    [Test]
    public async Task GetU64ArraySettingReturnsDefensiveCopy()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.U64Array("test.ports", "Ports", "test", [1UL, 2UL]));
        ulong[]? first = mgr.GetU64ArraySetting("test.ports");
        await Assert.That(first).IsNotNull();
        first![0] = 99UL;
        ulong[]? second = mgr.GetU64ArraySetting("test.ports");
        await Assert.That(second![0]).IsEqualTo(1UL);
    }

    [Test]
    public async Task PreloadValueU64ArrayAppliesOnRegister()
    {
        using SettingsManager mgr = new();
        mgr.PreloadValue("test.ports", SettingValue.U64Array([3UL, 4UL]));
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.U64Array("test.ports", "Ports", "test", []));
        await Assert.That(result.WasLoaded).IsTrue();
        result.TryGetAsU64Array(out ulong[] loaded);
        await Assert.That(loaded.Length).IsEqualTo(2);
        await Assert.That(loaded[0]).IsEqualTo(3UL);
        await Assert.That(loaded[1]).IsEqualTo(4UL);
    }

    [Test]
    public async Task LoadEnumByNumericValueReturnsTypeMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.level": 2}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Enum("test.level", "Level", string.Empty, 0UL,
                [("Low", 0UL), ("Mid", 1UL), ("High", 2UL)]);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadNonFiniteF64ReturnsWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.val": "NaN"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.F64("test.val", "Val", string.Empty, 1.0);
            mgr.RegisterSetting(s);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task RegisterGroupInvalidUiNameThrows()
    {
        using SettingsManager mgr = new();
        await Assert.That(() => mgr.RegisterGroup("mygroup", "Bad\nName"))
            .Throws<InvalidUiNameSettingsException>();
    }

    [Test]
    public async Task RegisterSettingInvalidNameAtManagerThrows()
    {
        using SettingsManager mgr = new();
        Setting corrupt = SettingsTestHelpers.CreateSettingForManagerValidationTests(
            "invalid name!",
            "UI",
            "test",
            SettingType.Bool,
            SettingValue.Bool(true));

        await Assert.That(() => mgr.RegisterSetting(corrupt))
            .Throws<InvalidNameSettingsException>();
    }

    [Test]
    public async Task RegisterSettingInvalidUiNameAtManagerThrows()
    {
        using SettingsManager mgr = new();
        Setting corrupt = SettingsTestHelpers.CreateSettingForManagerValidationTests(
            "test.flag",
            "Bad\nUI",
            "test",
            SettingType.Bool,
            SettingValue.Bool(true));

        await Assert.That(() => mgr.RegisterSetting(corrupt))
            .Throws<InvalidUiNameSettingsException>();
    }

    [Test]
    public async Task PreloadValueInvalidNameThrows()
    {
        using SettingsManager mgr = new();
        await Assert.That(() => mgr.PreloadValue("bad name!", SettingValue.Bool(true)))
            .Throws<InvalidNameSettingsException>();
    }

    [Test]
    public async Task PreloadValueOutOfRangeReturnsOutOfRangeOnRegister()
    {
        using SettingsManager mgr = new();
        mgr.PreloadValue("test.port", SettingValue.U64(80));
        SettingRegistrationResult result = mgr.RegisterSetting(
            Setting.U64("test.port", "Port", "test", 8080, min: 1024, max: 65535));

        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.OutOfRange);
        result.TryGetAsU64(out ulong value);
        await Assert.That(value).IsEqualTo(8080UL);
    }

    [Test]
    public async Task LoadMissingStorageDirectoryReturnsNoWarnings()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using SettingsManager mgr = new(dir, TestLayout.Default);
        IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task LoadInvalidRootShapeReturnsWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), "[]").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.InvalidGroupFileShape);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadOrphanedValueAppliedWhenSettingRegisteredLater()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"future.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(1);

            SettingRegistrationResult result = mgr.RegisterSetting(
                Setting.Bool("future.flag", "Flag", string.Empty, false));

            await Assert.That(result.WasLoaded).IsTrue();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadU64FromStringAndLongParsesCorrectly()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.a": "42", "test.b": 43}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting a = Setting.U64("test.a", "A", string.Empty, 0);
            Setting b = Setting.U64("test.b", "B", string.Empty, 0);
            mgr.RegisterSetting(a);
            mgr.RegisterSetting(b);
            await Assert.That(mgr.Load()).IsEmpty();

            a.Value.TryGetAsU64(out ulong av);
            b.Value.TryGetAsU64(out ulong bv);
            await Assert.That(av).IsEqualTo(42UL);
            await Assert.That(bv).IsEqualTo(43UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadI64FromStringParsesCorrectly()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.offset": "-15"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.I64("test.offset", "Offset", string.Empty, 0);
            mgr.RegisterSetting(s);
            await Assert.That(mgr.Load()).IsEmpty();

            s.Value.TryGetAsI64(out long value);
            await Assert.That(value).IsEqualTo(-15L);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadEnumObjectUnknownNumericValueReturnsTypeMismatchWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """
                {
                  "test.level": {"name": "Low", "value": 1}
                }
                """).ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
                [("Low", 0UL), ("High", 2UL)]);
            Setting level = Setting.EnumWithMetadata("test.level", "Level", string.Empty, 0, meta);
            mgr.RegisterSetting(level);

            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.TypeMismatch);
            await Assert.That(warnings[0].SettingName).IsEqualTo("test.level");
            level.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("Low");
            await Assert.That(ev.Value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadEnumObjectAllNumericFormatsLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """
                {
                  "test.a": {"name": "Low", "value": 0},
                  "test.b": {"name": "Mid", "value": 1},
                  "test.c": {"name": "High", "value": "2"}
                }
                """).ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
                [("Low", 0), ("Mid", 1), ("High", 2)]);
            Setting a = Setting.EnumWithMetadata("test.a", "A", string.Empty, 0, meta);
            Setting b = Setting.EnumWithMetadata("test.b", "B", string.Empty, 0, meta);
            Setting c = Setting.EnumWithMetadata("test.c", "C", string.Empty, 0, meta);
            mgr.RegisterSetting(a);
            mgr.RegisterSetting(b);
            mgr.RegisterSetting(c);
            await Assert.That(mgr.Load()).IsEmpty();

            c.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("High");
            await Assert.That(ev.Value).IsEqualTo(2UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ReadOnlyGroupCountMatchesManager()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g1", true));
        mgr.RegisterSetting(Setting.Bool("b.y", "Y", "g2", false));
        await Assert.That(mgr.ReadOnly.GroupCount).IsEqualTo(mgr.GroupCount);
    }

    [Test]
    public async Task LoadU64FromJsonLongParsesViaLongCoercion()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.count": -0}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.U64("test.count", "Count", string.Empty, 0);
            mgr.RegisterSetting(s);
            await Assert.That(mgr.Load()).IsEmpty();
            s.Value.TryGetAsU64(out ulong value);
            await Assert.That(value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OrphanedU64NegativeZeroJsonLoadsOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.count": -0}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            SettingRegistrationResult result = mgr.RegisterSetting(
                Setting.U64("test.count", "Count", string.Empty, 99));

            await Assert.That(result.WasLoaded).IsTrue();
            result.TryGetAsU64(out ulong value);
            await Assert.That(value).IsEqualTo(0UL);
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadEnumWithLongJsonValueParses()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.level": {"name": "High", "value": -0}}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Enum("test.level", "Level", string.Empty, 0UL,
                [("Low", 0UL), ("High", 1UL)]);
            mgr.RegisterSetting(s);
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.EnumNameMismatch);
            s.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("Low");
            await Assert.That(ev.Value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OrphanedEnumNegativeZeroValueLoadsOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.level": {"name": "Zero", "value": -0}}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            SettingRegistrationResult result = mgr.RegisterSetting(
                Setting.Enum("test.level", "Level", string.Empty, 1UL,
                    [("Zero", 0UL), ("One", 1UL)]));

            await Assert.That(result.WasLoaded).IsTrue();
            result.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("Zero");
            await Assert.That(ev.Value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PrivateParsersLongCoercionBranchesAreExercised()
    {
        System.Reflection.MethodInfo? parseU64 = typeof(SettingValueJson).GetMethod(
            "_TryParseU64", BindingFlags.NonPublic | BindingFlags.Static);
        System.Reflection.MethodInfo? parseEnumNumeric = typeof(SettingValueJson).GetMethod(
            "_TryGetEnumNumericValue", BindingFlags.NonPublic | BindingFlags.Static);
        System.Reflection.MethodInfo? parseEnum = typeof(SettingValueJson).GetMethod(
            "_TryParseEnumWithMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseU64).IsNotNull();
        await Assert.That(parseEnumNumeric).IsNotNull();
        await Assert.That(parseEnum).IsNotNull();

        JsonNode longOnly = JsonNode.Parse("-0")!;
        SettingValue? u64 = parseU64!.Invoke(null, [longOnly]) as SettingValue?;
        await Assert.That(u64).IsNotNull();
        SettingValue u64Value = u64!.Value;
        u64Value.TryGetAsU64(out ulong parsed);
        await Assert.That(parsed).IsEqualTo(0UL);

        JsonNode enumLong = JsonNode.Parse("""{"name": "Zero", "value": -0}""")!;
        JsonValue enumValueNode = (JsonValue)enumLong["value"]!;
        ulong? numeric = parseEnumNumeric!.Invoke(null, [enumValueNode]) as ulong?;
        await Assert.That(numeric).IsEqualTo(0UL);

        object? enumParseResult = parseEnum!.Invoke(
            null,
            [enumLong, null, string.Empty, "test.level"]);
        await Assert.That(enumParseResult).IsNotNull();
        (SettingValue? Value, SettingLoadResult Result, SettingsLoadWarning? Warning) enumTuple =
            ((SettingValue? Value, SettingLoadResult Result, SettingsLoadWarning? Warning))enumParseResult!;
        await Assert.That(enumTuple.Value).IsNotNull();
        enumTuple.Value!.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
        await Assert.That(ev.Name).IsEqualTo("Zero");
        await Assert.That(ev.Value).IsEqualTo(0UL);
    }

    [Test]
    public async Task TryLoadPersistedValueLockedWithOrphanReturnsSuccess()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"locked.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            Setting setting = Setting.Bool("locked.flag", "Flag", string.Empty, false);

            System.Reflection.FieldInfo lockField = typeof(SettingsManager).GetField(
                "_Lock", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Lock managerLock = (Lock)lockField.GetValue(mgr)!;
            System.Reflection.MethodInfo? loadMethod = typeof(SettingsManager).GetMethod(
                "_TryLoadPersistedValueLocked", BindingFlags.NonPublic | BindingFlags.Instance);
            await Assert.That(loadMethod).IsNotNull();

            (SettingLoadResult Result, SettingsLoadWarning? Warning) outcome;
            lock (managerLock)
            {
                outcome = ((SettingLoadResult, SettingsLoadWarning?))loadMethod!.Invoke(mgr, [setting])!;
            }

            await Assert.That(outcome.Result).IsEqualTo(SettingLoadResult.Success);
            await Assert.That(outcome.Warning).IsNull();

            setting.Value.TryGetAsBool(out bool value);
            await Assert.That(value).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OrphanedValueRegisterSettingAppliesPersistedSuccessPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"persist.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            SettingRegistrationResult result = mgr.RegisterSetting(
                Setting.Bool("persist.flag", "Flag", string.Empty, false));

            await Assert.That(result.WasLoaded).IsTrue();
            result.TryGetAsBool(out bool value);
            await Assert.That(value).IsTrue();
            await Assert.That(mgr.OrphanedEntryCount).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadPersistedValueLockedNoOrphanGroupReturnsNoPersistedValue()
    {
        using SettingsManager mgr = new();
        Setting setting = Setting.Bool("orphan.flag", "Flag", "missing.group", false);
        SettingLoadResult loadResult = _InvokeTryLoadPersistedValueLocked(mgr, setting);
        await Assert.That(loadResult).IsEqualTo(SettingLoadResult.NoPersistedValue);
    }

    [Test]
    public async Task TryLoadPersistedValueLockedMissingSettingInGroupReturnsNoPersistedValue()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "test.group.json"),
                """{"other.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            Setting setting = Setting.Bool("missing.flag", "Flag", "test.group", false);
            SettingLoadResult loadResult = _InvokeTryLoadPersistedValueLocked(mgr, setting);
            await Assert.That(loadResult).IsEqualTo(SettingLoadResult.NoPersistedValue);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadPersistedValueLockedTypeMismatchReturnsTypeMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"typed.flag": "not-a-bool"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            Setting setting = Setting.Bool("typed.flag", "Flag", string.Empty, false);
            SettingLoadResult loadResult = _InvokeTryLoadPersistedValueLocked(mgr, setting);
            await Assert.That(loadResult).IsEqualTo(SettingLoadResult.TypeMismatch);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadPersistedValueLockedOutOfRangeReturnsOutOfRange()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.port": 80}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            Setting setting = Setting.U64("test.port", "Port", string.Empty, 8080UL, min: 1024UL, max: 65535UL);
            SettingLoadResult loadResult = _InvokeTryLoadPersistedValueLocked(mgr, setting);
            await Assert.That(loadResult).IsEqualTo(SettingLoadResult.OutOfRange);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadPersistedValueLockedInvalidBase64ReturnsDeserializationError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.blob": "!!!"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.Load();
            Setting setting = Setting.Bytes("test.blob", "Blob", string.Empty, [1]);
            SettingLoadResult loadResult = _InvokeTryLoadPersistedValueLocked(mgr, setting);
            await Assert.That(loadResult).IsEqualTo(SettingLoadResult.DeserializationError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadI64FromJsonNumberParsesDirectNumeric()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.offset": -42}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.I64("test.offset", "Offset", string.Empty, 0);
            mgr.RegisterSetting(s);
            await Assert.That(mgr.Load()).IsEmpty();
            s.Value.TryGetAsI64(out long value);
            await Assert.That(value).IsEqualTo(-42L);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PrivateParsersEnumWithMetadataBranchCoverage()
    {
        MethodInfo? parseEnum = typeof(SettingValueJson).GetMethod(
            "_TryParseEnumWithMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? parseNumeric = typeof(SettingValueJson).GetMethod(
            "_TryGetEnumNumericValue", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseEnum).IsNotNull();
        await Assert.That(parseNumeric).IsNotNull();

        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0UL), ("High", 1UL)]);

        await Assert.That(_InvokeEnumParseResult(parseEnum!, JsonNode.Parse("[]")!, meta))
            .IsEqualTo(SettingLoadResult.TypeMismatch);

        JsonObject missingName = new() { ["value"] = 0 };
        await Assert.That(_InvokeEnumParseResult(parseEnum!, missingName, meta))
            .IsEqualTo(SettingLoadResult.TypeMismatch);

        JsonObject missingValue = new() { ["name"] = "Low" };
        await Assert.That(_InvokeEnumParseResult(parseEnum!, missingValue, meta))
            .IsEqualTo(SettingLoadResult.TypeMismatch);

        JsonObject badNumeric = new() { ["name"] = "Low", ["value"] = true };
        await Assert.That(_InvokeEnumParseResult(parseEnum!, badNumeric, meta))
            .IsEqualTo(SettingLoadResult.TypeMismatch);

        JsonObject unknownNumeric = new() { ["name"] = "Low", ["value"] = 99 };
        await Assert.That(_InvokeEnumParseResult(parseEnum!, unknownNumeric, meta))
            .IsEqualTo(SettingLoadResult.TypeMismatch);

        JsonValue directUlong = (JsonValue)JsonNode.Parse("1")!;
        ulong? parsedUlong = parseNumeric!.Invoke(null, [directUlong]) as ulong?;
        await Assert.That(parsedUlong).IsEqualTo(1UL);
    }

    [Test]
    public async Task LoadEnumWrongNameWithValidNumericEmitsNameMismatchWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.level": {"name": "Wrong", "value": 0}}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default);
            Setting s = Setting.Enum("test.level", "Level", string.Empty, 1UL,
                [("Low", 0UL), ("High", 1UL)]);
            mgr.RegisterSetting(s);
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.EnumNameMismatch);
            s.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("Low");
            await Assert.That(ev.Value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PrivateParsersSettingValueToJsonNonFiniteF64Throws()
    {
        MethodInfo? toJson = typeof(SettingValueJson).GetMethod(
            "_SettingValueToJson", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(toJson).IsNotNull();

        SettingValue nonFinite = SettingValue.F64(double.PositiveInfinity);
        TargetInvocationException tie = Assert.Throws<TargetInvocationException>(
            () => toJson!.Invoke(null, [nonFinite]));
        await Assert.That(tie.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(tie.InnerException!.Message).Contains("finite");
    }

    [Test]
    public async Task PrivateParsersTryParseI64InvalidNodeReturnsNull()
    {
        MethodInfo? parseI64 = typeof(SettingValueJson).GetMethod(
            "_TryParseI64", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseI64).IsNotNull();

        JsonNode node = JsonValue.Create(true)!;
        SettingValue? value = parseI64!.Invoke(null, [node]) as SettingValue?;
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task PrivateParsersTryParseEnumWithMetadataInvalidNameReturnsTypeMismatch()
    {
        MethodInfo? parseEnum = typeof(SettingValueJson).GetMethod(
            "_TryParseEnumWithMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseEnum).IsNotNull();

        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0UL)]);
        JsonObject obj = new() { ["name"] = 1, ["value"] = 0 };
        SettingLoadResult result = _InvokeEnumParseResult(parseEnum!, obj, meta);
        await Assert.That(result).IsEqualTo(SettingLoadResult.TypeMismatch);
    }

    [Test]
    public async Task PrivateParsersTryParseEnumWithMetadataUnknownNumericReturnsTypeMismatch()
    {
        MethodInfo? parseEnum = typeof(SettingValueJson).GetMethod(
            "_TryParseEnumWithMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseEnum).IsNotNull();

        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0UL)]);
        JsonObject obj = new() { ["name"] = "Low", ["value"] = 99 };
        SettingLoadResult result = _InvokeEnumParseResult(parseEnum!, obj, meta);
        await Assert.That(result).IsEqualTo(SettingLoadResult.TypeMismatch);
    }

    [Test]
    public async Task PrivateParsersSettingValueToJsonBoolReturnsJsonValue()
    {
        MethodInfo? toJson = typeof(SettingValueJson).GetMethod(
            "_SettingValueToJson", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(toJson).IsNotNull();

        JsonNode? node = toJson!.Invoke(null, [SettingValue.Bool(true)]) as JsonNode;
        await Assert.That(node).IsNotNull();
        await Assert.That(node!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task PrivateParsersSettingValueToJsonUnknownTypeReturnsNull()
    {
        MethodInfo? toJson = typeof(SettingValueJson).GetMethod(
            "_SettingValueToJson", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(toJson).IsNotNull();

        SettingValue unknown = SettingsTestHelpers.WithSettingValueField(
            SettingValue.Bool(true), "_Type", (SettingType)99);
        JsonNode? node = toJson!.Invoke(null, [unknown]) as JsonNode;
        await Assert.That(node).IsNull();
    }

    [Test]
    public async Task PrivateParsersTryParseEnumWithMetadataUnknownCanonicalNumericReturnsTypeMismatch()
    {
        MethodInfo? parseEnum = typeof(SettingValueJson).GetMethod(
            "_TryParseEnumWithMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseEnum).IsNotNull();

        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0UL), ("High", 2UL)]);
        JsonObject obj = new() { ["name"] = "Low", ["value"] = 1 };
        SettingLoadResult result = _InvokeEnumParseResult(parseEnum!, obj, meta);
        await Assert.That(result).IsEqualTo(SettingLoadResult.TypeMismatch);
    }

    private static SettingLoadResult _InvokeTryLoadPersistedValueLocked(SettingsManager mgr, Setting setting)
    {
        System.Reflection.FieldInfo lockField = typeof(SettingsManager).GetField(
            "_Lock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Lock managerLock = (Lock)lockField.GetValue(mgr)!;
        MethodInfo loadMethod = typeof(SettingsManager).GetMethod(
            "_TryLoadPersistedValueLocked", BindingFlags.NonPublic | BindingFlags.Instance)!;

        lock (managerLock)
        {
            (SettingLoadResult Result, SettingsLoadWarning? Warning) outcome =
                ((SettingLoadResult, SettingsLoadWarning?))loadMethod.Invoke(mgr, [setting])!;
            return outcome.Result;
        }
    }

    private static SettingLoadResult _InvokeEnumParseResult(
        MethodInfo parseEnum, JsonNode node, EnumSettingMetadata? metadata)
    {
        object? boxed = parseEnum.Invoke(null, [node, metadata, string.Empty, "test.enum"]);
        (_, SettingLoadResult result, _) =
            ((SettingValue?, SettingLoadResult, SettingsLoadWarning?))boxed!;
        return result;
    }

    [Test]
    public void LoadWhenAlreadyLoadingThrowsInvalidOperationException()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            using SettingsManager mgr = new(dir, TestLayout.Default);
            System.Reflection.FieldInfo loadingField = typeof(SettingsManager).GetField(
                "_IsLoading", BindingFlags.NonPublic | BindingFlags.Instance)!;
            loadingField.SetValue(mgr, 1);
            try
            {
                _ = Assert.Throws<InvalidOperationException>(() => mgr.Load());
            }
            finally
            {
                loadingField.SetValue(mgr, 0);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadOversizedFileSkipsWithWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "default.json");
            await using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(1025);
            }

            using SettingsManager mgr = new(dir, TestLayout.Default, _FileSizeLimit(1024));
            mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.FileTooLarge);
            await Assert.That(warnings[0].Message).Contains("is larger than allowed");
            await Assert.That(warnings[0].Message).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
            await Assert.That(mgr.GetBoolSetting("test.flag")).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SaveUnrelatedSidecarJsonIsNotDeleted()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string sidecar = Path.Combine(dir, "sidecar.json");
        try
        {
            await File.WriteAllTextAsync(sidecar, "{\"keep\":true}").ConfigureAwait(false);

            using (SettingsManager first = new(dir, TestLayout.Default))
            {
                first.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));
                first.GetSetting("test.flag")?.SetPendingValue(SettingValue.Bool(false));
                first.ApplyChanges();
                first.Save();
            }

            await Assert.That(File.Exists(sidecar)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "test.json"))).IsTrue();

            using (SettingsManager second = new(dir, TestLayout.Default))
            {
                second.RegisterSetting(Setting.Bool("other.flag", "Other", string.Empty, false));
                second.GetSetting("other.flag")?.SetPendingValue(SettingValue.Bool(true));
                second.ApplyChanges();
                second.Save();
            }

            await Assert.That(File.Exists(sidecar)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "test.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "default.json"))).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SaveOversizedOwnedManifestDoesNotDeleteSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string sidecar = Path.Combine(dir, "sidecar.json");
        string manifest = Path.Combine(dir, TestLayout.Default.OwnedManifestFileName);
        try
        {
            await File.WriteAllTextAsync(sidecar, "{\"keep\":true}").ConfigureAwait(false);
            await using (FileStream stream = new(manifest, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(1025);
            }

            using SettingsManager mgr = new(dir, TestLayout.Default, _FileSizeLimit(1024));
            mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));
            mgr.Save();

            await Assert.That(File.Exists(sidecar)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task ReadOwnedGroupFilesSharingViolationReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string manifest = Path.Combine(dir, TestLayout.Default.OwnedManifestFileName);
        try
        {
            await File.WriteAllTextAsync(manifest, "test.json\n").ConfigureAwait(false);
            using FileStream locked = new(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using SettingsManager mgr = new(dir, TestLayout.Default);
            MethodInfo read = typeof(SettingsManager).GetMethod(
                "_ReadOwnedGroupFiles", BindingFlags.NonPublic | BindingFlags.Instance)!;
            HashSet<string> owned = (HashSet<string>)read.Invoke(mgr, [manifest])!;
            await Assert.That(owned.Count).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Limit values with one file size cap; every other limit keeps its default.</summary>
    /// <param name="maxFileSize">The cap in bytes; inside the range of <see cref="SettingsLimits.MaxFileSize"/>.</param>
    /// <returns>The limit values.</returns>
    private static SettingsLimitValues _FileSizeLimit(long maxFileSize) =>
        new() { MaxFileSize = LimitValue.Of(maxFileSize) };

    [Test]
    public async Task CtorDefaultUsesTheSecureDefaultLimits()
    {
        using SettingsManager mgr = new();

        await Assert.That(mgr.Limits).IsEqualTo(SettingsLimitValues.Default);
        await Assert.That(mgr.ReadOnly.Limits).IsEqualTo(SettingsLimitValues.Default);
        await Assert.That(mgr.Limits.MaxFileSize).IsEqualTo(LimitValue.Of(64L << 20));
        await Assert.That(mgr.Limits.MaxJsonDepth).IsEqualTo(LimitValue.Of(64));
        await Assert.That(mgr.Limits.FolderLockTimeout).IsEqualTo(LimitValue.Of(2_000));
        await Assert.That(mgr.Limits.MaxTextLength).IsEqualTo(LimitValue.Of(1_048_576));
    }

    [Test]
    public async Task TheLoaderLimitsAreSoundAndFinite()
    {
        await Assert.That(LimitCatalog.Check(SettingsLimits.All)).IsEmpty();
        foreach (LimitDefinition definition in SettingsLimits.All)
        {
            await Assert.That(definition.DefaultValue.IsOff).IsFalse();
            await Assert.That(definition.AllowOff).IsTrue();
        }
    }

    [Test]
    public async Task CtorStoragePathUsesDefaultLimits()
    {
        using SettingsManager mgr = new(Path.GetTempPath(), TestLayout.Default);

        await Assert.That(mgr.Limits).IsEqualTo(SettingsLimitValues.Default);
    }

    [Test]
    public async Task CtorCustomLimitsAreExposed()
    {
        SettingsLimitValues limits = new() { MaxFileSize = LimitValue.Of(4096), MaxJsonDepth = LimitValue.Of(8) };

        using SettingsManager mgr = new(Path.GetTempPath(), TestLayout.Default, limits);

        await Assert.That(mgr.Limits.MaxFileSize).IsEqualTo(LimitValue.Of(4096));
        await Assert.That(mgr.ReadOnly.Limits.MaxJsonDepth).IsEqualTo(LimitValue.Of(8));
        await Assert.That(mgr.Limits.FolderLockTimeout).IsEqualTo(SettingsLimits.FolderLockTimeout.DefaultValue);
    }

    [Test]
    [Arguments(0L, 64L, 2_000L, 1_048_576L)]
    [Arguments(1_024L, 0L, 2_000L, 1_048_576L)]
    [Arguments(1_024L, 64L, 0L, 1_048_576L)]
    [Arguments(1_024L, 64L, 2_000L, 0L)]
    [Arguments(1_023L, 64L, 2_000L, 1_048_576L)]
    [Arguments(1_024L, 3L, 2_000L, 1_048_576L)]
    [Arguments(1_024L, 1_025L, 2_000L, 1_048_576L)]
    [Arguments(1_024L, 64L, 99L, 1_048_576L)]
    public async Task CtorRefusesAZeroOrOutOfRangeLimitAndNamesTheKey(long fileSize, long depth, long lockWait, long textLength)
    {
        SettingsLimitValues limits = new()
        {
            MaxFileSize = LimitValue.Of(fileSize),
            MaxJsonDepth = LimitValue.Of(depth),
            FolderLockTimeout = LimitValue.Of(lockWait),
            MaxTextLength = LimitValue.Of(textLength),
        };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = new SettingsManager(Path.GetTempPath(), TestLayout.Default, limits));

        await Assert.That(exception.Message).Contains("settings.");
    }

    [Test]
    public async Task CtorRefusesADefaultLimitValueBecauseItIsZero()
    {
        SettingsLimitValues limits = new() { MaxFileSize = default };

        await Assert.That(() => new SettingsManager(Path.GetTempPath(), TestLayout.Default, limits)).Throws<ArgumentOutOfRangeException>();

        // null! is the input under test
        await Assert.That(() => new SettingsManager(Path.GetTempPath(), TestLayout.Default, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CtorAcceptsOffForEveryLimit()
    {
        SettingsLimitValues limits = new()
        {
            MaxFileSize = LimitValue.Off,
            MaxJsonDepth = LimitValue.Off,
            FolderLockTimeout = LimitValue.Off,
            MaxTextLength = LimitValue.Off,
        };

        using SettingsManager mgr = new(Path.GetTempPath(), TestLayout.Default, limits);

        await Assert.That(mgr.Limits.MaxFileSize.IsOff).IsTrue();
        await Assert.That(mgr.Limits.MaxJsonDepth.IsOff).IsTrue();
        await Assert.That(mgr.Limits.FolderLockTimeout.IsOff).IsTrue();
        await Assert.That(mgr.Limits.MaxTextLength.IsOff).IsTrue();
    }

    [Test]
    public async Task ASettingThatBelongsToOneManagerIsRefusedByAnotherAndTheSecondManagerStaysUnchanged()
    {
        Setting setting = Setting.Bool("a.flag", "Flag", "a", false);
        using SettingsManager first = new();
        using SettingsManager second = new();
        first.RegisterSetting(setting);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => second.RegisterSetting(setting));

        await Assert.That(exception.Message).Contains("a.flag");
        await Assert.That(second.SettingCount).IsEqualTo(0);
        await Assert.That(second.GroupCount).IsEqualTo(0);
        await Assert.That(first.SettingCount).IsEqualTo(1);
    }

    [Test]
    public async Task APendingChangeRaisesTheEventOfTheFirstManagerOnly()
    {
        Setting setting = Setting.Bool("a.flag", "Flag", "a", false);
        using SettingsManager first = new();
        using SettingsManager second = new();
        int raisedByFirst = 0;
        int raisedBySecond = 0;
        first.PendingChanged += (_, _) => raisedByFirst++;
        second.PendingChanged += (_, _) => raisedBySecond++;
        first.RegisterSetting(setting);
        Assert.Throws<InvalidOperationException>(() => second.RegisterSetting(setting));

        setting.SetPendingValue(SettingValue.Bool(true));

        await Assert.That(raisedByFirst).IsEqualTo(1);
        await Assert.That(raisedBySecond).IsEqualTo(0);
    }

    [Test]
    public async Task SaveKeepsForeignTmpFilesAndRemovesItsOwnLeftovers()
    {
        using SettingsTempFolder folder = new();
        string foreign = folder.Combine("notes.json.tmp");
        string clash = folder.Combine("app.json.tmp");
        string leftover = folder.Combine("app.json." + new string('a', 32) + ".tmp");
        File.WriteAllText(foreign, "mine");
        File.WriteAllText(clash, "mine");
        File.WriteAllText(leftover, "stale");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        new SettingsRegistrar(manager).RegisterStringSetting("app.name", "Name", "app", "x");
        manager.GetSetting("app.name")!.SetPendingValue(SettingValue.String("y"));
        manager.ApplyChanges();

        manager.Save();

        await Assert.That(File.ReadAllText(foreign)).IsEqualTo("mine");
        await Assert.That(File.ReadAllText(clash)).IsEqualTo("mine");
        await Assert.That(File.Exists(leftover)).IsFalse();
        await Assert.That(folder.ReadJson("app.json")["app.name"]!.GetValue<string>()).IsEqualTo("y");
    }

    [Test]
    public async Task SaveChangedNeverWritesOverAForeignTmpFile()
    {
        using SettingsTempFolder folder = new();
        string clash = folder.Combine("app.json.tmp");
        File.WriteAllText(clash, "mine");
        using SettingsManager manager = new(folder.Path, TestLayout.Default);
        new SettingsRegistrar(manager).RegisterStringSetting("app.name", "Name", "app", "x");
        manager.GetSetting("app.name")!.SetPendingValue(SettingValue.String("y"));
        manager.ApplyChanges();

        manager.SaveChanged(["app.name"]);

        await Assert.That(File.ReadAllText(clash)).IsEqualTo("mine");
        await Assert.That(folder.ReadJson("app.json")["app.name"]!.GetValue<string>()).IsEqualTo("y");
    }

    [Test]
    [Arguments("a.json.tmp", false)]
    [Arguments("notes.json.tmp", false)]
    [Arguments("a.json.0123456789abcdef0123456789abcdef.tmp", true)]
    [Arguments("a.json.0123456789ABCDEF0123456789abcdef.tmp", false)]
    [Arguments("a.json.0123456789abcdef0123456789abcde.tmp", false)]
    [Arguments("a.txt.0123456789abcdef0123456789abcdef.tmp", false)]
    [Arguments("a.json.0123456789abcdef0123456789abcdef", false)]
    [Arguments(".json.0123456789abcdef0123456789abcdef.tmp", true)]
    public async Task OnlyTheTmpNamesOfTheLibraryAreRecognizedAsItsOwn(string fileName, bool expected)
    {
        await Assert.That(SettingsFileAccess.IsTempFileName(fileName)).IsEqualTo(expected);
    }

    [Test]
    public async Task ANewTempPathIsRecognizedAndNeverRepeats()
    {
        string first = SettingsFileAccess.NewTempPath(Path.Combine("folder", "app.json"));
        string second = SettingsFileAccess.NewTempPath(Path.Combine("folder", "app.json"));

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(SettingsFileAccess.IsTempFileName(Path.GetFileName(first))).IsTrue();
    }

    [Test]
    public async Task LoadWithoutASizeLimitLoadsANonEmptyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string json = "{\"test.flag\":true}";
            string padded = json + new string(' ', 2048);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), padded).ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default, new SettingsLimitValues { MaxFileSize = LimitValue.Off });
            mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings).IsEmpty();
            await Assert.That(mgr.GetBoolSetting("test.flag")).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadDefaultLimitSparseFileLargerThanDefaultIsSkippedWithTheHitSentence()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "default.json");
            await using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(SettingsLimits.MaxFileSize.DefaultValue.Value + 1);
            }

            using SettingsManager mgr = new(dir, TestLayout.Default);
            mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.FileTooLarge);
            await Assert.That(warnings[0].Message).Contains("is larger than allowed");
            await Assert.That(warnings[0].Message).Contains("Limit: 64 MiB (setting settings.maxFileSize)");
            await Assert.That(mgr.GetBoolSetting("test.flag")).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadJsonDeeperThanTheLimitIsSkipped()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), _NestedJsonObject(5))
                .ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default, new SettingsLimitValues { MaxJsonDepth = LimitValue.Of(4) });
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.JsonTooDeep);
            await Assert.That(warnings[0].Message).Contains("settings.maxJsonDepth");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadWithoutADepthLimitLoadsADeepObject()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), _NestedJsonObject(65))
                .ConfigureAwait(false);

            using SettingsManager mgr = new(dir, TestLayout.Default, new SettingsLimitValues { MaxJsonDepth = LimitValue.Off });
            IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();

            await Assert.That(warnings).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string _NestedJsonObject(int depth)
    {
        StringBuilder builder = new();
        for (int i = 0; i < depth; i++)
        {
            builder.Append("{\"x\":");
        }

        builder.Append('1');
        builder.Append('}', depth);
        return builder.ToString();
    }
}
