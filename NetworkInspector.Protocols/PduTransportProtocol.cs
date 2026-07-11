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
/// <para>Field tree structure:</para>
/// <code>
/// pdu_transport: PDU Transport
/// ├── [dispatched payload via pdu_transport.id table]   [eager, per PDU, when a sub-protocol matches]
/// ├── pdu_transport.payload: (16 bytes)                 [eager, per PDU, when no sub-protocol matches]
/// ├── pdu_transport.pdu: PDU: BrakeStatus (ID: 1)       [lazy descriptive metadata]
/// │   ├── pdu_transport.id: 1
/// │   ├── pdu_transport.length: 8
/// │   └── pdu_transport.name: "BrakeStatus"             [optional, from config]
/// ├── pdu_transport.pdu: PDU: EngineData (ID: 2)
/// │   ├── pdu_transport.id: 2
/// │   └── pdu_transport.length: 16
/// </code>
/// <para>
/// Sub-protocol dispatch and the raw-payload fallback are performed eagerly in <see cref="Parse"/>
/// (so dispatched sub-protocols record their index groups during the index phase); the descriptive
/// <c>pdu_transport.pdu</c> metadata tree is built lazily.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>_RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
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

    [U64Setting("pdu_transport.id_field_size", "ID Field Size (bytes)", "pdu_transport", Default = 4)]
    private ulong _IdFieldSize;

    [U64Setting("pdu_transport.length_field_size", "Length Field Size (bytes)", "pdu_transport", Default = 4)]
    private ulong _LengthFieldSize;

    /// <summary>
    /// When non-zero (1–65535), registers this parser on <see cref="UdpProtocol.PortTableName"/> under that
    /// port key so UDP tries PDU Transport before falling back to other sub-dissectors. Zero (default)
    /// means no UDP auto-dispatch — matching Wireshark setups that rely solely on Decode-As /
    /// per-capture heuristic binding.
    /// </summary>
    [U64Setting("pdu_transport.udp_dispatch_port", "UDP dispatch port", "pdu_transport", Default = 0)]
    private ulong _UdpDispatchPort;

    #endregion

    #region Runtime state

    /// <summary>PDU ID → display name lookup, built from config.</summary>
    private FrozenDictionary<uint, string> _NameLookup = FrozenDictionary<uint, string>.Empty;

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    /// <summary>
    /// Warning produced during registration if the config file referenced by
    /// <c>pdu_transport.config_file</c> could not be loaded; <see langword="null"/> when loading
    /// succeeded or no path was configured.
    /// </summary>
    public SettingsLoadWarning? ConfigLoadWarning
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
    /// Initializes config-driven state at the end of RegisterFields:
    /// validates setting values, builds the PDU name lookup from the JSON config file,
    /// and pre-allocates the lazy populator delegate. Performing all of this during the
    /// registration phase keeps the resulting <c>Stack</c> immutable.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _Populator = _PopulatePduTransportFields;

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

        // Load configuration for name resolution
        builder.Settings.TryLoadReferencedJsonConfig(
            "pdu_transport.config_file", PduTransportConfigContext.Default.PduTransportConfig,
            out PduTransportConfig? config, out SettingsLoadWarning? configLoadWarning);
        ConfigLoadWarning = configLoadWarning;

        if (config?.Pdus is { Length: > 0 })
        {
            Dictionary<uint, string> names = new(config.Pdus.Length);
            foreach (PduTransportPduEntry entry in config.Pdus)
            {
                names.TryAdd(entry.Id, entry.Name);
            }
            _NameLookup = names.ToFrozenDictionary();
        }
        else
        {
            _NameLookup = FrozenDictionary<uint, string>.Empty;
        }

        if (_UdpDispatchPort is >= 1UL and <= ushort.MaxValue)
        {
            ulong portKey = _UdpDispatchPort;
            builder.WhenProtocolTableRegistered(UdpProtocol.PortTableName, table =>
                builder.RegisterParserInU64Table(table, portKey, protocolId));
        }
    }

    /// <summary>
    /// Parses PDU Transport datagrams containing one or more concatenated PDUs.
    /// <para>
    /// Sub-protocol dispatch is performed <b>eagerly</b> here (not in the lazy populator) so
    /// the dispatched sub-protocols record their index groups during the capture/index phase,
    /// when the real index-carrying <see cref="ParseContext"/> is available. The descriptive
    /// per-PDU field tree (<c>pdu_transport.pdu</c> / <c>id</c> / <c>length</c> / <c>name</c>)
    /// remains lazily built in <see cref="_PopulatePduTransportFields"/>.
    /// </para>
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        int headerSize = (int)_IdFieldSize + (int)_LengthFieldSize;
        if (data.Length < headerSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)headerSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Pdu_transportGroupId);

        parentField.SetPacketInfo(new LazyString("PDU Transport"));

        FieldValue containerValue = FieldValue.NewBytes(data);
        MutField container = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, containerValue, new LazyString("PDU Transport"), _Populator);

        // Eagerly walk the concatenated PDUs and dispatch each payload to its sub-protocol
        // with the real index-carrying context. Dispatched sub-protocols therefore record
        // their presence during the index phase (Q6: the index must be complete when the
        // packet is finalized). When no sub-protocol consumes a payload, the raw payload
        // field is appended eagerly so the dispatch decision is fully resolved here and the
        // lazy populator never needs to dispatch.
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

            // The lazy populator emits pdu_transport.name for every PDU whose ID resolves to a
            // configured display name. The index must mirror that decision, so perform the same
            // name lookup eagerly here and record the group when a name is present. This repeats
            // the lookup the populator performs later — the accepted cost of deferred field building.
            if (!string.IsNullOrEmpty(_NameLookup.GetValueOrDefault(pduId)))
            {
                context.RecordGroupPresence(_Pdu_transportNameGroupId);
            }

            // Clamp payload to available data before slicing (boundary validation).
            int actualPayload = Math.Min((int)payloadLength, span.Length - offset);
            if (actualPayload > 0)
            {
                ReadOnlyMemory<byte> payloadData = data.Slice(offset, actualPayload);

                ParseResult dispatchResult = container.TryCallNextProtocolU64(
                    _IdTableId, pduId, payloadData, in context);
                if (dispatchResult.IsError)
                {
                    return dispatchResult;
                }

                // If no sub-protocol consumed the payload, append raw bytes.
                if (!dispatchResult.IsSuccess || dispatchResult.Value == 0)
                {
                    context.RecordGroupPresence(_Pdu_transportPayloadGroupId);
                    container.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));
                }
            }

            offset += actualPayload;
        }

        return data.Length;
    }

    /// <summary>
    /// Lazily populates the descriptive PDU Transport field tree from stored data.
    /// Each PDU has: [id:id_field_size] [length:length_field_size] [payload:length].
    /// <para>
    /// This populator builds only the descriptive metadata (<c>pdu</c> / <c>id</c> /
    /// <c>length</c> / <c>name</c>); sub-protocol dispatch and the raw-payload fallback are
    /// performed eagerly in <see cref="Parse"/> so the index is complete at packet finalization.
    /// </para>
    /// </summary>
    private ParseResult _PopulatePduTransportFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> pduData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = pduData.Span;
        int idSize = (int)_IdFieldSize;
        int lenSize = (int)_LengthFieldSize;
        int headerSize = idSize + lenSize;
        int offset = 0;

        // Parse concatenated PDUs
        while (offset + headerSize <= span.Length)
        {
            // Read PDU ID (big-endian)
            uint pduId = _ReadBigEndianUint(span[offset..], idSize);
            offset += idSize;

            // Read payload length (big-endian)
            uint payloadLength = _ReadBigEndianUint(span[offset..], lenSize);
            offset += lenSize;

            // Clamp payload to available data
            int actualPayload = Math.Min((int)payloadLength, span.Length - offset);

            // Look up display name
            string? name = _NameLookup.GetValueOrDefault(pduId);
            bool hasName = !string.IsNullOrEmpty(name);

            // PDU summary
            LazyString pduSummary = hasName
                ? ZA.Lazy("PDU: ", name!, " (ID: ", pduId, ")")
                : ZA.Lazy("PDU (ID: ", pduId, ")");

            MutField pduField = container.AppendWithCustomText(
                _PduFieldId, FieldValue.None, pduSummary);

            // PDU ID
            pduField.Append(_IdFieldId, FieldValue.NewU64(pduId));

            // Length
            pduField.Append(_LengthFieldId, FieldValue.NewU64(payloadLength));

            // Name (from config)
            if (hasName)
            {
                pduField.Append(_NameFieldId, FieldValue.NewString(name!));
            }

            offset += actualPayload;
        }

        return 0;
    }

    /// <summary>
    /// Reads a big-endian unsigned integer of the given byte size (1, 2, or 4).
    /// </summary>
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
