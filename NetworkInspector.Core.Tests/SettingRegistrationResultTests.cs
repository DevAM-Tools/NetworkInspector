// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingRegistrationResult"/> accessors and load-state flags.
/// </summary>
internal sealed class SettingRegistrationResultTests
{
    [Test]
    public async Task WasLoaded_TrueWhenPersistedValueApplied()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "default.json"), """{"test.flag": true}""").ConfigureAwait(false);

            using SettingsManager mgr = new(dir);
            mgr.Load();
            SettingRegistrationResult result =
                mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", string.Empty, false));

            await Assert.That(result.WasLoaded).IsTrue();
            await Assert.That(result.IsDefault).IsFalse();
            await Assert.That(result.LoadResult).IsEqualTo(SettingLoadResult.Success);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryGetAsBool_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.Bool("test.flag", "Flag", "test", true));

        bool ok = result.TryGetAsBool(out bool value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsTrue();
    }

    [Test]
    public async Task TryGetAsString_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.String("test.name", "Name", "test", "hello"));

        bool ok = result.TryGetAsString(out string value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo("hello");
    }

    [Test]
    public async Task TryGetAsF64_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.F64("test.ratio", "Ratio", "test", 0.25));

        bool ok = result.TryGetAsF64(out double value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(0.25);
    }

    [Test]
    public async Task TryGetAsU64_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.U64("test.port", "Port", "test", 443));

        bool ok = result.TryGetAsU64(out ulong value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(443UL);
    }

    [Test]
    public async Task TryGetAsI64_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.I64("test.offset", "Offset", "test", -7));

        bool ok = result.TryGetAsI64(out long value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(-7L);
    }

    [Test]
    public async Task TryGetAsBytes_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result =
            mgr.RegisterSetting(Setting.Bytes("test.data", "Data", "test", [1, 2]));

        bool ok = result.TryGetAsBytes(out byte[] value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Length).IsEqualTo(2);
        await Assert.That(value[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task TryGetAsEnum_ReturnsRegisteredValue()
    {
        using SettingsManager mgr = new();
        SettingRegistrationResult result = mgr.RegisterSetting(
            Setting.Enum("test.level", "Level", "test", 1, [("Low", 0), ("High", 1)]));

        bool ok = result.TryGetAsEnum(out (string Name, ulong Value) value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value.Name).IsEqualTo("High");
        await Assert.That(value.Value).IsEqualTo(1UL);
    }
}
