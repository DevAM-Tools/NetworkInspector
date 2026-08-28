// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ReadOnlySettingView"/> and <see cref="ReadOnlySettingGroupView"/>.
/// </summary>
internal sealed class ReadOnlySettingViewTests
{
    [Test]
    public async Task Setting_AsReadOnlyView_ForwardsMetadataAndValue()
    {
        Setting setting = Setting.Bool("test.flag", "Flag", "test", true, "desc");
        ReadOnlySettingView view = setting.AsReadOnlyView();

        await Assert.That(view.Name).IsEqualTo("test.flag");
        await Assert.That(view.UiName).IsEqualTo("Flag");
        await Assert.That(view.Description).IsEqualTo("desc");
        await Assert.That(view.GroupName).IsEqualTo("test");
        await Assert.That(view.Type).IsEqualTo(SettingType.Bool);
        await Assert.That(view.TryGetAsBool(out bool value)).IsTrue();
        await Assert.That(value).IsTrue();
        await Assert.That(view.IsDirty).IsFalse();
    }

    [Test]
    public async Task Setting_AsReadOnlyView_SeesPendingAndCurrentAfterMutation()
    {
        Setting setting = Setting.Bool("test.flag", "Flag", "test", true);
        ReadOnlySettingView view = setting.AsReadOnlyView();

        _ = setting.SetPendingValue(SettingValue.Bool(false));
        await Assert.That(view.IsDirty).IsTrue();
        await Assert.That(view.TryGetAsBool(out bool current)).IsTrue();
        await Assert.That(current).IsTrue();
        await Assert.That(view.PendingValue.TryGetAsBool(out bool pending)).IsTrue();
        await Assert.That(pending).IsFalse();
    }

    [Test]
    public void ReadOnlySettingView_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new ReadOnlySettingView(null!));

    [Test]
    public void ReadOnlySettingGroupView_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _ = new ReadOnlySettingGroupView(null!));

    [Test]
    public async Task SettingGroup_AsReadOnlyView_WrapsSettingsWithoutMutationSurface()
    {
        SettingGroup group = new("test", "Test", "group-desc");
        group.AddSetting(Setting.Bool("test.a", "A", "test", true));
        group.AddSetting(Setting.String("test.b", "B", "test", "x"));

        ReadOnlySettingGroupView view = group.AsReadOnlyView();
        await Assert.That(view.Name).IsEqualTo("test");
        await Assert.That(view.UiName).IsEqualTo("Test");
        await Assert.That(view.Description).IsEqualTo("group-desc");
        await Assert.That(view.IsDefaultGroup).IsFalse();
        await Assert.That(view.SettingCount).IsEqualTo(2);

        IReadOnlyList<ReadOnlySettingView> settings = view.Settings;
        await Assert.That(settings.Count).IsEqualTo(2);
        await Assert.That(settings[0].Name).IsEqualTo("test.a");
        await Assert.That(settings[1].Name).IsEqualTo("test.b");
    }

    [Test]
    public async Task SettingGroup_CopySettings_ReturnsMutableInstances()
    {
        SettingGroup group = new("test", "Test");
        Setting setting = Setting.Bool("test.a", "A", "test", true);
        group.AddSetting(setting);

        IReadOnlyList<Setting> settings = group.CopySettings();
        await Assert.That(settings.Count).IsEqualTo(1);
        await Assert.That(settings[0].SetPendingValue(SettingValue.Bool(false))).IsTrue();
    }

    [Test]
    public async Task ManagerReadOnly_GetSetting_ReturnsStructView()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.U64("test.port", "Port", "test", 80));

        ReadOnlySettingView? view = mgr.ReadOnly.GetSetting("test.port");
        await Assert.That(view).IsNotNull();
        await Assert.That(view!.Value.TryGetAsU64(out ulong port)).IsTrue();
        await Assert.That(port).IsEqualTo(80UL);
        await Assert.That(mgr.ReadOnly.GetSetting("missing")).IsNull();
    }

    [Test]
    public async Task ManagerReadOnly_GetGroup_ReturnsStructView()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.Bool("g.flag", "Flag", "g", false));

        ReadOnlySettingGroupView? group = mgr.ReadOnly.GetGroup("g");
        await Assert.That(group).IsNotNull();
        await Assert.That(group!.Value.Settings[0].TryGetAsBool(out bool flag)).IsTrue();
        await Assert.That(flag).IsFalse();
        await Assert.That(mgr.ReadOnly.GetGroup("missing")).IsNull();
    }

    [Test]
    public async Task ManagerReadOnly_GetSettingsInGroup_EmptyWhenMissing()
    {
        using SettingsManager mgr = new();
        IReadOnlyList<ReadOnlySettingView> missing = mgr.ReadOnly.GetSettingsInGroup("nope");
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Setting_AsReadOnlyView_ForwardsU64Array()
    {
        Setting setting = Setting.U64Array("test.ports", "Ports", "test", [1UL, 2UL]);
        ReadOnlySettingView view = setting.AsReadOnlyView();

        await Assert.That(view.Type).IsEqualTo(SettingType.U64Array);
        await Assert.That(view.TryGetAsU64Array(out ulong[] value)).IsTrue();
        await Assert.That(value.Length).IsEqualTo(2);
        await Assert.That(value[0]).IsEqualTo(1UL);
        await Assert.That(value[1]).IsEqualTo(2UL);
    }
}
