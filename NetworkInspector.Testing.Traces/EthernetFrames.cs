// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// Frame builders shared by the Ethernet recipes, on the FrameBuilder layers (lengths and checksums are computed). All frames
/// travel from 192.0.2.1 / 02:00:00:00:00:01 to 192.0.2.2 / 02:00:00:00:00:02 (documentation addresses). Stateless;
/// thread-safe.
/// </summary>
internal static class EthernetFrames
{
    #region Constants

    /// <summary>Source MAC address of every frame.</summary>
    internal static readonly MacAddress SourceMac = MacAddress.FromBytes([0x02, 0x00, 0x00, 0x00, 0x00, 0x01]);

    /// <summary>Destination MAC address of every unicast frame.</summary>
    internal static readonly MacAddress DestinationMac = MacAddress.FromBytes([0x02, 0x00, 0x00, 0x00, 0x00, 0x02]);

    /// <summary>Source IPv4 address of every frame (192.0.2.1).</summary>
    internal static readonly IPv4Address SourceAddress = new(0xC0000201);

    /// <summary>Destination IPv4 address of every frame (192.0.2.2).</summary>
    internal static readonly IPv4Address DestinationAddress = new(0xC0000202);

    /// <summary>Largest frame size the Ethernet layer accepts when jumbo frames are requested.</summary>
    internal const ushort MaxJumboFrameSize = ushort.MaxValue;

    /// <summary>DNS record type A (IPv4 address).</summary>
    private const ushort _DnsTypeA = 1;

    #endregion

    #region Public API

    /// <summary>Builds an Ethernet/IPv4/UDP frame with the given payload; IPv4 does not fragment.</summary>
    /// <param name="sourcePort">UDP source port.</param>
    /// <param name="destinationPort">UDP destination port.</param>
    /// <param name="payload">The UDP payload.</param>
    /// <param name="maxFrameSize">Largest frame the link accepts (1518 for normal frames).</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] Udp(ushort sourcePort, ushort destinationPort, ReadOnlySpan<byte> payload, ushort maxFrameSize = 1518)
    {
        EthernetLayer eth = new(DestinationMac, SourceMac, maxFrameSize: maxFrameSize);
        IPv4Layer ip = new(SourceAddress, DestinationAddress, dontFragment: true);
        UdpLayer udp = new(sourcePort, destinationPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds an Ethernet/IPv4/UDP frame that carries a DNS standard query for an A record.</summary>
    /// <param name="sourcePort">UDP source port.</param>
    /// <param name="id">DNS transaction identifier.</param>
    /// <param name="name">The queried name.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] DnsQuery(ushort sourcePort, ushort id, string name)
    {
        EthernetLayer eth = new(DestinationMac, SourceMac);
        IPv4Layer ip = new(SourceAddress, DestinationAddress);
        UdpLayer udp = new(sourcePort, 53);
        DnsLayer dns = DnsLayer.BuildQuery(id, name, _DnsTypeA);
        return FrameStack.Start(eth).Then(ip).Then(udp).Then(dns).CreateWithFixedValues().EmitFrame([]);
    }

    /// <summary>Builds an Ethernet/IPv4/TCP frame with the ACK flag and the given payload.</summary>
    /// <param name="sourcePort">TCP source port.</param>
    /// <param name="destinationPort">TCP destination port.</param>
    /// <param name="sequence">Sequence number.</param>
    /// <param name="payload">The segment payload.</param>
    /// <returns>The frame bytes.</returns>
    internal static byte[] TcpAck(ushort sourcePort, ushort destinationPort, uint sequence, ReadOnlySpan<byte> payload)
    {
        EthernetLayer eth = new(DestinationMac, SourceMac);
        IPv4Layer ip = new(SourceAddress, DestinationAddress);
        TcpLayer tcp = new(sourcePort, destinationPort, sequence, ackNum: 1, flags: NetworkInspector.FrameBuilder.Constants.TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Builds an ARP request "who has 192.0.2.2" from 192.0.2.1 to the broadcast address.</summary>
    /// <returns>The frame bytes.</returns>
    internal static byte[] ArpRequest()
    {
        MacAddress broadcast = MacAddress.FromBytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        MacAddress unknown = MacAddress.FromBytes([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        EthernetLayer eth = new(broadcast, SourceMac);
        ArpLayer arp = new(ArpLayer.OpcodeRequest, SourceMac, SourceAddress, unknown, DestinationAddress);
        return FrameStack.Start(eth).Then(arp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion
}
