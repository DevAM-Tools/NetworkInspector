// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// LIN protocol parser (ISO 17987) for DLT_LIN (link type 212).
/// Dispatches the frame payload to sub-protocols (e.g. Signal Message) via the <c>lin.id</c>
/// dispatch table, keyed by the 6-bit frame ID. Dispatching applies only to standard
/// (non-event-triggered) frames.
/// <para>DLT_LIN capture format (per Wireshark packet-lin.h / packet-lin.c):</para>
/// <code>
/// Byte  0:    Message Format Revision (should be 1)
/// Bytes 1-3:  Reserved (3 bytes)
/// Byte  4:    Payload-Length[7:4] + Msg-Type[3:2] + Checksum-Type[1:0]
///               Payload-Length: number of data bytes (0-8)
///               Msg-Type: 0=Frame, 3=Event
///               Checksum-Type: 0=Unknown/Error, 1=Classic, 2=Enhanced, 3=Undefined
/// Byte  5:    Parity[7:6] + Frame-ID[5:0]  (Protected ID)
/// Byte  6:    Checksum
/// Byte  7:    Error Flags
///               Bit 0: No Slave Response
///               Bit 1: Framing Error
///               Bit 2: Parity Error
///               Bit 3: Checksum Error
///               Bit 4: Invalid ID Error
///               Bit 5: Overflow Error
/// Bytes 8+:   Data payload (payload_length bytes)
/// </code>
/// <para>Field tree structure:</para>
/// <code>
/// lin: LIN Frame, ID: 0x10, Len: 4
/// ├── lin.message_format: 1
/// ├── lin.message_type: Frame
/// ├── lin.checksum_type: Enhanced
/// ├── lin.pid: 0xD0
/// ├── lin.id: 0x10 (6-bit frame identifier)
/// ├── lin.parity: 0x03
/// ├── lin.parity.valid: Valid
/// ├── lin.length: 4
/// ├── lin.checksum: 0xAB
/// ├── lin.checksum.status: [Good]
/// ├── lin.errors: 0x00
/// ├── lin.data: (4 bytes)
/// └── signal_message: ...                     [optional, when registered on lin.id]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("lin", "LIN", Description = "LIN (ISO 17987)")]
[RegisterAtTable(FrameProtocol.LinkTypeTableName, LinkTypeKey)]
public sealed partial class LinProtocol : IProtocol
{
    #region Constants

    /// <summary>LinkType for DLT_LIN = 212.</summary>
    public const ulong LinkTypeKey = 212;

    /// <summary>
    /// Fixed DLT_LIN header size in bytes (offsets 0-7 inclusive).
    /// Data payload starts at offset 8.
    /// </summary>
    private const int _HeaderSize = 8;

    /// <summary>Mask for the payload-length nibble in byte 4 (bits 7-4).</summary>
    private const byte _PayloadLengthMask = 0xF0;

    /// <summary>Mask for the message-type field in byte 4 (bits 3-2).</summary>
    private const byte _MsgTypeMask = 0x0C;

    /// <summary>Mask for the checksum-type field in byte 4 (bits 1-0).</summary>
    private const byte _ChecksumTypeMask = 0x03;

    /// <summary>Message type: standard frame (unconditional or sporadic).</summary>
    private const byte _MsgTypeFrame = 0;

    /// <summary>Message type: event-triggered frame.</summary>
    private const byte _MsgTypeEvent = 3;

    /// <summary>Mask for the 6-bit frame ID within the PID byte.</summary>
    private const byte _FrameIdMask = 0x3F;

    // Error flag bits in byte 7 (per Wireshark packet-lin.h)
    private const byte _ErrNoSlaveResponse = 0x01;
    private const byte _ErrFraming = 0x02;
    private const byte _ErrParity = 0x04;
    private const byte _ErrChecksum = 0x08;
    private const byte _ErrInvalidId = 0x10;
    private const byte _ErrOverflow = 0x20;

    /// <summary>Index group for always-present LIN fields.</summary>
    private const string _LinIndexGroup = "lin";

    /// <summary>
    /// Dispatch-table name for sub-protocol lookup by 6-bit LIN frame ID.
    /// Dispatching is performed only for standard frames (not event-triggered).
    /// Key: 6-bit frame ID value (0–63).
    /// </summary>
    public const string IdTableName = "lin.id";

    #endregion

    #region Protocol container

    [BytesField("lin", "LIN", IndexGroup = _LinIndexGroup)]
    private FieldId _ProtocolFieldId;

    #endregion

    #region Header fields

    /// <summary>Message format revision (byte 0; standard value is 1).</summary>
    [U64Field("lin.message_format", "Message Format Revision", IndexGroup = _LinIndexGroup)]
    private FieldId _MessageFormatFieldId;

    /// <summary>Message type decoded from bits 3-2 of byte 4.</summary>
    [StringField("lin.message_type", "Message Type", IndexGroup = _LinIndexGroup)]
    private FieldId _MessageTypeFieldId;

    /// <summary>Checksum type decoded from bits 1-0 of byte 4.</summary>
    [StringField("lin.checksum_type", "Checksum Type", IndexGroup = _LinIndexGroup)]
    private FieldId _ChecksumTypeFieldId;

    /// <summary>Protected ID byte (parity + frame ID) at offset 5.</summary>
    [U64Field("lin.pid", "Protected ID", IndexGroup = _LinIndexGroup)]
    private FieldId _PidFieldId;

    /// <summary>6-bit frame identifier (bits 5-0 of the PID byte).</summary>
    [U64Field("lin.id", "Frame ID", IndexGroup = _LinIndexGroup)]
    private FieldId _IdFieldId;

    /// <summary>2-bit parity (bits 7-6 of the PID byte).</summary>
    [U64Field("lin.parity", "Parity", IndexGroup = _LinIndexGroup)]
    private FieldId _ParityFieldId;

    /// <summary>Parity validity computed against ISO 17987 P0/P1 formula.</summary>
    [BoolField("lin.parity.valid", "Parity Valid", IndexGroup = _LinIndexGroup)]
    private FieldId _ParityValidFieldId;

    /// <summary>Payload length decoded from bits 7-4 of byte 4.</summary>
    [U64Field("lin.length", "Length", IndexGroup = _LinIndexGroup)]
    private FieldId _LengthFieldId;

    /// <summary>Checksum byte at offset 6.</summary>
    [U64Field("lin.checksum", "Checksum", IndexGroup = "lin.checksum")]
    private FieldId _ChecksumFieldId;

    /// <summary>Checksum validation result ([Good]/[Bad]).</summary>
    [StringField("lin.checksum.status", "Checksum Status", IndexGroup = "lin.checksum")]
    private FieldId _ChecksumStatusFieldId;

    /// <summary>Error flags byte at offset 7.</summary>
    [U64Field("lin.errors", "Errors", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrorsFieldId;

    /// <summary>No-slave-response error flag (bit 0 of error byte).</summary>
    [BoolField("lin.errors.no_slave_response", "No Slave Response Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrNoSlaveResponseFieldId;

    /// <summary>Framing error flag (bit 1 of error byte).</summary>
    [BoolField("lin.errors.framing", "Framing Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrFramingFieldId;

    /// <summary>Parity error flag (bit 2 of error byte).</summary>
    [BoolField("lin.errors.parity", "Parity Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrParityFieldId;

    /// <summary>Checksum error flag (bit 3 of error byte).</summary>
    [BoolField("lin.errors.checksum", "Checksum Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrChecksumFieldId;

    /// <summary>Invalid-ID error flag (bit 4 of error byte).</summary>
    [BoolField("lin.errors.invalid_id", "Invalid ID Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrInvalidIdFieldId;

    /// <summary>Overflow error flag (bit 5 of error byte).</summary>
    [BoolField("lin.errors.overflow", "Overflow Error", IndexGroup = _LinIndexGroup)]
    private FieldId _ErrOverflowFieldId;

    #endregion

    #region Data (conditional — present when payload length > 0 and no errors)

    /// <summary>Dispatch table for sub-protocols keyed by 6-bit LIN frame ID.</summary>
    [ProtocolTableU64(IdTableName, "LIN Frame ID")]
    private ProtocolTableId _IdTableId;

    [BytesField("lin.data", "Data", IndexGroup = "lin.data")]
    private FieldId _DataFieldId;

    /// <summary>
    /// Parses a LIN frame in DLT_LIN format (per Wireshark packet-lin.c).
    /// Dispatches the data payload to sub-protocols keyed by the 6-bit frame ID.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        ReadOnlySpan<byte> span = data.Span;

        // ── Byte 0: Message Format Revision ──────────────────────────────────
        byte msgFormatRev = span[0];
        // Bytes 1-3: reserved, not decoded

        // ── Byte 4: Payload-Length[7:4] | Msg-Type[3:2] | Checksum-Type[1:0] ─
        byte byte4 = span[4];
        byte payloadLength = (byte)((byte4 & _PayloadLengthMask) >> 4);
        byte msgType = (byte)((byte4 & _MsgTypeMask) >> 2);
        byte checksumType = (byte)(byte4 & _ChecksumTypeMask);

        // ── Byte 5: PID (Parity[7:6] | Frame-ID[5:0]) ────────────────────────
        byte pid = span[5];
        byte frameId = (byte)(pid & _FrameIdMask);
        byte parity = (byte)((pid >> 6) & 0x03);

        // ── Byte 6: Checksum ──────────────────────────────────────────────────
        byte checksum = span[6];

        // ── Byte 7: Error Flags ───────────────────────────────────────────────
        byte errorFlags = span[7];

        // Total consumed: 8-byte header + data payload (clamped to available data).
        int actualDataLen = Math.Min((int)payloadLength, data.Length - _HeaderSize);
        int totalConsumed = _HeaderSize + actualDataLen;

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_LinGroupId);

        // Container field showing type, ID, and length in the summary
        string msgTypeSummaryText = msgType == _MsgTypeEvent ? "Event" : "Frame";
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId,
            FieldValue.NewBytes(data[..totalConsumed]),
            ZA.Lazy("LIN ", msgTypeSummaryText, ", ID: ", Helpers.DisplayTables.FormatHexU8(frameId),
                    ", Len: ", payloadLength));

        // Message format revision
        container.Append(_MessageFormatFieldId, FieldValue.NewU64(msgFormatRev));

        // Message type
        string msgTypeText = msgType switch
        {
            _MsgTypeFrame => "Frame",
            1 => "Event-triggered",
            2 => "Sporadic",
            _MsgTypeEvent => "Event",
            _ => ZA.String("Unknown (", msgType, ")")
        };
        container.AppendWithCustomText(_MessageTypeFieldId, FieldValue.NewString(msgTypeText), msgTypeText);

        // For non-event frames only: decode checksum type, PID, parity, length, checksum
        if (msgType != _MsgTypeEvent)
        {
            // Checksum type
            string checksumTypeText = checksumType switch
            {
                1 => "Classic",
                2 => "Enhanced",
                3 => "Undefined",
                _ => "Unknown/Error"
            };
            container.AppendWithCustomText(_ChecksumTypeFieldId, FieldValue.NewString(checksumTypeText), checksumTypeText);

            // Protected ID
            container.AppendWithCustomText(_PidFieldId,
                FieldValue.NewU64(pid),
                Helpers.DisplayTables.FormatHexU8(pid));

            // Frame ID (6-bit)
            container.AppendWithCustomText(_IdFieldId,
                FieldValue.NewU64(frameId),
                Helpers.DisplayTables.FormatHexU8(frameId));

            // Parity (2-bit)
            container.Append(_ParityFieldId, FieldValue.NewU64(parity));

            // Parity validity per ISO 17987: P0 = ID0^ID1^ID2^ID4, P1 = !(ID1^ID3^ID4^ID5)
            bool parityValid = _ValidateParity(frameId, parity);
            container.AppendWithCustomText(_ParityValidFieldId,
                FieldValue.NewBool(parityValid),
                parityValid ? "Valid" : "Invalid");

            // Payload length
            container.Append(_LengthFieldId, FieldValue.NewU64(payloadLength));

            // Checksum
            context.RecordGroupPresence(_LinChecksumGroupId);
            container.AppendWithCustomText(_ChecksumFieldId,
                FieldValue.NewU64(checksum),
                Helpers.DisplayTables.FormatHexU8(checksum));

            // Checksum validation (only possible when full data was received)
            if (actualDataLen == (int)payloadLength)
            {
                bool valid = _ValidateChecksum(span.Slice(_HeaderSize, actualDataLen), pid, checksumType, checksum);
                container.AppendWithCustomText(_ChecksumStatusFieldId,
                    FieldValue.NewString(valid ? "[Good]" : "[Bad]"),
                    valid ? "[Good]" : "[Bad]");
            }
        }

        // Error flags
        container.AppendWithCustomText(_ErrorsFieldId,
            FieldValue.NewU64(errorFlags),
            Helpers.DisplayTables.FormatHexU8(errorFlags));
        container.Append(_ErrNoSlaveResponseFieldId, FieldValue.NewBool((errorFlags & _ErrNoSlaveResponse) != 0));
        container.Append(_ErrFramingFieldId, FieldValue.NewBool((errorFlags & _ErrFraming) != 0));
        container.Append(_ErrParityFieldId, FieldValue.NewBool((errorFlags & _ErrParity) != 0));
        container.Append(_ErrChecksumFieldId, FieldValue.NewBool((errorFlags & _ErrChecksum) != 0));
        container.Append(_ErrInvalidIdFieldId, FieldValue.NewBool((errorFlags & _ErrInvalidId) != 0));
        container.Append(_ErrOverflowFieldId, FieldValue.NewBool((errorFlags & _ErrOverflow) != 0));

        // Data payload (only when length > 0); dispatch to sub-protocols for standard frames.
        if (actualDataLen > 0)
        {
            context.RecordGroupPresence(_LinDataGroupId);
            ReadOnlyMemory<byte> payload = data.Slice(_HeaderSize, actualDataLen);
            container.Append(_DataFieldId, FieldValue.NewBytes(payload));

            // Dispatch only for standard frames: lin.id is appended exclusively for non-event
            // frames, so sub-protocols keyed on lin.id are only triggered here.
            if (msgType != _MsgTypeEvent)
            {
                ParseResult dispatchResult = container.TryCallNextProtocolU64(_IdTableId, (ulong)frameId, payload, in context);
                if (dispatchResult.TryPropagateError(out ParseResult error))
                {
                    return error;
                }
            }
        }

        return totalConsumed;
    }

    /// <summary>
    /// Validates the LIN checksum using carry-add (mod 255) then bit-inversion.
    /// Classic checksum (type 1): sums data bytes only.
    /// Enhanced checksum (type 2): includes the PID byte in the sum (ISO 17987).
    /// </summary>
    private static bool _ValidateChecksum(ReadOnlySpan<byte> dataBytes, byte pid, byte checksumType, byte expected)
    {
        // Enhanced checksum includes the PID in the initial sum
        uint sum = checksumType == 2 ? pid : 0u;

        foreach (byte b in dataBytes)
        {
            sum += b;
            // Carry-add: if overflow past 0xFF, wrap and add the carry bit
            if (sum > 0xFF)
            {
                sum = (sum & 0xFF) + 1;
            }
        }

        return (byte)(~sum & 0xFF) == expected;
    }

    /// <summary>
    /// Validates the 2-bit parity field against the 6-bit frame ID per ISO 17987.
    /// P0 (bit 6 of PID) = ID0 ^ ID1 ^ ID2 ^ ID4.
    /// P1 (bit 7 of PID) = NOT(ID1 ^ ID3 ^ ID4 ^ ID5).
    /// </summary>
    private static bool _ValidateParity(byte frameId, byte parity)
    {
        int id0 = (frameId >> 0) & 1;
        int id1 = (frameId >> 1) & 1;
        int id2 = (frameId >> 2) & 1;
        int id3 = (frameId >> 3) & 1;
        int id4 = (frameId >> 4) & 1;
        int id5 = (frameId >> 5) & 1;

        int expectedP0 = id0 ^ id1 ^ id2 ^ id4;
        int expectedP1 = (id1 ^ id3 ^ id4 ^ id5) ^ 1; // NOT of XOR

        return (parity & 1) == expectedP0 && ((parity >> 1) & 1) == expectedP1;
    }
    #endregion
}
