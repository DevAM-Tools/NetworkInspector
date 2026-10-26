// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Frame header reading (shared by the populator and the eager dispatch walk)

    /// <summary>
    /// The decoded header of one frame (RFC 6455 section 5.2). Immutable; only exists on the stack of a frame walk.
    /// </summary>
    /// <param name="Fin">The FIN bit: the last fragment of a message.</param>
    /// <param name="Rsv">The three RSV bits (RSV1 is 0x04, RSV2 0x02, RSV3 0x01).</param>
    /// <param name="Opcode">The 4-bit opcode.</param>
    /// <param name="Masked">The MASK bit.</param>
    /// <param name="PayloadLength">The declared payload length. May be larger than the bytes present; it is only displayed and is
    /// clamped to the available bytes before any slicing.</param>
    /// <param name="MaskingKey">The masking key as read big-endian (byte 0 in the high bits); 0 for an unmasked frame.</param>
    private readonly record struct FrameHeader(bool Fin, byte Rsv, byte Opcode, bool Masked, ulong PayloadLength, uint MaskingKey)
    {
        /// <summary>RSV1 is set: per-message compressed (RFC 7692).</summary>
        public bool IsCompressed => (Rsv & 0x04) != 0;
    }

    /// <summary>
    /// Reads one frame header at <paramref name="offset"/> (2 to 14 octets: base header, extended length, masking key).
    /// </summary>
    /// <param name="span">The whole TCP payload.</param>
    /// <param name="offset">Read position; advanced to the first payload octet on success, left unchanged on failure.</param>
    /// <param name="header">The decoded header; <see langword="default"/> on failure.</param>
    /// <returns><see langword="false"/> when the header is cut by the end of the data (the frame walk ends there).</returns>
    /// <remarks>
    /// The eager dispatch walk and the populator both read headers through this method, so they visit exactly the same
    /// frames and the eager index groups can mirror the populator's fields. Every length is bounds-checked before any octet
    /// is read. RFC 6455 requires the most significant bit of a 64-bit length to be 0, but the value is not rejected: it is
    /// only displayed and is clamped to the available bytes before any slicing, so a huge declared length cannot cause an
    /// oversized slice or an int overflow.
    /// </remarks>
    private static bool _TryReadFrameHeader(ReadOnlySpan<byte> span, ref int offset, out FrameHeader header)
    {
        header = default;
        int position = offset;
        if (position + 2 > span.Length)
        {
            return false;
        }

        byte firstByte = span[position];
        byte secondByte = span[position + 1];
        position += 2;

        bool masked = (secondByte & 0x80) != 0;
        byte payloadLenByte = (byte)(secondByte & 0x7F);

        // Determine the actual payload length (RFC 6455 section 5.2 three-case scheme)
        ulong payloadLength;
        if (payloadLenByte <= 125)
        {
            // Short frame: the length is encoded directly in the 7 bits.
            payloadLength = payloadLenByte;
        }
        else if (payloadLenByte == 126)
        {
            // 16-bit extended length in the next 2 bytes (big-endian).
            if (position + 2 > span.Length)
            {
                return false;
            }

            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(span[position..]);
            position += 2;
        }
        else
        {
            // 64-bit extended length in the next 8 bytes (big-endian).
            if (position + 8 > span.Length)
            {
                return false;
            }

            payloadLength = BinaryPrimitives.ReadUInt64BigEndian(span[position..]);
            position += 8;
        }

        // Masking key (4 bytes, only if masked)
        uint maskingKey = 0;
        if (masked)
        {
            if (position + 4 > span.Length)
            {
                return false;
            }

            maskingKey = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            position += 4;
        }

        header = new FrameHeader(
            Fin: (firstByte & 0x80) != 0,
            Rsv: (byte)((firstByte >> 4) & 0x07),
            Opcode: (byte)(firstByte & 0x0F),
            Masked: masked,
            PayloadLength: payloadLength,
            MaskingKey: maskingKey);
        offset = position;
        return true;
    }

    /// <summary>
    /// Clamps a declared payload length to the bytes present, so the result is a safe slice length. The minimum with the
    /// available count makes the (int) cast safe (the second operand is at most <see cref="int.MaxValue"/>) and implements
    /// the "show what was captured" behaviour for a frame cut by the end of the TCP segment.
    /// </summary>
    private static int _ClampPayloadLength(ulong declaredLength, int available) =>
        (int)Math.Min(declaredLength, (ulong)available);

    #endregion

    #region Frame-field population (_PopulateWebSocketFields + _ParseClosePayload)

    /// <summary>
    /// Populates all WebSocket frame fields from the stored data.
    /// Handles variable-length header (2–14 bytes) and XOR unmasking.
    /// Processes consecutive frames while sufficient bytes remain in the buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>Algorithm.</b> The method walks <paramref name="container"/>'s raw bytes
    /// in a <c>while</c> loop, consuming one RFC 6455 frame per iteration:</para>
    /// <list type="number">
    ///   <item>Read the frame header (<c>_TryReadFrameHeader</c>: base header, extended length, masking key).</item>
    ///   <item>If masked, XOR-unmask the payload.</item>
    ///   <item>Append frame sub-fields and the opcode-specific descriptive fields (no dispatch, see below).</item>
    ///   <item>Advance <c>offset</c> by the payload length and repeat.</item>
    /// </list>
    /// <para>The loop exits early when a frame header is truncated (no extended length or masking key to read)
    /// rather than producing partial output. A truncated payload is not an exit: the payload is clamped to the
    /// bytes that are present and shown (<c>websocket.payload_length</c> keeps the declared length), because a frame
    /// cut by the end of the TCP segment is the normal case for large messages.</para>
    /// <para>Sub-protocol dispatch of text and binary payloads is done eagerly in <c>Parse</c> by
    /// <c>_DispatchWebSocketPayloads</c>, which walks the same frames through the same header reader and also records the
    /// index groups of every field emitted here; the two must stay in step.</para>
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
        while (_TryReadFrameHeader(span, ref offset, out FrameHeader header))
        {
            string opcodeText = WebSocketDisplayTables.GetOpcodeDisplayText(header.Opcode);

            // Frame container
            MutField frameContainer = container.AppendWithCustomText(
                _FrameFieldId, FieldValue.None,
                opcodeText, header.Fin ? " [FIN]" : "", ", Length: ", header.PayloadLength);

            frameContainer.Append(_FinFieldId, FieldValue.NewBool(header.Fin));
            frameContainer.Append(_RsvFieldId, FieldValue.NewU64(header.Rsv));
            frameContainer.AppendWithCustomText(_OpcodeFieldId, FieldValue.NewU64(header.Opcode), opcodeText);
            frameContainer.Append(_MaskFieldId, FieldValue.NewBool(header.Masked));
            frameContainer.Append(_PayloadLengthFieldId, FieldValue.NewU64(header.PayloadLength));

            // Masking key (if masked)
            if (header.Masked)
            {
                frameContainer.AppendWithCustomText(_MaskingKeyFieldId,
                    FieldValue.NewU64(header.MaskingKey),
                    "0x", new Hex8(header.MaskingKey));
            }

            // RSV1 = Per-Message Compressed (RFC 7692)
            if (header.IsCompressed)
            {
                frameContainer.Append(_PmcFieldId, FieldValue.NewBool(true));
            }

            // Payload, clamped to the bytes present.
            int payloadLen = _ClampPayloadLength(header.PayloadLength, span.Length - offset);
            if (payloadLen > 0)
            {
                ReadOnlyMemory<byte> payloadData;
                if (header.Masked)
                {
                    // Unmask the payload using the shared 4-byte cyclic XOR helper. This allocates a copy: the packet
                    // memory is the captured frame and must not be modified, so unmasking in place is not an option.
                    // Unmasked frames (server to client) skip the copy and reference the packet memory directly.
                    payloadData = _UnmaskPayload(span.Slice(offset, payloadLen), header.MaskingKey);
                }
                else
                {
                    payloadData = wsData.Slice(offset, payloadLen);
                }

                frameContainer.Append(_PayloadFieldId, FieldValue.NewBytes(payloadData));

                // RFC 7692: Decompress per-message compressed payload (RSV1 set on first frame).
                // The compressed data uses raw DEFLATE with an appended 0x00 0x00 0xFF 0xFF trailer.
                // This repeats the inflate that _DispatchWebSocketPayloads already did for text/binary frames of the same
                // packet: the lazy populator has no place to keep that result without retaining one decoded payload per
                // packet, so a compressed frame is inflated once per walk (each inflate is bounded by the limit).
                // effectivePayload is what the text field shows; the raw bytes stay in websocket.payload.
                // Each frame with RSV1 is inflated on its own with a fresh inflater (see _DecompressPermessageDeflate for the
                // consequences for fragmented messages and context takeover).
                ReadOnlyMemory<byte> effectivePayload = payloadData;
                if (header.IsCompressed)
                {
                    DecompressionStatus status = _DecompressPermessageDeflate(payloadData, out ReadOnlyMemory<byte> decompressed);
                    if (status == DecompressionStatus.Succeeded)
                    {
                        effectivePayload = decompressed;
                        frameContainer.Append(_DecompressedPayloadFieldId, FieldValue.NewBytes(effectivePayload));
                    }
                    else if (status == DecompressionStatus.LimitExceeded)
                    {
                        // Name the setting so the user knows which limit to raise; the payload stays undecoded, never truncated.
                        frameContainer.Append(_DecompressedPayloadLimitFieldId, FieldValue.NewString(_DecompressLimitText()));
                    }
                    else
                    {
                        // Decompression failed on a non-empty compressed payload — surface the error
                        // so callers can detect the failure rather than silently receiving no field.
                        frameContainer.Append(_DecompressedPayloadErrorFieldId, FieldValue.NewString("Decompression failed"));
                    }
                }

                // Append opcode-specific descriptive fields. Sub-protocol dispatch for text
                // (opcode 1) and binary (opcode 2) frames is performed eagerly in Parse() with
                // the real index-carrying context, so it is not repeated here.
                switch (header.Opcode)
                {
                    case 0: // Continuation frame — mark as continuation
                        frameContainer.Append(_ContinuationFieldId, FieldValue.NewBool(true));
                        break;
                    case 1: // Text frame — decode UTF-8 for display (dispatch happens eagerly in Parse). The lenient decoder
                            // replaces invalid sequences with U+FFFD; it allocates one string of the (decompressed) size.
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
    /// The text of the <c>websocket.payload.decompressed.limit</c> field: it names the setting and the effective bound
    /// (one array holds the result, so the bound cannot exceed <see cref="Array.MaxLength"/>).
    /// </summary>
    private string _DecompressLimitText()
    {
        ulong effectiveLimit = Math.Min(_MaxDecompressedBytes, (ulong)Array.MaxLength);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Decompressed payload is larger than {effectiveLimit} bytes (setting ws.max_decompressed_bytes); not decoded");
    }

    /// <summary>
    /// Parses a WebSocket close-frame payload.
    /// <para>Per RFC 6455 §5.5.1 the first two bytes are a big-endian unsigned status code;
    /// any remaining bytes are a UTF-8 reason string. A payload shorter than two bytes (including the valid empty
    /// close payload) produces no fields. The payload is the raw frame payload (never the decompressed one).</para>
    /// </summary>
    /// <param name="frameContainer">The frame node that receives the close fields.</param>
    /// <param name="payloadData">The close frame payload (unmasked).</param>
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
