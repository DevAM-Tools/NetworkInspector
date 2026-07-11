// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF LIN object payloads into DLT_LIN frame bytes.
///
/// Output format (DLT_LIN, variable length):
/// <code>
///   [0]   PID  (6-bit frame ID + P0/P1 parity bits per LIN 2.x spec)
///   [1]   length (DLC, 0–8)
///   [2..2+length)  data bytes
///   [2+length]     checksum
///   [2+length+1]   errors (0 = no error, non-zero = error flags)
/// </code>
///
/// Supported types:
/// <list type="bullet">
///   <item>Type 11  (<c>LIN_MESSAGE</c>)    — V1 format, 12-byte payload</item>
///   <item>Type 57  (<c>LIN_MESSAGE2</c>)   — V2 format, 132-byte nested struct</item>
///   <item>Types 12/14/15 (V1 errors)       — CRC, RCV, SND error frames</item>
///   <item>Types 58/60/61 (V2 errors)       — CRC, RCV, SND error frames</item>
/// </list>
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
    /// Minimum Type 57 payload size (blf_linmessage2_t — 132-byte packed struct):
    /// The channel is at offset 12, id at 37, dlc at 38, data at 112..120, checksum at 120.
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

    /// <summary>Maximum LIN data length.</summary>
    private const int _MaxLinDataLength = 8;

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
    /// </code>
    ///
    /// Checksum is not present in the V1 payload; the DLT_LIN checksum byte is set to 0.
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
        byte rawId = (byte)(payload[2] & 0x3F); // strip parity if any
        byte dlc = payload[3];
        int dataLen = Math.Min((int)dlc, _MaxLinDataLength);

        byte pid = _ComputeLinPid(rawId);

        frame = _BuildDltLinFrame(pid, dataLen, payload.Length >= 4 + dataLen
            ? payload.Slice(4, dataLen)
            : payload[4..], checksum: 0, errors: 0);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 57 (LIN_MESSAGE2) payload into a DLT_LIN frame.
    ///
    /// The <c>blf_linmessage2_t</c> is a 132-byte packed nested struct. Relevant fields:
    /// <code>
    ///   [12..14) channel (u16 LE) — inside blf_linbusevent
    ///   [37]     id (6-bit frame ID) — inside blf_linmessagedescriptor
    ///   [38]     dlc — inside blf_linmessagedescriptor
    ///   [112..120) data (8 bytes) — inside blf_linmessage2
    ///   [120]    crc low byte (checksum) — blf_linmessage2.crc low byte
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
        byte dlc = payload[38];
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
        byte pid = _ComputeLinPid(rawId);

        // Error frames have no data; error type goes into the errors field
        frame = _BuildDltLinFrame(pid, dlc: 0, data: [], checksum: 0, errors: errorType);
        return true;
    }

    /// <summary>
    /// Parses a BLF LIN V2 error object (Types 58, 60, 61) into a DLT_LIN error frame.
    ///
    /// Uses the same nested blf_linmessage2_t layout as Type 57. The minimum requirement
    /// is that the <c>id</c> field at offset 37 is accessible.
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
        byte pid = _ComputeLinPid(rawId);

        frame = _BuildDltLinFrame(pid, dlc: 0, data: [], checksum: 0, errors: errorType);
        return true;
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
    /// Builds a DLT_LIN frame: PID(1) + dlc(1) + data(dlc) + checksum(1) + errors(1).
    /// </summary>
    private static byte[] _BuildDltLinFrame(
        byte pid, int dlc, ReadOnlySpan<byte> data, byte checksum, byte errors)
    {
        int frameLen = 2 + dlc + 2;
        byte[] frame = new byte[frameLen];
        frame[0] = pid;
        frame[1] = (byte)dlc;

        int copyLen = Math.Min(dlc, data.Length);
        if (copyLen > 0)
        {
            data[..copyLen].CopyTo(frame.AsSpan(2));
        }

        frame[2 + dlc] = checksum;
        frame[2 + dlc + 1] = errors;
        return frame;
    }

    #endregion
}
