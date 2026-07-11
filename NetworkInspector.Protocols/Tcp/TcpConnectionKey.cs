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
/// Uses a fast XOR-rotate hash instead of <see cref="HashCode.Combine{T1,T2,T3,T4}"/>
/// to reduce per-packet overhead. The hash only mixes the non-redundant parts of
/// the address fields (lower 64 bits carry all entropy for IPv4-mapped addresses).
/// </para>
/// </summary>
internal readonly struct TcpConnectionKey : IEquatable<TcpConnectionKey>
{
    // Normalized: lower (addr, port) pair is always stored first
    private readonly UInt128 _Addr1;
    private readonly UInt128 _Addr2;
    private readonly ushort _Port1;
    private readonly ushort _Port2;

    /// <summary>Creates a normalized connection key from two endpoints.</summary>
    /// <param name="srcAddr">Source IP address (IPv4 or IPv6).</param>
    /// <param name="dstAddr">Destination IP address (IPv4 or IPv6).</param>
    /// <param name="srcPort">Source TCP port.</param>
    /// <param name="dstPort">Destination TCP port.</param>
    internal TcpConnectionKey(UInt128 srcAddr, UInt128 dstAddr, ushort srcPort, ushort dstPort)
    {
        // Normalize: lower (addr, port) pair first for consistent hashing
        // Compare addresses first, then ports as tiebreaker
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
    internal static TcpConnectionKey FromIPv4(uint srcIp, uint dstIp, ushort srcPort, ushort dstPort)
    {
        // Map IPv4 to IPv4-mapped IPv6: ::ffff:a.b.c.d
        UInt128 srcAddr = _MapIPv4ToIPv6(srcIp);
        UInt128 dstAddr = _MapIPv4ToIPv6(dstIp);
        return new TcpConnectionKey(srcAddr, dstAddr, srcPort, dstPort);
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
        _Port1 == other._Port1 && _Port2 == other._Port2;

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is TcpConnectionKey other && Equals(other);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // Fast XOR-rotate mixing — avoids SipHash overhead of HashCode.Combine.
        // For IPv4-mapped addresses the upper 64 bits are always 0, so we focus on
        // the lower 64 bits which carry all the entropy (0x0000FFFF_xxxxxxxx).
        // Ports are mixed via shift + XOR to spread their bits across the hash.
        ulong lo1 = (ulong)_Addr1;
        ulong lo2 = (ulong)_Addr2;
        ulong hi1 = (ulong)(_Addr1 >> 64);
        ulong hi2 = (ulong)(_Addr2 >> 64);

        // Combine ports into a single 32-bit value
        uint ports = ((uint)_Port1 << 16) | _Port2;

        // XOR-rotate mixing: each rotate uses a different prime shift count
        // to minimize collision probability on structured network data.
        ulong h = lo1;
        h = _RotateLeft(h, 5) ^ lo2;
        h = _RotateLeft(h, 7) ^ hi1;
        h = _RotateLeft(h, 11) ^ hi2;
        h = _RotateLeft(h, 13) ^ ports;

        // Final avalanche — ensures all bits influence the result
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCD; // Murmur3-style constant
        h ^= h >> 33;

        return (int)h;
    }

    /// <summary>Rotates a 64-bit value left by the specified number of bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _RotateLeft(ulong value, int shift) =>
        (value << shift) | (value >> (64 - shift));
}
