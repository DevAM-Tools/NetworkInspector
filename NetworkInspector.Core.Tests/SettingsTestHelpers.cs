// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Internal helpers for settings tests that need controlled instances bypassing
/// public factory validation (defensive paths in <see cref="SettingsManager"/>).
/// </summary>
internal static class SettingsTestHelpers
{
    internal static Setting CreateSettingForManagerValidationTests(
        string name,
        string uiName,
        string groupName,
        SettingType type,
        SettingValue defaultValue,
        EnumSettingMetadata? enumMetadata = null)
    {
        // Start from a valid setting so locks and snapshots are initialized, then
        // replace metadata fields to exercise manager-side re-validation.
        Setting setting = Setting.Bool("test.base", "Base", "test", false);
        _SetField(setting, "_Name", name);
        _SetField(setting, "_UiName", uiName);
        _SetField(setting, "_GroupName", groupName);
        _SetField(setting, "_Type", type);
        _SetField(setting, "_DefaultValue", defaultValue);
        _SetField(setting, "_MinValue", null);
        _SetField(setting, "_MaxValue", null);
        _SetField(setting, "_EnumMetadata", enumMetadata);

        Type snapshotType = typeof(Setting).GetNestedType("SettingSnapshot", System.Reflection.BindingFlags.NonPublic)!;
        object snapshot = Activator.CreateInstance(snapshotType, defaultValue, defaultValue)!;
        _SetField(setting, "_Snapshot", snapshot);

        return setting;
    }

    internal static SettingValue WithSettingValueField<T>(SettingValue value, string fieldName, T fieldValue)
    {
        object boxed = value;
        System.Reflection.FieldInfo field = typeof(SettingValue).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(boxed, fieldValue);
        return (SettingValue)boxed;
    }

    private static void _SetField(object target, string fieldName, object? value)
    {
        System.Reflection.FieldInfo field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(target, value);
    }
}
