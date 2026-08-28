// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// A <see langword="ref struct"/> facade for registering settings during protocol setup.
/// <para>
/// Because this is a <see langword="ref struct"/>, it cannot be stored as a field,
/// preventing protocols from keeping a reference to the underlying
/// <see cref="SettingsManager"/>. Obtain an instance via
/// <see cref="IStackBuilder.SettingsRegistrar"/>.
/// </para>
/// </summary>
public readonly ref struct SettingsRegistrar
{
    private readonly SettingsManager _Manager;

    /// <summary>Creates a new registrar wrapping the given settings manager.</summary>
    internal SettingsRegistrar(SettingsManager manager)
    {
        _Manager = manager;
    }

    #region Group Registration

    /// <summary>
    /// Registers a group with explicit UI name and description.
    /// Groups are also created implicitly when registering a setting with a new group name.
    /// </summary>
    public void RegisterGroup(
        string name, string uiName, string? description = null) =>
        _Manager.RegisterGroup(name, uiName, description);

    #endregion

    #region Setting Registration

    /// <summary>Registers a raw setting instance.</summary>
    public SettingRegistrationResult RegisterSetting(Setting setting) =>
        _Manager.RegisterSetting(setting);

    /// <summary>Registers a boolean setting.</summary>
    public SettingRegistrationResult RegisterBoolSetting(
        string name, string uiName, string groupName, bool defaultValue,
        string? description = null)
    {
        Setting setting = Setting.Bool(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a string setting.</summary>
    public SettingRegistrationResult RegisterStringSetting(
        string name, string uiName, string groupName, string defaultValue,
        string? description = null)
    {
        Setting setting = Setting.String(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a 64-bit floating-point setting with optional min/max bounds.</summary>
    public SettingRegistrationResult RegisterF64Setting(
        string name, string uiName, string groupName, double defaultValue,
        double? min = null, double? max = null, string? description = null)
    {
        Setting setting = Setting.F64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers an unsigned 64-bit integer setting with optional min/max bounds.</summary>
    public SettingRegistrationResult RegisterU64Setting(
        string name, string uiName, string groupName, ulong defaultValue,
        ulong? min = null, ulong? max = null, string? description = null)
    {
        Setting setting = Setting.U64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a signed 64-bit integer setting with optional min/max bounds.</summary>
    public SettingRegistrationResult RegisterI64Setting(
        string name, string uiName, string groupName, long defaultValue,
        long? min = null, long? max = null, string? description = null)
    {
        Setting setting = Setting.I64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a byte array setting.</summary>
    public SettingRegistrationResult RegisterBytesSetting(
        string name, string uiName, string groupName, byte[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.Bytes(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers an enum-like setting with constrained allowed values.</summary>
    public SettingRegistrationResult RegisterEnumSetting(
        string name, string uiName, string groupName, ulong defaultValue,
        EnumSettingMetadata metadata, string? description = null)
    {
        Setting setting = Setting.EnumWithMetadata(name, uiName, groupName, defaultValue, metadata, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a boolean array setting.</summary>
    public SettingRegistrationResult RegisterBoolArraySetting(
        string name, string uiName, string groupName, bool[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.BoolArray(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a string array setting.</summary>
    public SettingRegistrationResult RegisterStringArraySetting(
        string name, string uiName, string groupName, string[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.StringArray(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers an F64 array setting with optional per-element min/max bounds.</summary>
    public SettingRegistrationResult RegisterF64ArraySetting(
        string name, string uiName, string groupName, double[] defaultValue,
        double? min = null, double? max = null, string? description = null)
    {
        Setting setting = Setting.F64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers a U64 array setting with optional per-element min/max bounds.</summary>
    public SettingRegistrationResult RegisterU64ArraySetting(
        string name, string uiName, string groupName, ulong[] defaultValue,
        ulong? min = null, ulong? max = null, string? description = null)
    {
        Setting setting = Setting.U64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Registers an I64 array setting with optional per-element min/max bounds.</summary>
    public SettingRegistrationResult RegisterI64ArraySetting(
        string name, string uiName, string groupName, long[] defaultValue,
        long? min = null, long? max = null, string? description = null)
    {
        Setting setting = Setting.I64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }
    #endregion
}
