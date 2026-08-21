// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Compile-time settings applied when turning JSON message configs into runtime protocols.
/// </summary>
/// <remarks>Thread safety: immutable value; safe to share.</remarks>
/// <param name="ShowRaw">When true, register and populate <c>.raw</c> child fields.</param>
/// <param name="ShowEnum">When true, register and populate <c>.enum</c> child fields on enum hits.</param>
/// <param name="MaxEnumValues">Hard cap on enum entries per signal.</param>
internal readonly record struct SignalMessageCompileSettings(
    bool ShowRaw,
    bool ShowEnum,
    int MaxEnumValues);

/// <summary>
/// One compiled mux group after field IDs are assigned: selector value plus contiguous
/// <see cref="SignalInfo"/> array.
/// </summary>
/// <param name="MuxValue">Mux selector value.</param>
/// <param name="Signals">Signals active for this mux value.</param>
internal readonly record struct MuxGroupRuntime(ulong MuxValue, SignalInfo[] Signals);

/// <summary>
/// Fully compiled runtime for one signal message protocol (before field IDs are assigned).
/// Field IDs are filled during <see cref="SignalMessageProtocol.RegisterFields"/>.
/// JSON <c>byte_length</c> is compile-only (must be ≥ <see cref="RequiredByteLength"/>) and is
/// not stored here — parse consumes <see cref="RequiredByteLength"/> bytes.
/// </summary>
/// <param name="Name">Registered protocol name and container field name (JSON <c>name</c>).</param>
/// <param name="UiName">Protocol UI name (JSON <c>ui_name</c>).</param>
/// <param name="Description">Protocol description (never null; default when JSON omits <c>description</c>).</param>
/// <param name="RequiredByteLength">Minimum payload length for unchecked extraction.</param>
/// <param name="DispatchBindings">Dispatch table registrations.</param>
/// <param name="StaticSignals">Compiled static signals (field IDs still invalid until registration).</param>
/// <param name="MuxSignal">Compiled mux selector; <see langword="null"/> when no mux.</param>
/// <param name="MuxGroups">Compiled mux groups.</param>
internal readonly record struct CompiledSignalMessage(
    string Name,
    string UiName,
    string Description,
    int RequiredByteLength,
    DispatchBinding[] DispatchBindings,
    SignalInfo[] StaticSignals,
    SignalInfo? MuxSignal,
    CompiledMuxGroup[] MuxGroups);

/// <summary>
/// Validates JSON DTOs and builds <see cref="CompiledSignalMessage"/> instances.
/// Signal Message is best-effort: invalid or colliding signals are skipped with a warning
/// so siblings in the same message still compile. A message is skipped only when its
/// protocol identity or declared <c>byte_length</c> is unusable.
/// </summary>
internal static class SignalMessageCompiler
{
    #region Constants

    /// <summary>Description when JSON omits <c>description</c> or supplies whitespace only.</summary>
    internal const string DefaultMessageDescription = "Signal message";

    #endregion

    #region Public API

    /// <summary>
    /// Compiles each message independently. Invalid signals are skipped with a warning;
    /// a failure of the message identity or <c>byte_length</c> does not prevent compiling
    /// later messages. Unexpected exceptions during a single message are converted to
    /// warnings so later messages still compile.
    /// </summary>
    /// <param name="config">Root JSON config.</param>
    /// <param name="settings">Compile settings (must have <see cref="SignalMessageCompileSettings.MaxEnumValues"/> &gt; 0).</param>
    /// <param name="warnings">Receives one warning per skipped message or skipped signal/mux item.</param>
    /// <returns>Successfully compiled messages (may be empty; a compiled message may have zero signals).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="settings"/>.<see cref="SignalMessageCompileSettings.MaxEnumValues"/> is not positive
    /// (caller should validate settings before compile).
    /// </exception>
    internal static CompiledSignalMessage[] CompileMessages(
        SignalMessagesConfig config,
        in SignalMessageCompileSettings settings,
        ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);

        if (settings.MaxEnumValues <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "signal_message.max_enum_values must be greater than zero.");
        }

        if (config.Messages is not { Length: > 0 })
        {
            return [];
        }

        List<CompiledSignalMessage> result = new(config.Messages.Length);
        HashSet<string> usedProtocolNames = new(StringComparer.Ordinal);
        HashSet<string> usedFieldNames = new(StringComparer.Ordinal);

        for (int i = 0; i < config.Messages.Length; i++)
        {
            SignalMessageConfig? messageCfg = config.Messages[i];
            string label = messageCfg is null || string.IsNullOrWhiteSpace(messageCfg.Name)
                ? $"messages[{i.ToString(CultureInfo.InvariantCulture)}]"
                : messageCfg.Name;
            try
            {
                if (messageCfg is null)
                {
                    _AddWarning(warnings, $"Skipping signal message '{label}': message entry is null.");
                    continue;
                }

                if (!_TryCompileMessage(
                    messageCfg,
                    in settings,
                    usedProtocolNames,
                    usedFieldNames,
                    warnings,
                    out CompiledSignalMessage msg,
                    out string? error))
                {
                    _AddWarning(warnings, $"Skipping signal message '{label}': {error}");
                    continue;
                }

                result.Add(msg);
            }
            catch (Exception ex)
            {
                // Config trust boundary: one malformed message must not abort the rest of the file.
                _AddWarning(warnings, $"Skipping signal message '{label}': {ex.Message}");
            }
        }

        return result.ToArray();
    }

    #endregion

    #region Message Compile

    /// <summary>
    /// Compiles one message. Returns <see langword="false"/> only for unusable protocol
    /// identity or <c>byte_length</c>. Signal and mux failures are warnings, not message skips.
    /// </summary>
    /// <param name="cfg">JSON message entry. Caller guarantees non-null.</param>
    /// <param name="settings">Compile settings already validated by <see cref="CompileMessages"/>.</param>
    /// <param name="usedProtocolNames">Protocol names reserved by earlier successful messages.</param>
    /// <param name="usedFieldNames">Field names reserved by earlier successful messages.</param>
    /// <param name="warnings">Receives per-signal and per-mux skip warnings.</param>
    /// <param name="message">Compiled message when this method returns <see langword="true"/>.</param>
    /// <param name="error">Message-level skip reason when this method returns <see langword="false"/>.</param>
    private static bool _TryCompileMessage(
        SignalMessageConfig cfg,
        in SignalMessageCompileSettings settings,
        HashSet<string> usedProtocolNames,
        HashSet<string> usedFieldNames,
        ICollection<SettingsLoadWarning> warnings,
        out CompiledSignalMessage message,
        [NotNullWhen(false)] out string? error)
    {
        message = default;
        error = null;

        if (string.IsNullOrWhiteSpace(cfg.Name))
        {
            error = "Message name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(cfg.UiName))
        {
            error = $"Message '{cfg.Name}': ui_name is required.";
            return false;
        }

        if (!NameValidation.IsValidName(cfg.Name))
        {
            error = $"Message '{cfg.Name}': name is not a valid protocol identifier.";
            return false;
        }

        if (!NameValidation.IsValidUiName(cfg.UiName))
        {
            error = $"Message '{cfg.Name}': ui_name is invalid.";
            return false;
        }

        if (usedProtocolNames.Contains(cfg.Name))
        {
            error = $"Duplicate message name '{cfg.Name}'.";
            return false;
        }

        if (usedFieldNames.Contains(cfg.Name))
        {
            error = $"container field name '{cfg.Name}' collides with an already reserved field.";
            return false;
        }

        if (cfg.ByteLength < 1)
        {
            error = $"Message '{cfg.Name}': byte_length must be >= 1.";
            return false;
        }

        HashSet<string> messageFields = new(StringComparer.Ordinal);
        if (!messageFields.Add(cfg.Name))
        {
            error = $"Message '{cfg.Name}': container field name could not be reserved.";
            return false;
        }

        SignalFieldConfig[] staticCfgs = cfg.Signals ?? [];
        SignalInfo[] staticSignals = _CompileSignalArray(
            cfg.Name,
            staticCfgs,
            in settings,
            usedFieldNames,
            messageFields,
            warnings);

        SignalInfo? muxSignal = null;
        CompiledMuxGroup[] muxGroups = [];
        MuxSignalConfig? muxCfg = cfg.MuxSignal;
        MuxGroupConfig[] groupCfgs = cfg.MuxGroups ?? [];
        bool hasMux = false;

        if (muxCfg is null)
        {
            if (groupCfgs.Length > 0)
            {
                _AddWarning(
                    warnings,
                    $"Message '{cfg.Name}': mux_groups ignored because mux_signal is missing.");
            }
        }
        else
        {
            hasMux = _TryCompileMux(
                cfg.Name,
                muxCfg,
                groupCfgs,
                in settings,
                usedFieldNames,
                messageFields,
                warnings,
                out muxSignal,
                out muxGroups);
        }

        SignalInfo[][] groupArrays = new SignalInfo[muxGroups.Length][];
        for (int g = 0; g < muxGroups.Length; g++)
        {
            groupArrays[g] = muxGroups[g].Signals;
        }

        int required = SignalMessageBits.ComputeRequiredByteLength(
            staticSignals,
            hasMux,
            muxSignal ?? default,
            groupArrays);

        if (cfg.ByteLength < required)
        {
            error = $"Message '{cfg.Name}': byte_length ({cfg.ByteLength}) is less than RequiredByteLength ({required}).";
            return false;
        }

        message = new CompiledSignalMessage(
            cfg.Name,
            cfg.UiName,
            _ResolveDescription(cfg.Description),
            required,
            cfg.DispatchBindings ?? [],
            staticSignals,
            muxSignal,
            muxGroups);

        // Reserve names only after a fully successful compile so a failed message
        // does not block a later valid definition with the same name.
        if (!usedProtocolNames.Add(cfg.Name))
        {
            error = $"Duplicate message name '{cfg.Name}'.";
            return false;
        }

        foreach (string fieldName in messageFields)
        {
            if (!usedFieldNames.Add(fieldName))
            {
                continue;
            }
        }

        return true;
    }

    /// <summary>
    /// Compiles mux selector and groups. Returns <see langword="false"/> when the selector
    /// itself is unusable; callers keep static signals and treat mux as absent.
    /// Invalid group signals and duplicate <c>mux_value</c> entries are skipped with warnings.
    /// </summary>
    private static bool _TryCompileMux(
        string messageName,
        MuxSignalConfig muxCfg,
        MuxGroupConfig[] groupCfgs,
        in SignalMessageCompileSettings settings,
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        ICollection<SettingsLoadWarning> warnings,
        out SignalInfo? muxSignal,
        out CompiledMuxGroup[] muxGroups)
    {
        muxSignal = null;
        muxGroups = [];

        if (string.IsNullOrWhiteSpace(muxCfg.Name) || string.IsNullOrWhiteSpace(muxCfg.UiName))
        {
            _AddWarning(warnings, $"Skipping mux_signal in message '{messageName}': name and ui_name are required.");
            return false;
        }

        if (!NameValidation.IsValidName(muxCfg.Name))
        {
            _AddWarning(warnings, $"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': invalid name.");
            return false;
        }

        if (!NameValidation.IsValidUiName(muxCfg.UiName))
        {
            _AddWarning(warnings, $"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': invalid ui_name.");
            return false;
        }

        if (!_TryParseBitLayout(
                muxCfg.StartBit,
                muxCfg.BitLength,
                muxCfg.ByteOrder,
                out ushort startBit,
                out byte bitLength,
                out bool bigEndian,
                out string? layoutError))
        {
            _AddWarning(warnings, $"Skipping mux_signal in message '{messageName}': {layoutError}");
            return false;
        }

        string muxValueName = $"{muxCfg.Name}.value";
        if (!_TryReserveFieldNames(priorFieldNames, messageFields, muxCfg.Name, muxValueName, out string? collision))
        {
            _AddWarning(
                warnings,
                $"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': name '{collision}' collides.");
            return false;
        }

        ulong muxMaxRaw = SignalMessageBits.MaxRawForBitLength(bitLength);
        muxSignal = new SignalInfo(
            startBit,
            bitLength,
            bigEndian,
            Factor: 1.0,
            Offset: 0.0,
            FieldId.Invalid,
            FieldId.Invalid,
            FieldId.Invalid,
            muxCfg.Name,
            muxCfg.UiName,
            string.Empty,
            SignalEnumTable.None,
            CustomTextByRaw: null);

        List<CompiledMuxGroup> keptGroups = new(groupCfgs.Length);
        HashSet<ulong> muxValues = [];
        for (int g = 0; g < groupCfgs.Length; g++)
        {
            MuxGroupConfig? group = groupCfgs[g];
            if (group is null)
            {
                _AddWarning(
                    warnings,
                    $"Skipping mux_group in message '{messageName}': group entry is null.");
                continue;
            }

            if (group.MuxValue > muxMaxRaw)
            {
                _AddWarning(
                    warnings,
                    $"Skipping mux_group mux_value {group.MuxValue.ToString(CultureInfo.InvariantCulture)} in message '{messageName}': exceeds max raw {muxMaxRaw.ToString(CultureInfo.InvariantCulture)} for mux bit_length {bitLength.ToString(CultureInfo.InvariantCulture)}.");
                continue;
            }

            if (!muxValues.Add(group.MuxValue))
            {
                _AddWarning(
                    warnings,
                    $"Skipping mux_group mux_value {group.MuxValue.ToString(CultureInfo.InvariantCulture)} in message '{messageName}': duplicate mux_value.");
                continue;
            }

            SignalFieldConfig[] gSignals = group.Signals ?? [];
            SignalInfo[] compiled = _CompileSignalArray(
                messageName,
                gSignals,
                in settings,
                priorFieldNames,
                messageFields,
                warnings);
            keptGroups.Add(new CompiledMuxGroup(group.MuxValue, compiled));
        }

        muxGroups = keptGroups.ToArray();
        return true;
    }

    /// <summary>
    /// Compiles each signal independently. Invalid or colliding entries are skipped
    /// with a warning; remaining signals are returned (possibly empty).
    /// </summary>
    private static SignalInfo[] _CompileSignalArray(
        string messageName,
        SignalFieldConfig[] configs,
        in SignalMessageCompileSettings settings,
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        ICollection<SettingsLoadWarning> warnings)
    {
        List<SignalInfo> result = new(configs.Length);
        for (int i = 0; i < configs.Length; i++)
        {
            SignalFieldConfig? cfg = configs[i];
            if (cfg is null)
            {
                _AddWarning(warnings, $"Skipping signal in message '{messageName}': signal entry is null.");
                continue;
            }

            if (!_TryCompileSignal(cfg, in settings, out SignalInfo compiled, out string? error))
            {
                string label = string.IsNullOrWhiteSpace(cfg.Name) ? "(unnamed)" : cfg.Name;
                _AddWarning(warnings, $"Skipping signal '{label}' in message '{messageName}': {error}");
                continue;
            }

            bool reserveRaw = settings.ShowRaw;
            bool reserveEnum = settings.ShowEnum && compiled.Enums.Kind != SignalEnumKind.None;
            if (!_TryReserveSignalFieldNames(
                    priorFieldNames,
                    messageFields,
                    compiled.Name,
                    reserveRaw,
                    reserveEnum,
                    out string? collision))
            {
                _AddWarning(
                    warnings,
                    $"Skipping signal '{compiled.Name}' in message '{messageName}': name '{collision}' collides.");
                continue;
            }

            result.Add(compiled);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Validates one signal DTO. Returns <see langword="false"/> for missing names, invalid
    /// identifiers, bad bit layout, or unusable <c>value_names</c>. Does not throw.
    /// </summary>
    private static bool _TryCompileSignal(
        SignalFieldConfig cfg,
        in SignalMessageCompileSettings settings,
        out SignalInfo compiled,
        [NotNullWhen(false)] out string? error)
    {
        compiled = default;
        error = null;

        if (string.IsNullOrWhiteSpace(cfg.Name) || string.IsNullOrWhiteSpace(cfg.UiName))
        {
            error = "signal name and ui_name are required.";
            return false;
        }

        if (!NameValidation.IsValidName(cfg.Name))
        {
            error = $"invalid name '{cfg.Name}'.";
            return false;
        }

        if (!NameValidation.IsValidUiName(cfg.UiName))
        {
            error = $"invalid ui_name.";
            return false;
        }

        if (!_TryParseBitLayout(
                cfg.StartBit,
                cfg.BitLength,
                cfg.ByteOrder,
                out ushort startBit,
                out byte bitLength,
                out bool bigEndian,
                out error))
        {
            return false;
        }

        Dictionary<ulong, string>? numericNames = null;
        if (cfg.ValueNames is { Count: > 0 })
        {
            numericNames = new Dictionary<ulong, string>(cfg.ValueNames.Count);
            foreach (KeyValuePair<string, string> kvp in cfg.ValueNames)
            {
                if (!ulong.TryParse(kvp.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong key))
                {
                    error = $"invalid value_names key '{kvp.Key}'.";
                    return false;
                }

                numericNames[key] = kvp.Value;
            }
        }

        if (!SignalEnumTableBuilder.TryBuild(numericNames, bitLength, settings.MaxEnumValues, out SignalEnumTable enums, out string? enumError))
        {
            error = enumError;
            return false;
        }

        compiled = new SignalInfo(
            startBit,
            bitLength,
            bigEndian,
            cfg.Factor,
            cfg.Offset,
            FieldId.Invalid,
            FieldId.Invalid,
            FieldId.Invalid,
            cfg.Name,
            cfg.UiName,
            cfg.Unit ?? string.Empty,
            enums,
            CustomTextByRaw: null);
        return true;
    }

    /// <summary>
    /// Parses start bit, bit length, and byte order. <paramref name="byteOrder"/> must be
    /// <c>big_endian</c> or <c>little_endian</c> (case-insensitive); null/whitespace is an error.
    /// </summary>
    private static bool _TryParseBitLayout(
        int startBit,
        int bitLength,
        string? byteOrder,
        out ushort startBitU,
        out byte bitLengthB,
        out bool bigEndian,
        [NotNullWhen(false)] out string? error)
    {
        startBitU = 0;
        bitLengthB = 0;
        bigEndian = false;
        error = null;

        if (startBit < 0 || startBit > ushort.MaxValue)
        {
            error = "start_bit out of range.";
            return false;
        }

        if (bitLength < 1 || bitLength > 64)
        {
            error = "bit_length must be in the range 1..64.";
            return false;
        }

        startBitU = (ushort)startBit;
        bitLengthB = (byte)bitLength;

        if (string.IsNullOrWhiteSpace(byteOrder))
        {
            error = "byte_order is required.";
            return false;
        }

        if (byteOrder.Equals("big_endian", StringComparison.OrdinalIgnoreCase))
        {
            bigEndian = true;
        }
        else if (!byteOrder.Equals("little_endian", StringComparison.OrdinalIgnoreCase))
        {
            error = $"unsupported byte_order '{byteOrder}'.";
            return false;
        }

        return true;
    }

    /// <summary>Trims JSON description; returns <see cref="DefaultMessageDescription"/> when omitted or whitespace.</summary>
    private static string _ResolveDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return DefaultMessageDescription;
        }

        return description.Trim();
    }

    #endregion

    #region Name reservation

    /// <summary>
    /// Adds signal field names (and optional <c>.raw</c>/<c>.enum</c> suffixes) to
    /// <paramref name="messageFields"/> when none collide with prior messages or this message.
    /// </summary>
    private static bool _TryReserveSignalFieldNames(
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        string fieldName,
        bool reserveRaw,
        bool reserveEnum,
        [NotNullWhen(false)] out string? collision)
    {
        if (!_CanUseFieldName(priorFieldNames, messageFields, fieldName))
        {
            collision = fieldName;
            return false;
        }

        string rawName = $"{fieldName}.raw";
        if (reserveRaw && !_CanUseFieldName(priorFieldNames, messageFields, rawName))
        {
            collision = rawName;
            return false;
        }

        string enumName = $"{fieldName}.enum";
        if (reserveEnum && !_CanUseFieldName(priorFieldNames, messageFields, enumName))
        {
            collision = enumName;
            return false;
        }

        if (!messageFields.Add(fieldName))
        {
            collision = fieldName;
            return false;
        }

        if (reserveRaw && !messageFields.Add(rawName))
        {
            collision = rawName;
            return false;
        }

        if (reserveEnum && !messageFields.Add(enumName))
        {
            collision = enumName;
            return false;
        }

        collision = null;
        return true;
    }

    /// <summary>Reserves two field names (mux selector and <c>.value</c> child) or reports the first collision.</summary>
    private static bool _TryReserveFieldNames(
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        string first,
        string second,
        [NotNullWhen(false)] out string? collision)
    {
        if (!_CanUseFieldName(priorFieldNames, messageFields, first))
        {
            collision = first;
            return false;
        }

        if (!_CanUseFieldName(priorFieldNames, messageFields, second))
        {
            collision = second;
            return false;
        }

        if (!messageFields.Add(first))
        {
            collision = first;
            return false;
        }

        if (!messageFields.Add(second))
        {
            collision = second;
            return false;
        }

        collision = null;
        return true;
    }

    /// <summary>True when <paramref name="name"/> is unused in prior messages and in this message.</summary>
    private static bool _CanUseFieldName(
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        string name)
    {
        if (priorFieldNames.Contains(name))
        {
            return false;
        }

        return !messageFields.Contains(name);
    }

    #endregion

    #region Warnings

    /// <summary>Appends a compile warning tied to <c>signal_message.config_file</c>.</summary>
    private static void _AddWarning(ICollection<SettingsLoadWarning> warnings, string detail)
    {
        warnings.Add(new SettingsLoadWarning(
            SettingsLoadWarningKind.DeserializationError,
            "signal_message",
            SignalMessageRegistration.ConfigFileSetting,
            detail));
    }

    #endregion
}
