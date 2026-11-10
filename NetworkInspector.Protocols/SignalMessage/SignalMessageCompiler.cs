// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Compile-time settings applied when turning JSON message configs into runtime protocols.
/// </summary>
/// <remarks>Thread safety: immutable value; safe to share.</remarks>
/// <param name="ShowRaw">When true, register and populate <c>.raw</c> child fields.</param>
/// <param name="ShowEnum">When true, register and populate <c>.enum</c> child fields on enum hits.</param>
/// <param name="MaxEnumValues">
/// Hard cap on enum entries per signal: the <c>signal_message.max_enum_values</c> setting, which protects registration
/// from a config with an unbounded <c>value_names</c> map (see <see cref="SignalEnumTableBuilder.TryBuild"/>).
/// </param>
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
/// <param name="RequiredByteLength">
/// Minimum payload length for unchecked extraction: the maximum end byte over every static signal, the mux selector
/// and all signals of all mux groups (see <see cref="SignalMessageBits"/>). The protocol rejects shorter payloads
/// once per frame so that no signal needs its own bounds check.
/// </param>
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
/// <para>
/// The JSON config is operator-supplied external input (a trust boundary): every name, bit layout, enum key and
/// count is validated here, failures are expected outcomes and become warnings (never exceptions that escape), and
/// no failure may leave a name reserved by a message that was not accepted. Compilation runs once at registration,
/// so it favours clarity over speed.
/// </para>
/// <para>Thread safety: static and stateless; the caller owns the collections it passes in and must not share them.</para>
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

        // Names are unique across the whole file: protocol names double as container field names, and every signal
        // field name (plus .raw/.enum suffixes) must be globally unique because field names are registered stack-wide.
        // Names are compared ordinally (case-sensitive). Only successfully compiled messages add to these sets
        // (see _TryCompileMessage).
        List<CompiledSignalMessage> result = new(config.Messages.Length);
        HashSet<string> usedProtocolNames = new(StringComparer.Ordinal);
        HashSet<string> usedFieldNames = new(StringComparer.Ordinal);

        for (int i = 0; i < config.Messages.Length; i++)
        {
            SignalMessageConfig? messageCfg = config.Messages[i];
            string label = messageCfg is null || string.IsNullOrWhiteSpace(messageCfg.Name)
                ? Invariant($"messages[{i.ToString(CultureInfo.InvariantCulture)}]")
                : messageCfg.Name;
            try
            {
                if (messageCfg is null)
                {
                    _AddWarning(warnings, Invariant($"Skipping signal message '{label}': message entry is null."));
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
                    _AddWarning(warnings, Invariant($"Skipping signal message '{label}': {error}"));
                    continue;
                }

                result.Add(msg);
            }
            catch (Exception ex)
            {
                // Config trust boundary: one malformed message must not abort the rest of the file.
                _AddWarning(warnings, Invariant($"Skipping signal message '{label}': {ex.Message}"));
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
            error = Invariant($"Message '{cfg.Name}': ui_name is required.");
            return false;
        }

        if (!NameValidation.IsValidName(cfg.Name))
        {
            error = Invariant($"Message '{cfg.Name}': name is not a valid protocol identifier.");
            return false;
        }

        if (!NameValidation.IsValidUiName(cfg.UiName))
        {
            error = Invariant($"Message '{cfg.Name}': ui_name is invalid.");
            return false;
        }

        if (usedProtocolNames.Contains(cfg.Name))
        {
            error = Invariant($"Duplicate message name '{cfg.Name}'.");
            return false;
        }

        if (usedFieldNames.Contains(cfg.Name))
        {
            error = Invariant($"container field name '{cfg.Name}' collides with an already reserved field.");
            return false;
        }

        if (cfg.ByteLength < 1)
        {
            error = Invariant($"Message '{cfg.Name}': byte_length must be >= 1.");
            return false;
        }

        // Names of this message are collected in a private set first (starting with the container field) and are
        // merged into the global set only after the whole message compiled (see the end of this method), so a
        // rejected message does not block a later valid definition that uses the same names.
        HashSet<string> messageFields = new(StringComparer.Ordinal);
        if (!messageFields.Add(cfg.Name))
        {
            error = Invariant($"Message '{cfg.Name}': container field name could not be reserved.");
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
                    Invariant($"Message '{cfg.Name}': mux_groups ignored because mux_signal is missing."));
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

        // The payload length contract covers every signal that could be read: static signals, the selector, and all
        // groups (not just the group selected at parse time). byte_length from the JSON is only a declared upper
        // bound that must not be smaller than what the signals need; it is not used at parse time.
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
            error = Invariant($"Message '{cfg.Name}': byte_length ({cfg.ByteLength}) is less than RequiredByteLength ({required}).");
            return false;
        }

        // Every name of this message was checked against the global set when it was reserved, so a collision cannot
        // happen here. The set is still checked once more, before anything is published, because ignoring a failed
        // publication would let two messages share a field name unnoticed; a broken invariant rejects the message and
        // leaves the global sets unchanged.
        foreach (string fieldName in messageFields)
        {
            if (usedFieldNames.Contains(fieldName))
            {
                error = Invariant($"Message '{cfg.Name}': field name '{fieldName}' collides with an already reserved field.");
                return false;
            }
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
        // does not block a later valid definition with the same name. The earlier Contains checks make a duplicate
        // impossible here; the Add result is still checked so the invariant cannot break silently.
        if (!usedProtocolNames.Add(cfg.Name))
        {
            error = Invariant($"Duplicate message name '{cfg.Name}'.");
            return false;
        }

        // Publish the message's names to the global set. They were all checked against it above, so every one is new.
        usedFieldNames.UnionWith(messageFields);

        return true;
    }

    /// <summary>
    /// Compiles mux selector and groups. Returns <see langword="false"/> when the selector
    /// itself is unusable; callers keep static signals and treat mux as absent.
    /// Invalid group signals and duplicate <c>mux_value</c> entries are skipped with warnings.
    /// <para>
    /// All groups share the message-wide name set, so the same signal name cannot be used in two mux groups of one
    /// message: the second signal is skipped as a collision.
    /// </para>
    /// </summary>
    /// <param name="messageName">Name of the owning message (for warnings).</param>
    /// <param name="muxCfg">The selector config.</param>
    /// <param name="groupCfgs">The group configs (may contain null entries).</param>
    /// <param name="settings">Compile settings.</param>
    /// <param name="priorFieldNames">Names reserved by earlier accepted messages.</param>
    /// <param name="messageFields">Names reserved so far by this message; updated.</param>
    /// <param name="warnings">Receives skip warnings.</param>
    /// <param name="muxSignal">The compiled selector when this method returns <see langword="true"/>.</param>
    /// <param name="muxGroups">The kept groups when this method returns <see langword="true"/>.</param>
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
            _AddWarning(warnings, Invariant($"Skipping mux_signal in message '{messageName}': name and ui_name are required."));
            return false;
        }

        if (!NameValidation.IsValidName(muxCfg.Name))
        {
            _AddWarning(warnings, Invariant($"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': invalid name."));
            return false;
        }

        if (!NameValidation.IsValidUiName(muxCfg.UiName))
        {
            _AddWarning(warnings, Invariant($"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': invalid ui_name."));
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
            _AddWarning(warnings, Invariant($"Skipping mux_signal in message '{messageName}': {layoutError}"));
            return false;
        }

        string muxValueName = Invariant($"{muxCfg.Name}.value");
        if (!_TryReserveFieldNames(priorFieldNames, messageFields, muxCfg.Name, muxValueName, out string? collision))
        {
            _AddWarning(
                warnings,
                Invariant($"Skipping mux_signal '{muxCfg.Name}' in message '{messageName}': name '{collision}' collides."));
            return false;
        }

        // The selector is a plain unsigned value: factor 1, offset 0, no unit, no enum names. Its field ids stay
        // invalid until the protocol registers its fields.
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

        // Keep a group only when its selector value fits the selector width (a larger value could never be selected)
        // and is unique (the first group with a value wins; later duplicates would be unreachable).
        List<CompiledMuxGroup> keptGroups = new(groupCfgs.Length);
        HashSet<ulong> muxValues = [];
        for (int g = 0; g < groupCfgs.Length; g++)
        {
            MuxGroupConfig? group = groupCfgs[g];
            if (group is null)
            {
                _AddWarning(
                    warnings,
                    Invariant($"Skipping mux_group in message '{messageName}': group entry is null."));
                continue;
            }

            if (group.MuxValue > muxMaxRaw)
            {
                _AddWarning(
                    warnings,
                    Invariant($"Skipping mux_group mux_value {group.MuxValue.ToString(CultureInfo.InvariantCulture)} in message '{messageName}': ")
                    + Invariant($"exceeds max raw {muxMaxRaw.ToString(CultureInfo.InvariantCulture)} ")
                    + Invariant($"for mux bit_length {bitLength.ToString(CultureInfo.InvariantCulture)}."));
                continue;
            }

            if (!muxValues.Add(group.MuxValue))
            {
                _AddWarning(
                    warnings,
                    Invariant($"Skipping mux_group mux_value {group.MuxValue.ToString(CultureInfo.InvariantCulture)} in message '{messageName}': ")
                    + Invariant($"duplicate mux_value."));
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
    /// <param name="messageName">Name of the owning message (for warnings).</param>
    /// <param name="configs">Signal configs (may contain null entries).</param>
    /// <param name="settings">Compile settings; decide which <c>.raw</c> and <c>.enum</c> names are reserved.</param>
    /// <param name="priorFieldNames">Names reserved by earlier accepted messages.</param>
    /// <param name="messageFields">Names reserved so far by this message; updated for every kept signal.</param>
    /// <param name="warnings">Receives skip warnings.</param>
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
                _AddWarning(warnings, Invariant($"Skipping signal in message '{messageName}': signal entry is null."));
                continue;
            }

            if (!_TryCompileSignal(cfg, in settings, out SignalInfo compiled, out string? error))
            {
                string label = string.IsNullOrWhiteSpace(cfg.Name) ? "(unnamed)" : cfg.Name;
                _AddWarning(warnings, Invariant($"Skipping signal '{label}' in message '{messageName}': {error}"));
                continue;
            }

            // Reserve exactly the field names registration will create: the signal itself, plus ".raw" and ".enum"
            // children only when those are enabled (and, for enum, only when the signal has value names).
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
                    Invariant($"Skipping signal '{compiled.Name}' in message '{messageName}': name '{collision}' collides."));
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
            error = Invariant($"invalid name '{cfg.Name}'.");
            return false;
        }

        if (!NameValidation.IsValidUiName(cfg.UiName))
        {
            error = Invariant($"invalid ui_name.");
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

        // JSON object keys are strings; convert them to numeric raw values (invariant culture, so the result does not
        // depend on the machine's locale). Two keys that denote the same number (for example "1" and "01") are a
        // configuration mistake: with a plain assignment the later one would win and the other name would vanish
        // without a trace, so the signal is rejected with a warning that names the key (like an invalid key).
        Dictionary<ulong, string>? numericNames = null;
        if (cfg.ValueNames is { Count: > 0 })
        {
            numericNames = new Dictionary<ulong, string>(cfg.ValueNames.Count);
            foreach (KeyValuePair<string, string> kvp in cfg.ValueNames)
            {
                if (!ulong.TryParse(kvp.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong key))
                {
                    error = Invariant($"invalid value_names key '{kvp.Key}'.");
                    return false;
                }

                if (!numericNames.TryAdd(key, kvp.Value))
                {
                    error = Invariant($"duplicate value_names key '{kvp.Key}': another key denotes the same number ")
                        + Invariant($"{key.ToString(CultureInfo.InvariantCulture)}.");
                    return false;
                }
            }
        }

        // The table builder enforces the enum count cap, the raw range of the bit length, and chooses the layout.
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
        // Range rationale: SignalInfo stores the start bit as ushort and the length as byte, which also bounds the
        // payload length a signal can require to about 8.2 KB (start bit 65535 plus up to 64 bits). A length of at
        // most 64 bits is the width of the unsigned 64-bit raw value used everywhere.
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
            error = Invariant($"unsupported byte_order '{byteOrder}'.");
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
    /// All-or-nothing: on a collision nothing is added.
    /// </summary>
    /// <param name="priorFieldNames">Names reserved by earlier accepted messages.</param>
    /// <param name="messageFields">Names reserved so far by this message.</param>
    /// <param name="fieldName">The signal field name.</param>
    /// <param name="reserveRaw">Whether the <c>.raw</c> child name is reserved too.</param>
    /// <param name="reserveEnum">Whether the <c>.enum</c> child name is reserved too.</param>
    /// <param name="collision">The first colliding name, or <see langword="null"/> on success.</param>
    private static bool _TryReserveSignalFieldNames(
        HashSet<string> priorFieldNames,
        HashSet<string> messageFields,
        string fieldName,
        bool reserveRaw,
        bool reserveEnum,
        [NotNullWhen(false)] out string? collision)
    {
        // Two phases: first check every name, then add them all. A collision therefore never leaves a half-reserved
        // signal behind in messageFields (all-or-nothing).
        if (!_CanUseFieldName(priorFieldNames, messageFields, fieldName))
        {
            collision = fieldName;
            return false;
        }

        string rawName = Invariant($"{fieldName}.raw");
        if (reserveRaw && !_CanUseFieldName(priorFieldNames, messageFields, rawName))
        {
            collision = rawName;
            return false;
        }

        string enumName = Invariant($"{fieldName}.enum");
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
