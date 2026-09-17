// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Builds DLT_LIN (link type 212) frame bytes for exporter tests.
/// Layout matches Wireshark <c>packet-lin.h</c> / <c>blf_read_linmessage</c>:
/// 8-byte header, then data padded to 4 or 8 bytes.
/// </summary>
internal static class LinGenerators
{
    /// <summary>Maximum LIN data length.</summary>
    private const int _MaxLinDataLength = 8;

    /// <summary>
    /// Builds a DLT_LIN frame with the specified parameters.
    /// </summary>
    /// <param name="frameId">6-bit LIN frame identifier (0–63).</param>
    /// <param name="data">Payload data bytes (up to 8).</param>
    /// <param name="checksum">LIN checksum byte.</param>
    /// <param name="errors">Error flags byte (0 = no errors).</param>
    internal static byte[] BuildLinFrame(
        byte frameId, ReadOnlySpan<byte> data, byte checksum = 0, byte errors = 0)
    {
        int dataLength = Math.Min(data.Length, _MaxLinDataLength);
        int dataPad = dataLength <= 4 ? 4 : 8;
        byte[] frame = new byte[8 + dataPad];
        frame[0] = 1;
        frame[4] = (byte)(dataLength << 4);
        frame[5] = ComputePid(frameId);
        frame[6] = checksum;
        frame[7] = errors;
        if (dataLength > 0)
        {
            data[..dataLength].CopyTo(frame.AsSpan(8));
        }

        return frame;
    }

    /// <summary>
    /// Computes the LIN PID (Protected Identifier) from a 6-bit frame ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ComputePid(byte id)
    {
        int frameId = id & 0x3F;

        // P0: even parity of bits 0,1,2,4
        int p0 = ((frameId >> 0) ^ (frameId >> 1) ^ (frameId >> 2) ^ (frameId >> 4)) & 1;

        // P1: inverted even parity of bits 1,3,4,5
        int p1 = (~((frameId >> 1) ^ (frameId >> 3) ^ (frameId >> 4) ^ (frameId >> 5))) & 1;

        return (byte)(frameId | (p0 << 6) | (p1 << 7));
    }
}
