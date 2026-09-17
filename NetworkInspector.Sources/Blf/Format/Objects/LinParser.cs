// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF LIN object payloads into DLT_LIN frame bytes (link type 212).
///
/// Output format is DLT_LIN (link type 212):
/// <code>
///   [0]     message format revision (1)
///   [1..4)  reserved
///   [4]     DLC nibble (bits 7-4) | message type (bits 3-2) | checksum type (bits 1-0)
///   [5]     Protected ID (parity + 6-bit frame ID)
///   [6]     checksum
///   [7]     error flags
///   [8..]   data, padded to 4 or 8 bytes
/// </code>
/// Sleep and wakeup objects reconstruct the 12-byte DLT_LIN event (message type 3).
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class LinParser
{
    #region Constants

    /// <summary>
    /// Minimum Type 11 payload size: channel(2) + id(1) + dlc(1) + data(8) = 12 bytes.
    /// </summary>
    private const int _LinMessageV1MinSize = 12;

    /// <summary>
    /// Offset of the Type 11 CRC field (u16 LE). Present when the payload is at least 18 bytes;
    /// the DLT_LIN checksum uses the low byte. Shorter payloads leave checksum 0.
    /// </summary>
    private const int _LinMessageV1CrcOffset = 16;

    /// <summary>
    /// Minimum Type 57 payload size needed to read checksum at offset 120.
    /// Full packed object is 132 bytes (136 on disk with 4-byte alignment padding).
    /// Channel is at offset 12, id at 37, dlc at 38, data at 112..120, checksum at 120.
    /// </summary>
    private const int _LinMessageV2MinSize = 121;

    /// <summary>
    /// Minimum LIN V1 error payload size: channel(2) + dlc(1) + id(1) + ... ≥ 4 bytes.
    /// </summary>
    private const int _LinErrorV1MinSize = 4;

    /// <summary>
    /// Minimum LIN V2 error payload size; same nested struct but only channel and id matter.
    /// </summary>
    private const int _LinErrorV2MinSize = 38;

    /// <summary>
    /// Type 21 LIN wakeup v1: channel (u16 LE) + signal (u8) + external (u8) = 4 bytes.
    /// </summary>
    private const int _LinWakeupV1MinSize = 4;

    /// <summary>
    /// Type 62 LIN wakeup v2: 16-byte bus-event prefix (SOF u64, baudrate u32, channel u16, reserved)
    /// plus lengthInfo/signal/external/reserved = 20 bytes. Channel is at offset 12.
    /// </summary>
    private const int _LinWakeupV2MinSize = 20;

    /// <summary>
    /// Type 20 LIN sleep: channel (u16 LE) + reason (u8) + flags (u8) = 4 bytes.
    /// </summary>
    private const int _LinSleepMinSize = 4;

    /// <summary>Maximum LIN data length.</summary>
    private const int _MaxLinDataLength = 8;

    /// <summary>DLT_LIN header size before payload / event bytes.</summary>
    private const int _DltLinHeaderSize = 8;

    /// <summary>Message type 3 (event) encoded in bits 3-2 of byte 4: <c>3 &lt;&lt; 2</c>.</summary>
    private const byte _DltLinEventTypeByte = 3 << 2;

    private const byte _SleepReasonGoToSleepFrame = 1;
    private const byte _SleepReasonBusIdleTimeout = 2;
    private const byte _SleepReasonSilentSleepmodeCmd = 3;
    private const byte _WakeReasonExternal = 9;
    private const byte _WakeReasonInternal = 10;
    private const byte _WakeReasonBusTraffic = 11;
    private const byte _SleepReasonStartState = 0;
    private const byte _NoSleepReasonBusTraffic = 18;

    #endregion

    #region Public API — Message frames

    /// <summary>
    /// Parses a BLF Type 11 (LIN_MESSAGE) payload into a DLT_LIN frame.
    ///
    /// Payload layout:
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     id (6-bit frame ID, no parity)
    ///   [3]     dlc (0–8)
    ///   [4..12) data (8 bytes, zero-padded)
    ///   [12..16) fsmId, fsmState, headerTime, fullTime (optional)
    ///   [16..18) crc (u16 LE; checksum in the low byte when present)
    /// </code>
    ///
    /// When the payload is shorter than 18 bytes the DLT_LIN checksum byte is 0.
    /// </summary>
    internal static bool TryParseLinMessageV1(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinMessageV1MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte rawId = (byte)(payload[2] & 0x3F);
        byte dlc = (byte)(payload[3] & 0x0F);
        int dataLen = Math.Min((int)dlc, _MaxLinDataLength);

        byte pid = _ComputeLinPid(rawId);
        byte checksum = payload.Length >= _LinMessageV1CrcOffset + 2
            ? payload[_LinMessageV1CrcOffset]
            : (byte)0;

        frame = _BuildDltLinFrame(
            pid,
            dataLen,
            payload.Length >= 4 + dataLen ? payload.Slice(4, dataLen) : payload[4..],
            checksum,
            errors: 0);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 57 (LIN_MESSAGE2) payload into a DLT_LIN frame.
    ///
    /// 132-byte packed nested layout (relevant fields):
    /// <code>
    ///   [0..8)     start-of-frame timestamp (u64 LE)
    ///   [8..12)    event baudrate (u32 LE)
    ///   [12..14)   channel (u16 LE)
    ///   [14..16)   reserved
    ///   [16..32)   sync-break / sync-delimiter lengths (two u64 LE)
    ///   [32..36)   supplier id + message id (two u16 LE)
    ///   [36]       configured node address
    ///   [37]       6-bit frame ID
    ///   [38]       DLC
    ///   [39]       checksum model
    ///   [40..112)  nine u64 LE per-byte timestamps
    ///   [112..120) data (8 bytes)
    ///   [120..122) CRC (u16 LE; checksum in the low byte)
    ///   [122..]    direction / ETF / FSM fields (unused by reconstruction)
    /// </code>
    /// </summary>
    internal static bool TryParseLinMessageV2(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinMessageV2MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        byte rawId = (byte)(payload[37] & 0x3F);
        byte dlc = (byte)(payload[38] & 0x0F);
        int dataLen = Math.Min((int)dlc, _MaxLinDataLength);
        byte checksum = payload[120];

        byte pid = _ComputeLinPid(rawId);

        ReadOnlySpan<byte> data = payload.Length >= 112 + dataLen
            ? payload.Slice(112, dataLen)
            : payload[112..];

        frame = _BuildDltLinFrame(pid, dataLen, data, checksum, errors: 0);
        return true;
    }

    #endregion

    #region Public API — Error frames

    /// <summary>
    /// Parses a BLF LIN V1 error object (Types 12, 14, 15) into a DLT_LIN error frame.
    ///
    /// Payload layout (minimum 4 bytes):
    /// <code>
    ///   [0..2) channel (u16 LE)
    ///   [2]    id (6-bit frame ID, no parity)
    ///   [3]    dlc
    /// </code>
    /// </summary>
    /// <param name="payload">Raw payload bytes.</param>
    /// <param name="errorType">Error flag byte (<see cref="BlfConstants.LinErrorCrc"/>, etc.).</param>
    /// <param name="frame">Resulting DLT_LIN frame.</param>
    /// <param name="channel">BLF channel number.</param>
    internal static bool TryParseLinErrorV1(
        ReadOnlySpan<byte> payload, byte errorType, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinErrorV1MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte rawId = (byte)(payload[2] & 0x3F);
        byte dlc = (byte)(payload[3] & 0x0F);
        byte pid = _ComputeLinPid(rawId);

        frame = _BuildDltLinErrorFrame(pid, Math.Min((int)dlc, _MaxLinDataLength), errorType);
        return true;
    }

    /// <summary>
    /// Parses a BLF LIN V2 error object (Types 58, 60, 61) into a DLT_LIN error frame.
    ///
    /// Uses the same nested Type 57 layout. The minimum requirement is that the
    /// <c>id</c> field at offset 37 is accessible.
    /// </summary>
    /// <param name="payload">Raw payload bytes.</param>
    /// <param name="errorType">Error flag byte (<see cref="BlfConstants.LinErrorCrc"/>, etc.).</param>
    /// <param name="frame">Resulting DLT_LIN frame.</param>
    /// <param name="channel">BLF channel number.</param>
    internal static bool TryParseLinErrorV2(
        ReadOnlySpan<byte> payload, byte errorType, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinErrorV2MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        byte rawId = (byte)(payload[37] & 0x3F);
        byte dlc = payload.Length > 38
            ? (byte)(payload[38] & 0x0F)
            : (byte)0;
        byte pid = _ComputeLinPid(rawId);

        frame = _BuildDltLinErrorFrame(pid, Math.Min((int)dlc, _MaxLinDataLength), errorType);
        return true;
    }

    #endregion

    #region Public API — Sleep / wakeup events

    /// <summary>
    /// Parses a BLF Type 20 (LIN_SLEEP) payload into a 12-byte DLT_LIN event.
    /// Layout: channel (u16 LE) at 0, reason at 2, flags at 3. Reason mapping is in
    /// <see cref="_SleepEventByte"/>.
    /// </summary>
    internal static bool TryParseLinSleep(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinSleepMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte reason = payload[2];
        byte flags = payload[3];
        frame = _BuildDltLinEvent(_SleepEventByte(reason, flags));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 21 (LIN_WAKEUP) payload into a 12-byte DLT_LIN wake-up event.
    /// </summary>
    internal static bool TryParseLinWakeup(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinWakeupV1MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = _BuildDltLinEvent(0x04);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 62 (LIN_WAKEUP2) payload into a 12-byte DLT_LIN wake-up event.
    /// Channel is the u16 LE field at offset 12 (same bus-event prefix as Type 57).
    /// </summary>
    internal static bool TryParseLinWakeup2(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _LinWakeupV2MinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        frame = _BuildDltLinEvent(0x04);
        return true;
    }

    #endregion

    #region Public API — channel peek

    /// <summary>
    /// Reads only the channel field for a LIN object, using the same minimum sizes as the parsers.
    /// </summary>
    internal static bool TryGetChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        switch (objectType)
        {
            case BlfConstants.ObjTypeLinMessage:
                if (payload.Length < _LinMessageV1MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeLinCrcError:
            case BlfConstants.ObjTypeLinRcvError:
            case BlfConstants.ObjTypeLinSndError:
                if (payload.Length < _LinErrorV1MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeLinSleep:
                if (payload.Length < _LinSleepMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeLinWakeup:
                if (payload.Length < _LinWakeupV1MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeLinMessage2:
                if (payload.Length < _LinMessageV2MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
                return true;

            case BlfConstants.ObjTypeLinCrcError2:
            case BlfConstants.ObjTypeLinRcvError2:
            case BlfConstants.ObjTypeLinSndError2:
                if (payload.Length < _LinErrorV2MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
                return true;

            case BlfConstants.ObjTypeLinWakeup2:
                if (payload.Length < _LinWakeupV2MinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
                return true;

            default:
                return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Computes the LIN Protected Identifier (PID) from a 6-bit frame ID.
    /// <para>
    /// The two parity bits are computed per the LIN 2.x specification:
    /// <list type="bullet">
    ///   <item>P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4</item>
    ///   <item>P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5)</item>
    /// </list>
    /// Bit layout of PID: [P1|P0|ID5|ID4|ID3|ID2|ID1|ID0].
    /// </para>
    /// </summary>
    private static byte _ComputeLinPid(byte id)
    {
        byte id0 = (byte)(id & 0x01);
        byte id1 = (byte)((id >> 1) & 0x01);
        byte id2 = (byte)((id >> 2) & 0x01);
        byte id3 = (byte)((id >> 3) & 0x01);
        byte id4 = (byte)((id >> 4) & 0x01);
        byte id5 = (byte)((id >> 5) & 0x01);

        byte p0 = (byte)((id0 ^ id1 ^ id2 ^ id4) & 0x01);
        byte p1 = (byte)((1 ^ id1 ^ id3 ^ id4 ^ id5) & 0x01); // NOT(...)

        return (byte)((id & 0x3F) | (p0 << 6) | (p1 << 7));
    }

    /// <summary>
    /// Builds a DLT_LIN frame: 8-byte header plus data padded to 4 or 8 bytes.
    /// </summary>
    private static byte[] _BuildDltLinFrame(
        byte pid, int dlc, ReadOnlySpan<byte> data, byte checksum, byte errors)
    {
        int clampedDlc = Math.Clamp(dlc, 0, _MaxLinDataLength);
        int dataPad = clampedDlc <= 4 ? 4 : 8;
        byte[] frame = new byte[_DltLinHeaderSize + dataPad];
        frame[0] = 1;
        frame[4] = (byte)(clampedDlc << 4);
        frame[5] = pid;
        frame[6] = checksum;
        frame[7] = errors;

        int copyLen = Math.Min(clampedDlc, data.Length);
        if (copyLen > 0)
        {
            data[..copyLen].CopyTo(frame.AsSpan(_DltLinHeaderSize));
        }

        return frame;
    }

    /// <summary>
    /// Builds a 12-byte DLT_LIN error frame (8-byte header plus four reserved payload bytes;
    /// DLC is still encoded in byte 4).
    /// </summary>
    private static byte[] _BuildDltLinErrorFrame(byte pid, int dlc, byte errors)
    {
        int clampedDlc = Math.Clamp(dlc, 0, _MaxLinDataLength);
        byte[] frame = new byte[12];
        frame[0] = 1;
        frame[4] = (byte)(clampedDlc << 4);
        frame[5] = pid;
        frame[7] = errors;
        return frame;
    }

    /// <summary>Builds the 12-byte DLT_LIN event used for sleep and wakeup objects.</summary>
    private static byte[] _BuildDltLinEvent(byte eventCode)
    {
        byte[] frame = new byte[12];
        frame[0] = 1;
        frame[4] = _DltLinEventTypeByte;
        if (eventCode != 0)
        {
            frame[8] = 0xB0;
            frame[9] = 0xB0;
            frame[11] = eventCode;
        }
        return frame;
    }

    /// <summary>
    /// Maps LIN sleep/wakeup reason codes to DLT_LIN event bytes:
    /// 1 = go-to-sleep frame, 2 = inactivity, 4 = wakeup.
    /// Reason 0 / 18 with flags bit 0x02 is treated as wakeup.
    /// </summary>
    private static byte _SleepEventByte(byte reason, byte flags)
    {
        switch (reason)
        {
            case _SleepReasonGoToSleepFrame:
                return 0x01;
            case _SleepReasonBusIdleTimeout:
            case _SleepReasonSilentSleepmodeCmd:
                return 0x02;
            case _WakeReasonExternal:
            case _WakeReasonInternal:
            case _WakeReasonBusTraffic:
                return 0x04;
            case _SleepReasonStartState:
            case _NoSleepReasonBusTraffic:
                if ((flags & 0x02) != 0)
                {
                    return 0x04;
                }

                return 0x02;
            default:
                return 0;
        }
    }

    #endregion
}
