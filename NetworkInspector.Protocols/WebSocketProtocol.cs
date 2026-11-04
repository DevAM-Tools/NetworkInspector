// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// WebSocket protocol parser (RFC 6455).
/// Parses WebSocket frame headers including FIN, opcode, mask, payload length, and masking key.
/// Performs XOR unmasking for masked frames (client → server).
/// <para><b>Thread safety:</b> Instances are immutable after registration; <see cref="IProtocol.Parse"/> may
/// be called concurrently from multiple threads without external synchronisation. See remarks.</para>
/// <para>WebSocket is established via HTTP Upgrade. Registered in the <c>http.upgrade</c>
/// dispatch table with key "websocket". When HTTP parses a <c>101 Switching Protocols</c>
/// response with <c>Upgrade: websocket</c>, it dispatches subsequent data to this protocol.</para>
/// <para><b>Scope and limits of the decoding:</b> the protocol keeps no per-connection state and does not reassemble.
/// Every call sees one TCP payload, walks the frames that start inside it, and shows a frame that is cut off by the end
/// of the payload with the bytes that are present (the declared payload length is still shown as sent). A frame or message
/// continued in a later segment is therefore not joined, and the first bytes of that next segment are read as a frame
/// header. The handshake headers (negotiated extensions) are not consulted: RSV1 alone selects per-message DEFLATE.</para>
/// <para>Field tree structure:</para>
/// <code>
/// websocket: WebSocket
/// └── websocket.frame: WebSocket Frame
///     ├── websocket.fin: true
///     ├── websocket.rsv: 0
///     ├── websocket.opcode: Text (0x1)
///     ├── websocket.mask: true
///     ├── websocket.payload_length: 128
///     ├── websocket.masking_key: 0xAABBCCDD    [only if masked]
///     └── websocket.payload: [bytes]             [only if payload > 0]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// The only instance state is the field and protocol ids (assigned by the generated registration code), the populator
/// delegate and the cached ids of the <c>text</c> and <c>data</c> protocols (assigned in <c>_OnStartCustom</c>), all during the
/// single-threaded build phase and read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. There is no cross-packet state, so there is no first-parse/replay distinction.</para>
/// <para><b>Frame walk, two loops, one header reader:</b> the frame header decoding (RFC 6455 Section 5.2) lives in
/// <c>_TryReadFrameHeader</c>. <see cref="Parse"/> checks the first frame's opcode only (a plausibility test). The eager
/// <c>_DispatchWebSocketPayloads</c> (all frames) records the index groups and dispatches sub-protocols, and the lazy
/// <c>_PopulateWebSocketFields</c> (all frames) builds the field tree. Both loops read headers through the same method and
/// clamp payloads the same way, so they visit the same frames; the dispatch records a group under exactly the condition
/// under which the populator emits its field, and unmasks and inflates exactly as the populator does, so the dispatched
/// bytes equal the displayed payload.</para>
/// </remarks>
[Protocol("websocket", "WebSocket", Description = "WebSocket (RFC 6455)")]
[RegisterAtStringTable(HttpProtocol.UpgradeTableName, UpgradeKey)]
public sealed partial class WebSocketProtocol : IProtocol
{
    #region Constants

    /// <summary>Key for HTTP Upgrade dispatch table (lowercase per RFC 6455).</summary>
    public const string UpgradeKey = "websocket";

    /// <summary>
    /// Protocol table name for WebSocket sub-protocol dispatch (by negotiated protocol name). The table is declared
    /// for extension; nothing in this protocol performs a lookup in it yet.
    /// </summary>
    public const string ProtocolTableName = "ws.protocol";

    /// <summary>
    /// Protocol table name for the WebSocket payload override. Text and binary payloads look up the fixed key 0 here
    /// before falling back to the <c>text</c> / <c>data</c> protocols, so a protocol that registers at key 0 receives
    /// every message payload. No built-in protocol registers here.
    /// </summary>
    public const string PortTableName = "ws.port";

    /// <summary>
    /// Error text for an unmasked or inflated payload that cannot be handed on because the packet's table of bound buffers is full.
    /// Limit: 255 additional buffers per packet (a constant of the packet's effect keys, not configurable); secure by
    /// default. Exceeding it is an expected failure that is named, not a silent drop.
    /// </summary>
    /// <seealso cref="HttpProtocol"/>
    /// <remarks>Sync: <c>HttpProtocol._BoundBufferLimitMessage</c> (the HTTP text of the same limit).</remarks>
    private const string _BoundBufferLimitMessage =
        "Unmasked or decompressed WebSocket payload cannot be parsed further: a packet can hold at most 255 additional buffers";

    /// <summary>Index group for always-present WebSocket fields.</summary>
    private const string _WsIndexGroup = "websocket";

    /// <summary>Index group for masking key (only present in masked frames).</summary>
    private const string _WsMaskKeyGroup = "websocket.masking_key";

    /// <summary>Index group for payload (only present when payload length > 0).</summary>
    private const string _WsPayloadGroup = "websocket.payload";

    /// <summary>Index group for PMC flag (only present when RSV1 is set).</summary>
    private const string _WsPmcGroup = "websocket.pmc";

    /// <summary>Index group for ping payload.</summary>
    private const string _WsPingGroup = "websocket.ping";

    /// <summary>Index group for pong payload.</summary>
    private const string _WsPongGroup = "websocket.pong";

    /// <summary>Index group for text payload.</summary>
    private const string _WsTextGroup = "websocket.payload.text";

    #endregion

    #region Protocol container

    /// <summary>Id of the <c>websocket</c> field ("WebSocket", bytes); set by the generated registration.</summary>
    [BytesField("websocket", "WebSocket", IndexGroup = _WsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch tables

    /// <summary>Id of the string-keyed table <c>ws.protocol</c> ("WebSocket Sub-Protocol"); declared for extension, nothing looks up in it yet.</summary>
    [ProtocolTableString(ProtocolTableName, "WebSocket Sub-Protocol")]
    private ProtocolTableId _ProtocolTableId;

    /// <summary>
    /// Id of the table <c>ws.port</c> ("WebSocket Server Port"), keyed by the fixed key 0: a protocol registered there receives
    /// every text and binary payload before the <c>text</c> and <c>data</c> fallback.
    /// </summary>
    [ProtocolTableU64(PortTableName, "WebSocket Server Port")]
    private ProtocolTableId _PortTableId;

    #endregion

    #region Frame fields

    /// <summary>Id of the <c>websocket.frame</c> field ("WebSocket Frame", a container without a value); set by the generated registration.</summary>
    [NoneField("websocket.frame", "WebSocket Frame", IndexGroup = _WsIndexGroup)]
    private FieldId _FrameFieldId;

    /// <summary>Id of the <c>websocket.fin</c> field ("FIN", a boolean); set by the generated registration.</summary>
    [BoolField("websocket.fin", "FIN", IndexGroup = _WsIndexGroup)]
    private FieldId _FinFieldId;

    /// <summary>Id of the <c>websocket.rsv</c> field ("RSV", an unsigned integer); set by the generated registration.</summary>
    [U64Field("websocket.rsv", "RSV", IndexGroup = _WsIndexGroup)]
    private FieldId _RsvFieldId;

    /// <summary>Id of the <c>websocket.opcode</c> field ("Opcode", an unsigned integer); set by the generated registration.</summary>
    [U64Field("websocket.opcode", "Opcode", IndexGroup = _WsIndexGroup)]
    private FieldId _OpcodeFieldId;

    /// <summary>Id of the <c>websocket.mask</c> field ("Mask", a boolean); set by the generated registration.</summary>
    [BoolField("websocket.mask", "Mask", IndexGroup = _WsIndexGroup)]
    private FieldId _MaskFieldId;

    /// <summary>Id of the <c>websocket.payload_length</c> field ("Payload Length", an unsigned integer); set by the generated registration.</summary>
    [U64Field("websocket.payload_length", "Payload Length", IndexGroup = _WsIndexGroup)]
    private FieldId _PayloadLengthFieldId;

    /// <summary>Id of the <c>websocket.masking_key</c> field ("Masking Key", an unsigned integer); set by the generated registration.</summary>
    [U64Field("websocket.masking_key", "Masking Key", IndexGroup = _WsMaskKeyGroup)]
    private FieldId _MaskingKeyFieldId;

    /// <summary>Id of the <c>websocket.payload</c> field ("Payload", bytes); set by the generated registration.</summary>
    [BytesField("websocket.payload", "Payload", IndexGroup = _WsPayloadGroup)]
    private FieldId _PayloadFieldId;

    #endregion

    #region Per-message compressed flag (RSV1)

    /// <summary>Id of the <c>websocket.pmc</c> field ("Per-Message Compressed", a boolean); set by the generated registration.</summary>
    [BoolField("websocket.pmc", "Per-Message Compressed", IndexGroup = _WsPmcGroup)]
    private FieldId _PmcFieldId;

    #endregion

    #region Text payload (decoded UTF-8 for text frames)

    /// <summary>Id of the <c>websocket.payload.text</c> field ("Text Payload", a string); set by the generated registration.</summary>
    [StringField("websocket.payload.text", "Text Payload", IndexGroup = _WsTextGroup)]
    private FieldId _PayloadTextField;

    #endregion

    #region Ping/Pong payload

    /// <summary>Id of the <c>websocket.payload.ping</c> field ("Ping Payload", bytes); set by the generated registration.</summary>
    [BytesField("websocket.payload.ping", "Ping Payload", IndexGroup = _WsPingGroup)]
    private FieldId _PingPayloadFieldId;

    /// <summary>Id of the <c>websocket.payload.pong</c> field ("Pong Payload", bytes); set by the generated registration.</summary>
    [BytesField("websocket.payload.pong", "Pong Payload", IndexGroup = _WsPongGroup)]
    private FieldId _PongPayloadFieldId;

    /// <summary>Index group for close frame fields (only present in close frames).</summary>
    private const string _WsCloseGroup = "websocket.close";

    // Close frame sub-fields (opcode 8): first 2 bytes = status code, rest = reason UTF-8 string

    /// <summary>Id of the <c>websocket.close.code</c> field ("Status Code", an unsigned integer); set by the generated registration.</summary>
    [U64Field("websocket.close.code", "Status Code", IndexGroup = _WsCloseGroup)]
    private FieldId _CloseCodeFieldId;

    /// <summary>Id of the <c>websocket.close.reason</c> field ("Reason", a string); set by the generated registration.</summary>
    [StringField("websocket.close.reason", "Reason", IndexGroup = _WsCloseGroup)]
    private FieldId _CloseReasonFieldId;

    #endregion

    #region Per-message decompressed payload (RFC 7692)

    /// <summary>Id of the <c>websocket.payload.decompressed</c> field ("Decompressed Payload", bytes); set by the generated registration.</summary>
    [BytesField("websocket.payload.decompressed", "Decompressed Payload", IndexGroup = "websocket.payload.decompressed")]
    private FieldId _DecompressedPayloadFieldId;

    /// <summary>Reported when per-message DEFLATE decompression fails on a non-empty compressed payload.</summary>
    [StringField("websocket.payload.decompressed.error", "Decompression Error", IndexGroup = "websocket.payload.decompressed")]
    private FieldId _DecompressedPayloadErrorFieldId;

    /// <summary>Reported when per-message DEFLATE decompression stopped at <c>ws.max_decompressed_bytes</c>; names the setting.</summary>
    [StringField("websocket.payload.decompressed.limit", "Decompression Limit Reached", IndexGroup = "websocket.payload.decompressed")]
    private FieldId _DecompressedPayloadLimitFieldId;

    #endregion

    #region Continuation frame info

    /// <summary>Id of the <c>websocket.continuation</c> field ("Continuation Frame", a boolean); set by the generated registration.</summary>
    [BoolField("websocket.continuation", "Continuation Frame", IndexGroup = "websocket.continuation")]
    private FieldId _ContinuationFieldId;

    #endregion

    #region Cached protocol references for payload dispatch
    /// <summary>Protocol ID for the <c>text</c> protocol used for UTF-8 text payload dispatch.</summary>
    private ProtocolId _TextProtocolId;
    /// <summary>Protocol ID for the <c>data</c> protocol used for binary payload dispatch.</summary>
    private ProtocolId _DataProtocolId;

    /// <summary>Pre-allocated populator delegate to avoid per-frame closure allocation.</summary>
    private LazyPopulator _Populator = null!;

    partial void _OnStartCustom(Stack stack)
    {
        _Populator = _PopulateWebSocketFields;

        // Resolve Text and Data protocol IDs for payload dispatch fallback
        ProtocolId? textId = stack.GetProtocolId("text");
        _TextProtocolId = textId ?? default;
        ProtocolId? dataId = stack.GetProtocolId("data");
        _DataProtocolId = dataId ?? default;
    }

    /// <summary>
    /// Parses WebSocket frames from the TCP payload. Uses lazy population for the frame tree; the index groups and the
    /// sub-protocol dispatch are done eagerly.
    /// Performs a heuristic check to validate the data looks like WebSocket (the opcode of the first frame must not be
    /// in a reserved range); only the first frame's opcode is inspected for that check.
    /// </summary>
    /// <param name="parentField">Field that receives the lazy <c>websocket</c> container.</param>
    /// <param name="data">TCP payload bytes starting at a frame header.</param>
    /// <param name="context">Parse context used to record index presence and to dispatch frame payloads.</param>
    /// <returns>
    /// All of <paramref name="data"/> as consumed, or a <see cref="ParseError"/> when it is shorter than a frame header,
    /// starts with a reserved opcode, a dispatched sub-protocol reports an error, or an unmasked or inflated payload cannot be
    /// bound: the packet already holds 255 additional buffers, or the bound payloads would pass
    /// <c>ws.max_derived_bytes_per_packet</c> in total (the first frame that finds a limit reached reports it; the frames after it are not dispatched).
    /// </returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Minimum WebSocket frame is 2 bytes (FIN/opcode + mask/length)
        if (data.Length < 2)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, 2, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // Heuristic: validate first byte looks like a valid WebSocket frame. Rejecting reserved opcodes is a cheap plausibility
        // check: data that is not a frame start (for example payload bytes of a frame split across segments) is often refused.
        // Valid opcodes (RFC 6455 Section 5.2): 0 (continuation), 1 (text), 2 (binary), 8 (close), 9 (ping), 10 (pong)
        byte opcode = (byte)(span[0] & 0x0F);
        if (opcode > 2 && opcode < 8)
        {
            // Reserved opcodes 3-7 — not a valid WebSocket frame
            return ParseError.InvalidData(ProtocolName, "Invalid WebSocket opcode (reserved range)");
        }

        if (opcode > 10)
        {
            // Reserved opcodes 11-15 — not a valid WebSocket frame
            return ParseError.InvalidData(ProtocolName, "Invalid WebSocket opcode (reserved range)");
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_WebsocketGroupId);

        // Summary from the first frame: the opcode text comes from a precomputed 16-entry table (no allocation); the info
        // text is lazy.
        bool fin = (span[0] & 0x80) != 0;
        string opcodeText = WebSocketDisplayTables.GetOpcodeDisplayText(opcode);

        parentField.SetPacketInfo(LazyString.FormatLazy(
            opcodeText, static s => string.Create(CultureInfo.InvariantCulture, $"WebSocket {s}")));

        MutField container = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), "WebSocket ", opcodeText, fin ? " [FIN]" : "", _Populator);

        // Walk every frame of the segment: the walk records the index groups of all frames (the masking key, payload,
        // compression and opcode-specific groups; exactly what the lazy populator will emit, no more and no less) and
        // dispatches text/binary frame payloads to sub-protocols with the real context so dispatched sub-protocols
        // record their index groups during the index phase (the index must be complete when the packet is finalized, and
        // must not depend on the lazy populator running). The lazy populator builds the descriptive frame tree but no
        // longer dispatches.
        ParseResult dispatchResult = _DispatchWebSocketPayloads(in container, data, in context);
        if (dispatchResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        return data.Length;
    }

    #endregion
}
