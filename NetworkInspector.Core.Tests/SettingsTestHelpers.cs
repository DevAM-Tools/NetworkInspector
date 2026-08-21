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
        // replace metadata to exercise manager-side re-validation.
        Setting setting = Setting.Bool("test.base", "Base", "test", false);
        _SetAutoProperty(setting, nameof(Setting.Name), name);
        _SetAutoProperty(setting, nameof(Setting.UiName), uiName);
        _SetAutoProperty(setting, nameof(Setting.GroupName), groupName);
        _SetAutoProperty(setting, nameof(Setting.Type), type);
        _SetAutoProperty(setting, nameof(Setting.DefaultValue), defaultValue);
        _SetAutoProperty(setting, nameof(Setting.MinValue), null);
        _SetAutoProperty(setting, nameof(Setting.MaxValue), null);
        _SetAutoProperty(setting, nameof(Setting.EnumMetadata), enumMetadata);

        Type snapshotType = typeof(Setting).GetNestedType("SettingSnapshot", System.Reflection.BindingFlags.NonPublic)!;
        object snapshot = Activator.CreateInstance(snapshotType, defaultValue, defaultValue)!;
        _SetField(setting, "_Snapshot", snapshot);

        return setting;
    }

    internal static SettingValue WithSettingValueField<T>(SettingValue value, string fieldName, T fieldValue)
    {
        object boxed = value;
        _SetField(boxed, fieldName, fieldValue);
        return (SettingValue)boxed;
    }

    private static void _SetAutoProperty(object target, string propertyName, object? value)
    {
        string backingFieldName = string.Create(
            CultureInfo.InvariantCulture,
            $"<{propertyName}>k__BackingField");
        _SetField(target, backingFieldName, value);
    }

    private static void _SetField(object target, string fieldName, object? value)
    {
        System.Reflection.FieldInfo? field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field is null && fieldName.StartsWith('_') && fieldName.Length > 1)
        {
            string propertyName = fieldName[1..];
            if (char.IsLower(propertyName[0]))
            {
                propertyName = char.ToUpper(propertyName[0], CultureInfo.InvariantCulture) + propertyName[1..];
            }

            field = target.GetType().GetField(
                string.Create(CultureInfo.InvariantCulture, $"<{propertyName}>k__BackingField"),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        }

        field!.SetValue(target, value);
    }
}
