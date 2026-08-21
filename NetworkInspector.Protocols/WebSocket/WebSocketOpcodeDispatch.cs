// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Eager opcode-based payload dispatch (_DispatchWebSocketPayloads + helpers)

    /// <summary>
    /// Eagerly walks every RFC 6455 frame in <paramref name="wsData"/> and dispatches the
    /// text/binary frame payloads to sub-protocols using the real index-carrying
    /// <paramref name="context"/>, so dispatched sub-protocols record their index groups during
    /// the capture/index phase. No field tree is built here — the descriptive frame tree is
    /// built lazily by <c>PopulateWebSocketFields</c>, which no longer dispatches.
    /// </summary>
    /// <remarks>
    /// <para>This mirrors the frame walk in <c>PopulateWebSocketFields</c> (header + length +
    /// masking-key decode per RFC 6455 §5.2). Only text (opcode 1) and binary (opcode 2) frames
    /// dispatch to sub-protocols; all other opcodes carry no dispatchable sub-protocol payload.
    /// Masked payloads are unmasked and per-message-compressed payloads (RSV1) are inflated
    /// exactly as the lazy populator does, so the dispatched bytes match the displayed payload.</para>
    /// <para>Every length/offset is bounds-checked before slicing; a truncated frame ends the
    /// walk rather than producing an out-of-range slice.</para>
    /// </remarks>
    private ParseResult _DispatchWebSocketPayloads(in MutField container, ReadOnlyMemory<byte> wsData, in ParseContext context)
    {
        ReadOnlySpan<byte> span = wsData.Span;
        int offset = 0;

        while (offset + 2 <= span.Length)
        {
            byte firstByte = span[offset];
            byte secondByte = span[offset + 1];

            byte rsv = (byte)((firstByte >> 4) & 0x07);
            byte opcode = (byte)(firstByte & 0x0F);
            bool masked = (secondByte & 0x80) != 0;
            byte payloadLenByte = (byte)(secondByte & 0x7F);

            offset += 2;

            // Determine actual payload length (RFC 6455 §5.2 three-case scheme)
            ulong payloadLength;
            if (payloadLenByte <= 125)
            {
                payloadLength = payloadLenByte;
            }
            else if (payloadLenByte == 126)
            {
                if (offset + 2 > span.Length)
                {
                    break;
                }
                payloadLength = (ulong)((span[offset] << 8) | span[offset + 1]);
                offset += 2;
            }
            else // 127
            {
                if (offset + 8 > span.Length)
                {
                    break;
                }
                payloadLength = ((ulong)span[offset] << 56)
                    | ((ulong)span[offset + 1] << 48)
                    | ((ulong)span[offset + 2] << 40)
                    | ((ulong)span[offset + 3] << 32)
                    | ((ulong)span[offset + 4] << 24)
                    | ((ulong)span[offset + 5] << 16)
                    | ((ulong)span[offset + 6] << 8)
                    | span[offset + 7];
                offset += 8;
            }

            uint maskingKey = 0;
            if (masked)
            {
                if (offset + 4 > span.Length)
                {
                    break;
                }
                maskingKey = (uint)((span[offset] << 24) | (span[offset + 1] << 16)
                    | (span[offset + 2] << 8) | span[offset + 3]);
                offset += 4;
            }

            int payloadLen = (int)Math.Min(payloadLength, (ulong)(span.Length - offset));
            if (payloadLen > 0 && (opcode == 1 || opcode == 2))
            {
                ReadOnlyMemory<byte> payloadData = masked
                    ? _UnmaskPayload(span.Slice(offset, payloadLen), maskingKey)
                    : wsData.Slice(offset, payloadLen);

                // RFC 7692: inflate per-message compressed payloads before dispatch so the
                // dispatched bytes match the bytes the populator displays.
                ReadOnlyMemory<byte> effectivePayload = payloadData;
                if ((rsv & 0x04) != 0)
                {
                    ReadOnlyMemory<byte>? decompressed = _DecompressPermessageDeflate(payloadData);
                    if (decompressed is not null)
                    {
                        effectivePayload = decompressed.Value;
                    }
                }

                ParseResult dispatchResult = opcode == 1
                    ? _DispatchTextPayload(in container, effectivePayload, in context)
                    : _DispatchBinaryPayload(in container, effectivePayload, in context);
                if (dispatchResult.TryPropagateError(out ParseResult error))
                {
                    return error;
                }
            }

            offset += payloadLen;
        }

        return 0;
    }

    /// <summary>
    /// Dispatches a text-frame payload (opcode 1): first the <c>ws.port</c> table (port-based
    /// sub-protocol override), then a fallback to the registered <c>text</c> protocol.
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
    /// (RFC 6455 §5.3). <c>GC.AllocateUninitializedArray</c> avoids zeroing since every byte is written.
    /// </summary>
    private static ReadOnlyMemory<byte> _UnmaskPayload(ReadOnlySpan<byte> maskedPayload, uint maskingKey)
    {
        byte[] unmasked = GC.AllocateUninitializedArray<byte>(maskedPayload.Length);

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
