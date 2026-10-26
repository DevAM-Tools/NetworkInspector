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
    /// error or limit field).</para>
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
    /// <returns>0, or the first error reported by a dispatched sub-protocol.</returns>
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
    /// Unmasks and (for RSV1) inflates the payload of a text or binary frame, then dispatches it.
    /// </summary>
    /// <param name="container">The lazy <c>websocket</c> container on which the sub-protocols are dispatched.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="rawPayload">The payload bytes present in the packet (masked as on the wire).</param>
    /// <param name="context">The parse context that carries the packet index.</param>
    /// <returns>0, or the error reported by the dispatched sub-protocol.</returns>
    private ParseResult _DispatchFramePayload(
        in MutField container, in FrameHeader header, ReadOnlyMemory<byte> rawPayload, in ParseContext context)
    {
        ReadOnlyMemory<byte> payloadData = header.Masked
            ? _UnmaskPayload(rawPayload.Span, header.MaskingKey)
            : rawPayload;

        // RFC 7692: inflate per-message compressed payloads before dispatch so the
        // dispatched bytes match the bytes the populator displays.
        ReadOnlyMemory<byte> effectivePayload = payloadData;
        if (header.IsCompressed
            && _DecompressPermessageDeflate(payloadData, out ReadOnlyMemory<byte> decompressed) == DecompressionStatus.Succeeded)
        {
            effectivePayload = decompressed;
        }

        return header.Opcode == 1
            ? _DispatchTextPayload(in container, effectivePayload, in context)
            : _DispatchBinaryPayload(in container, effectivePayload, in context);
    }

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
