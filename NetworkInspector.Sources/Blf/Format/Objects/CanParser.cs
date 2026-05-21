// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF CAN and CAN FD object payloads into SocketCAN frame bytes.
///
/// Output format is SocketCAN for classic CAN (16 bytes fixed):
/// <code>
///   id(4 BE) + dlc(1) + fd_flags(1=0) + reserved(2) + data(8, zero-padded)
/// </code>
/// and SocketCAN FD for CAN FD (8 + data_len bytes, data zero-padded to 64):
/// <code>
///   id(4 BE) + len(1, byte count) + fd_flags(1) + reserved(2) + data(0-64)
/// </code>
///
/// Supported types:
/// <list type="bullet">
///   <item>Type 1  (<c>CAN_MESSAGE</c>)     — classic CAN, 16-byte payload</item>
///   <item>Type 2  (<c>CAN_ERROR</c>)        — CAN error frame (produces SocketCAN error)</item>
///   <item>Type 3  (<c>CAN_OVERLOAD</c>)     — CAN overload (produces SocketCAN error)</item>
///   <item>Type 73 (<c>CAN_ERROR_EXT</c>)    — extended error, with detailed flags</item>
///   <item>Type 86 (<c>CAN_MESSAGE2</c>)     — classic CAN v2 (same header as Type 1)</item>
///   <item>Type 100 (<c>CAN_FD_MESSAGE</c>)  — CAN FD with 24-byte struct header</item>
///   <item>Type 101 (<c>CAN_FD_MESSAGE_64</c>) — CAN FD 64 with 1-byte channel</item>
///   <item>Type 104 (<c>CAN_FD_ERROR_64</c>) — CAN FD error 64 (produces SocketCAN error)</item>
/// </list>
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class CanParser
{
    #region Constants

    /// <summary>
    /// Minimum size of a BLF Type 1 / Type 86 CAN message payload:
    /// channel(2) + dlc(1) + flags(1) + id(4) + data(8) = 16 bytes.
    /// </summary>
    private const int CanMessageMinSize = 16;

    /// <summary>Minimum size of a BLF Type 2 CAN error payload: channel(2) + length(2) + reserved(4) = 8 bytes.</summary>
    private const int CanErrorMinSize = 8;

    /// <summary>Minimum size of a BLF Type 3 CAN overload payload: channel(2) + reserved(2) = 4 bytes.</summary>
    private const int CanOverloadMinSize = 4;

    /// <summary>
    /// Minimum size of a BLF Type 73 CAN error ext payload.
    /// channel(2) + length(2) + flags(4) + ecc(1) + position(1) + dlc(2) +
    /// frameLength(2) + id(2) + extFlags(2) + extEcc(1) + reserved(1) + data(8) = 28 bytes.
    /// </summary>
    private const int CanErrorExtMinSize = 20;

    /// <summary>
    /// Minimum size of a BLF Type 100 CAN FD message payload (header only, without data):
    /// channel(2) + dlc(1) + validDataBytes(1) + txCount(4) + id(4) +
    /// frameLength(4) + blfFlags(4) + fdFlags(1) + reserved(3) = 24 bytes.
    /// </summary>
    private const int CanFdMessageHeaderSize = 24;

    /// <summary>
    /// Minimum size of a BLF Type 101 CAN FD Message 64 payload (header without data):
    /// channel(1) + dlc(1) + validDataLength(1) + txCount(1) + id(4) +
    /// frameLength(4) + flags(4) + brsDelay(1) + reserved(1) = 18 bytes.
    /// </summary>
    private const int CanFdMessage64HeaderSize = 18;

    /// <summary>SocketCAN classic frame total size: header(8) + data(8).</summary>
    private const int SocketCanClassicSize = 16;

    /// <summary>SocketCAN FD frame header size (before data).</summary>
    private const int SocketCanFdHeaderSize = 8;

    /// <summary>Classic CAN maximum data length.</summary>
    private const int CanMaxDataLength = 8;

    /// <summary>CAN FD maximum data length.</summary>
    private const int CanFdMaxDataLength = 64;

    #endregion

    #region Public API — Classic CAN

    /// <summary>
    /// Parses a BLF Type 1 (CAN_MESSAGE) payload into a SocketCAN classic frame.
    ///
    /// Payload layout (all little-endian):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     dlc
    ///   [3]     flags  (bit 0x04 = EFF, bit 0x10 = RTR)
    ///   [4..8)  id (u32 LE, 29-bit arbitration ID)
    ///   [8..16) data (8 bytes, zero-padded)
    /// </code>
    /// </summary>
    internal static bool TryParseCanMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => TryParseCanMessageCore(payload, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 86 (CAN_MESSAGE2) payload into a SocketCAN classic frame.
    /// The first 16 bytes are identical to Type 1; any trailing bytes are ignored.
    /// </summary>
    internal static bool TryParseCanMessage2(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => TryParseCanMessageCore(payload, out frame, out channel);

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

        if (payload.Length < CanErrorMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
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

        if (payload.Length < CanOverloadMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        // CAN overload is a bus-level condition; produce a generic SocketCAN error frame
        frame = BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 73 (CAN_ERROR_EXT) payload into a SocketCAN error frame.
    ///
    /// Payload layout (minimum 20 bytes, little-endian):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2..4)  length (u16 LE)
    ///   [4..8)  flags (u32 LE) — CAN error type bitmask
    ///   [8]     ecc — error capture code
    ///   [9]     position — error position
    ///   [10..12) dlc (u16 LE)
    ///   [12..14) frameLength (u16 LE)
    ///   [14..16) id (u16 LE)
    ///   [16..18) extFlags (u16 LE)
    ///   [18]    extEcc
    ///   [19]    reserved
    ///   [20..)  data (8 bytes)
    /// </code>
    /// </summary>
    internal static bool TryParseCanErrorExt(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < CanErrorExtMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Public API — CAN FD

    /// <summary>
    /// Parses a BLF Type 100 (CAN_FD_MESSAGE) payload into a SocketCAN FD frame.
    ///
    /// Payload layout (all little-endian):
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2]      dlc (4-bit DLC code, looked up via CanFdDlcToLength)
    ///   [3]      validDataBytes (actual payload byte count)
    ///   [4..8)   txCount (u32 LE, ignored)
    ///   [8..12)  id (u32 LE, 29-bit arbitration ID)
    ///   [12..16) frameLength (u32 LE, total struct size including data)
    ///   [16..20) blfFlags (u32 LE; bit 0x04 = EFF, bit 0x10 = RTR)
    ///   [20]     fdFlags (BLF FD flags: bit 0x01=EDL, bit 0x02=BRS, bit 0x04=ESI)
    ///   [21..24) reserved
    ///   [24..)   data (validDataBytes bytes)
    /// </code>
    /// </summary>
    internal static bool TryParseCanFdMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < CanFdMessageHeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte dlc = payload[2];
        byte validDataBytes = payload[3];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        uint blfFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        byte fdFlags = payload[20];

        // Clamp validDataBytes to what is declared and what is present
        byte dataLen = BlfConstants.CanFdDlcToLength[Math.Min(dlc, (byte)15)];
        int actualDataLen = Math.Min((int)Math.Min(validDataBytes, dataLen), CanFdMaxDataLength);
        int available = Math.Max(0, payload.Length - CanFdMessageHeaderSize);
        actualDataLen = Math.Min(actualDataLen, available);

        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((blfFlags & BlfConstants.BlfCanMessageFlagEff) != 0 || rawId > 0x7FF)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        if ((blfFlags & BlfConstants.CanFlagRtr) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
        }

        // Map BLF FD flags → SocketCAN FD flags
        byte socketFdFlags = 0;
        if ((fdFlags & BlfConstants.BlfCanFdEdl) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdFdf; // EDL → FDF
        }

        if ((fdFlags & BlfConstants.BlfCanFdBrs) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdBrs;
        }

        if ((fdFlags & BlfConstants.BlfCanFdEsi) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdEsi;
        }

        // SocketCAN FD: id(4 BE) + len(1, byte count) + fd_flags(1) + reserved(2) + data(64, zero-padded)
        frame = new byte[SocketCanFdHeaderSize + CanFdMaxDataLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)actualDataLen;
        frame[5] = socketFdFlags;
        // frame[6..7] = 0 (reserved)

        if (actualDataLen > 0)
        {
            payload.Slice(CanFdMessageHeaderSize, actualDataLen).CopyTo(frame.AsSpan(8));
        }

        return true;
    }

    /// <summary>
    /// Parses a BLF Type 101 (CAN_FD_MESSAGE_64) payload into a SocketCAN FD frame.
    ///
    /// Payload layout (all little-endian), note channel is 1 byte (not 2):
    /// <code>
    ///   [0]      channel (u8)
    ///   [1]      dlc (4-bit DLC code)
    ///   [2]      validDataLength (actual payload byte count)
    ///   [3]      txCount (u8, ignored)
    ///   [4..8)   id (u32 LE, 29-bit arbitration ID)
    ///   [8..12)  frameLength (u32 LE, total struct size including data)
    ///   [12..16) flags (u32 LE; bit 0x1000=EDL, 0x2000=BRS, 0x4000=ESI)
    ///   [16]     brsDelay (u8, ignored)
    ///   [17]     reserved (u8)
    ///   [18..)   data (validDataLength bytes)
    /// </code>
    /// </summary>
    internal static bool TryParseCanFdMessage64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < CanFdMessage64HeaderSize)
        {
            return false;
        }

        // NOTE: channel is a single byte in this struct variant
        channel = payload[0];
        byte dlc = payload[1];
        byte validDataLength = payload[2];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);

        byte dataLen = BlfConstants.CanFdDlcToLength[Math.Min(dlc, (byte)15)];
        int actualDataLen = Math.Min((int)Math.Min(validDataLength, dataLen), CanFdMaxDataLength);
        int available = Math.Max(0, payload.Length - CanFdMessage64HeaderSize);
        actualDataLen = Math.Min(actualDataLen, available);

        uint socketCanId = rawId & 0x1FFF_FFFF;
        if (rawId > 0x7FF)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        // Map Type 101 FD flags to SocketCAN FD flags (different bit positions)
        byte socketFdFlags = 0;
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

        frame = new byte[SocketCanFdHeaderSize + CanFdMaxDataLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)actualDataLen;
        frame[5] = socketFdFlags;

        if (actualDataLen > 0)
        {
            payload.Slice(CanFdMessage64HeaderSize, actualDataLen).CopyTo(frame.AsSpan(8));
        }

        return true;
    }

    /// <summary>
    /// Parses a BLF Type 104 (CAN_FD_ERROR_64) payload into a SocketCAN error frame.
    /// Layout is similar to Type 101 with additional error code fields; the first 4 bytes
    /// identify the channel.
    /// </summary>
    internal static bool TryParseCanFdError64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < CanFdMessage64HeaderSize)
        {
            return false;
        }

        channel = payload[0];
        frame = BuildSocketCanErrorFrame(BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Core parser for Type 1 and Type 86 CAN messages (identical first-16-byte layout).
    /// Reads channel, dlc, flags, id, and up to 8 data bytes.
    /// </summary>
    private static bool TryParseCanMessageCore(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < CanMessageMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte dlc = payload[2];
        byte flags = payload[3];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        byte dataLen = BlfConstants.CanDlcToLength[Math.Min(dlc, (byte)15)];

        // Reconstruct SocketCAN ID
        uint socketCanId = rawId & 0x1FFF_FFFF;

        // Set EFF if the BLF EFF flag is present OR if the raw 11-bit range is exceeded
        if ((flags & BlfConstants.BlfCanMessageFlagEff) != 0 || rawId > 0x7FF)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        if ((flags & BlfConstants.CanFlagRtr) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
        }

        // SocketCAN classic frame: id(4 BE) + dlc(1) + fd_flags(0 for classic) + reserved(2) + data(8)
        frame = new byte[SocketCanClassicSize];
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = dlc;
        // frame[5] = 0 (fd_flags = classic CAN)
        // frame[6..7] = 0 (reserved)

        // Copy data — payload[8..16] always exists (guaranteed by CanMessageMinSize = 16)
        int copyLen = Math.Min((int)dataLen, CanMaxDataLength);
        payload.Slice(8, copyLen).CopyTo(frame.AsSpan(8));

        return true;
    }

    /// <summary>
    /// Builds a minimal 16-byte SocketCAN error frame with the given error flag.
    /// </summary>
    private static byte[] BuildSocketCanErrorFrame(uint socketCanErrId)
    {
        byte[] errorFrame = new byte[SocketCanClassicSize];
        BinaryPrimitives.WriteUInt32BigEndian(errorFrame, socketCanErrId);
        return errorFrame;
    }

    #endregion
}
