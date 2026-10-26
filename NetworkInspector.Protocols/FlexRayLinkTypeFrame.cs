// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// LINKTYPE_FLEXRAY (DLT 210 / link type 210) wire format helpers and
/// <c>flexray.id</c> dispatch-key encoding.
/// <para>Capture layout (tcpdump.org / ISO 17458-2):</para>
/// <code>
/// Byte 0:     Measurement Header ([7] CH, [6:0] Type Index)
/// Byte 1:     Error Flags
/// Bytes 2-6:  FlexRay Frame Header (ISO 17458-2 Section 8)
/// Bytes 7+:   Payload data (even byte count)
/// </code>
/// <para>Dispatch key layout for <c>flexray.id</c>:</para>
/// <code>
/// bits [10:0]  = Frame ID (slot, 11 bits)
/// bit  [11]    = Channel B (0 = A, 1 = B)
/// bits [17:12] = Cycle count (6 bits)
/// </code>
/// <para>
/// ISO frame header bits (bytes 2-6, 40 bits, big-endian): reserved (1), PPI, NFI, SFI, STFI (4 indicator bits),
/// frame ID (11), payload length in 16-bit words (7), header CRC (11), cycle count (6).
/// </para>
/// <para>
/// Shared by the capture sources that synthesize LINKTYPE_FLEXRAY frames from Vector ASC and BLF files
/// (<see cref="BuildFrame"/> and the flag mappers) and by the exporters that read such frames back
/// (<see cref="TryParseDataFrame"/>), so that both directions of the bit layout live in one place.
/// <c>FlexRayProtocol</c> only uses <see cref="EncodeDispatchKey"/>: it decodes the header with its own copy of the
/// bit layout and, unlike <see cref="TryParseDataFrame"/>, tolerates a payload that is shorter than the header declares.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> stateless static helpers; safe for concurrent use.</para>
/// </remarks>
internal static class FlexRayLinkTypeFrame
{
    #region Constants

    /// <summary>Minimum header size: measurement (2) + ISO frame header (5).</summary>
    public const int MinHeaderSize = 7;

    /// <summary>Maximum payload size in bytes (127 words × 2 bytes).</summary>
    public const int MaxPayloadBytes = 254;

    /// <summary>Mask for the 11-bit frame ID portion of a dispatch key.</summary>
    public const ulong FrameIdKeyMask = 0x7FF;

    /// <summary>Bit 11 of the dispatch key signals Channel B.</summary>
    public const ulong ChannelBKeyBit = 1UL << 11;

    /// <summary>Bit shift for the 6-bit cycle count in a dispatch key.</summary>
    public const int CycleKeyShift = 12;

    /// <summary>Mask for the cycle-count portion of a dispatch key.</summary>
    public const ulong CycleKeyMask = 0x3FUL << CycleKeyShift;

    /// <summary>Type index value for a standard FlexRay data frame.</summary>
    public const byte TypeIndexFrame = 0x01;

    private const byte _ChannelBitMask = 0x80;
    private const byte _TypeIndexMask = 0x7F;
    private const byte _PpiBitMask = 0x40;
    private const byte _NfiBitMask = 0x20;
    private const byte _SfiBitMask = 0x10;
    private const byte _StfiBitMask = 0x08;
    private const byte _FrameIdHighMask = 0x07;

    #endregion

    #region Parsed fields

    /// <summary>Parsed LINKTYPE_FLEXRAY data-frame header fields.</summary>
    public readonly struct Fields
    {
        /// <summary><c>true</c> when the frame was captured on Channel B.</summary>
        public bool ChannelB
        {
            get; init;
        }

        /// <summary>Measurement-header type index (0x01 = data frame).</summary>
        public byte TypeIndex
        {
            get; init;
        }

        /// <summary>Error-flags byte from the measurement header.</summary>
        public byte ErrorFlags
        {
            get; init;
        }

        /// <summary>11-bit FlexRay slot / frame ID.</summary>
        public ushort FrameId
        {
            get; init;
        }

        /// <summary>6-bit cycle count.</summary>
        public byte Cycle
        {
            get; init;
        }

        /// <summary>11-bit header CRC.</summary>
        public ushort HeaderCrc
        {
            get; init;
        }

        /// <summary>Payload Preamble Indicator.</summary>
        public bool Ppi
        {
            get; init;
        }

        /// <summary>Null Frame Indicator (<c>true</c> = not a null frame).</summary>
        public bool Nfi
        {
            get; init;
        }

        /// <summary>Sync Frame Indicator.</summary>
        public bool Sfi
        {
            get; init;
        }

        /// <summary>Startup Frame Indicator.</summary>
        public bool Stfi
        {
            get; init;
        }

        /// <summary>Payload length in bytes (even, derived from the ISO header).</summary>
        public int PayloadByteCount
        {
            get; init;
        }
    }

    #endregion

    #region Dispatch key

    /// <summary>
    /// Encodes slot, channel, and cycle into a <c>flexray.id</c> dispatch key.
    /// The three parts occupy disjoint bit ranges (see the class summary), which is why both inputs are range checked:
    /// an oversized value would otherwise spill into a neighbouring part and silently select the wrong binding.
    /// </summary>
    /// <param name="frameId">Slot / frame ID, at most 0x7FF (11 bits).</param>
    /// <param name="channelB"><see langword="true"/> for channel B.</param>
    /// <param name="cycle">Cycle count, at most 63 (6 bits).</param>
    /// <returns>The dispatch key.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameId"/> or <paramref name="cycle"/> exceeds its bit width.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeDispatchKey(ushort frameId, bool channelB, byte cycle)
    {
        if (frameId > FrameIdKeyMask)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }

        if (cycle > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(cycle));
        }

        return (ulong)frameId
             | (channelB ? ChannelBKeyBit : 0UL)
             | ((ulong)cycle << CycleKeyShift);
    }

    /// <summary>
    /// Decodes a <c>flexray.id</c> dispatch key into slot, channel, and cycle. Inverse of
    /// <see cref="EncodeDispatchKey"/>; bits above bit 17 are ignored.
    /// </summary>
    /// <param name="key">A dispatch key.</param>
    /// <param name="frameId">Slot / frame ID (11 bits).</param>
    /// <param name="channelB"><see langword="true"/> when the key selects channel B.</param>
    /// <param name="cycle">Communication cycle (6 bits, 0-63).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecodeDispatchKey(ulong key, out ushort frameId, out bool channelB, out byte cycle)
    {
        frameId = (ushort)(key & FrameIdKeyMask);
        channelB = (key & ChannelBKeyBit) != 0;
        cycle = (byte)((key & CycleKeyMask) >> CycleKeyShift);
    }

    #endregion

    #region ASC channel mapping

    /// <summary>
    /// Maps an ASC channel number (1 = A, 2 = B) to a bus channel flag. Every value of 2 or more maps to channel B and
    /// every smaller value to channel A, so out-of-range channel numbers do not fail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AscChannelToBusChannel(int ascChannel)
    {
        return ascChannel >= 2;
    }

    /// <summary>Maps a bus channel flag to the ASC channel number (1 = A, 2 = B).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BusChannelToAscChannel(bool channelB)
    {
        if (channelB)
        {
            return 2;
        }

        return 1;
    }

    #endregion

    #region Build

    /// <summary>
    /// Builds a LINKTYPE_FLEXRAY data frame (measurement header + ISO header + payload).
    /// Payload is zero-padded to an even byte count per the FlexRay specification (the payload length field counts
    /// 16-bit words). Allocates the returned array; used when importing captures, not on the parse path.
    /// </summary>
    /// <param name="channelB"><see langword="true"/> for channel B.</param>
    /// <param name="frameId">Slot / frame ID, at most 0x7FF.</param>
    /// <param name="cycle">Cycle count, at most 63.</param>
    /// <param name="headerCrc">11-bit header CRC; bits above bit 10 are dropped.</param>
    /// <param name="payload">Payload bytes, at most <see cref="MaxPayloadBytes"/>.</param>
    /// <param name="errorFlags">Error-flags byte of the measurement header.</param>
    /// <param name="ppi">Payload Preamble Indicator.</param>
    /// <param name="nfi">Null Frame Indicator; <see langword="true"/> (default) marks a normal, non-null frame.</param>
    /// <param name="sfi">Sync Frame Indicator.</param>
    /// <param name="stfi">Startup Frame Indicator.</param>
    /// <param name="typeIndex">Measurement-header type index; only the low 7 bits are used.</param>
    /// <returns>The frame: 2 measurement bytes, the 5-byte ISO header and the padded payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="frameId"/>, <paramref name="cycle"/> or the payload length is out of range.
    /// </exception>
    public static byte[] BuildFrame(
        bool channelB,
        ushort frameId,
        byte cycle,
        ushort headerCrc,
        ReadOnlySpan<byte> payload,
        byte errorFlags = 0,
        bool ppi = false,
        bool nfi = true,
        bool sfi = false,
        bool stfi = false,
        byte typeIndex = TypeIndexFrame)
    {
        if (frameId > FrameIdKeyMask)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }

        if (cycle > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(cycle));
        }

        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        // Round the payload up to whole 16-bit words; the 7-bit length field below holds the word count (at most 127
        // because MaxPayloadBytes was checked above).
        int payloadWords = (payload.Length + 1) / 2;
        int paddedPayloadBytes = payloadWords * 2;
        byte[] frame = new byte[MinHeaderSize + paddedPayloadBytes];

        // Measurement header: bit 7 = channel, bits 6..0 = type index; then the error-flags byte.
        frame[0] = (byte)((channelB ? _ChannelBitMask : 0) | (typeIndex & _TypeIndexMask));
        frame[1] = errorFlags;

        // ISO 17458-2 header, bytes 2-6: indicators + frame ID high bits, frame ID low byte, payload words (7 bits)
        // + CRC bit 10, CRC bits 9..2, CRC bits 1..0 + cycle (6 bits). Must stay the exact inverse of TryParseDataFrame.
        frame[2] = (byte)(
            (ppi ? _PpiBitMask : 0)
            | (nfi ? _NfiBitMask : 0)
            | (sfi ? _SfiBitMask : 0)
            | (stfi ? _StfiBitMask : 0)
            | ((frameId >> 8) & _FrameIdHighMask));
        frame[3] = (byte)(frameId & 0xFF);
        frame[4] = (byte)((payloadWords << 1) | ((headerCrc >> 10) & 0x01));
        frame[5] = (byte)((headerCrc >> 2) & 0xFF);
        frame[6] = (byte)(((headerCrc & 0x03) << 6) | (cycle & 0x3F));

        // The pad byte of an odd payload length needs no write: only payload.Length bytes are copied and the array is
        // zero-initialised, so the byte after the payload is already 0.
        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(MinHeaderSize));
        }

        return frame;
    }

    /// <summary>
    /// Maps a legacy Vector/BLF <c>type_flags</c> byte to ISO 17458-2 indicator bits.
    /// In the legacy encoding bit 0x40 means "null frame", which is the inverse of the ISO null-frame indicator
    /// (NFI is set for a normal frame), hence the negation.
    /// </summary>
    /// <param name="typeFlags">Legacy flags: 0x80 payload preamble, 0x40 null frame, 0x20 sync, 0x10 startup.</param>
    /// <param name="ppi">Payload Preamble Indicator.</param>
    /// <param name="nfi">Null Frame Indicator (set for a non-null frame).</param>
    /// <param name="sfi">Sync Frame Indicator.</param>
    /// <param name="stfi">Startup Frame Indicator.</param>
    public static void MapLegacyTypeFlags(
        byte typeFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi)
    {
        ppi = (typeFlags & 0x80) != 0;
        bool isNullFrame = (typeFlags & 0x40) != 0;
        nfi = !isNullFrame;
        sfi = (typeFlags & 0x20) != 0;
        stfi = (typeFlags & 0x10) != 0;
    }

    /// <summary>
    /// Maps BLF Type 50/66 <c>frameFlags</c> to ISO 17458-2 indicator bits
    /// (<c>NULL_FRAME=0x01</c>, <c>SYNC=0x04</c>, <c>STARTUP=0x08</c>, <c>PAYLOAD_PREAM=0x10</c>).
    /// As for the legacy mapping, NULL_FRAME is inverted into the ISO null-frame indicator.
    /// </summary>
    /// <param name="frameFlags">BLF frame flags.</param>
    /// <param name="ppi">Payload Preamble Indicator.</param>
    /// <param name="nfi">Null Frame Indicator (set for a non-null frame).</param>
    /// <param name="sfi">Sync Frame Indicator.</param>
    /// <param name="stfi">Startup Frame Indicator.</param>
    public static void MapBlfFrameFlags(uint frameFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi)
    {
        ppi = (frameFlags & 0x10) != 0;
        bool isNullFrame = (frameFlags & 0x01) != 0;
        nfi = !isNullFrame;
        sfi = (frameFlags & 0x04) != 0;
        stfi = (frameFlags & 0x08) != 0;
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses a LINKTYPE_FLEXRAY data frame. Returns <see langword="false"/> when the
    /// buffer is too short, the measurement type index is not <see cref="TypeIndexFrame"/>, or the buffer is shorter
    /// than the header plus the payload length the header declares. Bytes after the declared payload are ignored.
    /// </summary>
    /// <param name="data">The LINKTYPE_FLEXRAY frame starting at the measurement header.</param>
    /// <param name="fields">The decoded header fields, or <see langword="default"/> on failure.</param>
    /// <param name="payload">A slice of <paramref name="data"/> (no copy), empty when the frame has no payload.</param>
    /// <returns><see langword="true"/> when a data frame was decoded.</returns>
    public static bool TryParseDataFrame(
        ReadOnlySpan<byte> data, out Fields fields, out ReadOnlySpan<byte> payload)
    {
        fields = default;
        payload = default;

        if (data.Length < MinHeaderSize)
        {
            return false;
        }

        byte measurementHeader = data[0];
        bool channelB = (measurementHeader & _ChannelBitMask) != 0;
        byte typeIndex = (byte)(measurementHeader & _TypeIndexMask);
        if (typeIndex != TypeIndexFrame)
        {
            return false;
        }

        // The payload length is a 7-bit word count from the (untrusted) header, so at most 254 bytes; the length check
        // below is therefore overflow free and guarantees the Slice cannot throw.
        byte headerByte0 = data[2];
        int payloadWords = (data[4] >> 1) & 0x7F;
        int payloadSize = payloadWords * 2;
        if (data.Length < MinHeaderSize + payloadSize)
        {
            return false;
        }

        fields = new Fields
        {
            ChannelB = channelB,
            TypeIndex = typeIndex,
            ErrorFlags = data[1],
            FrameId = (ushort)(((headerByte0 & _FrameIdHighMask) << 8) | data[3]),
            Cycle = (byte)(data[6] & 0x3F),
            HeaderCrc = (ushort)(
                ((data[4] & 0x01) << 10)
                | (data[5] << 2)
                | ((data[6] >> 6) & 0x03)),
            Ppi = (headerByte0 & _PpiBitMask) != 0,
            Nfi = (headerByte0 & _NfiBitMask) != 0,
            Sfi = (headerByte0 & _SfiBitMask) != 0,
            Stfi = (headerByte0 & _StfiBitMask) != 0,
            PayloadByteCount = payloadSize,
        };

        if (payloadSize > 0)
        {
            payload = data.Slice(MinHeaderSize, payloadSize);
        }

        return true;
    }

    #endregion
}
