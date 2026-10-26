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
/// Type 120 also covers Types 121–123, which share its header (<see cref="BlfConstants.IsEthernetExType"/>).
/// </summary>
/// <remarks>
/// <para>
/// Stateless and thread-safe; the only mutable object is the optional <see cref="FrameSlabAllocator"/> argument, which is
/// not thread-safe and must be confined to the calling reader thread.
/// </para>
/// <para>
/// Memory ownership of the returned frame differs by type. Type 71 must rebuild the frame (the BLF header holds the MAC
/// addresses, EtherType and VLAN tag separately), so it always writes a new buffer. Types 120 and 102 store the raw
/// frame verbatim, so without a slab allocator they can alias the already decompressed container instead of copying
/// (zero copy per frame; see <c>_SliceOrCopyEthernet</c>); with a slab allocator they copy into a slab so a held frame
/// cannot pin a whole container. All header fields are read with sequential little-endian reads, never a struct overlay.
/// </para>
/// </remarks>
internal static class EthernetParser
{
    #region Constants

    /// <summary>Packed size of the Type 71 Ethernet header (32 bytes, including 8-byte reserved tail).</summary>
    private const int _Type71HeaderSize = 32;

    /// <summary>Minimum raw Ethernet frame size: dst(6)+src(6)+ethertype(2) = 14 bytes.</summary>
    private const int _MinEthernetFrameSize = 14;

    /// <summary>
    /// Maximum Ethernet payload length that Type 71 reconstruction will materialise into a new buffer.
    /// Standard Ethernet II MTU is 1500 bytes; jumbo frames reach up to ~9000 bytes.
    /// Threat: a crafted object with an inflated payload length forcing a large allocation. Constant (a format bound,
    /// not a host setting); secure by default. The Type 71 length field is a u16 (at most 65535), so today this bound
    /// equals the field's own range and is a defensive limit that keeps holding if the field is ever read wider. The
    /// copy is in any case clamped to the bytes present in the object (see the Type 71 parser).
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
    /// A VLAN tag is inserted whenever the TPID is non-zero: Vector writes TPID 0 for an untagged frame, and a
    /// tag with TCI 0 (priority 0, VID 0) is a real 802.1Q tag that must survive.
    /// A payload length larger than the bytes present is clamped (the frame is kept at its captured length), unlike
    /// Types 120 and 102, which reject a short object.
    /// </summary>
    /// <param name="payload">BLF object payload; at least the 32-byte header.</param>
    /// <param name="frame">Ethernet bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header (u16 LE at offset 6).</param>
    internal static bool TryParseType71(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseType71(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseType71(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">Ethernet bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseType71(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _Type71HeaderSize)
        {
            return false;
        }

        // Header fields. The MAC addresses stay as spans over the input (no copy until they are written into the frame);
        // EtherType, TPID and TCI are little-endian in the BLF struct and are converted to wire order when written below.
        ReadOnlySpan<byte> srcMac = payload[0..6];
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        ReadOnlySpan<byte> dstMac = payload[8..14];
        ushort etherType = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
        ushort tpid = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]);
        ushort tci = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);

        // Bound the L3 payload by what the object really holds and by the Type 71 maximum, so the buffer size below is
        // never driven by the declared length alone. Max(0, ...) is defensive; the header size was checked above.
        int availablePayload = Math.Max(0, payload.Length - _Type71HeaderSize);
        payloadLen = Math.Min(payloadLen, Math.Min(availablePayload, _MaxEthernetPayload));
        ReadOnlySpan<byte> innerPayload = payload.Slice(_Type71HeaderSize, payloadLen);

        // Vector writes tpid = 0 when the frame carried no tag. A tag with TCI 0 (priority 0, VID 0) is a real
        // 802.1Q tag and must survive, so only the TPID decides.
        bool hasVlan = tpid != 0;
        // Wire layout: dst(6) + src(6) + [TPID(2) + TCI(2)] + EtherType(2) + payload. The BLF header stores src before dst,
        // so the order is swapped here. Every byte of the frame is written below.
        int frameLen = 12 + (hasVlan ? 4 : 0) + 2 + payloadLen;
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, frameLen);
        Span<byte> ethernet = buffer.Span;

        int offset = 0;
        dstMac.CopyTo(ethernet[offset..]);
        offset += 6;
        srcMac.CopyTo(ethernet[offset..]);
        offset += 6;

        if (hasVlan)
        {
            BinaryPrimitives.WriteUInt16BigEndian(ethernet[offset..], tpid);
            offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(ethernet[offset..], tci);
            offset += 2;
        }

        BinaryPrimitives.WriteUInt16BigEndian(ethernet[offset..], etherType);
        offset += 2;
        innerPayload.CopyTo(ethernet[offset..]);

        // frameLen is at least 14 (two MACs plus EtherType) by construction, so this size check currently always passes;
        // it mirrors the minimum frame size enforced for Types 120 and 102.
        frame = buffer;
        return frameLen >= _MinEthernetFrameSize;
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
    /// Copies the Ethernet bytes into a new array (no <c>payloadMemory</c> is supplied, so nothing can be aliased).
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
        out ushort channel) =>
        TryParseType120(payload, payloadMemory, null, out frame, out channel);

    /// <summary>
    /// Parses a Type 120 payload, or one of the Types 121–123 that share its 32-byte header
    /// (<see cref="BlfConstants.IsEthernetExType"/>). With <paramref name="slabs"/> set, the Ethernet bytes are copied
    /// into a slab region (bounded retention); without, they alias <paramref name="payloadMemory"/> when it covers the
    /// payload (a held frame then keeps the whole decompressed container alive), else they are copied into a new array.
    /// </summary>
    /// <param name="payload">BLF object payload; at least the 32-byte header.</param>
    /// <param name="payloadMemory">Heap-backed view of the same bytes as <paramref name="payload"/>, or empty.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null.</param>
    /// <param name="frame">Ethernet bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header (u16 LE at offset 4).</param>
    internal static bool TryParseType120(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        FrameSlabAllocator? slabs,
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

        // frame_length is the Ethernet size. A zero length is not "the rest of the object":
        // alignment zeros after the frame must not become a packet. A short object fails
        // instead of being truncated (the length field is a u16, so the slice below is also bounded by 65535).
        if (frameLength < _MinEthernetFrameSize || available < frameLength)
        {
            return false;
        }

        // Copy or alias exactly frame_length bytes; trailing bytes after the frame in the object are ignored.
        int actualLen = frameLength;

        frame = _SliceOrCopyEthernet(payload, payloadMemory, slabs, _Type120HeaderSize, actualLen);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 102 (ETHERNET_RX_ERROR) object payload into a raw Ethernet frame.
    /// Sequential LE fields: struct_length@0, channel@2, direction@4, hw_channel@6,
    /// frame_checksum@8, frame_length@12, 2 pad, error@16; data at 20.
    /// Copies the Ethernet bytes into a new array (no <c>payloadMemory</c> is supplied, so nothing can be aliased).
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
        out ushort channel) =>
        TryParseType102(payload, payloadMemory, null, out frame, out channel);

    /// <summary>
    /// Parses a Type 102 payload. Memory rules as for
    /// <see cref="TryParseType120(ReadOnlySpan{byte}, ReadOnlyMemory{byte}, FrameSlabAllocator?, out ReadOnlyMemory{byte}, out ushort)"/>.
    /// </summary>
    internal static bool TryParseType102(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        FrameSlabAllocator? slabs,
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

        // Same rule as Type 120: only frame_length bytes, and only when they are all present.
        if (frameLength < _MinEthernetFrameSize || available < frameLength)
        {
            return false;
        }

        int actualLen = frameLength;

        frame = _SliceOrCopyEthernet(payload, payloadMemory, slabs, _Type102HeaderSize, actualLen);
        return true;
    }

    /// <summary>
    /// Reads Type 71 channel without reconstructing the Ethernet frame (used by the index scan, which builds no frame
    /// bytes). The size check equals the one in <see cref="TryParseType71(ReadOnlySpan{byte}, out byte[], out ushort)"/>.
    /// </summary>
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

    /// <summary>
    /// Reads the channel of Type 120 (and 121–123) without reconstructing the Ethernet frame. Only the header size is
    /// checked, not frame_length, so an object with a bad frame_length is still indexed here and fails later in the full
    /// parse.
    /// </summary>
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

    /// <summary>
    /// Reads the hardware channel when the object carries one.
    /// Types 120–123 store it at offset 6 and set flags bit 0x0002 when it is valid.
    /// Type 102 stores it at offset 6; zero means the field is absent.
    /// </summary>
    internal static bool TryReadHardwareChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort hardwareChannel)
    {
        hardwareChannel = 0;
        const ushort hardwareChannelFlag = 0x0002;

        // Types 120-123: the flags word says whether the hardware channel field holds a real value.
        if (BlfConstants.IsEthernetExType(objectType))
        {
            // Offsets 2 (flags) and 6 (hardware channel) both lie inside the first 8 bytes.
            if (payload.Length < 8)
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            if ((flags & hardwareChannelFlag) == 0)
            {
                return false;
            }

            hardwareChannel = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
            return true;
        }

        // Type 102 has no validity flag, so zero is treated as "no hardware channel".
        if (objectType == BlfConstants.ObjTypeEthernetRxError)
        {
            if (payload.Length < 8)
            {
                return false;
            }

            hardwareChannel = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
            return hardwareChannel != 0;
        }

        return false;
    }

    /// <summary>
    /// Reads Type 102 channel without reconstructing the Ethernet frame. Only the header size is checked (see
    /// <see cref="TryGetChannelType120"/>).
    /// </summary>
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

    /// <summary>
    /// Returns the Ethernet bytes: a slab copy when slabs are on (bounded retention), else a slice of the container
    /// when <paramref name="payloadMemory"/> covers the payload (zero copy), else a new array.
    /// </summary>
    /// <remarks>
    /// Trade-off: aliasing avoids one copy and one allocation per frame, but every frame that is held keeps the whole
    /// decompressed container array alive. The sequential reader therefore passes slabs when the slab option is set (copy
    /// into a bounded slab); without slabs, and on random access (<c>FrameById</c> passes none) for objects that live in
    /// a decompressed container, the frame aliases the container. The caller has already checked that
    /// <c>headerSize + actualLen</c> lies inside <paramref name="payload"/>.
    /// </remarks>
    private static ReadOnlyMemory<byte> _SliceOrCopyEthernet(
        ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> payloadMemory,
        FrameSlabAllocator? slabs,
        int headerSize,
        int actualLen)
    {
        ReadOnlySpan<byte> ethernet = payload.Slice(headerSize, actualLen);
        if (slabs is not null)
        {
            // Every byte is copied, so the uninitialised slab region needs no clearing.
            Memory<byte> region = slabs.Allocate(actualLen);
            ethernet.CopyTo(region.Span);
            return region;
        }

        // A span cannot be compared to a memory for identity; equal length is the contract that the caller passed a memory
        // over the same bytes as the span (BlfObjectInfo builds both from one buffer, or leaves the memory empty).
        if (payloadMemory.Length == payload.Length && payloadMemory.Length >= headerSize + actualLen)
        {
            return payloadMemory.Slice(headerSize, actualLen);
        }

        return ethernet.ToArray();
    }

    #endregion
}
