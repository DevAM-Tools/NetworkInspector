// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Helpers for building BOOTP/DHCPv4 payloads. Produces a BOOTP fixed header
/// (with magic cookie) followed by a TLV option block terminated by the
/// End sentinel (0xFF). <see cref="WrapUdp"/> places the payload inside a
/// complete Ethernet/IPv4/UDP frame using <see cref="FrameStack"/>.
/// </summary>
internal static class DhcpPayloadBuilder
{
    /// <summary>BOOTP/DHCP magic cookie placed between the fixed header and the option block.</summary>
    private const uint MagicCookie = 0x63825363u;

    /// <summary>Single TLV option for the DHCP option block.</summary>
    internal readonly struct Option(byte type, byte[] data)
    {
        internal byte Type { get; } = type;
        internal byte[] Data { get; } = data;
    }

    /// <summary>Builds the DHCP payload (BOOTP fixed header + cookie + options + End).</summary>
    internal static byte[] BuildPayload(
        byte op,
        uint xid,
        IList<Option> options,
        ushort flags = 0,
        IPv4Address ciaddr = default,
        IPv4Address yiaddr = default,
        IPv4Address siaddr = default,
        IPv4Address giaddr = default)
    {
        // chaddr: hard-wired to AA:BB:CC:DD:EE:FF for tests.
        byte[] mac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

        int optionsLen = 1; // End byte
        for (int i = 0; i < options.Count; i++)
        {
            optionsLen += 2 + options[i].Data.Length;
        }

        byte[] buf = new byte[240 + optionsLen];
        buf[0] = op;
        buf[1] = 1;            // htype = Ethernet
        buf[2] = 6;            // hlen = 6
        buf[3] = 0;            // hops
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), ciaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), yiaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), siaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), giaddr.RawValue);
        mac.CopyTo(buf.AsSpan(28));
        // sname (64) and file (128) left zero; they fall between offsets 44 and 236.
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(236, 4), MagicCookie);

        int idx = 240;
        for (int i = 0; i < options.Count; i++)
        {
            buf[idx++] = options[i].Type;
            buf[idx++] = (byte)options[i].Data.Length;
            options[i].Data.CopyTo(buf, idx);
            idx += options[i].Data.Length;
        }
        buf[idx] = 0xFF; // End
        return buf;
    }

    /// <summary>Wraps a DHCP payload in a complete Ethernet/IPv4/UDP frame.</summary>
    internal static byte[] WrapUdp(ReadOnlySpan<byte> dhcpPayload, ushort srcPort = 68, ushort dstPort = 67)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        // 0.0.0.0 → 255.255.255.255 is the canonical DHCP DISCOVER source/destination pair.
        IPv4Layer ip = new(new IPv4Address(0x00000000u), new IPv4Address(0xFFFFFFFFu));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(dhcpPayload);
    }
}
