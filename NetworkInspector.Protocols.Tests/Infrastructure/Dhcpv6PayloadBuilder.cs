// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Helpers for building DHCPv6 payloads (RFC 8415). Produces a 4-byte
/// client/server header (msg-type + 24-bit transaction id) followed by
/// a TLV option block (2-byte code, 2-byte length, value).
/// </summary>
internal static class Dhcpv6PayloadBuilder
{
    /// <summary>Single TLV option for the DHCPv6 option block.</summary>
    internal readonly struct Option(ushort code, byte[] data)
    {
        internal ushort Code { get; } = code;
        internal byte[] Data { get; } = data;
    }

    /// <summary>Builds a DHCPv6 message with the given message type, 24-bit XID and options.</summary>
    internal static byte[] BuildMessage(byte msgType, uint xid24, IList<Option> options)
    {
        if ((xid24 & 0xFF000000u) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(xid24));
        }

        int optionsLen = 0;
        for (int i = 0; i < options.Count; i++)
        {
            optionsLen += 4 + options[i].Data.Length;
        }

        byte[] buf = new byte[4 + optionsLen];
        buf[0] = msgType;
        buf[1] = (byte)((xid24 >> 16) & 0xFF);
        buf[2] = (byte)((xid24 >> 8) & 0xFF);
        buf[3] = (byte)(xid24 & 0xFF);
        int idx = 4;
        for (int i = 0; i < options.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(idx, 2), options[i].Code);
            idx += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(idx, 2), (ushort)options[i].Data.Length);
            idx += 2;
            options[i].Data.CopyTo(buf, idx);
            idx += options[i].Data.Length;
        }
        return buf;
    }

    /// <summary>Wraps a DHCPv6 payload in an Ethernet/IPv6/UDP frame.</summary>
    internal static byte[] WrapUdp(ReadOnlySpan<byte> payload, ushort srcPort = 546, ushort dstPort = 547)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x33, 0x33, 0x00, 0x01, 0x00, 0x02]));
        // fe80::1 → ff02::1:2 (canonical link-local DHCPv6 conversation).
        IPv6Layer ip = new(
            IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]),
            IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0, 0x01, 0, 0x02]));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }
}
