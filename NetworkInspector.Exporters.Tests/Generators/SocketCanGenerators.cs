// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building SocketCAN frame data for exporter tests.
/// Produces CAN classic (16 bytes), CAN FD (72 bytes), and CAN XL (12 + payload bytes) frames.
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
    internal static byte[] BuildCanClassic(uint canId, ReadOnlySpan<byte> data, bool extended = false)
    {
        int dlc = Math.Min(data.Length, 8);
        uint id = canId;
        if (extended)
        {
            id |= 0x8000_0000; // EFF flag
        }

        byte[] frame = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        // frame[5] = 0 (fd_flags = classic)
        // frame[6..7] = 0 (reserved)
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>
    /// Builds a SocketCAN FD frame (72 bytes):
    /// id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-64, zero-padded to 64).
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

        byte[] frame = new byte[72];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        frame[5] = fdFlags;
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>
    /// Builds a minimal SocketCAN CAN XL frame (12-byte header + payload).
    /// Wire layout (LINKTYPE_CAN_SOCKETCAN):
    /// Prio/VCID(4 BE) + Flags(1, XLF=0x80 always set) + Sdt(1) + Len(2 LE) + Af(4 LE) + Data.
    /// </summary>
    /// <param name="priority">11-bit CAN XL priority (bits 0–10).</param>
    /// <param name="data">Payload bytes (0–2048).</param>
    internal static byte[] BuildCanXl(uint priority, ReadOnlySpan<byte> data)
    {
        int payloadLen = Math.Min(data.Length, 2048);
        byte[] frame = new byte[12 + payloadLen];

        // Priority/VCID word: big-endian, priority in bits 0–10.
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), priority & 0x7FFu);

        // Flags: XLF (0x80) always set — this is the discriminator that distinguishes
        // CAN XL from classic/FD on the same LinkType.CanSocketcan link type.
        frame[4] = 0x80;

        // SDU type: 0 (default)
        frame[5] = 0;

        // Payload length: little-endian u16
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)payloadLen);

        // Acceptance field: little-endian u32, left as 0 for test frames
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 0u);

        // Data payload
        data[..payloadLen].CopyTo(frame.AsSpan(12));
        return frame;
    }
}
