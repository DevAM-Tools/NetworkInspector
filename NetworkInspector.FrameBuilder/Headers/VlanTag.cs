// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IEEE 802.1Q VLAN tag (4 bytes) written after the Ethernet header.
/// Layout: TCI(2 BE) + InnerEtherType(2 BE) where TCI = PCP(3 bits) + DEI(1 bit) + VID(12 bits).
/// The TPID (0x8100/0x88A8) is placed at the Ethernet EtherType position by
/// <see cref="VlanLayer.ProtocolType"/> and patched via <see cref="EthernetLayer.PatchNextProtocol"/>.
/// </summary>
[BinaryWritable]
internal readonly partial struct VlanTag
{
    /// <summary>Size of the VLAN tag in bytes.</summary>
    internal const int Size = 4;

    /// <summary>
    /// Tag Control Information: PCP(3) + DEI(1) + VID(12).
    /// Use <see cref="MakeTci"/> to construct this value.
    /// </summary>
    internal U16BE Tci
    {
        get; init;
    }

    /// <summary>
    /// Inner EtherType / next-protocol identifier.
    /// Written as 0 by <see cref="VlanLayer.WriteHeader"/>;
    /// patched to the real value by <see cref="VlanLayer.PatchNextProtocol"/>.
    /// </summary>
    internal U16BE InnerEtherType
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
}
