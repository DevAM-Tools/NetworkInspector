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

    /// <summary>Log object header Type 1 size in bytes.</summary>
    internal const int LogObjectHeaderType1Size = 16;

    /// <summary>Log object header Type 2 size in bytes.</summary>
    internal const int LogObjectHeaderType2Size = 24;

    /// <summary>Log object header Type 3 size in bytes.</summary>
    internal const int LogObjectHeaderType3Size = 16;

    /// <summary>Container header size in bytes.</summary>
    internal const int ContainerHeaderSize = 16;

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

    /// <summary>CAN RTR flag.</summary>
    internal const byte CanFlagRtr = 0x10;

    /// <summary>
    /// Extended-frame flag in BLF classic CAN (<see cref="ObjTypeCanMessage"/>) payload byte flags
    /// and in CAN FD (<see cref="ObjTypeCanFdMessage"/>) 32-bit <c>blfFlags</c> (same numeric value).
    /// Must not be confused with <see cref="BlfCanFdEsi"/>, which is the ESI bit in the separate 8-bit FD-flags byte.
    /// </summary>
    internal const uint BlfCanMessageFlagEff = 0x04;

    /// <summary>Lookup: SocketCAN FD payload byte length (index 0..64) → 4‑bit DLC code for BLF CAN FD payloads.</summary>
    internal static ReadOnlySpan<byte> CanFdPayloadLengthToDlc => _CanFdPayloadLengthToDlc;

    private static readonly byte[] _CanFdPayloadLengthToDlc = CreateCanFdPayloadLengthToDlcTable();

    /// <summary>Returns the DLC code for a SocketCAN FD <paramref name="payloadByteCount"/> (0–64).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte GetCanFdDlcFromPayloadByteCount(byte payloadByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadByteCount, 64);
        return _CanFdPayloadLengthToDlc[payloadByteCount];
    }

    private static byte[] CreateCanFdPayloadLengthToDlcTable()
    {
        byte[] table = new byte[65];
        for (int len = 0; len <= 64; len++)
        {
            table[len] = PayloadLengthToCanFdDlcImpl((byte)len);
        }

        return table;
    }

    /// <summary>
    /// Maps actual FD payload byte count (0–64) → DLC per CiA 1301 / Vector BLF (same logic as exporters).
    /// </summary>
    private static byte PayloadLengthToCanFdDlcImpl(byte length)
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

        return length <= 48 ? (byte)14 : (byte)15;
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

    /// <summary>CAN FD 64: EDL flag in u32 flags field.</summary>
    internal const uint CanFd64FlagEdl = 0x001000;

    /// <summary>CAN FD 64: BRS flag in u32 flags field.</summary>
    internal const uint CanFd64FlagBrs = 0x002000;

    /// <summary>CAN FD 64: ESI flag in u32 flags field.</summary>
    internal const uint CanFd64FlagEsi = 0x004000;

    #endregion

    #region LIN constants

    /// <summary>LIN error flag: receive error.</summary>
    internal const byte LinErrorRcv = 0x01;

    /// <summary>LIN error flag: send error.</summary>
    internal const byte LinErrorSnd = 0x02;

    /// <summary>LIN error flag: CRC error.</summary>
    internal const byte LinErrorCrc = 0x04;

    #endregion

    #region AppText source masks

    /// <summary>AppText source mask for channel name detection.</summary>
    internal const uint AppTextSourceChannelName = 0x00020000;

    /// <summary>AppText channel number mask.</summary>
    internal const uint AppTextChannelMask = 0xFF;

    /// <summary>AppText bus type shift (bits 8–15).</summary>
    internal const int AppTextBusTypeShift = 8;

    /// <summary>AppText bus type mask.</summary>
    internal const uint AppTextBusTypeMask = 0xFF;

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
        or ObjTypeCanFdError64 or ObjTypeEthernetFrameEx => true,
        _ => false,
    };
    #endregion
}
