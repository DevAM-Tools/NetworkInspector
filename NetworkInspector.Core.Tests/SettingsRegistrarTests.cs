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

    [Test]
    public async Task RegisterU64ArraySetting_RegistersAndReturnsResult()
    {
        using SettingsManager mgr = new();
        StackBuilder builder = new(mgr, new FrameInterfaceRegistry());
        SettingRegistrationResult result =
            builder.SettingsRegistrar.RegisterU64ArraySetting("test.ports", "Ports", "test", [1UL, 2UL]);

        await Assert.That(result.IsDefault).IsTrue();
        ulong[]? loaded = mgr.GetU64ArraySetting("test.ports");
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.Length).IsEqualTo(2);
        await Assert.That(loaded[0]).IsEqualTo(1UL);
        await Assert.That(loaded[1]).IsEqualTo(2UL);
    }

    [Test]
    public async Task RegisterArraySettings_AllFiveTypes_SucceedIncludingEmpty()
    {
        using SettingsManager mgr = new();
        StackBuilder builder = new(mgr, new FrameInterfaceRegistry());
        SettingsRegistrar registrar = builder.SettingsRegistrar;

        registrar.RegisterBoolArraySetting("test.flags", "Flags", "test", [true, false]);
        registrar.RegisterStringArraySetting("test.names", "Names", "test", ["a"]);
        registrar.RegisterF64ArraySetting("test.vals", "Vals", "test", [1.5]);
        registrar.RegisterI64ArraySetting("test.offs", "Offs", "test", [-1L]);
        registrar.RegisterU64ArraySetting("test.empty", "Empty", "test", []);

        bool[]? flags = mgr.GetBoolArraySetting("test.flags");
        await Assert.That(flags).IsNotNull();
        await Assert.That(flags!.Length).IsEqualTo(2);
        await Assert.That(flags[0]).IsTrue();
        await Assert.That(flags[1]).IsFalse();

        string[]? names = mgr.GetStringArraySetting("test.names");
        await Assert.That(names).IsNotNull();
        await Assert.That(names![0]).IsEqualTo("a");

        double[]? vals = mgr.GetF64ArraySetting("test.vals");
        await Assert.That(vals).IsNotNull();
        await Assert.That(vals![0]).IsEqualTo(1.5);

        long[]? offs = mgr.GetI64ArraySetting("test.offs");
        await Assert.That(offs).IsNotNull();
        await Assert.That(offs![0]).IsEqualTo(-1L);

        ulong[]? empty = mgr.GetU64ArraySetting("test.empty");
        await Assert.That(empty).IsNotNull();
        await Assert.That(empty!.Length).IsEqualTo(0);
    }
}
