// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Generators;

/// <summary>
/// Utility methods for building raw network frame data for tests.
/// Produces standard Ethernet, CAN (SocketCAN), FlexRay, and LIN frame bytes.
/// </summary>
internal static class FrameBuilders
{
    // ========================================================================
    // Ethernet
    // ========================================================================

    /// <summary>
    /// Builds a simple Ethernet II frame: dst(6) + src(6) + ethertype(2 BE) + payload.
    /// </summary>
    internal static byte[] BuildEthernetFrame(
        ReadOnlySpan<byte> dstMac,
        ReadOnlySpan<byte> srcMac,
        ushort etherType,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[14 + payload.Length];
        dstMac[..6].CopyTo(frame);
        srcMac[..6].CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), etherType);
        payload.CopyTo(frame.AsSpan(14));
        return frame;
    }

    /// <summary>
    /// Builds an 802.1Q VLAN-tagged Ethernet frame:
    /// dst(6) + src(6) + TPID(0x8100, 2 BE) + TCI(2 BE) + ethertype(2 BE) + payload.
    /// </summary>
    internal static byte[] BuildVlanEthernetFrame(
        ReadOnlySpan<byte> dstMac,
        ReadOnlySpan<byte> srcMac,
        ushort vlanId,
        ushort etherType,
        ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[18 + payload.Length];
        dstMac[..6].CopyTo(frame);
        srcMac[..6].CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x8100); // TPID
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), vlanId); // TCI
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), etherType);
        payload.CopyTo(frame.AsSpan(18));
        return frame;
    }

    // ========================================================================
    // SocketCAN
    // ========================================================================

    /// <summary>
    /// Builds a SocketCAN classic frame (16 bytes):
    /// id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-8, zero-padded to 8).
    /// </summary>
    /// <param name="canId">CAN arbitration ID (11 or 29 bit).</param>
    /// <param name="data">CAN data bytes (0–8).</param>
    /// <param name="extended">If true, sets the EFF bit (29-bit ID).</param>
    internal static byte[] BuildSocketCanClassic(uint canId, ReadOnlySpan<byte> data, bool extended = false)
    {
        int dlc = Math.Min(data.Length, 8);
        uint id = canId;
        if (extended)
        {
            id |= 0x8000_0000; // EFF flag
        }

        byte[] frame = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        // frame[5] = 0 (fd_flags = classic)
        // frame[6..7] = 0 (reserved)
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>
    /// Builds a SocketCAN FD frame (72 bytes):
    /// id(4 BE) + dlc(1) + fd_flags(1) + reserved(2) + data(0-64, zero-padded to 64).
    /// </summary>
    /// <param name="canId">CAN arbitration ID.</param>
    /// <param name="data">CAN data bytes (0–64).</param>
    /// <param name="extended">If true, sets the EFF bit.</param>
    /// <param name="brs">If true, sets the BRS (Bit Rate Switch) flag.</param>
    internal static byte[] BuildSocketCanFd(
        uint canId, ReadOnlySpan<byte> data, bool extended = false, bool brs = false)
    {
        int dlc = Math.Min(data.Length, 64);
        uint id = canId;
        if (extended)
        {
            id |= 0x8000_0000;
        }

        // SocketCAN canfd_frame.flags bits per Linux <linux/can.h>:
        //   CANFD_BRS = 0x01, CANFD_ESI = 0x02, CANFD_FDF = 0x04.
        byte fdFlags = 0x04; // FDF (FD format indicator)
        if (brs)
        {
            fdFlags |= 0x01; // BRS
        }

        byte[] frame = new byte[72];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = (byte)dlc;
        frame[5] = fdFlags;
        data[..dlc].CopyTo(frame.AsSpan(8));
        return frame;
    }

    // ========================================================================
    // IPv4 / UDP helpers (for Ethernet payload generation)
    // ========================================================================

    /// <summary>
    /// Builds a minimal IPv4 header (20 bytes, zero checksum) + payload.
    /// </summary>
    internal static byte[] BuildIpv4Packet(
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        byte protocol,
        ReadOnlySpan<byte> payload)
    {
        int totalLength = 20 + payload.Length;
        byte[] packet = new byte[totalLength];
        packet[0] = 0x45; // Version 4, IHL 5
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)totalLength);
        packet[6] = 0x40; // Don't Fragment
        packet[8] = 64;   // TTL
        packet[9] = protocol;
        srcIp[..4].CopyTo(packet.AsSpan(12));
        dstIp[..4].CopyTo(packet.AsSpan(16));
        payload.CopyTo(packet.AsSpan(20));
        return packet;
    }

    /// <summary>
    /// Builds a minimal UDP datagram (8-byte header, zero checksum) + payload.
    /// </summary>
    internal static byte[] BuildUdpDatagram(ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        int length = 8 + payload.Length;
        byte[] datagram = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)length);
        payload.CopyTo(datagram.AsSpan(8));
        return datagram;
    }

    /// <summary>
    /// Builds a complete Ethernet + IPv4 + UDP frame.
    /// </summary>
    internal static byte[] BuildEthernetIpv4UdpFrame(
        ReadOnlySpan<byte> srcMac,
        ReadOnlySpan<byte> dstMac,
        ReadOnlySpan<byte> srcIp,
        ReadOnlySpan<byte> dstIp,
        ushort srcPort,
        ushort dstPort,
        ReadOnlySpan<byte> payload)
    {
        byte[] udp = BuildUdpDatagram(srcPort, dstPort, payload);
        byte[] ipv4 = BuildIpv4Packet(srcIp, dstIp, 17, udp); // 17 = UDP
        return BuildEthernetFrame(dstMac, srcMac, 0x0800, ipv4); // 0x0800 = IPv4
    }
}
