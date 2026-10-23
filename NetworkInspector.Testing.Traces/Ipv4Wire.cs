// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// Hand-written Ethernet and IPv4 headers for the recipes that need bytes the FrameBuilder layers refuse to produce (a
/// datagram that is shorter than its protocol header) or that must equal the bytes of another writer exactly. The addresses
/// and MACs are those of <see cref="EthernetFrames"/>. Stateless; thread-safe.
/// </summary>
internal static class Ipv4Wire
{
    #region Constants

    /// <summary>Length of the Ethernet header in bytes.</summary>
    internal const int EthernetHeaderLength = 14;

    /// <summary>Length of the IPv4 header without options in bytes.</summary>
    internal const int Ipv4HeaderLength = 20;

    /// <summary>IP protocol number of TCP.</summary>
    internal const byte ProtocolTcp = 6;

    /// <summary>IP protocol number of UDP.</summary>
    internal const byte ProtocolUdp = 17;

    private const uint _SourceAddress = 0xC0000201;

    private const uint _DestinationAddress = 0xC0000202;

    #endregion

    #region Public API

    /// <summary>
    /// Writes the Ethernet header (destination 02:00:00:00:00:02, source 02:00:00:00:00:01, EtherType IPv4) and the IPv4 header
    /// with its checksum: version 4, header length 5, identification 0, don't fragment, TTL 64, from 192.0.2.1 to 192.0.2.2.
    /// </summary>
    /// <param name="frame">The frame; at least 34 bytes.</param>
    /// <param name="ipTotalLength">Value of the IPv4 total length field (it need not match the frame on purpose).</param>
    /// <param name="protocol">IP protocol number.</param>
    internal static void WriteEthernetAndIpv4(Span<byte> frame, int ipTotalLength, byte protocol)
    {
        frame[0] = 0x02;
        frame[5] = 0x02;
        frame[6] = 0x02;
        frame[11] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], 0x0800);

        Span<byte> ip = frame[EthernetHeaderLength..];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipTotalLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000);
        ip[8] = 64;
        ip[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(ip[12..], _SourceAddress);
        BinaryPrimitives.WriteUInt32BigEndian(ip[16..], _DestinationAddress);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], OnesComplementChecksum(ip[..Ipv4HeaderLength], 0));
    }

    /// <summary>The TCP checksum over the IPv4 pseudo header and a segment whose checksum field is still zero.</summary>
    /// <param name="segment">The TCP header and payload.</param>
    /// <returns>The checksum.</returns>
    internal static ushort TcpChecksum(ReadOnlySpan<byte> segment)
    {
        uint pseudo = (_SourceAddress >> 16) + (_SourceAddress & 0xFFFF)
            + (_DestinationAddress >> 16) + (_DestinationAddress & 0xFFFF)
            + ProtocolTcp + (uint)segment.Length;
        return OnesComplementChecksum(segment, pseudo);
    }

    /// <summary>The Internet checksum: the sum of the 16-bit big-endian words plus the seed, carries folded back in, inverted.</summary>
    /// <param name="data">The bytes; a trailing odd byte is the high byte of a last word.</param>
    /// <param name="seed">Sum to start from (the pseudo header).</param>
    /// <returns>The checksum.</returns>
    internal static ushort OnesComplementChecksum(ReadOnlySpan<byte> data, uint seed)
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
