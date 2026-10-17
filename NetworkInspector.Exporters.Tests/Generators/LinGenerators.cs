// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Builds DLT_LIN (link type 212) frame bytes for exporter tests.
/// 8-byte header, then data padded to 4 or 8 bytes. Byte 5 is the protected identifier (6-bit frame id plus the
/// two parity bits), the form the BLF and ASC readers produce, so round trips compare byte for byte.
/// Byte 4 bits 1-0 default to classic checksum type (1).
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
        frame[5] = _ProtectedId(frameId);
        frame[6] = checksum;
        frame[7] = errors;
        if (dataLength > 0)
        {
            data[..dataLength].CopyTo(frame.AsSpan(8));
        }

        return frame;
    }

    /// <summary>
    /// LIN 2.x protected identifier: P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4 (bit 6), P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5) (bit 7).
    /// Written out independently of the readers' helper so the tests check the rule, not a shared implementation.
    /// </summary>
    private static byte _ProtectedId(byte frameId)
    {
        int id = frameId & 0x3F;
        int p0 = (id ^ (id >> 1) ^ (id >> 2) ^ (id >> 4)) & 1;
        int p1 = ~((id >> 1) ^ (id >> 3) ^ (id >> 4) ^ (id >> 5)) & 1;
        return (byte)(id | (p0 << 6) | (p1 << 7));
    }
}
