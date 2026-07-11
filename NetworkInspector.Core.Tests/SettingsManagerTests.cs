// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

        IReadOnlySettingsManager readOnly = mgr.ReadOnly;
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

        IReadOnlySettingsManager readOnly = mgr.ReadOnly;
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

        IReadOnlySettingsManager readOnly = mgr.ReadOnly;
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

        IReadOnlySettingsManager readOnly = mgr.ReadOnly;
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

    // === Typed convenience getters ===

    [Test]
    public async Task GetF64Setting_ReturnsValueOrNull()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.F64("test.rate", "Rate", "test", 1.25));
        await Assert.That(mgr.GetF64Setting("test.rate")).IsEqualTo(1.25);
        await Assert.That(mgr.GetF64Setting("missing")).IsNull();
        await Assert.That(mgr.GetF64Setting("test.rate")!.Value).IsEqualTo(1.25);
    }

    [Test]
    public async Task GetU64AndI64Settings_ReturnValueOrNull()
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
    public async Task StoragePath_AndGroupCount()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using SettingsManager mgr = new(dir);
        await Assert.That(mgr.StoragePath).IsEqualTo(dir);
        mgr.RegisterGroup("g1", "G1");
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g2", true));
        await Assert.That(mgr.GroupCount).IsEqualTo(2);
    }

    [Test]
    public void RegisterGroup_OnDisposedManager_Throws()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => mgr.RegisterGroup("x", "X"));
    }

    [Test]
    public void RegisterSetting_OnDisposedManager_Throws()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(
            () => mgr.RegisterSetting(Setting.Bool("x.y", "Y", "g", false)));
    }

    // === Preload and orphaned values ===

    [Test]
    public async Task PreloadValue_LoadsBeforeRegistration()
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
    public async Task OrphanedEntries_LoadClearAndApplyOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"orphan.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public void PreloadValue_OnDisposedManager_Throws()
    {
        SettingsManager mgr = new();
        mgr.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(
            () => mgr.PreloadValue("x", SettingValue.Bool(true)));
    }

    [Test]
    public async Task GetGroup_Unregistered_ReturnsNull()
    {
        using SettingsManager mgr = new();
        await Assert.That(mgr.GetGroup("missing")).IsNull();
    }

    // === Save round-trip all scalar types ===

    [Test]
    public async Task SaveAndLoad_AllSettingTypes_Roundtrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using (SettingsManager mgr = new(dir))
            {
                mgr.RegisterSetting(Setting.Bool("s.bool", "Bool", string.Empty, true));
                mgr.RegisterSetting(Setting.String("s.str", "Str", string.Empty, "text"));
                mgr.RegisterSetting(Setting.F64("s.f64", "F64", string.Empty, 2.5));
                mgr.RegisterSetting(Setting.U64("s.u64", "U64", string.Empty, 100));
                mgr.RegisterSetting(Setting.I64("s.i64", "I64", string.Empty, -5));
                mgr.RegisterSetting(Setting.Bytes("s.bytes", "Bytes", string.Empty, [1, 2]));
                mgr.RegisterSetting(Setting.Enum("s.enum", "Enum", string.Empty, 1UL,
                    [("Low", 0UL), ("High", 1UL)]));
                mgr.Save();
            }

            using SettingsManager mgr2 = new(dir);
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
    public async Task Load_EnumByNumericValue_ReturnsTypeMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.level": 2}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_NonFiniteF64_ReturnsWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.val": "NaN"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task RegisterGroup_InvalidUiName_Throws()
    {
        using SettingsManager mgr = new();
        await Assert.That(() => mgr.RegisterGroup("mygroup", "Bad\nName"))
            .Throws<InvalidNameSettingsException>();
    }

    [Test]
    public async Task RegisterSetting_InvalidNameAtManager_Throws()
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
    public async Task RegisterSetting_InvalidUiNameAtManager_Throws()
    {
        using SettingsManager mgr = new();
        Setting corrupt = SettingsTestHelpers.CreateSettingForManagerValidationTests(
            "test.flag",
            "Bad\nUI",
            "test",
            SettingType.Bool,
            SettingValue.Bool(true));

        await Assert.That(() => mgr.RegisterSetting(corrupt))
            .Throws<InvalidNameSettingsException>();
    }

    [Test]
    public async Task PreloadValue_InvalidName_Throws()
    {
        using SettingsManager mgr = new();
        await Assert.That(() => mgr.PreloadValue("bad name!", SettingValue.Bool(true)))
            .Throws<InvalidNameSettingsException>();
    }

    [Test]
    public async Task PreloadValue_OutOfRange_ReturnsOutOfRangeOnRegister()
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
    public async Task Load_MissingStorageDirectory_ReturnsNoWarnings()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using SettingsManager mgr = new(dir);
        IReadOnlyList<SettingsLoadWarning> warnings = mgr.Load();
        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task Load_InvalidRootShape_ReturnsWarning()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), "[]").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_OrphanedValue_AppliedWhenSettingRegisteredLater()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"future.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_U64FromStringAndLong_ParsesCorrectly()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.a": "42", "test.b": 43}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_I64FromString_ParsesCorrectly()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.offset": "-15"}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_EnumObject_AllNumericFormats_Load()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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

            using SettingsManager mgr = new(dir);
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
    public async Task ReadOnly_GroupCount_MatchesManager()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("a.x", "X", "g1", true));
        mgr.RegisterSetting(Setting.Bool("b.y", "Y", "g2", false));
        await Assert.That(mgr.ReadOnly.GroupCount).IsEqualTo(mgr.GroupCount);
    }

    [Test]
    public async Task Load_U64FromJsonLong_ParsesViaLongCoercion()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.count": -0}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task OrphanedU64_NegativeZeroJson_LoadsOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.count": -0}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task Load_EnumWithLongJsonValue_Parses()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.level": {"name": "High", "value": -0}}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
            Setting s = Setting.Enum("test.level", "Level", string.Empty, 0UL,
                [("Low", 0UL), ("High", 1UL)]);
            mgr.RegisterSetting(s);
            await Assert.That(mgr.Load()).IsEmpty();
            s.Value.TryGetAsEnum(out (string Name, ulong Value) ev);
            await Assert.That(ev.Name).IsEqualTo("High");
            await Assert.That(ev.Value).IsEqualTo(0UL);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OrphanedEnum_NegativeZeroValue_LoadsOnRegister()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"test.level": {"name": "Zero", "value": -0}}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
    public async Task PrivateParsers_LongCoercionBranches_AreExercised()
    {
        System.Reflection.MethodInfo? parseU64 = typeof(SettingsManager).GetMethod(
            "_TryParseU64", BindingFlags.NonPublic | BindingFlags.Static);
        System.Reflection.MethodInfo? parseEnum = typeof(SettingsManager).GetMethod(
            "_TryParseEnum", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(parseU64).IsNotNull();
        await Assert.That(parseEnum).IsNotNull();

        JsonNode longOnly = JsonNode.Parse("-0")!;
        SettingValue? u64 = parseU64!.Invoke(null, [longOnly]) as SettingValue?;
        await Assert.That(u64).IsNotNull();
        SettingValue u64Value = u64!.Value;
        u64Value.TryGetAsU64(out ulong parsed);
        await Assert.That(parsed).IsEqualTo(0UL);

        JsonNode enumLong = JsonNode.Parse("""{"name": "Zero", "value": -0}""")!;
        SettingValue? enumValue = parseEnum!.Invoke(null, [enumLong]) as SettingValue?;
        await Assert.That(enumValue).IsNotNull();
        SettingValue enumVal = enumValue!.Value;
        enumVal.TryGetAsEnum(out (string Name, ulong Value) ev);
        await Assert.That(ev.Name).IsEqualTo("Zero");
        await Assert.That(ev.Value).IsEqualTo(0UL);
    }

    [Test]
    public async Task TryLoadPersistedValueLocked_WithOrphan_ReturnsSuccess()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"locked.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
            mgr.Load();
            Setting setting = Setting.Bool("locked.flag", "Flag", string.Empty, false);

            System.Reflection.FieldInfo lockField = typeof(SettingsManager).GetField(
                "_Lock", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ReaderWriterLockSlim rwLock = (ReaderWriterLockSlim)lockField.GetValue(mgr)!;
            System.Reflection.MethodInfo? loadMethod = typeof(SettingsManager).GetMethod(
                "_TryLoadPersistedValueLocked", BindingFlags.NonPublic | BindingFlags.Instance);
            await Assert.That(loadMethod).IsNotNull();

            rwLock.EnterWriteLock();
            try
            {
                SettingLoadResult loadResult = (SettingLoadResult)loadMethod!.Invoke(mgr, [setting])!;
                await Assert.That(loadResult).IsEqualTo(SettingLoadResult.Success);
            }
            finally
            {
                rwLock.ExitWriteLock();
            }

            setting.Value.TryGetAsBool(out bool value);
            await Assert.That(value).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OrphanedValue_RegisterSetting_AppliesPersistedSuccessPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "default.json"),
                """{"persist.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
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
}
