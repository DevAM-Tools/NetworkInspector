// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// HTTP/2 protocol parser (RFC 7540) for the frames of one TCP segment.
/// Parses the 9-byte frame header of every frame in the segment and extracts type, flags, stream ID, and length.
/// SETTINGS, PING, GOAWAY, WINDOW_UPDATE, and RST_STREAM payloads are decoded into fields. HEADERS and CONTINUATION
/// header blocks are decoded with the stateless <see cref="HpackDecoder"/>, so headers that depend on the dynamic
/// table of earlier blocks are missing from the field tree.
/// <para>
/// A segment that starts with the client connection preface shows it as <c>http2.magic</c> and decodes the frames after it.
/// </para>
/// <para>
/// Design: frames are not reassembled across TCP segments (no stream reassembly is registered for HTTP/2). A frame
/// whose payload extends past the segment is shown with the bytes that are present, and the next frame header is
/// searched after them. Each frame is walked twice: once eagerly in <c>Parse</c> to record the index groups, and again
/// lazily in the populator to build the fields (see <c>Parse</c>).
/// </para>
/// <para>Field tree structure (abridged; per-type flag fields, settings, and header fields follow the same pattern):</para>
/// <code>
/// http2: HTTP/2
/// └── http2.frame: HTTP/2 Frame
///     ├── http2.frame.length: 16384
///     ├── http2.frame.type: HEADERS (1)
///     ├── http2.frame.flags: 0x05 [ES/ACK, END_HDRS]
///     ├── http2.frame.stream_id: 1
///     ├── http2.frame.flags.end_stream / end_headers / ... (per frame type)
///     ├── http2.header: name: value (one per decoded header, each with .name and .value children)
///     └── http2.frame.payload: [bytes]   (only when the payload is not decoded into structured fields)
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("http2", "HyperText Transfer Protocol 2", Description = "HTTP/2 (RFC 7540)")]
[RegisterAtTable(TcpProtocol.PortTableName, TcpPortKey)]
public sealed partial class Http2Protocol : IProtocol
{
    #region Constants

    /// <summary>
    /// TCP port that selects this parser through the TCP port table. 8443 is a conventional alternative HTTPS port, not
    /// a port assigned to HTTP/2; TLS-protected HTTP/2 is encrypted and only parses here for cleartext (h2c prior
    /// knowledge) traffic. Cleartext HTTP/2 on other ports is found by the heuristic parser, which recognizes the
    /// client connection preface.
    /// </summary>
    public const ulong TcpPortKey = 8443;

    /// <summary>
    /// The HTTP/2 client connection preface, "PRI * HTTP/2.0", CRLF CRLF, "SM", CRLF CRLF (24 octets, RFC 9113 section
    /// 3.4). A protocol fact, shared with the heuristic parser that recognizes it.
    /// </summary>
    internal static ReadOnlySpan<byte> ConnectionPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    /// <summary>Index group for always-present HTTP/2 fields.</summary>
    private const string _Http2IndexGroup = "http2";

    /// <summary>
    /// Index group for the raw frame payload (conditional): present only when a frame has a payload that is not decoded
    /// into structured fields. <c>_DetectFrameGroups</c> must keep this in step with <c>_PopulateHttp2Fields</c>.
    /// </summary>
    private const string _Http2PayloadIndexGroup = "http2.payload";

    #endregion

    #region Protocol container

    [BytesField("http2", "HTTP/2", IndexGroup = _Http2IndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Frame fields

    /// <summary>The 24-octet client connection preface at the start of a connection (RFC 9113 section 3.4).</summary>
    [BytesField("http2.magic", "Magic", IndexGroup = _Http2IndexGroup)]
    private FieldId _MagicFieldId;

    [NoneField("http2.frame", "HTTP/2 Frame", IndexGroup = _Http2IndexGroup)]
    private FieldId _FrameFieldId;

    [U64Field("http2.frame.length", "Length", IndexGroup = _Http2IndexGroup)]
    private FieldId _FrameLengthFieldId;

    [U64Field("http2.frame.type", "Type", IndexGroup = _Http2IndexGroup)]
    private FieldId _FrameTypeFieldId;

    [U64Field("http2.frame.flags", "Flags", IndexGroup = _Http2IndexGroup)]
    private FieldId _FrameFlagsFieldId;

    [U64Field("http2.frame.stream_id", "Stream Identifier", IndexGroup = _Http2IndexGroup)]
    private FieldId _FrameStreamIdFieldId;

    [BytesField("http2.frame.payload", "Payload", IndexGroup = _Http2PayloadIndexGroup)]
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

    // Pre-allocated populator: converting the instance method _PopulateHttp2Fields to a delegate allocates a new
    // delegate object on every conversion, so it is converted once at stack start and the same delegate is handed
    // to every packet instead of allocating one per parsed segment.
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack) => _Populator = _PopulateHttp2Fields;

    /// <summary>
    /// Parses HTTP/2 frames from the TCP segment payload. Uses lazy population.
    /// </summary>
    /// <param name="parentField">The parent field (the TCP layer) that receives the HTTP/2 container.</param>
    /// <param name="data">The TCP segment payload; the container keeps a view of it (no copy) for lazy population.</param>
    /// <param name="context">The parse context used to record protocol and index group presence.</param>
    /// <returns>
    /// The number of bytes consumed (the whole segment), or a parse error when the segment neither starts with the
    /// 24-octet client connection preface nor holds a complete 9-octet frame header. Only the first frame header (after
    /// the preface, if any) is read here for the packet summary.
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The client connection preface (24 octets) is not a frame: reading it as a frame header would show a length of
        // 0x505249 and a type of 0x20. It is skipped here and shown as the magic field; the frames follow after it.
        int prefaceLength = _PrefaceLength(data.Span);

        // The first frame header, if one is completely present after the preface. A segment that is neither long enough
        // for a frame header nor the preface is not HTTP/2 data we can show.
        bool hasFirstFrame = Http2FrameHeader.TryParse(data.Span[prefaceLength..], out Http2FrameHeader firstFrame);
        if (!hasFirstFrame && prefaceLength == 0)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, Http2FrameHeader.Size, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Http2GroupId);

        // Build summary from first frame (or from the preface when no frame header follows it)
        string typeText = hasFirstFrame
            ? Http2DisplayTables.GetFrameTypeDisplayText(firstFrame.Type)
            : "Magic";

        parentField.SetPacketInfo(LazyString.FormatLazy(
            typeText, static s => string.Create(CultureInfo.InvariantCulture, $"HTTP/2 {s}")));

        // Eagerly scan every frame in the segment to record exactly the index groups whose fields
        // the lazy populator will emit. Index state can only be recorded here: a LazyPopulator receives no
        // ParseContext, so it cannot record presence when the fields are materialized later.
        // The relevant groups depend on each frame's type and payload
        // length (and, for HEADERS/CONTINUATION, on the decoded HPACK header count), and a segment
        // may carry several frames — so the decision requires walking all frames and mirroring the
        // populator's per-frame emission guards. This keeps the presence index content-consistent
        // with materialization and free of false positives, at the deliberate cost of repeating the
        // frame walk (and HPACK decode) that the populator performs lazily.
        _DetectFrameGroups(
            data.Span[prefaceLength..],
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

        // The container stores the segment bytes and the shared populator; the frame fields are built only when the
        // container is first expanded.
        if (hasFirstFrame)
        {
            parentField.AppendLazyWithCustomText(
                _ProtocolFieldId, FieldValue.NewBytes(data), "HTTP/2 ", typeText, ", Stream: ", firstFrame.StreamId, ", Length: ",
                firstFrame.Length, _Populator);
        }
        else
        {
            parentField.AppendLazyWithCustomText(_ProtocolFieldId, FieldValue.NewBytes(data), "HTTP/2 ", typeText, _Populator);
        }

        return data.Length;
    }

    /// <summary>
    /// Returns the length of the client connection preface at the start of <paramref name="span"/>, or 0 when the span
    /// does not start with it.
    /// </summary>
    private static int _PrefaceLength(ReadOnlySpan<byte> span) =>
        span.StartsWith(ConnectionPreface)
            ? ConnectionPreface.Length
            : 0;

    /// <summary>
    /// Eagerly walks every HTTP/2 frame in the segment and reports which optional index groups
    /// apply, mirroring the populator's per-frame emission guards exactly (including HPACK decode
    /// for HEADERS/CONTINUATION) so the presence index never reports a group whose field would not
    /// be emitted. This duplicates the populator's frame walk; that is the accepted cost of keeping
    /// the field materialization lazy while the index stays content-consistent.
    /// </summary>
    private static void _DetectFrameGroups(
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

        // One iteration per frame header that is completely present. The loop must visit exactly the frames that
        // _PopulateHttp2Fields visits.
        int offset = 0;
        while (offset + Http2FrameHeader.Size <= span.Length)
        {
            if (!Http2FrameHeader.TryParse(span[offset..], out Http2FrameHeader frame))
            {
                break;
            }

            // _AppendFrameFlags emits flag fields for these types regardless of payload length.
            if (frame.Type is 0 or 1 or 4 or 5 or 6 or 9)
            {
                hasFlags = true;
            }

            // The payload length comes from the wire (24 bits). It is clamped to the bytes in this segment, so a
            // hostile length can neither read out of range nor cause an allocation; the sum cannot overflow an int
            // (Length < 2^24 and the span is one TCP segment, far below int.MaxValue). A frame that continues in the next segment is cut here.
            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            if (payloadEnd > payloadStart)
            {
                ReadOnlySpan<byte> payload = span[payloadStart..payloadEnd];

                // structured == _ParseFramePayload's return value: true when the payload is parsed
                // into structured fields (so no raw http2.payload field), false otherwise. The length guards
                // below are copied from _ParseFramePayload and must stay identical to it.
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
                        structured = _DetectHpackHeaders(frame.Type, frame.Flags, payload, ref hasHeader);
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
    /// Mirrors the HEADERS/CONTINUATION branch of <see cref="_ParseFramePayload"/> for detection
    /// only: returns the same structured/raw decision and sets <paramref name="hasHeader"/> when the
    /// HPACK block decodes to at least one header (the exact condition under which the populator
    /// appends http2.header fields).
    /// </summary>
    private static bool _DetectHpackHeaders(byte frameType, byte flags, ReadOnlySpan<byte> payload, ref bool hasHeader)
    {
        // Same layout walk as _ParseFramePayload (optional pad length, optional priority block, fragment, padding).
        // Returns false exactly where _ParseFramePayload returns false: a malformed frame keeps its raw payload field.
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

        // The decode result is thrown away: only whether it has at least one header matters here. This is the second
        // decode of the block (the populator decodes it again lazily), the accepted cost named in _DetectFrameGroups.
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
    /// <param name="container">The lazy HTTP/2 container whose value holds the segment bytes.</param>
    /// <returns>0 on success (the value is not significant for a populator), or an error when the container holds no bytes.</returns>
    private ParseResult _PopulateHttp2Fields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> http2Data))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        ReadOnlySpan<byte> span = http2Data.Span;

        // The connection preface is shown as the magic field; the frames start after it (same skip as in Parse).
        int offset = _PrefaceLength(span);
        if (offset > 0)
        {
            container.Append(_MagicFieldId, FieldValue.NewBytes(http2Data[..offset]));
        }

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
                typeText, ", Stream: ", frame.StreamId, ", Length: ", frame.Length);

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
            _AppendFrameFlags(in frameContainer, frame.Type, frame.Flags);

            // Payload (if any) — parse type-specific content for known frame types. The length is clamped to the
            // segment exactly as in _DetectFrameGroups (wire-controlled, never trusted for allocation).
            int payloadStart = offset + Http2FrameHeader.Size;
            int payloadEnd = Math.Min(payloadStart + frame.Length, span.Length);
            if (payloadEnd > payloadStart)
            {
                ReadOnlyMemory<byte> payloadData = http2Data.Slice(payloadStart, payloadEnd - payloadStart);
                ReadOnlySpan<byte> payloadSpan = span[payloadStart..payloadEnd];

                // Parse type-specific payloads
                bool parsed = _ParseFramePayload(in frameContainer, frame.Type, frame.Flags, payloadSpan, payloadData);

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
    private void _AppendFrameFlags(in MutField frameContainer, byte frameType, byte flags)
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
    /// <param name="frameContainer">The frame field that receives the payload fields.</param>
    /// <param name="frameType">The frame type octet.</param>
    /// <param name="flags">The frame flags octet.</param>
    /// <param name="payload">The payload bytes present in the segment (possibly shorter than the declared length).</param>
    /// <param name="payloadMemory">The same bytes as memory, for fields that keep a view instead of a copy.</param>
    /// <returns>
    /// <see langword="true"/> when the payload became structured fields; <see langword="false"/> when the caller must
    /// add the raw payload (unknown type, or a payload too short or malformed for its type). The length guards are
    /// deliberately lenient (for example RST_STREAM accepts more than 4 bytes) because a segment can hold a cut-off frame.
    /// </returns>
    private bool _ParseFramePayload(
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

            case 4 when payload.Length >= 6: // SETTINGS: parameters in 6-byte entries (16-bit id + 32-bit value)
                {
                    // Only complete entries are emitted; a trailing partial entry (a cut-off or malformed frame) is
                    // ignored. The number of entries is bounded by the payload present in the segment.
                    int pos = 0;
                    while (pos + 6 <= payload.Length)
                    {
                        ushort settingId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
                        uint settingValue = BinaryPrimitives.ReadUInt32BigEndian(payload[(pos + 2)..]);

                        string settingName = Http2DisplayTables.GetSettingsDisplayText(settingId);
                        MutField settingField = frameContainer.AppendWithCustomText(
                            _SettingsFieldId, FieldValue.None,
                            settingName, ": ", settingValue);

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
                        _DecodeHpackHeaders(in frameContainer, payload[hpackOffset..hpackEnd]);
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
    private void _DecodeHpackHeaders(in MutField frameContainer, ReadOnlySpan<byte> hpackBlock)
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
                header.Name, ": ", header.Value);

            headerField.Append(_HeaderNameFieldId, FieldValue.NewString(header.Name));
            headerField.Append(_HeaderValueFieldId, FieldValue.NewString(header.Value));
        }
    }
    #endregion
}
