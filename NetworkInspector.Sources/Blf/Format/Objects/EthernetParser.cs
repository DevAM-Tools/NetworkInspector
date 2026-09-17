// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF Ethernet object payloads into raw Ethernet frame bytes.
///
/// Three BLF object types carry Ethernet frames:
/// <list type="bullet">
///   <item><description>Type 71 (<c>ETHERNET_FRAME</c>) — decomposed 32-byte header.
///     EtherType/TPID/TCI are little-endian in the BLF header and converted to
///     wire big-endian on the reconstructed Ethernet frame.</description></item>
///   <item><description>Type 120 (<c>ETHERNET_FRAME_EX</c>) — 32-byte header;
///     raw frame starts at offset 32, <c>frame_length</c> at 22.</description></item>
///   <item><description>Type 102 (<c>ETHERNET_RX_ERROR</c>) — 20-byte naturally aligned
///     header; channel at offset 2, <c>frame_length</c> at 12, data at 20.</description></item>
/// </list>
/// </summary>
/// <remarks>Not thread-safe. Caller synchronisation required.</remarks>
internal static class EthernetParser
{
    #region Constants

    /// <summary>Packed size of the Type 71 Ethernet header (32 bytes, including 8-byte reserved tail).</summary>
    private const int _Type71HeaderSize = 32;

    /// <summary>Minimum raw Ethernet frame size: dst(6)+src(6)+ethertype(2) = 14 bytes.</summary>
    private const int _MinEthernetFrameSize = 14;

    /// <summary>
    /// Maximum Ethernet payload length that this parser will materialise into a heap array.
    /// Standard Ethernet II MTU is 1500 bytes; jumbo frames reach up to ~9000 bytes.
    /// 64 KiB provides a generous upper bound while preventing a crafted BLF object
    /// with a huge payloadLen field from triggering a multi-megabyte allocation.
    /// </summary>
    private const int _MaxEthernetPayload = 64 * 1024;

    /// <summary>Packed size of the Type 120 Ethernet-ex header (32 bytes).</summary>
    private const int _Type120HeaderSize = 32;

    /// <summary>Byte offset of <c>channel</c> in Type 120.</summary>
    private const int _Type120ChannelOffset = 4;

    /// <summary>Byte offset of <c>frame_length</c> in Type 120 (u16 LE).</summary>
    private const int _Type120FrameLengthOffset = 22;

    /// <summary>
    /// Naturally aligned size of the Type 102 Ethernet RX-error header.
    /// Sequential fields without packing are 18 bytes; <c>error</c> is u32 after <c>frame_length</c>
    /// so default C alignment inserts 2 pad bytes and the stored size is 20.
    /// </summary>
    private const int _Type102HeaderSize = 20;

    /// <summary>Byte offset of <c>channel</c> in Type 102.</summary>
    private const int _Type102ChannelOffset = 2;

    /// <summary>Byte offset of <c>frame_length</c> in Type 102 (immediately after checksum).</summary>
    private const int _Type102FrameLengthOffset = 12;

    #endregion

    #region Public API

    /// <summary>
    /// Parses a BLF Type 71 (ETHERNET_FRAME) object payload into a raw Ethernet frame.
    ///
    /// The 32-byte Type 71 header layout:
    /// <code>
    ///   [0..6)    src MAC
    ///   [6..8)    channel (u16 LE)
    ///   [8..14)   dst MAC
    ///   [14..16)  direction (u16 LE, ignored)
    ///   [16..18)  EtherType (u16 LE in the BLF struct)
    ///   [18..20)  TPID (u16 LE)
    ///   [20..22)  TCI (u16 LE)
    ///   [22..24)  payload length (u16 LE)
    ///   [24..32)  uint64 reserved
    ///   [32..)    L3 payload bytes
    /// </code>
    /// Reconstructed Ethernet uses wire big-endian EtherType/VLAN.
    /// VLAN is inserted only when both TPID and TCI are non-zero.
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
        ushort etherType = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
        ushort tpid = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]);
        ushort tci = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);

        int availablePayload = Math.Max(0, payload.Length - _Type71HeaderSize);
        payloadLen = Math.Min(payloadLen, Math.Min(availablePayload, _MaxEthernetPayload));
        ReadOnlySpan<byte> innerPayload = payload.Slice(_Type71HeaderSize, payloadLen);

        // Insert a VLAN tag only when both TPID and TCI are non-zero.
        bool hasVlan = tpid != 0 && tci != 0;
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
    /// 32-byte header; raw frame starts at offset 32; <c>frame_length</c> is u16 LE at offset 22:
    /// <code>
    ///   [0..2)   struct length (u16 LE)
    ///   [2..4)   flags (u16 LE)
    ///   [4..6)   channel (u16 LE)
    ///   [6..8)   hardware channel (u16 LE)
    ///   [8..16)  frame duration (u64 LE, nanoseconds)
    ///   [16..20) frame checksum (u32 LE)
    ///   [20..22) direction (u16 LE)
    ///   [22..24) frame length (u16 LE)
    ///   [24..28) frame handle (u32 LE)
    ///   [28..32) error (u32 LE)
    ///   [32..)   raw Ethernet frame
    /// </code>
    /// Copies the Ethernet bytes into a new array.
    /// </summary>
    internal static bool TryParseType120(
        ReadOnlySpan<byte> payload,
        out ReadOnlyMemory<byte> frame,
        out ushort channel) =>
        TryParseType120(payload, ReadOnlyMemory<byte>.Empty, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 120 (ETHERNET_FRAME_EX) object payload into a raw Ethernet frame.
    /// 32-byte header; raw frame starts at offset 32; <c>frame_length</c> is u16 LE at offset 22.
    /// When <paramref name="payloadMemory"/> covers the same bytes as <paramref name="payload"/>,
    /// the returned frame aliases that array instead of copying.
    /// </summary>
    internal static bool TryParseType120(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        out ReadOnlyMemory<byte> frame,
        out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _Type120HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type120ChannelOffset..]);
        int frameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type120FrameLengthOffset..]);
        int available = payload.Length - _Type120HeaderSize;

        if (available <= 0)
        {
            return false;
        }

        int actualLen = frameLength > 0
            ? Math.Min(frameLength, available)
            : available;

        if (actualLen < _MinEthernetFrameSize)
        {
            return false;
        }

        frame = _SliceOrCopyEthernet(payload, payloadMemory, _Type120HeaderSize, actualLen);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 102 (ETHERNET_RX_ERROR) object payload into a raw Ethernet frame.
    /// Sequential LE fields: struct_length@0, channel@2, direction@4, hw_channel@6,
    /// frame_checksum@8, frame_length@12, 2 pad, error@16; data at 20.
    /// Copies the Ethernet bytes into a new array.
    /// </summary>
    internal static bool TryParseType102(
        ReadOnlySpan<byte> payload,
        out ReadOnlyMemory<byte> frame,
        out ushort channel) =>
        TryParseType102(payload, ReadOnlyMemory<byte>.Empty, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 102 (ETHERNET_RX_ERROR) object payload into a raw Ethernet frame.
    /// Sequential LE fields: struct_length@0, channel@2, direction@4, hw_channel@6,
    /// frame_checksum@8, frame_length@12, 2 pad, error@16; data at 20.
    /// When <paramref name="payloadMemory"/> covers the same bytes as <paramref name="payload"/>,
    /// the returned frame aliases that array instead of copying.
    /// </summary>
    internal static bool TryParseType102(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        out ReadOnlyMemory<byte> frame,
        out ushort channel)
    {
        frame = default;
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

        int actualLen = frameLength > 0
            ? Math.Min(frameLength, available)
            : available;

        if (actualLen < _MinEthernetFrameSize)
        {
            return false;
        }

        frame = _SliceOrCopyEthernet(payload, payloadMemory, _Type102HeaderSize, actualLen);
        return true;
    }

    /// <summary>Reads Type 71 channel without reconstructing the Ethernet frame.</summary>
    internal static bool TryGetChannelType71(ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        if (payload.Length < _Type71HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        return true;
    }

    /// <summary>Reads Type 120 channel without reconstructing the Ethernet frame.</summary>
    internal static bool TryGetChannelType120(ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        if (payload.Length < _Type120HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type120ChannelOffset..]);
        return true;
    }

    /// <summary>Reads Type 102 channel without reconstructing the Ethernet frame.</summary>
    internal static bool TryGetChannelType102(ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        if (payload.Length < _Type102HeaderSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[_Type102ChannelOffset..]);
        return true;
    }

    #endregion

    #region Private Helpers

    private static ReadOnlyMemory<byte> _SliceOrCopyEthernet(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        int headerSize,
        int actualLen)
    {
        if (payloadMemory.Length == payload.Length && payloadMemory.Length >= headerSize + actualLen)
        {
            return payloadMemory.Slice(headerSize, actualLen);
        }

        byte[] copy = new byte[actualLen];
        payload.Slice(headerSize, actualLen).CopyTo(copy);
        return copy;
    }

    #endregion
}
