// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// A <see langword="ref struct"/> facade for registering settings during protocol setup and for the option settings of
/// plugin sources and exporters.
/// <para>
/// Because this is a <see langword="ref struct"/>, it cannot be stored as a field,
/// preventing protocols from keeping a reference to the underlying
/// <see cref="SettingsManager"/>. Obtain an instance via
/// <see cref="IStackBuilder.SettingsRegistrar"/>, or create one over a manager you own with the public constructor
/// (plugins do this for the short-lived option managers of their requests).
/// </para>
/// <para>
/// Thread safety: as safe as the wrapped manager (all <see cref="SettingsManager"/> members are thread-safe).
/// </para>
/// <para>
/// Exceptions: every registering member forwards to <see cref="SettingsManager.RegisterGroup"/> or
/// <see cref="SettingsManager.RegisterSetting"/> and surfaces what they throw (an invalid name or UI name, a duplicate
/// name). The typed members first build the <see cref="Setting"/> through the matching <c>Setting</c> factory, whose
/// checks (an invalid name or UI name, invalid min/max/default constraints) throw before the manager is touched. The
/// typed members always use the factory default for the setting activation (<see cref="SettingActivation.Immediate"/>); a
/// plugin that needs another activation passes a prepared <see cref="Setting"/> to <see cref="RegisterSetting"/>. A
/// <c>default(SettingsRegistrar)</c> throws <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
public readonly ref struct SettingsRegistrar
{
    /// <summary>The wrapped manager; <see langword="null"/> only for <c>default(SettingsRegistrar)</c>. Read it through <see cref="_Manager"/>.</summary>
    private readonly SettingsManager? _Wrapped;

    /// <summary>
    /// Creates a registrar that registers groups and settings into <paramref name="manager"/>. The registrar is a
    /// <see langword="ref struct"/>, so it cannot be stored and nobody keeps a reference to the manager through it.
    /// </summary>
    /// <param name="manager">The manager that receives the registered groups and settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public SettingsRegistrar(SettingsManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _Wrapped = manager;
    }

    /// <summary>
    /// The wrapped manager. A <see langword="ref struct"/> cannot forbid <c>default(SettingsRegistrar)</c>, so every
    /// member reads the manager through this guard and fails with a clear message instead of a null reference.
    /// </summary>
    /// <exception cref="InvalidOperationException">The registrar is <c>default</c> and was not created with a manager.</exception>
    private SettingsManager _Manager =>
        _Wrapped
        ?? throw new InvalidOperationException("The registrar was not created with a settings manager.");

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

    /// <summary>Registers a raw setting instance (needed for options the typed members do not cover, such as another activation).</summary>
    /// <param name="setting">The setting to register.</param>
    /// <returns>The load result (a persisted value is applied on registration) and the current value of the setting.</returns>
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
