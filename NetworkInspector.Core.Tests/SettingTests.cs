// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Setting"/> — factory methods, pending/current value model,
/// validation, min/max constraints, dirty tracking, and enum settings.
/// </summary>
internal sealed class SettingTests
{
    // === Bool Factory ===

    [Test]
    public async Task Bool_FactoryCreatesCorrectSetting()
    {
        Setting s = Setting.Bool("test.enabled", "Enabled", "test", true, "A test setting");
        await Assert.That(s.Name).IsEqualTo("test.enabled");
        await Assert.That(s.UiName).IsEqualTo("Enabled");
        await Assert.That(s.GroupName).IsEqualTo("test");
        await Assert.That(s.Type).IsEqualTo(SettingType.Bool);
        s.DefaultValue.TryGetAsBool(out bool defBool);
        s.Value.TryGetAsBool(out bool valBool);
        await Assert.That(defBool).IsTrue();
        await Assert.That(valBool).IsTrue();
        await Assert.That(s.Description).IsEqualTo("A test setting");
    }

    [Test]
    public async Task Bool_NullDescription()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        await Assert.That(s.Description).IsNull();
    }

    // === String Factory ===

    [Test]
    public async Task String_FactoryCreatesCorrectSetting()
    {
        Setting s = Setting.String("test.name", "Name", "test", "default");
        await Assert.That(s.Type).IsEqualTo(SettingType.String);
        s.Value.TryGetAsString(out string strVal);
        await Assert.That(strVal).IsEqualTo("default");
    }

    // === F64 Factory ===

    [Test]
    public async Task F64_NoConstraints_Succeeds()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5);
        await Assert.That(s.Type).IsEqualTo(SettingType.F64);
        s.Value.TryGetAsF64(out double f64Val);
        await Assert.That(f64Val).IsEqualTo(0.5);
    }

    [Test]
    public async Task F64_WithMinMax_Succeeds()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5, min: 0.0, max: 1.0);
        await Assert.That(s).IsNotNull();
    }

    [Test]
    public async Task F64_MinGreaterThanMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", 0.5, min: 1.0, max: 0.0));
    }

    [Test]
    public async Task F64_DefaultBelowMin_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", -1.0, min: 0.0));
    }

    [Test]
    public async Task F64_DefaultAboveMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", 2.0, max: 1.0));
    }

    [Test]
    public async Task F64_NonFiniteDefault_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", double.NaN));
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", double.PositiveInfinity));
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", double.NegativeInfinity));
    }

    [Test]
    public async Task F64_NonFiniteMin_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", 0.5, min: double.NegativeInfinity));
    }

    [Test]
    public async Task F64_NonFiniteMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.F64("test.ratio", "Ratio", "test", 0.5, max: double.PositiveInfinity));
    }

    // === U64 Factory ===

    [Test]
    public async Task U64_Factory_Succeeds()
    {
        Setting s = Setting.U64("test.count", "Count", "test", 10, min: 0, max: 100);
        s.Value.TryGetAsU64(out ulong u64Val);
        await Assert.That(u64Val).IsEqualTo(10UL);
    }

    [Test]
    public async Task U64_DefaultBelowMin_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.U64("test.count", "Count", "test", 0, min: 5));
    }

    // === I64 Factory ===

    [Test]
    public async Task I64_Factory_Succeeds()
    {
        Setting s = Setting.I64("test.offset", "Offset", "test", 0, min: -100, max: 100);
        s.Value.TryGetAsI64(out long i64Val);
        await Assert.That(i64Val).IsEqualTo(0L);
    }

    [Test]
    public async Task I64_MinGreaterThanMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.I64("test.offset", "Offset", "test", 0, min: 100, max: -100));
    }

    // === Bytes Factory ===

    [Test]
    public async Task Bytes_Factory_Succeeds()
    {
        Setting s = Setting.Bytes("test.data", "Data", "test", [1, 2, 3]);
        await Assert.That(s.Type).IsEqualTo(SettingType.Bytes);
        s.Value.TryGetAsBytes(out byte[] bytesVal);
        await Assert.That(bytesVal.Length).IsEqualTo(3);
    }

    // === Enum Factory ===

    [Test]
    public async Task Enum_FromPairs_Succeeds()
    {
        Setting s = Setting.Enum(
            "test.level", "Level", "test", 1,
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        await Assert.That(s.Type).IsEqualTo(SettingType.Enum);
        bool enumOk = s.Value.TryGetAsEnum(out (string Name, ulong Value) e);
        await Assert.That(enumOk).IsTrue();
        await Assert.That(e.Name).IsEqualTo("Medium");
        await Assert.That(e.Value).IsEqualTo(1UL);
    }

    [Test]
    public async Task Enum_EmptyAllowed_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.Enum(
                "test.level", "Level", "test", 0,
                []));
    }

    [Test]
    public async Task Enum_DefaultNotInAllowed_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.Enum(
                "test.level", "Level", "test", 99,
                [("Low", 0), ("High", 1)]));
    }

    // === Pending/Current Value Model ===

    [Test]
    public async Task SetPendingValue_Changes_MakesSettingDirty()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        await Assert.That(s.IsDirty).IsFalse();

        bool changed = s.SetPendingValue(SettingValue.Bool(true));
        await Assert.That(changed).IsTrue();
        await Assert.That(s.IsDirty).IsTrue();
        s.PendingValue.TryGetAsBool(out bool pendBool);
        await Assert.That(pendBool).IsTrue();
        // Current value unchanged until Apply
        s.Value.TryGetAsBool(out bool curBool);
        await Assert.That(curBool).IsFalse();
    }

    [Test]
    public async Task SetPendingValue_SameValue_ReturnsFalse()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        bool changed = s.SetPendingValue(SettingValue.Bool(false));
        await Assert.That(changed).IsFalse();
        await Assert.That(s.IsDirty).IsFalse();
    }

    [Test]
    public async Task Apply_MovesPendingToCurrent()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        s.SetPendingValue(SettingValue.Bool(true));
        bool changed = s.Apply();
        await Assert.That(changed).IsTrue();
        s.Value.TryGetAsBool(out bool appliedBool);
        await Assert.That(appliedBool).IsTrue();
        await Assert.That(s.IsDirty).IsFalse();
    }

    [Test]
    public async Task Apply_NoPending_ReturnsFalse()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        bool changed = s.Apply();
        await Assert.That(changed).IsFalse();
    }

    [Test]
    public async Task Reset_DiscardsPendingValue()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        s.SetPendingValue(SettingValue.Bool(true));
        await Assert.That(s.IsDirty).IsTrue();
        s.Reset();
        await Assert.That(s.IsDirty).IsFalse();
        s.PendingValue.TryGetAsBool(out bool afterResetBool);
        await Assert.That(afterResetBool).IsFalse();
    }

    [Test]
    public async Task ResetToDefault_ResetsBothValues()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        s.SetPendingValue(SettingValue.Bool(true));
        _ = s.Apply(); // current = true
        s.ResetToDefault();
        s.Value.TryGetAsBool(out bool defValBool);
        s.PendingValue.TryGetAsBool(out bool defPendBool);
        await Assert.That(defValBool).IsFalse();
        await Assert.That(defPendBool).IsFalse();
        await Assert.That(s.IsDirty).IsFalse();
    }

    // === Validation ===

    [Test]
    public async Task SetPendingValue_TypeMismatch_Fails()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        _ = Assert.Throws<TypeMismatchSettingsException>(
            () => s.SetPendingValue(SettingValue.String("wrong")));
    }

    [Test]
    public async Task SetPendingValue_F64_BelowMin_Fails()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5, min: 0.0, max: 1.0);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.F64(-0.1)));
    }

    [Test]
    public async Task SetPendingValue_F64_AboveMax_Fails()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5, min: 0.0, max: 1.0);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.F64(1.1)));
    }

    [Test]
    public async Task SetPendingValue_F64_NonFinite_Fails()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.F64(double.NaN)));
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.F64(double.PositiveInfinity)));
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.F64(double.NegativeInfinity)));
    }

    [Test]
    public async Task SetPendingValue_F64_AtBounds_Succeeds()
    {
        Setting s = Setting.F64("test.ratio", "Ratio", "test", 0.5, min: 0.0, max: 1.0);

        bool r1 = s.SetPendingValue(SettingValue.F64(0.0));
        await Assert.That(r1).IsTrue();

        bool r2 = s.SetPendingValue(SettingValue.F64(1.0));
        await Assert.That(r2).IsTrue();
    }

    [Test]
    public async Task SetPendingValue_U64_BelowMin_Fails()
    {
        Setting s = Setting.U64("test.port", "Port", "test", 8080, min: 1024, max: 65535);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.U64(80)));
    }

    [Test]
    public async Task SetPendingValue_Enum_InvalidValue_Fails()
    {
        Setting s = Setting.Enum("test.level", "Level", "test", 0,
            [("Low", 0), ("High", 1)]);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.Enum("Invalid", 99)));
    }

    [Test]
    public async Task SetPendingValue_Enum_ValidValue_Succeeds()
    {
        Setting s = Setting.Enum("test.level", "Level", "test", 0,
            [("Low", 0), ("High", 1)]);
        bool changed = s.SetPendingValue(SettingValue.Enum("High", 1));
        await Assert.That(changed).IsTrue();
    }

    // === Group Name Lowercase Rule ===

    [Test]
    public async Task Bool_UppercaseGroupName_Fails()
    {
        // Group names must be lowercase dot-separated identifiers.
        _ = Assert.Throws<InvalidNameRegistrationException>(
            () => Setting.Bool("test.flag", "Flag", "MyGroup", true));
    }

    [Test]
    public async Task Bool_LowercaseGroupName_Succeeds()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "my.group", true);
        await Assert.That(s.GroupName).IsEqualTo("my.group");
    }

    [Test]
    public async Task F64_UppercaseGroupName_Fails()
    {
        _ = Assert.Throws<InvalidNameRegistrationException>(
            () => Setting.F64("test.ratio", "Ratio", "MyGroup", 0.5));
    }

    [Test]
    public async Task U64_MinGreaterThanMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.U64("test.count", "Count", "test", 10, min: 100, max: 1));
    }

    [Test]
    public async Task U64_DefaultAboveMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.U64("test.count", "Count", "test", 200, max: 100));
    }

    [Test]
    public async Task I64_DefaultBelowMin_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.I64("test.offset", "Offset", "test", -5, min: 0));
    }

    [Test]
    public async Task I64_DefaultAboveMax_Fails()
    {
        _ = Assert.Throws<ValidationSettingsException>(
            () => Setting.I64("test.offset", "Offset", "test", 5, max: 0));
    }

    [Test]
    public async Task MinMaxValue_ExposedForConstrainedNumericSettings()
    {
        Setting s = Setting.U64("test.port", "Port", "test", 8080, min: 1024, max: 65535);
        await Assert.That(s.MinValue).IsNotNull();
        await Assert.That(s.MaxValue).IsNotNull();
        SettingValue minVal = s.MinValue!.Value;
        SettingValue maxVal = s.MaxValue!.Value;
        minVal.TryGetAsU64(out ulong min);
        maxVal.TryGetAsU64(out ulong max);
        await Assert.That(min).IsEqualTo(1024UL);
        await Assert.That(max).IsEqualTo(65535UL);
    }

    [Test]
    public async Task SetPendingValue_String_ValidValue_Succeeds()
    {
        Setting s = Setting.String("test.name", "Name", "test", "default");
        bool changed = s.SetPendingValue(SettingValue.String("updated"));
        await Assert.That(changed).IsTrue();
        s.PendingValue.TryGetAsString(out string pending);
        await Assert.That(pending).IsEqualTo("updated");
    }

    [Test]
    public async Task SetPendingValue_Bytes_ValidValue_Succeeds()
    {
        Setting s = Setting.Bytes("test.data", "Data", "test", [1]);
        bool changed = s.SetPendingValue(SettingValue.Bytes([9, 8]));
        await Assert.That(changed).IsTrue();
        s.PendingValue.TryGetAsBytes(out byte[] pending);
        await Assert.That(pending[0]).IsEqualTo((byte)9);
    }

    [Test]
    public async Task SetPendingValue_U64_AboveMax_Fails()
    {
        Setting s = Setting.U64("test.port", "Port", "test", 8080, min: 1, max: 65535);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.U64(70000)));
    }

    [Test]
    public async Task SetPendingValue_U64_WithinRange_Succeeds()
    {
        Setting s = Setting.U64("test.port", "Port", "test", 8080, min: 1, max: 65535);
        bool changed = s.SetPendingValue(SettingValue.U64(9000));
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task SetPendingValue_I64_BelowMin_Fails()
    {
        Setting s = Setting.I64("test.offset", "Offset", "test", 0, min: -10, max: 10);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.I64(-11)));
    }

    [Test]
    public async Task SetPendingValue_I64_AboveMax_Fails()
    {
        Setting s = Setting.I64("test.offset", "Offset", "test", 0, min: -10, max: 10);
        _ = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.I64(11)));
    }

    [Test]
    public async Task SetPendingValue_I64_WithinRange_Succeeds()
    {
        Setting s = Setting.I64("test.offset", "Offset", "test", 0, min: -10, max: 10);
        bool changed = s.SetPendingValue(SettingValue.I64(5));
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task EnumWithNullMetadata_AcceptsValidPendingValue()
    {
        Setting setting = SettingsTestHelpers.CreateSettingForManagerValidationTests(
            "test.enum",
            "Enum",
            "test",
            SettingType.Enum,
            SettingValue.Enum("Low", 0),
            enumMetadata: null);

        bool changed = setting.SetPendingValue(SettingValue.Enum("Low", 0));
        await Assert.That(changed).IsFalse();
    }

    [Test]
    public async Task ToString_IncludesNameTypeAndValue()
    {
        Setting s = Setting.Bool("test.flag", "Flag", "test", true);
        string text = s.ToString();
        await Assert.That(text).Contains("test.flag");
        await Assert.That(text).Contains("Bool");
    }

    [Test]
    public async Task SetPendingValue_EnumDisallowedNumeric_Throws()
    {
        Setting s = Setting.Enum("test.mode", "Mode", "test", 0UL,
            [("Low", 0UL), ("High", 1UL)]);
        ValidationSettingsException ex = Assert.Throws<ValidationSettingsException>(
            () => s.SetPendingValue(SettingValue.Enum("Low", 99UL)));
        await Assert.That(ex.Message).Contains("not an allowed enum value");
    }

    [Test]
    public async Task ValidateEnum_NullMetadata_ReturnsNull()
    {
        Setting setting = SettingsTestHelpers.CreateSettingForManagerValidationTests(
            "test.enum", "Enum", "test", SettingType.Enum, SettingValue.Enum("Low", 0), enumMetadata: null);
        MethodInfo? validateEnum = typeof(Setting).GetMethod(
            "_ValidateEnum", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(validateEnum).IsNotNull();
        string? result = (string?)validateEnum!.Invoke(setting, [SettingValue.Enum("Low", 0)]);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateEnum_InvalidEnumValue_ReturnsErrorMessage()
    {
        Setting setting = Setting.Enum("test.mode", "Mode", "test", 0UL, [("Low", 0UL)]);
        MethodInfo? validateEnum = typeof(Setting).GetMethod(
            "_ValidateEnum", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(validateEnum).IsNotNull();
        string? result = (string?)validateEnum!.Invoke(setting, [SettingValue.U64(1UL)]);
        await Assert.That(result).IsEqualTo("Invalid enum value");
    }

    [Test]
    public async Task ApplyFromPersistence_TypeMismatch_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);

        MethodInfo? apply = typeof(Setting).GetMethod(
            "ApplyFromPersistence", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(apply).IsNotNull();

        TargetInvocationException tie = Assert.Throws<TargetInvocationException>(
            () => apply!.Invoke(s, [SettingValue.U64(1)]));
        await Assert.That(tie.InnerException).IsTypeOf<TypeMismatchSettingsException>();
    }

    [Test]
    public async Task ApplyFromPersistence_ValidationFailed_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.U64("test.port", "Port", "test", 8080UL, min: 1024UL, max: 65535UL);
        mgr.RegisterSetting(s);

        MethodInfo? apply = typeof(Setting).GetMethod(
            "ApplyFromPersistence", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(apply).IsNotNull();

        TargetInvocationException tie = Assert.Throws<TargetInvocationException>(
            () => apply!.Invoke(s, [SettingValue.U64(80UL)]));
        await Assert.That(tie.InnerException).IsTypeOf<ValidationSettingsException>();
    }

    [Test]
    public async Task SetPendingValue_WhileManagerLoading_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);

        System.Reflection.FieldInfo? loadingField = typeof(SettingsManager).GetField(
            "_IsLoading", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(loadingField).IsNotNull();
        loadingField!.SetValue(mgr, 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => s.SetPendingValue(SettingValue.Bool(true)));
        await Assert.That(ex.Message).Contains("Load()");
    }

    [Test]
    public async Task Apply_WhileManagerLoading_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));

        System.Reflection.FieldInfo? loadingField = typeof(SettingsManager).GetField(
            "_IsLoading", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(loadingField).IsNotNull();
        loadingField!.SetValue(mgr, 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => s.Apply());
        await Assert.That(ex.Message).Contains("Load()");
    }

    [Test]
    public async Task Reset_WhileManagerLoading_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);
        s.SetPendingValue(SettingValue.Bool(true));

        System.Reflection.FieldInfo loadingField = typeof(SettingsManager).GetField(
            "_IsLoading", BindingFlags.NonPublic | BindingFlags.Instance)!;
        loadingField.SetValue(mgr, 1);
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => s.Reset());
            await Assert.That(ex.Message).Contains("Load()");
        }
        finally
        {
            loadingField.SetValue(mgr, 0);
        }
    }

    [Test]
    public async Task ResetToDefault_WhileManagerLoading_Throws()
    {
        using SettingsManager mgr = new();
        Setting s = Setting.Bool("test.flag", "Flag", "test", false);
        mgr.RegisterSetting(s);

        System.Reflection.FieldInfo loadingField = typeof(SettingsManager).GetField(
            "_IsLoading", BindingFlags.NonPublic | BindingFlags.Instance)!;
        loadingField.SetValue(mgr, 1);
        try
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => s.ResetToDefault());
            await Assert.That(ex.Message).Contains("Load()");
        }
        finally
        {
            loadingField.SetValue(mgr, 0);
        }
    }
}
