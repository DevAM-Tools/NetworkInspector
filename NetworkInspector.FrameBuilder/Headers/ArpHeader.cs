// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// ARP header for Ethernet/IPv4 (28 bytes).
/// Layout per RFC 826: HardwareType(2), ProtocolType(2), HwAddrLen(1), ProtoAddrLen(1),
/// Opcode(2), SenderMac(6), SenderIp(4), TargetMac(6), TargetIp(4).
/// </summary>
internal readonly struct ArpHeader
{
    /// <summary>Size of the ARP header for Ethernet/IPv4.</summary>
    internal const int Size = 28;

    /// <summary>Hardware type (1 = Ethernet).</summary>
    internal ushort HardwareType
    {
        get; init;
    }

    /// <summary>Protocol type (0x0800 = IPv4).</summary>
    internal ushort ProtocolType
    {
        get; init;
    }

    /// <summary>Hardware address length (6 for Ethernet).</summary>
    internal byte HardwareAddrLen
    {
        get; init;
    }

    /// <summary>Protocol address length (4 for IPv4).</summary>
    internal byte ProtocolAddrLen
    {
        get; init;
    }

    /// <summary>ARP operation (1 = Request, 2 = Reply).</summary>
    internal ushort Opcode
    {
        get; init;
    }

    /// <summary>Sender hardware (MAC) address.</summary>
    internal MacAddress SenderMac
    {
        get; init;
    }

    /// <summary>Sender protocol (IPv4) address.</summary>
    internal IPv4Address SenderIp
    {
        get; init;
    }

    /// <summary>Target hardware (MAC) address.</summary>
    internal MacAddress TargetMac
    {
        get; init;
    }

    /// <summary>Target protocol (IPv4) address.</summary>
    internal IPv4Address TargetIp
    {
        get; init;
    }

    /// <summary>
    /// Creates an ARP header for Ethernet/IPv4 with standard hardware/protocol settings.
    /// </summary>
    /// <param name="opcode">ARP operation code (1 = Request, 2 = Reply).</param>
    /// <param name="senderMac">Sender MAC address.</param>
    /// <param name="senderIp">Sender IPv4 address.</param>
    /// <param name="targetMac">Target MAC address.</param>
    /// <param name="targetIp">Target IPv4 address.</param>
    internal static ArpHeader Create(
        ushort opcode,
        MacAddress senderMac,
        IPv4Address senderIp,
        MacAddress targetMac,
        IPv4Address targetIp)
    {
        return new ArpHeader
        {
            HardwareType = (ushort)1,       // Ethernet
            ProtocolType = (ushort)0x0800,  // IPv4
            HardwareAddrLen = 6,
            ProtocolAddrLen = 4,
            Opcode = opcode,
            SenderMac = senderMac,
            SenderIp = senderIp,
            TargetMac = targetMac,
            TargetIp = targetIp,
        };
    }

    /// <summary>Writes the 28 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
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
        BinaryPrimitives.WriteUInt16BigEndian(d, HardwareType);       // 0..2
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], ProtocolType);  // 2..4
        d[4] = HardwareAddrLen;                                       // 4
        d[5] = ProtocolAddrLen;                                       // 5
        BinaryPrimitives.WriteUInt16BigEndian(d[6..], Opcode);        // 6..8
        _ = SenderMac.ToBytes(d[8..]);                                // 8..14 (the slice is exact, so the count is always 6)
        _ = SenderIp.ToBytes(d[14..]);                                // 14..18
        _ = TargetMac.ToBytes(d[18..]);                               // 18..24
        _ = TargetIp.ToBytes(d[24..]);                                // 24..28
        bytesWritten = Size;
        return true;
    }
}
