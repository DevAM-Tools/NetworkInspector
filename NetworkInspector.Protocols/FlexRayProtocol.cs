// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// FlexRay protocol parser (ISO 17458-2) for LINKTYPE_FLEXRAY (link type 210).
/// Dispatches the frame payload to sub-protocols (e.g. Signal Message) via the <c>flexray.id</c>
/// dispatch table, keyed by the 11-bit slot number, channel (bit 11 = Channel B), and
/// 6-bit cycle count (bits [17:12]). See <see cref="FlexRayLinkTypeFrame.EncodeDispatchKey"/>.
/// <para>LINKTYPE_FLEXRAY capture format (per tcpdump.org specification):</para>
/// <code>
/// Byte 0:     Measurement Header ([7] CH, [6:0] Type Index)
/// Byte 1:     Error Flags ([4] FCRCERR, [3] HCRCERR, [2] FESERR, [1] CODERR, [0] TSSVIOL)
/// Bytes 2-6:  FlexRay Frame Header (ISO 17458-2 Section 8)
///   Byte 2:   [7] Reserved | [6] PPI | [5] NFI | [4] SFI | [3] STFI | [2:0] FID[10:8]
///   Byte 3:   [7:0] FID[7:0]
///   Byte 4:   [7:1] Payload Length (7 bits, in 16-bit words) | [0] HCRC[10]
///   Byte 5:   [7:0] HCRC[9:2]
///   Byte 6:   [7:6] HCRC[1:0] | [5:0] Cycle Count (6 bits)
/// Bytes 7+:   Payload data (0-254 bytes)
/// </code>
/// <para>Frame CRC is NOT included (only FCRCERR flag in Error Flags).</para>
/// <para>Field tree structure:</para>
/// <code>
/// flexray: FlexRay, Slot: 42, Cycle: 3
/// ├── flexray.channel: Channel A
/// ├── flexray.frame_id: 42
/// ├── flexray.payload_length: 32 bytes
/// ├── flexray.cycle: 3
/// ├── flexray.flags: [NFI]
/// │   ├── flexray.nfi: Not Null
/// │   ├── flexray.sfi: Not set
/// │   ├── flexray.stfi: Not set
/// │   └── flexray.ppi: Not set
/// ├── flexray.hcrc: 0x0000
/// ├── flexray.err_flags: [None]
/// │   ├── flexray.fcrc_err: Not set
/// │   ├── flexray.hcrc_err: Not set
/// │   ├── flexray.fes_err: Not set
/// │   ├── flexray.cod_err: Not set
/// │   └── flexray.tss_viol: Not set
/// ├── flexray.data: (32 bytes)
/// └── signal_message: ...                     [optional, when registered on flexray.id]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("flexray", "FlexRay", Description = "FlexRay (ISO 17458)")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, LinkTypeKey)]
public sealed partial class FlexRayProtocol : IProtocol
{
    #region Constants

    /// <summary>LinkType for DLT_FLEXRAY = 210.</summary>
    public const ulong LinkTypeKey = 210;

    /// <summary>
    /// Minimum size: 1 byte measurement header + 1 byte error flags + 5 bytes frame header = 7.
    /// </summary>
    private const int _MinHeaderSize = 7;

    /// <summary>Index group for always-present FlexRay fields.</summary>
    private const string _FlexRayIndexGroup = "flexray";

    /// <summary>Index group for error flag fields (always present but separate for filtering).</summary>
    private const string _FlexRayErrorGroup = "flexray.err";

    /// <summary>
    /// Dispatch-table name for sub-protocol lookup by FlexRay slot, channel, and cycle.
    /// Key encoding: bits [10:0] = Frame ID; bit 11 = Channel B; bits [17:12] = Cycle.
    /// </summary>
    /// <remarks>
    /// Example: Channel A, slot 42, cycle 3 →
    /// <see cref="FlexRayLinkTypeFrame.EncodeDispatchKey"/>(42, false, 3).
    /// </remarks>
    public const string IdTableName = "flexray.id";

    /// <summary>
    /// Bit 11 of the dispatch key signals Channel B. Delegates to
    /// <see cref="FlexRayLinkTypeFrame.ChannelBKeyBit"/>.
    /// </summary>
    public const ulong ChannelBKeyBit = FlexRayLinkTypeFrame.ChannelBKeyBit;

    /// <summary>
    /// Bit shift for the cycle count in a <c>flexray.id</c> dispatch key.
    /// Delegates to <see cref="FlexRayLinkTypeFrame.CycleKeyShift"/>.
    /// </summary>
    public const int CycleKeyShift = FlexRayLinkTypeFrame.CycleKeyShift;

    #endregion

    #region Measurement header bit masks

    /// <summary>Bit 7 of measurement header: channel (0=A, 1=B).</summary>
    private const byte _ChannelBitMask = 0x80;

    /// <summary>Bits 6-0 of measurement header: type index.</summary>
    private const byte _TypeIndexMask = 0x7F;

    /// <summary>Type index value for a FlexRay frame.</summary>
    private const byte _TypeIndexFrame = 0x01;

    #endregion

    #region Error flag bit masks (byte 1)

    private const byte _FcrcErrMask = 0x10;
    private const byte _HcrcErrMask = 0x08;
    private const byte _FesErrMask = 0x04;
    private const byte _CodErrMask = 0x02;
    private const byte _TssViolMask = 0x01;

    #endregion

    #region Frame header indicator bit masks (byte 2)

    /// <summary>Bit 6: Payload Preamble Indicator.</summary>
    private const byte _PpiBitMask = 0x40;

    /// <summary>Bit 5: Null Frame Indicator (1 = NOT null frame).</summary>
    private const byte _NfiBitMask = 0x20;

    /// <summary>Bit 4: Sync Frame Indicator.</summary>
    private const byte _SfiBitMask = 0x10;

    /// <summary>Bit 3: Startup Frame Indicator.</summary>
    private const byte _StfiBitMask = 0x08;

    /// <summary>Bits 2-0: high 3 bits of the 11-bit Frame ID.</summary>
    private const byte _FrameIdHighMask = 0x07;

    #endregion

    #region Protocol container

    [BytesField("flexray", "FlexRay", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Measurement header fields

    [StringField("flexray.channel", "Channel", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _ChannelFieldId;

    #endregion

    #region Frame header fields

    [U64Field("flexray.frame_id", "Frame ID (Slot)", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _FrameIdFieldId;

    [U64Field("flexray.payload_length", "Payload Length", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _PayloadLengthFieldId;

    [U64Field("flexray.cycle", "Cycle Count", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _CycleFieldId;

    [U64Field("flexray.hcrc", "Header CRC", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _HeaderCrcFieldId;

    #endregion

    #region Frame header indicator fields

    [NoneField("flexray.flags", "Flags", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _FlagsFieldId;

    [BoolField("flexray.nfi", "Null Frame Indicator", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _NfiFieldId;

    [BoolField("flexray.sfi", "Sync Frame Indicator", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _SfiFieldId;

    [BoolField("flexray.stfi", "Startup Frame Indicator", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _StfiFieldId;

    [BoolField("flexray.ppi", "Payload Preamble Indicator", IndexGroup = _FlexRayIndexGroup)]
    private FieldId _PpiFieldId;

    #endregion

    #region Error flag fields

    [NoneField("flexray.err_flags", "Error Flags", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _ErrFlagsFieldId;

    [BoolField("flexray.fcrc_err", "Frame CRC Error", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _FcrcErrFieldId;

    [BoolField("flexray.hcrc_err", "Header CRC Error", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _HcrcErrFieldId;

    [BoolField("flexray.fes_err", "Frame End Sequence Error", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _FesErrFieldId;

    [BoolField("flexray.cod_err", "Coding Error", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _CodErrFieldId;

    [BoolField("flexray.tss_viol", "TSS Violation", IndexGroup = _FlexRayErrorGroup)]
    private FieldId _TssViolFieldId;

    #endregion

    #region Payload

    /// <summary>Dispatch table for sub-protocols keyed by slot, channel, and cycle.</summary>
    [ProtocolTableU64(IdTableName, "FlexRay Frame ID + Channel + Cycle")]
    private ProtocolTableId _IdTableId;

    [BytesField("flexray.data", "Data", IndexGroup = "flexray.data")]
    private FieldId _DataFieldId;

    /// <summary>
    /// Parses a FlexRay frame in LINKTYPE_FLEXRAY format.
    /// Leaf protocol — no lazy population, direct Append().
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_FlexrayGroupId);
        context.RecordGroupPresence(_FlexrayErrGroupId);

    #endregion

        #region Measurement Header (byte 0)
        byte measurementHeader = span[0];
        // Bit 7: Channel (0 = Channel A, 1 = Channel B)
        bool isChannelB = (measurementHeader & _ChannelBitMask) != 0;

        // Bits 6-0: Type Index (0x01 = standard data frame).
        // Symbol frames and other non-data type indices do not carry a full 5-byte
        // FlexRay frame header or payload. Parsing them as data frames would
        // mis-decode garbage bytes.
        byte typeIndex = (byte)(measurementHeader & _TypeIndexMask);

        #endregion

        #region Error Flags (byte 1)
        byte errorFlags = span[1];
        bool fcrcErr = (errorFlags & _FcrcErrMask) != 0;
        bool hcrcErr = (errorFlags & _HcrcErrMask) != 0;
        bool fesErr = (errorFlags & _FesErrMask) != 0;
        bool codErr = (errorFlags & _CodErrMask) != 0;
        bool tssViol = (errorFlags & _TssViolMask) != 0;

        #endregion

        // Symbol and event frames: only the 2-byte measurement/error prefix is meaningful.
        // Skip the full frame-header decode to avoid mis-interpreting non-data bytes.
        if (typeIndex != _TypeIndexFrame)
        {
            MutField symbolContainer = parentField.AppendWithCustomText(
                _ProtocolFieldId,
                FieldValue.NewBytes(data[..2]),
                ZA.Lazy("FlexRay Symbol, Channel: ", isChannelB ? "B" : "A"));

            symbolContainer.Append(_ChannelFieldId, FieldValue.NewString(isChannelB ? "Channel B" : "Channel A"));

            // Error flags container — precomputed display text lists active error flag abbreviations.
            MutField symbolErrFlags = symbolContainer.AppendWithCustomText(
                _ErrFlagsFieldId, FieldValue.None,
                FlexRayFlagsFormatter.FormatErrors(fcrcErr, hcrcErr, fesErr, codErr, tssViol));
            symbolErrFlags.AppendWithCustomText(_FcrcErrFieldId, FieldValue.NewBool(fcrcErr), fcrcErr ? "Error" : "No error");
            symbolErrFlags.AppendWithCustomText(_HcrcErrFieldId, FieldValue.NewBool(hcrcErr), hcrcErr ? "Error" : "No error");
            symbolErrFlags.AppendWithCustomText(_FesErrFieldId, FieldValue.NewBool(fesErr), fesErr ? "Error" : "No error");
            symbolErrFlags.AppendWithCustomText(_CodErrFieldId, FieldValue.NewBool(codErr), codErr ? "Error" : "No error");
            symbolErrFlags.AppendWithCustomText(_TssViolFieldId, FieldValue.NewBool(tssViol), tssViol ? "Violation" : "No violation");

            return 2;
        }

        #region FlexRay Frame Header (bytes 2-6, per ISO 17458-2 Section 8)
        byte headerByte0 = span[2];
        byte headerByte1 = span[3];

        // Indicator bits from byte 2
        bool ppi = (headerByte0 & _PpiBitMask) != 0;
        bool nfi = (headerByte0 & _NfiBitMask) != 0;  // 1 = NOT null frame
        bool sfi = (headerByte0 & _SfiBitMask) != 0;
        bool stfi = (headerByte0 & _StfiBitMask) != 0;

        // Frame ID: 11 bits (byte 2 bits 2-0 = high 3 bits, byte 3 = low 8 bits)
        ushort frameId = (ushort)(((headerByte0 & _FrameIdHighMask) << 8) | headerByte1);

        // Payload length: 7 bits from byte 4 bits 7-1 (in 16-bit words)
        int payloadWords = (span[4] >> 1) & 0x7F;
        int payloadSize = payloadWords * 2;   // convert to bytes

        // Header CRC: 11 bits spanning bytes 4-6
        // Byte 4 bit 0 = HCRC[10], byte 5 = HCRC[9:2], byte 6 bits 7-6 = HCRC[1:0]
        ushort headerCrc = (ushort)(
            ((span[4] & 0x01) << 10) |
            (span[5] << 2) |
            ((span[6] >> 6) & 0x03));

        // Cycle count: 6 bits from byte 6 bits 5-0
        byte cycle = (byte)(span[6] & 0x3F);

        // Total consumed: measurement header (2) + frame header (5) + payload
        int totalConsumed = Math.Min(_MinHeaderSize + payloadSize, data.Length);

        #endregion

        #region Build field tree

        // Container with summary text
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId,
            FieldValue.NewBytes(data[..totalConsumed]),
            ZA.Lazy("FlexRay, Slot: ", frameId, ", Cycle: ", cycle));

        // Channel
        container.Append(_ChannelFieldId, FieldValue.NewString(isChannelB ? "Channel B" : "Channel A"));

        // Frame ID (11-bit slot number)
        container.Append(_FrameIdFieldId, FieldValue.NewU64(frameId));

        // Payload length (display in bytes)
        container.AppendWithCustomText(_PayloadLengthFieldId,
            FieldValue.NewU64((ulong)payloadSize),
            ZA.Lazy(payloadSize, " bytes"));

        // Cycle count (6-bit)
        container.Append(_CycleFieldId, FieldValue.NewU64(cycle));

        // Indicator flags container — precomputed display text lists active indicator abbreviations.
        MutField flagsContainer = container.AppendWithCustomText(
            _FlagsFieldId, FieldValue.None,
            FlexRayFlagsFormatter.FormatIndicators(ppi, nfi, sfi, stfi));
        flagsContainer.AppendWithCustomText(_NfiFieldId,
            FieldValue.NewBool(nfi),
            nfi ? "Not Null" : "Null Frame");
        flagsContainer.AppendWithCustomText(_SfiFieldId,
            FieldValue.NewBool(sfi),
            sfi ? "Sync Frame" : "Not set");
        flagsContainer.AppendWithCustomText(_StfiFieldId,
            FieldValue.NewBool(stfi),
            stfi ? "Startup Frame" : "Not set");
        flagsContainer.AppendWithCustomText(_PpiFieldId,
            FieldValue.NewBool(ppi),
            ppi ? "Set" : "Not set");

        // Header CRC (11-bit)
        container.Append(_HeaderCrcFieldId, FieldValue.NewU64(headerCrc));

        // Error flags container — precomputed display text lists active error flag abbreviations.
        MutField errFlagsContainer = container.AppendWithCustomText(
            _ErrFlagsFieldId, FieldValue.None,
            FlexRayFlagsFormatter.FormatErrors(fcrcErr, hcrcErr, fesErr, codErr, tssViol));
        errFlagsContainer.AppendWithCustomText(_FcrcErrFieldId,
            FieldValue.NewBool(fcrcErr),
            fcrcErr ? "Error" : "No error");
        errFlagsContainer.AppendWithCustomText(_HcrcErrFieldId,
            FieldValue.NewBool(hcrcErr),
            hcrcErr ? "Error" : "No error");
        errFlagsContainer.AppendWithCustomText(_FesErrFieldId,
            FieldValue.NewBool(fesErr),
            fesErr ? "Error" : "No error");
        errFlagsContainer.AppendWithCustomText(_CodErrFieldId,
            FieldValue.NewBool(codErr),
            codErr ? "Error" : "No error");
        errFlagsContainer.AppendWithCustomText(_TssViolFieldId,
            FieldValue.NewBool(tssViol),
            tssViol ? "Violation" : "No violation");

        // Payload data (optional); dispatch to sub-protocols (e.g. Signal Message) when present.
        // Key encodes slot, channel, and cycle — the three identifiers of a FlexRay message.
        if (totalConsumed > _MinHeaderSize)
        {
            context.RecordGroupPresence(_FlexrayDataGroupId);
            ReadOnlyMemory<byte> payload = data[_MinHeaderSize..totalConsumed];
            container.Append(_DataFieldId, FieldValue.NewBytes(payload));

            ulong dispatchKey = FlexRayLinkTypeFrame.EncodeDispatchKey(frameId, isChannelB, cycle);
            ParseResult dispatchResult = container.TryCallNextProtocolU64(_IdTableId, dispatchKey, payload, in context);
            if (dispatchResult.TryPropagateError(out ParseResult error))
            {
                return error;
            }
        }

        return totalConsumed;
    }
        #endregion
}
