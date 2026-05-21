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
    /// <summary>
    /// Builds an Ethernet Frame (Type 71) payload from a raw Ethernet frame.
    /// <para>
    /// BLF layout:
    /// <c>src(6) + channel(2) + dst(6) + dir(2) + ethertype(2 BE) + tpid(2 BE) + tci(2 BE) + payload_len(2 LE) + payload_data</c>.
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

        // Check for VLAN tag (0x8100)
        if (frame.Length >= 18 && frame[12] == 0x81 && frame[13] == 0x00)
        {
            tpid = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
            tci = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(14));
            ethertype = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16));
            payloadOffset = 18;
        }
        else
        {
            ethertype = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
            payloadOffset = 14;
        }

        ReadOnlySpan<byte> payload = frame.Length > payloadOffset
            ? frame.Slice(payloadOffset)
            : ReadOnlySpan<byte>.Empty;
        ushort payloadLen = (ushort)payload.Length;

        // Vector blf_ethernetframeheader_t (per Wireshark wiretap/blf.h):
        //   src(6) + channel(2 LE) + dst(6) + dir(2 LE) + ethtype(2) + tpid(2) +
        //   tci(2) + payload_len(2 LE) + uint64 res = 32 bytes total.
        // The trailing 8-byte reserved field MUST be present; tshark always reads
        // sizeof(blf_ethernetframeheader_t) = 32 bytes for the header and expects
        // the actual frame payload to start at offset 32. Writing only 24 bytes
        // here causes tshark to read 8 bytes of payload as part of the header,
        // then over-read by 8 bytes when fetching the payload — for the last
        // object in the file this trips "appears to have been cut short".
        // The ethtype/tpid/tci numeric values are stored little-endian in the
        // BLF struct but their wire byte order on Ethernet is big-endian; since
        // the high/low bytes are swapped the LE store of the BE-read value
        // happens to round-trip the original two bytes identically. We keep the
        // BE writes here because our reader is symmetric.
        Span<byte> header = output.Reserve(32);
        header.Clear(); // zero everything (covers the 8-byte res field)
        src.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), channel);
        dst.CopyTo(header.Slice(8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14), direction);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(16), ethertype);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(18), tpid);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(20), tci);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22), payloadLen);
        // header[24..32] = 0 (uint64 res, already zeroed by Clear())

        // Append payload data
        if (payloadLen > 0)
        {
            output.Write(payload);
        }

        return true;
    }

    /// <summary>
    /// Builds a CAN Message (Type 1) payload from a SocketCAN frame.
    /// <para>
    /// SocketCAN layout: <c>id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-8)</c>.
    /// BLF layout: <c>channel(2 LE) + flags(1) + dlc(1) + id(4 LE) + data(0-8)</c>.
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

        // Convert SocketCAN ID → BLF ID (29-bit mask)
        uint blfId = canId & 0x1FFFFFFF;

        // BLF CAN flags
        byte blfFlags = 0;
        if ((canId & BlfConstants.SocketCanRtr) != 0)
        {
            blfFlags |= BlfConstants.CanFlagRtr;
        }

        if ((canId & BlfConstants.SocketCanEff) != 0)
        {
            blfFlags |= (byte)BlfConstants.BlfCanMessageFlagEff;
        }

        // Data length from DLC lookup table
        byte dataLen = BlfConstants.CanDlcToLength[Math.Min(dlc, (byte)15)];
        int dataAvailable = Math.Max(0, socketCanFrame.Length - 8);
        int actualDataLen = Math.Min(dataLen, Math.Min(dataAvailable, 8));

        // Header: channel(2) + dlc(1) + flags(1) + id(4) = 8 bytes
        // Layout matches CanParser.TryParseCanMessage which expects dlc@2, flags@3.
        Span<byte> header = output.Reserve(8);
        BinaryPrimitives.WriteUInt16LittleEndian(header, channel);
        header[2] = dlc;
        header[3] = blfFlags;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), blfId);

        // Always emit 8 data bytes (zero-padded) to match the canonical Vector
        // CAN message layout. The parser requires the 16-byte object size and
        // the round-trip comparison expects a fixed 16-byte SocketCAN frame.
        Span<byte> dataOut = output.Reserve(8);
        dataOut.Clear();
        if (actualDataLen > 0)
        {
            socketCanFrame.Slice(8, actualDataLen).CopyTo(dataOut);
        }

        return true;
    }

    /// <summary>
    /// Builds a CAN FD Message (Type 100) payload from a SocketCAN FD frame.
    /// <para>
    /// SocketCAN FD layout: <c>id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-64)</c>.
    /// BLF layout: <c>channel(2 LE) + flags(1) + dlc(1) + id(4 LE) + frameLength(4 LE) +
    /// arbBitCount(1) + canfdflags(1) + validDataBytes(1) + reserved(5) + data</c>.
    /// </para>
    /// </summary>
    /// <param name="socketCanFrame">SocketCAN FD frame bytes.</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildCanFdMessagePayload(
        ReadOnlySpan<byte> socketCanFrame, ushort channel,
        PooledBuffer output)
    {
        // Minimum SocketCAN FD: id(4) + dlc(1) + flags(1) + reserved(2) = 8 bytes
        if (socketCanFrame.Length < 8)
        {
            return false;
        }

        output.Reset();

        uint canId = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        // SocketCAN FD `len` field at offset 4 is the actual byte count (0..64),
        // not a DLC code. BLF stores it as a 4-bit DLC index that the parser
        // expands via CanFdDlcToLength. Convert byte count → DLC code by reverse
        // lookup so values like 12, 16, 20, 24, 32, 48, 64 round-trip correctly.
        byte payloadByteCount = socketCanFrame[4];
        byte dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount(payloadByteCount);
        byte socketCanFdFlags = socketCanFrame[5];

        // Convert SocketCAN ID → BLF ID (29-bit mask)
        uint blfId = canId & 0x1FFFFFFF;

        // Map SocketCAN FD flags → BLF CAN FD flags. FDF (FD format) is the
        // canonical FD indicator; without it the parser would misclassify the
        // frame as classic CAN.
        byte canFdFlags = 0;
        if ((socketCanFdFlags & BlfConstants.SocketCanFdFdf) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdEdl; // FDF → EDL
        }
        if ((socketCanFdFlags & BlfConstants.SocketCanFdBrs) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdBrs;
        }
        if ((socketCanFdFlags & BlfConstants.SocketCanFdEsi) != 0)
        {
            canFdFlags |= BlfConstants.BlfCanFdEsi;
        }

        // BLF CAN message flags (u32 in Type 100; EFF is 0x04, same as classic single-byte flags)
        uint blfFlags32 = 0;
        if ((canId & BlfConstants.SocketCanRtr) != 0)
        {
            blfFlags32 |= BlfConstants.CanFlagRtr;
        }

        if ((canId & BlfConstants.SocketCanEff) != 0)
        {
            blfFlags32 |= BlfConstants.BlfCanMessageFlagEff;
        }

        // Data lengths from FD DLC lookup table; validDataBytes is the actual
        // bytes carried (may be less than the DLC-implied length when payload
        // is shorter than the next DLC bucket).
        byte dataLen = BlfConstants.CanFdDlcToLength[Math.Min(dlc, (byte)15)];
        int dataAvailable = Math.Max(0, socketCanFrame.Length - 8);
        byte validDataBytes = (byte)Math.Min(payloadByteCount, (byte)dataAvailable);
        validDataBytes = (byte)Math.Min(validDataBytes, dataLen);

        // Header layout (24 bytes) matches CanParser.TryParseCanFdMessage:
        //   [0..2]  channel (u16 LE)
        //   [2]     dlc
        //   [3]     validPayloadLength
        //   [4..8]  txCount (u32 LE)            -- zero
        //   [8..12] can_id (u32 LE)
        //   [12..16] frameLength (u32 LE)       -- total struct size
        //   [16..20] blfFlags (u32 LE)
        //   [20]    fdFlags (BLF EDL/BRS/ESI)
        //   [21..24] reserved                    -- zero
        Span<byte> header = output.Reserve(24);
        header.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(header, channel);
        header[2] = dlc;
        header[3] = validDataBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8), blfId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12), (uint)(24 + validDataBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), blfFlags32);
        header[20] = canFdFlags;

        // Data (variable up to 64 bytes — BLF stores only validDataBytes; the
        // parser zero-pads the resulting SocketCAN frame to 64 data bytes).
        if (validDataBytes > 0)
        {
            output.Write(socketCanFrame.Slice(8, validDataBytes));
        }

        return true;
    }

    /// <summary>
    /// Builds a FlexRay RcvMessage (Type 50) payload from a DLT_FLEXRAY frame.
    /// <para>
    /// DLT_FLEXRAY layout:
    /// <c>channel(1)|type_flags(1)|frame_id(2 BE)|cycle(1)|header_crc(2 BE)|data...</c>.
    /// BLF Type 50 layout:
    /// <c>channel(2 LE)|version(2 LE)|channel_mask(2 LE)|dir(2 LE)|client_idx(4 LE)|
    /// cluster_no(4 LE)|frame_id(2 LE)|header_crc1(2 LE)|header_crc2(2 LE)|
    /// payload_length(2 LE)|cycle(1)|tag(1)|data_flag(1)|frame_flags(1)|data...</c>.
    /// </para>
    /// </summary>
    /// <param name="dltFlexRayFrame">DLT_FLEXRAY frame bytes (7-byte header + data).</param>
    /// <param name="channel">BLF channel number to encode into the output payload header.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildFlexRayRcvMessagePayload(
        ReadOnlySpan<byte> dltFlexRayFrame, ushort channel, PooledBuffer output)
    {
        // DLT_FLEXRAY minimum: channel(1) + type_flags(1) + frame_id(2) + cycle(1) + header_crc(2) = 7 bytes
        if (dltFlexRayFrame.Length < 7)
        {
            return false;
        }

        output.Reset();

        // NOTE: dltFlexRayFrame[0] holds the FlexRay sub-channel (A=0, B=1) within
        // the BLF stream. We encode it into channelMask (bit 0 = A, bit 1 = B) per
        // the Vector spec. The BLF object's `channel` field is the BLF stream
        // channel coming from the source interface — this keeps all frames on the
        // same source interface mapped to the same reimport interface.
        byte flexRaySubChannel = dltFlexRayFrame[0];
        ushort channelMask = flexRaySubChannel == 0 ? (ushort)0x0001 : (ushort)0x0002;
        byte typeFlags = dltFlexRayFrame[1];
        ushort frameId = BinaryPrimitives.ReadUInt16BigEndian(dltFlexRayFrame.Slice(2));
        byte cycle = dltFlexRayFrame[4];
        ushort headerCrc = BinaryPrimitives.ReadUInt16BigEndian(dltFlexRayFrame.Slice(5));

        int dataLength = dltFlexRayFrame.Length - 7;
        ReadOnlySpan<byte> data = dataLength > 0
            ? dltFlexRayFrame.Slice(7, dataLength)
            : ReadOnlySpan<byte>.Empty;

        // Reverse the type_flags → frame_flags mapping from the parser:
        // Parser: BLF flag 0x01 → DLT bit 7 (payload preamble)
        //         BLF flag 0x02 → DLT bit 6 (null frame)
        //         BLF flag 0x04 → DLT bit 5 (sync frame)
        //         BLF flag 0x08 → DLT bit 4 (startup frame)
        byte frameFlags = 0;
        if ((typeFlags & 0x80) != 0)
        {
            frameFlags |= 0x01;
        } // payload preamble
        if ((typeFlags & 0x40) != 0)
        {
            frameFlags |= 0x02;
        } // null frame
        if ((typeFlags & 0x20) != 0)
        {
            frameFlags |= 0x04;
        } // sync frame
        if ((typeFlags & 0x10) != 0)
        {
            frameFlags |= 0x08;
        } // startup frame

        // BLF FLEXRAY_RCVMESSAGE (Type 50) — Vector blf_flexrayrcvmessage_t header is
        // 44 bytes packed (matches Wireshark wiretap/blf.h). Field-by-field LE layout:
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

    /// <summary>
    /// Maximum LIN data length.
    /// </summary>
    private const int MaxLinDataLength = 8;

    /// <summary>
    /// Builds a LIN Message V2 (Type 57) payload from a DLT_LIN frame.
    /// <para>
    /// DLT_LIN layout:
    /// <c>pid(1)|length(1)|data(0–8)|checksum(1)|errors(1)</c>.
    /// BLF Type 57 layout:
    /// <c>data(8)|crc(1)|dir(1)|simulated(1)|isEtf(1)|etfAI(1)|id(1)|dlc(1)|
    /// startOfFrame(8 LE)|baudrate(4 LE)|responseFlags(4 LE)|channel(1)|...</c>.
    /// </para>
    /// </summary>
    /// <param name="dltLinFrame">DLT_LIN frame bytes (4-byte header + data).</param>
    /// <param name="channel">BLF channel number.</param>
    /// <param name="output">Buffer to write the payload into (reset before use).</param>
    /// <returns><c>true</c> if the payload was built successfully; <c>false</c> if the frame is too short.</returns>
    internal static bool TryBuildLinMessage2Payload(
        ReadOnlySpan<byte> dltLinFrame, ushort channel, PooledBuffer output)
    {
        // DLT_LIN minimum: pid(1) + length(1) + checksum(1) + errors(1) = 4 bytes
        if (dltLinFrame.Length < 4)
        {
            return false;
        }

        output.Reset();

        byte pid = dltLinFrame[0];
        byte dlc = dltLinFrame[1];

        // Extract 6-bit frame ID from PID (strip parity bits)
        byte id = (byte)(pid & 0x3F);

        // Clamp DLC to max LIN data length
        int dataLength = Math.Min((int)dlc, (int)MaxLinDataLength);

        // Data starts at offset 2, followed by checksum and errors
        // Layout: pid(1)|length(1)|data(dataLength)|checksum(1)|errors(1)
        int expectedMinLength = 2 + dataLength + 2; // header + data + trailer
        byte checksum = 0;
        if (dltLinFrame.Length >= expectedMinLength)
        {
            checksum = dltLinFrame[2 + dataLength]; // checksum after data
        }

        // BLF LIN_MESSAGE2 (Type 57) — Vector blf_linmessage2_t is 132 bytes packed
        // (matches Wireshark wiretap/blf.h). The struct nests several smaller events:
        //   blf_linbusevent (16)            : sof(8) eventBaudrate(4) channel(2) res1(2)
        //   blf_linsynchfieldevent (32)     : linbusevent(16) synchBreakLength(8) synchDelLength(8)
        //   blf_linmessagedescriptor (40)   : linsynchfieldevent(32) supplierId(2) messageId(2)
        //                                     configuredNodeAddress(1) id(1) dlc(1) checksumModel(1)
        //   blf_lindatabytetimestampevent (112): linmessagedescriptor(40) databyteTimestamps[9](72)
        //   blf_linmessage2 (132)           : lindatabytetimestampevent(112) data[8](8) crc(2)
        //                                     dir(1) simulated(1) isEtf(1) eftAssocIndex(1)
        //                                     eftAssocEftId(1) fsmId(1) fsmState(1) res1[3](3)
        // We zero everything except channel, id, dlc, data, crc — enough for tshark to
        // accept the object and reconstruct the LIN frame on reimport.
        // NOTE: Wireshark's blf_read_linmessage2 requires sizeof(blf_linmessage2_t)
        // bytes. Although the nominal field layout is 132 bytes, C struct alignment
        // (max member alignment = 8 from uint64_t sof + databyteTimestamps[]) pads
        // the struct to 136 bytes. We must emit those 4 trailing padding bytes.
        const int LinMessage2Size = 136;
        Span<byte> payload = output.Reserve(LinMessage2Size);
        payload.Clear(); // zero-fill all fields including trailing alignment padding

        // ── blf_linbusevent (offset 0..16) ──
        // sof(0..8) = 0
        // eventBaudrate(8..12) = 0
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(12), channel); // channel
        // res1(14..16) = 0

        // ── blf_linsynchfieldevent extra (offset 16..32) ──
        // synchBreakLength(16..24) = 0
        // synchDelLength(24..32)   = 0

        // ── blf_linmessagedescriptor extra (offset 32..40) ──
        // supplierId(32..34) = 0
        // messageId(34..36)  = 0
        // configuredNodeAddress(36) = 0
        payload[37] = id;           // id (6-bit frame id)
        payload[38] = dlc;          // dlc
        // checksumModel(39) = 0

        // ── blf_lindatabytetimestampevent extra (offset 40..112) ──
        // databyteTimestamps[9] (40..112) = 0

        // ── blf_linmessage2 extra (offset 112..132) ──
        // data[8] (112..120)
        if (dataLength > 0 && dltLinFrame.Length >= 2 + dataLength)
        {
            dltLinFrame.Slice(2, dataLength).CopyTo(payload.Slice(112));
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
}
