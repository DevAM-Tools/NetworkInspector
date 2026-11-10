// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

// Simple configuration model used exclusively by these tests.
internal sealed record TestSimpleConfig(string Label, int Count);

// A model whose converter refuses its content the way a serializer does for an unknown type discriminator or a wrong token.
[JsonConverter(typeof(TestThrowingConverter))]
internal sealed record TestThrowingConfig(string Label);

/// <summary>A converter that throws the exception types a serializer raises for content it cannot use.</summary>
internal sealed class TestThrowingConverter : JsonConverter<TestThrowingConfig>
{
    /// <inheritdoc/>
    public override TestThrowingConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        throw new NotSupportedException("The type discriminator is unknown.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TestThrowingConfig value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}

/// <summary>
/// AOT-compatible serializer context for test types.
/// </summary>
[JsonSerializable(typeof(TestSimpleConfig))]
[JsonSerializable(typeof(TestThrowingConfig))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Tests for <see cref="JsonConfigFile"/> (internal pure-IO loader) and
/// <see cref="SettingsManagerExtensions.TryLoadReferencedJsonConfig{T, TSettings}"/>
/// (the public extension on <see cref="IReadOnlySettingsManager"/>).
/// </summary>
internal sealed class JsonConfigFileTests
{
    // === JsonConfigFile.TryLoad — direct tests ===

    [Test]
    public async Task TryLoadFileNotFoundReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            bool result = _TryLoad(
                "missing.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("not found");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadMalformedJsonReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json !!!").ConfigureAwait(false);

            bool result = _TryLoad(
                "config.json",
                baseDirectory: dir,
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
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadJsonNullLiteralReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "null").ConfigureAwait(false);

            bool result = _TryLoad(
                "config.json",
                baseDirectory: dir,
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
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadValidJsonReturnsTrueAndDeserializedValue()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"test\",\"Count\":42}").ConfigureAwait(false);

            bool result = _TryLoad(
                "config.json",
                baseDirectory: dir,
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
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadNullBaseDirectoryReturnsFalse()
    {
        bool result = _TryLoad(
            "relative/path/config.json",
            baseDirectory: null,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("base directory is required");
    }

    // === SettingsManagerExtensions.TryLoadReferencedJsonConfig — extension tests ===

    [Test]
    public async Task TryLoadReferencedJsonConfigEmptySettingValueReturnsFalseNoWarning()
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
    public async Task TryLoadReferencedJsonConfigSettingNotRegisteredReturnsFalseNoWarning()
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
    public async Task TryLoadReferencedJsonConfigFileNotFoundReturnsFalseWarningSet()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(storageDir);

        using SettingsManager mgr = new(storageDir, TestLayout.Default);
        mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "missing.json"));

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

        Directory.Delete(storageDir, recursive: true);
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigValidFileReturnsTrueWarningNull()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            await File.WriteAllTextAsync(configPath, "{\"Label\":\"hello\",\"Count\":7}").ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir, TestLayout.Default);
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

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
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigOversizedRelativeToCapReturnsWarning()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            await File.WriteAllTextAsync(configPath, "{\"Label\":\"" + new string('a', 2000) + "\",\"Count\":7}").ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir, TestLayout.Default, new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(warning).IsNotNull();
            await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
            await Assert.That(warning.Value.Message).Contains("is larger than allowed");
        }
        finally
        {
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigNoSizeLimitLoadsLargeFile()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            string json = "{\"Label\":\"" + new string('a', 1_048_577) + "\",\"Count\":1}";
            await File.WriteAllTextAsync(configPath, json).ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir, TestLayout.Default, new SettingsLimitValues { MaxFileSize = LimitValue.Off });
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsTrue();
            await Assert.That(warning).IsNull();
            await Assert.That(value).IsNotNull();
            await Assert.That(value!.Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigDefaultCapLoadsFormerOneMibPlusFile()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            string json = "{\"Label\":\"" + new string('a', 1_048_577) + "\",\"Count\":1}";
            await File.WriteAllTextAsync(configPath, json).ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir, TestLayout.Default);
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsTrue();
            await Assert.That(warning).IsNull();
            await Assert.That(value).IsNotNull();
        }
        finally
        {
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigMalformedJsonReturnsFalseWarningWithKind()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            await File.WriteAllTextAsync(configPath, "<<<not-json>>>").ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir, TestLayout.Default);
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

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
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigWhitespaceSettingValueReturnsFalseNoWarning()
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
    public async Task TryLoadReferencedJsonConfigSettingWithoutDotUsesFullNameAsGroup()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(storageDir);

        using SettingsManager mgr = new(storageDir, TestLayout.Default);
        mgr.RegisterSetting(Setting.String("configfile", "Config", string.Empty, "missing.json"));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "configfile",
            TestJsonContext.Default.TestSimpleConfig,
            out _,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.GroupName).IsEqualTo("configfile");

        Directory.Delete(storageDir, recursive: true);
    }

    [Test]
    public async Task TryLoadPathOutsideBaseDirectoryReturnsFalseWithError()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(baseDir);

            bool result = _TryLoad(
                Path.Combine(Path.GetTempPath(), "outside.json"),
                baseDirectory: baseDir,
                TestJsonContext.Default.TestSimpleConfig,
                out _,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("outside the allowed base directory");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadPathWithDotDotReturnsFalseWithError()
    {
        bool result = _TryLoad(
            "../secret.json",
            baseDirectory: Path.GetTempPath(),
            TestJsonContext.Default.TestSimpleConfig,
            out _,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("outside the allowed base directory");
    }

    [Test]
    public async Task TryLoadFileNameWithConsecutiveDotsLoadsWhenUnderBase()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "foo..bar.json");
            await File.WriteAllTextAsync(path, "{\"Label\":\"dots\",\"Count\":3}").ConfigureAwait(false);

            bool result = _TryLoad(
                "foo..bar.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(value).IsNotNull();
            await Assert.That(value!.Label).IsEqualTo("dots");
            await Assert.That(value.Count).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadEmptyPathReturnsFalseWithError()
    {
        bool result = _TryLoad(
            "   ",
            baseDirectory: null,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("empty");
    }

    [Test]
    public async Task PathResolutionIsPathUnderBaseEqualPathReturnsTrue()
    {
        MethodInfo? isUnderBase = typeof(JsonConfigFile).GetMethod(
            "_IsPathUnderBase", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(isUnderBase).IsNotNull();

        string basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "json-base"));
        bool underBase = (bool)isUnderBase!.Invoke(null, [basePath, basePath])!;
        await Assert.That(underBase).IsTrue();
    }

    [Test]
    public async Task PathResolutionTryResolvePathRejectsDotDotSegments()
    {
        MethodInfo? resolvePath = typeof(JsonConfigFile).GetMethod(
            "_TryResolvePath", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(resolvePath).IsNotNull();

        object?[] args = ["../escape.json", Path.GetTempPath(), string.Empty, null!];
        bool resolved = (bool)resolvePath!.Invoke(null, args)!;
        await Assert.That(resolved).IsFalse();
        string? error = (string?)args[3];
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("outside the allowed base directory");
    }

    [Test]
    public async Task TryLoadOversizedFileReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "huge.json");
        try
        {
            await using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(1025);
            }

            bool result = JsonConfigFile.TryLoad(
                "huge.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                maxFileSize: LimitValue.Of(1024),
                maxJsonDepth: SettingsLimits.MaxJsonDepth.DefaultValue,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("is larger than allowed");
            await Assert.That(error!).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadNoSizeLimitDoesNotRejectLargeFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ok.json");
        try
        {
            string json = "{\"Label\":\"" + new string('a', 2048) + "\",\"Count\":1}";
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                "ok.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                maxFileSize: LimitValue.Off,
                maxJsonDepth: SettingsLimits.MaxJsonDepth.DefaultValue,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(value).IsNotNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadZeroLimitValueRejectsEveryNonEmptyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ok.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"x\",\"Count\":1}").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                "ok.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                maxFileSize: LimitValue.Of(0),
                maxJsonDepth: SettingsLimits.MaxJsonDepth.DefaultValue,
                out TestSimpleConfig? value,
                out string? error);

            // Zero is a cap of zero bytes, not "off": the load fails closed.
            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error!).Contains("is larger than allowed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadJsonDeeperThanLimitReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "deep.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"x\",\"Count\":1,\"extra\":{\"x\":{\"x\":{\"x\":{\"x\":1}}}}}")
                .ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                "deep.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                maxFileSize: LimitValue.Off,
                maxJsonDepth: LimitValue.Of(4),
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("settings.maxJsonDepth");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadNoDepthLimitLoadsDeepExtraProperty()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "deep.json");
        try
        {
            StringBuilder nested = new("{\"Label\":\"x\",\"Count\":1,\"extra\":");
            for (int i = 0; i < 65; i++)
            {
                nested.Append("{\"x\":");
            }

            nested.Append('1');
            nested.Append('}', 65);
            nested.Append('}');
            await File.WriteAllTextAsync(path, nested.ToString()).ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
                "deep.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                maxFileSize: LimitValue.Off,
                maxJsonDepth: LimitValue.Off,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(value).IsNotNull();
            await Assert.That(value!.Label).IsEqualTo("x");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadAbsolutePathOutsideBaseDoesNotEchoFullPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string outside = Path.Combine(Path.GetTempPath(), "outside-secret.json");
            bool result = _TryLoad(
                outside,
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                out _,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("outside the allowed base directory");
            await Assert.That(error!.Contains(outside, StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigWhitespaceStoragePathReturnsWarning()
    {
        using SettingsManager mgr = new("   ", TestLayout.Default);
        mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", "config.json"));

        bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
            "cfg.path",
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.Message).Contains("no storage path or application base directory");
    }

    [Test]
    public async Task TryLoadReferencedJsonConfigNoStoragePathAbsolutePathOutsideAppBaseReturnsFalse()
    {
        string outsideDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "config.json");
        try
        {
            await File.WriteAllTextAsync(outsideFile, "{\"Label\":\"x\",\"Count\":1}").ConfigureAwait(false);

            using SettingsManager mgr = new();
            mgr.RegisterSetting(Setting.String("cfg.path", "Path", "cfg", outsideFile));

            bool result = mgr.ReadOnly.TryLoadReferencedJsonConfig(
                "cfg.path",
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out SettingsLoadWarning? warning);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(warning).IsNotNull();
            await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Test]
    public async Task SafeFileLabelEmptyFileNameReturnsGenericLabel()
    {
        string path = OperatingSystem.IsWindows() ? @"C:\" : "/";
        await Assert.That(SettingsFileAccess.SafeFileLabel(path)).IsEqualTo("configuration file");
        await Assert.That(SettingsFileAccess.SafeFileLabel(string.Empty)).IsEqualTo("configuration file");
    }

    [Test]
    public async Task OpenSharedReadAllowsSecondReader()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "shared.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"share\",\"Count\":1}").ConfigureAwait(false);

            using FileStream first = SettingsFileAccess.OpenSharedRead(path);
            using FileStream second = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Assert.That(first.CanRead).IsTrue();
            await Assert.That(second.CanRead).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PathResolutionIsPathUnderBaseCaseMismatchMatchesOsRules()
    {
        MethodInfo? isUnderBase = typeof(JsonConfigFile).GetMethod(
            "_IsPathUnderBase", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(isUnderBase).IsNotNull();

        string basePath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "config");
        string other = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tmp", "CONFIG", "x.json");
        bool underBase = (bool)isUnderBase!.Invoke(null, [other, basePath])!;
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(underBase).IsTrue();
        }
        else
        {
            await Assert.That(underBase).IsFalse();
        }
    }

    [Test]
    public async Task PathResolutionTryResolvePathNullBaseReturnsFalse()
    {
        MethodInfo? resolvePath = typeof(JsonConfigFile).GetMethod(
            "_TryResolvePath", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(resolvePath).IsNotNull();

        object?[] args = ["file.json", null, string.Empty, null!];
        bool resolved = (bool)resolvePath!.Invoke(null, args)!;
        await Assert.That(resolved).IsFalse();
        string? error = (string?)args[3];
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("base directory is required");
    }

    // === JsonConfigFile.TryLoadFromStream / JsonConfigStream ===

    [Test]
    public async Task TryLoadFromStreamValidJsonReturnsTrue()
    {
        using MemoryStream stream = _Utf8Stream("""{"Label":"from-stream","Count":9}""");
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Label).IsEqualTo("from-stream");
        await Assert.That(value.Count).IsEqualTo(9);
    }

    [Test]
    public async Task TryLoadFromStreamMalformedJsonReturnsFalseWithError()
    {
        using MemoryStream stream = _Utf8Stream("{ not json");
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("Failed to parse JSON");
    }

    [Test]
    public async Task TryLoadFromStreamJsonNullLiteralReturnsFalseWithError()
    {
        using MemoryStream stream = _Utf8Stream("null");
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("null result");
    }

    [Test]
    public async Task TryLoadFromStreamNotReadableReturnsFalse()
    {
        using NonReadableStream stream = new();
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("not readable");
    }

    [Test]
    public async Task TryLoadFromStreamPayloadLargerThanFormer1MiBCapSucceeds()
    {
        string json = "{\"Label\":\"" + new string('a', 1_048_577) + "\",\"Count\":1}";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryLoadFromStreamForwardOnlyValidJsonReturnsTrue()
    {
        using ForwardOnlyStream stream = new(Encoding.UTF8.GetBytes("""{"Label":"fwd","Count":1}"""));
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Label).IsEqualTo("fwd");
    }

    [Test]
    public async Task TryLoadFromStreamForwardOnlyLargerThanFormer1MiBCapSucceeds()
    {
        string json = "{\"Label\":\"" + new string('a', 1_048_577) + "\",\"Count\":1}";
        using ForwardOnlyStream stream = new(Encoding.UTF8.GetBytes(json));
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryLoadFromStreamDisposedStreamReturnsFalse()
    {
        MemoryStream stream = _Utf8Stream("""{"Label":"x","Count":1}""");
        stream.Dispose();
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("not readable");
    }

    [Test]
    public async Task JsonConfigStreamTryLoadValidJsonReturnsTrue()
    {
        using MemoryStream stream = _Utf8Stream("""{"Label":"pub","Count":2}""");
        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Label).IsEqualTo("pub");
    }

    [Test]
    public async Task JsonConfigStreamTryLoadPayloadLargerThanFormer1MiBCapSucceeds()
    {
        string json = "{\"Label\":\"" + new string('a', 1_048_577) + "\",\"Count\":1}";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task JsonConfigStreamTryLoadWarningValidWarningNull()
    {
        using MemoryStream stream = _Utf8Stream("""{"Label":"ok","Count":3}""");
        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "grp",
            "grp.file",
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsTrue();
        await Assert.That(warning).IsNull();
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.Count).IsEqualTo(3);
    }

    [Test]
    public async Task JsonConfigStreamTryLoadMalformedSetsWarning()
    {
        using MemoryStream stream = _Utf8Stream("{");
        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "grp",
            "grp.file",
            out TestSimpleConfig? value,
            out SettingsLoadWarning? warning);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.GroupName).IsEqualTo("grp");
        await Assert.That(warning.Value.SettingName).IsEqualTo("grp.file");
    }

    [Test]
    public async Task JsonConfigStreamTryLoadNullStreamThrows()
    {
        await Assert.That(() => JsonConfigStream.TryLoad(
                null!,
                TestJsonContext.Default.TestSimpleConfig,
                out _,
                out string? _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task JsonConfigStreamTryLoadNullTypeInfoThrows()
    {
        using MemoryStream stream = _Utf8Stream("{}");
        await Assert.That(() => JsonConfigStream.TryLoad<TestSimpleConfig>(
                stream,
                null!,
                out _,
                out string? _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task JsonConfigStreamTryLoadWarningNullSettingNameThrows()
    {
        using MemoryStream stream = _Utf8Stream("{}");
        await Assert.That(() => JsonConfigStream.TryLoad(
                stream,
                TestJsonContext.Default.TestSimpleConfig,
                "grp",
                null!,
                out _,
                out SettingsLoadWarning? _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryLoadFromStreamReadThrowsIoExceptionReturnsFalse()
    {
        using ThrowingReadStream stream = new();
        bool result = _TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("Failed to read");
    }

    [Test]
    public async Task JsonConfigStreamTryLoadWarningNullGroupThrows()
    {
        using MemoryStream stream = _Utf8Stream("{}");
        await Assert.That(() => JsonConfigStream.TryLoad(
                stream,
                TestJsonContext.Default.TestSimpleConfig,
                null!,
                "s",
                out _,
                out SettingsLoadWarning? _))
            .Throws<ArgumentNullException>();
    }

    private static MemoryStream _Utf8Stream(string json) =>
        new(Encoding.UTF8.GetBytes(json), writable: false);

    // === Bounded stream loads ===

    [Test]
    public async Task ANonSeekableStreamAboveTheSizeLimitIsRefusedWithTheLimitAndNothingMoreIsRead()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"Label\":\"" + new string('x', 5_000) + "\",\"Count\":1}");
        using ForwardOnlyStream stream = new(data);
        SettingsLimitValues limits = new() { MaxFileSize = LimitValue.Of(1024) };

        bool result = JsonConfigStream.TryLoad(
            stream, TestJsonContext.Default.TestSimpleConfig, out TestSimpleConfig? value, out string? error, limits);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error!).Contains("settings.maxFileSize");
        await Assert.That(stream.BytesRead).IsLessThanOrEqualTo(1025L);
    }

    [Test]
    public async Task ASeekableStreamAboveTheSizeLimitIsRefusedToo()
    {
        using MemoryStream stream = _Utf8Stream("{\"Label\":\"" + new string('x', 5_000) + "\",\"Count\":1}");

        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? _,
            out string? error,
            new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });

        await Assert.That(result).IsFalse();
        await Assert.That(error!).Contains("Limit: 1 KiB (setting settings.maxFileSize)");
    }

    [Test]
    public async Task AStreamOfExactlyTheSizeLimitIsLoaded()
    {
        string json = "{\"Label\":\"x\",\"Count\":1}";
        using MemoryStream stream = _Utf8Stream(json + new string(' ', 1024 - json.Length));

        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? value,
            out string? _,
            new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });

        await Assert.That(result).IsTrue();
        await Assert.That(value!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AStreamDeeperThanTheDefaultDepthIsRefusedWithoutAnyArgument()
    {
        StringBuilder builder = new("{\"Label\":\"x\",\"Count\":1,\"extra\":");
        builder.Append('[', 70);
        builder.Append(']', 70);
        builder.Append('}');
        using MemoryStream stream = _Utf8Stream(builder.ToString());

        bool result = JsonConfigStream.TryLoad(stream, TestJsonContext.Default.TestSimpleConfig, out TestSimpleConfig? _, out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(error!).Contains("Failed to parse JSON");
    }

    [Test]
    public async Task AStreamOfAnyDepthIsLoadedWhenTheDepthLimitIsOff()
    {
        StringBuilder builder = new("{\"Label\":\"x\",\"Count\":1,\"extra\":");
        builder.Append('[', 200);
        builder.Append(']', 200);
        builder.Append('}');
        using MemoryStream stream = _Utf8Stream(builder.ToString());

        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            out TestSimpleConfig? _,
            out string? _,
            new SettingsLimitValues { MaxJsonDepth = LimitValue.Off });

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TheWarningOverloadOfTheStreamLoaderAlsoTakesTheLimits()
    {
        using MemoryStream stream = _Utf8Stream("{\"Label\":\"" + new string('x', 5_000) + "\",\"Count\":1}");

        bool result = JsonConfigStream.TryLoad(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "reader",
            "reader.config_file",
            out TestSimpleConfig? _,
            out SettingsLoadWarning? warning,
            new SettingsLimitValues { MaxFileSize = LimitValue.Of(1024) });

        await Assert.That(result).IsFalse();
        await Assert.That(warning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.ExternalConfigUnavailable);
        await Assert.That(warning.Value.Message).Contains("settings.maxFileSize");
    }

    [Test]
    public async Task ALimitOutsideItsRangeIsRefusedWithAnException()
    {
        using MemoryStream stream = _Utf8Stream("{}");

        await Assert.That(() => JsonConfigStream.TryLoad(
                stream,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? _,
                out string? _,
                new SettingsLimitValues { MaxFileSize = LimitValue.Of(1) }))
            .Throws<ArgumentOutOfRangeException>();
    }

    // === Failures of a file that the loader returns instead of throwing ===

    [Test]
    public async Task APathWithANulCharacterIsReturnedAsAnErrorAndNotThrown()
    {
        using SettingsTempFolder folder = new();

        bool result = _TryLoad("a\0b.json", folder.Path, TestJsonContext.Default.TestSimpleConfig, out TestSimpleConfig? value, out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsEqualTo("Configuration file path is not a valid path.");
    }

    [Test]
    public async Task AConverterThatRefusesTheContentIsReturnedAsAnErrorFromAFileAndFromAStream()
    {
        using SettingsTempFolder folder = new();
        folder.Write("bad.json", "{\"Label\":\"x\"}");
        using MemoryStream stream = _Utf8Stream("{\"Label\":\"x\"}");

        bool fromFile = _TryLoad("bad.json", folder.Path, TestJsonContext.Default.TestThrowingConfig, out TestThrowingConfig? fileValue, out string? fileError);
        bool fromStream = _TryLoadFromStream(stream, TestJsonContext.Default.TestThrowingConfig, "configuration stream", out TestThrowingConfig? _, out string? streamError);

        await Assert.That(fromFile).IsFalse();
        await Assert.That(fileValue).IsNull();
        await Assert.That(fileError!).Contains("Failed to parse JSON in 'bad.json'");
        await Assert.That(fromStream).IsFalse();
        await Assert.That(streamError!).Contains("type discriminator is unknown");
    }

    [Test]
    public async Task AFileDeeperThanTheLimitIsRefusedWithTheDepthLimitAndAShallowSyntaxErrorIsNot()
    {
        using SettingsTempFolder folder = new();
        folder.Write("deep.json", "{\"Label\":\"x\",\"Count\":1,\"extra\":[[[[[1]]]]]}");
        folder.Write("shallow.json", "{\"Label\":\"x\",\"Count\":");

        bool deep = JsonConfigFile.TryLoad(
            "deep.json", folder.Path, TestJsonContext.Default.TestSimpleConfig, LimitValue.Off, LimitValue.Of(4), out TestSimpleConfig? _, out string? deepError);
        bool shallow = JsonConfigFile.TryLoad(
            "shallow.json", folder.Path, TestJsonContext.Default.TestSimpleConfig, LimitValue.Off, LimitValue.Of(4), out TestSimpleConfig? _, out string? shallowError);

        await Assert.That(deep).IsFalse();
        await Assert.That(deepError!).Contains("Limit: 4 (setting settings.maxJsonDepth)");
        await Assert.That(shallow).IsFalse();
        await Assert.That(shallowError!).Contains("Failed to parse JSON");
    }

    [Test]
    public async Task AFileOfExactlyTheDepthLimitIsLoaded()
    {
        using SettingsTempFolder folder = new();
        folder.Write("edge.json", "{\"Label\":\"x\",\"Count\":1,\"extra\":[[[1]]]}");

        bool result = JsonConfigFile.TryLoad(
            "edge.json", folder.Path, TestJsonContext.Default.TestSimpleConfig, LimitValue.Off, LimitValue.Of(4), out TestSimpleConfig? _, out string? _);

        await Assert.That(result).IsTrue();
    }

    /// <summary>Loads a file with the default limits of the library, like a manager that kept every default.</summary>
    /// <typeparam name="T">The configuration type.</typeparam>
    /// <param name="filePath">The path of the file.</param>
    /// <param name="baseDirectory">The directory that confines the path.</param>
    /// <param name="typeInfo">The type info of the configuration type.</param>
    /// <param name="value">The loaded object on success.</param>
    /// <param name="error">The reason on failure.</param>
    /// <returns><see langword="true"/> when the file was loaded.</returns>
    private static bool _TryLoad<T>(
        string filePath,
        string? baseDirectory,
        JsonTypeInfo<T> typeInfo,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value,
        out string? error)
        where T : class =>
        JsonConfigFile.TryLoad(
            filePath,
            baseDirectory,
            typeInfo,
            SettingsLimits.MaxFileSize.DefaultValue,
            SettingsLimits.MaxJsonDepth.DefaultValue,
            out value,
            out error);

    /// <summary>Loads a stream with the default limits of the library.</summary>
    /// <typeparam name="T">The configuration type.</typeparam>
    /// <param name="stream">The stream to read.</param>
    /// <param name="typeInfo">The type info of the configuration type.</param>
    /// <param name="label">The label for error text.</param>
    /// <param name="value">The loaded object on success.</param>
    /// <param name="error">The reason on failure.</param>
    /// <returns><see langword="true"/> when the stream was loaded.</returns>
    private static bool _TryLoadFromStream<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string label,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value,
        out string? error)
        where T : class =>
        JsonConfigFile.TryLoadFromStream(stream, typeInfo, label, SettingsLimitValues.Default, out value, out error);

    /// <summary>Readable stream with <see cref="Stream.CanRead"/> false.</summary>
    private sealed class NonReadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Forwards bytes without seeking so the bounded-copy path is used.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _Inner;

        public ForwardOnlyStream(byte[] data) =>
            _Inner = new MemoryStream(data, writable: false);

        /// <summary>The bytes that the reader took from the stream so far.</summary>
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _Inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _Inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _Inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Non-seekable stream whose <see cref="Read(byte[], int, int)"/> always throws <see cref="IOException"/>.</summary>
    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated read failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
