// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Controller Area Network (CAN) protocol parser (ISO 11898 / ISO 11898-1:2024).
/// Parses classic CAN, CAN FD, and CAN XL frames using the SocketCAN format (DLT 227).
/// The frame variant is determined by the XLF flag (0x80) at byte offset 4:
/// classic CAN and CAN FD never set bit 7; CAN XL always sets it.
/// Leaf protocol with few fields — no lazy population, direct <c>Append()</c>.
/// <para>SocketCAN classic/FD header format (8 bytes minimum):</para>
/// <code>
/// Bytes 0-3:  CAN ID (29-bit ID + EFF/RTR/ERR flags in bits 31-29)
/// Byte  4:    Data Length Code (DLC)
/// Byte  5:    Flags (BRS=0x01, ESI=0x02, FDF=0x04 for CAN FD — Linux SocketCAN spec)
/// Bytes 6-7:  Reserved
/// Bytes 8+:   Data (0-8 classic, 0-64 CAN FD)
/// </code>
/// <para>SocketCAN CAN XL header format (12 bytes):</para>
/// <code>
/// Bytes  0-3:  Priority / VCID (32-bit BE; bits 0-10 = priority, bits 16-23 = VCID)
/// Byte   4:    Flags (XLF=0x80 always set, SEC=0x01, RRS=0x02)
/// Byte   5:    SDU Type
/// Bytes  6-7:  Payload Length (LE u16, 1-2048)
/// Bytes  8-11: Acceptance Field (LE u32)
/// Bytes 12+:   Data (1-2048 bytes)
/// </code>
/// <para>Field tree structure (classic / FD):</para>
/// <code>
/// can: Controller Area Network
/// ├── can.id: 0x123
/// ├── can.flags
/// │   ├── can.flags.xtd: false (Standard 11-bit)
/// │   ├── can.flags.rtr: false
/// │   ├── can.flags.err: false
/// │   ├── can.flags.fd: true                    [CAN FD only]
/// │   ├── can.flags.brs: true                   [CAN FD only]
/// │   └── can.flags.esi: false                  [CAN FD only]
/// ├── can.len: 8
/// └── can.data: (8 bytes)
/// </code>
/// <para>Field tree structure (CAN XL):</para>
/// <code>
/// canxl: CAN XL, Priority: 5, VCID: 0x1A, Length: 128
/// ├── canxl.priority: 5
/// ├── canxl.vcid: 0x1A
/// ├── canxl.flags
/// │   ├── canxl.flags.xlf: true (always set)
/// │   ├── canxl.flags.sec: false
/// │   └── canxl.flags.rrs: false
/// ├── canxl.sdu_type: 0x03
/// ├── canxl.len: 128
/// ├── canxl.acceptance_field: 0x12345678
/// └── canxl.data: (128 bytes)                  [conditional, if len > 0]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("can", "Controller Area Network", Description = "CAN (ISO 11898)")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, LinkTypeSocketCAN)]
public sealed partial class CanProtocol : IProtocol
{
    #region Constants

    /// <summary>LinkType for SocketCAN (DLT_CAN_SOCKETCAN = 227).</summary>
    public const ulong LinkTypeSocketCAN = 227;

    /// <summary>Minimum SocketCAN classic/FD header size in bytes.</summary>
    private const int _MinHeaderSize = 8;

    /// <summary>Index group for always-present CAN classic/FD fields.</summary>
    private const string _CanIndexGroup = "can";

    /// <summary>Protocol table name for CAN identifier dispatch (standard 11-bit and FD/FD+ 29-bit IDs).</summary>
    public const string IdTableName = "can.id";

    /// <summary>
    /// Protocol table name for CAN extended-frame identifier dispatch.
    /// Used for classic CAN extended (29-bit) frames and for CAN XL acceptance-field (32-bit) dispatch.
    /// Sub-protocols that need to distinguish extended from standard IDs should register here
    /// instead of (or in addition to) <see cref="IdTableName"/>.
    /// </summary>
    public const string ExtendedIdTableName = "can.extended_id";

    // Bit masks for CAN ID field (upper 3 bits of the 32-bit CAN ID)
    private const uint _EFF_FLAG = 0x80000000; // Extended Frame Format
    private const uint _RTR_FLAG = 0x40000000; // Remote Transmission Request
    private const uint _ERR_FLAG = 0x20000000; // Error frame
    private const uint _CAN_ID_MASK_STD = 0x7FF; // 11-bit standard ID
    private const uint _CAN_ID_MASK_EXT = 0x1FFFFFFF; // 29-bit extended ID

    // CAN FD flag bits (byte 5) — Linux SocketCAN spec / Wireshark canonical layout.
    private const byte _BRS_FLAG = 0x01; // Bit Rate Switch
    private const byte _ESI_FLAG = 0x02; // Error State Indicator
    private const byte _FDF_FLAG = 0x04; // FD Frame

    // CAN XL discriminator (byte 4 bit 7) — shared discriminator used by both parse paths.
    // Classic CAN DLC (0-8) and CAN FD DLC (0-15) never set bit 7; CAN XL always sets it.
    private const byte _XLF_FLAG = 0x80; // CAN XL Frame indicator

    // CAN XL header layout
    /// <summary>CAN XL header size in bytes (before data payload).</summary>
    private const int _XlHeaderSize = 12;

    /// <summary>Maximum CAN XL payload length in bytes (ISO 11898-1:2024).</summary>
    private const int _XlMaxPayloadLength = 2048;

    // CAN XL flag bits (byte 4)
    private const byte _XlSEC_FLAG = 0x01; // Simple Extended Content
    private const byte _XlRRS_FLAG = 0x02; // Remote Request Substitution

    // CAN XL priority/VCID field masks (32-bit BE word at bytes 0-3)
    private const uint _XlPRIORITY_MASK = 0x7FF; // bits 0-10: 11-bit priority
    private const int _XlVCID_OFFSET = 16;        // bits 16-23: 8-bit VCID
    private const uint _XlVCID_VAL_MASK = 0xFF;   // mask after shifting

    /// <summary>Index group for always-present CAN XL fields.</summary>
    private const string _CanXlIndexGroup = "canxl";

    /// <summary>Index group for the CAN XL data payload field.</summary>
    private const string _CanXlDataIndexGroup = "canxl.data";

    #endregion

    #region Protocol container

    [BytesField("can", "Controller Area Network", IndexGroup = _CanIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch tables

    /// <summary>
    /// Dispatch table for standard 11-bit CAN IDs, CAN FD 29-bit IDs, and CAN XL priority (11-bit).
    /// Sub-protocols (e.g., Signal PDU) register here to be invoked by CAN ID or CAN XL priority.
    /// </summary>
    [ProtocolTableU64(IdTableName, "CAN Identifier")]
    private ProtocolTableId _IdTableId;

    /// <summary>
    /// Dispatch table for extended-frame CAN IDs (29-bit) and CAN XL acceptance-field (32-bit).
    /// Enables protocol registration specifically on the extended or application-level identifier,
    /// independent of the arbitration priority.
    /// </summary>
    [ProtocolTableU64(ExtendedIdTableName, "CAN Extended Identifier")]
    private ProtocolTableId _ExtendedIdTableId;

    #endregion

    #region Fields (always present)

    [U64Field("can.id", "Identifier", IndexGroup = _CanIndexGroup)]
    private FieldId _IdFieldId;

    [NoneField("can.flags", "Flags", IndexGroup = _CanIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("can.flags.xtd", "Extended Frame Format", IndexGroup = _CanIndexGroup)]
    private FieldId _FlagsXtdFieldId;

    [BoolField("can.flags.rtr", "Remote Transmission Request", IndexGroup = _CanIndexGroup)]
    private FieldId _FlagsRtrFieldId;

    [BoolField("can.flags.err", "Error Frame", IndexGroup = _CanIndexGroup)]
    private FieldId _FlagsErrFieldId;

    [U64Field("can.len", "Data Length Code", IndexGroup = _CanIndexGroup)]
    private FieldId _LenFieldId;

    #endregion

    #region CAN FD fields (conditional)

    [BoolField("can.flags.fd", "FD Frame", IndexGroup = "can.fd")]
    private FieldId _FlagsFdFieldId;

    [BoolField("can.flags.brs", "Bit Rate Switch", IndexGroup = "can.fd")]
    private FieldId _FlagsBrsFieldId;

    [BoolField("can.flags.esi", "Error State Indicator", IndexGroup = "can.fd")]
    private FieldId _FlagsEsiFieldId;

    #endregion

    #region Data (conditional — present when DLC > 0)

    [BytesField("can.data", "Data", IndexGroup = "can.data")]
    private FieldId _DataFieldId;

    #endregion

    #region Message name field (conditional — present when config provides a name)

    [StringField("can.name", "Message Name", IndexGroup = "can.name")]
    private FieldId _NameFieldId;

    #endregion

    #region CAN XL protocol container

    [BytesField("canxl", "CAN XL", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlProtocolFieldId;

    #endregion

    #region CAN XL fields (always present when frame is CAN XL)

    [U64Field("canxl.priority", "Priority", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlPriorityFieldId;

    [U64Field("canxl.vcid", "Virtual CAN Network ID", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlVcidFieldId;

    [NoneField("canxl.flags", "Flags", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlFlagsFieldId;

    [BoolField("canxl.flags.xlf", "CAN XL Frame", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlFlagsXlfFieldId;

    [BoolField("canxl.flags.sec", "Simple Extended Content", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlFlagsSecFieldId;

    [BoolField("canxl.flags.rrs", "Remote Request Substitution", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlFlagsRrsFieldId;

    [U64Field("canxl.sdu_type", "SDU Type", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlSduTypeFieldId;

    [U64Field("canxl.len", "Payload Length", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlLenFieldId;

    [U64Field("canxl.acceptance_field", "Acceptance Field", IndexGroup = _CanXlIndexGroup)]
    private FieldId _CanxlAcceptanceFieldId;

    #endregion

    #region CAN XL data (conditional — present when payload length > 0)

    [BytesField("canxl.data", "Data", IndexGroup = _CanXlDataIndexGroup)]
    private FieldId _CanxlDataFieldId;

    #endregion

    #region Settings

    /// <summary>Path to JSON config file for CAN message name resolution.</summary>
    [StringSetting("can.config_file", "Configuration File", "can",
        Description = "Path to JSON file mapping CAN IDs to message names")]
    private string? _ConfigFile;

    #endregion

    #region Runtime state — populated in _RegisterFieldsCustom

    /// <summary>Message name lookup: CAN ID → display name. Built from config file.</summary>
    private FrozenDictionary<uint, string> _MessageNames = FrozenDictionary<uint, string>.Empty;

    /// <summary>
    /// Warning produced during registration if the config file referenced by
    /// <c>can.config_file</c> could not be loaded; <see langword="null"/> when loading
    /// succeeded or no path was configured.
    /// </summary>
    public SettingsLoadWarning? ConfigLoadWarning
    {
        get; private set;
    }

    /// <summary>
    /// Loads CAN configuration and builds the message name lookup dictionary.
    /// Runs at the end of RegisterFields, after settings have been loaded into the
    /// <c>_ConfigFile</c> backing field. Building the lookup here keeps all CAN-specific
    /// configuration state inside the registration phase, so the resulting <c>Stack</c> stays
    /// truly immutable.
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        builder.Settings.TryLoadReferencedJsonConfig(
            "can.config_file", CanConfigContext.Default.CanConfig,
            out CanConfig? config, out SettingsLoadWarning? configLoadWarning);
        ConfigLoadWarning = configLoadWarning;

        if (config?.Messages is { Length: > 0 })
        {
            Dictionary<uint, string> names = new(config.Messages.Length);
            foreach (CanMessageEntry msg in config.Messages)
            {
                // Store with the raw CAN ID — the lookup uses the masked extracted ID
                names.TryAdd(msg.Id, msg.Name);
            }
            _MessageNames = names.ToFrozenDictionary();
        }
        else
        {
            _MessageNames = FrozenDictionary<uint, string>.Empty;
        }
    }

    /// <summary>
    /// Parses a CAN frame from SocketCAN format.
    /// No lazy population — all fields are appended directly.
    /// After appending all fields, dispatches payload to sub-protocols via can.id table.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // CAN XL detection: byte 4 bit 7 (_XLF_FLAG) distinguishes CAN XL from classic/FD.
        // Classic CAN DLC is 0-8 and CAN FD DLC is 0-15 — neither sets bit 7.
        if ((span[4] & _XLF_FLAG) != 0)
        {
            return _ParseCanXl(in parentField, data, in context);
        }

        // Parse 32-bit CAN ID with flags. LINKTYPE_CAN_SOCKETCAN (DLT 227) stores the
        // CAN-ID/flags word in network byte order (big-endian); see
        // https://www.tcpdump.org/linktypes/LINKTYPE_CAN_SOCKETCAN.html.
        uint rawId = BinaryPrimitives.ReadUInt32BigEndian(span);
        bool isExtended = (rawId & _EFF_FLAG) != 0;
        bool isRtr = (rawId & _RTR_FLAG) != 0;
        bool isError = (rawId & _ERR_FLAG) != 0;
        uint canId = isExtended ? (rawId & _CAN_ID_MASK_EXT) : (rawId & _CAN_ID_MASK_STD);

        // DLC and flags
        byte dlc = span[4];
        byte fdFlags = span[5];
        bool isFd = (fdFlags & _FDF_FLAG) != 0;

        // Extract FD-specific flag bits here so they are available for FormatFd below.
        bool brs = isFd && (fdFlags & _BRS_FLAG) != 0;
        bool esi = isFd && (fdFlags & _ESI_FLAG) != 0;

        // SocketCAN's struct can_frame.len / canfd_frame.len holds the actual
        // payload byte count (0..8 for classic, 0..64 for FD) — there is no
        // DLC encoding on the wire for either variant. Clamp defensively to
        // the protocol-allowed maximum.
        int dataLen = isFd ? Math.Min(dlc, (byte)64) : Math.Min(dlc, (byte)8);

        // Verify we have enough data
        int totalLen = _MinHeaderSize + dataLen;
        if (data.Length < totalLen)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)totalLen, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_CanGroupId);

        // Build summary
        // Use full 32-bit hex formatting for CAN IDs to support extended 29-bit identifiers
        LazyString summary = isFd
            ? ZA.Lazy("Controller Area Network FD, ID: 0x",
                new Hex8(canId),
                ", Length: ", dlc)
            : ZA.Lazy("Controller Area Network, ID: 0x",
                new Hex8(canId),
                ", Length: ", dlc);

        parentField.SetPacketInfo(isFd
            ? ZA.Lazy("CAN FD 0x", new Hex8(canId))
            : ZA.Lazy("CAN 0x", new Hex8(canId)));

        MutField canField = parentField.AppendWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data[..totalLen]), summary);

        // CAN ID
        LazyString idText = isExtended
            ? ZA.Lazy("0x", new Hex8(canId), " (Extended)")
            : ZA.Lazy("0x", new Hex3((ushort)canId), " (Standard)");
        canField.AppendWithCustomText(_IdFieldId, FieldValue.NewU64(canId), idText);

        // Message name from configuration
        if (_MessageNames.TryGetValue(canId, out string? messageName))
        {
            context.RecordGroupPresence(_CanNameGroupId);
            canField.Append(_NameFieldId, FieldValue.NewString(messageName));
        }

        // Flags container — precomputed display text lists active flag abbreviations.
        string flagsText = isFd
            ? CanFlagsFormatter.FormatFd(isExtended, isRtr, isError, brs, esi)
            : CanFlagsFormatter.FormatClassic(isExtended, isRtr, isError);
        MutField flagsField = canField.AppendWithCustomText(_FlagsFieldId, FieldValue.None, flagsText);
        flagsField.Append(_FlagsXtdFieldId, FieldValue.NewBool(isExtended));
        flagsField.Append(_FlagsRtrFieldId, FieldValue.NewBool(isRtr));
        flagsField.Append(_FlagsErrFieldId, FieldValue.NewBool(isError));

        // CAN FD specific flags
        if (isFd)
        {
            context.RecordGroupPresence(_CanFdGroupId);
            flagsField.Append(_FlagsFdFieldId, FieldValue.NewBool(true));
            flagsField.Append(_FlagsBrsFieldId, FieldValue.NewBool(brs));
            flagsField.Append(_FlagsEsiFieldId, FieldValue.NewBool(esi));
        }

        // DLC
        canField.Append(_LenFieldId, FieldValue.NewU64(dlc));

        // Data
        if (dataLen > 0)
        {
            context.RecordGroupPresence(_CanDataGroupId);
            canField.Append(_DataFieldId, FieldValue.NewBytes(data.Slice(_MinHeaderSize, dataLen)));

            // Dispatch payload to sub-protocols registered on can.id (e.g., Signal PDU).
            // Extended frames (29-bit IDs) are also dispatched via can.extended_id so that
            // sub-protocols can register on the more specific extended-ID table without
            // colliding with standard 11-bit IDs that share the same numeric value.
            ReadOnlyMemory<byte> payload = data.Slice(_MinHeaderSize, dataLen);
            ParseResult dispatchResult = canField.TryCallNextProtocolU64(
                _IdTableId, canId, payload, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }

            if (isExtended)
            {
                dispatchResult = canField.TryCallNextProtocolU64(
                    _ExtendedIdTableId, canId, payload, in context);
                if (dispatchResult.IsError)
                {
                    return dispatchResult;
                }
            }
        }

        return totalLen;
    }

    /// <summary>
    /// Parses a CAN XL frame from SocketCAN format (ISO 11898-1:2024).
    /// Called by <see cref="Parse"/> when the XLF flag (0x80) is set at byte offset 4.
    /// No lazy population — all fields are appended directly.
    /// </summary>
    /// <remarks>
    /// When the payload is non-empty, the frame is dispatched twice:
    /// <list type="bullet">
    ///   <item><description>
    ///     Via <c>can.id</c> using the 11-bit <c>canxl.priority</c> as the dispatch key — enabling
    ///     unified configuration with classic CAN standard-ID protocols.
    ///   </description></item>
    ///   <item><description>
    ///     Via <c>can.extended_id</c> using the 32-bit <c>canxl.acceptance_field</c> as the dispatch key —
    ///     allowing higher-layer protocols to bind to the application-level identifier.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="parentField">Parent field that receives the decoded CAN XL container and children.</param>
    /// <param name="data">Raw frame bytes starting at offset 0 (12-byte header + payload).</param>
    /// <param name="context">Owning stack; passed to sub-protocol dispatch.</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/>.</returns>
    private ParseResult _ParseCanXl(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Validate minimum header size
        if (data.Length < _XlHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _XlHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // Parse the 32-bit priority/VCID field.
        // LINKTYPE_CAN_SOCKETCAN (DLT 227) stores this word in big-endian (network) byte order;
        // see Wireshark packet-socketcan.c: "The priority/VCID field is big-endian in
        // LINKTYPE_CAN_SOCKETCAN captures, for historical reasons."
        // bits 0-10 = priority, bits 16-23 = VCID.
        uint rawPrio = BinaryPrimitives.ReadUInt32BigEndian(span);
        uint priority = rawPrio & _XlPRIORITY_MASK;
        uint vcid = (rawPrio >> _XlVCID_OFFSET) & _XlVCID_VAL_MASK;

        // Flags (byte 4 — XLF is always set; SEC and RRS are optional)
        byte flags = span[4];
        bool isSec = (flags & _XlSEC_FLAG) != 0;
        bool isRrs = (flags & _XlRRS_FLAG) != 0;

        // SDU type (byte 5)
        byte sduType = span[5];

        // Payload length (little-endian u16, bytes 6-7).
        // The spec (ISO 11898-1:2024) defines valid range 1-2048; a value of 0 is technically
        // a spec violation but accepted here to allow display of the other header fields.
        // canxl.len always reflects the raw wire value; effectivePayloadLength is additionally
        // clamped to _XlMaxPayloadLength (2048) to guard against malformed frames.
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);

        // Acceptance field (little-endian u32, bytes 8-11).
        // Application-level 32-bit identifier; used as the dispatch key for can.extended_id.
        uint acceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);

        // Clamp payload length to protocol maximum to guard against malformed frames.
        // effectivePayloadLength drives memory slicing; canxl.len records the raw wire value.
        int effectivePayloadLength = Math.Min((int)payloadLength, _XlMaxPayloadLength);

        // Verify we have enough data for header + payload
        int totalLen = _XlHeaderSize + effectivePayloadLength;
        if (data.Length < totalLen)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)totalLen, (ulong)data.Length);
        }

        // Record protocol and index group presence
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_CanxlGroupId);

        // Build summary text: "CAN XL, Priority: N, VCID: 0xNN, Length: N"
        LazyString summary = ZA.Lazy("CAN XL, Priority: ", priority,
            ", VCID: 0x", Helpers.DisplayTables.FormatHexU8((byte)vcid),
            ", Length: ", payloadLength);

        parentField.SetPacketInfo(ZA.Lazy(
            "CAN XL P:", priority, " VCID:0x", Helpers.DisplayTables.FormatHexU8((byte)vcid)));

        // Append protocol container with frame slice bytes
        MutField xlField = parentField.AppendWithCustomText(
            _CanxlProtocolFieldId, FieldValue.NewBytes(data[..totalLen]), summary);

        // Priority (11-bit, bits 0-10 of the priority/VCID word)
        xlField.Append(_CanxlPriorityFieldId, FieldValue.NewU64(priority));

        // Virtual CAN Network ID (8-bit, bits 16-23 of the priority/VCID word)
        xlField.AppendWithCustomText(_CanxlVcidFieldId, FieldValue.NewU64(vcid),
            Helpers.DisplayTables.FormatHexU8((byte)vcid));

        // Flags container — XLF is structurally always true for CAN XL frames.
        // Precomputed display text lists "XLF" plus any active variable flags.
        MutField flagsField = xlField.AppendWithCustomText(
            _CanxlFlagsFieldId, FieldValue.None, CanFlagsFormatter.FormatXl(isSec, isRrs));
        flagsField.Append(_CanxlFlagsXlfFieldId, FieldValue.NewBool(true));
        flagsField.Append(_CanxlFlagsSecFieldId, FieldValue.NewBool(isSec));
        flagsField.Append(_CanxlFlagsRrsFieldId, FieldValue.NewBool(isRrs));

        // SDU Type (hex display)
        xlField.AppendWithCustomText(_CanxlSduTypeFieldId, FieldValue.NewU64(sduType),
            Helpers.DisplayTables.FormatHexU8(sduType));

        // Payload Length
        xlField.Append(_CanxlLenFieldId, FieldValue.NewU64(payloadLength));

        // Acceptance Field (hex display)
        xlField.AppendWithCustomText(_CanxlAcceptanceFieldId, FieldValue.NewU64(acceptanceField),
            ZA.Lazy("0x", new Hex8(acceptanceField)));

        // Data payload (conditional — present when payload length > 0).
        // Dispatches via can.id (key = priority) and can.extended_id (key = acceptanceField)
        // so higher-layer protocols can bind to either the arbitration priority or the
        // application-level acceptance field, mirroring the two dispatch tables of classic CAN.
        if (effectivePayloadLength > 0)
        {
            context.RecordGroupPresence(_CanxlDataGroupId);
            ReadOnlyMemory<byte> payload = data.Slice(_XlHeaderSize, effectivePayloadLength);
            xlField.Append(_CanxlDataFieldId, FieldValue.NewBytes(payload));

            // Dispatch via can.id using priority (11-bit, 0–2047) as the key.
            ParseResult dispatchResult = xlField.TryCallNextProtocolU64(_IdTableId, priority, payload, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }

            // Dispatch via can.extended_id using the acceptance field (32-bit) as the key.
            dispatchResult = xlField.TryCallNextProtocolU64(_ExtendedIdTableId, acceptanceField, payload, in context);
            if (dispatchResult.IsError)
            {
                return dispatchResult;
            }
        }

        return totalLen;
    }

    #endregion
}
