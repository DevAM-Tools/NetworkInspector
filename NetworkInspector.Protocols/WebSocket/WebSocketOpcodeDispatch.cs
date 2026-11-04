// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Eager opcode-based payload dispatch (_DispatchWebSocketPayloads + helpers)

    /// <summary>
    /// Eagerly walks every RFC 6455 frame in <paramref name="wsData"/>, records the index groups of the fields the lazy
    /// populator will emit for each frame, and dispatches the text/binary frame payloads to sub-protocols using the real
    /// index-carrying <paramref name="context"/>, so dispatched sub-protocols record their index groups during
    /// the capture/index phase. No field tree is built here — the descriptive frame tree is
    /// built lazily by <c>_PopulateWebSocketFields</c>, which no longer dispatches.
    /// </summary>
    /// <remarks>
    /// <para>This mirrors the frame walk in <c>_PopulateWebSocketFields</c>: both read the header through
    /// <c>_TryReadFrameHeader</c> (header + length + masking-key decode per RFC 6455 §5.2) and clamp the payload the same way,
    /// so they visit the same frames. The index groups are recorded here, per frame and under exactly the conditions
    /// of the populator's field emission (the presence index must be exact: no group without a field, no field without its
    /// group), instead of from the first frame's header alone. Only text (opcode 1) and binary (opcode 2) frames
    /// dispatch to sub-protocols; all other opcodes carry no dispatchable sub-protocol payload.
    /// Masked payloads are unmasked and per-message-compressed payloads (RSV1) are inflated
    /// exactly as the lazy populator does, so the dispatched bytes match the displayed payload.
    /// If inflating fails or passes the size limit, the compressed bytes are dispatched unchanged (the populator reports the
    /// error or limit field). Every unmasked or inflated payload is bound as an additional packet buffer before it is
    /// dispatched (see <c>_DispatchFramePayload</c>), so a packet with many masked frames can fill the 255-entry buffer
    /// table; the first frame that finds it full reports the limit, the frames after it are not dispatched, and the index
    /// groups of all frames are still recorded. The bound payloads stay alive as long as the packet, so their sum is bounded
    /// too: all payloads that this protocol binds to the packet together may not exceed
    /// <c>ws.max_derived_bytes_per_packet</c>, and the first frame that would pass it reports the limit by name and ends the
    /// dispatch like the table limit does. The inflated size of one payload is bounded by <c>ws.max_decompressed_bytes</c>.</para>
    /// <para>Every length/offset is bounds-checked before slicing; a truncated frame header ends the
    /// walk rather than producing an out-of-range slice. A frame whose payload is cut by the end of the data is
    /// dispatched with the bytes that are present, like the populator shows them.</para>
    /// <para>When a dispatched sub-protocol reports an error the walk continues without dispatching further frames, so the
    /// index groups of the remaining frames are still recorded; the first error is returned.</para>
    /// <para>Cost: unmasking and inflating allocate, and a compressed frame is inflated again by the populator. This method runs
    /// eagerly for every WebSocket packet, whether or not the tree is ever expanded.</para>
    /// </remarks>
    /// <param name="container">The lazy <c>websocket</c> container on which the sub-protocols are dispatched.</param>
    /// <param name="wsData">The TCP payload, starting at a frame header.</param>
    /// <param name="context">Parse context that carries the packet index.</param>
    /// <returns>
    /// 0, or the first error: one reported by a dispatched sub-protocol, or one of the two limit errors of
    /// <c>_DispatchFramePayload</c> (the 255-buffer table is full, or the bound payloads would pass
    /// <c>ws.max_derived_bytes_per_packet</c> in total).
    /// </returns>
    private ParseResult _DispatchWebSocketPayloads(in MutField container, ReadOnlyMemory<byte> wsData, in ParseContext context)
    {
        ReadOnlySpan<byte> span = wsData.Span;
        int offset = 0;
        bool dispatchFailed = false;
        ParseResult firstError = 0;

        while (_TryReadFrameHeader(span, ref offset, out FrameHeader header))
        {
            // Groups of the fields that need no payload bytes: the populator emits the masking key and the compression flag
            // for every frame whose header is complete.
            if (header.Masked)
            {
                context.RecordGroupPresence(_WebsocketMasking_keyGroupId);
            }

            if (header.IsCompressed)
            {
                context.RecordGroupPresence(_WebsocketPmcGroupId);
            }

            // Clamp to the bytes present (the Min keeps the (int) cast safe). Every payload-dependent field is emitted only
            // when at least one payload byte is present, so a declared length alone (a frame cut at its header, or a 126/127
            // length of 0) records nothing.
            int payloadLen = _ClampPayloadLength(header.PayloadLength, span.Length - offset);
            if (payloadLen > 0)
            {
                _RecordPayloadGroups(in header, payloadLen, in context);

                // Dispatch only data frames. Continuation frames (opcode 0) are not dispatched: without reassembly a
                // fragment is not a complete message.
                if (!dispatchFailed && (header.Opcode == 1 || header.Opcode == 2))
                {
                    ParseResult dispatchResult = _DispatchFramePayload(
                        in container, in header, wsData.Slice(offset, payloadLen), in context);
                    if (dispatchResult.TryPropagateError(out ParseResult error))
                    {
                        dispatchFailed = true;
                        firstError = error;
                    }
                }
            }

            offset += payloadLen;
        }

        return firstError;
    }

    /// <summary>
    /// Records the index groups of the payload-dependent fields of one frame, mirroring what
    /// <c>_PopulateWebSocketFields</c> appends when the frame has at least one payload byte present.
    /// </summary>
    /// <param name="header">The frame header.</param>
    /// <param name="payloadLen">The number of payload bytes present (greater than 0).</param>
    /// <param name="context">The parse context that carries the packet index.</param>
    private void _RecordPayloadGroups(in FrameHeader header, int payloadLen, in ParseContext context)
    {
        context.RecordGroupPresence(_WebsocketPayloadGroupId);

        // Exactly one of the decompressed, error or limit fields is emitted for a compressed payload, and the three share
        // one group, so the group does not depend on the inflate result.
        if (header.IsCompressed)
        {
            context.RecordGroupPresence(_WebsocketPayloadDecompressedGroupId);
        }

        switch (header.Opcode)
        {
            case 0:
                context.RecordGroupPresence(_WebsocketContinuationGroupId);
                break;
            case 1:
                context.RecordGroupPresence(_WebsocketPayloadTextGroupId);
                break;
            case 8:
                // The close code needs the two status octets.
                if (payloadLen >= 2)
                {
                    context.RecordGroupPresence(_WebsocketCloseGroupId);
                }

                break;
            case 9:
                context.RecordGroupPresence(_WebsocketPingGroupId);
                break;
            case 10:
                context.RecordGroupPresence(_WebsocketPongGroupId);
                break;
        }
    }

    /// <summary>
    /// Unmasks and (for RSV1) inflates the payload of a text or binary frame, binds the derived bytes to the packet, then
    /// dispatches them.
    /// </summary>
    /// <param name="container">The lazy <c>websocket</c> container on which the sub-protocols are dispatched.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="rawPayload">The payload bytes present in the packet (masked as on the wire).</param>
    /// <param name="context">The parse context that carries the packet index.</param>
    /// <returns>
    /// 0 (also when the derived payload is empty and nothing is dispatched), the error reported by the dispatched
    /// sub-protocol, or a parse error when the derived payload cannot be bound to the packet (nothing is dispatched then):
    /// the table already holds 255 additional buffers, or the payloads this protocol bound to the packet would pass
    /// <c>ws.max_derived_bytes_per_packet</c> in total; each message names its limit.
    /// </returns>
    /// <remarks>
    /// A masked or inflated payload is a fresh array, so it is bound once as an additional packet buffer and the bound slice
    /// is what the opcode dispatch receives. A payload that is neither masked nor inflated is already a slice of the bytes
    /// this protocol was given, binds nothing, and counts for neither limit. Cost: one buffer-table entry and the payload
    /// length against the total of the packet per masked or inflated frame.
    /// </remarks>
    private ParseResult _DispatchFramePayload(
        in MutField container, in FrameHeader header, ReadOnlyMemory<byte> rawPayload, in ParseContext context)
    {
        ReadOnlyMemory<byte> payloadData = header.Masked
            ? _UnmaskPayload(rawPayload.Span, header.MaskingKey)
            : rawPayload;

        // RFC 7692: inflate per-message compressed payloads before dispatch so the
        // dispatched bytes match the bytes the populator displays.
        ReadOnlyMemory<byte> effectivePayload = payloadData;
        bool inflated = false;
        if (header.IsCompressed
            && _DecompressPermessageDeflate(payloadData, out ReadOnlyMemory<byte> decompressed) == DecompressionStatus.Succeeded)
        {
            effectivePayload = decompressed;
            inflated = true;
        }

        // Invariant: the slice handed to a nested protocol is either a slice of the bytes this protocol was given or a
        // slice bound to the packet. A nested protocol that keys state on the bytes (Parse data must be a slice of the
        // packet's buffers, see MutField.TryGetEffectLayerKey) and a buffer export rely on that.
        //
        // Unmasked or inflated bytes are derived, not a frame slice: they are bound once so nested protocols can locate
        // them and a buffer export sees them. A raw payload (neither masked nor inflated) is already a packet slice and
        // stays as it is. A failed inflate falls back to the unmasked bytes, which are still a fresh array when the frame
        // is masked, so the condition is "masked or inflated", not "changed by inflating".
        if (header.Masked || inflated)
        {
            // An empty derived payload (an inflated empty message) has nothing to parse: nothing is bound or dispatched.
            if (effectivePayload.IsEmpty)
            {
                return 0;
            }

            // Security: two limits stop the bind, and each is named. (1) Every bound payload stays referenced until the packet is
            // recycled, so without a bound on the sum one packet could keep up to 255 inflated payloads of
            // ws.max_decompressed_bytes each (deflate reaches about 1000:1, so about 1 MiB of small frames pins about 1 GiB) and
            // exhaust the memory of a tree-free parse, which would release each payload after its dispatch. The sum of the bytes
            // of all bytes bound to the packet, with this payload, therefore may not pass ws.max_derived_bytes_per_packet (see
            // the setting for value, range, posture, and who may change it). DerivedBytesLimit.TryBind checks the total before
            // it binds, so a refused payload leaves the packet unchanged; the packet keeps one sum for all protocols, so it also
            // covers payloads that an earlier WebSocket layer of the same packet bound (a tunnel, several records). (2) The table of additional buffers
            // holds at most 255 entries (the effect-key format), and every masked (client-to-server) frame takes one, so a packet
            // with many small masked frames can fill it. Either way the payload is not parsed further and the cause is named; the
            // caller keeps walking the frames, so the index groups of the later frames of the packet are still recorded and only
            // the dispatch of the later frames stops after the first error. Rejected alternatives: dispatching the unbound bytes
            // (a stateful nested protocol would fail with a generic message and a buffer export would miss the bytes), accepting
            // the 255-fold ceiling (a hostile capture exhausts memory). Residual risk: the payload that fails the check was
            // already inflated (at most ws.max_decompressed_bytes, freed right away), so the peak is the total plus one payload.
            bool isBound = DerivedBytesLimit.TryBind(
                in container, effectivePayload, _MaxDerivedBytesPerPacket, out ReadOnlyMemory<byte> bound, out DerivedBytesFailure failure);
            if (!isBound)
            {
                return ParseError.Custom(
                    ProtocolName,
                    failure == DerivedBytesFailure.TotalExceeded
                        ? _DerivedBytesLimitText()
                        : _BoundBufferLimitMessage);
            }

            effectivePayload = bound;
        }

        return header.Opcode == 1
            ? _DispatchTextPayload(in container, effectivePayload, in context)
            : _DispatchBinaryPayload(in container, effectivePayload, in context);
    }

    /// <summary>
    /// The text of the parse error for a packet whose unmasked or inflated payloads would pass
    /// <c>ws.max_derived_bytes_per_packet</c> in total: it names the setting, the limit, and that the limit is the total per
    /// packet. Sync: <c>DerivedBytesLimit.Text</c> (the wording shared with HTTP and SOME/IP-TP).
    /// </summary>
    /// <returns>The error text; allocates one string, on the error path only.</returns>
    private string _DerivedBytesLimitText() => DerivedBytesLimit.Text(
        "Unmasked or decompressed WebSocket payloads", _MaxDerivedBytesPerPacket, "ws.max_derived_bytes_per_packet");

    /// <summary>
    /// Dispatches a text-frame payload (opcode 1): first the <c>ws.port</c> table (port-based
    /// sub-protocol override), then a fallback to the registered <c>text</c> protocol.
    /// The override lookup uses the fixed key 0 (there is no port to key on inside an upgraded connection), so a protocol
    /// registered at key 0 takes every text and binary payload. Returns 0 when the override consumed the payload.
    /// </summary>
    private ParseResult _DispatchTextPayload(in MutField container, ReadOnlyMemory<byte> payloadData, in ParseContext context)
    {
        ParseResult portResult = container.TryCallNextProtocolU64(_PortTableId, 0, payloadData, in context);
        if (portResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }
        if (portResult.TryGetConsumed(out int consumed) && consumed > 0)
        {
            return 0;
        }

        if (_TextProtocolId.IsValid)
        {
            return container.CallProtocol(_TextProtocolId, payloadData, in context);
        }

        return 0;
    }

    /// <summary>
    /// Dispatches a binary-frame payload (opcode 2): first the <c>ws.port</c> table (port-based
    /// sub-protocol override), then a fallback to the registered <c>data</c> protocol.
    /// </summary>
    private ParseResult _DispatchBinaryPayload(in MutField container, ReadOnlyMemory<byte> payloadData, in ParseContext context)
    {
        ParseResult portResult = container.TryCallNextProtocolU64(_PortTableId, 0, payloadData, in context);
        if (portResult.TryPropagateError(out ParseResult error))
        {
            return error;
        }
        if (portResult.TryGetConsumed(out int consumed) && consumed > 0)
        {
            return 0;
        }

        if (_DataProtocolId.IsValid)
        {
            return container.CallProtocol(_DataProtocolId, payloadData, in context);
        }

        return 0;
    }

    /// <summary>
    /// Unmasks a WebSocket payload into a fresh buffer using the 4-byte cyclic XOR mask
    /// (RFC 6455 §5.3). <c>GC.AllocateUninitializedArray</c> avoids zeroing since every byte is written
    /// (the runtime still zeroes small arrays; the saving applies to large payloads).
    /// A new buffer is required because the packet memory is the captured frame and must not be modified.
    /// The result is owned by the caller and never aliases <paramref name="maskedPayload"/>.
    /// </summary>
    /// <param name="maskedPayload">The masked payload bytes.</param>
    /// <param name="maskingKey">The masking key as read big-endian from the frame header (byte 0 in the high bits).</param>
    /// <returns>A new buffer of the same length with the mask removed.</returns>
    private static ReadOnlyMemory<byte> _UnmaskPayload(ReadOnlySpan<byte> maskedPayload, uint maskingKey)
    {
        byte[] unmasked = GC.AllocateUninitializedArray<byte>(maskedPayload.Length);

        // Split the key into its four bytes once; byte i of the payload is XORed with key byte (i mod 4), per RFC 6455 §5.3.
        byte m0 = (byte)(maskingKey >> 24);
        byte m1 = (byte)(maskingKey >> 16);
        byte m2 = (byte)(maskingKey >> 8);
        byte m3 = (byte)maskingKey;

        for (int i = 0; i < maskedPayload.Length; i++)
        {
            unmasked[i] = (byte)(maskedPayload[i] ^ (i & 3) switch
            {
                0 => m0,
                1 => m1,
                2 => m2,
                _ => m3
            });
        }

        return unmasked;
    }

    #endregion
}
