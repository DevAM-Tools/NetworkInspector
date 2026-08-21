// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Registers signal-message settings and spawns one <see cref="SignalMessageProtocol"/>
/// per JSON message. There is no meta protocol instance — only message protocols
/// participate in dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Register(IStackBuilder)"/> from
/// <see cref="ProtocolRegistration.RegisterStandardProtocols"/> (or custom stacks)
/// after parent tables such as <c>can.id</c> / <c>udp.port</c> exist or will exist
/// (deferred via <see cref="IStackBuilder.WhenProtocolTableRegistered"/>).
/// </para>
/// <para>
/// Best-effort: invalid or colliding <b>signals</b> are skipped with a warning; the rest of
/// the message still registers. A message is skipped only when its protocol identity or
/// declared <c>byte_length</c> is unusable. Name collisions never throw.
/// See <c>docs/stack-registration-error-model.md</c> and PROTOCOL_GUIDE §10.7.
/// </para>
/// <para><b>Thread safety:</b> not thread-safe; call once during single-threaded stack build.</para>
/// </remarks>
public static class SignalMessageRegistration
{
    #region Constants

    /// <summary>Setting: path to the signal-message JSON configuration file.</summary>
    public const string ConfigFileSetting = "signal_message.config_file";

    /// <summary>Setting: append <c>.raw</c> child fields under each signal (default false).</summary>
    public const string ShowRawSetting = "signal_message.show_raw";

    /// <summary>Setting: append <c>.enum</c> child fields when an enum name hits (default false).</summary>
    public const string ShowEnumSetting = "signal_message.show_enum";

    /// <summary>Setting: max enum entries per signal (default 4096).</summary>
    public const string MaxEnumValuesSetting = "signal_message.max_enum_values";

    private const string _GroupName = "signal_message";

    #endregion

    #region Public API

    /// <summary>
    /// Registers settings, loads/compiles the JSON config (if configured), and registers
    /// one message protocol per successful message with <c>dispatch_bindings</c> wiring.
    /// </summary>
    /// <param name="builder">Stack builder during the registration phase.</param>
    /// <returns>
    /// Zero or more warnings (load failure, per-message compile skips, per-signal skips,
    /// per-message registration skips, per-binding dispatch failures). Empty when no config path
    /// is set or all messages succeed.
    /// </returns>
    public static IReadOnlyList<SettingsLoadWarning> Register(IStackBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        List<SettingsLoadWarning> warnings = [];
        _RegisterSettings(builder, warnings);

        if (!_TryLoadConfig(builder, warnings, out SignalMessagesConfig? config))
        {
            return warnings;
        }

        if (!_TryResolveCompileSettings(builder.Settings, warnings, out SignalMessageCompileSettings compileSettings))
        {
            return warnings;
        }

        foreach (CompiledSignalMessage compiled in SignalMessageCompiler.CompileMessages(
            config!,
            in compileSettings,
            warnings))
        {
            _RegisterCompiledMessage(builder, compiled, in compileSettings, warnings);
        }

        return warnings;
    }

    /// <summary>
    /// Maps unresolved deferred dispatch-table callbacks from <see cref="Stack.BuildDiagnostics"/>
    /// into <see cref="SettingsLoadWarning"/> entries for signal-message config.
    /// Call after <see cref="StackBuilder.Build"/> when tolerant registration was used.
    /// </summary>
    public static void AppendBuildDiagnosticsWarnings(
        IStack stack,
        ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(warnings);

        foreach (BuildDiagnostic diagnostic in stack.BuildDiagnostics.Span)
        {
            if (diagnostic is not BuildCallbackWarning callback)
            {
                continue;
            }

            if (callback.EntityKind != BuildCallbackWarningKind.ProtocolTable)
            {
                continue;
            }

            warnings.Add(new SettingsLoadWarning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                _GroupName,
                ConfigFileSetting,
                $"Dispatch table '{callback.Name}' was never registered "
                + $"({callback.CallbackCount.ToString(CultureInfo.InvariantCulture)} deferred binding(s) skipped)."));
        }
    }

    #endregion

    #region Settings and Config Load

    /// <summary>
    /// Registers the <c>signal_message</c> settings group and its four settings.
    /// Persisted values that fail to apply are reported on <paramref name="warnings"/>.
    /// </summary>
    private static void _RegisterSettings(IStackBuilder builder, List<SettingsLoadWarning> warnings)
    {
        SettingsRegistrar registrar = builder.SettingsRegistrar;
        registrar.RegisterGroup(_GroupName, "Signal Message", "Automotive signal-message configuration");

        SettingRegistrationResult configFile = registrar.RegisterStringSetting(
            ConfigFileSetting,
            "Configuration File",
            _GroupName,
            defaultValue: string.Empty,
            description: "Path to the signal-message JSON configuration file.");
        _AddSettingLoadWarningIfNeeded(configFile, ConfigFileSetting, warnings);

        SettingRegistrationResult showRaw = registrar.RegisterBoolSetting(
            ShowRawSetting,
            "Show Raw Child Fields",
            _GroupName,
            defaultValue: false,
            description: "When true, each signal gets a .raw U64 child field on materialization.");
        _AddSettingLoadWarningIfNeeded(showRaw, ShowRawSetting, warnings);

        SettingRegistrationResult showEnum = registrar.RegisterBoolSetting(
            ShowEnumSetting,
            "Show Enum Child Fields",
            _GroupName,
            defaultValue: false,
            description: "When true, each signal with value_names gets a .enum string child on materialization.");
        _AddSettingLoadWarningIfNeeded(showEnum, ShowEnumSetting, warnings);

        SettingRegistrationResult maxEnum = registrar.RegisterU64Setting(
            MaxEnumValuesSetting,
            "Max Enum Values Per Signal",
            _GroupName,
            defaultValue: 4096UL,
            min: 0UL,
            description: "Hard cap on discrete value_names entries per signal.");
        _AddSettingLoadWarningIfNeeded(maxEnum, MaxEnumValuesSetting, warnings);
    }

    /// <summary>
    /// Loads JSON from <see cref="ConfigFileSetting"/>. Returns <see langword="false"/> when
    /// no path is set or the file cannot be deserialized (warning already appended).
    /// </summary>
    private static bool _TryLoadConfig(
        IStackBuilder builder,
        List<SettingsLoadWarning> warnings,
        [NotNullWhen(true)] out SignalMessagesConfig? config)
    {
        config = null;
        if (!builder.Settings.TryLoadReferencedJsonConfig(
                ConfigFileSetting,
                SignalMessagesConfigContext.Default.SignalMessagesConfig,
                out SignalMessagesConfig? loaded,
                out SettingsLoadWarning? loadWarning))
        {
            if (loadWarning is not null)
            {
                warnings.Add(loadWarning.Value);
            }

            return false;
        }

        config = loaded;
        return true;
    }

    /// <summary>
    /// Reads show-raw / show-enum / max-enum settings. Returns <see langword="false"/> when
    /// <c>max_enum_values</c> is not positive.
    /// </summary>
    private static bool _TryResolveCompileSettings(
        ReadOnlySettingsManagerView settings,
        List<SettingsLoadWarning> warnings,
        out SignalMessageCompileSettings compileSettings)
    {
        compileSettings = default;
        bool showRaw = settings.GetBoolSetting(ShowRawSetting) ?? false;
        bool showEnum = settings.GetBoolSetting(ShowEnumSetting) ?? false;
        ulong maxEnumU = settings.GetU64Setting(MaxEnumValuesSetting) ?? 4096UL;
        int maxEnum = maxEnumU > int.MaxValue ? int.MaxValue : (int)maxEnumU;
        if (maxEnum <= 0)
        {
            warnings.Add(new SettingsLoadWarning(
                SettingsLoadWarningKind.OutOfRange,
                _GroupName,
                MaxEnumValuesSetting,
                "signal_message.max_enum_values must be greater than zero."));
            return false;
        }

        compileSettings = new SignalMessageCompileSettings(showRaw, showEnum, maxEnum);
        return true;
    }

    #endregion

    #region Message Registration

    /// <summary>
    /// Pre-validates protocol and container field names against the builder, then registers
    /// the protocol and its fields. Does not throw on name collisions.
    /// </summary>
    private static void _RegisterCompiledMessage(
        IStackBuilder builder,
        CompiledSignalMessage compiled,
        in SignalMessageCompileSettings compileSettings,
        List<SettingsLoadWarning> warnings)
    {
        if (builder.GetProtocolId(compiled.Name) is not null)
        {
            warnings.Add(_MessageWarning(compiled.Name, "protocol name already registered."));
            return;
        }

        if (builder.GetFieldId(compiled.Name) is not null)
        {
            warnings.Add(_MessageWarning(compiled.Name, "container field name already registered."));
            return;
        }

        if (!NameValidation.IsValidName(compiled.Name))
        {
            warnings.Add(_MessageWarning(compiled.Name, "invalid protocol name."));
            return;
        }

        if (!NameValidation.IsValidUiName(compiled.UiName))
        {
            warnings.Add(_MessageWarning(compiled.Name, "invalid protocol ui_name."));
            return;
        }

        try
        {
            SignalMessageProtocol protocol = new(compiled, in compileSettings);
            ProtocolId messageId = builder.RegisterProtocol(protocol);
            protocol.RegisterFields(builder, messageId, warnings);
            _RegisterDispatchBindings(builder, compiled.DispatchBindings, messageId, compiled.Name, warnings);
        }
        catch (RegistrationException ex)
        {
            warnings.Add(_MessageWarning(
                compiled.Name,
                $"registration failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Wires each dispatch binding through <see cref="IStackBuilder.WhenProtocolTableRegistered"/>.
    /// Empty table names are skipped. Binding failures become warnings.
    /// </summary>
    private static void _RegisterDispatchBindings(
        IStackBuilder builder,
        DispatchBinding[] bindings,
        ProtocolId messageId,
        string messageName,
        List<SettingsLoadWarning> warnings)
    {
        foreach (DispatchBinding binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Table))
            {
                continue;
            }

            string tableName = binding.Table;
            ulong key = binding.Key;
            builder.WhenProtocolTableRegistered(tableName, tableId =>
            {
                try
                {
                    builder.RegisterParserInU64Table(tableId, key, messageId);
                }
                catch (Exception ex) when (ex is RegistrationException or InvalidOperationException)
                {
                    warnings.Add(_MessageWarning(
                        messageName,
                        $"dispatch binding table '{tableName}' key {key.ToString(CultureInfo.InvariantCulture)} failed: {ex.Message}"));
                }
            });
        }
    }

    /// <summary>
    /// Records a warning when registering a setting could not apply a persisted value.
    /// <see cref="SettingLoadResult.Success"/> and <see cref="SettingLoadResult.NoPersistedValue"/>
    /// are silent.
    /// </summary>
    private static void _AddSettingLoadWarningIfNeeded(
        SettingRegistrationResult result,
        string settingName,
        List<SettingsLoadWarning> warnings)
    {
        SettingsLoadWarningKind kind;
        switch (result.LoadResult)
        {
            case SettingLoadResult.Success:
            case SettingLoadResult.NoPersistedValue:
                return;
            case SettingLoadResult.TypeMismatch:
                kind = SettingsLoadWarningKind.TypeMismatch;
                break;
            case SettingLoadResult.DeserializationError:
                kind = SettingsLoadWarningKind.DeserializationError;
                break;
            case SettingLoadResult.OutOfRange:
                kind = SettingsLoadWarningKind.OutOfRange;
                break;
            default:
                kind = SettingsLoadWarningKind.ExternalConfigUnavailable;
                break;
        }

        warnings.Add(new SettingsLoadWarning(
            kind,
            _GroupName,
            settingName,
            $"Persisted value for '{settingName}' could not be applied ({result.LoadResult}). The value is ignored."));
    }

    /// <summary>Builds a message-level skip warning for <see cref="ConfigFileSetting"/>.</summary>
    private static SettingsLoadWarning _MessageWarning(string messageName, string detail) =>
        new(
            SettingsLoadWarningKind.ExternalConfigUnavailable,
            _GroupName,
            ConfigFileSetting,
            $"Skipping signal message '{messageName}': {detail}");

    #endregion
}
