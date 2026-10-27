// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IEEE 802.1Q VLAN tag (4 bytes) written after the Ethernet header.
/// Layout: TCI(2 BE) + InnerEtherType(2 BE) where TCI = PCP(3 bits) + DEI(1 bit) + VID(12 bits).
/// The TPID (0x8100/0x88A8) is placed at the Ethernet EtherType position by
/// <see cref="VlanLayer.ProtocolType"/> and patched via <see cref="EthernetLayer.PatchNextProtocol"/>.
/// </summary>
internal readonly struct VlanTag
{
    /// <summary>Size of the VLAN tag in bytes.</summary>
    internal const int Size = 4;

    /// <summary>
    /// Tag Control Information: PCP(3) + DEI(1) + VID(12).
    /// Use <see cref="MakeTci"/> to construct this value.
    /// </summary>
    internal ushort Tci
    {
        get; init;
    }

    /// <summary>
    /// Inner EtherType / next-protocol identifier.
    /// Written as 0 by <see cref="VlanLayer.WriteHeader"/>;
    /// patched to the real value by <see cref="VlanLayer.PatchNextProtocol"/>.
    /// </summary>
    internal ushort InnerEtherType
    {
        get; init;
    }

    /// <summary>
    /// Constructs the 16-bit TCI field from its components.
    /// </summary>
    /// <param name="vlanId">VLAN identifier (0–4095).</param>
    /// <param name="pcp">Priority Code Point (0–7).</param>
    /// <param name="dei">Drop Eligible Indicator (0–1).</param>
    /// <returns>The 16-bit TCI value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort MakeTci(ushort vlanId, byte pcp = 0, byte dei = 0)
        => (ushort)((pcp << 13) | (dei << 12) | (vlanId & 0x0FFF));

    /// <summary>Writes the 4 tag bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset big-endian stores on an exact-size slice, so the JIT
    /// drops the per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
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
        BinaryPrimitives.WriteUInt16BigEndian(d, Tci);              // 0..2
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], InnerEtherType); // 2..4
        bytesWritten = Size;
        return true;
    }
}
