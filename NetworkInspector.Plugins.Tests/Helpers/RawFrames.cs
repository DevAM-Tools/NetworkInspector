// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Builds Ethernet/IPv4 frames with a UDP datagram or a TCP segment around an arbitrary payload, with valid IPv4 and
/// transport checksums, for tests that need payloads the sample writer does not produce. Stateless and thread-safe.
/// </summary>
internal static class RawFrames
{
    #region Constants

    private const uint _SourceAddress = 0xC0000201;

    private const uint _DestinationAddress = 0xC0000202;

    #endregion

    #region Public API

    /// <summary>Builds an Ethernet/IPv4/UDP frame around <paramref name="payload"/>.</summary>
    /// <param name="payload">UDP payload.</param>
    /// <param name="sourcePort">UDP source port.</param>
    /// <param name="destinationPort">UDP destination port.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] Udp(ReadOnlySpan<byte> payload, ushort sourcePort, ushort destinationPort)
    {
        byte[] frame = new byte[14 + 20 + 8 + payload.Length];
        _WriteEthernetAndIp(frame, 20 + 8 + payload.Length, 17);
        Span<byte> udp = frame.AsSpan(34);
        BinaryPrimitives.WriteUInt16BigEndian(udp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payload.Length));
        payload.CopyTo(udp[8..]);
        return frame;
    }

    /// <summary>Builds an Ethernet/IPv4/TCP frame (PSH|ACK) around <paramref name="payload"/>.</summary>
    /// <param name="payload">TCP payload.</param>
    /// <param name="sourcePort">TCP source port; one source port is one connection.</param>
    /// <param name="destinationPort">TCP destination port.</param>
    /// <param name="sequence">Sequence number of the segment.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] Tcp(ReadOnlySpan<byte> payload, ushort sourcePort, ushort destinationPort, uint sequence)
    {
        byte[] frame = new byte[14 + 20 + 20 + payload.Length];
        _WriteEthernetAndIp(frame, 20 + 20 + payload.Length, 6);
        Span<byte> tcp = frame.AsSpan(34);
        BinaryPrimitives.WriteUInt16BigEndian(tcp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 1);
        tcp[12] = 0x50;
        tcp[13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
        payload.CopyTo(tcp[20..]);

        // Checksum over the pseudo header and the whole segment, with the checksum field still zero.
        Span<byte> segment = tcp[..(20 + payload.Length)];
        uint pseudo = (_SourceAddress >> 16) + (_SourceAddress & 0xFFFF) + (_DestinationAddress >> 16)
            + (_DestinationAddress & 0xFFFF) + 6 + (uint)segment.Length;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], _Checksum(segment, pseudo));
        return frame;
    }

    #endregion

    #region Private helpers

    private static void _WriteEthernetAndIp(Span<byte> frame, int ipTotalLength, byte protocol)
    {
        frame[0] = 0x02;
        frame[5] = 0x02;
        frame[6] = 0x02;
        frame[11] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], 0x0800);
        Span<byte> ip = frame[14..];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipTotalLength);
        BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000);
        ip[8] = 64;
        ip[9] = protocol;
        BinaryPrimitives.WriteUInt32BigEndian(ip[12..], _SourceAddress);
        BinaryPrimitives.WriteUInt32BigEndian(ip[16..], _DestinationAddress);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], _Checksum(ip[..20], 0));
    }

    private static ushort _Checksum(ReadOnlySpan<byte> data, uint seed)
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
