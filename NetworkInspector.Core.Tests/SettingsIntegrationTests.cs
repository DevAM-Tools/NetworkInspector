// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Integration tests for the settings system through
/// <see cref="StackBuilder"/> and <see cref="Stack"/>.
/// Verifies registration via <see cref="SettingsRegistrar"/> and querying via IStack.
/// </summary>
internal sealed class SettingsIntegrationTests
{
    [Test]
    public async Task RegisterBoolSetting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SettingRegistrationResult result =
            builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Test Flag", "test", true);
        await Assert.That(result.IsDefault).IsTrue();

        Stack stack = builder.Build();
        bool? value = stack.Settings.GetBoolSetting("test.flag");
        await Assert.That(value).IsTrue();
    }

    [Test]
    public async Task RegisterStringSetting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterStringSetting("test.name", "Name", "test", "hello");
        Stack stack = builder.Build();
        string? value = stack.Settings.GetStringSetting("test.name");
        await Assert.That(value).IsEqualTo("hello");
    }

    [Test]
    public async Task RegisterF64Setting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterF64Setting("test.ratio", "Ratio", "test", 0.75, min: 0.0, max: 1.0);
        Stack stack = builder.Build();
        double? value = stack.Settings.GetF64Setting("test.ratio");
        await Assert.That(value).IsEqualTo(0.75);
    }

    [Test]
    public async Task RegisterU64Setting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterU64Setting("test.port", "Port", "test", 8080, min: 1, max: 65535);
        Stack stack = builder.Build();
        ulong? value = stack.Settings.GetU64Setting("test.port");
        await Assert.That(value).IsEqualTo(8080UL);
    }

    [Test]
    public async Task RegisterI64Setting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterI64Setting("test.offset", "Offset", "test", -10, min: -100, max: 100);
        Stack stack = builder.Build();
        long? value = stack.Settings.GetI64Setting("test.offset");
        await Assert.That(value).IsEqualTo(-10L);
    }

    [Test]
    public async Task RegisterEnumSetting_ThenQueryFromStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        builder.SettingsRegistrar.RegisterEnumSetting("test.level", "Level", "test", 1, meta);
        Stack stack = builder.Build();
        IReadOnlySetting? setting = stack.Settings.GetSetting("test.level");
        await Assert.That(setting).IsNotNull();
        await Assert.That(setting!.Type).IsEqualTo(SettingType.Enum);
        bool enumOk = setting.Value.TryGetAsEnum(out (string Name, ulong Value) e);
        await Assert.That(enumOk).IsTrue();
        await Assert.That(e.Name).IsEqualTo("Medium");
    }

    [Test]
    public async Task Settings_Property_ReturnsAllSettings()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("test.a", "A", "test", true);
        builder.SettingsRegistrar.RegisterBoolSetting("test.b", "B", "test", false);
        Stack stack = builder.Build();
        await Assert.That(stack.Settings.AllSettings.Count).IsEqualTo(2); // 2 user settings, no system settings
    }

    [Test]
    public async Task SettingCount_ReturnsCorrectCount()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("test.a", "A", "test", true);
        builder.SettingsRegistrar.RegisterStringSetting("test.b", "B", "test", "x");
        Stack stack = builder.Build();
        await Assert.That(stack.Settings.SettingCount).IsEqualTo(2); // 2 user settings, no system settings
    }

    [Test]
    public async Task GetSetting_NotRegistered_ReturnsNull()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        Stack stack = builder.Build();
        IReadOnlySetting? setting = stack.Settings.GetSetting("nonexistent");
        await Assert.That(setting).IsNull();
    }

    [Test]
    public async Task GetBoolSetting_WrongName_ReturnsNull()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Flag", "test", true);
        Stack stack = builder.Build();
        bool? value = stack.Settings.GetBoolSetting("wrong.name");
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task SettingGroups_ListsGroupNames()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("a.x", "X", "group1", true);
        builder.SettingsRegistrar.RegisterBoolSetting("b.y", "Y", "group2", false);
        Stack stack = builder.Build();
        IReadOnlyList<string> groups = stack.Settings.AllGroups;
        await Assert.That(groups.Count).IsEqualTo(2); // group1, group2
    }

    [Test]
    public async Task GetSettingGroup_Found()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Flag", "mygroup", true);
        Stack stack = builder.Build();
        IReadOnlySettingGroup? group = stack.Settings.GetGroup("mygroup");
        await Assert.That(group).IsNotNull();
    }

    [Test]
    public void F64_InvalidRange_Throws()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        Assert.Throws<ValidationSettingsException>(() =>
            builder.SettingsRegistrar.RegisterF64Setting("test.ratio", "Ratio", "test", 0.5, min: 1.0, max: 0.0));
    }

    [Test]
    public void DuplicateRegistration_Throws()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Flag", "test", true);
        Assert.Throws<DuplicateNameSettingsException>(() =>
            builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Flag 2", "test", false));
    }
}
