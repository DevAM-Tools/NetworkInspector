// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Bidirectional TCP connection key, normalized so the numerically lower
/// (address, port) pair is always first. This ensures the same connection
/// maps to the same key regardless of packet direction.
/// <para>
/// IPv4 addresses are stored as IPv4-mapped IPv6 (::ffff:a.b.c.d)
/// so all addresses use a uniform 128-bit representation.
/// </para>
/// <para>
/// The hash is <see cref="HashCode"/>, which is seeded randomly per process. The keys come from captured, possibly
/// crafted packets: an unkeyed mix would let colliding connections be computed offline and turn every dictionary
/// insert into a long probe (denial of service). Every field is added as 32-bit words, so no folding step creates
/// collisions of its own. Hash values are not persisted or shown.
/// </para>
/// </summary>
internal readonly struct TcpConnectionKey : IEquatable<TcpConnectionKey>
{
    // Normalized: lower (addr, port) pair is always stored first
    private readonly UInt128 _Addr1;
    private readonly UInt128 _Addr2;
    private readonly ushort _Port1;
    private readonly ushort _Port2;
    private readonly ushort _LastVlanId;
    private readonly int _FrameIdentity;

    /// <summary>Creates a normalized connection key from two endpoints and optional stream discriminators.</summary>
    /// <param name="srcAddr">Source IP address (IPv4 or IPv6).</param>
    /// <param name="dstAddr">Destination IP address (IPv4 or IPv6).</param>
    /// <param name="srcPort">Source TCP port.</param>
    /// <param name="dstPort">Destination TCP port.</param>
    /// <param name="lastVlanId">Innermost VLAN id, or 0 when the stream key does not include VLAN.</param>
    /// <param name="frameIdentity">Capture-interface identity, or 0 when the stream key does not include it.</param>
    internal TcpConnectionKey(
        UInt128 srcAddr, UInt128 dstAddr, ushort srcPort, ushort dstPort,
        ushort lastVlanId = 0, int frameIdentity = 0)
    {
        _LastVlanId = lastVlanId;
        _FrameIdentity = frameIdentity;

        // Normalize: lower (addr, port) pair first for consistent hashing.
        // VLAN and frame identity are not swapped; they are properties of the packet's path.
        if (srcAddr < dstAddr || (srcAddr == dstAddr && srcPort <= dstPort))
        {
            _Addr1 = srcAddr;
            _Port1 = srcPort;
            _Addr2 = dstAddr;
            _Port2 = dstPort;
        }
        else
        {
            _Addr1 = dstAddr;
            _Port1 = dstPort;
            _Addr2 = srcAddr;
            _Port2 = srcPort;
        }
    }

    /// <summary>
    /// Determines the direction of a packet relative to the normalized key.
    /// Returns <see langword="true"/> when the source matches the first (lower) endpoint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsForward(UInt128 srcAddr, ushort srcPort) =>
        _Addr1 == srcAddr && _Port1 == srcPort;

    /// <summary>Creates a connection key from raw IPv4 addresses (as 32-bit values).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TcpConnectionKey FromIPv4(
        uint srcIp, uint dstIp, ushort srcPort, ushort dstPort, ushort lastVlanId = 0, int frameIdentity = 0)
    {
        // Map IPv4 to IPv4-mapped IPv6: ::ffff:a.b.c.d
        UInt128 srcAddr = _MapIPv4ToIPv6(srcIp);
        UInt128 dstAddr = _MapIPv4ToIPv6(dstIp);
        return new TcpConnectionKey(srcAddr, dstAddr, srcPort, dstPort, lastVlanId, frameIdentity);
    }

    /// <summary>Maps an IPv4 address (32 bits) to IPv4-mapped IPv6 (::ffff:x.x.x.x) as UInt128.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 _MapIPv4ToIPv6(uint ipv4) =>
        // IPv4-mapped IPv6: upper 80 bits are 0, next 16 bits are 0xFFFF, lower 32 bits are the IPv4 address
        new UInt128(0, 0x0000_FFFF_0000_0000UL | ipv4);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(TcpConnectionKey other) =>
        _Addr1 == other._Addr1 && _Addr2 == other._Addr2 &&
        _Port1 == other._Port1 && _Port2 == other._Port2 &&
        _LastVlanId == other._LastVlanId && _FrameIdentity == other._FrameIdentity;

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is TcpConnectionKey other && Equals(other);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // Process-seeded hash over 32-bit words: injective input, unpredictable buckets for crafted keys.
        HashCode hash = default;
        ConnectionKeyHash.AddAddress(ref hash, _Addr1);
        ConnectionKeyHash.AddAddress(ref hash, _Addr2);
        hash.Add(((uint)_Port1 << 16) | _Port2);
        hash.Add((uint)_LastVlanId);
        hash.Add((uint)_FrameIdentity);
        return hash.ToHashCode();
    }
}
