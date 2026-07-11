// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Helpers for building LIN frames in DLT_LIN format (per Wireshark packet-lin.h / packet-lin.c).
/// Wire layout (8-byte fixed header followed by data payload):
/// <code>
/// Byte  0:    Message Format Revision (1)
/// Bytes 1-3:  Reserved (0x00 0x00 0x00)
/// Byte  4:    (payloadLength &lt;&lt; 4) | (msgType &lt;&lt; 2) | checksumType
/// Byte  5:    PID = parity[7:6] | frameId[5:0]
/// Byte  6:    Checksum
/// Byte  7:    Error Flags (0 = no errors)
/// Bytes 8+:   Data payload
/// </code>
/// </summary>
internal static class LinFrameBuilder
{
    /// <summary>
    /// Builds a standard LIN frame (msgType = 0 = Frame), computing the protected
    /// ID parity bits and the checksum (classic or enhanced) automatically.
    /// </summary>
    /// <param name="frameId">6-bit frame identifier (0..63).</param>
    /// <param name="data">Payload bytes (0..8).</param>
    /// <param name="checksumType">1=Classic, 2=Enhanced (default).</param>
    /// <param name="errorFlags">Error flags byte (default 0 = no errors).</param>
    internal static byte[] Build(byte frameId, ReadOnlySpan<byte> data, byte checksumType = 2, byte errorFlags = 0)
    {
        if (frameId > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }

        byte pid = _WithParity(frameId);
        // Byte 4: payload_length[7:4] | msg_type[3:2] | checksum_type[1:0]
        // msg_type = 0 (Frame), so bits 3-2 are 00.
        byte byte4 = (byte)((data.Length << 4) | (checksumType & 0x03));
        byte checksum = _ComputeChecksum(data, pid, checksumType);

        byte[] frame = new byte[8 + data.Length];
        frame[0] = 1;       // msg_format_rev = 1
        frame[1] = 0;       // reserved
        frame[2] = 0;       // reserved
        frame[3] = 0;       // reserved
        frame[4] = byte4;
        frame[5] = pid;
        frame[6] = checksum;
        frame[7] = errorFlags;
        data.CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>Computes the 8-bit Protected ID (frame id + 2 parity bits per ISO 17987).</summary>
    private static byte _WithParity(byte frameId)
    {
        int id0 = (frameId >> 0) & 1;
        int id1 = (frameId >> 1) & 1;
        int id2 = (frameId >> 2) & 1;
        int id3 = (frameId >> 3) & 1;
        int id4 = (frameId >> 4) & 1;
        int id5 = (frameId >> 5) & 1;
        int p0 = id0 ^ id1 ^ id2 ^ id4;
        int p1 = (id1 ^ id3 ^ id4 ^ id5) ^ 1; // NOT of XOR
        return (byte)(frameId | (p0 << 6) | (p1 << 7));
    }

    /// <summary>
    /// Computes the LIN checksum using carry-add (mod 255) then bit-inversion.
    /// Classic (type 1): sums data bytes only.
    /// Enhanced (type 2): includes the PID byte in the sum (ISO 17987).
    /// </summary>
    private static byte _ComputeChecksum(ReadOnlySpan<byte> data, byte pid, byte type)
    {
        uint sum = type == 2 ? pid : 0u;
        foreach (byte b in data)
        {
            sum += b;
            if (sum > 0xFF)
            {
                sum = (sum & 0xFF) + 1;
            }
        }
        return (byte)(~sum & 0xFF);
    }
}
