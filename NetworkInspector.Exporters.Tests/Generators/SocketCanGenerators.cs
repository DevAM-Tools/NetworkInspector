// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building SocketCAN frame data for exporter tests.
/// Produces CAN classic (16 bytes) and CAN FD (72 bytes) frames.
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
}
