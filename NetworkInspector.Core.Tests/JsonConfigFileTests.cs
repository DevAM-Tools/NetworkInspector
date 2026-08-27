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
/// <see cref="SettingsManagerExtensions.TryLoadReferencedJsonConfig{T, TSettings}"/>
/// (the public extension on <see cref="IReadOnlySettingsManager"/>).
/// </summary>
internal sealed class JsonConfigFileTests
{
    // === JsonConfigFile.TryLoad — direct tests ===

    [Test]
    public async Task TryLoad_FileNotFound_ReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_MalformedJson_ReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json !!!").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_JsonNullLiteral_ReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "null").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_ValidJson_ReturnsTrue_AndDeserializedValue()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Label\":\"test\",\"Count\":42}").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_NullBaseDirectory_ReturnsFalse()
    {
        bool result = JsonConfigFile.TryLoad(
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
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDir);

        using SettingsManager mgr = new(storageDir);
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
    public async Task TryLoadReferencedJsonConfig_ValidFile_ReturnsTrue_WarningNull()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            await File.WriteAllTextAsync(configPath, "{\"Label\":\"hello\",\"Count\":7}").ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir);
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
    public async Task TryLoadReferencedJsonConfig_MalformedJson_ReturnsFalse_WarningWithKind()
    {
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(storageDir);
            string configPath = Path.Combine(storageDir, "config.json");
            await File.WriteAllTextAsync(configPath, "<<<not-json>>>").ConfigureAwait(false);

            using SettingsManager mgr = new(storageDir);
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
        string storageDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDir);

        using SettingsManager mgr = new(storageDir);
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
    public async Task TryLoad_PathOutsideBaseDirectory_ReturnsFalseWithError()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(baseDir);

            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_PathWithDotDot_ReturnsFalseWithError()
    {
        bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_FileNameWithConsecutiveDots_LoadsWhenUnderBase()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "foo..bar.json");
            await File.WriteAllTextAsync(path, "{\"Label\":\"dots\",\"Count\":3}").ConfigureAwait(false);

            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoad_EmptyPath_ReturnsFalseWithError()
    {
        bool result = JsonConfigFile.TryLoad(
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
    public async Task PathResolution_IsPathUnderBase_EqualPath_ReturnsTrue()
    {
        MethodInfo? isUnderBase = typeof(JsonConfigFile).GetMethod(
            "_IsPathUnderBase", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(isUnderBase).IsNotNull();

        string basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "json-base"));
        bool underBase = (bool)isUnderBase!.Invoke(null, [basePath, basePath])!;
        await Assert.That(underBase).IsTrue();
    }

    [Test]
    public async Task PathResolution_TryResolvePath_RejectsDotDotSegments()
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
    public async Task TryLoad_OversizedFile_ReturnsFalseWithError()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "huge.json");
        try
        {
            await using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(SettingsFileAccess.MaxFileBytes + 1);
            }

            bool result = JsonConfigFile.TryLoad(
                "huge.json",
                baseDirectory: dir,
                TestJsonContext.Default.TestSimpleConfig,
                out TestSimpleConfig? value,
                out string? error);

            await Assert.That(result).IsFalse();
            await Assert.That(value).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!).Contains("exceeds");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryLoad_AbsolutePathOutsideBase_DoesNotEchoFullPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string outside = Path.Combine(Path.GetTempPath(), "outside-secret.json");
            bool result = JsonConfigFile.TryLoad(
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
    public async Task TryLoadReferencedJsonConfig_WhitespaceStoragePath_ReturnsWarning()
    {
        using SettingsManager mgr = new("   ");
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
    public async Task TryLoadReferencedJsonConfig_NoStoragePath_AbsolutePathOutsideAppBase_ReturnsFalse()
    {
        string outsideDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
    public async Task SafeFileLabel_EmptyFileName_ReturnsGenericLabel()
    {
        string path = OperatingSystem.IsWindows() ? @"C:\" : "/";
        await Assert.That(SettingsFileAccess.SafeFileLabel(path)).IsEqualTo("configuration file");
        await Assert.That(SettingsFileAccess.SafeFileLabel(string.Empty)).IsEqualTo("configuration file");
    }

    [Test]
    public async Task OpenSharedRead_AllowsSecondReader()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
    public async Task PathResolution_IsPathUnderBase_CaseMismatch_MatchesOsRules()
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
    public async Task PathResolution_TryResolvePath_NullBase_ReturnsFalse()
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
    public async Task TryLoadFromStream_ValidJson_ReturnsTrue()
    {
        using MemoryStream stream = _Utf8Stream("""{"Label":"from-stream","Count":9}""");
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task TryLoadFromStream_MalformedJson_ReturnsFalseWithError()
    {
        using MemoryStream stream = _Utf8Stream("{ not json");
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task TryLoadFromStream_JsonNullLiteral_ReturnsFalseWithError()
    {
        using MemoryStream stream = _Utf8Stream("null");
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task TryLoadFromStream_NotReadable_ReturnsFalse()
    {
        using NonReadableStream stream = new();
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task TryLoadFromStream_OversizedSeekable_ReturnsFalse()
    {
        using MemoryStream stream = new();
        stream.SetLength(SettingsFileAccess.MaxFileBytes + 1);
        stream.Position = 0;

        bool result = JsonConfigFile.TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("exceeds");
    }

    [Test]
    public async Task TryLoadFromStream_ForwardOnlyValidJson_ReturnsTrue()
    {
        using ForwardOnlyStream stream = new(Encoding.UTF8.GetBytes("""{"Label":"fwd","Count":1}"""));
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task TryLoadFromStream_ForwardOnlyOversized_ReturnsFalse()
    {
        using OversizedForwardOnlyStream stream = new(SettingsFileAccess.MaxFileBytes + 1);
        bool result = JsonConfigFile.TryLoadFromStream(
            stream,
            TestJsonContext.Default.TestSimpleConfig,
            "configuration stream",
            out TestSimpleConfig? value,
            out string? error);

        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("exceeds");
    }

    [Test]
    public async Task TryLoadFromStream_DisposedStream_ReturnsFalse()
    {
        MemoryStream stream = _Utf8Stream("""{"Label":"x","Count":1}""");
        stream.Dispose();
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task JsonConfigStream_TryLoad_ValidJson_ReturnsTrue()
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
    public async Task JsonConfigStream_TryLoadWarning_Valid_WarningNull()
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
    public async Task JsonConfigStream_TryLoad_Malformed_SetsWarning()
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
    public async Task JsonConfigStream_TryLoad_NullStream_Throws()
    {
        await Assert.That(() => JsonConfigStream.TryLoad(
                null!,
                TestJsonContext.Default.TestSimpleConfig,
                out _,
                out string? _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task JsonConfigStream_TryLoad_NullTypeInfo_Throws()
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
    public async Task JsonConfigStream_TryLoadWarning_NullSettingName_Throws()
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
    public async Task TryLoadFromStream_ReadThrowsIoException_ReturnsFalse()
    {
        using ThrowingReadStream stream = new();
        bool result = JsonConfigFile.TryLoadFromStream(
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
    public async Task JsonConfigStream_TryLoadWarning_NullGroup_Throws()
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
        public override int Read(byte[] buffer, int offset, int count) => _Inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _Inner.Read(buffer);
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

    /// <summary>Non-seekable source that yields a fixed number of zero bytes then EOF.</summary>
    private sealed class OversizedForwardOnlyStream : Stream
    {
        private long _Remaining;

        public OversizedForwardOnlyStream(long length) => _Remaining = length;

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
            if (_Remaining <= 0)
            {
                return 0;
            }

            int n = (int)Math.Min(count, _Remaining);
            _Remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
