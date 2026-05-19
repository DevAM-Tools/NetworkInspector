// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsManager"/> — registration, querying, groups,
/// dirty tracking, bulk operations, and orphaned value recovery.
/// </summary>
internal sealed class SettingsManagerTests
{
    // === Registration ===

    [Test]
    public async Task RegisterSetting_BoolSetting_Succeeds()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", true);
        SettingRegistrationResult result = mgr.RegisterSetting(s);
        await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.NoPersistedValue);
        await Assert.That(result.IsDefault).IsTrue();
    }

    [Test]
    public async Task RegisterSetting_Duplicate_Throws()
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
    public async Task RegisterSetting_InvalidName_Throws()
    {
        using SettingsManager mgr = new();
        // The Setting constructor validates names eagerly, so the exception is thrown
        // when creating the Setting, before RegisterSetting is even called.
        InvalidNameRegistrationException ex = Assert.Throws<InvalidNameRegistrationException>(
            () => mgr.RegisterSetting(Setting.Bool("invalid name!", "Flag", "test", true)));
        await Assert.That(ex.Name).IsEqualTo("invalid name!");
    }

    [Test]
    public void RegisterSetting_InvalidUiName_Throws()
    {
        using SettingsManager mgr = new();
        // The Setting constructor validates UI names eagerly.
        _ = Assert.Throws<InvalidUiNameRegistrationException>(
            () => mgr.RegisterSetting(Setting.Bool("test.flag", "Flag\n", "test", true)));
    }

    // === Querying ===

    [Test]
    public async Task GetSetting_ReturnsRegisteredSetting()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", true);
        mgr.RegisterSetting(s);
        Setting? found = mgr.GetSetting("test.flag");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Name).IsEqualTo("test.flag");
    }

    [Test]
    public async Task GetSetting_NotRegistered_ReturnsNull()
    {
        using SettingsManager mgr = new();
        Setting? found = mgr.GetSetting("nonexistent");
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task AllSettings_ReturnsAllRegistered()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.a", "A", "test", true));
        mgr.RegisterSetting(Setting.Bool("test.b", "B", "test", false));
        IReadOnlyList<Setting> all = mgr.AllSettings;
        await Assert.That(all.Count).IsEqualTo(2);
    }

    // === Groups ===

    [Test]
    public async Task RegisterGroup_Explicit_Succeeds()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("network", "Network Settings");
        SettingGroup? group = mgr.GetGroup("network");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task RegisterGroup_Duplicate_Throws()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("network", "Network Settings");
        DuplicateNameSettingsException ex =
            Assert.Throws<DuplicateNameSettingsException>(() => mgr.RegisterGroup("network", "Network 2"));
        await Assert.That(ex.Name).IsEqualTo("network");
    }

    [Test]
    public async Task RegisterGroup_UppercaseName_Throws()
    {
        // Group names must be lowercase — uppercase letters are rejected.
        using SettingsManager mgr = new();
        _ = Assert.Throws<InvalidNameSettingsException>(() => mgr.RegisterGroup("MyGroup", "My Group"));
    }

    [Test]
    public async Task RegisterGroup_LowercaseName_Succeeds()
    {
        using SettingsManager mgr = new();
        mgr.RegisterGroup("my.group", "My Group");
        SettingGroup? group = mgr.GetGroup("my.group");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task RegisterSetting_AutoCreatesGroup()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "mygroup", true));
        SettingGroup? group = mgr.GetGroup("mygroup");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public async Task AllGroups_ListsGroupNames()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "groupa", true));
        mgr.RegisterSetting(Setting.Bool("b.y", "Y", "groupb", false));
        IReadOnlyList<string> groups = mgr.AllGroups;
        await Assert.That(groups.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSettingsInGroup_ReturnsCorrectSettings()
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
    public async Task HasDirtySettings_InitiallyFalse()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));
        await Assert.That(mgr.HasDirtySettings).IsFalse();
    }

    [Test]
    public async Task HasDirtySettings_TrueAfterPendingChange()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));
        await Assert.That(mgr.HasDirtySettings).IsTrue();
    }

    [Test]
    public async Task ApplyChanges_ReturnsChangedNames()
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
    public async Task ResetChanges_DiscardsAllPending()
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
    public async Task ResetAllToDefaults_ResetsBothValues()
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
    public async Task DefaultGroup_EmptyStringName()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", SettingGroup.DefaultGroupName, true);
        mgr.RegisterSetting(s);
        SettingGroup? group = mgr.GetGroup(SettingGroup.DefaultGroupName);
        await Assert.That(group).IsNotNull();
    }

    // === Save without StoragePath ===

    [Test]
    public void Save_NoStoragePath_Throws()
    {
        using SettingsManager mgr = new();
        Assert.Throws<PersistenceSettingsException>(() => mgr.Save());
    }

    // === Load without StoragePath ===

    [Test]
    public void Load_NoStoragePath_Throws()
    {
        using SettingsManager mgr = new();
        _ = Assert.Throws<PersistenceSettingsException>(() => mgr.Load());
    }

    // === Load warnings ===

    [Test]
    public async Task Load_InvalidGroupName_ReturnsWarningAndSkipsFile()
    {
        // A JSON file whose name contains uppercase letters produces an InvalidGroupName warning
        // and its contents are not loaded.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // Write a file with uppercase group name "MyGroup.json" containing a valid setting
            await File.WriteAllTextAsync(Path.Combine(dir, "MyGroup.json"), """{"test.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_TypeMismatch_ReturnsWarning()
    {
        // A JSON value with an incompatible type (boolean stored for a numeric setting)
        // produces a TypeMismatch warning and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // "default.json" maps to the default group (empty string)
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.count": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_OutOfRange_ReturnsWarning()
    {
        // A numeric value from JSON that violates the min/max constraint
        // produces an OutOfRange warning and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.port": 99999}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_ValidSettings_NoWarnings()
    {
        // A well-formed settings file produces no warnings.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_InvalidBase64ForBytesSetting_ReturnsDeserializationErrorWarning()
    {
        // A JSON string value for a Bytes setting that is not valid base64
        // must produce a DeserializationError warning (not TypeMismatch),
        // and the setting keeps its default.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // "!!!" is not valid base64
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.data": "!!!not-valid-base64!!!"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_WrongJsonTypeForBytesSetting_ReturnsTypeMismatch()
    {
        // A JSON number value for a Bytes setting is a TypeMismatch (not DeserializationError)
        // because the JSON type itself is wrong, not the content.
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.data": 42}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_ValidBase64ForBytesSetting_LoadsCorrectly()
    {
        // A valid base64-encoded string for a Bytes setting loads without warnings.
        byte[] expected = [0xDE, 0xAD, 0xBE, 0xEF];
        string base64 = Convert.ToBase64String(expected);

        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                $$"""{"test.data": "{{base64}}"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task GetBytesSetting_ReturnsCorrectValue()
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
    public async Task GetBytesSetting_UnregisteredName_ReturnsNull()
    {
        using SettingsManager mgr = new();
        byte[]? result = mgr.GetBytesSetting("nonexistent");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetEnumSetting_ReturnsCorrectValue()
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
    public async Task GetEnumSetting_UnregisteredName_ReturnsNull()
    {
        using SettingsManager mgr = new();
        (string Name, ulong Value)? result = mgr.GetEnumSetting("nonexistent");
        await Assert.That(result).IsNull();
    }

    // === SettingsApplied event ===

    [Test]
    public async Task SettingsApplied_FiredWithChangedNames_WhenDirtySettingsExist()
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
    public async Task SettingsApplied_NotFired_WhenNoDirtySettings()
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
    public async Task SettingsApplied_EventArgsChangedNamesMatchReturnValue()
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
    public async Task IReadOnlySettingsManager_GetSetting_ReturnsIReadOnlySetting()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));

        IReadOnlySettingsManager readOnly = mgr;
        IReadOnlySetting? s = readOnly.GetSetting("test.flag");

        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Name).IsEqualTo("test.flag");
        bool ifaceBool = s.TryGetAsBool(out bool ifaceBoolVal);
        await Assert.That(ifaceBool).IsTrue();
        await Assert.That(ifaceBoolVal).IsTrue();
    }

    [Test]
    public async Task IReadOnlySettingsManager_GetGroup_ReturnsIReadOnlySettingGroup()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "mygroup", true));

        IReadOnlySettingsManager readOnly = mgr;
        IReadOnlySettingGroup? group = readOnly.GetGroup("mygroup");

        await Assert.That(group).IsNotNull();
        await Assert.That(group!.Name).IsEqualTo("mygroup");
        await Assert.That(group.SettingCount).IsEqualTo(1);
    }

    [Test]
    public async Task IReadOnlySettingsManager_AllSettings_ReturnsIReadOnlyList()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("test.a", "A", "test", true));
        mgr.RegisterSetting(Setting.Bool("test.b", "B", "test", false));

        IReadOnlySettingsManager readOnly = mgr;
        IReadOnlyList<IReadOnlySetting> all = readOnly.AllSettings;

        await Assert.That(all.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IReadOnlySettingsManager_GetSettingsInGroup_ReturnsGroupSettings()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g1", true));
        mgr.RegisterSetting(Setting.Bool("a.y", "Y", "g1", true));
        mgr.RegisterSetting(Setting.Bool("b.z", "Z", "g2", false));

        IReadOnlySettingsManager readOnly = mgr;
        IReadOnlyList<IReadOnlySetting> inG1 = readOnly.GetSettingsInGroup("g1");

        await Assert.That(inG1.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSettingsInGroup_EmptyGroup_ReturnsEmptyList()
    {
        // Querying a non-existent group should return an empty list, not null.
        using SettingsManager mgr = new();
        IReadOnlyList<Setting> result = mgr.GetSettingsInGroup("nonexistent");
        await Assert.That(result).IsEmpty();
    }

    // === Group lock disposal (regression for LOW-7) ===

    [Test]
    public async Task Dispose_WithRegisteredGroups_DoesNotThrow()
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
}
