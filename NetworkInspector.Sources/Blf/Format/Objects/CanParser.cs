// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Objects;

/// <summary>
/// Parses BLF CAN, CAN FD, and CAN XL object payloads into SocketCAN frame bytes.
///
/// Output format is SocketCAN for classic CAN (8 bytes plus the captured data length, 0–8).
/// DLC 8 is 16 bytes. An RTR frame is 8 bytes: byte 4 still holds the requested length and there is no data tail.
/// The frame is deliberately not padded to the 16-byte <c>can_frame</c> size: the data tail is stored at its captured length.
/// <code>
///   id(4 BE) + len(1, payload byte count 0–8) + zero(2) + len8_dlc(1, bus DLC 9–15, otherwise 0) + data(0–8)
/// </code>
/// SocketCAN FD for CAN FD (8 + actual data length, not padded to 64):
/// <code>
///   id(4 BE) + len(1, byte count) + fd_flags(1) + reserved(2) + data(0-64)
/// </code>
/// and SocketCAN XL for CAN XL (12 + dataLength):
/// <code>
///   [0]=0, vcid, priority(2 BE), flags, sdu, dataLength(2 LE), acceptance(4 LE), payload
/// </code>
///
/// Supported types:
/// <list type="bullet">
///   <item>Type 1  (<c>CAN_MESSAGE</c>) — classic CAN, 8-byte header + 8 data bytes</item>
///   <item>Type 2  (<c>CAN_ERROR</c>) — CAN error frame (produces SocketCAN error)</item>
///   <item>Type 3  (<c>CAN_OVERLOAD</c>) — CAN overload (produces SocketCAN error)</item>
///   <item>Type 73 (<c>CAN_ERROR_EXT</c>) — extended error, 24-byte header</item>
///   <item>Type 86 (<c>CAN_MESSAGE2</c>) — classic CAN v2 (same first 16 bytes as Type 1)</item>
///   <item>Type 100 (<c>CAN_FD_MESSAGE</c>) — CAN FD, 20-byte header, data at offset 20</item>
///   <item>Type 101 (<c>CAN_FD_MESSAGE_64</c>) — CAN FD, 40-byte header, data at offset 40</item>
///   <item>Type 104 (<c>CAN_FD_ERROR_64</c>) — CAN FD error, 44-byte header (produces SocketCAN error)</item>
///   <item>Type 139 (<c>CAN_XL_CHANNEL_FRAME</c>) — CAN XL, 104-byte header + payload</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Stateless and thread-safe; the only mutable object is the optional <see cref="FrameSlabAllocator"/> argument, which is
/// not thread-safe and must be confined to the calling reader thread.
/// </para>
/// <para>
/// Every parser exists twice. The overload with a <see cref="FrameSlabAllocator"/> carves the frame bytes out of a shared
/// slab (one array per slab instead of one per frame; sequential reads). The <c>out byte[]</c> overload is the same code
/// path with no allocator, so it allocates exactly one array per frame (random access, tests); the two cannot diverge.
/// Fixed header fields are read with sequential little-endian reads, never by overlaying a C# struct, so the result does
/// not depend on host endianness or struct padding. All lengths and counts come from the file and are bounded by the
/// payload that was actually read before any buffer is sized from them.
/// </para>
/// </remarks>
internal static class CanParser
{
    #region Constants

    /// <summary>
    /// Minimum size of a BLF Type 1 / Type 86 CAN message payload:
    /// channel(2) + flags(1) + dlc(1) + id(4) + data(8) = 16 bytes.
    /// </summary>
    private const int _CanMessageMinSize = 16;

    /// <summary>Minimum size of a BLF Type 2 CAN error payload: channel(2) + length(2) + reserved(4) = 8 bytes.</summary>
    private const int _CanErrorMinSize = 8;

    /// <summary>Minimum size of a BLF Type 3 CAN overload payload: channel(2) + reserved(2) = 4 bytes.</summary>
    private const int _CanOverloadMinSize = 4;

    /// <summary>
    /// Packed size of the Type 73 CAN error-ext header:
    /// channel(2) + length(2) + flags(4) + ecc(1) + position(1) + dlc(1) + reserved(1) +
    /// frame length ns(4) + id(4) + errorCodeExt(2) + reserved(2) = 24 bytes.
    /// </summary>
    private const int _CanErrorExtMinSize = 24;

    /// <summary>
    /// Packed size of the Type 100 CAN FD header (data follows at offset 20).
    /// </summary>
    private const int _CanFdMessageHeaderSize = 20;

    /// <summary>
    /// Packed size of the Type 101 CAN FD header (data follows at offset 40).
    /// </summary>
    private const int _CanFdMessage64HeaderSize = 40;

    /// <summary>
    /// Packed size of the Type 104 CAN FD error header. Channel is byte 0.
    /// </summary>
    private const int _CanFdError64HeaderSize = 44;

    /// <summary>
    /// SocketCAN error-frame size (the full 16-byte <c>can_frame</c> layout, zero-filled). Classic data frames are 8 plus
    /// the captured length, not this constant.
    /// </summary>
    private const int _SocketCanClassicSize = 16;

    /// <summary>SocketCAN FD frame header size (before data).</summary>
    private const int _SocketCanFdHeaderSize = 8;

    /// <summary>Classic CAN maximum data length.</summary>
    private const int _CanMaxDataLength = 8;

    /// <summary>
    /// CAN FD maximum data length (a fact of the CAN FD format). Also the cap on a CAN FD data copy, so a corrupt length field
    /// cannot produce an FD frame larger than the format allows.
    /// </summary>
    private const int _CanFdMaxDataLength = 64;

    #endregion

    #region Public API — Classic CAN

    /// <summary>
    /// Parses a BLF Type 1 (CAN_MESSAGE) payload into a SocketCAN classic frame.
    ///
    /// Payload layout (all little-endian):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     flags  (bit 0x80 = RTR; TX/NERR/WU unused for reconstruction)
    ///   [3]     dlc (low 4 bits)
    ///   [4..8)  id (u32 LE; bit 31 = EFF)
    ///   [8..16) data (8 bytes on the object; the SocketCAN buffer keeps only the captured length)
    /// </code>
    /// The returned frame is 8 bytes plus the data length for that DLC. RTR keeps the DLC in byte 4
    /// and omits the data tail, so the frame is 8 bytes. A bus DLC of 9–15 (data length 8) is kept in byte 7 so
    /// writers can restore it.
    /// </summary>
    /// <param name="payload">BLF object payload; at least 16 bytes.</param>
    /// <param name="frame">SocketCAN classic bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header (u16 LE at offset 0).</param>
    /// <returns><see langword="true"/> when the payload held the full 16-byte message; otherwise <see langword="false"/>.</returns>
    internal static bool TryParseCanMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanMessage(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanMessage(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN classic bytes on success.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanMessage(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
        => _TryParseCanMessageCore(payload, slabs, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 86 (CAN_MESSAGE2) payload into a SocketCAN classic frame.
    /// The first 16 bytes are identical to Type 1; any trailing bytes are ignored.
    /// </summary>
    internal static bool TryParseCanMessage2(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanMessage2(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanMessage2(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN classic bytes on success.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanMessage2(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
        => _TryParseCanMessageCore(payload, slabs, out frame, out channel);

    /// <summary>
    /// Parses a BLF Type 2 (CAN_ERROR) payload into a SocketCAN error frame.
    ///
    /// Payload layout:
    /// <code>
    ///   [0..2) channel (u16 LE)
    ///   [2..4) length  (u16 LE, always 0)
    ///   [4..8) reserved (u32 LE)
    /// </code>
    /// The object carries no error class, so the result is a generic error marker: a 16-byte zero frame whose id has only
    /// the SocketCAN error flag set.
    /// </summary>
    internal static bool TryParseCanError(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanError(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanError(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanError(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanErrorMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = _BuildSocketCanErrorFrame(slabs, BlfConstants.SocketCanErr);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 3 (CAN_OVERLOAD) payload into a SocketCAN error frame.
    ///
    /// Payload layout:
    /// <code>
    ///   [0..2) channel (u16 LE)
    ///   [2..4) reserved (u16 LE)
    /// </code>
    /// </summary>
    internal static bool TryParseCanOverload(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanOverload(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanOverload(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanOverload(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanOverloadMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        // CAN overload is a bus-level condition; produce a generic SocketCAN error frame
        frame = _BuildSocketCanErrorFrame(slabs, BlfConstants.SocketCanErr);
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 73 (CAN_ERROR_EXT) payload into a SocketCAN error frame.
    ///
    /// Payload layout (24 bytes, little-endian):
    /// <code>
    ///   [0..2)   channel (u16 LE)
    ///   [2..4)   length (u16 LE)
    ///   [4..8)   flags (u32 LE)
    ///   [8]      ecc
    ///   [9]      position
    ///   [10]     dlc
    ///   [11]     reserved1
    ///   [12..16) frameLength_in_ns (u32 LE)
    ///   [16..20) id (u32 LE; bit 31 set means 29-bit extended ID)
    ///   [20..22) errorCodeExt (u16 LE)
    ///   [22..24) reserved2
    /// </code>
    /// The reconstructed SocketCAN frame is the same generic error marker as for Type 2; the ecc, position, id, and dlc
    /// fields of this object are not mapped, only the size is validated and the channel read.
    /// </summary>
    internal static bool TryParseCanErrorExt(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanErrorExt(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanErrorExt(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanErrorExt(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanErrorExtMinSize)
        {
            return false;
        }

        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        frame = _BuildSocketCanErrorFrame(slabs, BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Public API — CAN XL

    /// <summary>
    /// Parses a BLF Type 139 (<c>CAN_XL_CHANNEL_FRAME</c>) payload into SocketCAN XL.
    /// Requires at least <see cref="BlfConstants.CanXlChannelFrameHeaderSize"/> bytes.
    /// Layout: see that constant. Reconstruction uses identifier@12, SDU@16, dataLength@20,
    /// VCID@26, acceptance@28, flags@48, and payload at 104. When the XLF flag is clear,
    /// nested classic (8 bytes plus the data length) or CAN FD frames are reconstructed from FDF/BRS/ESI/RTR.
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN XL, FD, or classic bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from payload byte 0, zero-extended.</param>
    internal static bool TryParseCanXlChannelFrame(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanXlChannelFrame(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanXlChannelFrame(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanXlChannelFrame(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize)
        {
            return false;
        }

        // Sequential LE reads of the 104-byte packed Type 139 header. Do not overlay a C# struct.
        channel = payload[0];
        uint frameIdentifier = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);
        byte sduType = payload[16];
        ushort declaredDataLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[20..]);
        byte vcid = payload[26];
        uint acceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[48..]);

        // The declared data length is a u16 from the file. The copy is clamped to the bytes really present after the header,
        // so the buffer below is never larger than the object that was already read (no allocation amplification from a
        // crafted length) and the slice cannot run past the payload. A truncated XL frame is kept at its captured length.
        int available = payload.Length - BlfConstants.CanXlChannelFrameHeaderSize;
        int actualDataLen = Math.Min((int)declaredDataLength, available);

        // A Type 139 object whose XLF flag is clear is a classic or CAN FD frame recorded on a CAN XL channel.
        if ((flags & BlfConstants.BlfCanXlFlagXlf) == 0)
        {
            // Pass the declared length. Classic rejects a payload that does not contain
            // those data bytes. The CAN FD branch clamps to the bytes that are present.
            return _TryParseNestedClassicOrFdFromCanXl(
                payload, slabs, flags, frameIdentifier, declaredDataLength, out frame);
        }

        // True CAN XL frame: translate the BLF flag bits into the SocketCAN XL flag byte (header byte 4).
        byte socketFlags = BlfConstants.SocketCanXlXlf;
        if ((flags & BlfConstants.BlfCanXlFlagSec) != 0)
        {
            socketFlags |= BlfConstants.SocketCanXlSec;
        }

        if ((flags & BlfConstants.BlfCanXlFlagRrs) != 0)
        {
            socketFlags |= BlfConstants.SocketCanXlRrs;
        }

        // 12-byte SocketCAN XL header + data. RentZeroed (not a plain slab allocation) because header byte 0 is never
        // written and must be zero for slab and non-slab callers alike (slab memory is otherwise uninitialised).
        // Only the 11-bit priority part of the identifier is stored, big-endian at bytes 2-3.
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, 12 + actualDataLen);
        Span<byte> xl = buffer.Span;
        xl[1] = vcid;
        BinaryPrimitives.WriteUInt16BigEndian(xl[2..], (ushort)(frameIdentifier & 0x7FFu));
        xl[4] = socketFlags;
        xl[5] = sduType;
        BinaryPrimitives.WriteUInt16LittleEndian(xl[6..], (ushort)actualDataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(xl[8..], acceptanceField);

        if (actualDataLen > 0)
        {
            payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen).CopyTo(xl[12..]);
        }

        frame = buffer;

        return true;
    }

    #endregion

    #region Public API — CAN FD

    /// <summary>
    /// Parses a BLF Type 100 (CAN_FD_MESSAGE) payload into a SocketCAN FD frame.
    /// 20-byte header, data at offset 20 (all little-endian except reconstructed SocketCAN ID):
    /// <code>
    ///   [0..2)  channel (u16 LE)
    ///   [2]     flags (bit 0x80 = RTR)
    ///   [3]     DLC (low 4 bits)
    ///   [4..8)  id (u32 LE; bit 31 = EFF)
    ///   [8..12) frame length on bus (u32 LE, nanoseconds)
    ///   [12]    arbitration bit count
    ///   [13]    CAN FD flags (EDL 0x01, BRS 0x02, ESI 0x04)
    ///   [14]    valid data bytes
    ///   [15..20) reserved
    ///   [20..)  data
    /// </code>
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN FD bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanFdMessage(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanFdMessage(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanFdMessage(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanFdMessage(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanFdMessageHeaderSize)
        {
            return false;
        }

        // Sequential LE reads of the 20-byte packed Type 100 header. Do not overlay a C# struct.
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte flags = payload[2];
        byte dlc = (byte)(payload[3] & 0x0F);
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        byte canFdFlags = payload[13];
        byte validDataBytes = payload[14];

        // The DLC means a different length depending on EDL: 9-15 is 8 bytes for classic CAN but 12-64 for CAN FD.
        // The copied length is the smallest of four independent bounds, so no single corrupt field can over-read or
        // oversize the frame: the DLC's nominal length, the bytes the logger says are valid, the bytes physically present
        // after the header, and the CAN FD maximum.
        bool isCanFd = (canFdFlags & BlfConstants.BlfCanFdEdl) != 0;
        ReadOnlySpan<byte> dlcTable = isCanFd
            ? BlfConstants.CanFdDlcToLength
            : BlfConstants.CanDlcToLength;
        int tableLen = dlcTable[dlc];
        int available = payload.Length - _CanFdMessageHeaderSize;
        int actualDataLen = Math.Min(tableLen, Math.Min(validDataBytes, Math.Min(available, _CanFdMaxDataLength)));

        // The BLF id keeps the extended-frame marker in bit 31; SocketCAN keeps the same bit as CAN_EFF_FLAG, so the
        // 29 identifier bits are masked out first and the flag is re-applied explicitly.
        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        // A classic object (EDL clear) is stored exactly like a Type 1 frame: a remote frame has no data bytes but keeps the
        // requested DLC in byte 4, and a DLC of 9-15 is kept in byte 7 (len8_dlc). Problem avoided: building it as an FD
        // frame lost both, so the same bus frame read from Type 100 and from Type 1 gave different SocketCAN bytes and a
        // writer restoring the DLC wrote 0 for a remote frame.
        if (!isCanFd)
        {
            bool remote = (flags & BlfConstants.BlfCanMessageFlagRtr) != 0;
            if (remote)
            {
                socketCanId |= BlfConstants.SocketCanRtr;
                actualDataLen = 0;
            }

            frame = _BuildSocketCanClassicFrame(
                slabs, socketCanId, dlc, remote, payload.Slice(_CanFdMessageHeaderSize, actualDataLen));
            return true;
        }

        // Map BLF FD flags onto SocketCAN fd_flags: EDL<<2 | ESI>>1 | BRS>>1.
        byte socketFdFlags = (byte)(
            ((canFdFlags & BlfConstants.BlfCanFdEdl) << 2)
            | ((canFdFlags & BlfConstants.BlfCanFdEsi) >> 1)
            | ((canFdFlags & BlfConstants.BlfCanFdBrs) >> 1));
        frame = _BuildSocketCanFdFrame(
            slabs, socketCanId, socketFdFlags, payload.Slice(_CanFdMessageHeaderSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 101 (CAN_FD_MESSAGE_64) payload into a SocketCAN FD frame.
    /// 40-byte header, data at offset 40:
    /// <code>
    ///   [0]      channel (u8)
    ///   [1]      DLC (low 4 bits)
    ///   [2]      valid data bytes
    ///   [3]      tx count
    ///   [4..8)   id (u32 LE; bit 31 = EFF)
    ///   [8..12)  frame length on bus (u32 LE, nanoseconds)
    ///   [12..16) flags (u32 LE; remote 0x10, EDL 0x1000, BRS 0x2000, ESI 0x4000)
    ///   [16..40) bit-timing / CRC fields (unused by reconstruction)
    ///   [40..)   data
    /// </code>
    /// </summary>
    /// <param name="payload">BLF object payload; length is already bounded by the object header parser.</param>
    /// <param name="frame">SocketCAN FD bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from payload byte 0, zero-extended.</param>
    internal static bool TryParseCanFdMessage64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanFdMessage64(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanFdMessage64(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanFdMessage64(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanFdMessage64HeaderSize)
        {
            return false;
        }

        channel = payload[0];
        byte dlc = (byte)(payload[1] & 0x0F);
        byte validDataBytes = payload[2];
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]);

        // Same four-way length bound as Type 100 (DLC table, valid byte count, bytes present, FD maximum); here the EDL bit
        // lives in the u32 flags word instead of a separate FD flags byte.
        bool isCanFd = (flags & BlfConstants.CanFd64FlagEdl) != 0;
        ReadOnlySpan<byte> dlcTable = isCanFd
            ? BlfConstants.CanFdDlcToLength
            : BlfConstants.CanDlcToLength;
        int tableLen = dlcTable[dlc];
        int available = payload.Length - _CanFdMessage64HeaderSize;
        int actualDataLen = Math.Min(tableLen, Math.Min(validDataBytes, Math.Min(available, _CanFdMaxDataLength)));

        // Identifier: bit 31 is the extended-frame marker in BLF and CAN_EFF_FLAG in SocketCAN (same as Type 100).
        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        // A classic object (EDL clear) is stored like a Type 1 frame: a remote frame keeps the requested DLC in byte 4 and a
        // DLC of 9-15 is kept in byte 7 (the same rule as Type 100 above).
        if (!isCanFd)
        {
            bool remote = (flags & BlfConstants.CanFd64FlagRemoteFrame) != 0;
            if (remote)
            {
                socketCanId |= BlfConstants.SocketCanRtr;
                actualDataLen = 0;
            }

            frame = _BuildSocketCanClassicFrame(
                slabs, socketCanId, dlc, remote, payload.Slice(_CanFdMessage64HeaderSize, actualDataLen));
            return true;
        }

        // Flags: translate each BLF bit into its SocketCAN fd_flags counterpart (the bit positions differ between the two).
        byte socketFdFlags = 0;
        if ((flags & BlfConstants.CanFd64FlagEdl) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdFdf;
        }

        if ((flags & BlfConstants.CanFd64FlagBrs) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdBrs;
        }

        if ((flags & BlfConstants.CanFd64FlagEsi) != 0)
        {
            socketFdFlags |= BlfConstants.SocketCanFdEsi;
        }

        frame = _BuildSocketCanFdFrame(
            slabs, socketCanId, socketFdFlags, payload.Slice(_CanFdMessage64HeaderSize, actualDataLen));
        return true;
    }

    /// <summary>
    /// Parses a BLF Type 104 (CAN_FD_ERROR_64) payload into a SocketCAN error frame.
    /// Requires the 44-byte header; channel is byte 0. Remaining fields are unused.
    /// </summary>
    internal static bool TryParseCanFdError64(
        ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel)
        => FrameSlabAllocator.ToArray(TryParseCanFdError64(payload, null, out ReadOnlyMemory<byte> memory, out channel), memory, out frame);

    /// <inheritdoc cref="TryParseCanFdError64(ReadOnlySpan{byte}, out byte[], out ushort)"/>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="frame">SocketCAN bytes on success; empty on failure.</param>
    /// <param name="channel">Channel from the header.</param>
    internal static bool TryParseCanFdError64(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanFdError64HeaderSize)
        {
            return false;
        }

        channel = payload[0];
        frame = _BuildSocketCanErrorFrame(slabs, BlfConstants.SocketCanErr);
        return true;
    }

    #endregion

    #region Public API — channel peek

    /// <summary>
    /// Reads only the channel field for a CAN / CAN FD / CAN XL object, using the same
    /// minimum sizes as the corresponding parser. The index scan uses this instead of a full parse so that indexing a
    /// capture builds no frame bytes; it must accept exactly the objects whose parser size check passes, otherwise the
    /// scanner and the later re-read would disagree about which objects are frames.
    /// </summary>
    /// <param name="objectType">BLF object type.</param>
    /// <param name="payload">BLF object payload.</param>
    /// <param name="channel">Channel on success; zero otherwise. Types 101, 104, and 139 store it as one byte, the rest as u16 LE.</param>
    /// <returns><see langword="false"/> when the type is not a CAN object type or the payload is shorter than the parser's minimum.</returns>
    internal static bool TryGetChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        switch (objectType)
        {
            case BlfConstants.ObjTypeCanMessage:
            case BlfConstants.ObjTypeCanMessage2:
                if (payload.Length < _CanMessageMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanError:
                if (payload.Length < _CanErrorMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanOverload:
                if (payload.Length < _CanOverloadMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanErrorExt:
                if (payload.Length < _CanErrorExtMinSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanFdMessage:
                if (payload.Length < _CanFdMessageHeaderSize)
                {
                    return false;
                }

                channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                return true;

            case BlfConstants.ObjTypeCanFdMessage64:
                if (payload.Length < _CanFdMessage64HeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            case BlfConstants.ObjTypeCanFdError64:
                if (payload.Length < _CanFdError64HeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            case BlfConstants.ObjTypeCanXlChannelFrame:
                if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize)
                {
                    return false;
                }

                channel = payload[0];
                return true;

            default:
                return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Core parser for Type 1 and Type 86 CAN messages (identical first-16-byte layout).
    /// Reads the Type 1 / Type 86 header: flags at offset 2, DLC at 3, EFF in ID bit 31.
    /// </summary>
    private static bool _TryParseCanMessageCore(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel)
    {
        frame = default;
        channel = 0;

        if (payload.Length < _CanMessageMinSize)
        {
            return false;
        }

        // The classic header is fixed at 16 bytes (checked above), so the 8 data bytes are always present when read.
        channel = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        byte flags = payload[2];
        byte dlc = (byte)(payload[3] & 0x0F);
        uint rawId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);

        // DLC 9-15 are valid on the bus and all mean 8 data bytes in classic CAN; the table maps them to 8.
        byte dataLen = BlfConstants.CanDlcToLength[dlc];

        // Reconstruct SocketCAN ID. EFF lives in BLF id bit 31, not in the flags byte.
        uint socketCanId = rawId & 0x1FFF_FFFF;
        if ((rawId & 0x8000_0000u) != 0)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        // RTR zeros the reconstructed payload; SocketCAN still carries the DLC in byte 4.
        if ((flags & BlfConstants.BlfCanMessageFlagRtr) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            dataLen = 0;
        }

        // Captured length is the header plus the DLC's data bytes. Do not keep a zero tail
        // out to 16: a frame with fewer data bytes is stored at its real length, so it holds less memory than a fixed
        // 16-byte buffer would. DLC 8 is still 16 bytes. RTR has copyLen 0, so the frame
        // stays 8 bytes. copyLen is at most 8, and _CanMessageMinSize is 16, so the object already contains
        // those data bytes. A shorter object returned false above.
        int copyLen = Math.Min((int)dataLen, _CanMaxDataLength);
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, 8 + copyLen);
        Span<byte> socketCan = buffer.Span;
        BinaryPrimitives.WriteUInt32BigEndian(socketCan, socketCanId);

        // SocketCAN classic: byte 4 is the payload byte count (0-8; for RTR the requested count). A bus DLC of 9-15 is
        // kept in byte 7 (can_frame.len8_dlc) so writers can restore it. The ASC reader and both writers use the same rule.
        socketCan[BlfConstants.SocketCanLenOffset] = (byte)Math.Min((int)dlc, _CanMaxDataLength);
        socketCan[BlfConstants.SocketCanLen8DlcOffset] = dlc > _CanMaxDataLength ? dlc : (byte)0;
        if (copyLen > 0)
        {
            payload.Slice(8, copyLen).CopyTo(socketCan[8..]);
        }

        frame = buffer;

        return true;
    }

    /// <summary>
    /// Builds a classic SocketCAN frame from a CAN FD object whose EDL bit is clear (Type 100 / 101): id BE, byte 4 the data
    /// byte count (for a remote frame the requested DLC, capped at 8), byte 7 the bus DLC when it is 9-15, then the data. The
    /// same layout as <see cref="_TryParseCanMessageCore"/> produces for Type 1, with the frame stored at its real length.
    /// </summary>
    private static ReadOnlyMemory<byte> _BuildSocketCanClassicFrame(
        FrameSlabAllocator? slabs, uint socketCanId, byte dlc, bool remote, ReadOnlySpan<byte> data)
    {
        // RentZeroed: bytes 5-6 (flags, reserved) are never written and must read as zero in slab-backed memory.
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, 8 + data.Length);
        Span<byte> frame = buffer.Span;
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[BlfConstants.SocketCanLenOffset] = remote
            ? (byte)Math.Min((int)dlc, _CanMaxDataLength)
            : (byte)data.Length;
        frame[BlfConstants.SocketCanLen8DlcOffset] = dlc > _CanMaxDataLength ? dlc : (byte)0;
        if (data.Length > 0)
        {
            data.CopyTo(frame[8..]);
        }

        return buffer;
    }

    /// <summary>
    /// Builds a SocketCAN FD frame: id BE, len as byte count, flags at byte 5, data of <paramref name="data"/>.Length.
    /// The frame is 8 plus the actual data length, not padded to 64 bytes. Callers bound <paramref name="data"/> to 64 bytes
    /// (the length byte is a single byte).
    /// </summary>
    private static ReadOnlyMemory<byte> _BuildSocketCanFdFrame(
        FrameSlabAllocator? slabs, uint socketCanId, byte socketFdFlags, ReadOnlySpan<byte> data)
    {
        // RentZeroed: bytes 6-7 (reserved) are never written and must read as zero in slab-backed memory.
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, _SocketCanFdHeaderSize + data.Length);
        Span<byte> frame = buffer.Span;
        BinaryPrimitives.WriteUInt32BigEndian(frame, socketCanId);
        frame[4] = (byte)data.Length;
        frame[5] = socketFdFlags;
        if (data.Length > 0)
        {
            data.CopyTo(frame[8..]);
        }

        return buffer;
    }

    /// <summary>
    /// Builds a minimal 16-byte SocketCAN error frame with the given error flag. Zero-filled apart from the id, so the
    /// error class and data bytes read as "unspecified".
    /// </summary>
    private static ReadOnlyMemory<byte> _BuildSocketCanErrorFrame(FrameSlabAllocator? slabs, uint socketCanErrId)
    {
        Memory<byte> errorFrame = FrameSlabAllocator.RentZeroed(slabs, _SocketCanClassicSize);
        BinaryPrimitives.WriteUInt32BigEndian(errorFrame.Span, socketCanErrId);
        return errorFrame;
    }

    /// <summary>
    /// Reconstructs classic or CAN FD SocketCAN bytes from a Type 139 object whose XLF flag is clear.
    /// EFF is derived from the identifier value, not from a flag: it is set when the 29-bit identifier exceeds 11 bits,
    /// so an extended frame with an identifier below 0x800 comes back as a standard frame. RTR applies only to classic frames.
    /// </summary>
    /// <param name="payload">Type 139 payload including the 104-byte header.</param>
    /// <param name="slabs">Frame slab allocator, or null for a new array.</param>
    /// <param name="flags">The u32 flags word at offset 48.</param>
    /// <param name="frameIdentifier">The identifier at offset 12.</param>
    /// <param name="actualDataLen">
    /// The data length <em>declared</em> in the header; this method bounds it against the bytes present and the
    /// format maximum (the parameter is reassigned with the bounded value).
    /// </param>
    /// <param name="frame">SocketCAN classic or FD bytes on success; empty on failure.</param>
    private static bool _TryParseNestedClassicOrFdFromCanXl(
        ReadOnlySpan<byte> payload,
        FrameSlabAllocator? slabs,
        uint flags,
        uint frameIdentifier,
        int actualDataLen,
        out ReadOnlyMemory<byte> frame)
    {
        bool isCanFd = (flags & BlfConstants.BlfCanXlFlagFdf) != 0;
        uint socketCanId = frameIdentifier & 0x1FFF_FFFFu;
        if (socketCanId > 0x7FFu)
        {
            socketCanId |= BlfConstants.SocketCanEff;
        }

        if (isCanFd)
        {
            // CAN FD: keep only the bytes actually present after the header (a truncated frame is kept at its captured
            // length rather than rejected), capped at the CAN FD maximum of 64.
            int availableFd = payload.Length - BlfConstants.CanXlChannelFrameHeaderSize;
            actualDataLen = Math.Min(actualDataLen, availableFd);
            actualDataLen = Math.Min(actualDataLen, _CanFdMaxDataLength);
            byte socketFdFlags = BlfConstants.SocketCanFdFdf;
            if ((flags & BlfConstants.BlfCanXlFlagBrs) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdBrs;
            }

            if ((flags & BlfConstants.BlfCanXlFlagEsi) != 0)
            {
                socketFdFlags |= BlfConstants.SocketCanFdEsi;
            }

            frame = _BuildSocketCanFdFrame(
                slabs,
                socketCanId,
                socketFdFlags,
                payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen));
            return true;
        }

        if ((flags & BlfConstants.BlfCanXlFlagRemoteFrame) != 0)
        {
            socketCanId |= BlfConstants.SocketCanRtr;
            actualDataLen = 0;
        }

        // Same captured-length rule as Type 1: 8 plus the data bytes. A declared length
        // that overruns the payload is a failure, not a zero-padded 16-byte buffer.
        actualDataLen = Math.Min(actualDataLen, _CanMaxDataLength);
        if (payload.Length < BlfConstants.CanXlChannelFrameHeaderSize + actualDataLen)
        {
            frame = default;
            return false;
        }

        // The nested classic frame carries a byte count (dataLength), never a DLC above 8, so len8_dlc stays 0.
        Memory<byte> buffer = FrameSlabAllocator.RentZeroed(slabs, 8 + actualDataLen);
        Span<byte> classic = buffer.Span;
        BinaryPrimitives.WriteUInt32BigEndian(classic, socketCanId);
        classic[BlfConstants.SocketCanLenOffset] = (byte)actualDataLen;
        if (actualDataLen > 0)
        {
            payload.Slice(BlfConstants.CanXlChannelFrameHeaderSize, actualDataLen).CopyTo(classic[8..]);
        }

        frame = buffer;

        return true;
    }

    #endregion
}
