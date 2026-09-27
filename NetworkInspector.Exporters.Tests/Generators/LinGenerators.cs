// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Builds DLT_LIN (link type 212) frame bytes for exporter tests.
/// 8-byte header, then data padded to 4 or 8 bytes. Byte 5 is the 6-bit frame id
/// (no protected-ID parity). Byte 4 bits 1-0 default to classic checksum type (1).
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
    /// <param name="checksumTypeBits">DLT checksum-type bits 1-0 (1 = classic, 2 = enhanced).</param>
    internal static byte[] BuildLinFrame(
        byte frameId,
        ReadOnlySpan<byte> data,
        byte checksum = 0,
        byte errors = 0,
        byte checksumTypeBits = 1)
    {
        int dataLength = Math.Min(data.Length, _MaxLinDataLength);
        int dataPad = dataLength <= 4 ? 4 : 8;
        byte[] frame = new byte[8 + dataPad];
        frame[0] = 1;
        frame[4] = (byte)((dataLength << 4) | (checksumTypeBits & 0x03));
        frame[5] = (byte)(frameId & 0x3F);
        frame[6] = checksum;
        frame[7] = errors;
        if (dataLength > 0)
        {
            data[..dataLength].CopyTo(frame.AsSpan(8));
        }

        return frame;
    }
}
