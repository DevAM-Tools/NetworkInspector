// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Ethernet II frame header (14 bytes).
/// Layout: DstMac(6) + SrcMac(6) + EtherType(2 BE).
/// </summary>
internal readonly struct EthernetHeader
{
    /// <summary>Size of the Ethernet header in bytes.</summary>
    internal const int Size = 14;

    /// <summary>Destination MAC address.</summary>
    internal MacAddress DstMac
    {
        get; init;
    }

    /// <summary>Source MAC address.</summary>
    internal MacAddress SrcMac
    {
        get; init;
    }

    /// <summary>
    /// EtherType field identifying the next protocol.
    /// Common values: 0x0800 (IPv4), 0x86DD (IPv6), 0x0806 (ARP), 0x8100 (VLAN).
    /// </summary>
    internal ushort EtherType
    {
        get; init;
    }

    /// <summary>Writes the 14 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
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
        _ = DstMac.ToBytes(d);                                      // 0..6 (the slice is exact, so the count is always 6)
        _ = SrcMac.ToBytes(d[6..]);                                 // 6..12
        BinaryPrimitives.WriteUInt16BigEndian(d[12..], EtherType);  // 12..14
        bytesWritten = Size;
        return true;
    }
}
