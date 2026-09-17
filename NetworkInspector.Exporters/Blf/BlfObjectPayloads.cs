// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// Static helpers that build BLF object payloads from raw frame data.
/// Each method resets the provided <see cref="PooledBuffer"/>, writes the
/// payload, and returns <c>true</c> on success.
/// <para>
/// <b>Direction loss:</b> Direction is intentionally set to 0 (RX) for all protocols.
/// The <see cref="Frame"/> struct does not carry direction information, so the original
/// RX/TX/TX_RQ direction from the source BLF file is lost during round-trip export.
/// This is an accepted limitation — direction has no effect on the frame payload data,
/// and preserving it would require extending <see cref="Frame"/> with a breaking change
/// across all sources.
/// </para>
/// </summary>
internal static class BlfObjectPayloads
{
    #region Ethernet

    /// <summary>
    /// Builds an Ethernet Frame (Type 71) payload from a raw Ethernet frame.
    /// <para>
    /// BLF layout:
    /// <c>src(6) + channel(2) + dst(6) + dir(2) + ethertype(2 LE) + tpid(2 LE) + tci(2 LE) + payload_len(2 LE) + payload_data</c>.
    /// </para>
    /// </summary>
    /// <param name="frame">Raw Ethernet frame bytes (dst + src + ethertype + payload).</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="direction">Frame direction (0 = receive, 1 = transmit).</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildEthernetFramePayload(
        ReadOnlySpan<byte> frame, ushort channel, ushort direction,
        PooledBuffer output)
    {
        // Minimum Ethernet: dst(6) + src(6) + ethertype(2) = 14 bytes
        if (frame.Length < 14)
        {
            return false;
        }

        output.Reset();

        ReadOnlySpan<byte> dst = frame.Slice(0, 6);
        ReadOnlySpan<byte> src = frame.Slice(6, 6);

        ushort tpid = 0;
        ushort tci = 0;
        ushort ethertype;
        int payloadOffset;

        // VLAN / QinQ: 0x8100, 0x9100, and 0x88A8 all carry a 4-byte tag before the inner EtherType.
        ushort typeAt12 = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
        if (frame.Length >= 18 && (typeAt12 == 0x8100 || typeAt12 == 0x9100 || typeAt12 == 0x88A8))
        {
            tpid = typeAt12;
            tci = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(14));
            ethertype = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16));
            payloadOffset = 18;
        }
        else
        {
            ethertype = typeAt12;
            payloadOffset = 14;
        }

        ReadOnlySpan<byte> payload = frame.Length > payloadOffset
            ? frame.Slice(payloadOffset)
            : ReadOnlySpan<byte>.Empty;
        // BLF Ethernet Type 71 stores payload_len as u16 — reject oversized payloads
        // rather than silently truncating jumbo/edge frames.
        if (payload.Length > ushort.MaxValue)
        {
            return false;
        }

        ushort payloadLen = (ushort)payload.Length;

        // Type 71 Ethernet header is 32 bytes:
        //   src(6) + channel(2 LE) + dst(6) + dir(2 LE) + ethtype(2 LE) + tpid(2 LE) +
        //   tci(2 LE) + payload_len(2 LE) + reserved(8) = 32.
        // The trailing 8-byte reserved field MUST be present; readers always consume
        // 32 header bytes and expect the Ethernet payload to start at offset 32.
        // EtherType/TPID/TCI are little-endian numeric fields in the BLF header.
        Span<byte> header = output.Reserve(32);
        header.Clear(); // zero everything (covers the 8-byte res field)
        src.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), channel);
        dst.CopyTo(header.Slice(8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14), direction);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(16), ethertype);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(18), tpid);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20), tci);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22), payloadLen);
        // header[24..32] = 0 (uint64 res, already zeroed by Clear())

        // Append payload data
        if (payloadLen > 0)
        {
            output.Write(payload);
        }

        return true;
    }

    #endregion

    #region CAN classic

    /// <summary>
    /// Builds a CAN Message (Type 1) payload from a SocketCAN frame.
    /// <para>
    /// SocketCAN layout: <c>id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-8)</c>.
    /// BLF Type 1 layout:
    /// <c>channel(2 LE) + flags(1) + dlc(1) + id(4 LE, bit 31 = EFF) + data(8)</c>.
    /// </para>
    /// </summary>
    /// <param name="socketCanFrame">SocketCAN frame bytes.</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildCanMessagePayload(
        ReadOnlySpan<byte> socketCanFrame, ushort channel,
        PooledBuffer output)
    {
        // Minimum SocketCAN: id(4) + dlc(1) + flags(1) + reserved(2) = 8 bytes
        if (socketCanFrame.Length < 8)
        {
            return false;
        }

        output.Reset();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        byte dlc = socketCanFrame[4];

        // BLF stores the 29-bit ID plus EFF in bit 31 (not as a flags-byte bit).
        uint blfId = canId & 0x1FFF_FFFFu;
        if ((canId & BlfConstants.SocketCanEff) != 0)
        {
            blfId |= 0x8000_0000u;
        }

        byte blfFlags = 0;
        if ((canId & BlfConstants.SocketCanRtr) != 0)
        {
            blfFlags |= BlfConstants.BlfCanMessageFlagRtr;
        }

        byte dataLen = BlfConstants.CanDlcToLength[Math.Min(dlc, (byte)15)];
        int dataAvailable = Math.Max(0, socketCanFrame.Length - 8);
        int actualDataLen = Math.Min(dataLen, Math.Min(dataAvailable, 8));

        Span<byte> header = output.Reserve(8);
        BinaryPrimitives.WriteUInt16LittleEndian(header, channel);
        header[2] = blfFlags;
        header[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), blfId);

        // Always emit 8 data bytes (zero-padded) to match Vector CAN message layout.
        Span<byte> dataOut = output.Reserve(8);
        dataOut.Clear();
        if (actualDataLen > 0)
        {
            socketCanFrame.Slice(8, actualDataLen).CopyTo(dataOut);
        }

        return true;
    }

    #endregion

    #region CAN XL

    /// <summary>
    /// Builds a CAN XL Channel Frame (Type 139) payload from a SocketCAN XL frame.
    /// <para>
    /// SocketCAN XL: <c>[0]=0, vcid, priority(2 BE), flags, sdu, dataLength(2 LE), acceptance(4 LE), data</c>.
    /// BLF Type 139 header is 104 bytes (see <see cref="BlfConstants.CanXlChannelFrameHeaderSize"/>);
    /// payload follows at offset 104. Channel is stored as a single byte (low 8 bits of the interface channel).
    /// Direction is 0 (RX) — see class remarks on direction loss.
    /// </para>
    /// </summary>
    /// <param name="socketCanFrame">SocketCAN XL frame bytes (12 + payload).</param>
    /// <param name="channel">BLF channel number; stored as the low 8 bits.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns>
    /// <c>true</c> if the payload was built; <c>false</c> if the frame is shorter than 12 bytes,
    /// XLF is clear, or the declared dataLength exceeds the remaining bytes.
    /// </returns>
    internal static bool TryBuildCanXlChannelFramePayload(
        ReadOnlySpan<byte> socketCanFrame, ushort channel,
        PooledBuffer output)
    {
        const int SocketCanXlHeaderSize = 12;
        if (socketCanFrame.Length < SocketCanXlHeaderSize)
        {
            return false;
        }

        byte socketFlags = socketCanFrame[4];
        if ((socketFlags & BlfConstants.SocketCanXlXlf) == 0)
        {
            return false;
        }

        ushort dataLength = BinaryPrimitives.ReadUInt16LittleEndian(socketCanFrame.Slice(6));
        int required = SocketCanXlHeaderSize + dataLength;
        if (socketCanFrame.Length < required)
        {
            return false;
        }

        output.Reset();

        byte vcid = socketCanFrame[1];
        ushort priority = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(socketCanFrame.Slice(2)) & 0x7FF);
        byte sduType = socketCanFrame[5];
        uint acceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(socketCanFrame.Slice(8));

        // Type 139 stores DLC as dataLength - 1 when dataLength > 0, else 0.
        ushort dlc = dataLength > 0
            ? (ushort)(dataLength - 1)
            : (ushort)0;

        uint flags = BlfConstants.BlfCanXlFlagXlf;
        if ((socketFlags & BlfConstants.SocketCanXlSec) != 0)
        {
            flags |= BlfConstants.BlfCanXlFlagSec;
        }

        if ((socketFlags & BlfConstants.SocketCanXlRrs) != 0)
        {
            flags |= BlfConstants.BlfCanXlFlagRrs;
        }

        Span<byte> header = output.Reserve(BlfConstants.CanXlChannelFrameHeaderSize);
        header.Clear();
        header[0] = (byte)channel;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12), priority);
        header[16] = sduType;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(18), dlc);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20), dataLength);
        header[26] = vcid;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28), acceptanceField);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(48), flags);

        if (dataLength > 0)
        {
            output.Write(socketCanFrame.Slice(SocketCanXlHeaderSize, dataLength));
        }

        return true;
    }

    #endregion

    #region CAN FD

    /// <summary>
    /// Builds a CAN FD Message (Type 100) payload from a SocketCAN FD frame.
    /// 20-byte header, data at offset 20 (same layout as the Type 100 parser).
    /// The exporter writes Type 101 for FD; this builder remains for Vector-style Type 100 files.
    /// </summary>
    /// <param name="socketCanFrame">SocketCAN FD frame bytes.</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildCanFdMessagePayload(
        ReadOnlySpan<byte> socketCanFrame, ushort channel,
        PooledBuffer output)
    {
        if (socketCanFrame.Length < 8)
        {
            return false;
        }

        output.Reset();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        byte payloadByteCount = socketCanFrame[4];
        if (payloadByteCount > 64)
        {
            payloadByteCount = 64;
        }

        byte dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount(payloadByteCount);
        byte socketCanFdFlags = socketCanFrame[5];

        uint blfId = canId & 0x1FFF_FFFF;
        if ((canId & BlfConstants.SocketCanEff) != 0)
        {
            blfId |= 0x8000_0000u;
        }

        byte canFdFlags = 0;
        if ((socketCanFdFlags & BlfConstants.SocketCanFdFdf) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdEdl;
        }

        if ((socketCanFdFlags & BlfConstants.SocketCanFdBrs) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdBrs;
        }

        if ((socketCanFdFlags & BlfConstants.SocketCanFdEsi) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdEsi;
        }

        byte blfFlags = 0;
        if ((canId & BlfConstants.SocketCanRtr) != 0)
        {
            blfFlags |= BlfConstants.BlfCanMessageFlagRtr;
        }

        byte dataLen = BlfConstants.CanFdDlcToLength[Math.Min(dlc, (byte)15)];
        int dataAvailable = Math.Max(0, socketCanFrame.Length - 8);
        byte validDataBytes = (byte)Math.Min(payloadByteCount, (byte)dataAvailable);
        validDataBytes = (byte)Math.Min(validDataBytes, dataLen);

        const int CanFdMessageHeaderSize = 20;
        Span<byte> header = output.Reserve(CanFdMessageHeaderSize);
        header.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(header, channel);
        header[2] = blfFlags;
        header[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), blfId);
        header[13] = canFdFlags;
        header[14] = validDataBytes;

        if (validDataBytes > 0)
        {
            output.Write(socketCanFrame.Slice(8, validDataBytes));
        }

        return true;
    }

    /// <summary>
    /// Builds a CAN FD Message 64 (Type 101) payload from a SocketCAN FD frame.
    /// 40-byte header, unused timing fields zero, data at offset 40:
    /// <code>
    ///   [0]      channel (u8)
    ///   [1]      DLC
    ///   [2]      valid data bytes
    ///   [3]      tx count (0)
    ///   [4..8)   id (u32 LE; bit 31 = EFF)
    ///   [8..12)  frame length ns (0)
    ///   [12..16) flags (u32 LE; EDL 0x1000, BRS 0x2000, ESI 0x4000)
    ///   [16..40) unused timing / CRC fields (0)
    ///   [40..)   data
    /// </code>
    /// </summary>
    /// <param name="socketCanFrame">SocketCAN FD frame bytes.</param>
    /// <param name="channel">BLF channel number; stored as the low 8 bits.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built; <c>false</c> if the frame is shorter than 8 bytes.</returns>
    internal static bool TryBuildCanFdMessage64Payload(
        ReadOnlySpan<byte> socketCanFrame, ushort channel,
        PooledBuffer output)
    {
        if (socketCanFrame.Length < 8)
        {
            return false;
        }

        output.Reset();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        byte payloadByteCount = socketCanFrame[4];
        if (payloadByteCount > 64)
        {
            payloadByteCount = 64;
        }

        byte dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount(payloadByteCount);
        byte socketCanFdFlags = socketCanFrame[5];

        uint blfId = canId & 0x1FFF_FFFF;
        if ((canId & BlfConstants.SocketCanEff) != 0)
        {
            blfId |= 0x8000_0000u;
        }

        uint flags = 0;
        if ((socketCanFdFlags & BlfConstants.SocketCanFdFdf) != 0)
        {
            flags |= BlfConstants.CanFd64FlagEdl;
        }

        if ((socketCanFdFlags & BlfConstants.SocketCanFdBrs) != 0)
        {
            flags |= BlfConstants.CanFd64FlagBrs;
        }

        if ((socketCanFdFlags & BlfConstants.SocketCanFdEsi) != 0)
        {
            flags |= BlfConstants.CanFd64FlagEsi;
        }

        int dataAvailable = Math.Max(0, socketCanFrame.Length - 8);
        byte validDataBytes = (byte)Math.Min(payloadByteCount, (byte)dataAvailable);

        const int CanFdMessage64HeaderSize = 40;
        Span<byte> header = output.Reserve(CanFdMessage64HeaderSize);
        header.Clear();
        header[0] = (byte)channel;
        header[1] = dlc;
        header[2] = validDataBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), blfId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12), flags);

        if (validDataBytes > 0)
        {
            output.Write(socketCanFrame.Slice(8, validDataBytes));
        }

        return true;
    }

    #endregion

    #region FlexRay

    /// <summary>
    /// Builds a FlexRay RcvMessage (Type 50) payload from a LINKTYPE_FLEXRAY frame.
    /// 44-byte header. <c>frameFlags</c> at offset 36 uses
    /// NULL=0x01, SYNC=0x04, STARTUP=0x08, PAYLOAD_PREAM=0x10.
    /// </summary>
    /// <param name="linkTypeFlexRayFrame">LINKTYPE_FLEXRAY frame bytes (7-byte header + data).</param>
    /// <param name="channel">BLF channel number to encode into the output payload header.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildFlexRayRcvMessagePayload(
        ReadOnlySpan<byte> linkTypeFlexRayFrame, ushort channel, PooledBuffer output)
    {
        if (!FlexRayLinkTypeFrame.TryParseDataFrame(
            linkTypeFlexRayFrame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> data))
        {
            return false;
        }

        output.Reset();

        byte flexRaySubChannel = fields.ChannelB ? (byte)1 : (byte)0;
        ushort channelMask = flexRaySubChannel == 0 ? (ushort)0x0001 : (ushort)0x0002;
        ushort frameId = fields.FrameId;
        byte cycle = fields.Cycle;
        ushort headerCrc = fields.HeaderCrc;
        int dataLength = data.Length;

        // Map ISO indicator bits → BLF frame_flags (reverse of FlexRayParser).
        uint frameFlags = 0;
        if (fields.Ppi)
        {
            frameFlags |= 0x10;
        }
        if (!fields.Nfi)
        {
            frameFlags |= 0x01;
        }
        if (fields.Sfi)
        {
            frameFlags |= 0x04;
        }
        if (fields.Stfi)
        {
            frameFlags |= 0x08;
        }

        // BLF FLEXRAY_RCVMESSAGE (Type 50) — 44-byte packed header, little-endian:
        //   ch(2) ver(2) chMask(2) dir(2)              =  8
        //   clientIndex(4) clusterNo(4)                = +8 = 16
        //   frameId(2) headerCrc1(2) headerCrc2(2)
        //   payloadLen(2) payloadLenValid(2) cycle(2)  = +12 = 28
        //   tag(4) data(4) frameFlags(4) appParam(4)   = +16 = 44
        // The 44-byte header is followed by the FlexRay payload bytes.
        const int FlexRayRcvMessageHeaderSize = 44;
        Span<byte> header = output.Reserve(FlexRayRcvMessageHeaderSize);
        header.Clear(); // zero-fill all fields

        BinaryPrimitives.WriteUInt16LittleEndian(header, channel);                          // channel
        // header[2..4]  = version (0)
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4), channelMask);            // channelMask: bit0=A, bit1=B
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), 0x0001);                 // dir = RX
        // header[8..12]  = clientIndex (0)
        // header[12..16] = clusterNo (0)
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(16), frameId);                // frameId
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(18), headerCrc);              // headerCrc1
        // header[20..22] = headerCrc2 (0)
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22), (ushort)dataLength);     // payloadLength
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(24), (ushort)dataLength);     // payloadLengthValid
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26), cycle);                  // cycle
        // header[28..32] = tag (0)
        // header[32..36] = data (0)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(36), frameFlags);             // frameFlags
        // header[40..44] = appParameter (0)

        // Append FlexRay payload data
        if (dataLength > 0)
        {
            output.Write(data);
        }

        return true;
    }

    #endregion

    #region LIN

    /// <summary>
    /// Maximum LIN data length.
    /// </summary>
    private const int _MaxLinDataLength = 8;

    /// <summary>
    /// Builds a LIN Message V2 (Type 57) payload from a DLT_LIN frame.
    /// <para>
    /// DLT_LIN layout (8-byte header):
    /// <c>rev(1)|reserved(3)|dlc nibble at [4]|pid at [5]|checksum at [6]|errors at [7]|data at 8</c>.
    /// BLF Type 57 is a 132-byte packed nested layout (136 on disk with 4-byte alignment padding).
    /// Written fields: channel at 12, id at 37, dlc at 38, data at 112, crc low byte at 120.
    /// </para>
    /// </summary>
    /// <param name="dltLinFrame">DLT_LIN frame bytes (8-byte header + data).</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildLinMessage2Payload(
        ReadOnlySpan<byte> dltLinFrame, ushort channel, PooledBuffer output)
    {
        // DLT_LIN minimum: 8-byte header
        if (dltLinFrame.Length < 8)
        {
            return false;
        }

        output.Reset();

        byte pid = dltLinFrame[5];
        byte dlc = (byte)(dltLinFrame[4] >> 4);
        byte checksum = dltLinFrame[6];

        // Extract 6-bit frame ID from PID (strip parity bits)
        byte id = (byte)(pid & 0x3F);

        // Clamp DLC to max LIN data length
        int dataLength = Math.Min((int)dlc, (int)_MaxLinDataLength);

        // BLF LIN_MESSAGE2 (Type 57) — 132 packed bytes, stored as 136 with 4-byte alignment:
        //   [0..8)     SOF timestamp (u64 LE)
        //   [8..12)    event baudrate (u32 LE)
        //   [12..14)   channel (u16 LE)
        //   [14..16)   reserved
        //   [16..32)   sync-break / sync-delimiter lengths (two u64 LE)
        //   [32..36)   supplier id + message id (two u16 LE)
        //   [36]       configured node address
        //   [37]       6-bit frame id
        //   [38]       dlc
        //   [39]       checksum model
        //   [40..112)  nine u64 LE per-byte timestamps
        //   [112..120) data (8)
        //   [120..122) crc (u16 LE; checksum in the low byte)
        //   [122]      dir
        //   [123]      simulated
        //   [124]      isEtf
        //   [125]      eftAssocIndex
        //   [126]      eftAssocEftId
        //   [127]      fsmId
        //   [128]      fsmState
        //   [129..132) reserved
        //   [132..136) alignment padding (u64 members force 8-byte alignment)
        // Unused fields stay zero. Readers require the full 136-byte object.
        const int LinMessage2Size = 136;
        Span<byte> payload = output.Reserve(LinMessage2Size);
        payload.Clear(); // zero-fill all fields including trailing alignment padding

        // ── bus-event prefix (offset 0..16) ──
        // sof(0..8) = 0
        // eventBaudrate(8..12) = 0
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(12), channel); // channel
        // res1(14..16) = 0

        // ── sync-field extra (offset 16..32) ──
        // synchBreakLength(16..24) = 0
        // synchDelLength(24..32)   = 0

        // ── message descriptor extra (offset 32..40) ──
        // supplierId(32..34) = 0
        // messageId(34..36)  = 0
        // configuredNodeAddress(36) = 0
        payload[37] = id;           // id (6-bit frame id)
        payload[38] = dlc;          // dlc
        // checksumModel(39) = 0

        // ── per-byte timestamps (offset 40..112) ──
        // databyteTimestamps[9] (40..112) = 0

        // ── message extra (offset 112..132) ──
        // data[8] (112..120)
        if (dataLength > 0 && dltLinFrame.Length >= 8 + dataLength)
        {
            dltLinFrame.Slice(8, dataLength).CopyTo(payload.Slice(112));
        }
        // crc (120..122) — store the LIN checksum byte in the low byte; high byte stays 0.
        payload[120] = checksum;
        // dir(122)        = 0 (RX)
        // simulated(123)  = 0
        // isEtf(124)      = 0
        // eftAssocIndex(125) = 0
        // eftAssocEftId(126) = 0
        // fsmId(127)      = 0
        // fsmState(128)   = 0
        // res1(129..132)  = 0

        return true;
    }

    #endregion
}
