// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building SocketCAN frame data for exporter tests.
/// Produces CAN classic (16 bytes), CAN FD (8 + payload), and CAN XL (12 + payload bytes) frames.
/// </summary>
internal static class SocketCanGenerators
{
    /// <summary>
    /// Builds a SocketCAN classic frame (16 bytes):
    /// id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-8, zero-padded to 8).
    /// </summary>
    /// <param name="canId">CAN arbitration ID (11 or 29 bit).</param>
    /// <param name="data">CAN data bytes (0–8).</param>
    /// <param name="extended">If true, sets the EFF bit (29-bit ID).</param>
    /// <param name="rtr">If true, sets the SocketCAN RTR bit (bit 30).</param>
    internal static byte[] BuildCanClassic(uint canId, ReadOnlySpan<byte> data, bool extended = false, bool rtr = false)
    {
        int dlc = Math.Min(data.Length, 8);
        uint id = canId;
        if (extended)
        {
            id |= 0x8000_0000; // EFF flag
        }

        if (rtr)
        {
            id |= 0x4000_0000;
        }

        byte[] frame = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>
    /// Builds a SocketCAN FD frame (8 + actual data length, not padded to 64):
    /// id(4 BE) + len(1) + fd_flags(1) + reserved(2) + data(0-64).
    /// </summary>
    /// <param name="canId">CAN arbitration ID.</param>
    /// <param name="data">CAN data bytes (0–64).</param>
    /// <param name="extended">If true, sets the EFF bit.</param>
    /// <param name="brs">If true, sets the BRS (Bit Rate Switch) flag.</param>
    internal static byte[] BuildCanFd(
        uint canId, ReadOnlySpan<byte> data, bool extended = false, bool brs = false)
    {
        int dlc = Math.Min(data.Length, 64);
        uint id = canId;
        if (extended)
        {
            id |= 0x8000_0000;
        }

        // SocketCAN canfd_frame.flags bits per Linux <linux/can.h>:
        //   CANFD_BRS = 0x01 (Bit Rate Switch)
        //   CANFD_ESI = 0x02 (Error State Indicator)
        //   CANFD_FDF = 0x04 (FD Format)
        byte fdFlags = 0x04; // FDF (FD format indicator)
        if (brs)
        {
            fdFlags |= 0x01; // BRS
        }

        byte[] frame = new byte[8 + dlc];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        frame[5] = fdFlags;
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>
    /// Builds a SocketCAN CAN XL frame (12-byte header + payload).
    /// Wire layout (LINKTYPE_CAN_SOCKETCAN / Wireshark <c>blf_read_canxlchannelframe</c>):
    /// <c>[0]=0, vcid, priority(2 BE), flags (XLF=0x80), sdt, len(2 LE), af(4 LE), data</c>.
    /// </summary>
    /// <param name="priority">11-bit CAN XL priority (bits 0–10).</param>
    /// <param name="data">Payload bytes (0–2048).</param>
    /// <param name="vcid">Virtual CAN network ID stored at byte 1.</param>
    /// <param name="sdt">Service data unit type at byte 5.</param>
    /// <param name="acceptanceField">Acceptance field (little-endian u32 at bytes 8–11).</param>
    /// <param name="sec">When true, sets SocketCAN SEC (0x01) next to XLF.</param>
    /// <param name="rrs">When true, sets SocketCAN RRS (0x02) next to XLF.</param>
    internal static byte[] BuildCanXl(
        uint priority,
        ReadOnlySpan<byte> data,
        byte vcid = 0,
        byte sdt = 0,
        uint acceptanceField = 0,
        bool sec = false,
        bool rrs = false)
    {
        int payloadLen = Math.Min(data.Length, 2048);
        byte[] frame = new byte[12 + payloadLen];

        frame[1] = vcid;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)(priority & 0x7FFu));

        byte flags = 0x80;
        if (sec)
        {
            flags |= 0x01;
        }

        if (rrs)
        {
            flags |= 0x02;
        }

        frame[4] = flags;
        frame[5] = sdt;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), acceptanceField);
        data[..payloadLen].CopyTo(frame.AsSpan(12));
        return frame;
    }
}
