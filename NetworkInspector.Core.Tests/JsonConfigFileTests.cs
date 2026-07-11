// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

// Simple configuration model used exclusively by these tests.
internal sealed record TestSimpleConfig(string Label, int Count);

/// <summary>
/// AOT-compatible serializer context for test types.
/// </summary>
[JsonSerializable(typeof(TestSimpleConfig))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Tests for <see cref="JsonConfigFile"/> (internal pure-IO loader) and
/// <see cref="SettingsManagerExtensions.TryLoadReferencedJsonConfig{T}"/>
/// (the public extension on <see cref="IReadOnlySettingsManager"/>).
/// </summary>
internal sealed class JsonConfigFileTests
{
    // === JsonConfigFile.TryLoad — direct tests ===

    [Test]
    public async Task TryLoad_FileNotFound_ReturnsFalseWithError()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        bool result = JsonConfigFile.TryLoad(
            path,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("not found");
    }

    [Test]
    public async Task TryLoad_MalformedJson_ReturnsFalseWithError()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json !!!").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                path,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("Failed to parse JSON");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_JsonNullLiteral_ReturnsFalseWithError()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "null").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                path,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("null result");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_ValidJson_ReturnsTrue_AndDeserializedValue()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"test\",\"Count\":42}").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                path,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(value).IsNotNull();
            await Assert.That(value!.Label).IsEqualTo("test");
            await Assert.That(value.Count).IsEqualTo(42);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_RelativePath_ResolvesToAbsolute()
    {
        // Relative paths must be accepted without throwing — Path.GetFullPath resolves them.
        // Since the cwd is arbitrary in tests, the resolved path will not exist, which is fine —
        // we just verify the error message contains the resolved (absolute) path, not the original relative.
        bool result = JsonConfigFile.TryLoad(
            "relative/path/config.json",
            TestJsonContext.Default.TestSimpleConfig,
            out _,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(error).IsNotNull();
        // Absolute path is longer than the relative input
        await Assert.That(error!.Length).IsGreaterThan("relative/path/config.json".Length);
    }

    // === SettingsManagerExtensions.TryLoadReferencedJsonConfig — extension tests ===

    [Test]
    public async Task TryLoadReferencedJsonConfig_EmptySettingValue_ReturnsFalse_NoWarning()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", ""));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "cfg.path",
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        // Empty/whitespace path is not an error — no warning expected
        await Assert.That(warning).IsNull();
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_SettingNotRegistered_ReturnsFalse_NoWarning()
    {
        // Unregistered setting name → GetStringSetting returns null → treated as empty path
        using SettingsManager mgr = new();

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "cfg.nonexistent",
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(warning).IsNull();
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_FileNotFound_ReturnsFalse_WarningSet()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", missingPath));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "cfg.path",
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.SettingName).IsEqualTo("cfg.path");
        await Assert.That(warning.Value.GroupName).IsEqualTo("cfg");
        await Assert.That(warning.Value.Message).IsNotNull();
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_ValidFile_ReturnsTrue_WarningNull()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"hello\",\"Count\":7}").ConfigureAwait(false);

            using SettingsManager mgr = new();
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", path));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsTrue();
            await Assert.That(warning).IsNull();
            await Assert.That(value).IsNotNull();
            await Assert.That(value!.Label).IsEqualTo("hello");
            await Assert.That(value.Count).IsEqualTo(7);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_MalformedJson_ReturnsFalse_WarningWithKind()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<<<not-json>>>").ConfigureAwait(false);

            using SettingsManager mgr = new();
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", path));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out _,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsFalse();
            await Assert.That(warning).IsNotNull();
            await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_WhitespaceSettingValue_ReturnsFalse_NoWarning()
    {
        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "   "));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "cfg.path",
            TestJsonContext.Default.TestSimpleConfig,
            out _,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(warning).IsNull();
    }

    [Test]
    public async Task TryLoadReferencedJsonConfig_SettingWithoutDot_UsesFullNameAsGroup()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        using SettingsManager mgr = new();
        mgr.RegisterSetting(Setting.String("configfile", "Config", string.Empty, missingPath));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "configfile",
            TestJsonContext.Default.TestSimpleConfig,
            out _,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.GroupName).IsEqualTo("configfile");
    }
}
