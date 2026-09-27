// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF CAN, CAN FD, and CAN XL object payloads into SocketCAN frame bytes.
///
/// Output format is SocketCAN for classic CAN (8 bytes plus the captured data length, 0–8).
/// DLC 8 is 16 bytes. An RTR frame is 8 bytes: byte 4 still holds the DLC and there is no data tail.
/// <code>
///   id(4 BE) + dlc(1) + fd_flags(1=0) + reserved(2) + data(0–8)
/// </code>
/// SocketCAN FD for CAN FD (8 + actual data length, not padded to 64):
/// <code>
///   id(4 BE) + len(1, byte count) + fd_flags(1) + reserved(2) + data(0-64)
/// </code>
/// and SocketCAN XL for CAN XL (12 + dataLength):
/// <code>
///   [0]=0, vcid, priority(2 BE), flags, sdu, dataLength(2 LE), acceptance(4 LE), payload
/// </code>
///
/// Supported types:
/// <list type="bullet">
///   <item>Type 1  (<c>CAN_MESSAGE</c>) — classic CAN, 8-byte header + 8 data bytes</item>
///   <item>Type 2  (<c>CAN_ERROR</c>) — CAN error frame (produces SocketCAN error)</item>
///   <item>Type 3  (<c>CAN_OVERLOAD</c>) — CAN overload (produces SocketCAN error)</item>
///   <item>Type 73 (<c>CAN_ERROR_EXT</c>) — extended error, 24-byte header</item>
///   <item>Type 86 (<c>CAN_MESSAGE2</c>) — classic CAN v2 (same first 16 bytes as Type 1)</item>
///   <item>Type 100 (<c>CAN_FD_MESSAGE</c>) — CAN FD, 20-byte header, data at offset 20</item>
///   <item>Type 101 (<c>CAN_FD_MESSAGE_64</c>) — CAN FD, 40-byte header, data at offset 40</item>
///   <item>Type 104 (<c>CAN_FD_ERROR_64</c>) — CAN FD error, 44-byte header (produces SocketCAN error)</item>
///   <item>Type 139 (<c>CAN_XL_CHANNEL_FRAME</c>) — CAN XL, 104-byte header + payload</item>
/// </list>
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class CanParser
{
    #region Constants

    /// <summary>
    /// Minimum size of a BLF Type 1 / Type 86 CAN message payload:
    /// channel(2) + flags(1) + dlc(1) + id(4) + data(8) = 16 bytes.
    /// </summary>
    private const int _CanMessageMinSize = 16;

    /// <summary>Minimum size of a BLF Type 2 CAN error payload: channel(2) + length(2) + reserved(4) = 8 bytes.</summary>
    private const int _CanErrorMinSize = 8;

    /// <summary>Minimum size of a BLF Type 3 CAN overload payload: channel(2) + reserved(2) = 4 bytes.</summary>
    private const int _CanOverloadMinSize = 4;

    /// <summary>
    /// Packed size of the Type 73 CAN error-ext header:
    /// channel(2) + length(2) + flags(4) + ecc(1) + position(1) + dlc(1) + reserved(1) +
    /// frame length ns(4) + id(4) + errorCodeExt(2) + reserved(2) = 24 bytes.
    /// </summary>
    private const int _CanErrorExtMinSize = 24;

    /// <summary>
    /// Packed size of the Type 100 CAN FD header (data follows at offset 20).
    /// </summary>
    private const int _CanFdMessageHeaderSize = 20;

    /// <summary>
    /// Packed size of the Type 101 CAN FD header (data follows at offset 40).
    /// </summary>
    private const int _CanFdMessage64HeaderSize = 40;

    /// <summary>
    /// Packed size of the Type 104 CAN FD error header. Channel is byte 0.
    /// </summary>
    private const int _CanFdError64HeaderSize = 44;

    /// <summary>SocketCAN error-frame size. Classic data frames are 8 plus the captured length, not this constant.</summary>
    private const int _SocketCanClassicSize = 16;

    /// <summary>SocketCAN FD frame header size (before data).</summary>
    private const int _SocketCanFdHeaderSize = 8;

    /// <summary>Classic CAN maximum data length.</summary>
    private const int _CanMaxDataLength = 8;

    /// <summary>CAN FD maximum data length.</summary>
    private const int _CanFdMaxDataLength = 64;

    #endregion

    #region Public API — Classic CAN

    /// <summary>
    /// Parses a BLF Type 1 (CAN_MESSAGE) payload into a SocketCAN classic frame.
    ///
    /// Payload layout (all little-endian):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     flags  (bit 0x80 = RTR; TX/NERR/WU unused for reconstruction)
    ///   [3]     dlc (low 4 bits)
    ///   [4..8)  id (u32 LE; bit 31 = EFF)
    ///   [8..16) data (8 bytes on the object; the SocketCAN buffer keeps only the captured length)
    /// </code>
    /// The returned frame is 8 bytes plus the data length for that DLC. RTR keeps the DLC in byte 4
    /// and omits the data tail, so the frame is 8 bytes.
    /// </summary>
    internal static bool TryParseCanMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => _TryParseCanMessageCore(payload, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 86 (CAN_MESSAGE2) payload into a SocketCAN classic frame.
    /// The first 16 bytes are identical to Type 1; any trailing bytes are ignored.
    /// </summary>
    internal static bool TryParseCanMessage2(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => _TryParseCanMessageCore(payload, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 2 (CAN_ERROR) payload into a SocketCAN error frame.
    ///
    /// Payload layout:
    /// <code>
    ///   [0..2) channel (u16 LE)
    ///   [2..4) length  (u16 LE, always 0)
    ///   [4..8) reserved (u32 LE)
    /// </code>
    /// </summary>
    internal static bool TryParseCanError(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanErrorMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = _BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 3 (CAN_OVERLOAD) payload into a SocketCAN error frame.
    ///
    /// Payload layout:
    /// <code>
    ///   [0..2) channel (u16 LE)
    ///   [2..4) reserved (u16 LE)
    /// </code>
    /// </summary>
    internal static bool TryParseCanOverload(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanOverloadMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        // CAN overload is a bus-level condition; produce a generic SocketCAN error frame
        frame = _BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 73 (CAN_ERROR_EXT) payload into a SocketCAN error frame.
    ///
    /// Payload layout (24 bytes, little-endian):
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2..4)   length (u16 LE)
    ///   [4..8)   flags (u32 LE)
    ///   [8]      ecc
    ///   [9]      position
    ///   [10]     dlc
    ///   [11]     reserved1
    ///   [12..16) frameLength_in_ns (u32 LE)
    ///   [16..20) id (u32 LE; bit 31 set means 29-bit extended ID)
    ///   [20..22) errorCodeExt (u16 LE)
    ///   [22..24) reserved2
    /// </code>
    /// The reconstructed SocketCAN frame is an error marker; id/dlc from this object are not mapped.
    /// </summary>
    internal static bool TryParseCanErrorExt(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanErrorExtMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = _BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Public API — CAN XL

    /// <summary>
    /// Parses a BLF Type 139 (<c>CAN_XL_CHANNEL_FRAME</c>) payload into SocketCAN XL.
    /// Requires at least <see cref="BlfConstants.CanXlChannelFrameHeaderSize"/> bytes.
    /// Layout: see that constant. Reconstruction uses identifier@12, SDU@16, dataLength@20,
    /// VCID@26, acceptance@28, flags@48, and payload at 104. When the XLF flag is clear,
    /// nested classic (8 bytes plus the data length) or CAN FD frames are reconstructed from FDF/BRS/ESI/RTR.
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN XL, FD, or classic bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from payload byte 0, zero-extended.</param>
    internal static bool TryParseCanXlChannelFrame(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize)
        {
            return false;
        }

        // Sequential LE reads of the 104-byte packed Type 139 header. Do not overlay a C# struct.
        channel = payload[0];
        uint frameIdentifier = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        byte sduType = payload[16];
        ushort declaredDataLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        byte vcid = payload[26];
        uint acceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[48..]);
        int available = payload.Length - BlfConstants.CanXlChannelFrameHeaderSize;
        int actualDataLen = Math.Min((int)declaredDataLength, available);

        if ((flags & BlfConstants.BlfCanXlFlagXlf) == 0)
        {
            // Pass the declared length. Classic rejects a payload that does not contain
            // those data bytes. The CAN FD branch clamps to the bytes that are present.
            return _TryParseNestedClassicOrFdFromCanXl(
                payload, flags, frameIdentifier, declaredDataLength, out frame);
        }

        byte socketFlags = BlfConstants.SocketCanXlXlf;
        if ((flags & BlfConstants.BlfCanXlFlagSec) != 0)
        {
            socketFlags |= BlfConstants.SocketCanXlSec;
        }

        if ((flags & BlfConstants.BlfCanXlFlagRrs) != 0)
        {
            socketFlags |= BlfConstants.SocketCanXlRrs;
        }

        frame = new byte[12 + actualDataLen];
        frame[1] = vcid;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)(frameIdentifier & 0x7FFu));
        frame[4] = socketFlags;
        frame[5] = sduType;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)actualDataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), acceptanceField);

        if (actualDataLen > 0)
        {
            payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen).CopyTo(frame.AsSpan(12));
        }

        return true;
    }

    #endregion

    #region Public API — CAN FD

    /// <summary>
    /// Parses a BLF Type 100 (CAN_FD_MESSAGE) payload into a SocketCAN FD frame.
    /// 20-byte header, data at offset 20 (all little-endian except reconstructed SocketCAN ID):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     flags (bit 0x80 = RTR)
    ///   [3]     DLC (low 4 bits)
    ///   [4..8)  id (u32 LE; bit 31 = EFF)
    ///   [8..12) frame length on bus (u32 LE, nanoseconds)
    ///   [12]    arbitration bit count
    ///   [13]    CAN FD flags (EDL 0x01, BRS 0x02, ESI 0x04)
    ///   [14]    valid data bytes
    ///   [15..20) reserved
    ///   [20..)  data
    /// </code>
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN FD bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanFdMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanFdMessageHeaderSize)
        {
            return false;
        }

        // Sequential LE reads of the 20-byte packed Type 100 header. Do not overlay a C# struct.
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte flags = payload[2];
        byte dlc = (byte)(payload[3] & 0x0F);
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        byte canFdFlags = payload[13];
        byte validDataBytes = payload[14];

        bool isCanFd = (canFdFlags & BlfConstants.BlfCanFdEdl) != 0;
        ReadOnlySpan<byte> dlcTable = isCanFd
            ? BlfConstants.CanFdDlcToLength
            : BlfConstants.CanDlcToLength;
        int tableLen = dlcTable[dlc];
        int available = payload.Length - _CanFdMessageHeaderSize;
        int actualDataLen = Math.Min(tableLen, Math.Min(validDataBytes, Math.Min(available, _CanFdMaxDataLength)));

        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        byte socketFdFlags = 0;
        if (isCanFd)
        {
            // Map BLF FD flags onto SocketCAN fd_flags: EDL<<2 | ESI>>1 | BRS>>1.
            socketFdFlags = (byte)(
                ((canFdFlags & BlfConstants.BlfCanFdEdl) << 2)
                | ((canFdFlags & BlfConstants.BlfCanFdEsi) >> 1)
                | ((canFdFlags & BlfConstants.BlfCanFdBrs) >> 1));
        }
        else if ((flags & BlfConstants.BlfCanMessageFlagRtr) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            actualDataLen = 0;
        }

        frame = _BuildSocketCanFdFrame(
            socketCanId, socketFdFlags, payload.Slice(_CanFdMessageHeaderSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 101 (CAN_FD_MESSAGE_64) payload into a SocketCAN FD frame.
    /// 40-byte header, data at offset 40:
    /// <code>
    ///   [0]      channel (u8)
    ///   [1]      DLC (low 4 bits)
    ///   [2]      valid data bytes
    ///   [3]      tx count
    ///   [4..8)   id (u32 LE; bit 31 = EFF)
    ///   [8..12)  frame length on bus (u32 LE, nanoseconds)
    ///   [12..16) flags (u32 LE; remote 0x10, EDL 0x1000, BRS 0x2000, ESI 0x4000)
    ///   [16..40) bit-timing / CRC fields (unused by reconstruction)
    ///   [40..)   data
    /// </code>
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN FD bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from payload byte 0, zero-extended.</param>
    internal static bool TryParseCanFdMessage64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanFdMessage64HeaderSize)
        {
            return false;
        }

        channel = payload[0];
        byte dlc = (byte)(payload[1] & 0x0F);
        byte validDataBytes = payload[2];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);

        bool isCanFd = (flags & BlfConstants.CanFd64FlagEdl) != 0;
        ReadOnlySpan<byte> dlcTable = isCanFd
            ? BlfConstants.CanFdDlcToLength
            : BlfConstants.CanDlcToLength;
        int tableLen = dlcTable[dlc];
        int available = payload.Length - _CanFdMessage64HeaderSize;
        int actualDataLen = Math.Min(tableLen, Math.Min(validDataBytes, Math.Min(available, _CanFdMaxDataLength)));

        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        byte socketFdFlags = 0;
        if (isCanFd)
        {
            if ((flags & BlfConstants.CanFd64FlagEdl) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdFdf;
            }

            if ((flags & BlfConstants.CanFd64FlagBrs) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdBrs;
            }

            if ((flags & BlfConstants.CanFd64FlagEsi) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdEsi;
            }
        }
        else if ((flags & BlfConstants.CanFd64FlagRemoteFrame) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            actualDataLen = 0;
        }

        frame = _BuildSocketCanFdFrame(
            socketCanId, socketFdFlags, payload.Slice(_CanFdMessage64HeaderSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 104 (CAN_FD_ERROR_64) payload into a SocketCAN error frame.
    /// Requires the 44-byte header; channel is byte 0. Remaining fields are unused.
    /// </summary>
    internal static bool TryParseCanFdError64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanFdError64HeaderSize)
        {
            return false;
        }

        channel = payload[0];
        frame = _BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Public API — channel peek

    /// <summary>
    /// Reads only the channel field for a CAN / CAN FD / CAN XL object, using the same
    /// minimum sizes as the corresponding parser.
    /// </summary>
    internal static bool TryGetChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        switch (objectType)
        {
            case BlfConstants.ObjTypeCanMessage:
            case BlfConstants.ObjTypeCanMessage2:
                if (payload.Length < _CanMessageMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanError:
                if (payload.Length < _CanErrorMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanOverload:
                if (payload.Length < _CanOverloadMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanErrorExt:
                if (payload.Length < _CanErrorExtMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanFdMessage:
                if (payload.Length < _CanFdMessageHeaderSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanFdMessage64:
                if (payload.Length < _CanFdMessage64HeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            case BlfConstants.ObjTypeCanFdError64:
                if (payload.Length < _CanFdError64HeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            case BlfConstants.ObjTypeCanXlChannelFrame:
                if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            default:
                return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Core parser for Type 1 and Type 86 CAN messages (identical first-16-byte layout).
    /// Reads the Type 1 / Type 86 header: flags at offset 2, DLC at 3, EFF in ID bit 31.
    /// </summary>
    private static bool _TryParseCanMessageCore(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _CanMessageMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte flags = payload[2];
        byte dlc = (byte)(payload[3] & 0x0F);
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        byte dataLen = BlfConstants.CanDlcToLength[dlc];

        // Reconstruct SocketCAN ID. EFF lives in BLF id bit 31, not in the flags byte.
        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        // RTR zeros the reconstructed payload; SocketCAN still carries the DLC in byte 4.
        if ((flags & BlfConstants.BlfCanMessageFlagRtr) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            dataLen = 0;
        }

        // Captured length is the header plus the DLC's data bytes. Do not keep a zero tail
        // out to 16. DLC 8 is still 16 bytes. RTR has copyLen 0, so the frame stays 8 bytes.
        // copyLen is at most 8, and _CanMessageMinSize is 16, so the object already contains
        // those data bytes. A shorter object returned false above.
        int copyLen = Math.Min((int)dataLen, _CanMaxDataLength);
        frame = new byte[8 + copyLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = dlc;
        if (copyLen > 0)
        {
            payload.Slice(8, copyLen).CopyTo(frame.AsSpan(8));
        }

        return true;
    }

    /// <summary>
    /// Builds a SocketCAN FD frame: id BE, len as byte count, flags at byte 5, data of <paramref name="data"/>.Length.
    /// </summary>
    private static byte[] _BuildSocketCanFdFrame(
        uint socketCanId, byte socketFdFlags, ReadOnlySpan<byte> data)
    {
        byte[] frame = new byte[_SocketCanFdHeaderSize + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)data.Length;
        frame[5] = socketFdFlags;
        if (data.Length > 0)
        {
            data.CopyTo(frame.AsSpan(8));
        }

        return frame;
    }

    /// <summary>
    /// Builds a minimal 16-byte SocketCAN error frame with the given error flag.
    /// </summary>
    private static byte[] _BuildSocketCanErrorFrame(uint socketCanErrId)
    {
        byte[] errorFrame = new byte[_SocketCanClassicSize];
        BinaryPrimitives.WriteUInt32BigEndian(errorFrame, socketCanErrId);
        return errorFrame;
    }

    /// <summary>
    /// Reconstructs classic or CAN FD SocketCAN bytes from a Type 139 object whose XLF flag is clear.
    /// EFF is set when the 29-bit identifier exceeds 11 bits. RTR applies only to classic frames.
    /// </summary>
    private static bool _TryParseNestedClassicOrFdFromCanXl(
        ReadOnlySpan<byte> payload,
        uint flags,
        uint frameIdentifier,
        int actualDataLen,
        out byte[] frame)
    {
        bool isCanFd = (flags & BlfConstants.BlfCanXlFlagFdf) != 0;
        uint socketCanId = frameIdentifier & 0x1FFF_FFFFu;
        if (socketCanId > 0x7FFu)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        if (isCanFd)
        {
            // Same captured length as before: only the bytes present after the header, capped at 64.
            int availableFd = payload.Length - BlfConstants.CanXlChannelFrameHeaderSize;
            actualDataLen = Math.Min(actualDataLen, availableFd);
            actualDataLen = Math.Min(actualDataLen, _CanFdMaxDataLength);
            byte socketFdFlags = BlfConstants.SocketCanFdFdf;
            if ((flags & BlfConstants.BlfCanXlFlagBrs) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdBrs;
            }

            if ((flags & BlfConstants.BlfCanXlFlagEsi) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdEsi;
            }

            frame = _BuildSocketCanFdFrame(
                socketCanId,
                socketFdFlags,
                payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen));
            return true;
        }

        if ((flags & BlfConstants.BlfCanXlFlagRemoteFrame) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            actualDataLen = 0;
        }

        // Same captured-length rule as Type 1: 8 plus the data bytes. A declared length
        // that overruns the payload is a failure, not a zero-padded 16-byte buffer.
        actualDataLen = Math.Min(actualDataLen, _CanMaxDataLength);
        if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize + actualDataLen)
        {
            frame = [];
            return false;
        }

        frame = new byte[8 + actualDataLen];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)actualDataLen;
        if (actualDataLen > 0)
        {
            payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen).CopyTo(frame.AsSpan(8));
        }

        return true;
    }

    #endregion
}
