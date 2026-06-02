// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// HTTP/2 protocol parser — frame-level only (RFC 7540).
/// Parses the 9-byte frame header and extracts type, flags, stream ID, and length.
/// Does not decode HPACK-compressed headers (Phase 1 approach).
/// <para>Field tree structure:</para>
/// <code>
/// http2: HTTP/2
/// └── http2.frame: HTTP/2 Frame
///     ├── http2.frame.length: 16384
///     ├── http2.frame.type: HEADERS (1)
///     ├── http2.frame.flags: 0x05
///     ├── http2.frame.stream_id: 1
///     └── http2.frame.payload: [bytes]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("http2", "HyperText Transfer Protocol 2", Description = "HTTP/2 (RFC 7540)")]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class Http2Protocol : IProtocol
{
    #region Constants

    /// <summary>TCP port for HTTP/2 over TLS (h2). Also used for direct TCP (h2c prior knowledge).</summary>
    public const ulong TcpPortKey = 8443;

    /// <summary>Index group for always-present HTTP/2 fields.</summary>
    private const string Http2IndexGroup = "http2";

    /// <summary>Index group for frame payload (conditional, only when payload length > 0).</summary>
    private const string Http2PayloadIndexGroup = "http2.payload";

    #endregion

    #region Protocol container

    [BytesField("http2", "HTTP/2", IndexGroup = Http2IndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Frame fields

    [NoneField("http2.frame", "HTTP/2 Frame", IndexGroup = Http2IndexGroup)]
    private FieldId _FrameFieldId;

    [U64Field("http2.frame.length", "Length", IndexGroup = Http2IndexGroup)]
    private FieldId _FrameLengthFieldId;

    [U64Field("http2.frame.type", "Type", IndexGroup = Http2IndexGroup)]
    private FieldId _FrameTypeFieldId;

    [U64Field("http2.frame.flags", "Flags", IndexGroup = Http2IndexGroup)]
    private FieldId _FrameFlagsFieldId;

    [U64Field("http2.frame.stream_id", "Stream Identifier", IndexGroup = Http2IndexGroup)]
    private FieldId _FrameStreamIdFieldId;

    [BytesField("http2.frame.payload", "Payload", IndexGroup = Http2PayloadIndexGroup)]
    private FieldId _FramePayloadFieldId;

    #endregion

    #region Per-type flag fields

    [BoolField("http2.frame.flags.end_stream", "END_STREAM", IndexGroup = "http2.flags")]
    private FieldId _FlagEndStreamFieldId;

    [BoolField("http2.frame.flags.ack", "ACK", IndexGroup = "http2.flags")]
    private FieldId _FlagAckFieldId;

    [BoolField("http2.frame.flags.end_headers", "END_HEADERS", IndexGroup = "http2.flags")]
    private FieldId _FlagEndHeadersFieldId;

    [BoolField("http2.frame.flags.padded", "PADDED", IndexGroup = "http2.flags")]
    private FieldId _FlagPaddedFieldId;

    [BoolField("http2.frame.flags.priority", "PRIORITY", IndexGroup = "http2.flags")]
    private FieldId _FlagPriorityFieldId;

    #endregion

    #region SETTINGS frame fields

    [NoneField("http2.settings", "Setting", IndexGroup = "http2.settings")]
    private FieldId _SettingsFieldId;

    [U64Field("http2.settings.id", "Identifier", IndexGroup = "http2.settings")]
    private FieldId _SettingsIdFieldId;

    [U64Field("http2.settings.value", "Value", IndexGroup = "http2.settings")]
    private FieldId _SettingsValueFieldId;

    #endregion

    #region GOAWAY frame fields

    [U64Field("http2.goaway.last_stream_id", "Last-Stream-ID", IndexGroup = "http2.goaway")]
    private FieldId _GoawayLastStreamIdFieldId;

    [U64Field("http2.goaway.error_code", "Error Code", IndexGroup = "http2.goaway")]
    private FieldId _GoawayErrorCodeFieldId;

    [BytesField("http2.goaway.debug_data", "Debug Data", IndexGroup = "http2.goaway")]
    private FieldId _GoawayDebugDataFieldId;

    #endregion

    #region PING frame fields

    [BytesField("http2.ping.opaque", "Opaque Data", IndexGroup = "http2.ping")]
    private FieldId _PingOpaqueFieldId;

    #endregion

    #region WINDOW_UPDATE frame fields

    [U64Field("http2.window_update.increment", "Window Size Increment", IndexGroup = "http2.window_update")]
    private FieldId _WindowUpdateIncrementFieldId;

    #endregion

    #region RST_STREAM frame fields

    [U64Field("http2.rst_stream.error_code", "Error Code", IndexGroup = "http2.rst_stream")]
    private FieldId _RstStreamErrorCodeFieldId;

    #endregion

    #region HPACK-decoded header fields

    [NoneField("http2.header", "Header", IndexGroup = "http2.header")]
    private FieldId _HeaderFieldId;

    [StringField("http2.header.name", "Name", IndexGroup = "http2.header")]
    private FieldId _HeaderNameFieldId;

    [StringField("http2.header.value", "Value", IndexGroup = "http2.header")]
    private FieldId _HeaderValueFieldId;

    // Pre-allocated populator
    private LazyPopulator _Populator = null!;

    partial void OnStartCustom(Stack stack) => _Populator = PopulateHttp2Fields;

    /// <summary>
    /// Parses HTTP/2 frames from the TCP segment payload. Uses lazy population.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < Http2FrameHeader.Size)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, Http2FrameHeader.Size, (ulong)data.Length);
        }

        if (!Http2FrameHeader.TryParse(data.Span, out Http2FrameHeader firstFrame))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, Http2FrameHeader.Size, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Http2GroupId);

        // Build summary from first frame
        string typeText = Http2DisplayTables.GetFrameTypeDisplayText(firstFrame.Type);

        LazyString summary = ZA.Lazy(
            "HTTP/2 ", typeText, ", Stream: ", firstFrame.StreamId, ", Length: ", firstFrame.Length);

        parentField.SetPacketInfo(ZA.Lazy("HTTP/2 ", typeText));

        // Eagerly scan every frame in the segment to record exactly the index groups whose fields
        // the lazy populator will emit. The relevant groups depend on each frame's type and payload
        // length (and, for HEADERS/CONTINUATION, on the decoded HPACK header count), and a segment
        // may carry several frames — so the decision requires walking all frames and mirroring the
        // populator's per-frame emission guards. This keeps the presence index content-consistent
        // with materialization and free of false positives, at the deliberate cost of repeating the
        // frame walk (and HPACK decode) that the populator performs lazily.
        DetectFrameGroups(
            data.Span,
            out bool hasFlags, out bool hasPayload, out bool hasSettings, out bool hasGoaway,
            out bool hasPing, out bool hasWindowUpdate, out bool hasRstStream, out bool hasHeader);

        if (hasFlags)
        {
            context.RecordGroupPresence(_Http2FlagsGroupId);
        }
        if (hasPayload)
        {
            context.RecordGroupPresence(_Http2PayloadGroupId);
        }
        if (hasSettings)
        {
            context.RecordGroupPresence(_Http2SettingsGroupId);
        }
        if (hasGoaway)
        {
            context.RecordGroupPresence(_Http2GoawayGroupId);
        }
        if (hasPing)
        {
            context.RecordGroupPresence(_Http2PingGroupId);
        }
        if (hasWindowUpdate)
        {
            context.RecordGroupPresence(_Http2Window_updateGroupId);
        }
        if (hasRstStream)
        {
            context.RecordGroupPresence(_Http2Rst_streamGroupId);
        }
        if (hasHeader)
        {
            context.RecordGroupPresence(_Http2HeaderGroupId);
        }

        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Eagerly walks every HTTP/2 frame in the segment and reports which optional index groups
    /// apply, mirroring the populator's per-frame emission guards exactly (including HPACK decode
    /// for HEADERS/CONTINUATION) so the presence index never reports a group whose field would not
    /// be emitted. This duplicates the populator's frame walk; that is the accepted cost of keeping
    /// the field materialization lazy while the index stays content-consistent.
    /// </summary>
    private static void DetectFrameGroups(
        ReadOnlySpan<byte> span,
        out bool hasFlags, out bool hasPayload, out bool hasSettings, out bool hasGoaway,
        out bool hasPing, out bool hasWindowUpdate, out bool hasRstStream, out bool hasHeader)
    {
        hasFlags = false;
        hasPayload = false;
        hasSettings = false;
        hasGoaway = false;
        hasPing = false;
        hasWindowUpdate = false;
        hasRstStream = false;
        hasHeader = false;

        int offset = 0;
        while (offset + Http2FrameHeader.Size <= span.Length)
        {
            if (!Http2FrameHeader.TryParse(span[offset..], out Http2FrameHeader frame))
            {
                break;
            }

            // AppendFrameFlags emits flag fields for these types regardless of payload length.
            if (frame.Type is 0 or 1 or 4 or 5 or 6 or 9)
            {
                hasFlags = true;
            }

            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            if (payloadEnd > payloadStart)
            {
                ReadOnlySpan<byte> payload = span[payloadStart..payloadEnd];

                // structured == ParseFramePayload's return value: true when the payload is parsed
                // into structured fields (so no raw http2.payload field), false otherwise.
                bool structured;
                switch (frame.Type)
                {
                    case 3 when payload.Length >= 4: // RST_STREAM
                        hasRstStream = true;
                        structured = true;
                        break;
                    case 4 when payload.Length >= 6: // SETTINGS
                        hasSettings = true;
                        structured = true;
                        break;
                    case 6 when payload.Length == 8: // PING
                        hasPing = true;
                        structured = true;
                        break;
                    case 7 when payload.Length >= 8: // GOAWAY
                        hasGoaway = true;
                        structured = true;
                        break;
                    case 8 when payload.Length >= 4: // WINDOW_UPDATE
                        hasWindowUpdate = true;
                        structured = true;
                        break;
                    case 1: // HEADERS
                    case 9: // CONTINUATION
                        structured = DetectHpackHeaders(frame.Type, frame.Flags, payload, ref hasHeader);
                        break;
                    default:
                        structured = false;
                        break;
                }

                // The populator emits the raw http2.payload field only when the payload is present
                // and was not parsed into structured fields.
                if (!structured)
                {
                    hasPayload = true;
                }
            }

            offset = payloadEnd;
        }
    }

    /// <summary>
    /// Mirrors the HEADERS/CONTINUATION branch of <see cref="ParseFramePayload"/> for detection
    /// only: returns the same structured/raw decision and sets <paramref name="hasHeader"/> when the
    /// HPACK block decodes to at least one header (the exact condition under which the populator
    /// appends http2.header fields).
    /// </summary>
    private static bool DetectHpackHeaders(byte frameType, byte flags, ReadOnlySpan<byte> payload, ref bool hasHeader)
    {
        int hpackOffset = 0;
        int padLength = 0;

        if (frameType == 1)
        {
            bool padded = (flags & 0x08) != 0;
            bool priority = (flags & 0x20) != 0;

            if (padded)
            {
                if (payload.Length < 1)
                {
                    return false;
                }
                padLength = payload[0];
                hpackOffset += 1;
            }
            if (priority)
            {
                if (payload.Length < hpackOffset + 5)
                {
                    return false;
                }
                hpackOffset += 5;
            }
            if (padLength > 0 && payload.Length - hpackOffset < padLength)
            {
                return false;
            }
        }

        int hpackEnd = payload.Length - padLength;
        if (hpackOffset < hpackEnd && HpackDecoder.Decode(payload[hpackOffset..hpackEnd]).Count > 0)
        {
            hasHeader = true;
        }
        return true;
    }

    /// <summary>
    /// Populates all HTTP/2 frame fields from the stored data.
    /// Processes multiple frames in the same segment if present.
    /// </summary>
    private ParseResult PopulateHttp2Fields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> http2Data))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = http2Data.Span;
        int offset = 0;

        // Process all HTTP/2 frames in the segment
        while (offset + Http2FrameHeader.Size <= span.Length)
        {
            if (!Http2FrameHeader.TryParse(span[offset..], out Http2FrameHeader frame))
            {
                break;
            }

            string typeText = Http2DisplayTables.GetFrameTypeDisplayText(frame.Type);

            // Frame container
            MutField frameContainer = container.AppendWithCustomText(
                _FrameFieldId, FieldValue.None,
                ZA.Lazy(typeText, ", Stream: ", frame.StreamId, ", Length: ", frame.Length));

            // Length (24-bit)
            frameContainer.Append(_FrameLengthFieldId, FieldValue.NewU64((ulong)frame.Length));

            // Type
            frameContainer.AppendWithCustomText(_FrameTypeFieldId,
                FieldValue.NewU64(frame.Type), typeText);

            // Flags — precomputed display text includes hex value and active flag names.
            frameContainer.AppendWithCustomText(_FrameFlagsFieldId,
                FieldValue.NewU64(frame.Flags),
                Http2FlagsFormatter.Format(frame.Flags));

            // Stream ID
            frameContainer.Append(_FrameStreamIdFieldId, FieldValue.NewU64(frame.StreamId));

            // Interpret per-type flags
            AppendFrameFlags(in frameContainer, frame.Type, frame.Flags);

            // Payload (if any) — parse type-specific content for known frame types
            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            if (payloadEnd > payloadStart)
            {
                ReadOnlyMemory<byte> payloadData = http2Data.Slice(payloadStart, payloadEnd - payloadStart);
                ReadOnlySpan<byte> payloadSpan = span[payloadStart..payloadEnd];

                // Parse type-specific payloads
                bool parsed = ParseFramePayload(in frameContainer, frame.Type, frame.Flags, payloadSpan, payloadData);

                // If not parsed as structured data, show raw payload
                if (!parsed)
                {
                    frameContainer.Append(_FramePayloadFieldId, FieldValue.NewBytes(payloadData));
                }
            }

            offset = payloadEnd;
        }

        return 0;
    }

    /// <summary>
    /// Appends per-type flag sub-fields based on the frame type.
    /// RFC 7540 Section 6 defines which flags are valid for each frame type.
    /// </summary>
    private void AppendFrameFlags(in MutField frameContainer, byte frameType, byte flags)
    {
        switch (frameType)
        {
            case 0: // DATA: END_STREAM (0x1), PADDED (0x8)
                frameContainer.Append(_FlagEndStreamFieldId, FieldValue.NewBool((flags & 0x01) != 0));
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0));
                break;

            case 1: // HEADERS: END_STREAM (0x1), END_HEADERS (0x4), PADDED (0x8), PRIORITY (0x20)
                frameContainer.Append(_FlagEndStreamFieldId, FieldValue.NewBool((flags & 0x01) != 0));
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0));
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0));
                frameContainer.Append(_FlagPriorityFieldId, FieldValue.NewBool((flags & 0x20) != 0));
                break;

            case 4: // SETTINGS: ACK (0x1)
                frameContainer.Append(_FlagAckFieldId, FieldValue.NewBool((flags & 0x01) != 0));
                break;

            case 5: // PUSH_PROMISE: END_HEADERS (0x4), PADDED (0x8)
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0));
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0));
                break;

            case 6: // PING: ACK (0x1)
                frameContainer.Append(_FlagAckFieldId, FieldValue.NewBool((flags & 0x01) != 0));
                break;

            case 9: // CONTINUATION: END_HEADERS (0x4)
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0));
                break;
        }
    }

    /// <summary>
    /// Parses type-specific payload content for known frame types.
    /// Returns true if the payload was parsed as structured fields.
    /// </summary>
    private bool ParseFramePayload(
        in MutField frameContainer, byte frameType, byte flags,
        ReadOnlySpan<byte> payload, ReadOnlyMemory<byte> payloadMemory)
    {
        switch (frameType)
        {
            case 3 when payload.Length >= 4: // RST_STREAM: 4-byte error code
                {
                    uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload);
                    frameContainer.AppendWithCustomText(_RstStreamErrorCodeFieldId,
                        FieldValue.NewU64(errorCode),
                        Http2DisplayTables.GetErrorCodeDisplayText(errorCode));
                    return true;
                }

            case 4 when payload.Length >= 6: // SETTINGS: parameters in 6-byte entries
                {
                    int pos = 0;
                    while (pos + 6 <= payload.Length)
                    {
                        ushort settingId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
                        uint settingValue = BinaryPrimitives.ReadUInt32BigEndian(payload[(pos + 2)..]);

                        string settingName = Http2DisplayTables.GetSettingsDisplayText(settingId);
                        MutField settingField = frameContainer.AppendWithCustomText(
                            _SettingsFieldId, FieldValue.None,
                            ZA.Lazy(settingName, ": ", settingValue));

                        settingField.AppendWithCustomText(_SettingsIdFieldId,
                            FieldValue.NewU64(settingId), settingName);
                        settingField.Append(_SettingsValueFieldId, FieldValue.NewU64(settingValue));

                        pos += 6;
                    }
                    return true;
                }

            case 6 when payload.Length == 8: // PING: 8 bytes opaque data
                {
                    frameContainer.Append(_PingOpaqueFieldId, FieldValue.NewBytes(payloadMemory));
                    return true;
                }

            case 7 when payload.Length >= 8: // GOAWAY: last_stream_id(4) + error_code(4) + debug(N)
                {
                    uint lastStreamId = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFFU;
                    uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);

                    frameContainer.Append(_GoawayLastStreamIdFieldId, FieldValue.NewU64(lastStreamId));
                    frameContainer.AppendWithCustomText(_GoawayErrorCodeFieldId,
                        FieldValue.NewU64(errorCode),
                        Http2DisplayTables.GetErrorCodeDisplayText(errorCode));

                    if (payload.Length > 8)
                    {
                        frameContainer.Append(_GoawayDebugDataFieldId,
                            FieldValue.NewBytes(payloadMemory[8..]));
                    }
                    return true;
                }

            case 8 when payload.Length >= 4: // WINDOW_UPDATE: 4-byte increment (31-bit)
                {
                    uint increment = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFFU;
                    frameContainer.Append(_WindowUpdateIncrementFieldId, FieldValue.NewU64(increment));
                    return true;
                }

            case 1: // HEADERS — decode HPACK header block
            case 9: // CONTINUATION — contains HPACK header block fragment
                {
                    // Per RFC 7540 §6.2, the HEADERS frame payload is laid out as:
                    //   [Pad Length (1 byte, if PADDED)]
                    //   [Stream Dependency (4 bytes) + Weight (1 byte), if PRIORITY]
                    //   Header Block Fragment
                    //   [Padding (Pad Length bytes), if PADDED]
                    // CONTINUATION frames (RFC 7540 §6.10) carry only the header block fragment
                    // and have neither padding nor priority fields.
                    int hpackOffset = 0;
                    int padLength = 0;

                    if (frameType == 1)
                    {
                        bool padded = (flags & 0x08) != 0;
                        bool priority = (flags & 0x20) != 0;

                        if (padded)
                        {
                            if (payload.Length < 1)
                            {
                                return false;
                            }
                            padLength = payload[0];
                            hpackOffset += 1;
                        }
                        if (priority)
                        {
                            if (payload.Length < hpackOffset + 5)
                            {
                                return false;
                            }
                            // Stream dependency is the low 31 bits of the 32-bit big-endian field;
                            // top bit is the exclusive flag. Currently we surface neither but we
                            // must still skip the 5-byte priority block so HPACK starts at the
                            // correct offset.
                            hpackOffset += 5;
                        }
                        // Padding is appended at the end of the payload — exclude it from HPACK.
                        if (padLength > 0 && payload.Length - hpackOffset < padLength)
                        {
                            // Malformed: declared padding exceeds remaining payload; refuse to decode.
                            return false;
                        }
                    }

                    int hpackEnd = payload.Length - padLength;
                    if (hpackOffset < hpackEnd)
                    {
                        DecodeHpackHeaders(in frameContainer, payload[hpackOffset..hpackEnd]);
                    }
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Decodes HPACK-encoded headers from a HEADERS or CONTINUATION frame payload
    /// and appends each decoded header as a field to the frame container.
    /// </summary>
    private void DecodeHpackHeaders(in MutField frameContainer, ReadOnlySpan<byte> hpackBlock)
    {
        List<HpackDecoder.Header> headers = HpackDecoder.Decode(hpackBlock);

        if (headers.Count == 0)
        {
            return;
        }

        foreach (HpackDecoder.Header header in headers)
        {
            MutField headerField = frameContainer.AppendWithCustomText(
                _HeaderFieldId, FieldValue.None,
                ZA.Lazy(header.Name, ": ", header.Value));

            headerField.Append(_HeaderNameFieldId, FieldValue.NewString(header.Name));
            headerField.Append(_HeaderValueFieldId, FieldValue.NewString(header.Value));
        }
    }
    #endregion
}
