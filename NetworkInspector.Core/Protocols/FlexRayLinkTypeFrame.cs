// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

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
/// </summary>
public static class FlexRayLinkTypeFrame
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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong EncodeDispatchKey(ushort frameId, bool channelB, byte cycle)
        => (ulong)(frameId & FrameIdKeyMask)
         | (channelB ? ChannelBKeyBit : 0UL)
         | ((ulong)(cycle & 0x3F) << CycleKeyShift);

    /// <summary>
    /// Decodes a <c>flexray.id</c> dispatch key into slot, channel, and cycle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DecodeDispatchKey(ulong key, out ushort frameId, out bool channelB, out byte cycle)
    {
        frameId = (ushort)(key & FrameIdKeyMask);
        channelB = (key & ChannelBKeyBit) != 0;
        cycle = (byte)((key & CycleKeyMask) >> CycleKeyShift);
    }

    #endregion

    #region ASC channel mapping

    /// <summary>Maps an ASC channel number (1 = A, 2 = B) to a bus channel flag.</summary>
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
    /// Payload is zero-padded to an even byte count per the FlexRay specification.
    /// </summary>
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

        int payloadWords = (payload.Length + 1) / 2;
        int paddedPayloadBytes = payloadWords * 2;
        byte[] frame = new byte[MinHeaderSize + paddedPayloadBytes];

        frame[0] = (byte)((channelB ? _ChannelBitMask : 0) | (typeIndex & _TypeIndexMask));
        frame[1] = errorFlags;
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

        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(MinHeaderSize));
        }

        if (paddedPayloadBytes > payload.Length)
        {
            frame[MinHeaderSize + payload.Length] = 0;
        }

        return frame;
    }

    /// <summary>
    /// Maps a legacy Vector/BLF <c>type_flags</c> byte to ISO 17458-2 indicator bits.
    /// </summary>
    public static void MapLegacyTypeFlags(
        byte typeFlags, out bool ppi, out bool nfi, out bool sfi, out bool stfi)
    {
        ppi = (typeFlags & 0x80) != 0;
        bool isNullFrame = (typeFlags & 0x40) != 0;
        nfi = !isNullFrame;
        sfi = (typeFlags & 0x20) != 0;
        stfi = (typeFlags & 0x10) != 0;
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses a LINKTYPE_FLEXRAY data frame. Returns <see langword="false"/> when the
    /// buffer is too short or the measurement type index is not <see cref="TypeIndexFrame"/>.
    /// </summary>
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

        byte headerByte0 = data[2];
        int payloadWords = (data[4] >> 1) & 0x7F;
        int payloadSize = payloadWords * 2;
        int totalConsumed = Math.Min(MinHeaderSize + payloadSize, data.Length);

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
            PayloadByteCount = Math.Max(0, totalConsumed - MinHeaderSize),
        };

        if (totalConsumed > MinHeaderSize)
        {
            payload = data.Slice(MinHeaderSize, fields.PayloadByteCount);
        }

        return true;
    }

    #endregion
}
