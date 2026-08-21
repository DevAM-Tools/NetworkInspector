// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingGroup"/> — default factory, default-group detection,
/// and internal iteration.
/// </summary>
internal sealed class SettingGroupTests
{
    [Test]
    public async Task Default_CreatesDefaultGroup()
    {
        SettingGroup group = SettingGroup.Default();
        await Assert.That(group.Name).IsEqualTo(SettingGroup.DefaultGroupName);
        await Assert.That(group.UiName).IsEqualTo(SettingGroup.DefaultGroupUiName);
        await Assert.That(group.IsDefaultGroup).IsTrue();
    }

    [Test]
    public void Constructor_InvalidGroupName_Throws()
    {
        InvalidNameSettingsException ex = Assert.Throws<InvalidNameSettingsException>(
            () => _ = new SettingGroup("Not Valid", "Display"));
        _ = ex;
    }

    [Test]
    public void Constructor_InvalidUiName_Throws()
    {
        InvalidNameSettingsException ex = Assert.Throws<InvalidNameSettingsException>(
            () => _ = new SettingGroup("valid", ""));
        _ = ex;
    }

    [Test]
    public async Task IsDefaultGroup_FalseForNamedGroup()
    {
        SettingGroup group = new("mygroup", "My Group");
        await Assert.That(group.IsDefaultGroup).IsFalse();
    }

    [Test]
    public async Task ForEachSetting_InvokesCallbackForEachSetting()
    {
        SettingGroup group = new("test", "Test");
        Setting s1 = Setting.Bool("test.a", "A", "test", true);
        Setting s2 = Setting.Bool("test.b", "B", "test", false);
        group.AddSetting(s1);
        group.AddSetting(s2);

        List<string> names = [];
        group.ForEachSetting(s => names.Add(s.Name));

        await Assert.That(names.Count).IsEqualTo(2);
        await Assert.That(names).Contains("test.a");
        await Assert.That(names).Contains("test.b");
    }
}
