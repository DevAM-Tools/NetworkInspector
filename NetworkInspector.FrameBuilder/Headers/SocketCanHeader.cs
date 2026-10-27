// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Linux SocketCAN classic frame (16 bytes), PCAP linktype 227 (LINKTYPE_CAN_SOCKETCAN).
/// Layout (big-endian on the wire):
/// CanId(4, top 3 bits = EFF/RTR/ERR flags) + Dlc(1) + Pad(1) + Res0(1) + Res1(1) + Data(8).
/// </summary>
internal readonly struct SocketCanHeader
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
    internal uint CanId
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

    /// <summary>Writes the 16 frame bytes (id, DLC, padding, reserved, data); <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset stores on an exact-size slice, so the JIT drops the
    /// per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        // One length check up front; a short destination is an expected failure, not an exception.
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        // Exact-size slice: the fixed-offset stores below need no further bounds checks after JIT.
        Span<byte> d = destination[..Size];
        BinaryPrimitives.WriteUInt32BigEndian(d, CanId);  // 0..4
        d[4] = Dlc;                                       // 4
        d[5] = Pad;                                       // 5
        d[6] = Res0;                                      // 6
        d[7] = Res1;                                      // 7
        d[8] = Data0;                                     // 8
        d[9] = Data1;                                     // 9
        d[10] = Data2;                                    // 10
        d[11] = Data3;                                    // 11
        d[12] = Data4;                                    // 12
        d[13] = Data5;                                    // 13
        d[14] = Data6;                                    // 14
        d[15] = Data7;                                    // 15
        bytesWritten = Size;
        return true;
    }
}
