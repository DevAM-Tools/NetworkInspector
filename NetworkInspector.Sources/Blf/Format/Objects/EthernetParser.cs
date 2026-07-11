// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF Ethernet object payloads into raw Ethernet frame bytes.
///
/// Three BLF object types carry Ethernet frames:
/// <list type="bullet">
///   <item><description>Type 71 (<c>ETHERNET_FRAME</c>) — decomposed format with a 32-byte
///     <c>blf_ethernetframeheader_t</c> header. Source MAC, destination MAC, EtherType, optional
///     VLAN fields, and payload are stored separately. This parser reassembles them into a
///     standard Ethernet II or 802.1Q frame.</description></item>
///   <item><description>Type 120 (<c>ETHERNET_FRAME_EX</c>) — raw format with a 28-byte
///     <c>blf_ethernetframeex_t</c> header followed by the verbatim Ethernet frame starting
///     at offset 20 (frameLength field at [12..14], data at [16..]).</description></item>
///   <item><description>Type 102 (<c>ETHERNET_RX_ERROR</c>) — similar raw format with a
///     28-byte header; raw frame starts at offset 28.</description></item>
/// </list>
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class EthernetParser
{
    #region Constants

    /// <summary>Minimum size of the Type 71 header: 32 bytes (6+2+6+2+2+2+2+2+8).</summary>
    private const int _Type71HeaderSize = 32;

    /// <summary>Minimum raw Ethernet frame size: dst(6)+src(6)+ethertype(2) = 14 bytes.</summary>
    private const int _MinEthernetFrameSize = 14;

    /// <summary>
    /// Maximum Ethernet payload length that this parser will materialise into a heap array.
    /// Standard Ethernet II MTU is 1500 bytes; jumbo frames reach up to ~9000 bytes.
    /// 64 KiB provides a generous upper bound while preventing a crafted BLF object
    /// with a huge payloadLen field from triggering a multi-megabyte allocation.
    /// </summary>
    private const int _MaxEthernetPayload = 64 * 1024; // 64 KiB

    /// <summary>
    /// Size of the blf_ethernetframeex_t header (Type 120).
    /// Layout: structLength(2)+flags(2)+channel(2)+hardwareChannel(2)+frameTimeDelta(8)+
    ///         sequenceNumber(2)+frameLength(2)+frameHandle(4)+error(2)+reserved(2) = 28 bytes.
    /// The raw frame follows immediately after this header.
    /// </summary>
    private const int _Type120HeaderSize = 28;

    /// <summary>
    /// Byte offset of the <c>channel</c> field in the Type 120 header.
    /// </summary>
    private const int _Type120ChannelOffset = 4;

    /// <summary>
    /// Byte offset of the <c>frameLength</c> field in the Type 120 header (u16 LE).
    /// </summary>
    private const int _Type120FrameLengthOffset = 12;

    /// <summary>
    /// Size of the blf_etherneterror_t header (Type 102).
    /// Layout: structLength(2)+flags(2)+channel(2)+dir(2)+hardwareChannel(2)+
    ///         frameChecksum(2)+error(2)+frameLength(2)+frameHandle(4)+error2(2)+reserved(2) = 26 bytes.
    /// The raw frame follows immediately after this header.
    /// </summary>
    private const int _Type102HeaderSize = 26;

    /// <summary>Byte offset of the <c>channel</c> field in the Type 102 header.</summary>
    private const int _Type102ChannelOffset = 4;

    /// <summary>Byte offset of the <c>frameLength</c> field in the Type 102 header (u16 LE).</summary>
    private const int _Type102FrameLengthOffset = 14;

    #endregion

    #region Public API

    /// <summary>
    /// Parses a BLF Type 71 (ETHERNET_FRAME) object payload into a raw Ethernet frame.
    ///
    /// The 32-byte <c>blf_ethernetframeheader_t</c> layout (all fields little-endian unless noted):
    /// <code>
    ///   [0..6)    src MAC
    ///   [6..8)    channel (u16 LE)
    ///   [8..14)   dst MAC
    ///   [14..16)  direction (u16 LE, ignored)
    ///   [16..18)  EtherType / inner EtherType (u16 big-endian)
    ///   [18..20)  TPID (u16 big-endian; 0 = untagged, 0x8100 = 802.1Q)
    ///   [20..22)  TCI  (u16 big-endian; VLAN ID + PCP/CFI)
    ///   [22..24)  payload length (u16 LE, bytes of L3 payload after EtherType)
    ///   [24..32)  uint64 reserved
    ///   [32..)    L3 payload bytes
    /// </code>
    ///
    /// Reconstruction algorithm:
    /// <c>dst + src + [TPID + TCI if VLAN] + EtherType + payload</c>.
    /// </summary>
    internal static bool TryParseType71(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _Type71HeaderSize)
        {
            return false;
        }

        ReadOnlySpan<byte> srcMac = payload[0..6];
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        ReadOnlySpan<byte> dstMac = payload[8..14];
        // [14..16] direction — ignored
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(payload[16..]);
        ushort tpid = BinaryPrimitives.ReadUInt16BigEndian(payload[18..]);
        ushort tci = BinaryPrimitives.ReadUInt16BigEndian(payload[20..]);
        int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
        // [24..32] reserved — skipped

        // Clamp payloadLen to both the actual bytes present in the BLF object and
        // the _MaxEthernetPayload cap. The payloadLen field is untrusted: a crafted value that
        // matches the available bytes but exceeds 64 KiB would cause a silent multi-megabyte
        // heap allocation without any spec justification, since standard Ethernet payloads
        // are at most ~9000 bytes (jumbo) and 64 KiB is far beyond any Ethernet MTU.
        int availablePayload = Math.Max(0, payload.Length - _Type71HeaderSize);
        payloadLen = Math.Min(payloadLen, Math.Min(availablePayload, _MaxEthernetPayload));
        ReadOnlySpan<byte> innerPayload = payload.Slice(_Type71HeaderSize, payloadLen);

        bool hasVlan = tpid != 0;
        int frameLen = 12 + (hasVlan ? 4 : 0) + 2 + payloadLen;
        frame = new byte[frameLen];

        int offset = 0;
        dstMac.CopyTo(frame.AsSpan(offset));
        offset += 6;
        srcMac.CopyTo(frame.AsSpan(offset));
        offset += 6;

        if (hasVlan)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), tpid);
            offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), tci);
            offset += 2;
        }

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), etherType);
        offset += 2;
        innerPayload.CopyTo(frame.AsSpan(offset));

        return frame.Length >= _MinEthernetFrameSize;
    }

    /// <summary>
    /// Parses a BLF Type 120 (ETHERNET_FRAME_EX) object payload into a raw Ethernet frame.
    ///
    /// The <c>blf_ethernetframeex_t</c> header is 28 bytes:
    /// <code>
    ///   [0..2)    structLength (u16 LE)
    ///   [2..4)    flags (u16 LE)
    ///   [4..6)    channel (u16 LE)
    ///   [6..8)    hardwareChannel (u16 LE)
    ///   [8..16)   frameTimeDelta (u64 LE)
    ///   [16..18)  sequenceNumber (u16 LE)
    ///   [18..20)  reserved (u16)
    ///   [20..22)  frameLength (u16 LE, length of raw Ethernet frame)
    ///   [22..26)  frameHandle (u32 LE)
    ///   [26..28)  error (u16 LE)
    ///   [28..)    raw Ethernet frame bytes
    /// </code>
    /// </summary>
    internal static bool TryParseType120(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _Type120HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type120ChannelOffset..]);
        int frameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        int available = payload.Length - _Type120HeaderSize;

        if (available <= 0)
        {
            return false;
        }

        // Use the smaller of declared frameLength and available bytes
        int actualLen = frameLength > 0
            ? Math.Min(frameLength, available)
            : available;

        if (actualLen < _MinEthernetFrameSize)
        {
            return false;
        }

        frame = payload.Slice(_Type120HeaderSize, actualLen).ToArray();
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 102 (ETHERNET_RX_ERROR) object payload into a raw Ethernet frame.
    ///
    /// The <c>blf_etherneterror_t</c> header is 26 bytes:
    /// <code>
    ///   [0..2)    structLength (u16 LE)
    ///   [2..4)    flags (u16 LE)
    ///   [4..6)    channel (u16 LE)
    ///   [6..8)    dir (u16 LE)
    ///   [8..10)   hardwareChannel (u16 LE)
    ///   [10..12)  frameChecksum (u16 LE)
    ///   [12..14)  error (u16 LE)
    ///   [14..16)  frameLength (u16 LE, length of raw Ethernet frame)
    ///   [16..20)  frameHandle (u32 LE)
    ///   [20..22)  error2 (u16 LE)
    ///   [22..24)  reserved (u16)
    ///   [24..26)  reserved2 (u16)
    ///   [26..)    raw Ethernet frame bytes (partial, may be truncated due to RX error)
    /// </code>
    /// </summary>
    internal static bool TryParseType102(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
    {
        frame = [];
        channel = 0;

        if (payload.Length < _Type102HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type102ChannelOffset..]);
        int frameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type102FrameLengthOffset..]);
        int available = payload.Length - _Type102HeaderSize;

        if (available <= 0)
        {
            return false;
        }

        // Error frames may be truncated — use available bytes if frameLength is larger
        int actualLen = frameLength > 0
            ? Math.Min(frameLength, available)
            : available;

        if (actualLen < _MinEthernetFrameSize)
        {
            return false;
        }

        frame = payload.Slice(_Type102HeaderSize, actualLen).ToArray();
        return true;
    }

    #endregion
}
