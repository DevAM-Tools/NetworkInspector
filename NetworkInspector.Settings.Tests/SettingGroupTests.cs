// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// Tests for <see cref="SettingGroup"/> — default factory, default-group detection,
/// and internal iteration.
/// </summary>
internal sealed class SettingGroupTests
{
    [Test]
    public async Task DefaultCreatesDefaultGroup()
    {
        SettingGroup group = SettingGroup.Default();
        await Assert.That(group.Name).IsEqualTo(SettingGroup.DefaultGroupName);
        await Assert.That(group.UiName).IsEqualTo(SettingGroup.DefaultGroupUiName);
        await Assert.That(group.IsDefaultGroup).IsTrue();
    }

    [Test]
    public void ConstructorInvalidGroupNameThrows()
    {
        InvalidNameSettingsException ex = Assert.Throws<InvalidNameSettingsException>(
            () => _ = new SettingGroup("Not Valid", "Display"));
        _ = ex;
    }

    [Test]
    public void ConstructorInvalidUiNameThrows()
    {
        InvalidUiNameSettingsException ex = Assert.Throws<InvalidUiNameSettingsException>(
            () => _ = new SettingGroup("valid", ""));
        _ = ex;
    }

    [Test]
    public async Task IsDefaultGroupFalseForNamedGroup()
    {
        SettingGroup group = new("mygroup", "My Group");
        await Assert.That(group.IsDefaultGroup).IsFalse();
    }

    [Test]
    public async Task ASnapshotDoesNotShowALaterRegistration()
    {
        SettingGroup group = new("a", "A");
        group.AddSetting(Setting.Bool("a.one", "One", "a", false));
        IReadOnlyList<Setting> snapshot = group.CopySettings();

        group.AddSetting(Setting.Bool("a.two", "Two", "a", false));

        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(group.SettingCount).IsEqualTo(2);
        await Assert.That(group.CopySettings().Select(static setting => setting.Name)).IsEquivalentTo(["a.one", "a.two"]);
    }

    [Test]
    public async Task SettingsAreListedInRegistrationOrder()
    {
        SettingGroup group = new("test", "Test");
        group.AddSetting(Setting.Bool("test.b", "B", "test", true));
        group.AddSetting(Setting.Bool("test.a", "A", "test", false));

        await Assert.That(group.Settings.Select(static view => view.Name)).IsEquivalentTo(["test.b", "test.a"]);
        await Assert.That(group.CopySettings()[0].Name).IsEqualTo("test.b");
    }

    [Test]
    public async Task ConcurrentReadersSeeOnlyCompleteSnapshotsWhileOneWriterRegisters()
    {
        SettingGroup group = new("c", "C");
        using ManualResetEventSlim start = new(false);
        const int count = 200;

        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < count; index++)
            {
                group.AddSetting(Setting.Bool(Invariant($"c.s{index}"), "S", "c", false));
            }
        });
        Task<int> reader = Task.Run(() =>
        {
            start.Wait();
            int bad = 0;
            int last = 0;
            while (last < count)
            {
                IReadOnlyList<Setting> snapshot = group.CopySettings();

                // A snapshot only grows, never shrinks, and every element of it is set.
                if (snapshot.Count < last || snapshot.Any(static setting => setting is null))
                {
                    bad++;
                }

                last = snapshot.Count;
            }

            return bad;
        });
        start.Set();
        await writer;

        await Assert.That(await reader).IsEqualTo(0);
        await Assert.That(group.SettingCount).IsEqualTo(count);
    }
}
