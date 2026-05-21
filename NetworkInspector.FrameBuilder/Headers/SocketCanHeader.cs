// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Linux SocketCAN classic frame (16 bytes), PCAP linktype 227 (LINKTYPE_CAN_SOCKETCAN).
/// Layout (big-endian on the wire):
/// CanId(4, top 3 bits = EFF/RTR/ERR flags) + Dlc(1) + Pad(1) + Res0(1) + Res1(1) + Data(8).
/// </summary>
[BinaryWritable]
internal readonly partial struct SocketCanHeader
{
    /// <summary>Total fixed frame size in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Size of the header-only portion (CanId + Dlc + Pad + Res0 + Res1), in bytes.
    /// Does not include the 8-byte data area.</summary>
    internal const int HeaderSize = 8;

    /// <summary>Maximum data length for classic CAN, in bytes.</summary>
    internal const int MaxDataLength = Size - HeaderSize;

    /// <summary>EFF flag (bit 31 of CanId): extended-frame-format (29-bit identifier).</summary>
    internal const uint EffFlag = 0x80000000u;

    /// <summary>RTR flag (bit 30 of CanId): remote-transmission-request frame.</summary>
    internal const uint RtrFlag = 0x40000000u;

    /// <summary>ERR flag (bit 29 of CanId): error message frame.</summary>
    internal const uint ErrFlag = 0x20000000u;

    /// <summary>CAN identifier with flags in the top 3 bits.</summary>
    internal U32BE CanId
    {
        get; init;
    }

    /// <summary>Data length code (0..8 for classic CAN).</summary>
    internal byte Dlc
    {
        get; init;
    }

    /// <summary>Reserved padding byte (must be zero).</summary>
    internal byte Pad
    {
        get; init;
    }

    /// <summary>Reserved byte 0 (must be zero).</summary>
    internal byte Res0
    {
        get; init;
    }

    /// <summary>Reserved byte 1 (must be zero).</summary>
    internal byte Res1
    {
        get; init;
    }

    /// <summary>Payload byte 0.</summary>
    internal byte Data0
    {
        get; init;
    }

    /// <summary>Payload byte 1.</summary>
    internal byte Data1
    {
        get; init;
    }

    /// <summary>Payload byte 2.</summary>
    internal byte Data2
    {
        get; init;
    }

    /// <summary>Payload byte 3.</summary>
    internal byte Data3
    {
        get; init;
    }

    /// <summary>Payload byte 4.</summary>
    internal byte Data4
    {
        get; init;
    }

    /// <summary>Payload byte 5.</summary>
    internal byte Data5
    {
        get; init;
    }

    /// <summary>Payload byte 6.</summary>
    internal byte Data6
    {
        get; init;
    }

    /// <summary>Payload byte 7.</summary>
    internal byte Data7
    {
        get; init;
    }
}
