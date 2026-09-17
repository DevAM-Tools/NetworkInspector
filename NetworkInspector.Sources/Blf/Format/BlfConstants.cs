// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Constants for the BLF (Binary Logging Format) file format.
/// Includes magic numbers, object type IDs, compression methods, and bus types.
/// </summary>
internal static class BlfConstants
{
    #region Magic numbers (as little-endian u32)

    /// <summary>"LOGG" as LE u32 — file header magic.</summary>
    internal const uint FileMagic = 0x47474F4C; // 'L','O','G','G' → LE

    /// <summary>"LOBJ" as LE u32 — block/object header magic.</summary>
    internal const uint ObjectMagic = 0x4A424F4C; // 'L','O','B','J' → LE

    /// <summary>"LOGG" as byte array for comparison.</summary>
    internal static ReadOnlySpan<byte> FileMagicBytes => "LOGG"u8;

    /// <summary>"LOBJ" as byte array for comparison.</summary>
    internal static ReadOnlySpan<byte> ObjectMagicBytes => "LOBJ"u8;

    #endregion

    #region Header sizes

    /// <summary>Minimum file header size in bytes.</summary>
    internal const int FileHeaderMinSize = 144;

    /// <summary>Block/object header size in bytes.</summary>
    internal const int BlockHeaderSize = 16;

    /// <summary>
    /// Number of 0–3 alignment zeros that follow an unpadded LOBJ so the next object is 4-aligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AlignmentPaddingByteCount(int unpaddedSize) => (4 - (unpaddedSize & 3)) & 3;

    /// <summary>Log object header Type 1 size in bytes.</summary>
    internal const int LogObjectHeaderType1Size = 16;

    /// <summary>Log object header Type 2 size in bytes.</summary>
    internal const int LogObjectHeaderType2Size = 24;

    /// <summary>Log object header Type 3 size in bytes.</summary>
    internal const int LogObjectHeaderType3Size = 16;

    /// <summary>Container header size in bytes.</summary>
    internal const int ContainerHeaderSize = 16;

    /// <summary>
    /// Maximum LOBJ size fetched in one backend <c>GetSpan</c> or stream buffer (256 MiB).
    /// Untrusted <c>object_length</c> above this is treated as corrupt: skip and scan for the
    /// next <c>LOBJ</c> instead of mapping a multi-hundred-megabyte window.
    /// Matches the stream-source buffer cap so file and stream backends reject the same inputs.
    /// </summary>
    internal const int MaxBlockReadSize = 256 * 1024 * 1024;

    #endregion

    #region Compression methods

    /// <summary>No compression.</summary>
    internal const ushort CompressionNone = 0;

    /// <summary>
    /// LZ4 block compression (raw LZ4, not the LZ4 frame format).
    /// Used by recent Vector CANoe versions with the new high-throughput compressor.
    /// </summary>
    internal const ushort CompressionLz4 = 1;

    /// <summary>zlib compression.</summary>
    internal const ushort CompressionZlib = 2;

    #endregion

    #region Timestamp resolution

    /// <summary>10 µs resolution (multiply by 10,000 to get nanoseconds).</summary>
    internal const byte TimestampResolution10Us = 1;

    /// <summary>1 ns resolution (direct nanoseconds).</summary>
    internal const byte TimestampResolution1Ns = 2;

    /// <summary>Multiplier for 10 µs resolution → nanoseconds.</summary>
    internal const long TimestampMultiplier10Us = 10_000;

    #endregion

    #region Object types

    /// <summary>Classic CAN message.</summary>
    internal const uint ObjTypeCanMessage = 1;

    /// <summary>CAN error frame.</summary>
    internal const uint ObjTypeCanError = 2;

    /// <summary>CAN overload frame.</summary>
    internal const uint ObjTypeCanOverload = 3;

    /// <summary>Log container (wrapper for compressed objects).</summary>
    internal const uint ObjTypeLogContainer = 10;

    /// <summary>LIN message v1.</summary>
    internal const uint ObjTypeLinMessage = 11;

    /// <summary>LIN CRC error v1.</summary>
    internal const uint ObjTypeLinCrcError = 12;

    /// <summary>LIN receive error v1.</summary>
    internal const uint ObjTypeLinRcvError = 14;

    /// <summary>LIN send error v1.</summary>
    internal const uint ObjTypeLinSndError = 15;

    /// <summary>LIN sleep v1.</summary>
    internal const uint ObjTypeLinSleep = 20;

    /// <summary>LIN wakeup v1.</summary>
    internal const uint ObjTypeLinWakeup = 21;

    /// <summary>FlexRay data.</summary>
    internal const uint ObjTypeFlexRayData = 29;

    /// <summary>FlexRay message.</summary>
    internal const uint ObjTypeFlexRayMessage = 41;

    /// <summary>FlexRay receive message.</summary>
    internal const uint ObjTypeFlexRayRcvMessage = 50;

    /// <summary>LIN message v2.</summary>
    internal const uint ObjTypeLinMessage2 = 57;

    /// <summary>LIN send error v2.</summary>
    internal const uint ObjTypeLinSndError2 = 58;

    /// <summary>LIN CRC error v2.</summary>
    internal const uint ObjTypeLinCrcError2 = 60;

    /// <summary>LIN receive error v2.</summary>
    internal const uint ObjTypeLinRcvError2 = 61;

    /// <summary>LIN wakeup v2.</summary>
    internal const uint ObjTypeLinWakeup2 = 62;

    /// <summary>Application text (metadata / channel names).</summary>
    internal const uint ObjTypeAppText = 65;

    /// <summary>FlexRay receive message EX.</summary>
    internal const uint ObjTypeFlexRayRcvMessageEx = 66;

    /// <summary>Ethernet frame (decomposed, Type 71 — needs reassembly).</summary>
    internal const uint ObjTypeEthernetFrame = 71;

    /// <summary>CAN error ext.</summary>
    internal const uint ObjTypeCanErrorExt = 73;

    /// <summary>Classic CAN message v2.</summary>
    internal const uint ObjTypeCanMessage2 = 86;

    /// <summary>CAN FD message.</summary>
    internal const uint ObjTypeCanFdMessage = 100;

    /// <summary>CAN FD message 64-byte.</summary>
    internal const uint ObjTypeCanFdMessage64 = 101;

    /// <summary>Ethernet RX error.</summary>
    internal const uint ObjTypeEthernetRxError = 102;

    /// <summary>CAN FD error 64-byte.</summary>
    internal const uint ObjTypeCanFdError64 = 104;

    /// <summary>Ethernet frame EX (raw frame).</summary>
    internal const uint ObjTypeEthernetFrameEx = 120;

    /// <summary>CAN XL channel frame (object type 139).</summary>
    internal const uint ObjTypeCanXlChannelFrame = 139;

    /// <summary>CAN XL channel error frame (object type 140).</summary>
    internal const uint ObjTypeCanXlChannelErrorFrame = 140;

    /// <summary>
    /// Packed size of the Type 139 CAN XL channel-frame header. Payload bytes follow at offset 104.
    /// Sequential little-endian field sizes sum to 104; do not overlay a C# struct.
    /// <code>
    ///   [0]      channel (u8)
    ///   [1]      tx count (u8)
    ///   [2]      direction (u8)
    ///   [3]      reserved
    ///   [4..8)   frame length on bus (u32 LE, nanoseconds)
    ///   [8..10)  bit count (u16 LE)
    ///   [10..12) reserved
    ///   [12..16) frame identifier (u32 LE; 11-bit priority in the low bits)
    ///   [16]     SDU type
    ///   [17]     reserved
    ///   [18..20) DLC (u16 LE)
    ///   [20..22) data length (u16 LE)
    ///   [22..24) stuff-bit count (u16 LE)
    ///   [24..26) preface CRC (u16 LE)
    ///   [26]     VCID
    ///   [27]     reserved
    ///   [28..32) acceptance field (u32 LE)
    ///   [32]     stuff count
    ///   [33..36) reserved
    ///   [36..40) CRC (u32 LE)
    ///   [40..44) BRS time offset (u32 LE, nanoseconds)
    ///   [44..48) CRC-delimiter time offset (u32 LE, nanoseconds)
    ///   [48..52) flags (u32 LE; XLF = 0x400000, RRS = 0x800000, SEC = 0x1000000)
    ///   [52..56) reserved
    ///   [56..104) six u64 LE timing / hardware-setting words (unused by reconstruction)
    ///   [104..)  payload
    /// </code>
    /// </summary>
    internal const int CanXlChannelFrameHeaderSize = 104;

    #endregion

    #region Bus types (for AppText channel name resolution)

    /// <summary>CAN bus.</summary>
    internal const byte BusTypeCan = 1;

    /// <summary>LIN bus.</summary>
    internal const byte BusTypeLin = 5;

    /// <summary>FlexRay bus.</summary>
    internal const byte BusTypeFlexRay = 7;

    /// <summary>Ethernet bus.</summary>
    internal const byte BusTypeEthernet = 11;

    #endregion

    #region CAN flags and tables

    /// <summary>CAN NERR flag: 0 = error, 1 = valid frame.</summary>
    internal const byte CanFlagNerr = 0x20;

    /// <summary>
    /// Remote Transmission Request in the Type 1 / Type 86 / Type 100 flags byte at offset 2 (bit 0x80).
    /// </summary>
    internal const byte BlfCanMessageFlagRtr = 0x80;

    /// <summary>
    /// Extended-frame flag in the Type 100 CAN FD 32-bit <c>blfFlags</c> field.
    /// Classic Type 1/86 encode EFF in CAN ID bit 31, not in the flags byte.
    /// Must not be confused with <see cref="BlfCanFdEsi"/>, which is the ESI bit in the separate 8-bit FD-flags byte.
    /// </summary>
    internal const uint BlfCanMessageFlagEff = 0x04;

    /// <summary>Lookup: SocketCAN FD payload byte length (index 0..64) → 4‑bit DLC code for BLF CAN FD payloads.</summary>
    internal static ReadOnlySpan<byte> CanFdPayloadLengthToDlc => _CanFdPayloadLengthToDlc;

    private static readonly byte[] _CanFdPayloadLengthToDlc = _CreateCanFdPayloadLengthToDlcTable();

    /// <summary>Returns the DLC code for a SocketCAN FD <paramref name="payloadByteCount"/> (0–64).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte GetCanFdDlcFromPayloadByteCount(byte payloadByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadByteCount, 64);
        return _CanFdPayloadLengthToDlc[payloadByteCount];
    }

    private static byte[] _CreateCanFdPayloadLengthToDlcTable()
    {
        byte[] table = new byte[65];
        for (int len = 0; len <= 64; len++)
        {
            table[len] = _PayloadLengthToCanFdDlcImpl((byte)len);
        }

        return table;
    }

    /// <summary>
    /// Maps actual FD payload byte count (0–64) → DLC per CiA 1301 / Vector BLF (same logic as exporters).
    /// </summary>
    private static byte _PayloadLengthToCanFdDlcImpl(byte length)
    {
        if (length <= 8)
        {
            return length;
        }

        if (length <= 12)
        {
            return 9;
        }

        if (length <= 16)
        {
            return 10;
        }

        if (length <= 20)
        {
            return 11;
        }

        if (length <= 24)
        {
            return 12;
        }

        if (length <= 32)
        {
            return 13;
        }

        if (length <= 48)
        {
            return 14;
        }
        return 15;
    }

    /// <summary>SocketCAN Extended Frame Format flag (bit 31).</summary>
    internal const uint SocketCanEff = 0x80000000;

    /// <summary>SocketCAN Remote Transmission Request flag (bit 30).</summary>
    internal const uint SocketCanRtr = 0x40000000;

    /// <summary>SocketCAN Error Frame flag (bit 29).</summary>
    internal const uint SocketCanErr = 0x20000000;

    /// <summary>Classic CAN DLC to data length mapping (indices 0–15).</summary>
    internal static ReadOnlySpan<byte> CanDlcToLength =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 8, 8, 8, 8, 8, 8, 8];

    /// <summary>CAN FD DLC to data length mapping (indices 0–15).</summary>
    internal static ReadOnlySpan<byte> CanFdDlcToLength =>
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    #endregion

    #region CAN FD flags (BLF ↔ SocketCAN mapping)

    /// <summary>BLF CAN FD: EDL (Extended Data Length).</summary>
    internal const byte BlfCanFdEdl = 0x01;

    /// <summary>BLF CAN FD: BRS (Bit Rate Switch).</summary>
    internal const byte BlfCanFdBrs = 0x02;

    /// <summary>BLF CAN FD: ESI (Error State Indicator).</summary>
    internal const byte BlfCanFdEsi = 0x04;

    /// <summary>SocketCAN FD: FDF flag.</summary>
    internal const byte SocketCanFdFdf = 0x04;

    /// <summary>SocketCAN FD: BRS flag.</summary>
    internal const byte SocketCanFdBrs = 0x01;

    /// <summary>SocketCAN FD: ESI flag.</summary>
    internal const byte SocketCanFdEsi = 0x02;

    #endregion

    #region CAN FD Message 64 flags (u32)

    /// <summary>CAN FD Type 101: remote frame in the u32 flags field at offset 12 (bit 0x000010).</summary>
    internal const uint CanFd64FlagRemoteFrame = 0x000010;

    /// <summary>CAN FD 64: EDL flag in u32 flags field.</summary>
    internal const uint CanFd64FlagEdl = 0x001000;

    /// <summary>CAN FD 64: BRS flag in u32 flags field.</summary>
    internal const uint CanFd64FlagBrs = 0x002000;

    /// <summary>CAN FD 64: ESI flag in u32 flags field.</summary>
    internal const uint CanFd64FlagEsi = 0x004000;

    #endregion

    #region CAN XL flags (BLF ↔ SocketCAN)

    /// <summary>BLF CAN XL: XLF (CAN XL frame) in the Type 139 flags word at offset 48.</summary>
    internal const uint BlfCanXlFlagXlf = 0x400000;

    /// <summary>BLF CAN XL: RRS (Remote Request Substitution).</summary>
    internal const uint BlfCanXlFlagRrs = 0x800000;

    /// <summary>BLF CAN XL: SEC (Simple Extended Content).</summary>
    internal const uint BlfCanXlFlagSec = 0x1000000;

    /// <summary>SocketCAN XL: XLF discriminator at header byte 4.</summary>
    internal const byte SocketCanXlXlf = 0x80;

    /// <summary>SocketCAN XL: SEC flag at header byte 4.</summary>
    internal const byte SocketCanXlSec = 0x01;

    /// <summary>SocketCAN XL: RRS flag at header byte 4.</summary>
    internal const byte SocketCanXlRrs = 0x02;

    #endregion

    #region LIN constants

    /// <summary>DLT_LIN error bit: no slave response (BLF LIN send-error objects, types 15/58).</summary>
    internal const byte LinErrorSnd = 0x01;

    /// <summary>DLT_LIN error bit: framing error (BLF LIN receive-error objects, types 14/61).</summary>
    internal const byte LinErrorRcv = 0x02;

    /// <summary>DLT_LIN error bit: checksum error (BLF LIN CRC-error objects, types 12/60).</summary>
    internal const byte LinErrorCrc = 0x08;

    #endregion

    #region AppText source masks

    /// <summary>
    /// AppText Type 65 <c>source</c> value (u32 LE at offset 0) for a channel-name record.
    /// </summary>
    internal const uint AppTextSourceChannel = 1;

    /// <summary>
    /// Shift of the 1-based channel number inside the AppText reserved u32 at offset 4
    /// (<c>(reserved &gt;&gt; 8) &amp; 0xFF</c>).
    /// </summary>
    internal const int AppTextReservedChannelShift = 8;

    /// <summary>
    /// Shift of the bus-type byte inside the AppText reserved u32 at offset 4
    /// (<c>(reserved &gt;&gt; 16) &amp; 0xFF</c>).
    /// </summary>
    internal const int AppTextReservedBusTypeShift = 16;

    /// <summary>Eight-bit mask applied after an AppText reserved-field shift.</summary>
    internal const uint AppTextReservedByteMask = 0xFF;

    #endregion

    #region Default configuration

    /// <summary>Property key for the BLF channel number in <see cref="FrameInterfaceInfo.Properties"/>.</summary>
    /// <remarks>Use <see cref="FrameInterfacePropertyKeys.BlfChannel"/> instead. Kept for reference only.</remarks>
    internal const string PropertyKeyChannel = FrameInterfacePropertyKeys.BlfChannel;

    /// <summary>Default container cache budget in bytes (32 MiB).</summary>
    internal const int DefaultCacheBudget = 32 * 1024 * 1024;

    /// <summary>Maximum container buffer size for writing (10 MiB).</summary>
    internal const int MaxContainerBufferSize = 10 * 1024 * 1024;

    /// <summary>
    /// Returns true if the object type produces frames (is not a container or metadata-only).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsFrameProducingType(uint objectType) => objectType switch
    {
        ObjTypeCanMessage or ObjTypeCanError or ObjTypeCanOverload
        or ObjTypeLinMessage or ObjTypeLinCrcError or ObjTypeLinRcvError
        or ObjTypeLinSndError or ObjTypeLinSleep or ObjTypeLinWakeup
        or ObjTypeFlexRayData or ObjTypeFlexRayMessage or ObjTypeFlexRayRcvMessage
        or ObjTypeLinMessage2 or ObjTypeLinSndError2 or ObjTypeLinCrcError2
        or ObjTypeLinRcvError2 or ObjTypeLinWakeup2
        or ObjTypeFlexRayRcvMessageEx
        or ObjTypeEthernetFrame or ObjTypeCanErrorExt or ObjTypeCanMessage2
        or ObjTypeCanFdMessage or ObjTypeCanFdMessage64 or ObjTypeEthernetRxError
        or ObjTypeCanFdError64 or ObjTypeEthernetFrameEx or ObjTypeCanXlChannelFrame => true,
        _ => false,
    };
    #endregion
}
