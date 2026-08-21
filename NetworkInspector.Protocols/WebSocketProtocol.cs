// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// WebSocket protocol parser (RFC 6455).
/// Parses WebSocket frame headers including FIN, opcode, mask, payload length, and masking key.
/// Performs XOR unmasking for masked frames (client → server).
/// <para><b>Thread safety:</b> Instances are immutable after registration; <see cref="Parse"/> may
/// be called concurrently from multiple threads without external synchronisation. See remarks.</para>
/// <para>WebSocket is established via HTTP Upgrade. Registered in the <c>http.upgrade</c>
/// dispatch table with key "websocket". When HTTP parses a <c>101 Switching Protocols</c>
/// response with <c>Upgrade: websocket</c>, it dispatches subsequent data to this protocol.</para>
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
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("websocket", "WebSocket", Description = "WebSocket (RFC 6455)")]
[RegisterAtStringTable(HttpProtocol.UpgradeTableName, UpgradeKey)]
public sealed partial class WebSocketProtocol : IProtocol
{
    #region Constants

    /// <summary>Key for HTTP Upgrade dispatch table (lowercase per RFC 6455).</summary>
    public const string UpgradeKey = "websocket";

    /// <summary>Protocol table name for WebSocket sub-protocol dispatch (by negotiated protocol name).</summary>
    public const string ProtocolTableName = "ws.protocol";

    /// <summary>Protocol table name for WebSocket port-based dispatch.</summary>
    public const string PortTableName = "ws.port";

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

    [BytesField("websocket", "WebSocket", IndexGroup = _WsIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Dispatch tables

    [ProtocolTableString(ProtocolTableName, "WebSocket Sub-Protocol")]
    private ProtocolTableId _ProtocolTableId;

    [ProtocolTableU64(PortTableName, "WebSocket Server Port")]
    private ProtocolTableId _PortTableId;

    #endregion

    #region Frame fields

    [NoneField("websocket.frame", "WebSocket Frame", IndexGroup = _WsIndexGroup)]
    private FieldId _FrameFieldId;

    [BoolField("websocket.fin", "FIN", IndexGroup = _WsIndexGroup)]
    private FieldId _FinFieldId;

    [U64Field("websocket.rsv", "RSV", IndexGroup = _WsIndexGroup)]
    private FieldId _RsvFieldId;

    [U64Field("websocket.opcode", "Opcode", IndexGroup = _WsIndexGroup)]
    private FieldId _OpcodeFieldId;

    [BoolField("websocket.mask", "Mask", IndexGroup = _WsIndexGroup)]
    private FieldId _MaskFieldId;

    [U64Field("websocket.payload_length", "Payload Length", IndexGroup = _WsIndexGroup)]
    private FieldId _PayloadLengthFieldId;

    [U64Field("websocket.masking_key", "Masking Key", IndexGroup = _WsMaskKeyGroup)]
    private FieldId _MaskingKeyFieldId;

    [BytesField("websocket.payload", "Payload", IndexGroup = _WsPayloadGroup)]
    private FieldId _PayloadFieldId;

    #endregion

    #region Per-message compressed flag (RSV1)

    [BoolField("websocket.pmc", "Per-Message Compressed", IndexGroup = _WsPmcGroup)]
    private FieldId _PmcFieldId;

    #endregion

    #region Text payload (decoded UTF-8 for text frames)

    [StringField("websocket.payload.text", "Text Payload", IndexGroup = _WsTextGroup)]
    private FieldId _PayloadTextField;

    #endregion

    #region Ping/Pong payload

    [BytesField("websocket.payload.ping", "Ping Payload", IndexGroup = _WsPingGroup)]
    private FieldId _PingPayloadFieldId;

    [BytesField("websocket.payload.pong", "Pong Payload", IndexGroup = _WsPongGroup)]
    private FieldId _PongPayloadFieldId;

    /// <summary>Index group for close frame fields (only present in close frames).</summary>
    private const string _WsCloseGroup = "websocket.close";

    // Close frame sub-fields (opcode 8): first 2 bytes = status code, rest = reason UTF-8 string
    [U64Field("websocket.close.code", "Status Code", IndexGroup = _WsCloseGroup)]
    private FieldId _CloseCodeFieldId;

    [StringField("websocket.close.reason", "Reason", IndexGroup = _WsCloseGroup)]
    private FieldId _CloseReasonFieldId;

    #endregion

    #region Per-message decompressed payload (RFC 7692)

    [BytesField("websocket.payload.decompressed", "Decompressed Payload", IndexGroup = "websocket.payload.decompressed")]
    private FieldId _DecompressedPayloadFieldId;

    /// <summary>Reported when per-message DEFLATE decompression fails on a non-empty compressed payload.</summary>
    [StringField("websocket.payload.decompressed.error", "Decompression Error", IndexGroup = "websocket.payload.decompressed")]
    private FieldId _DecompressedPayloadErrorFieldId;

    #endregion

    #region Continuation frame info

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
    /// Parses WebSocket frames from the TCP payload. Uses lazy population.
    /// Performs a heuristic check to validate the data looks like WebSocket.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Minimum WebSocket frame is 2 bytes (FIN/opcode + mask/length)
        if (data.Length < 2)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, 2, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // Heuristic: validate first byte looks like a valid WebSocket frame
        byte opcode = (byte)(span[0] & 0x0F);
        // Valid opcodes: 0 (continuation), 1 (text), 2 (binary), 8 (close), 9 (ping), 10 (pong)
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

        bool fin = (span[0] & 0x80) != 0;
        string opcodeText = WebSocketDisplayTables.GetOpcodeDisplayText(opcode);

        LazyString summary = ZA.Lazy(
            "WebSocket ", opcodeText, fin ? " [FIN]" : "");

        parentField.SetPacketInfo(ZA.Lazy("WebSocket ", opcodeText));

        // Check mask and payload presence for index groups
        bool masked = (span[1] & 0x80) != 0;
        if (masked)
        {
            context.RecordGroupPresence(_WebsocketMasking_keyGroupId);
        }

        byte payloadLenByte = (byte)(span[1] & 0x7F);
        if (payloadLenByte > 0)
        {
            context.RecordGroupPresence(_WebsocketPayloadGroupId);

            // Record opcode-specific index groups
            if (opcode == 1)
            {
                context.RecordGroupPresence(_WebsocketPayloadTextGroupId);
            }
            else if (opcode == 9)
            {
                context.RecordGroupPresence(_WebsocketPingGroupId);
            }
            else if (opcode == 10)
            {
                context.RecordGroupPresence(_WebsocketPongGroupId);
            }
        }

        // RSV1 = PMC (per-message compressed)
        byte rsv = (byte)((span[0] >> 4) & 0x07);
        if ((rsv & 0x04) != 0)
        {
            context.RecordGroupPresence(_WebsocketPmcGroupId);
        }

        // Close frame with payload — record close index group
        if (opcode == 8 && payloadLenByte >= 2)
        {
            context.RecordGroupPresence(_WebsocketCloseGroupId);
        }

        MutField container = parentField.AppendLazyWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), summary, _Populator);

        // Eagerly dispatch text/binary frame payloads to sub-protocols with the real context so
        // dispatched sub-protocols record their index groups during the index phase (Q6: the
        // index must be complete when the packet is finalized). The lazy populator builds the
        // descriptive frame tree but no longer dispatches.
        ParseResult dispatchResult = _DispatchWebSocketPayloads(in container, data, in context);
        if (dispatchResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }

        return data.Length;
    }

    #endregion
}
