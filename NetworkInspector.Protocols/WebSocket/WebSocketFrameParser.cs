// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Frame-field population (_PopulateWebSocketFields + _ParseClosePayload)

    /// <summary>
    /// Populates all WebSocket frame fields from the stored data.
    /// Handles variable-length header (2–14 bytes) and XOR unmasking.
    /// Processes consecutive frames while sufficient bytes remain in the buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>Algorithm.</b> The method walks <paramref name="container"/>'s raw bytes
    /// in a <c>while</c> loop, consuming one complete RFC 6455 frame per iteration:</para>
    /// <list type="number">
    ///   <item>Read 2-byte frame header (FIN/RSV/opcode + MASK/length).</item>
    ///   <item>If payload length byte is 126, read 2 more bytes (16-bit extended length).</item>
    ///   <item>If payload length byte is 127, read 8 more bytes (64-bit extended length).</item>
    ///   <item>If masked, read 4-byte masking key and XOR-unmask the payload.</item>
    ///   <item>Append frame sub-fields; dispatch payload by opcode.</item>
    ///   <item>Advance <c>offset</c> by the payload length and repeat.</item>
    /// </list>
    /// <para>The loop exits early on truncated data (insufficient bytes for a complete
    /// frame) rather than producing partial output.</para>
    /// </remarks>
    private ParseResult _PopulateWebSocketFields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> wsData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }
        ReadOnlySpan<byte> span = wsData.Span;
        int offset = 0;

        // Process frames while there's enough data
        while (offset + 2 <= span.Length)
        {
            int frameStart = offset;
            byte firstByte = span[offset];
            byte secondByte = span[offset + 1];

            bool fin = (firstByte & 0x80) != 0;
            byte rsv = (byte)((firstByte >> 4) & 0x07);
            byte opcode = (byte)(firstByte & 0x0F);
            bool masked = (secondByte & 0x80) != 0;
            byte payloadLenByte = (byte)(secondByte & 0x7F);

            offset += 2;

            // Determine actual payload length (RFC 6455 §5.2 three-case scheme)
            ulong payloadLength;
            if (payloadLenByte <= 125)
            {
                // Short frame: length is encoded directly in the 7 bits.
                payloadLength = payloadLenByte;
            }
            else if (payloadLenByte == 126)
            {
                // 16-bit extended length follows in the next 2 bytes (big-endian).
                if (offset + 2 > span.Length)
                {
                    break;
                }
                payloadLength = (ulong)((span[offset] << 8) | span[offset + 1]);
                offset += 2;
            }
            else // 127
            {
                // 64-bit extended length follows in the next 8 bytes (big-endian).
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

            // Masking key (4 bytes, only if masked)
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

            string opcodeText = WebSocketDisplayTables.GetOpcodeDisplayText(opcode);

            // Frame container
            MutField frameContainer = container.AppendWithCustomText(
                _FrameFieldId, FieldValue.None,
                ZA.Lazy(opcodeText, fin ? " [FIN]" : "", ", Length: ", payloadLength));

            // FIN
            frameContainer.Append(_FinFieldId, FieldValue.NewBool(fin));

            // RSV
            frameContainer.Append(_RsvFieldId, FieldValue.NewU64(rsv));

            // Opcode
            frameContainer.AppendWithCustomText(_OpcodeFieldId,
                FieldValue.NewU64(opcode), opcodeText);

            // Mask flag
            frameContainer.Append(_MaskFieldId, FieldValue.NewBool(masked));

            // Payload length
            frameContainer.Append(_PayloadLengthFieldId, FieldValue.NewU64(payloadLength));

            // Masking key (if masked)
            if (masked)
            {
                frameContainer.AppendWithCustomText(_MaskingKeyFieldId,
                    FieldValue.NewU64(maskingKey),
                    ZA.Lazy("0x", new Hex8(maskingKey)));
            }

            // RSV1 = Per-Message Compressed (RFC 7692)
            if (rsv != 0 && (rsv & 0x04) != 0)
            {
                frameContainer.Append(_PmcFieldId, FieldValue.NewBool(true));
            }

            // Payload
            int payloadLen = (int)Math.Min(payloadLength, (ulong)(span.Length - offset));
            if (payloadLen > 0)
            {
                ReadOnlyMemory<byte> payloadData;
                if (masked)
                {
                    // Unmask the payload using the shared 4-byte cyclic XOR helper.
                    payloadData = _UnmaskPayload(span.Slice(offset, payloadLen), maskingKey);
                }
                else
                {
                    payloadData = wsData.Slice(offset, payloadLen);
                }

                frameContainer.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));

                // RFC 7692: Decompress per-message compressed payload (RSV1 set on first frame).
                // The compressed data uses raw DEFLATE with an appended 0x00 0x00 0xFF 0xFF trailer.
                ReadOnlyMemory<byte> effectivePayload = payloadData;
                bool isCompressed = (rsv & 0x04) != 0;
                if (isCompressed)
                {
                    ReadOnlyMemory<byte>? decompressed = _DecompressPermessageDeflate(payloadData);
                    if (decompressed is not null)
                    {
                        effectivePayload = decompressed.Value;
                        frameContainer.Append(_DecompressedPayloadFieldId,
                            FieldValue.NewBytes(effectivePayload));
                    }
                    else if (payloadData.Length > 0)
                    {
                        // Decompression failed on a non-empty compressed payload — surface the error
                        // so callers can detect the failure rather than silently receiving no field.
                        frameContainer.Append(_DecompressedPayloadErrorFieldId,
                            FieldValue.NewString("Decompression failed"));
                    }
                }

                // Append opcode-specific descriptive fields. Sub-protocol dispatch for text
                // (opcode 1) and binary (opcode 2) frames is performed eagerly in Parse() with
                // the real index-carrying context, so it is not repeated here.
                switch (opcode)
                {
                    case 0: // Continuation frame — mark as continuation
                        frameContainer.Append(_ContinuationFieldId, FieldValue.NewBool(true));
                        break;
                    case 1: // Text frame — decode UTF-8 for display (dispatch happens eagerly in Parse)
                        frameContainer.Append(_PayloadTextField,
                            FieldValue.NewString(System.Text.Encoding.UTF8.GetString(effectivePayload.Span)));
                        break;
                    case 2: // Binary frame — raw payload already appended; dispatch happens eagerly in Parse
                        break;
                    case 8: // Close frame — parse status code and reason
                        _ParseClosePayload(in frameContainer, payloadData);
                        break;
                    case 9: // Ping
                        frameContainer.Append(_PingPayloadFieldId, FieldValue.NewBytes(payloadData));
                        break;
                    case 10: // Pong
                        frameContainer.Append(_PongPayloadFieldId, FieldValue.NewBytes(payloadData));
                        break;
                }
            }

            offset += payloadLen;
        }

        return 0;
    }

    /// <summary>
    /// Parses a WebSocket close-frame payload.
    /// <para>Per RFC 6455 §5.5.1 the first two bytes are a big-endian unsigned status code;
    /// any remaining bytes are a UTF-8 reason string.</para>
    /// </summary>
    private void _ParseClosePayload(in MutField frameContainer, ReadOnlyMemory<byte> payloadData)
    {
        ReadOnlySpan<byte> closeData = payloadData.Span;
        if (closeData.Length >= 2)
        {
            ushort statusCode = (ushort)((closeData[0] << 8) | closeData[1]);
            string statusText = WebSocketDisplayTables.GetCloseCodeDisplayText(statusCode);
            frameContainer.AppendWithCustomText(_CloseCodeFieldId,
                FieldValue.NewU64(statusCode), statusText);

            if (closeData.Length > 2)
            {
                string reason = System.Text.Encoding.UTF8.GetString(closeData[2..]);
                frameContainer.Append(_CloseReasonFieldId,
                    FieldValue.NewString(reason));
            }
        }
    }

    #endregion
}
