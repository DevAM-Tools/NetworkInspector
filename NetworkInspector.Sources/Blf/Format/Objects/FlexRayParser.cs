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
/// <remarks>
/// <para>
/// Stateless and thread-safe: every call works on its own arguments.
/// </para>
/// <para>
/// Unlike the CAN, LIN, and Ethernet parsers there is no frame-slab overload: the result is built by
/// <see cref="FlexRayLinkTypeFrame.BuildFrame"/>, which the protocol layer shares and which always returns its own array,
/// so every FlexRay frame is one array allocation. Declared payload lengths come from the file and are checked against
/// <see cref="FlexRayLinkTypeFrame.MaxPayloadBytes"/> (127 words) and the bytes actually present before they size anything.
/// </para>
/// </remarks>
internal static class FlexRayParser
{
    #region Constants

    /// <summary>
    /// Packed size of the Type 29 FlexRay data header. Data starts at offset 12.
    /// </summary>
    private const int _FlexRayDataMinSize = 12;

    /// <summary>
    /// Minimum Type 41 payload size:
    /// channel(2)+dir(1)+lowTime(1)+fpgaTick(4)+fpgaTickOverflow(4)+clientIndex(4)+
    /// clusterTime(4)+frameId(2)+headerCrc(2)+frameState(2)+length(1)+cycle(1)+
    /// headerBitMask(1)+reserved1(1)+reserved2(2) = 32 bytes.
    /// </summary>
    private const int _FlexRayMessageMinSize = 32;

    /// <summary>
    /// Minimum Type 50 payload size (44-byte header):
    /// channel(2)+version(2)+channelMask(2)+dir(2)+clientIndex(4)+clusterNo(4)+
    /// frameId(2)+headerCrc1(2)+headerCrc2(2)+payloadLength(2)+payloadLengthValid(2)+
    /// cycle(2)+tag(4)+data(4)+frameFlags(4)+appParameter(4) = 44 bytes.
    /// </summary>
    private const int _FlexRayRcvMessageHeaderSize = 44;

    /// <summary>
    /// Type 66 is Type 50's 44-byte header plus 40 skipped extension bytes (84).
    /// Payload starts at offset 84.
    /// </summary>
    private const int _FlexRayRcvMessageExMinSize = 84;

    #endregion

    #region Public API

    /// <summary>
    /// Parses a BLF Type 29 (FLEXRAY_DATA) payload into a DLT_FLEXRAY frame.
    /// 12-byte header (little-endian), data at offset 12:
    /// <code>
    ///   [0..2)  channel (u16 LE; 0 = A, any other value = B)
    ///   [2]     mux (low 6 bits become the reconstructed cycle)
    ///   [3]     payload length
    ///   [4..6)  frame id (u16 LE)
    ///   [6..8)  header CRC (u16 LE)
    ///   [8]     direction
    ///   [9..12) reserved
    ///   [12..)  payload
    /// </code>
    /// Type 29 carries no frame-state word, so reconstruction always sets NFI (not a null frame) and takes the
    /// 6-bit cycle from the low bits of <c>mux</c>; PPI, SFI, and STFI are cleared.
    /// </summary>
    /// <param name="payload">BLF object payload; at least the 12-byte header.</param>
    /// <param name="frame">LINKTYPE_FLEXRAY bytes on success; empty on failure.</param>
    /// <param name="channel">Vector channel from the header (u16 LE at offset 0).</param>
    /// <returns>
    /// <see langword="false"/> when the payload is shorter than the header or the declared data length exceeds the FlexRay
    /// maximum.
    /// </returns>
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
        // [8] dir, [9] reserved, [10..12) reserved — skipped; data at sizeof(frheader).

        // Type 29 Channel B when the Vector channel field is not 0 (not only when it is 1).
        byte subChannel = channel != 0 ? (byte)1 : (byte)0;

        // The data length is one byte from the file (up to 255): reject what FlexRay cannot carry, and clamp the copy to the
        // bytes really present so a truncated object is kept at its captured length instead of over-reading.
        int available = Math.Max(0, payload.Length - _FlexRayDataMinSize);
        if (!_TryResolvePayloadLength(dataLen, available, out int actualDataLen))
        {
            return false;
        }

        ReadOnlySpan<byte> data = payload.Slice(_FlexRayDataMinSize, actualDataLen);

        // Type 29 has no frameState; reconstruct with NFI set and cycle from mux.
        frame = _BuildLinkTypeFrame(
            subChannel, ppi: false, nfi: true, sfi: false, stfi: false,
            frameId, cycle: (byte)(mux & 0x3F), headerCrc, data);
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
    ///   [28]    headerBitMask (ignored for ISO indicators; those come from frameState)
    ///   [29]    reserved1
    ///   [30..32) reserved2 (u16 LE)
    ///   [32..)  data
    /// </code>
    /// </summary>
    /// <param name="payload">BLF object payload; at least 32 bytes.</param>
    /// <param name="frame">LINKTYPE_FLEXRAY bytes on success; empty on failure.</param>
    /// <param name="channel">Vector channel from the header (u16 LE at offset 0).</param>
    /// <returns><see langword="false"/> when the payload is too short or the declared data length exceeds the FlexRay maximum.</returns>
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

        // Type 41 Channel B when the Vector channel field is not 0.
        byte subChannel = channel != 0 ? (byte)1 : (byte)0;
        ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        ushort headerCrc = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
        ushort frameState = BinaryPrimitives.ReadUInt16LittleEndian(payload[24..]);
        int dataLen = payload[26];
        byte cycle = payload[27];

        // ISO flags come from frameState, not headerBitMask. Bit 0x08 marks a null frame, so NFI (frame carries data) is its
        // inverse; the other three map directly onto PPI (0x01), SFI (0x02), and STFI (0x10).
        bool ppi = (frameState & 0x01) != 0;
        bool sfi = (frameState & 0x02) != 0;
        bool nfi = (frameState & 0x08) == 0;
        bool stfi = (frameState & 0x10) != 0;

        int available = Math.Max(0, payload.Length - _FlexRayMessageMinSize);
        if (!_TryResolvePayloadLength(dataLen, available, out int actualDataLen))
        {
            return false;
        }

        frame = _BuildLinkTypeFrame(
            subChannel, ppi, nfi, sfi, stfi, frameId, cycle, headerCrc,
            payload.Slice(_FlexRayMessageMinSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 50 (FLEXRAY_RCVMESSAGE) payload into a DLT_FLEXRAY frame.
    ///
    /// The 44-byte Type 50 header layout (all little-endian):
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2..4)   version (u16 LE)
    ///   [4..6)   channelMask (u16 LE; 1 = A, any other value including 0 = B)
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
    ///   [36..40) frameFlags (u32 LE; NULL=0x01, SYNC=0x04, STARTUP=0x08, PAYLOAD_PREAM=0x10)
    ///   [40..44) appParameter (u32 LE)
    ///   [44..)   FlexRay data payload
    /// </code>
    /// Declared ISO length comes from <c>payloadLength</c>; copied bytes come from
    /// <c>payloadLengthValid</c> and are zero-padded to the declared even length.
    /// </summary>
    /// <param name="payload">BLF object payload; at least the 44-byte header.</param>
    /// <param name="frame">LINKTYPE_FLEXRAY bytes on success; empty on failure.</param>
    /// <param name="channel">Vector channel from the header (u16 LE at offset 0).</param>
    /// <returns><see langword="false"/> when the payload is too short or a declared length exceeds the FlexRay maximum.</returns>
    internal static bool TryParseFlexRayRcvMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayRcvMessageHeaderSize)
        {
            return false;
        }

        return _TryParseFlexRayRcvCore(payload, _FlexRayRcvMessageHeaderSize, out frame, out channel);
    }

    /// <summary>
    /// Parses a BLF Type 66 (FLEXRAY_RCVMESSAGE_EX) payload into a DLT_FLEXRAY frame.
    /// Offsets 0–44 match Type 50; 40 extension bytes follow; payload starts at 84.
    /// Declared ISO length comes from <c>payloadLength</c> at offset 22; copied bytes
    /// come from <c>payloadLengthValid</c> at offset 24.
    /// </summary>
    /// <param name="payload">BLF object payload; at least 84 bytes.</param>
    /// <param name="frame">LINKTYPE_FLEXRAY bytes on success; empty on failure.</param>
    /// <param name="channel">Vector channel from the header (u16 LE at offset 0).</param>
    /// <returns><see langword="false"/> when the payload is too short or a declared length exceeds the FlexRay maximum.</returns>
    internal static bool TryParseFlexRayRcvMessageEx(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _FlexRayRcvMessageExMinSize)
        {
            return false;
        }

        return _TryParseFlexRayRcvCore(payload, _FlexRayRcvMessageExMinSize, out frame, out channel);
    }

    /// <summary>
    /// Reads only the channel field for a FlexRay object, using the same minimum sizes as the parsers.
    /// Channel is u16 LE at offset 0 for every FlexRay object type this parser handles. The index scan uses this instead
    /// of a full parse so indexing builds no frame bytes; the size checks must match the parsers' header checks.
    /// </summary>
    /// <param name="objectType">BLF object type.</param>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="channel">Channel on success; zero otherwise.</param>
    /// <returns><see langword="false"/> for a non-FlexRay object type or a payload shorter than the type's header.</returns>
    internal static bool TryGetChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        int minSize = objectType switch
        {
            BlfConstants.ObjTypeFlexRayData => _FlexRayDataMinSize,
            BlfConstants.ObjTypeFlexRayMessage => _FlexRayMessageMinSize,
            BlfConstants.ObjTypeFlexRayRcvMessage => _FlexRayRcvMessageHeaderSize,
            BlfConstants.ObjTypeFlexRayRcvMessageEx => _FlexRayRcvMessageExMinSize,
            _ => 0,
        };
        if (minSize == 0 || payload.Length < minSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Selects the ISO header CRC from Vector's dual-CRC Type 50/66 layout.
    /// Exclusive Channel B (<c>channelMask == 2</c>) stores the CRC in <c>headerCrc2</c>;
    /// Channel A, A+B, and any other mask use <c>headerCrc1</c>.
    /// </summary>
    /// <param name="channelMask">Type 50/66 <c>channelMask</c> field.</param>
    /// <param name="headerCrc1">CRC at header offset 18.</param>
    /// <param name="headerCrc2">CRC at header offset 20.</param>
    /// <returns>The CRC to place in the LINKTYPE_FLEXRAY ISO header.</returns>
    internal static ushort SelectHeaderCrc(ushort channelMask, ushort headerCrc1, ushort headerCrc2)
    {
        if (channelMask == 0x0002)
        {
            return headerCrc2;
        }

        return headerCrc1;
    }

    /// <summary>
    /// Shared Type 50 / Type 66 reconstruction. <paramref name="headerSize"/> is 44 or 84.
    /// ISO length comes from even <c>payloadLength</c>; copied bytes come from <c>payloadLengthValid</c>.
    /// </summary>
    private static bool _TryParseFlexRayRcvCore(
        ReadOnlySpan<byte> payload,
        int headerSize,
        out byte[] frame,
        out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < headerSize)
        {
            return false;
        }

        // Header fields (Type 50 layout; Type 66 shares offsets 0-43). The caller checked the size for the given header.
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        ushort channelMask = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        ushort frameId = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
        ushort headerCrc1 = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]);
        ushort headerCrc2 = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        ushort headerCrc = SelectHeaderCrc(channelMask, headerCrc1, headerCrc2);
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
        int payloadLengthValid = BinaryPrimitives.ReadUInt16LittleEndian(payload[24..]);
        byte cycle = payload[26];
        uint frameFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload[36..]);

        // Channel A only when the mask is exactly 1. Mask 0, exclusive B, and A+B are Channel B.
        byte subChannel = channelMask == 0x0001 ? (byte)0 : (byte)1;

        FlexRayLinkTypeFrame.MapBlfFrameFlags(frameFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi);

        // Two lengths with different meanings: payloadLength is the ISO payload size the frame declares (rounded down to
        // whole 16-bit words, hence the even isoLen); payloadLengthValid is how many of those bytes the logger actually
        // stored. A declared length beyond the FlexRay maximum is corrupt and rejected; the stored length is clamped to
        // the bytes present and to the declared length, so no field can drive a read past the object.
        if (payloadLength > FlexRayLinkTypeFrame.MaxPayloadBytes)
        {
            return false;
        }

        int isoLen = payloadLength & ~1;
        int available = Math.Max(0, payload.Length - headerSize);
        if (!_TryResolvePayloadLength(payloadLengthValid, available, out int copyLen))
        {
            return false;
        }

        copyLen = Math.Min(copyLen, isoLen);

        // Pad truncated copies to the declared ISO word count so TryParseDataFrame sees a full buffer.
        // stackalloc is safe here: a constant 254 bytes (the FlexRay maximum), never sized from the file. It avoids a
        // temporary heap array; BuildFrame then copies the span into the single array that is returned.
        Span<byte> isoScratch = stackalloc byte[FlexRayLinkTypeFrame.MaxPayloadBytes];
        Span<byte> isoPayload = isoScratch[..isoLen];
        isoPayload.Clear();
        if (copyLen > 0)
        {
            payload.Slice(headerSize, copyLen).CopyTo(isoPayload);
        }

        frame = _BuildLinkTypeFrame(subChannel, ppi, nfi, sfi, stfi, frameId, cycle, headerCrc, isoPayload);
        return true;
    }

    /// <summary>
    /// Validates a declared FlexRay payload length against the protocol maximum and
    /// clamps the copy length to available bytes. A declared length above the maximum is a corrupt object and fails;
    /// a declared length above the bytes present is a truncated object and is clamped (kept at its captured length).
    /// </summary>
    /// <param name="declaredLength">Length field from the object (untrusted).</param>
    /// <param name="available">Bytes present after the header.</param>
    /// <param name="actualDataLen">Bytes to copy on success; zero on failure.</param>
    /// <returns><see langword="false"/> when <paramref name="declaredLength"/> exceeds the FlexRay maximum.</returns>
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
    /// Masks slot and cycle to the ISO 11-bit / 6-bit fields so a wide Vector value
    /// cannot throw on this parse path.
    /// </summary>
    private static byte[] _BuildLinkTypeFrame(
        byte subChannel, bool ppi, bool nfi, bool sfi, bool stfi,
        ushort frameId, byte cycle, ushort headerCrc, ReadOnlySpan<byte> data)
    {
        bool channelB = subChannel != 0;
        ushort maskedFrameId = (ushort)(frameId & 0x7FF);
        byte maskedCycle = (byte)(cycle & 0x3F);
        return FlexRayLinkTypeFrame.BuildFrame(
            channelB, maskedFrameId, maskedCycle, headerCrc, data,
            ppi: ppi, nfi: nfi, sfi: sfi, stfi: stfi);
    }

    #endregion
}
