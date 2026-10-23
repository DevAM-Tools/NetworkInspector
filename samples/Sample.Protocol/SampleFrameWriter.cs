// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Builds Ethernet frames that carry the sample payload, and defines the generated speed pattern. Used by the sample source,
/// by tests, and by the trace kit's own copy of the layout. Stateless; every member is thread-safe and allocates only the
/// returned frame.
/// </summary>
public static class SampleFrameWriter
{
    #region Constants

    /// <summary>Length of an Ethernet frame with a UDP datagram that carries the sample payload (14 + 20 + 8 + 14 bytes).</summary>
    public const int UdpFrameLength = 56;

    /// <summary>Length of an Ethernet frame with a TCP segment that carries the sample payload (14 + 20 + 20 + 14 bytes).</summary>
    public const int TcpFrameLength = 68;

    /// <summary>Source port used for UDP frames and, by default, for TCP frames.</summary>
    public const ushort DefaultSourcePort = 50000;

    /// <summary>Speed value that means "error" (special value of the generated pattern).</summary>
    public const byte SpeedError = 254;

    /// <summary>Speed value that means "signal not available" (special value of the generated pattern).</summary>
    public const byte SpeedNotAvailable = 255;

    private const int _EthernetHeaderLength = 14;

    private const int _Ipv4HeaderLength = 20;

    private const int _UdpHeaderLength = 8;

    private const int _TcpHeaderLength = 20;

    private const byte _IpProtocolTcp = 6;

    private const byte _IpProtocolUdp = 17;

    // Documentation addresses (RFC 5737): 192.0.2.1 sends to 192.0.2.2.
    private const uint _SourceAddress = 0xC0000201;

    private const uint _DestinationAddress = 0xC0000202;

    #endregion

    #region Public API

    /// <summary>Builds a 56-byte Ethernet/IPv4/UDP frame that carries the sample payload.</summary>
    /// <param name="counter">Frame counter written big endian into the payload.</param>
    /// <param name="gear">Gear byte of the payload.</param>
    /// <param name="speed">Speed byte of the payload.</param>
    /// <param name="udpPort">UDP destination port; the sample protocol is registered on <c>40000</c>.</param>
    /// <returns>A new array of <see cref="UdpFrameLength"/> bytes; the magic starts at offset 42.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="udpPort"/> is 0.</exception>
    public static byte[] Write(ulong counter, byte gear, byte speed, ushort udpPort)
    {
        ArgumentOutOfRangeException.ThrowIfZero(udpPort);

        byte[] frame = new byte[UdpFrameLength];
        _WriteEthernetAndIpv4(frame, _Ipv4HeaderLength + _UdpHeaderLength + SampleWire.PayloadLength, _IpProtocolUdp);

        // UDP header: source port, destination port, length, checksum 0 (not used over IPv4).
        Span<byte> udp = frame.AsSpan(_EthernetHeaderLength + _Ipv4HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(udp, DefaultSourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], udpPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], _UdpHeaderLength + SampleWire.PayloadLength);

        _WritePayload(udp[_UdpHeaderLength..], counter, gear, speed);
        return frame;
    }

    /// <summary>Builds a 68-byte Ethernet/IPv4/TCP frame (one PSH|ACK segment, no handshake) that carries the sample payload.</summary>
    /// <param name="counter">Frame counter written big endian into the payload.</param>
    /// <param name="gear">Gear byte of the payload.</param>
    /// <param name="speed">Speed byte of the payload.</param>
    /// <param name="tcpPort">TCP destination port; any port works because the heuristic parser finds the magic.</param>
    /// <param name="sequence">TCP sequence number of the segment.</param>
    /// <param name="sourcePort">TCP source port; one source port is one connection, which matters because TCP caches a heuristic match per connection.</param>
    /// <returns>A new array of <see cref="TcpFrameLength"/> bytes; the magic starts at offset 54.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tcpPort"/> or <paramref name="sourcePort"/> is 0.</exception>
    public static byte[] WriteTcp(ulong counter, byte gear, byte speed, ushort tcpPort, uint sequence,
        ushort sourcePort = DefaultSourcePort)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tcpPort);
        ArgumentOutOfRangeException.ThrowIfZero(sourcePort);

        byte[] frame = new byte[TcpFrameLength];
        _WriteEthernetAndIpv4(frame, _Ipv4HeaderLength + _TcpHeaderLength + SampleWire.PayloadLength, _IpProtocolTcp);

        // TCP header: ports, sequence, acknowledgment 1, data offset 5 words, flags PSH|ACK (a mid-stream segment),
        // window 65535, urgent pointer 0. The checksum is filled in last because it covers the payload.
        Span<byte> tcp = frame.AsSpan(_EthernetHeaderLength + _Ipv4HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], tcpPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 1);
        tcp[12] = (_TcpHeaderLength / 4) << 4;
        tcp[13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);

        _WritePayload(tcp[_TcpHeaderLength..], counter, gear, speed);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], _TcpChecksum(tcp[..(_TcpHeaderLength + SampleWire.PayloadLength)]));
        return frame;
    }

    /// <summary>
    /// Generated speed of frame <paramref name="index"/>: 254 (error) when <c>index % 50 == 0</c>, 255 (signal not available)
    /// when <c>index % 100</c> is 70–76, otherwise <c>index % 200</c>. The special values give value-text and chart tests a
    /// protocol-free fixture; the pattern never produces a value in 200–253.
    /// </summary>
    /// <param name="index">Zero-based frame index.</param>
    /// <returns>The speed byte of that frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static byte SpeedFor(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // The error window wins over the not-available window at index % 50 == 0 (they cannot overlap: 70-76 is not a multiple of 50).
        if (index % 50 == 0)
        {
            return SpeedError;
        }

        long hundred = index % 100;
        return hundred is >= 70 and <= 76
            ? SpeedNotAvailable
            : (byte)(index % 200);
    }

    #endregion

    #region Private helpers

    // Writes the Ethernet header and the IPv4 header including its checksum; the caller fills in the transport part.
    private static void _WriteEthernetAndIpv4(Span<byte> frame, int ipTotalLength, byte protocol)
    {
        // Ethernet: destination 02:00:00:00:00:02, source 02:00:00:00:00:01 (locally administered), EtherType IPv4.
        frame[0] = 0x02;
        frame[5] = 0x02;
        frame[6] = 0x02;
        frame[11] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], 0x0800);

        // IPv4: version 4 / IHL 5, total length, identification 0, don't fragment, TTL 64, protocol.
        Span<byte> ip = frame[_EthernetHeaderLength..];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipTotalLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000);
        ip[8] = 64;
        ip[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(ip[12..], _SourceAddress);
        BinaryPrimitives.WriteUInt32BigEndian(ip[16..], _DestinationAddress);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], _OnesComplementChecksum(ip[.._Ipv4HeaderLength], 0));
    }

    // Magic, counter (big endian), gear, speed.
    private static void _WritePayload(Span<byte> payload, ulong counter, byte gear, byte speed)
    {
        SampleWire.MagicBytes.CopyTo(payload);
        BinaryPrimitives.WriteUInt64BigEndian(payload[SampleWire.CounterOffset..], counter);
        payload[SampleWire.GearOffset] = gear;
        payload[SampleWire.SpeedOffset] = speed;
    }

    // TCP checksum over the IPv4 pseudo header (source, destination, zero, protocol, TCP length) and the segment, whose
    // checksum field is still zero.
    private static ushort _TcpChecksum(ReadOnlySpan<byte> segment)
    {
        uint pseudo = (_SourceAddress >> 16) + (_SourceAddress & 0xFFFF)
            + (_DestinationAddress >> 16) + (_DestinationAddress & 0xFFFF)
            + _IpProtocolTcp + (uint)segment.Length;
        return _OnesComplementChecksum(segment, pseudo);
    }

    // Internet checksum: sum of 16-bit big-endian words (a trailing odd byte is the high byte of a word) plus the seed, carries
    // folded back in, then inverted. Seed and sums stay far below uint overflow for headers and segments of this size.
    private static ushort _OnesComplementChecksum(ReadOnlySpan<byte> data, uint seed)
    {
        uint sum = seed;
        int index = 0;
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
        }

        if (index < data.Length)
        {
            sum += (uint)(data[index] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    #endregion
}
