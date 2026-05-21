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

    partial void OnStartCustom(Stack stack) =>
        _Populator = (in MutField container) => PopulateHttp2Fields(in container);

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

        // Check if any frame has payload
        if (firstFrame.Length > 0)
        {
            context.RecordGroupPresence(_Http2PayloadGroupId);
        }

        parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), summary, _Populator);

        return data.Length;
    }

    /// <summary>
    /// Populates all HTTP/2 frame fields from the stored data.
    /// Processes multiple frames in the same segment if present.
    /// </summary>
    private ParseResult PopulateHttp2Fields(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
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
                ZA.Lazy(typeText, ", Stream: ", frame.StreamId, ", Length: ", frame.Length), in context);

            // Length (24-bit)
            frameContainer.Append(_FrameLengthFieldId, FieldValue.NewU64((ulong)frame.Length), in context);

            // Type
            frameContainer.AppendWithCustomText(_FrameTypeFieldId,
                FieldValue.NewU64(frame.Type), typeText, in context);

            // Flags — precomputed display text includes hex value and active flag names.
            frameContainer.AppendWithCustomText(_FrameFlagsFieldId,
                FieldValue.NewU64(frame.Flags),
                Http2FlagsFormatter.Format(frame.Flags), in context);

            // Stream ID
            frameContainer.Append(_FrameStreamIdFieldId, FieldValue.NewU64(frame.StreamId), in context);

            // Interpret per-type flags
            AppendFrameFlags(in frameContainer, frame.Type, frame.Flags, in context);

            // Payload (if any) — parse type-specific content for known frame types
            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            if (payloadEnd > payloadStart)
            {
                ReadOnlyMemory<byte> payloadData = http2Data.Slice(payloadStart, payloadEnd - payloadStart);
                ReadOnlySpan<byte> payloadSpan = span[payloadStart..payloadEnd];

                // Parse type-specific payloads
                bool parsed = ParseFramePayload(in frameContainer, frame.Type, frame.Flags, payloadSpan, payloadData, in context);

                // If not parsed as structured data, show raw payload
                if (!parsed)
                {
                    frameContainer.Append(_FramePayloadFieldId, FieldValue.NewBytes(payloadData), in context);
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
    private void AppendFrameFlags(in MutField frameContainer, byte frameType, byte flags, in ParseContext context)
    {
        switch (frameType)
        {
            case 0: // DATA: END_STREAM (0x1), PADDED (0x8)
                frameContainer.Append(_FlagEndStreamFieldId, FieldValue.NewBool((flags & 0x01) != 0), in context);
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0), in context);
                break;

            case 1: // HEADERS: END_STREAM (0x1), END_HEADERS (0x4), PADDED (0x8), PRIORITY (0x20)
                frameContainer.Append(_FlagEndStreamFieldId, FieldValue.NewBool((flags & 0x01) != 0), in context);
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0), in context);
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0), in context);
                frameContainer.Append(_FlagPriorityFieldId, FieldValue.NewBool((flags & 0x20) != 0), in context);
                break;

            case 4: // SETTINGS: ACK (0x1)
                frameContainer.Append(_FlagAckFieldId, FieldValue.NewBool((flags & 0x01) != 0), in context);
                break;

            case 5: // PUSH_PROMISE: END_HEADERS (0x4), PADDED (0x8)
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0), in context);
                frameContainer.Append(_FlagPaddedFieldId, FieldValue.NewBool((flags & 0x08) != 0), in context);
                break;

            case 6: // PING: ACK (0x1)
                frameContainer.Append(_FlagAckFieldId, FieldValue.NewBool((flags & 0x01) != 0), in context);
                break;

            case 9: // CONTINUATION: END_HEADERS (0x4)
                frameContainer.Append(_FlagEndHeadersFieldId, FieldValue.NewBool((flags & 0x04) != 0), in context);
                break;
        }
    }

    /// <summary>
    /// Parses type-specific payload content for known frame types.
    /// Returns true if the payload was parsed as structured fields.
    /// </summary>
    private bool ParseFramePayload(
        in MutField frameContainer, byte frameType, byte flags,
        ReadOnlySpan<byte> payload, ReadOnlyMemory<byte> payloadMemory, in ParseContext context)
    {
        switch (frameType)
        {
            case 3 when payload.Length >= 4: // RST_STREAM: 4-byte error code
                {
                    uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload);
                    frameContainer.AppendWithCustomText(_RstStreamErrorCodeFieldId,
                        FieldValue.NewU64(errorCode),
                        Http2DisplayTables.GetErrorCodeDisplayText(errorCode), in context);
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
                            ZA.Lazy(settingName, ": ", settingValue), in context);

                        settingField.AppendWithCustomText(_SettingsIdFieldId,
                            FieldValue.NewU64(settingId), settingName, in context);
                        settingField.Append(_SettingsValueFieldId, FieldValue.NewU64(settingValue), in context);

                        pos += 6;
                    }
                    return true;
                }

            case 6 when payload.Length == 8: // PING: 8 bytes opaque data
                {
                    frameContainer.Append(_PingOpaqueFieldId, FieldValue.NewBytes(payloadMemory), in context);
                    return true;
                }

            case 7 when payload.Length >= 8: // GOAWAY: last_stream_id(4) + error_code(4) + debug(N)
                {
                    uint lastStreamId = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFFU;
                    uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);

                    frameContainer.Append(_GoawayLastStreamIdFieldId, FieldValue.NewU64(lastStreamId), in context);
                    frameContainer.AppendWithCustomText(_GoawayErrorCodeFieldId,
                        FieldValue.NewU64(errorCode),
                        Http2DisplayTables.GetErrorCodeDisplayText(errorCode), in context);

                    if (payload.Length > 8)
                    {
                        frameContainer.Append(_GoawayDebugDataFieldId,
                            FieldValue.NewBytes(payloadMemory[8..]), in context);
                    }
                    return true;
                }

            case 8 when payload.Length >= 4: // WINDOW_UPDATE: 4-byte increment (31-bit)
                {
                    uint increment = BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFFU;
                    frameContainer.Append(_WindowUpdateIncrementFieldId, FieldValue.NewU64(increment), in context);
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
                        DecodeHpackHeaders(in frameContainer, payload[hpackOffset..hpackEnd], in context);
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
    private void DecodeHpackHeaders(in MutField frameContainer, ReadOnlySpan<byte> hpackBlock, in ParseContext context)
    {
        List<HpackDecoder.Header> headers = HpackDecoder.Decode(hpackBlock);

        if (headers.Count == 0)
        {
            return;
        }

        context.RecordGroupPresence(_Http2HeaderGroupId);

        foreach (HpackDecoder.Header header in headers)
        {
            MutField headerField = frameContainer.AppendWithCustomText(
                _HeaderFieldId, FieldValue.None,
                ZA.Lazy(header.Name, ": ", header.Value), in context);

            headerField.Append(_HeaderNameFieldId, FieldValue.NewString(header.Name), in context);
            headerField.Append(_HeaderValueFieldId, FieldValue.NewString(header.Value), in context);
        }
    }
    #endregion
}
