// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Signal PDU protocol — bit-level signal decoder for automotive payloads.
/// Decodes individual signals from PDU bytes using signal definitions loaded from
/// a JSON configuration file. Supports big-endian and little-endian byte order,
/// signed/unsigned/float data types, linear scaling, value name mapping,
/// and multiplexer-dependent signal groups.
/// <para>
/// Signal PDU does not register at any fixed dispatch table. Instead, it registers
/// dynamically into parent tables (<c>can.id</c>, <c>can.extended_id</c>, <c>pdu_transport.id</c>,
/// <c>flexray.id</c>, <c>lin.id</c>)
/// based on the <c>register_at</c> entries in the configuration file.
/// </para>
/// <para>Field tree structure:</para>
/// <code>
/// signal_pdu: EngineSignals
/// ├── signal_pdu.pdu_id: 1
/// ├── signal_pdu.name: "EngineSignals"
/// ├── signal_pdu.signal: EngineSpeed: 3000.00 rpm
/// │   └── signal_pdu.signal.raw: 12000
/// ├── signal_pdu.signal: EngineTemp: 85.00 °C
/// │   └── signal_pdu.signal.raw: 125
/// ├── signal_pdu.mux: Multiplexer                    [optional]
/// │   ├── signal_pdu.mux.value: 0
/// │   └── signal_pdu.signal: FrontLeftPressure: 12.3 bar
/// │       └── signal_pdu.signal.raw: 123
/// └── signal_pdu.payload.unparsed: (2 bytes)         [optional]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>_RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("signal_pdu", "Signal PDU", Description = "Signal PDU (Automotive Signal Decoder)")]
public sealed partial class SignalPduProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for always-present Signal PDU fields.</summary>
    private const string _SpduIndexGroup = "signal_pdu";

    /// <summary>Index group for mux container.</summary>
    private const string _SpduMuxGroup = "signal_pdu.mux";

    /// <summary>Index group for unparsed payload.</summary>
    private const string _SpduUnparsedGroup = "signal_pdu.unparsed";

    #endregion

    #region Fields

    [NoneField("signal_pdu", "Signal PDU", IndexGroup = _SpduIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("signal_pdu.pdu_id", "PDU ID", IndexGroup = _SpduIndexGroup)]
    private FieldId _PduIdFieldId;

    [StringField("signal_pdu.name", "Name", IndexGroup = _SpduIndexGroup)]
    private FieldId _NameFieldId;

    [NoneField("signal_pdu.signal", "Signal", IndexGroup = _SpduIndexGroup)]
    private FieldId _SignalFieldId;

    [U64Field("signal_pdu.signal.raw", "Raw Value", IndexGroup = _SpduIndexGroup)]
    private FieldId _SignalRawFieldId;

    [NoneField("signal_pdu.mux", "Multiplexer", IndexGroup = _SpduMuxGroup)]
    private FieldId _MuxFieldId;

    [U64Field("signal_pdu.mux.value", "Mux Value", IndexGroup = _SpduMuxGroup)]
    private FieldId _MuxValueFieldId;

    [BytesField("signal_pdu.payload.unparsed", "Unparsed Payload", IndexGroup = _SpduUnparsedGroup)]
    private FieldId _UnparsedFieldId;

    #endregion

    #region Settings

    [StringSetting("signal_pdu.config_file", "Configuration File", "signal_pdu", Default = "")]
    private string? _ConfigFile;

    #endregion

    #region Runtime state

    /// <summary>
    /// PDU definitions indexed by PDU ID.
    /// </summary>
    private FrozenDictionary<uint, SignalPduDefinition> _PduDefinitions = FrozenDictionary<uint, SignalPduDefinition>.Empty;

    /// <summary>
    /// Reverse lookup: (dispatch table ID, numeric key) → PDU ID.
    /// The composite key eliminates ambiguity when different parent protocols (e.g., CAN and
    /// FlexRay) share the same numeric key value for different PDUs. Populated in
    /// <see cref="IStackBuilder.WhenProtocolTableRegistered"/> callbacks where the
    /// <see cref="ProtocolTableId"/> is first known, then frozen in <c>_OnStartCustom</c>.
    /// </summary>
    private FrozenDictionary<(ProtocolTableId, ulong), uint> _DispatchKeyToPduId
        = FrozenDictionary<(ProtocolTableId, ulong), uint>.Empty;

    /// <summary>
    /// Mutable accumulator for <see cref="_DispatchKeyToPduId"/> during the build phase.
    /// Entries are added inside <see cref="IStackBuilder.WhenProtocolTableRegistered"/> callbacks
    /// where the <see cref="ProtocolTableId"/> is first available. Frozen and set to
    /// <see langword="null"/> in <c>_OnStartCustom</c> to release the builder memory.
    /// </summary>
    private Dictionary<(ProtocolTableId, ulong), uint>? _BuildingTableKeyToPduId;

    /// <summary>
    /// Warning produced during registration if the config file referenced by
    /// <c>signal_pdu.config_file</c> could not be loaded; <see langword="null"/> when loading
    /// succeeded or no path was configured.
    /// </summary>
    public SettingsLoadWarning? ConfigLoadWarning
    {
        get; private set;
    }

    /// <summary>
    /// Loads the JSON configuration, builds PDU lookup tables, and registers SignalPdu
    /// in the parent dispatch tables (e.g., <c>can.id</c>, <c>pdu_transport.id</c>) named by
    /// each PDU's <c>register_at</c> entries. Runs at the end of RegisterFields, before the
    /// stack is frozen, so the resulting <c>Stack</c> stays truly immutable.
    /// <para>
    /// Parent dispatch tables are resolved through <see cref="IStackBuilder.WhenProtocolTableRegistered"/>,
    /// so SignalPdu does not depend on the order in which parent protocols are registered
    /// relative to itself. The dispatch table ID and key are propagated from the parent parser
    /// to this protocol via <see cref="ParseContext"/>.<see cref="ParseContext.Dispatch"/>,
    /// which eliminates the need to re-read parent protocol fields from the packet at parse time.
    /// </para>
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        // Load configuration
        builder.Settings.TryLoadReferencedJsonConfig(
            "signal_pdu.config_file", SignalPduConfigContext.Default.SignalPduConfig,
            out SignalPduConfig? config, out SettingsLoadWarning? configLoadWarning);
        ConfigLoadWarning = configLoadWarning;

        if (config?.Pdus is not { Length: > 0 })
        {
            _PduDefinitions = FrozenDictionary<uint, SignalPduDefinition>.Empty;
            _DispatchKeyToPduId = FrozenDictionary<(ProtocolTableId, ulong), uint>.Empty;
            return;
        }

        // Build PDU lookup by ID and dispatch key reverse map.
        // The dispatch key accumulator (_BuildingTableKeyToPduId) is populated inside the
        // WhenProtocolTableRegistered callback so that the ProtocolTableId is included in the
        // composite key, distinguishing (e.g.) CAN ID 42 from FlexRay slot 42.
        Dictionary<uint, SignalPduDefinition> defs = new(config.Pdus.Length);
        _BuildingTableKeyToPduId = new(config.Pdus.Length * 2);

        foreach (SignalPduDefinition pdu in config.Pdus)
        {
            defs.TryAdd(pdu.PduId, pdu);

            // Pre-compute numeric value name dictionaries to avoid per-signal .ToString() allocation
            foreach (SignalDefinition signal in pdu.Signals)
            {
                signal.BuildNumericValueNames();
            }
            foreach (MuxGroup group in pdu.MuxGroups)
            {
                foreach (SignalDefinition signal in group.Signals)
                {
                    signal.BuildNumericValueNames();
                }
            }

            // Register into parent dispatch tables via the deferred WhenProtocolTableRegistered
            // mechanism: fires immediately if the parent table already exists, otherwise queued
            // until that table is registered later in the registration phase.
            // The tableId is captured here so that the composite (tableId, key) lookup in
            // _FindPduByDispatchKey can distinguish keys from different parent protocols.
            foreach (SignalPduRegistration reg in pdu.RegisterAt)
            {
                ulong key = reg.Key;
                uint pduId = pdu.PduId;
                builder.WhenProtocolTableRegistered(reg.Table, tableId =>
                {
                    builder.RegisterParserInU64Table(tableId, key, protocolId);
                    _BuildingTableKeyToPduId!.TryAdd((tableId, key), pduId);
                });
            }
        }

        _PduDefinitions = defs.ToFrozenDictionary();
    }

    /// <summary>
    /// Freezes the dispatch key accumulator built during <see cref="_RegisterFieldsCustom"/>
    /// into <see cref="_DispatchKeyToPduId"/> and releases the builder memory.
    /// Called once after all <see cref="IStackBuilder.WhenProtocolTableRegistered"/> callbacks
    /// have fired, ensuring every <see cref="ProtocolTableId"/> is known before freezing.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _DispatchKeyToPduId = _BuildingTableKeyToPduId?.ToFrozenDictionary()
            ?? FrozenDictionary<(ProtocolTableId, ulong), uint>.Empty;
        _BuildingTableKeyToPduId = null;
    }

    /// <summary>
    /// Parses a Signal PDU payload using signal definitions from configuration.
    /// The PDU ID is determined by looking up the dispatch key from the parent protocol.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        // Primary: resolve PDU definition by parent's dispatch key (CAN ID or PDU Transport ID)
        SignalPduDefinition? matchedPdu = _FindPduByDispatchKey(in context);

        // Fallback: match by byte length heuristic if dispatch key lookup failed
        matchedPdu ??= _FindMatchingPdu(data.Length);

        if (matchedPdu is null)
        {
            return 0; // No matching PDU — let parent handle as raw data
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Signal_pduGroupId);

        ReadOnlySpan<byte> span = data.Span;

        // Protocol container
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, FieldValue.None,
            ZA.Lazy("Signal PDU: ", matchedPdu.Name));

        parentField.SetPacketInfo(ZA.Lazy("Signal PDU: ", matchedPdu.Name));

        // PDU ID
        container.Append(_PduIdFieldId, FieldValue.NewU64(matchedPdu.PduId));

        // PDU Name
        container.Append(_NameFieldId, FieldValue.NewString(matchedPdu.Name));

        // Decode static signals (always present)
        foreach (SignalDefinition signal in matchedPdu.Signals)
        {
            _DecodeAndAppendSignal(in container, span, signal, in context);
        }

        // Multiplexer handling
        if (matchedPdu.MuxSignal is not null && matchedPdu.MuxGroups.Length > 0)
        {
            context.RecordGroupPresence(_Signal_pduMuxGroupId);
            ulong muxValue = SignalDecoder.ExtractMuxValue(span, matchedPdu.MuxSignal);

            MutField muxField = container.AppendWithCustomText(
                _MuxFieldId, FieldValue.None,
                ZA.Lazy("Multiplexer: ", matchedPdu.MuxSignal.Name, " = ", muxValue));

            muxField.Append(_MuxValueFieldId, FieldValue.NewU64(muxValue));

            // Find and decode signals for the current mux value
            foreach (MuxGroup group in matchedPdu.MuxGroups)
            {
                if (group.MuxValue == muxValue)
                {
                    foreach (SignalDefinition signal in group.Signals)
                    {
                        _DecodeAndAppendSignal(in muxField, span, signal, in context);
                    }
                    break;
                }
            }
        }

        return data.Length;
    }

    /// <summary>
    /// Decodes a single signal and appends it to the parent field.
    /// </summary>
    private void _DecodeAndAppendSignal(in MutField parent, ReadOnlySpan<byte> data, SignalDefinition signal, in ParseContext context)
    {
        double physicalValue = SignalDecoder.DecodeSignal(data, signal);
        ulong rawValue = SignalDecoder.ExtractRaw(data, signal);

        // Check for value name mapping using numeric key to avoid .ToString() allocation
        string? valueName = null;
        signal.NumericValueNames?.TryGetValue(rawValue, out valueName);

        // Build display text: "SignalName: 123.45 unit" or "SignalName: On (1)"
        // Use Formatted<double> to avoid intermediate string allocation from ToString("F2")
        Formatted<double> formattedValue = new(physicalValue, "F2", System.Globalization.CultureInfo.InvariantCulture);
        LazyString displayText;
        if (valueName is not null)
        {
            displayText = ZA.Lazy(signal.Name, ": ", valueName, " (", rawValue, ")");
        }
        else if (!string.IsNullOrEmpty(signal.Unit))
        {
            displayText = ZA.Lazy(
                signal.Name, ": ",
                formattedValue,
                " ", signal.Unit);
        }
        else
        {
            displayText = ZA.Lazy(
                signal.Name, ": ",
                formattedValue);
        }

        MutField signalField = parent.AppendWithCustomText(
            _SignalFieldId, FieldValue.None, displayText);

        signalField.Append(_SignalRawFieldId, FieldValue.NewU64(rawValue));
    }

    /// <summary>
    /// Resolves the PDU definition by reading the dispatch context from <paramref name="context"/>.
    /// The context was set by the parent protocol's <c>TryCallNextProtocolU64</c> call and carries
    /// both the dispatch table ID and the numeric key, enabling unambiguous lookup even when
    /// different parent protocols (e.g., CAN and FlexRay) share the same numeric key value.
    /// Returns <see langword="null"/> when no dispatch context is present or the key is not found.
    /// </summary>
    private SignalPduDefinition? _FindPduByDispatchKey(in ParseContext context)
    {
        DispatchContext dispatch = context.Dispatch;
        if (!dispatch.TryGetU64(out ulong key))
        {
            return null;
        }

        if (_DispatchKeyToPduId.TryGetValue((dispatch.TableId, key), out uint pduId)
            && _PduDefinitions.TryGetValue(pduId, out SignalPduDefinition? pdu))
        {
            return pdu;
        }

        return null;
    }

    private SignalPduDefinition? _FindMatchingPdu(int dataLength)
    {
        if (_PduDefinitions.Count == 0)
        {
            return null;
        }

        // If only one PDU is configured, use it directly
        if (_PduDefinitions.Count == 1)
        {
            foreach (SignalPduDefinition pdu in _PduDefinitions.Values)
            {
                return pdu;
            }
        }

        // Try to find by matching byte length
        foreach (SignalPduDefinition pdu in _PduDefinitions.Values)
        {
            if (pdu.ByteLength == dataLength)
            {
                return pdu;
            }
        }

        // Fallback: return first PDU that fits within the data
        foreach (SignalPduDefinition pdu in _PduDefinitions.Values)
        {
            if (pdu.ByteLength <= dataLength)
            {
                return pdu;
            }
        }

        return null;
    }
    #endregion
}
