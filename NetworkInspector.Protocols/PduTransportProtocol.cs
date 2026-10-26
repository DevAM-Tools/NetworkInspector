// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// PDU Transport protocol parser. Parses concatenated PDUs from UDP datagrams,
/// each with an ID + length header followed by payload bytes.
/// <para>
/// Header format (configurable field sizes, big-endian):
/// </para>
/// <code>
/// [PDU ID:    id_field_size bytes]
/// [Length:    length_field_size bytes]
/// [Payload:  length bytes]
/// </code>
/// <para>Field tree structure (PROTOCOL_GUIDE sibling layout, same as Ethernet / IPv6 / UDP):</para>
/// <code>
/// parent (e.g. same parent as udp)
/// ├── pdu_transport: PDU Transport
/// │   ├── pdu_transport.pdu: PDU: BrakeStatus (ID: 1)
/// │   │   ├── pdu_transport.id: 1
/// │   │   ├── pdu_transport.length: 8
/// │   │   ├── pdu_transport.name: "BrakeStatus"          [optional, from config]
/// │   │   └── pdu_transport.payload: (8 bytes)          [when no sub-protocol matches]
/// │   └── pdu_transport.pdu: PDU: EngineData (ID: 2)
/// │       ├── pdu_transport.id: 2
/// │       └── pdu_transport.length: 16
/// └── fixture_message                                   [sibling of pdu_transport; dispatch on parentField]
/// </code>
/// <para>
/// Header fields and dispatch run eagerly in <see cref="IProtocol.Parse"/> so index groups are
/// complete at packet finalization. Sub-protocols are siblings of this container (dispatch on
/// <c>parentField</c>), not children of <c>pdu_transport.pdu</c>.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>_RegisterFieldsCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. The configuration methods (<see cref="TryLoadConfigFromStream"/>,
/// <see cref="ApplyConfig"/>, <see cref="AppendRegistrationWarnings"/>) belong to that build phase: they are not
/// thread-safe, and the first two are rejected once fields have been registered.</para>
/// </remarks>
[Protocol("pdu_transport", "PDU Transport", Description = "PDU Transport (AUTOSAR)")]
public sealed partial class PduTransportProtocol : IProtocol
{
    #region Constants

    /// <summary>Protocol table name for PDU identifier dispatch.</summary>
    public const string IdTableName = "pdu_transport.id";

    /// <summary>Index group for always-present PDU Transport fields.</summary>
    private const string _PduTrIndexGroup = "pdu_transport";

    /// <summary>Index group for optional name field.</summary>
    private const string _PduTrNameGroup = "pdu_transport.name";

    /// <summary>Index group for raw payload (when no sub-protocol matches).</summary>
    private const string _PduTrPayloadGroup = "pdu_transport.payload";

    #endregion

    #region Fields

    [NoneField("pdu_transport", "PDU Transport", IndexGroup = _PduTrIndexGroup)]
    private FieldId _ProtocolFieldId;

    [NoneField("pdu_transport.pdu", "PDU", IndexGroup = _PduTrIndexGroup)]
    private FieldId _PduFieldId;

    [U64Field("pdu_transport.id", "PDU ID", IndexGroup = _PduTrIndexGroup)]
    private FieldId _IdFieldId;

    [U64Field("pdu_transport.length", "Length", IndexGroup = _PduTrIndexGroup)]
    private FieldId _LengthFieldId;

    [StringField("pdu_transport.name", "Name", IndexGroup = _PduTrNameGroup)]
    private FieldId _NameFieldId;

    [BytesField("pdu_transport.payload", "Payload", IndexGroup = _PduTrPayloadGroup)]
    private FieldId _PayloadFieldId;

    #endregion

    #region Dispatch table

    [ProtocolTableU64(IdTableName, "PDU Transport ID")]
    private ProtocolTableId _IdTableId;

    #endregion

    #region Settings

    [StringSetting("pdu_transport.config_file", "Configuration File", "pdu_transport", Default = "")]
    private string? _ConfigFile;

    // The widths of the ID and length fields are settings. Only 1, 2 and 4 bytes are accepted (checked in
    // _RegisterFieldsCustom): each width maps to a native integer read in _ReadBigEndianUint, and the sum is the
    // fixed header size used by Parse. Both are constant after registration.
    [U64Setting("pdu_transport.id_field_size", "ID Field Size (bytes)", "pdu_transport", Default = 4)]
    private ulong _IdFieldSize;

    [U64Setting("pdu_transport.length_field_size", "Length Field Size (bytes)", "pdu_transport", Default = 4)]
    private ulong _LengthFieldSize;

    /// <summary>
    /// UDP ports that select this parser on <see cref="UdpProtocol.PortTableName"/>. Empty (default) means
    /// no UDP auto-dispatch. Each element in 1–65535 becomes a table key; UDP matches source or destination.
    /// Values outside that range are skipped and recorded on <see cref="UdpDispatchPortsWarning"/>.
    /// </summary>
    [U64ArraySetting(
        "pdu_transport.udp_dispatch_ports",
        "UDP dispatch ports",
        "pdu_transport",
        Description = "UDP ports (1-65535) that select PDU Transport. Empty means no UDP auto-dispatch. Values outside 1-65535 are skipped with a warning.")]
    private ulong[] _UdpDispatchPorts = [];

    #endregion

    #region Runtime state

    /// <summary>
    /// PDU ID → display name lookup, built from config. Frozen because it is built once during registration and
    /// then read by every parse thread without locking; an immutable lookup needs no synchronization and is
    /// optimized for reads.
    /// </summary>
    private FrozenDictionary<uint, string> _NameLookup = FrozenDictionary<uint, string>.Empty;

    /// <summary>
    /// Extra PDU names from <see cref="TryLoadConfigFromStream"/> or <see cref="ApplyConfig"/>.
    /// Merged on top of the settings/profile file during <c>RegisterFields</c>; an empty
    /// file setting is valid and yields only these additional names.
    /// </summary>
    private PduTransportConfig? _AdditionalConfig;

    /// <summary>Load warning from a stream attempt; copied during registration.</summary>
    private SettingsLoadWarning? _AdditionalLoadWarning;

    /// <summary>True after <c>_RegisterFieldsCustom</c> has run; further config apply is rejected.</summary>
    private bool _FieldsRegistered;

    /// <summary>
    /// Warning produced during registration if the config file referenced by
    /// <c>pdu_transport.config_file</c> could not be loaded, or if an additional stream
    /// load failed and no file warning was produced; <see langword="null"/> when loading
    /// succeeded or no path was configured.
    /// </summary>
    public SettingsLoadWarning? ConfigLoadWarning
    {
        get; private set;
    }

    /// <summary>
    /// Warning from an additional stream/object config load
    /// (<see cref="TryLoadConfigFromStream"/>). Independent of <see cref="ConfigLoadWarning"/>
    /// so a failed extra document does not hide a file-setting issue.
    /// </summary>
    public SettingsLoadWarning? AdditionalConfigLoadWarning
    {
        get; private set;
    }

    /// <summary>
    /// Warning produced during registration if the configured
    /// <c>pdu_transport.id_field_size</c> setting was not one of the supported values
    /// (1, 2 or 4 bytes) and was therefore clamped to the default of 4.
    /// <see langword="null"/> when the configured value was valid.
    /// </summary>
    public SettingsLoadWarning? IdFieldSizeClampWarning
    {
        get; private set;
    }

    /// <summary>
    /// Warning produced during registration if the configured
    /// <c>pdu_transport.length_field_size</c> setting was not one of the supported values
    /// (1, 2 or 4 bytes) and was therefore clamped to the default of 4.
    /// <see langword="null"/> when the configured value was valid.
    /// </summary>
    public SettingsLoadWarning? LengthFieldSizeClampWarning
    {
        get; private set;
    }

    /// <summary>
    /// Warning produced during registration if <c>pdu_transport.udp_dispatch_ports</c> contained
    /// values outside 1–65535. Those elements are skipped; in-range ports still bind.
    /// Duplicate in-range values are silent. <see langword="null"/> when the array is empty
    /// or no element is outside 1–65535.
    /// </summary>
    public SettingsLoadWarning? UdpDispatchPortsWarning
    {
        get; private set;
    }

    #endregion

    #region Public config API

    /// <summary>
    /// Copies every registration warning (file load, additional stream load, field-size clamps,
    /// UDP dispatch port filter) into <paramref name="warnings"/>. Call after <c>RegisterFields</c>
    /// so the caller can decide how to surface them; nothing is discarded here.
    /// </summary>
    /// <param name="warnings">Destination list. Caller guarantees non-null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="warnings"/> is <see langword="null"/>.</exception>
    public void AppendRegistrationWarnings(ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        // When no file warning exists, _RegisterFieldsCustom reports the additional-load warning as ConfigLoadWarning
        // too; the comparison below keeps the same failure from being added twice.
        if (ConfigLoadWarning is { } configWarning)
        {
            warnings.Add(configWarning);
        }

        if (AdditionalConfigLoadWarning is { } additionalWarning
            && additionalWarning != ConfigLoadWarning)
        {
            warnings.Add(additionalWarning);
        }

        if (IdFieldSizeClampWarning is { } idWarning)
        {
            warnings.Add(idWarning);
        }

        if (LengthFieldSizeClampWarning is { } lengthWarning)
        {
            warnings.Add(lengthWarning);
        }

        if (UdpDispatchPortsWarning is { } portsWarning)
        {
            warnings.Add(portsWarning);
        }
    }

    /// <summary>
    /// Deserializes PDU Transport JSON from <paramref name="stream"/> and stores it as
    /// <b>additional</b> names, merged on top of the settings/profile file during
    /// <c>RegisterFields</c>. Must be called before field registration. A failed load
    /// still records a warning and does not skip the file setting.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the JSON payload. Not closed.</param>
    /// <param name="warning">Set when deserialization fails; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the JSON was deserialized successfully.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when fields have already been registered.</exception>
    public bool TryLoadConfigFromStream(Stream stream, out SettingsLoadWarning? warning)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ThrowIfFieldsRegistered();

        // Trust boundary: JsonConfigStream applies no size or depth limit to a caller-owned stream (unlike the
        // settings file path, which is bounded by the settings manager). The caller must bound an untrusted stream.
        // A failed load is an expected failure: it is reported as a warning and leaves no additional config behind.
        bool ok = JsonConfigStream.TryLoad(
            stream,
            PduTransportConfigContext.Default.PduTransportConfig,
            "pdu_transport",
            PduTransportRegistration.ConfigFileSetting,
            out PduTransportConfig? config,
            out warning);

        _AdditionalConfig = ok ? config : null;
        _AdditionalLoadWarning = warning;
        return ok;
    }

    /// <summary>
    /// Stores an already-deserialized config as <b>additional</b> PDU names, merged on
    /// top of the settings/profile file during <c>RegisterFields</c>.
    /// Must be called before field registration. Replaces any previous additional load.
    /// </summary>
    /// <param name="config">Deserialized PDU Transport configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when fields have already been registered.</exception>
    public void ApplyConfig(PduTransportConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _ThrowIfFieldsRegistered();
        _AdditionalConfig = config;
        _AdditionalLoadWarning = null;
    }

    #endregion

    #region Registration

    /// <summary>
    /// Validates setting values and builds the PDU name lookup from the settings/profile
    /// file (may be empty) plus any additional stream/object config. Performing this
    /// during registration keeps the resulting <c>Stack</c> immutable.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _FieldsRegistered = true;

        // Validate field sizes — only 1, 2, and 4 byte sizes are supported.
        // Invalid values are clamped to the default (4 bytes) and a warning is
        // surfaced via the corresponding *ClampWarning property so callers can
        // detect and report misconfiguration instead of seeing it silently swallowed.
        if (_IdFieldSize is not (1 or 2 or 4))
        {
            IdFieldSizeClampWarning = new SettingsLoadWarning(
                SettingsLoadWarningKind.OutOfRange,
                "pdu_transport",
                "pdu_transport.id_field_size",
                $"Unsupported ID field size {_IdFieldSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + " \u2014 only 1, 2, or 4 are valid; clamped to default 4.");
            _IdFieldSize = 4;
        }
        if (_LengthFieldSize is not (1 or 2 or 4))
        {
            LengthFieldSizeClampWarning = new SettingsLoadWarning(
                SettingsLoadWarningKind.OutOfRange,
                "pdu_transport",
                "pdu_transport.length_field_size",
                $"Unsupported length field size {_LengthFieldSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + " \u2014 only 1, 2, or 4 are valid; clamped to default 4.");
            _LengthFieldSize = 4;
        }

        // Load the optional name file referenced by the setting (bounded by the settings manager's file size and JSON
        // depth limits). A failure is not fatal: parsing works without names, so it is surfaced as a warning only.
        builder.Settings.TryLoadReferencedJsonConfig(
            PduTransportRegistration.ConfigFileSetting, PduTransportConfigContext.Default.PduTransportConfig,
            out PduTransportConfig? fileConfig, out SettingsLoadWarning? fileLoadWarning);
        ConfigLoadWarning = fileLoadWarning;
        AdditionalConfigLoadWarning = _AdditionalLoadWarning;
        if (ConfigLoadWarning is null)
        {
            ConfigLoadWarning = AdditionalConfigLoadWarning;
        }

        _ApplyNameLookup(fileConfig, _AdditionalConfig);

        // UDP auto-dispatch is opt-in: with no configured port this protocol is never selected by UDP. The port table
        // belongs to UDP and may be registered before or after this protocol, so the binding is deferred until the
        // table exists (the callback fires immediately when it already does). Out-of-range ports were already
        // filtered (and warned about).
        ulong[] udpPorts = _FilterUdpDispatchPorts(_UdpDispatchPorts, out SettingsLoadWarning? udpPortsWarning);
        UdpDispatchPortsWarning = udpPortsWarning;
        if (udpPorts.Length > 0)
        {
            builder.WhenProtocolTableRegistered(UdpProtocol.PortTableName, table =>
            {
                foreach (ulong portKey in udpPorts)
                {
                    builder.RegisterParserInU64Table(table, portKey, protocolId);
                }
            });
        }
    }

    /// <summary>
    /// Keeps unique UDP ports in 1–65535. Out-of-range values produce one
    /// <see cref="SettingsLoadWarningKind.OutOfRange"/> warning and are not bound.
    /// </summary>
    /// <param name="raw">Loaded setting elements; <see langword="null"/> or empty yields no ports and no warning.</param>
    /// <param name="warning">Set when any element is outside 1–65535; otherwise <see langword="null"/>.</param>
    /// <returns>Distinct ports in 1–65535, in first-seen order.</returns>
    private static ulong[] _FilterUdpDispatchPorts(ulong[] raw, out SettingsLoadWarning? warning)
    {
        if (raw is not { Length: > 0 })
        {
            warning = null;
            return [];
        }

        // Single pass: out-of-range values are collected for one combined warning (never dropped silently), and
        // duplicates are dropped without a warning so that each port is bound exactly once.
        // 'seen' answers the duplicate question in constant time; a List.Contains per element would cost
        // O(n^2) for a very large setting array. 'ports' keeps the first-seen order.
        List<ulong> ports = [];
        HashSet<ulong> seen = [];
        List<string> skipped = [];
        for (int i = 0; i < raw.Length; i++)
        {
            ulong candidate = raw[i];
            if (candidate is < 1UL or > 65535UL)
            {
                skipped.Add(candidate.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (!seen.Add(candidate))
            {
                continue;
            }

            ports.Add(candidate);
        }

        if (skipped.Count > 0)
        {
            warning = new SettingsLoadWarning(
                SettingsLoadWarningKind.OutOfRange,
                "pdu_transport",
                PduTransportRegistration.UdpDispatchPortsSetting,
                skipped.Count.ToString(CultureInfo.InvariantCulture)
                + " UDP dispatch port value(s) skipped (not an integer in 1-65535): "
                + string.Join(", ", skipped));
        }
        else
        {
            warning = null;
        }

        return ports.ToArray();
    }

    /// <summary>Rejects config apply after <c>RegisterFields</c> has committed runtime state.</summary>
    private void _ThrowIfFieldsRegistered()
    {
        if (_FieldsRegistered)
        {
            throw new InvalidOperationException(
                "PDU Transport config must be applied before RegisterFields.");
        }
    }

    /// <summary>
    /// Builds the frozen ID→name lookup from the settings file, then overlays additional
    /// names (same ID overwrites). Empty sources yield an empty lookup — parsing still works.
    /// </summary>
    private void _ApplyNameLookup(PduTransportConfig? fileConfig, PduTransportConfig? additionalConfig)
    {
        // Precedence: the additional (programmatic) names win over the settings file, so a host can override the
        // file. For duplicate IDs inside the settings file the first entry wins, inside the additional config the
        // last one wins (a consequence of overwrite being a per-source flag).
        Dictionary<uint, string> names = [];
        _MergePdus(names, fileConfig, overwrite: false);
        _MergePdus(names, additionalConfig, overwrite: true);
        _NameLookup = names.Count == 0
            ? FrozenDictionary<uint, string>.Empty
            : names.ToFrozenDictionary();
    }

    /// <summary>
    /// Copies PDU display names into <paramref name="names"/>. Empty names are skipped.
    /// With <paramref name="overwrite"/> false the first entry for an ID wins (also over entries already in
    /// <paramref name="names"/>); with true the last entry wins, replacing existing names.
    /// </summary>
    /// <param name="names">Destination lookup.</param>
    /// <param name="config">Source config; <see langword="null"/> or empty is ignored.</param>
    /// <param name="overwrite">Whether later entries replace existing names.</param>
    private static void _MergePdus(Dictionary<uint, string> names, PduTransportConfig? config, bool overwrite)
    {
        if (config?.Pdus is not { Length: > 0 })
        {
            return;
        }

        foreach (PduTransportPduEntry entry in config.Pdus)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (overwrite)
            {
                names[entry.Id] = entry.Name;
            }
            else
            {
                names.TryAdd(entry.Id, entry.Name);
            }
        }
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses PDU Transport datagrams containing one or more concatenated PDUs.
    /// Header fields are appended eagerly as children of the protocol container
    /// (Ethernet / IPv6 / UDP pattern). Sub-protocol dispatch uses <c>parentField</c>
    /// so Signal Messages are siblings of <c>pdu_transport</c>, not children of it.
    /// <para>
    /// A trailing PDU whose declared length exceeds the remaining bytes is shown with the bytes that are present
    /// (the length field keeps the declared value). The whole datagram is always consumed. Thread safety: only
    /// reads state fixed at registration; may be called concurrently.
    /// </para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the container; dispatched sub-protocols become siblings.</param>
    /// <param name="data">The UDP payload: concatenated PDUs.</param>
    /// <param name="context">Parse context used to record index groups and dispatch the PDU payloads.</param>
    /// <returns>The number of bytes consumed (all of <paramref name="data"/>), or a <see cref="ParseError"/>.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The field widths were validated to 1, 2 or 4 bytes at registration, so the casts cannot truncate and the
        // header size is 2 to 8 bytes.
        int headerSize = (int)_IdFieldSize + (int)_LengthFieldSize;
        if (data.Length < headerSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)headerSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Pdu_transportGroupId);

        parentField.SetPacketInfo(new LazyString("PDU Transport"));

        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, containerValue, new LazyString("PDU Transport"));

        // Walk the concatenated PDUs. Each iteration consumes at least the header (2 bytes or more), so the loop
        // terminates and the number of PDU nodes is bounded by the datagram size; there is no separate PDU count
        // limit. A trailing fragment shorter than a header is ignored.
        ReadOnlySpan<byte> span = data.Span;
        int idSize = (int)_IdFieldSize;
        int lenSize = (int)_LengthFieldSize;
        int offset = 0;
        while (offset + headerSize <= span.Length)
        {
            uint pduId = _ReadBigEndianUint(span[offset..], idSize);
            offset += idSize;

            uint payloadLength = _ReadBigEndianUint(span[offset..], lenSize);
            offset += lenSize;

            string? name = _NameLookup.GetValueOrDefault(pduId);
            bool hasName = !string.IsNullOrEmpty(name);
            if (hasName)
            {
                context.RecordGroupPresence(_Pdu_transportNameGroupId);
            }

            // Clamp the declared length to the bytes present (a datagram can be cut short); the Length field below
            // still shows the declared value. The comparison is done on unsigned values: a 4-byte length of 0x80000000
            // or more would turn negative in an int cast, and a negative payload size moves the read position backwards
            // (or before the start of the datagram), which makes the next slice throw on a hostile datagram.
            int availablePayload = span.Length - offset;
            int actualPayload = payloadLength > (uint)availablePayload
                ? availablePayload
                : (int)payloadLength;

            MutField pduField = hasName
                ? container.AppendWithCustomText(
                    _PduFieldId, FieldValue.None, "PDU: ", name!, " (ID: ", pduId, ")")
                : container.AppendWithCustomText(
                    _PduFieldId, FieldValue.None, "PDU (ID: ", pduId, ")");
            pduField.Append(_IdFieldId, FieldValue.NewU64(pduId));
            pduField.Append(_LengthFieldId, FieldValue.NewU64(payloadLength));
            if (hasName)
            {
                pduField.Append(_NameFieldId, FieldValue.NewString(name!));
            }

            // Hand the payload to the sub-protocol registered for this PDU ID. An error from it aborts the remaining
            // PDUs and is propagated. When nothing consumed the payload (no protocol registered, or it declined), the
            // raw bytes are attached so they stay visible; the index group is recorded in the same branch.
            if (actualPayload > 0)
            {
                ReadOnlyMemory<byte> payloadData = data.Slice(offset, actualPayload);

                ParseResult dispatchResult = parentField.TryCallNextProtocolU64(
                    _IdTableId, pduId, payloadData, in context);
                if (dispatchResult.TryPropagateError(out ParseResult error))
                {
                    return error;
                }

                if (!dispatchResult.TryGetConsumed(out int consumed) || consumed == 0)
                {
                    context.RecordGroupPresence(_Pdu_transportPayloadGroupId);
                    pduField.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));
                }
            }

            offset += actualPayload;
        }

        return data.Length;
    }

    /// <summary>
    /// Reads a big-endian unsigned integer of the given byte size (1, 2, or 4).
    /// </summary>
    /// <param name="span">Bytes starting at the field; must hold at least <paramref name="size"/> bytes (the caller's
    /// loop condition guarantees it).</param>
    /// <param name="size">Field width in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not 1, 2 or 4 (a broken invariant: the
    /// sizes are validated at registration).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint _ReadBigEndianUint(ReadOnlySpan<byte> span, int size) => size switch
    {
        1 => span[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(span),
        4 => BinaryPrimitives.ReadUInt32BigEndian(span),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Supported field sizes are 1, 2, or 4 bytes."),
    };

    #endregion
}
