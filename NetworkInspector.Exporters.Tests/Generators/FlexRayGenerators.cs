// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building DLT_FLEXRAY (LINKTYPE_FLEXRAY = 210) frame data for exporter tests.
/// Produces frames matching the wire format used by the <see cref="Sources.Blf.Format.Objects.FlexRayParser"/>.
///
/// DLT_FLEXRAY layout (7-byte header + payload):
///   [channel(1)|type_flags(1)|frame_id(2 BE)|cycle(1)|header_crc(2 BE)|data...]
///
/// Type flags byte (bit-packed):
///   bit 7: payload preamble indicator
///   bit 6: null frame indicator
///   bit 5: sync frame indicator
///   bit 4: startup frame indicator
///   bits 0–3: reserved
/// </summary>
internal static class FlexRayGenerators
{
    /// <summary>DLT_FLEXRAY header size: 7 bytes.</summary>
    private const int DltFlexRayHeaderSize = 7;

    /// <summary>
    /// Builds a DLT_FLEXRAY frame with the specified parameters.
    /// </summary>
    /// <param name="channel">FlexRay channel (0 = A, 1 = B).</param>
    /// <param name="frameId">11-bit FlexRay slot/frame ID.</param>
    /// <param name="cycle">Cycle counter (0–63).</param>
    /// <param name="headerCrc">FlexRay header CRC.</param>
    /// <param name="data">Payload data bytes.</param>
    /// <param name="sync">Whether the sync frame indicator flag is set.</param>
    /// <param name="startup">Whether the startup frame indicator flag is set.</param>
    internal static byte[] BuildFlexRayFrame(
        byte channel, ushort frameId, byte cycle, ushort headerCrc,
        ReadOnlySpan<byte> data, bool sync = false, bool startup = false)
    {
        byte[] frame = new byte[DltFlexRayHeaderSize + data.Length];

        // Byte 0: channel
        frame[0] = channel;

        // Byte 1: type_flags
        byte typeFlags = 0;
        if (sync)
        {
            typeFlags |= 0x20;
        }
        if (startup)
        {
            typeFlags |= 0x10;
        }
        frame[1] = typeFlags;

        // Bytes 2-3: frame ID (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), frameId);

        // Byte 4: cycle
        frame[4] = cycle;

        // Bytes 5-6: header CRC (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(5), headerCrc);

        // Payload
        data.CopyTo(frame.AsSpan(DltFlexRayHeaderSize));

        return frame;
    }
}
