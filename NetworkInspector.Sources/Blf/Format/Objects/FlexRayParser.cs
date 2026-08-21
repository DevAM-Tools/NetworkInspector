// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF FlexRay object payloads into LINKTYPE_FLEXRAY frame bytes.
///
/// Output format (LINKTYPE_FLEXRAY / DLT 210, variable length):
/// <code>
///   Byte 0:     Measurement Header ([7] CH, [6:0] Type Index)
///   Byte 1:     Error Flags
///   Bytes 2-6:  FlexRay Frame Header (ISO 17458-2 Section 8)
///   Bytes 7+:   Payload data
/// </code>
///
/// Supported types:
/// <list type="bullet">
///   <item>Type 29  (<c>FLEXRAY_DATA</c>)               — simple data object</item>
///   <item>Type 41  (<c>FLEXRAY_MESSAGE</c>)            — full message object</item>
///   <item>Type 50  (<c>FLEXRAY_RCVMESSAGE</c>)         — receive message with 44-byte header</item>
///   <item>Type 66  (<c>FLEXRAY_RCVMESSAGE_EX</c>)      — extended receive message</item>
/// </list>
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class FlexRayParser
{
    #region Constants

    /// <summary>
    /// Minimum Type 29 payload size: channel(2)+mux(1)+len(1)+messageId(2)+crc(2)+muxId(1) = 9 bytes.
    /// </summary>
    private const int _FlexRayDataMinSize = 9;

    /// <summary>
    /// Minimum Type 41 payload size (blf_flexraymessage_t):
    /// channel(2)+dir(1)+lowTime(1)+fpgaTick(4)+fpgaTickOverflow(4)+clientIndex(4)+
    /// clusterTime(4)+frameId(2)+headerCrc(2)+frameState(2)+length(1)+cycle(1)+
    /// headerBitMask(1)+reserved1(1)+reserved2(2) = 32 bytes.
    /// </summary>
    private const int _FlexRayMessageMinSize = 32;

    /// <summary>
    /// Minimum Type 50 payload size (blf_flexrayrcvmessage_t, 44-byte header):
    /// channel(2)+version(2)+channelMask(2)+dir(2)+clientIndex(4)+clusterNo(4)+
    /// frameId(2)+headerCrc1(2)+headerCrc2(2)+payloadLength(2)+payloadLengthValid(2)+
    /// cycle(2)+tag(4)+data(4)+frameFlags(4)+appParameter(4) = 44 bytes.
    /// </summary>
    private const int _FlexRayRcvMessageHeaderSize = 44;

    /// <summary>
    /// Minimum Type 66 payload size (blf_flexrayrcvmessageex_t):
    /// Similar to Type 50 but with extra version/TS fields. The frameId is at offset 40,
    /// which requires at least 42 bytes of header before data.
    /// </summary>
    private const int _FlexRayRcvMessageExMinSize = 60;

    #endregion

    #region Public API

    /// <summary>
    /// Parses a BLF Type 29 (FLEXRAY_DATA) payload into a DLT_FLEXRAY frame.
    ///
    /// Payload layout (all little-endian):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     mux (channel bitmask: bit 0 = A, bit 1 = B)
    ///   [3]     len (data length in bytes)
    ///   [4..6)  messageId (frame ID, u16 LE)
    ///   [6..8)  crc (header CRC, u16 LE)
    ///   [8]     muxId (ignored)
    ///   [9..)   data
    /// </code>
    /// </summary>
    internal static bool TryParseFlexRayData(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayDataMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte mux = payload[2];
        int dataLen = payload[3];
        ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort headerCrc = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);

        // mux bit 0 = channel A, bit 1 = channel B
        byte subChannel = (mux & 0x02) != 0 ? (byte)1 : (byte)0;

        int available = Math.Max(0, payload.Length - _FlexRayDataMinSize);
        if (!_TryResolvePayloadLength(dataLen, available, out int actualDataLen))
        {
            return false;
        }

        ReadOnlySpan<byte> data = payload.Slice(_FlexRayDataMinSize, actualDataLen);

        frame = _BuildLinkTypeFrame(
            subChannel, ppi: false, nfi: true, sfi: false, stfi: false,
            frameId, cycle: 0, headerCrc, data);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 41 (FLEXRAY_MESSAGE) payload into a DLT_FLEXRAY frame.
    ///
    /// Payload layout (little-endian fields):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     dir
    ///   [3]     lowTime
    ///   [4..8)  fpgaTick (u32 LE)
    ///   [8..12) fpgaTickOverflow (u32 LE)
    ///   [12..16) clientIndex (u32 LE)
    ///   [16..20) clusterTime (u32 LE)
    ///   [20..22) frameId (u16 LE)
    ///   [22..24) headerCrc (u16 LE)
    ///   [24..26) frameState (u16 LE)
    ///   [26]    length (data length in bytes)
    ///   [27]    cycle
    ///   [28]    headerBitMask (flags: bit 1=payload preamble, bit 2=null, bit 3=sync, bit 4=startup)
    ///   [29]    reserved1
    ///   [30..32) reserved2 (u16 LE)
    ///   [32..)  data
    /// </code>
    /// </summary>
    internal static bool TryParseFlexRayMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayMessageMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        ushort headerCrc = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
        int dataLen = payload[26];
        byte cycle = payload[27];
        byte headerBitMask = payload[28];

        FlexRayLinkTypeFrame.MapBlfHeaderBitMask(headerBitMask, out bool ppi, out bool nfi, out bool sfi, out bool stfi);

        int available = Math.Max(0, payload.Length - _FlexRayMessageMinSize);
        if (!_TryResolvePayloadLength(dataLen, available, out int actualDataLen))
        {
            return false;
        }

        frame = _BuildLinkTypeFrame(
            subChannel: 0, ppi, nfi, sfi, stfi, frameId, cycle, headerCrc,
            payload.Slice(_FlexRayMessageMinSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 50 (FLEXRAY_RCVMESSAGE) payload into a DLT_FLEXRAY frame.
    ///
    /// The 44-byte <c>blf_flexrayrcvmessage_t</c> header layout (all little-endian):
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2..4)   version (u16 LE)
    ///   [4..6)   channelMask (u16 LE; bit 0 = A, bit 1 = B)
    ///   [6..8)   dir (u16 LE; 0=RX, 1=TX)
    ///   [8..12)  clientIndex (u32 LE)
    ///   [12..16) clusterNo (u32 LE)
    ///   [16..18) frameId (u16 LE)
    ///   [18..20) headerCrc1 (u16 LE)
    ///   [20..22) headerCrc2 (u16 LE)
    ///   [22..24) payloadLength (u16 LE)
    ///   [24..26) payloadLengthValid (u16 LE)
    ///   [26..28) cycle (u16 LE; high byte = reserved)
    ///   [28..32) tag (u32 LE)
    ///   [32..36) data (u32 LE, field name, not payload)
    ///   [36..40) frameFlags (u32 LE; bit 1=payload preamble, bit 2=null, bit 3=sync, bit 4=startup)
    ///   [40..44) appParameter (u32 LE)
    ///   [44..)   FlexRay data payload
    /// </code>
    /// </summary>
    internal static bool TryParseFlexRayRcvMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayRcvMessageHeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        ushort channelMask = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
        ushort headerCrc = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]);
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
        byte cycle = payload[26]; // low byte of cycle u16
        uint frameFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload[36..]);

        // Determine sub-channel from channelMask (bit 0 = A, bit 1 = B)
        byte subChannel = (channelMask & 0x02) != 0 ? (byte)1 : (byte)0;

        FlexRayLinkTypeFrame.MapBlfFrameFlags(frameFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi);

        int available = Math.Max(0, payload.Length - _FlexRayRcvMessageHeaderSize);
        if (!_TryResolvePayloadLength(payloadLength, available, out int actualDataLen))
        {
            return false;
        }

        frame = _BuildLinkTypeFrame(
            subChannel, ppi, nfi, sfi, stfi, frameId, cycle, headerCrc,
            payload.Slice(_FlexRayRcvMessageHeaderSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 66 (FLEXRAY_RCVMESSAGE_EX) payload into a DLT_FLEXRAY frame.
    ///
    /// The extended receive message has a larger header than Type 50. The layout of the
    /// first 60 bytes (minimum required) is:
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2..4)   version (u16 LE)
    ///   [4..6)   channelMask (u16 LE)
    ///   [6..8)   dir (u16 LE)
    ///   [8..12)  clientIndex (u32 LE)
    ///   [12..16) clusterNo (u32 LE)
    ///   [16..18) reserved (u16 LE)
    ///   [18..20) reserved (u16 LE)
    ///   [20..24) frameId (u32 LE)   — extended, but only lower 16 bits used
    ///   [24..28) headerCrc (u32 LE) — extended
    ///   [28..30) payloadLength (u16 LE)
    ///   [30..32) payloadLengthValid (u16 LE)
    ///   [32..34) cycle (u16 LE)
    ///   [34..36) tag (u16 LE)
    ///   [36..40) frameFlags (u32 LE)
    ///   [40..44) appParameter (u32 LE)
    ///   [44..52) reserved (u64 LE) — timing extension
    ///   [52..60) reserved (u64 LE)
    ///   [60..)   FlexRay data payload
    /// </code>
    /// </summary>
    internal static bool TryParseFlexRayRcvMessageEx(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayRcvMessageExMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        ushort channelMask = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort frameId = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]);
        ushort headerCrc = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]);
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[28..]);
        byte cycle = payload[32];
        uint frameFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload[36..]);

        byte subChannel = (channelMask & 0x02) != 0 ? (byte)1 : (byte)0;

        FlexRayLinkTypeFrame.MapBlfFrameFlags(frameFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi);

        int available = Math.Max(0, payload.Length - _FlexRayRcvMessageExMinSize);
        if (!_TryResolvePayloadLength(payloadLength, available, out int actualDataLen))
        {
            return false;
        }

        frame = _BuildLinkTypeFrame(
            subChannel, ppi, nfi, sfi, stfi, frameId, cycle, headerCrc,
            payload.Slice(_FlexRayRcvMessageExMinSize, actualDataLen));
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Validates a declared FlexRay payload length against the protocol maximum and
    /// clamps the copy length to available bytes.
    /// </summary>
    private static bool _TryResolvePayloadLength(int declaredLength, int available, out int actualDataLen)
    {
        actualDataLen = 0;

        if (declaredLength > FlexRayLinkTypeFrame.MaxPayloadBytes)
        {
            return false;
        }

        actualDataLen = Math.Min(declaredLength, available);
        actualDataLen = Math.Min(actualDataLen, FlexRayLinkTypeFrame.MaxPayloadBytes);
        return true;
    }

    /// <summary>
    /// Builds a LINKTYPE_FLEXRAY frame from BLF sub-channel and ISO indicator bits.
    /// </summary>
    private static byte[] _BuildLinkTypeFrame(
        byte subChannel, bool ppi, bool nfi, bool sfi, bool stfi,
        ushort frameId, byte cycle, ushort headerCrc, ReadOnlySpan<byte> data)
    {
        bool channelB = subChannel != 0;
        return FlexRayLinkTypeFrame.BuildFrame(
            channelB, frameId, cycle, headerCrc, data,
            ppi: ppi, nfi: nfi, sfi: sfi, stfi: stfi);
    }

    #endregion
}
