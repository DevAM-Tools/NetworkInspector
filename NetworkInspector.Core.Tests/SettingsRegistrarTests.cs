// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsRegistrar"/> delegation paths not covered by integration tests.
/// </summary>
internal sealed class SettingsRegistrarTests
{
    [Test]
    public async Task RegisterGroup_DelegatesToManager()
    {
        using SettingsManager mgr = new();
        StackBuilder builder = new(mgr, new FrameInterfaceRegistry());
        builder.SettingsRegistrar.RegisterGroup("network", "Network", "Network settings");

        SettingGroup? group = mgr.GetGroup("network");
        await Assert.That(group).IsNotNull();
        await Assert.That(group!.UiName).IsEqualTo("Network");
        await Assert.That(group.Description).IsEqualTo("Network settings");
    }

    [Test]
    public async Task RegisterSetting_RawSetting_DelegatesToManager()
    {
        using SettingsManager mgr = new();
        StackBuilder builder = new(mgr, new FrameInterfaceRegistry());
        Setting setting = Setting.String("test.raw", "Raw", "test", "value");
        SettingRegistrationResult result = builder.SettingsRegistrar.RegisterSetting(setting);

        await Assert.That(result.IsDefault).IsTrue();
        await Assert.That(mgr.GetSetting("test.raw")).IsNotNull();
    }

    [Test]
    public async Task RegisterBytesSetting_RegistersAndReturnsResult()
    {
        using SettingsManager mgr = new();
        StackBuilder builder = new(mgr, new FrameInterfaceRegistry());
        byte[] data = [0xAA, 0xBB];
        SettingRegistrationResult result =
            builder.SettingsRegistrar.RegisterBytesSetting("test.data", "Data", "test", data);

        await Assert.That(result.IsDefault).IsTrue();
        byte[]? loaded = mgr.GetBytesSetting("test.data");
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded![0]).IsEqualTo((byte)0xAA);
        await Assert.That(loaded[1]).IsEqualTo((byte)0xBB);
    }
}
