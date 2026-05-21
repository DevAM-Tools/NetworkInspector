// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building DLT_LIN (LINKTYPE_LIN = 212) frame data for exporter tests.
/// Produces frames matching the wire format used by the <see cref="Sources.Blf.Format.Objects.LinParser"/>.
///
/// DLT_LIN layout:
///   [pid(1)|length(1)|data(0–8)|checksum(1)|errors(1)]
///
/// The PID (Protected Identifier) contains the 6-bit frame ID plus two parity bits:
///   P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4  (even parity)
///   P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5) (odd parity)
///   PID = P1:P0:ID[5:0]
/// </summary>
internal static class LinGenerators
{
    /// <summary>Maximum LIN data length.</summary>
    private const int MaxLinDataLength = 8;

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
        int dataLength = Math.Min(data.Length, MaxLinDataLength);

        // DLT_LIN layout: pid(1) + length(1) + data(dataLength) + checksum(1) + errors(1)
        byte[] frame = new byte[2 + dataLength + 2];

        // Byte 0: PID (protected identifier with parity bits)
        frame[0] = ComputePid(frameId);

        // Byte 1: data length
        frame[1] = (byte)dataLength;

        // Data
        if (dataLength > 0)
        {
            data[..dataLength].CopyTo(frame.AsSpan(2));
        }

        // Trailer: checksum + errors
        frame[2 + dataLength] = checksum;
        frame[2 + dataLength + 1] = errors;

        return frame;
    }

    /// <summary>
    /// Computes the LIN PID (Protected Identifier) from a 6-bit frame ID.
    /// Uses the same algorithm as <see cref="Sources.Blf.Format.Objects.LinParser.ComputePid"/>.
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
