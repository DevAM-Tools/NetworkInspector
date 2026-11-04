// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The one place where a SOME/IP configuration becomes stack state: it registers the settings, loads and compiles the optional
/// service description, creates the protocol with the immutable model and the effective settings, binds the ports, configures the
/// TCP framing and adds the content-based detection to the heuristic tables.
/// <para>
/// Everything that can go wrong with the configuration is a <see cref="SettingsLoadWarning"/> on the returned list; nothing is
/// thrown for file content and nothing is discarded. A missing or broken file never blocks the protocol: SOME/IP is registered with
/// an empty model, so messages decode at header level without names. Callers must inspect the list.
/// </para>
/// <para>
/// A stack registers SOME/IP once. <see cref="ProtocolRegistration.RegisterStandardProtocols"/> calls
/// <see cref="Register(IStackBuilder)"/>; a custom stack that wants its configuration from a stream or an object calls the matching
/// overload <em>instead</em>. A second call on the same builder adds a warning and registers nothing, because a protocol name is
/// unique in a stack. Rejected alternative: merging a second configuration into the first protocol, which would need a mutable
/// model and give up the guarantee that a parse never sees a model change.
/// </para>
/// <para><b>Thread-safety:</b> not thread-safe; call it during the single-threaded stack build.</para>
/// </summary>
public static class SomeIpRegistration
{
    #region Constants

    /// <summary>The settings group of every SOME/IP setting.</summary>
    public const string GroupName = "someip";

    /// <summary>Setting: path of the SOME/IP service description file (may be empty; empty is not an error).</summary>
    public const string ConfigFileSetting = "someip.config_file";

    /// <summary>Setting: text put in front of every filter name of the description; empty means none.</summary>
    public const string FieldPrefixSetting = "someip.field_prefix";

    /// <summary>Setting: decode the payloads of configured methods into typed fields.</summary>
    public const string DecodePayloadSetting = "someip.decode_payload";

    /// <summary>Setting: add type, length, padding and tag fields to a decoded payload.</summary>
    public const string ShowTypeMetadataSetting = "someip.show_type_metadata";

    /// <summary>Setting: compute and compare the end-to-end protection checksum.</summary>
    public const string VerifyE2eCrcSetting = "someip.verify_e2e_crc";

    /// <summary>Setting: pair requests with responses and show the delay.</summary>
    public const string MethodTimingSetting = "someip.method_timing";

    /// <summary>Setting: pair requests and responses that were captured on different interfaces.</summary>
    public const string MethodTimingIgnoreInterfaceSetting = "someip.method_timing_ignore_interface";

    /// <summary>Setting: decode UDP ports that service discovery announced.</summary>
    public const string LearnSdPortsSetting = "someip.learn_sd_ports";

    /// <summary>Setting: ports that are never learned.</summary>
    public const string LearnSdPortsIgnoreSetting = "someip.learn_sd_ports_ignore";

    /// <summary>Setting: decode plausible SOME/IP on any port.</summary>
    public const string HeuristicSetting = "someip.heuristic";

    /// <summary>Setting: find the next message after a TCP stream lost its position.</summary>
    public const string TcpResyncSetting = "someip.tcp_resync";

    /// <summary>Setting: hand datagrams that look like DTLS to the DTLS parser.</summary>
    public const string DetectDtlsSetting = "someip.detect_dtls";

    /// <summary>
    /// Room in the stream buffer beyond one maximal message: a segment can carry the end of one message and the start of the next,
    /// and a buffer exactly as large as the message limit would drop a legal message that arrives together with its successor.
    /// </summary>
    private const int _StreamBufferHeadroom = 1024 * 1024;

    #endregion

    #region Public API

    /// <summary>
    /// Deserializes a configuration document from <paramref name="stream"/>; the stream is not closed. Compiling and registering
    /// are separate steps (<see cref="Register(IStackBuilder, SomeIpConfig)"/>).
    /// </summary>
    /// <param name="stream">Readable stream positioned at the JSON.</param>
    /// <param name="config">The document on success; otherwise <see langword="null"/>.</param>
    /// <param name="warning">Set when the document is refused; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the document was read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static bool TryLoadConfig(Stream stream, [NotNullWhen(true)] out SomeIpConfig? config, out SettingsLoadWarning? warning)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<SettingsLoadWarning> warnings = [];
        if (SomeIpConfigLoader.TryLoadFromStream(stream, out config, warnings))
        {
            warning = null;
            return true;
        }

        warning = warnings.Count > 0
            ? warnings[0]
            : null;
        return false;
    }

    /// <summary>
    /// Registers the settings, loads the file named by <see cref="ConfigFileSetting"/> (it may be empty), compiles it, registers the
    /// protocol with its fields, the port bindings and the TCP framing, and adds the content-based detection to the heuristic
    /// tables that exist.
    /// </summary>
    /// <param name="builder">The stack builder during the registration phase.</param>
    /// <returns>Every warning of the settings, the load, the compile and the registration; empty when there is nothing to report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SettingsLoadWarning> Register(IStackBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        List<SettingsLoadWarning> warnings = [];
        _RegisterSettings(builder, warnings);

        // An empty path is not an error: the loader returns false without a warning, and SOME/IP is registered without a model.
        _ = SomeIpConfigLoader.TryLoadFromSettings(builder.Settings, out SomeIpConfig? config, warnings);
        return _RegisterWithConfig(builder, config, warnings);
    }

    /// <summary>
    /// Same as <see cref="Register(IStackBuilder)"/>, but the configuration comes from <paramref name="stream"/> instead of the file
    /// setting. A document that cannot be read is a warning, and SOME/IP is still registered without names.
    /// </summary>
    /// <param name="builder">The stack builder during the registration phase.</param>
    /// <param name="stream">Readable stream positioned at the JSON; not closed.</param>
    /// <returns>Every warning; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="stream"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SettingsLoadWarning> Register(IStackBuilder builder, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stream);

        List<SettingsLoadWarning> warnings = [];
        _RegisterSettings(builder, warnings);
        _ = SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings);
        return _RegisterWithConfig(builder, config, warnings);
    }

    /// <summary>
    /// Same as <see cref="Register(IStackBuilder)"/>, but the configuration is an object the caller built or deserialized.
    /// </summary>
    /// <param name="builder">The stack builder during the registration phase.</param>
    /// <param name="config">The configuration; not modified.</param>
    /// <returns>Every warning; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="config"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SettingsLoadWarning> Register(IStackBuilder builder, SomeIpConfig config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        List<SettingsLoadWarning> warnings = [];
        _RegisterSettings(builder, warnings);
        return _RegisterWithConfig(builder, config, warnings);
    }

    #endregion

    #region Registration

    /// <summary>
    /// The shared tail of every overload: validates and compiles the configuration, creates the protocol and wires it into the
    /// stack. The order matters: the settings are read before the model is compiled (the file limits come from them), the
    /// protocol is registered before its fields (they need the protocol id), and the bindings come last because they reference it.
    /// </summary>
    private static List<SettingsLoadWarning> _RegisterWithConfig(IStackBuilder builder, SomeIpConfig? config, List<SettingsLoadWarning> warnings)
    {
        // A protocol name is unique in a stack. Registering twice is a mistake of the caller; it is reported and changes nothing.
        if (builder.GetProtocolId(SomeIpProtocol.ProtocolName) is not null)
        {
            warnings.Add(_Warning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                ConfigFileSetting,
                "SOME/IP is already registered on this stack; the second registration was ignored."));
            return warnings;
        }

        SomeIpSettings settings = _ReadSettings(builder.Settings);

        // Validate the document as a whole (version, size limits), then compile it. A refused document leaves the model empty,
        // so the protocol still decodes headers.
        SomeIpCompiledModel model = SomeIpCompiledModel.Empty;
        if (config is not null && SomeIpConfigLoader.TryAccept(config, settings.ConfigMaxServices, settings.ConfigMaxTypes, warnings))
        {
            string prefix = _ReadPrefix(builder.Settings, warnings);
            model = SomeIpConfigCompiler.Compile(config, prefix, warnings);
        }

        SomeIpProtocol protocol = new(model, in settings);
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);

        // A payload field that the host refused is not fatal (its value is shown under a generic field), but the author of the
        // description needs to know why a parameter does not carry the name they gave it.
        foreach (string problem in protocol.PayloadRegistrationProblems)
        {
            warnings.Add(_Warning(SettingsLoadWarningKind.DeserializationError, ConfigFileSetting, problem));
        }

        // Ports. UDP always listens on the well-known port because service discovery runs there whatever the configuration lists.
        // TCP gets the well-known port only when the configuration lists no TCP port: a list is the user's statement of where
        // SOME/IP runs, and adding the default behind it would decode unrelated traffic on 30490.
        _BindPorts(builder, id, UdpProtocol.PortTableName, SomeIpPorts.WithDefault(model.UdpPorts.Span, alwaysWellKnown: true), warnings);
        _BindPorts(builder, id, TcpProtocol.PortTableName, SomeIpPorts.WithDefault(model.TcpPorts.Span, alwaysWellKnown: false), warnings);

        // Plaintext of an encrypted transport. A protocol that decrypts TLS or DTLS owns the table of its transport (tls.port,
        // dtls.port) and publishes the marker (PlaintextHandoff) before it hands a unit on; the listed ports are bound there so the
        // decrypting protocol can dispatch by port, and the protocol still checks the marker against the list. The TLS and DTLS
        // parsers of this version do not decrypt and register no such table, so a non-empty list is reported: a user who lists a
        // port must learn that it has no effect here, instead of finding "TLS" and nothing above it in the tree.
        _BindPlaintextPorts(builder, id, PlaintextHandoff.TlsPortTableName, "tls_ports", model.TlsPorts.Span, warnings);
        _BindPlaintextPorts(builder, id, PlaintextHandoff.DtlsPortTableName, "dtls_ports", model.DtlsPorts.Span, warnings);

        // TCP framing. The detector needs the message size limit, and the stream buffer must be able to hold one maximal message
        // plus headroom, so its size follows the limit. Limit someip.tcp_max_message_size; threat: a hostile length makes the
        // buffer wait for and reserve a message that never arrives; user-changeable setting, secure by default. A message above
        // the limit is never framed; the buffer's own limit then drops it and the TCP layer reports the drop by name.
        builder.RegisterStreamReassemblyConfig(
            id,
            new StreamReassemblyConfig
            {
                BoundaryDetector = new SomeIpPduDetector(settings.TcpResync, settings.TcpMaxMessageSize),
                ResyncHeuristic = settings.TcpResync
                    ? new SomeIpResyncHeuristic(settings.TcpMaxMessageSize)
                    : null,
                MaxPduSize = settings.TcpMaxMessageSize,
                MaxBufferSize = settings.TcpMaxMessageSize + _StreamBufferHeadroom,
            });

        // Content-based detection on both transports. The parser only says "plausible header"; whether the unit is accepted is
        // decided inside the protocol, where the packet is known. A heuristic table that does not exist (a custom stack without
        // it) is skipped without a warning: detection is optional by nature.
        _RegisterHeuristic(builder, UdpProtocol.HeuristicTableName, id, requireWholeFirstMessage: true);
        _RegisterHeuristic(builder, TcpProtocol.HeuristicTableName, id, requireWholeFirstMessage: false);
        return warnings;
    }

    /// <summary>
    /// Binds each port once the table exists. The table of a transport may be registered after SOME/IP, so the binding is deferred
    /// until it appears; a failure inside the callback has no caller to throw to and becomes a warning.
    /// </summary>
    private static void _BindPorts(IStackBuilder builder, ProtocolId id, string tableName, List<ushort> ports, List<SettingsLoadWarning> warnings)
    {
        foreach (ushort port in ports)
        {
            // The loop variable is copied: the callback runs later and must bind the value of this iteration.
            ushort key = port;
            builder.WhenProtocolTableRegistered(tableName, tableId =>
            {
                try
                {
                    builder.RegisterParserInU64Table(tableId, key, id);
                }
                catch (Exception ex) when (ex is RegistrationException or InvalidOperationException)
                {
                    warnings.Add(_Warning(
                        SettingsLoadWarningKind.ExternalConfigUnavailable,
                        ConfigFileSetting,
                        string.Create(CultureInfo.InvariantCulture, $"SOME/IP could not bind {tableName} port {key}: {ex.Message}")));
                }
            });
        }
    }

    /// <summary>
    /// Binds the ports of a plaintext list into the table of the decrypting protocol and warns when no protocol of this stack has
    /// registered that table yet: the list then has no effect. The callback of a table that exists runs at once, which is how the
    /// presence is known; a table registered later still receives the ports (the binding is deferred), but the warning stands, so
    /// a decrypting protocol must register before SOME/IP, as the standard stack does for TLS and DTLS.
    /// </summary>
    /// <param name="builder">The stack builder.</param>
    /// <param name="id">The SOME/IP protocol.</param>
    /// <param name="tableName">The table of the transport (<see cref="PlaintextHandoff.TlsPortTableName"/> or the DTLS one).</param>
    /// <param name="listName">The configuration list, for the warning text.</param>
    /// <param name="ports">The listed ports.</param>
    /// <param name="warnings">Receives the warning when no table exists.</param>
    private static void _BindPlaintextPorts(
        IStackBuilder builder, ProtocolId id, string tableName, string listName, ReadOnlySpan<ushort> ports, List<SettingsLoadWarning> warnings)
    {
        if (ports.Length == 0)
        {
            return;
        }

        bool publisherPresent = false;
        builder.WhenProtocolTableRegistered(tableName, _ => publisherPresent = true);
        if (!publisherPresent)
        {
            string reason = string.Create(
                CultureInfo.InvariantCulture,
                $"{listName} lists {ports.Length} port(s), but no protocol of this stack delivers that plaintext (table {tableName} is not registered): ");
            warnings.Add(_Warning(
                SettingsLoadWarningKind.ExternalConfigUnavailable,
                ConfigFileSetting,
                reason + "the TLS and DTLS parsers of this version do not decrypt, so the listed ports have no effect."));
        }

        _BindPorts(builder, id, tableName, SomeIpPorts.Distinct(ports), warnings);
    }

    /// <summary>Adds the content-based detection to a heuristic table when the stack has it.</summary>
    private static void _RegisterHeuristic(IStackBuilder builder, string tableName, ProtocolId id, bool requireWholeFirstMessage)
    {
        HeuristicProtocolTableId? table = builder.GetHeuristicProtocolTableId(tableName);
        if (table is not null)
        {
            builder.RegisterHeuristicParser(table.Value, new SomeIpHeuristicParser(id, requireWholeFirstMessage));
        }
    }

    #endregion

    #region Settings

    /// <summary>
    /// Registers the settings group. A second registration on the same builder does nothing (the setting names are unique);
    /// persisted values that fail to apply become warnings. Each description says what the switch does and what its risk is,
    /// because an end user sees it in a settings dialog without the documentation.
    /// </summary>
    private static void _RegisterSettings(IStackBuilder builder, List<SettingsLoadWarning> warnings)
    {
        if (builder.Settings.GetSetting(ConfigFileSetting) is not null)
        {
            return;
        }

        SettingsRegistrar registrar = builder.SettingsRegistrar;
        registrar.RegisterGroup(GroupName, "SOME/IP", "Automotive service-oriented middleware over IP");

        SomeIpSettings defaults = SomeIpSettings.Default;

        _Check(
            registrar.RegisterStringSetting(
                ConfigFileSetting,
                "Configuration File",
                GroupName,
                defaultValue: string.Empty,
                description: "Path of the SOME/IP service description (JSON). It names services, methods and clients and describes payloads. Empty means no description: messages are decoded at header level."),
            ConfigFileSetting,
            warnings);

        // Naming choice, not a trust-boundary limit: the value is validated as a host name when it is read (_ReadPrefix), so no
        // range or size applies. Open by default in the sense that an empty prefix is the short form; a prefix is opt-in.
        _Check(
            registrar.RegisterStringSetting(
                FieldPrefixSetting,
                "Field Name Prefix",
                GroupName,
                defaultValue: string.Empty,
                description: "Text put in front of every filter name the service description produces: 'acme' turns ClimateControl.v2.SetTemperature into acme.ClimateControl.v2.SetTemperature. Empty means the names start with the service name. Use it when a service name could meet the name of a protocol, a field or a plugin. Letters, digits and underscores; a part starts with a letter or underscore; parts are joined by dots. Takes effect when the stack is built again."),
            FieldPrefixSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                DecodePayloadSetting,
                "Decode Payloads",
                GroupName,
                defaults.DecodePayload,
                "Decode the payload of configured methods, events and fields into typed fields. Off shows every payload as raw bytes; use it to compare with the bytes or to save time on very large captures."),
            DecodePayloadSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                ShowTypeMetadataSetting,
                "Show Type Metadata",
                GroupName,
                defaults.ShowTypeMetadata,
                "Add the type, length, padding and tag fields to a decoded payload. Useful to see why a payload decodes the way it does; it adds many fields to every decoded payload."),
            ShowTypeMetadataSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                VerifyE2eCrcSetting,
                "Verify End-to-End CRC",
                GroupName,
                defaults.VerifyE2eCrc,
                "Compute the end-to-end protection checksum of configured methods and compare it with the transmitted one. Off by default because it costs one pass over each protected payload."),
            VerifyE2eCrcSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                MethodTimingSetting,
                "Method Timing",
                GroupName,
                defaults.MethodTiming,
                "Pair each response with its request and show the delay. Off by default because it keeps every unanswered request in memory (bounded by 'Pending requests for method timing')."),
            MethodTimingSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                MethodTimingIgnoreInterfaceSetting,
                "Method Timing Across Interfaces",
                GroupName,
                defaults.MethodTimingIgnoreInterface,
                "Pair a request and a response that were captured on different interfaces. Needed when the two directions come from separate capture points; it can pair unrelated messages of two networks that reuse the same ids."),
            MethodTimingIgnoreInterfaceSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                LearnSdPortsSetting,
                "Learn Ports From Service Discovery",
                GroupName,
                defaults.LearnSdPorts,
                "Decode UDP traffic on ports that service discovery announced in an endpoint option, from the announcing packet on. A port named in an option is not proof that SOME/IP runs there: unrelated UDP traffic on that port would be decoded as SOME/IP."),
            LearnSdPortsSetting,
            warnings);

        _Check(
            registrar.RegisterU64ArraySetting(
                LearnSdPortsIgnoreSetting,
                "Ports Never Learned",
                GroupName,
                [],
                min: 1,
                max: ushort.MaxValue,
                description: "Ports that are never learned from service discovery, for example ports that other protocols use. Each value is a port from 1 to 65535."),
            LearnSdPortsIgnoreSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                HeuristicSetting,
                "Detect SOME/IP On Any Port",
                GroupName,
                defaults.Heuristic,
                "Decode a UDP or TCP payload that starts with a plausible SOME/IP header even when its port is not configured. A plausible header is not proof: unrelated traffic that happens to look like one would be decoded as SOME/IP."),
            HeuristicSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                TcpResyncSetting,
                "Resynchronize TCP Streams",
                GroupName,
                defaults.TcpResync,
                "When a TCP stream no longer starts at a message, scan forward for the next plausible header and continue there. Off frames the stream by the length field alone and never recovers from a damaged or missing segment."),
            TcpResyncSetting,
            warnings);

        _Check(
            registrar.RegisterBoolSetting(
                DetectDtlsSetting,
                "Detect DTLS On SOME/IP Ports",
                GroupName,
                defaults.DetectDtls,
                "Hand a datagram on a SOME/IP port to the DTLS parser when it looks like a DTLS record and not like SOME/IP. The check is a heuristic on untrusted data and can misjudge a datagram."),
            DetectDtlsSetting,
            warnings);

        // The ten limits: each definition carries its range, its secure default and its seven texts.
        foreach (SomeIpLimitDefinition limit in SomeIpLimits.All)
        {
            _Check(
                registrar.RegisterU64Setting(
                    limit.Key,
                    limit.Texts.Title,
                    GroupName,
                    limit.Default,
                    limit.Minimum,
                    limit.Maximum,
                    limit.SettingDescription),
                limit.Key,
                warnings,
                limit);
        }
    }

    /// <summary>
    /// Reads every setting with the registered default as fallback and narrows the limits to the types the parser uses. The
    /// settings manager has already refused values outside the registered range, so the narrowing cannot overflow; the clamp
    /// is a second line of defense for a manager that was given a different range by an earlier registration.
    /// </summary>
    private static SomeIpSettings _ReadSettings(ReadOnlySettingsManagerView settings)
    {
        SomeIpSettings d = SomeIpSettings.Default;
        return new SomeIpSettings(
            settings.GetBoolSetting(DecodePayloadSetting) ?? d.DecodePayload,
            settings.GetBoolSetting(ShowTypeMetadataSetting) ?? d.ShowTypeMetadata,
            settings.GetBoolSetting(VerifyE2eCrcSetting) ?? d.VerifyE2eCrc,
            settings.GetBoolSetting(MethodTimingSetting) ?? d.MethodTiming,
            settings.GetBoolSetting(MethodTimingIgnoreInterfaceSetting) ?? d.MethodTimingIgnoreInterface,
            settings.GetBoolSetting(LearnSdPortsSetting) ?? d.LearnSdPorts,
            _ReadPorts(settings.GetU64ArraySetting(LearnSdPortsIgnoreSetting)),
            settings.GetBoolSetting(HeuristicSetting) ?? d.Heuristic,
            settings.GetBoolSetting(TcpResyncSetting) ?? d.TcpResync,
            settings.GetBoolSetting(DetectDtlsSetting) ?? d.DetectDtls,
            _ReadInt(settings, SomeIpLimits.MaxMessagesPerUnit),
            _ReadInt(settings, SomeIpLimits.MaxTypeDepth),
            _ReadInt(settings, SomeIpLimits.MaxPayloadNodes),
            _ReadInt(settings, SomeIpLimits.TpMaxSessions),
            _ReadInt(settings, SomeIpLimits.TpMaxMessageSize),
            _ReadLong(settings, SomeIpLimits.TpMaxBufferedBytes),
            _ReadLong(settings, SomeIpLimits.TpMaxDerivedBytesPerPacket),
            _ReadInt(settings, SomeIpLimits.TcpMaxMessageSize),
            _ReadInt(settings, SomeIpLimits.ConfigMaxServices),
            _ReadInt(settings, SomeIpLimits.ConfigMaxTypes),
            _ReadInt(settings, SomeIpLimits.TimingMaxPending));
    }

    private static int _ReadInt(ReadOnlySettingsManagerView settings, SomeIpLimitDefinition limit)
    {
        ulong value = settings.GetU64Setting(limit.Key) ?? limit.Default;
        return (int)Math.Min(value, int.MaxValue);
    }

    private static long _ReadLong(ReadOnlySettingsManagerView settings, SomeIpLimitDefinition limit)
    {
        ulong value = settings.GetU64Setting(limit.Key) ?? limit.Default;
        return (long)Math.Min(value, long.MaxValue);
    }

    /// <summary>Narrows the ports of an array setting; a value outside the port range cannot occur after the range check and is dropped.</summary>
    private static ushort[] _ReadPorts(ulong[]? values)
    {
        if (values is null)
        {
            return [];
        }

        List<ushort> ports = new(values.Length);
        foreach (ulong value in values)
        {
            if (value is >= 1 and <= ushort.MaxValue)
            {
                ports.Add((ushort)value);
            }
        }

        return [.. ports];
    }

    /// <summary>
    /// Turns the result of a setting registration into a warning when a persisted value could not be applied. The silent results
    /// are a success and the absence of a persisted value.
    /// </summary>
    private static void _Check(
        SettingRegistrationResult result,
        string settingName,
        List<SettingsLoadWarning> warnings,
        SomeIpLimitDefinition? limit = null)
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

        // A limit that is out of range names the range, because the user needs it to correct the value; the default stays in force.
        string text = limit is not null && result.LoadResult == SettingLoadResult.OutOfRange
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Persisted value for '{settingName}' is outside the allowed range {limit.Minimum} to {limit.Maximum}{_UnitSuffix(limit)}. The default {limit.Default} is kept.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Persisted value for '{settingName}' could not be applied ({result.LoadResult}). The value is ignored.");
        warnings.Add(_Warning(kind, settingName, text));
    }

    /// <summary>
    /// Reads the field prefix. The value comes from the user's settings (an end user may change it) and becomes part of every
    /// filter name, so it must be a valid dotted name; an invalid value is reported and not used, which keeps the description
    /// loadable. Rejected: refusing the whole description for a bad prefix (the author of the file did nothing wrong).
    /// </summary>
    private static string _ReadPrefix(ReadOnlySettingsManagerView settings, List<SettingsLoadWarning> warnings)
    {
        string value = settings.GetStringSetting(FieldPrefixSetting) ?? "";
        if (value.Length == 0 || SomeIpNameTokens.IsValidPrefix(value))
        {
            return value;
        }

        warnings.Add(_Warning(
            SettingsLoadWarningKind.OutOfRange,
            FieldPrefixSetting,
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{value}' is not a valid field prefix (parts of letters, digits and underscores, each starting with a letter or underscore, joined by dots); the names carry no prefix.")));
        return "";
    }

    private static string _UnitSuffix(SomeIpLimitDefinition limit) => limit.Unit == SomeIpLimitUnit.Bytes
        ? " bytes"
        : "";

    private static SettingsLoadWarning _Warning(SettingsLoadWarningKind kind, string settingName, string message) =>
        new(kind, GroupName, settingName, message);

    #endregion
}
