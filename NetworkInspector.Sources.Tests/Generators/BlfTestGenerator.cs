// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Generators;

/// <summary>
/// Builds synthetic BLF files in memory for testing.
/// Writes raw BLF binary format: 144-byte file header followed by LOBJ objects.
/// Each object = block header (16B) + V1 log object header (16B) + payload.
///
/// Timestamps use 1 ns resolution (flags = 0x02) and are relative offsets
/// from the file's measurement start time (2024-01-01 00:00:00 UTC).
///
/// LogContainer support:
/// <see cref="AddLogContainer"/> wraps the objects of an inner generator into a single
/// compressed container LOBJ (type 10) using None (0), LZ4 (1), or Zlib (2) compression.
/// <see cref="AddLogContainerWithWrongSize"/> and <see cref="AddCorruptLogContainer"/>
/// produce intentionally malformed containers for negative testing.
/// </summary>
internal sealed class BlfTestGenerator
{
    // ========================================================================
    // Constants
    // ========================================================================

    /// <summary>"LOGG" as little-endian u32.</summary>
    private const uint _FileMagic = 0x47474F4C;

    /// <summary>"LOBJ" as little-endian u32.</summary>
    private const uint _ObjectMagic = 0x4A424F4C;

    /// <summary>Minimum file header size.</summary>
    private const int _FileHeaderSize = 144;

    /// <summary>Block header (16B) + V1 log object header (16B).</summary>
    private const int _ObjectHeaderOverhead = 32;

    /// <summary>V1 header type constant.</summary>
    private const ushort _HeaderTypeV1 = 1;

    /// <summary>Timestamp flags indicating 1 ns resolution.</summary>
    private const uint _TimestampFlagsNs = 0x02;

    /// <summary>LogContainer object type — wraps one or more compressed inner LOBJ objects.</summary>
    private const uint _ObjTypeLogContainer = 10;

    /// <summary>BLF container header size in bytes (matches BlfConstants._ContainerHeaderSize).</summary>
    private const int _ContainerHeaderSize = 16;

    // BLF object type constants (matching BlfConstants)
    private const uint _ObjTypeCanMessage = 1;
    private const uint _ObjTypeCanFdMessage = 100;
    private const uint _ObjTypeCanXlChannelFrame = 139;
    private const uint _ObjTypeLinMessage = 11;
    private const uint _ObjTypeLinMessage2 = 57;
    private const uint _ObjTypeFlexRayRcvMessage = 50;
    private const uint _ObjTypeEthernetFrame = 71;
    private const uint _ObjTypeAppText = 65;

    // ========================================================================
    // State
    // ========================================================================

    private readonly List<PendingObject> _Objects = [];

    /// <summary>
    /// Measurement start time (nanoseconds since Unix epoch).
    /// Default: 2024-01-01 00:00:00 UTC = 1704067200 seconds = 1_704_067_200_000_000_000 ns
    /// </summary>
    private readonly long _StartNanos;

    /// <summary>
    /// Creates a new generator with the default start time (2024-01-01 00:00:00 UTC).
    /// </summary>
    internal BlfTestGenerator()
    {
        // 2024-01-01T00:00:00Z → 19723 days since epoch × 86400 s/day × 1e9 ns/s
        _StartNanos = 1_704_067_200_000_000_000L;
    }

    /// <summary>
    /// Creates a new generator with a custom start timestamp.
    /// </summary>
    internal BlfTestGenerator(long startNanos)
    {
        _StartNanos = startNanos;
    }

    /// <summary>Start time of this BLF file in nanoseconds since Unix epoch.</summary>
    internal long StartNanos => _StartNanos;

    // ========================================================================
    // Add methods
    // ========================================================================

    /// <summary>
    /// Adds a raw BLF object with arbitrary type and payload.
    /// </summary>
    /// <param name="objectType">BLF object type constant.</param>
    /// <param name="offsetNanos">Timestamp offset from start (nanoseconds).</param>
    /// <param name="payload">Raw object payload bytes.</param>
    internal BlfTestGenerator AddRawObject(uint objectType, long offsetNanos, byte[] payload)
    {
        _Objects.Add(new PendingObject(objectType, offsetNanos, payload));
        return this;
    }

    /// <summary>
    /// Wraps all objects from <paramref name="inner"/> into a single LogContainer LOBJ
    /// compressed with <paramref name="compressionMethod"/> (0 = None, 1 = LZ4, 2 = Zlib).
    /// </summary>
    /// <param name="compressionMethod">BLF compression constant (0/1/2).</param>
    /// <param name="inner">Generator whose queued objects become the container payload.</param>
    internal BlfTestGenerator AddLogContainer(ushort compressionMethod, BlfTestGenerator inner)
    {
        byte[] uncompressedContent = inner._BuildInnerObjectBytes();
        byte[] compressedContent = _CompressForContainer(compressionMethod, uncompressedContent);
        return _AddLogContainerRaw(compressionMethod, (uint)uncompressedContent.Length, compressedContent);
    }

    /// <summary>
    /// Wraps all objects from <paramref name="inner"/> into a LogContainer LOBJ but writes
    /// <paramref name="wrongUncompressedSize"/> into the container header instead of the
    /// actual decompressed byte count. For negative tests verifying size-mismatch detection.
    /// </summary>
    internal BlfTestGenerator AddLogContainerWithWrongSize(
        ushort compressionMethod, BlfTestGenerator inner, uint wrongUncompressedSize)
    {
        byte[] uncompressedContent = inner._BuildInnerObjectBytes();
        byte[] compressedContent = _CompressForContainer(compressionMethod, uncompressedContent);
        return _AddLogContainerRaw(compressionMethod, wrongUncompressedSize, compressedContent);
    }

    /// <summary>
    /// Adds a LogContainer LOBJ whose compressed payload is replaced by the arbitrary
    /// <paramref name="corruptPayload"/> bytes, while the container header claims
    /// <paramref name="claimedUncompressedSize"/> bytes. For negative tests verifying
    /// corrupt decompression handling.
    /// </summary>
    internal BlfTestGenerator AddCorruptLogContainer(
        ushort compressionMethod, uint claimedUncompressedSize, byte[] corruptPayload) =>
        _AddLogContainerRaw(compressionMethod, claimedUncompressedSize, corruptPayload);

    /// <summary>
    /// Builds the raw LOBJ bytes for all queued objects <b>without</b> the file header.
    /// Produces the uncompressed payload of a LogContainer: a concatenation of
    /// block header (16B) + V1 log header (16B) + payload + 4-byte alignment padding
    /// for each object.
    /// </summary>
    private byte[] _BuildInnerObjectBytes()
    {
        int totalSize = 0;
        foreach (PendingObject obj in _Objects)
        {
            int objSize = _ObjectHeaderOverhead + obj.Payload.Length;
            int rem = objSize % 4;
            totalSize += rem != 0 ? objSize + (4 - rem) : objSize;
        }

        byte[] result = new byte[totalSize];
        Span<byte> span = result;
        int offset = 0;
        foreach (PendingObject obj in _Objects)
        {
            offset += _WriteObject(span[offset..], obj);
        }

        return result;
    }

    /// <summary>
    /// Compresses <paramref name="data"/> with the given BLF compression method:
    /// 0 = None (return as-is), 1 = LZ4 raw block, 2 = Zlib deflate.
    /// Throws <see cref="InvalidOperationException"/> for unsupported methods or if LZ4
    /// cannot achieve compression (compressed output would exceed input size — use
    /// more repetitive test content or <c>compressionMethod = 0</c>).
    /// </summary>
    private static byte[] _CompressForContainer(ushort compressionMethod, byte[] data)
    {
        if (compressionMethod == 0)
        {
            return data;
        }

        if (compressionMethod == 1)
        {
            // LZ4 raw block format. Returns -1 when compressed size >= input size.
            int maxSize = Lz4Codec.MaxCompressedSize(data.Length);
            byte[] compressed = new byte[maxSize];
            int written = Lz4Codec.Compress(data.AsSpan(), compressed.AsSpan());
            if (written < 0)
            {
                throw new InvalidOperationException(
                    "LZ4 cannot compress the inner container data (compressed output would exceed input). " +
                    "Use more repetitive test content or compressionMethod = 0 (None).");
            }

            return compressed[..written];
        }

        if (compressionMethod == 2)
        {
            using MemoryStream ms = new();
            using (ZLibStream zlib = new(ms, CompressionMode.Compress, leaveOpen: true))
            {
                zlib.Write(data);
            }

            return ms.ToArray();
        }

        throw new InvalidOperationException(
            $"Unsupported compression method {compressionMethod} in test generator.");
    }

    /// <summary>
    /// Writes the LogContainer payload (container header + compressed bytes) and
    /// adds it as an LOBJ with type <see cref="_ObjTypeLogContainer"/>.
    ///
    /// Container header layout (16 bytes):
    ///   [0..2)  compressionMethod (u16 LE)
    ///   [2..8)  reserved1A + reserved1B (zero)
    ///   [8..12) uncompressedSize (u32 LE)
    ///   [12..16) reserved2 (zero)
    ///
    /// Uses unpadded <c>object_length</c> (32 + compressed bytes) plus 0–3 alignment zeros
    /// after the object so the next LOBJ is 4-aligned. Compressed payload slices exclude
    /// those zeros.
    /// </summary>
    private BlfTestGenerator _AddLogContainerRaw(
        ushort compressionMethod, uint uncompressedSize, byte[] compressedContent)
    {
        byte[] payload = new byte[_ContainerHeaderSize + compressedContent.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), compressionMethod);
        // [2..8] reserved — zero-initialised
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), uncompressedSize);
        // [12..16] reserved — zero-initialised
        compressedContent.CopyTo(payload.AsSpan(_ContainerHeaderSize));
        _Objects.Add(new PendingObject(_ObjTypeLogContainer, 0, payload));
        return this;
    }

    /// <summary>
    /// Adds a Type 71 Ethernet frame (decomposed format).
    /// Builds the BLF-internal decomposed representation from a raw Ethernet frame.
    /// </summary>
    /// <param name="channel">BLF channel number (1-based).</param>
    /// <param name="ethernetFrame">Complete raw Ethernet frame (dst+src+ethtype+payload).</param>
    /// <param name="offsetNanos">Timestamp offset from start (nanoseconds).</param>
    internal BlfTestGenerator AddEthernetFrame(ushort channel, ReadOnlySpan<byte> ethernetFrame, long offsetNanos)
    {
        // Type 71 decomposed layout:
        // [0..6]  source MAC (src from Ethernet frame)
        // [6..8]  channel (u16 LE)
        // [8..14] destination MAC (dst from Ethernet frame)
        // [14..16] direction (u16 LE) = 0x0000 (RX)
        // [16..18] ethtype (u16 LE)
        // [18..20] TPID (u16 LE) — 0 if no VLAN
        // [20..22] TCI (u16 LE) — 0 if no VLAN
        // [22..24] payload_length (u16 LE) — length of payload after ethtype
        // [24..]  payload (data after ethtype in the Ethernet frame)

        if (ethernetFrame.Length < 14)
        {
            throw new ArgumentException("Ethernet frame too short (min 14 bytes)", nameof(ethernetFrame));
        }

        ReadOnlySpan<byte> dstMac = ethernetFrame[..6];
        ReadOnlySpan<byte> srcMac = ethernetFrame[6..12];
        ReadOnlySpan<byte> ethTypeBytes = ethernetFrame[12..14];

        // Check for VLAN tag (0x8100)
        ushort tpid = 0;
        ushort tci = 0;
        ReadOnlySpan<byte> innerEthType;
        ReadOnlySpan<byte> ethPayload;

        if (ethernetFrame.Length >= 18
            && ethernetFrame[12] == 0x81 && ethernetFrame[13] == 0x00)
        {
            // VLAN-tagged: TPID(2)+TCI(2)+inner ethtype(2)+payload
            tpid = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame[12..]);
            tci = BinaryPrimitives.ReadUInt16BigEndian(ethernetFrame[14..]);
            innerEthType = ethernetFrame[16..18];
            ethPayload = ethernetFrame.Length > 18 ? ethernetFrame[18..] : [];
        }
        else
        {
            innerEthType = ethTypeBytes;
            ethPayload = ethernetFrame.Length > 14 ? ethernetFrame[14..] : [];
        }

        // Vector blf_ethernetframeheader_t is 32 bytes (24 named fields + 8-byte uint64 res tail).
        // EtherType/TPID/TCI are little-endian numeric fields in the BLF struct.
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(innerEthType);
        byte[] payload = new byte[32 + ethPayload.Length];
        srcMac.CopyTo(payload);                                                     // [0..6]
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), channel);       // [6..8]
        dstMac.CopyTo(payload.AsSpan(8));                                           // [8..14]
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 0x0000);       // [14..16] direction=RX
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), etherType);    // [16..18] ethtype LE
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), tpid);         // [18..20] TPID LE
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), tci);          // [20..22] TCI LE
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22),
            (ushort)ethPayload.Length);                                              // [22..24] payload_len
        // [24..32] uint64 res — left zero by new byte[]
        ethPayload.CopyTo(payload.AsSpan(32));                                      // [32..] payload

        return AddRawObject(_ObjTypeEthernetFrame, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a CAN Classic message (Type 1) from a SocketCAN frame using the Wireshark
    /// <c>blf_canmessage_t</c> layout (flags at offset 2, dlc at 3, EFF in ID bit 31).
    /// </summary>
    /// <param name="channel">BLF channel number (1-based).</param>
    /// <param name="socketCanFrame">SocketCAN frame: id(4BE)+dlc(1)+flags(1)+reserved(2)+data.</param>
    /// <param name="offsetNanos">Timestamp offset from start (nanoseconds).</param>
    internal BlfTestGenerator AddCanFrame(ushort channel, ReadOnlySpan<byte> socketCanFrame, long offsetNanos)
    {
        if (socketCanFrame.Length < 8)
        {
            throw new ArgumentException("SocketCAN frame too short", nameof(socketCanFrame));
        }

        uint canIdBe = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        byte dlc = socketCanFrame[4];
        uint rawId = canIdBe & 0x1FFF_FFFF;
        if ((canIdBe & 0x8000_0000) != 0)
        {
            rawId |= 0x8000_0000;
        }

        byte blfFlags = 0;
        if ((canIdBe & 0x4000_0000) != 0)
        {
            blfFlags |= 0x80; // BLF_CANMESSAGE_FLAG_RTR
        }

        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, channel);
        payload[2] = blfFlags;
        payload[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rawId);

        int dataLen = Math.Min((int)dlc, 8);
        if (socketCanFrame.Length > 8)
        {
            socketCanFrame.Slice(8, Math.Min(dataLen, socketCanFrame.Length - 8))
                .CopyTo(payload.AsSpan(8));
        }

        return AddRawObject(_ObjTypeCanMessage, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a CAN XL channel frame (Type 139) from a SocketCAN XL frame, matching
    /// Wireshark <c>blf_dump_socketcanxl</c> (104-byte header, payload at offset 104).
    /// </summary>
    internal BlfTestGenerator AddCanXlChannelFrame(byte channel, ReadOnlySpan<byte> socketCanXlFrame, long offsetNanos)
    {
        const int SocketCanXlHeaderSize = 12;
        if (socketCanXlFrame.Length < SocketCanXlHeaderSize)
        {
            throw new ArgumentException("SocketCAN XL frame too short", nameof(socketCanXlFrame));
        }

        ushort dataLength = BinaryPrimitives.ReadUInt16LittleEndian(socketCanXlFrame.Slice(6));
        int available = socketCanXlFrame.Length - SocketCanXlHeaderSize;
        int actualDataLen = Math.Min((int)dataLength, available);
        ushort dlc = actualDataLen > 0
            ? (ushort)(actualDataLen - 1)
            : (ushort)0;

        byte socketFlags = socketCanXlFrame[4];
        uint flags = 0x400000; // XLF
        if ((socketFlags & 0x01) != 0)
        {
            flags |= 0x1000000; // SEC
        }

        if ((socketFlags & 0x02) != 0)
        {
            flags |= 0x800000; // RRS
        }

        byte[] payload = new byte[104 + actualDataLen];
        payload[0] = channel;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12),
            BinaryPrimitives.ReadUInt16BigEndian(socketCanXlFrame.Slice(2)) & 0x7FFu);
        payload[16] = socketCanXlFrame[5];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), dlc);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), (ushort)actualDataLen);
        payload[26] = socketCanXlFrame[1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28),
            BinaryPrimitives.ReadUInt32LittleEndian(socketCanXlFrame.Slice(8)));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), flags);

        if (actualDataLen > 0)
        {
            socketCanXlFrame.Slice(SocketCanXlHeaderSize, actualDataLen).CopyTo(payload.AsSpan(104));
        }

        return AddRawObject(_ObjTypeCanXlChannelFrame, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a CAN FD message (Type 100) from a SocketCAN FD frame using
    /// Wireshark <c>blf_canfdmessage_t</c> (20-byte header, data at offset 20).
    /// </summary>
    internal BlfTestGenerator AddCanFdFrame(ushort channel, ReadOnlySpan<byte> socketCanFrame, long offsetNanos)
    {
        // Type 100 layout (blf_canfdmessage_t):
        // [0..2)  channel (u16 LE)
        // [2]     flags (RTR 0x80)
        // [3]     dlc (4-bit code)
        // [4..8)  id (u32 LE, bit 31 = EFF)
        // [8..12) frameLength_in_ns
        // [12]    arbitration_bit_count
        // [13]    canfdflags (EDL 0x01, BRS 0x02, ESI 0x04)
        // [14]    validDataBytes
        // [15..20) reserved
        // [20..)  data

        if (socketCanFrame.Length < 8)
        {
            throw new ArgumentException("SocketCAN FD frame too short", nameof(socketCanFrame));
        }

        uint canIdBe = BinaryPrimitives.ReadUInt32BigEndian(socketCanFrame);
        byte payloadByteCount = socketCanFrame[4];
        if (payloadByteCount > 64)
        {
            payloadByteCount = 64;
        }

        byte dlc = BlfConstants.GetCanFdDlcFromPayloadByteCount(payloadByteCount);
        byte scFdFlags = socketCanFrame[5];

        uint rawId = canIdBe & 0x1FFF_FFFF;
        if ((canIdBe & 0x8000_0000) != 0)
        {
            rawId |= 0x8000_0000;
        }

        byte blfFlags = 0;
        if ((canIdBe & 0x4000_0000) != 0)
        {
            blfFlags |= 0x80;
        }

        byte blfFdFlags = 0;
        if ((scFdFlags & 0x04) != 0)
        {
            blfFdFlags |= 0x01;
        }

        if ((scFdFlags & 0x01) != 0)
        {
            blfFdFlags |= 0x02;
        }

        if ((scFdFlags & 0x02) != 0)
        {
            blfFdFlags |= 0x04;
        }

        int dataLen = Math.Min((int)payloadByteCount, 64);
        dataLen = Math.Min(dataLen, Math.Max(0, socketCanFrame.Length - 8));
        byte[] payload = new byte[20 + dataLen];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, channel);
        payload[2] = blfFlags;
        payload[3] = dlc;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), rawId);
        payload[13] = blfFdFlags;
        payload[14] = (byte)dataLen;

        if (dataLen > 0)
        {
            socketCanFrame.Slice(8, dataLen).CopyTo(payload.AsSpan(20));
        }

        return AddRawObject(_ObjTypeCanFdMessage, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a FlexRay RcvMessage (Type 50).
    /// </summary>
    internal BlfTestGenerator AddFlexRayFrame(
        ushort channel, ushort frameId, byte cycle, ushort headerCrc,
        ReadOnlySpan<byte> data, long offsetNanos)
    {
        // Vector blf_flexrayrcvmessage_t (44-byte packed header):
        //   [0..2]   channel (u16 LE)
        //   [2..4]   version (u16 LE) = 0
        //   [4..6]   channel_mask (u16 LE) = 0x0001
        //   [6..8]   dir (u16 LE) = 0x0001 (RX)
        //   [8..12]  client_index (u32 LE) = 0
        //   [12..16] cluster_no (u32 LE) = 0
        //   [16..18] frame_id (u16 LE)
        //   [18..20] header_crc1 (u16 LE)
        //   [20..22] header_crc2 (u16 LE) = 0
        //   [22..24] payload_length (u16 LE)
        //   [24..26] payload_length_valid (u16 LE)
        //   [26..28] cycle (u16 LE; high byte reserved)
        //   [28..32] tag (u32 LE) = 0
        //   [32..36] data (u32 LE) = 0
        //   [36..40] frame_flags (u32 LE) = 0
        //   [40..44] app_parameter (u32 LE) = 0
        //   [44..]   FlexRay payload bytes
        const int FlexRayHeaderSize = 44;
        byte[] payload = new byte[FlexRayHeaderSize + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, channel);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x0001); // channel_mask
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 0x0001); // direction=RX
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), headerCrc);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(26), cycle);
        data.CopyTo(payload.AsSpan(FlexRayHeaderSize));

        return AddRawObject(_ObjTypeFlexRayRcvMessage, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a LIN Message (Type 11).
    /// </summary>
    internal BlfTestGenerator AddLinFrame(ushort channel, byte frameId, ReadOnlySpan<byte> data, long offsetNanos)
    {
        // Type 11 layout: [0..2] channel(u16 LE) | [2] id | [3] dlc | [4..12] data (8B zero-padded)
        byte dlc = (byte)Math.Min(data.Length, 8);
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, channel);
        payload[2] = (byte)(frameId & 0x3F);
        payload[3] = dlc;
        data[..dlc].CopyTo(payload.AsSpan(4));
        return AddRawObject(_ObjTypeLinMessage, offsetNanos, payload);
    }

    /// <summary>
    /// Adds a LIN Message2 (Type 57).
    /// </summary>
    internal BlfTestGenerator AddLinMessage2(
        ushort channel, byte frameId, ReadOnlySpan<byte> data, byte checksum, long offsetNanos)
    {
        // Vector blf_linmessage2_t (132 bytes packed) — matches Wireshark wiretap/blf.h.
        // Nested layout (only fields populated by LinParser.TryParseLinMessageV2 are set):
        //   blf_linbusevent (16): sof(8) eventBaudrate(4) channel(2 LE @12) res1(2)
        //   blf_linsynchfieldevent (+16=32): synchBreakLength(8) synchDelLength(8)
        //   blf_linmessagedescriptor (+8=40): supplierId(2) messageId(2)
        //                                     configuredNodeAddress(1) id(1 @37) dlc(1 @38) checksumModel(1)
        //   blf_lindatabytetimestampevent (+72=112): databyteTimestamps[9] (72)
        //   blf_linmessage2 (+20=132): data[8] (8 @112) crc(2 LE @120)
        //                              dir(1) simulated(1) isEtf(1) eftAssocIndex(1)
        //                              eftAssocEftId(1) fsmId(1) fsmState(1) res1[3](3)
        const int LinMessage2Size = 132;
        byte dlc = (byte)Math.Min(data.Length, 8);
        byte[] payload = new byte[LinMessage2Size];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), channel); // channel
        payload[37] = (byte)(frameId & 0x3F);                                  // id
        payload[38] = dlc;                                                     // dlc
        data[..dlc].CopyTo(payload.AsSpan(112));                               // data[8]
        payload[120] = checksum;                                               // crc low byte
        return AddRawObject(_ObjTypeLinMessage2, offsetNanos, payload);
    }

    /// <summary>
    /// Adds an AppText channel-name object (Type 65): 16-byte header
    /// (<c>source</c>, <c>reservedAppText1</c>, <c>textLength</c>, <c>reservedAppText2</c>)
    /// and text <c>db;{name}</c> so token index 1 is the interface name.
    /// </summary>
    internal BlfTestGenerator AddAppTextChannel(ushort channel, byte busType, string name, long offsetNanos)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes("db;" + name + "\0");
        byte[] payload = new byte[16 + textBytes.Length];

        uint reserved = ((uint)busType << 16) | ((uint)channel << 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)textBytes.Length);
        textBytes.CopyTo(payload.AsSpan(16));

        return AddRawObject(_ObjTypeAppText, offsetNanos, payload);
    }

    // ========================================================================
    // Build
    // ========================================================================

    /// <summary>
    /// Builds the complete BLF file as a byte array.
    /// </summary>
    internal byte[] Build()
    {
        // Calculate total size: file header + all objects
        int totalSize = _FileHeaderSize;
        foreach (PendingObject obj in _Objects)
        {
            totalSize += _ObjectHeaderOverhead + obj.Payload.Length;
            // Pad to 4-byte alignment
            int remainder = (_ObjectHeaderOverhead + obj.Payload.Length) % 4;
            if (remainder != 0)
            {
                totalSize += 4 - remainder;
            }
        }

        byte[] result = new byte[totalSize];
        Span<byte> span = result;

        // Write file header (144 bytes)
        _WriteFileHeader(span);

        // Write objects
        int offset = _FileHeaderSize;
        foreach (PendingObject obj in _Objects)
        {
            offset += _WriteObject(span[offset..], obj);
        }

        return result;
    }

    // ========================================================================
    // Internal helpers
    // ========================================================================

    /// <summary>
    /// Writes the 144-byte BLF file header per Vector <c>blf_fileheader_t</c>:
    /// <c>magic(4) header_length(4) api_version(4) application(1) compression_level(1)
    /// app_major(1) app_minor(1) len_compressed(8) len_uncompressed(8) obj_count(4)
    /// app_build(4) start_date(16) end_date(16) restore_point_offset(4) padding[]</c>.
    /// </summary>
    private void _WriteFileHeader(Span<byte> span)
    {
        // "LOGG" magic
        BinaryPrimitives.WriteUInt32LittleEndian(span, _FileMagic);

        // Header size
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], _FileHeaderSize);

        // API version (decimal-encoded — keep historical 0x0403 placeholder)
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x0403);

        // [12..16] application(1)+compression_level(1)+app_major(1)+app_minor(1) → leave as zero.
        // [16..24] len_compressed (u64) → leave 0; the reader does not require it for parsing.
        // [24..32] len_uncompressed (u64) → leave 0.
        // [32..36] obj_count (u32) → leave 0.
        // [36..40] application_build (u32) → leave 0.

        // Measurement start time at offset 40: BlfDate (16 bytes)
        _WriteBlfDateFromNanos(span[40..], _StartNanos);

        // Measurement end time at offset 56: BlfDate (16 bytes)
        long lastTs = _Objects.Count > 0
            ? _StartNanos + _Objects[^1].OffsetNanos + 1_000_000_000L
            : _StartNanos + 1_000_000_000L;
        _WriteBlfDateFromNanos(span[56..], lastTs);

        // [72..76] restore_point_offset → leave 0.
        // [76..144] padding → leave 0.
    }

    /// <summary>
    /// Writes a single BLF object (block header + V1 log header + payload).
    /// Returns total bytes written (including alignment padding).
    /// <c>object_length</c> is the unpadded total; 0–3 zeros follow for 4-byte alignment.
    /// </summary>
    private static int _WriteObject(Span<byte> span, PendingObject obj)
    {
        int payloadLen = obj.Payload.Length;
        int objectLength = _ObjectHeaderOverhead + payloadLen;

        // Align to 4 bytes
        int remainder = objectLength % 4;
        int totalSize = remainder != 0 ? objectLength + (4 - remainder) : objectLength;

        // Block header (16 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(span, _ObjectMagic);                          // "LOBJ"
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], _ObjectHeaderOverhead);            // headerSize = 32
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], _HeaderTypeV1);                    // headerType = 1 (V1)
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)objectLength);              // objectLength (unpadded)
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], obj.ObjectType);                 // objectType

        // V1 log object header (16 bytes) at offset 16, per Vector blf_logobjectheader_t:
        //   uint32 flags (4) | uint16 client_index (2) | uint16 object_version (2) | uint64 timestamp (8)
        // flags (u32 LE) — 0x02 = nanosecond resolution
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], _TimestampFlagsNs);
        // clientIndex (u16 LE) = 0 → [20..22] already zero from new byte[]
        // objectVersion (u16 LE) = 0 → [22..24] already zero
        // timestamp (u64 LE) — nanoseconds offset from file start
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)obj.OffsetNanos);

        // Payload at offset 32
        obj.Payload.AsSpan().CopyTo(span[_ObjectHeaderOverhead..]);

        return totalSize;
    }

    /// <summary>
    /// Converts nanoseconds since Unix epoch to a BlfDate (Windows SYSTEMTIME) and writes it.
    /// The fields are written as <b>UTC</b> civil time to match the production reader's
    /// new default of <see cref="TimeZoneInfo.Utc"/>; this keeps the round-trip stable
    /// regardless of which machine the tests run on.
    /// </summary>
    private static void _WriteBlfDateFromNanos(Span<byte> span, long nanos)
    {
        DateTimeOffset utcDto = DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000L);
        DateTime utc = utcDto.UtcDateTime;

        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)utc.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)utc.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)utc.DayOfWeek);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)utc.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)utc.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)utc.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)utc.Second);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)utc.Millisecond);
    }

    /// <summary>
    /// Represents a pending BLF object to be written.
    /// </summary>
    /// <param name="ObjectType">BLF object type constant.</param>
    /// <param name="OffsetNanos">Timestamp offset from file start (nanoseconds).</param>
    /// <param name="Payload">Raw object payload bytes.</param>
    private sealed record PendingObject(
        uint ObjectType, long OffsetNanos, byte[] Payload);
}
