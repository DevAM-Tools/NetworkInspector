// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// A <see langword="ref struct"/> facade for registering settings during the setup of a component and for the option settings of
/// short-lived requests.
/// <para>
/// Because this is a <see langword="ref struct"/>, it cannot be stored as a field,
/// preventing components from keeping a reference to the underlying
/// <see cref="SettingsManager"/>. Obtain an instance via
/// the component's setup context, or create one over a manager you own with the public constructor
/// (an application does this for the short-lived option managers of its requests).
/// </para>
/// <para>
/// Thread safety: as safe as the wrapped manager (all <see cref="SettingsManager"/> members are thread-safe).
/// </para>
/// <para>
/// Exceptions: every registering member forwards to <see cref="SettingsManager.RegisterGroup"/> or
/// <see cref="SettingsManager.RegisterSetting"/> and surfaces what they throw (an invalid name or UI name, a duplicate
/// name, a setting that belongs to another manager). The typed members first build the <see cref="Setting"/> through the
/// matching <c>Setting</c> factory, whose checks (an invalid name or UI name, invalid min/max/default constraints) throw before
/// the manager is touched. The typed members always use the factory default for the setting activation
/// (<see cref="SettingActivation.Immediate"/>); a component that needs another activation passes a prepared <see cref="Setting"/>
/// to <see cref="RegisterSetting"/>. A <c>default(SettingsRegistrar)</c> throws <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
/// <seealso cref="SettingsManager"/>
/// <seealso cref="Setting"/>
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
    /// <param name="name">The group name: lower case, dot-separated ASCII identifier; not <c>default</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <exception cref="InvalidNameSettingsException">The name is not a valid group name.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A group of this name exists.</exception>
    public void RegisterGroup(
        string name, string uiName, string? description = null) =>
        _Manager.RegisterGroup(name, uiName, description);

    #endregion

    #region Setting Registration

    /// <summary>Registers a raw setting instance (needed for options the typed members do not cover, such as another activation).</summary>
    /// <param name="setting">The setting to register.</param>
    /// <returns>The load result (a persisted value is applied on registration) and the current value of the setting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setting"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The name of the setting is not valid.</exception>
    /// <exception cref="InvalidUiNameSettingsException">The UI name of the setting is not valid.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <exception cref="InvalidOperationException">The setting belongs to another manager.</exception>
    public SettingRegistrationResult RegisterSetting(Setting setting) =>
        _Manager.RegisterSetting(setting);

    /// <summary>Creates a boolean setting and registers it; the setting is bound to this manager.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.Bool"/>
    public SettingRegistrationResult RegisterBoolSetting(
        string name, string uiName, string groupName, bool defaultValue,
        string? description = null)
    {
        Setting setting = Setting.Bool(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a string setting and registers it; the setting is bound to this manager.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.greeting</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; not <see langword="null"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.String"/>
    public SettingRegistrationResult RegisterStringSetting(
        string name, string uiName, string groupName, string defaultValue,
        string? description = null)
    {
        Setting setting = Setting.String(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a 64-bit floating-point setting with optional min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; finite and inside the bounds.</param>
    /// <param name="min">The smallest accepted value, or <see langword="null"/> for none; finite.</param>
    /// <param name="max">The largest accepted value, or <see langword="null"/> for none; finite, not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ValidationSettingsException">The default or a bound is not finite, or the default is outside the bounds.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.F64"/>
    public SettingRegistrationResult RegisterF64Setting(
        string name, string uiName, string groupName, double defaultValue,
        double? min = null, double? max = null, string? description = null)
    {
        Setting setting = Setting.F64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates an unsigned 64-bit integer setting with optional min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds.</param>
    /// <param name="min">The smallest accepted value, or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value, or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ValidationSettingsException">The bounds are reversed, or the default is outside them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.U64"/>
    public SettingRegistrationResult RegisterU64Setting(
        string name, string uiName, string groupName, ulong defaultValue,
        ulong? min = null, ulong? max = null, string? description = null)
    {
        Setting setting = Setting.U64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a signed 64-bit integer setting with optional min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds.</param>
    /// <param name="min">The smallest accepted value, or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value, or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ValidationSettingsException">The bounds are reversed, or the default is outside them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.I64"/>
    public SettingRegistrationResult RegisterI64Setting(
        string name, string uiName, string groupName, long defaultValue,
        long? min = null, long? max = null, string? description = null)
    {
        Setting setting = Setting.I64(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a byte array setting and registers it; the default array is copied.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; not <see langword="null"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.Bytes"/>
    public SettingRegistrationResult RegisterBytesSetting(
        string name, string uiName, string groupName, byte[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.Bytes(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates an enum-like setting with constrained allowed values and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The numeric value used until a stored value or a change replaces it; one of the allowed values.</param>
    /// <param name="metadata">The allowed names and numbers; not <see langword="null"/>, at least one entry.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationSettingsException">There is no allowed value, or the default is not one of them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.EnumWithMetadata"/>
    public SettingRegistrationResult RegisterEnumSetting(
        string name, string uiName, string groupName, ulong defaultValue,
        EnumSettingMetadata metadata, string? description = null)
    {
        Setting setting = Setting.EnumWithMetadata(name, uiName, groupName, defaultValue, metadata, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a boolean array setting and registers it; the default array is copied.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; not <see langword="null"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.BoolArray"/>
    public SettingRegistrationResult RegisterBoolArraySetting(
        string name, string uiName, string groupName, bool[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.BoolArray(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a string array setting and registers it; the default array is copied.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">
    /// The value used until a stored value or a change replaces it; not <see langword="null"/>, no <see langword="null"/> element.
    /// </param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> or one of its elements is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.StringArray"/>
    public SettingRegistrationResult RegisterStringArraySetting(
        string name, string uiName, string groupName, string[] defaultValue,
        string? description = null)
    {
        Setting setting = Setting.StringArray(name, uiName, groupName, defaultValue, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates an F64 array setting with optional per-element min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; every element finite and inside the bounds.</param>
    /// <param name="min">The smallest accepted element, or <see langword="null"/> for none; finite.</param>
    /// <param name="max">The largest accepted element, or <see langword="null"/> for none; finite, not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationSettingsException">A bound or an element is not finite, the bounds are reversed, or an element is outside them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.F64Array"/>
    public SettingRegistrationResult RegisterF64ArraySetting(
        string name, string uiName, string groupName, double[] defaultValue,
        double? min = null, double? max = null, string? description = null)
    {
        Setting setting = Setting.F64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates a U64 array setting with optional per-element min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; every element inside the bounds.</param>
    /// <param name="min">The smallest accepted element, or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted element, or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationSettingsException">The bounds are reversed, or an element is outside them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.U64Array"/>
    public SettingRegistrationResult RegisterU64ArraySetting(
        string name, string uiName, string groupName, ulong[] defaultValue,
        ulong? min = null, ulong? max = null, string? description = null)
    {
        Setting setting = Setting.U64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>Creates an I64 array setting with optional per-element min/max bounds and registers it.</summary>
    /// <param name="name">The setting name, a dot-separated ASCII identifier.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; every element inside the bounds.</param>
    /// <param name="min">The smallest accepted element, or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted element, or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <returns>What happened to a stored value of this name, and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationSettingsException">The bounds are reversed, or an element is outside them.</exception>
    /// <exception cref="InvalidNameSettingsException">The name or the group is invalid.</exception>
    /// <exception cref="InvalidUiNameSettingsException"><paramref name="uiName"/> is empty or holds a control character.</exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this name exists.</exception>
    /// <seealso cref="Setting.I64Array"/>
    public SettingRegistrationResult RegisterI64ArraySetting(
        string name, string uiName, string groupName, long[] defaultValue,
        long? min = null, long? max = null, string? description = null)
    {
        Setting setting = Setting.I64Array(name, uiName, groupName, defaultValue, min, max, description);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>
    /// Registers the setting of a limit: name = key, UI name = title, group = the group of the key. The default is the fixed
    /// default of the definition; the setting owns a live holder that follows the current value
    /// (<see cref="Setting.LiveLimit"/>).
    /// </summary>
    /// <param name="definition">The limit; the one source of the key, the texts, the range, the default, and the flags.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>The load result (a stored value is applied on registration) and the current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">
    /// The group of the key is not a valid group name, for example the reserved group <c>default</c>.
    /// </exception>
    /// <exception cref="DuplicateNameSettingsException">A setting of this key exists.</exception>
    /// <seealso cref="RegisterLimitOption"/>
    public SettingRegistrationResult RegisterLimit(LimitDefinition definition, SettingActivation activation = SettingActivation.Immediate)
    {
        Setting setting = Setting.FromLimit(definition, activation);
        return _Manager.RegisterSetting(setting);
    }

    /// <summary>
    /// Registers a limit as a request option of a source or format: name = local name (<see cref="LimitDefinition.LocalName"/>),
    /// in the option group of the request.
    /// </summary>
    /// <param name="definition">The limit.</param>
    /// <returns>The load result and the current value (the definition default).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The local name of the key is not a valid setting name.</exception>
    /// <exception cref="DuplicateNameSettingsException">An option of this local name exists.</exception>
    /// <seealso cref="RegisterLimit"/>
    public SettingRegistrationResult RegisterLimitOption(LimitDefinition definition)
    {
        Setting setting = Setting.FromLimitOption(definition);
        return _Manager.RegisterSetting(setting);
    }

    #endregion
}
