// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using NetworkInspector.Core.Reassembly;
using NetworkInspector.Protocols.Tcp;

namespace NetworkInspector.Protocols;

/// <summary>
/// Extension methods for registering all standard protocols with a <see cref="IStackBuilder"/>.
/// </summary>
public static class ProtocolRegistration
{
    /// <summary>
    /// Registers all 30 standard protocols and wires up dispatch tables.
    /// </summary>
    /// <param name="builder">The stack builder to register protocols with.</param>
    /// <remarks>
    /// <para>Registered protocols (in registration order):</para>
    /// <list type="bullet">
    ///   <item><b>Meta / link:</b> Frame, Ethernet, VLAN (802.1Q), LLC/SNAP, Linux SLL, Linux SLL2.</item>
    ///   <item><b>Network:</b> IPv4 (options + fragmentation), IPv6 (extension headers + fragmentation), ARP, ICMPv4, ICMPv6.</item>
    ///   <item><b>Transport:</b> TCP (options, stateful analysis, heuristic detection, stream reassembly for DNS-over-TCP), UDP.</item>
    ///   <item><b>Application:</b> DNS, DHCPv4, DHCPv6, TLS, DTLS, HTTP/1.x, HTTP/2 (HPACK), WebSocket, SOME/IP, JSON, Text.</item>
    ///   <item><b>Automotive:</b> CAN (classic / FD / XL), FlexRay, LIN, PDU Transport, Signal PDU.</item>
    ///   <item><b>Fallback:</b> Data (raw bytes when no specific dissector applies).</item>
    /// </list>
    /// <para>The frame protocol is auto-discovered by name "frame" during
    /// <see cref="StackBuilder.Build"/> and stored on <see cref="Stack.FrameProtocolId"/>.</para>
    /// <para>Dispatch graph (high level):
    /// PacketProtocol → Frame (<c>frame.link_type</c>) → {Ethernet | SLL | SLL2 | CAN | FlexRay | LIN}
    /// → Ethernet (<c>eth.type</c>) → {IPv4 | IPv6 | ARP | VLAN | LLC}
    /// → IPv4/IPv6 (<c>ip.proto</c>) → {TCP | UDP | ICMP | ICMPv6 | …}
    /// → UDP (<c>udp.port</c>) → {DNS | DHCPv4 | DHCPv6 | DTLS | SOME/IP | …}
    /// → TCP (<c>tcp.port</c> + heuristics) → {TLS | HTTP/1.x | HTTP/2 | WebSocket | DNS-over-TCP | …}.</para>
    /// <para>Dispatch table wiring is handled automatically via <c>[RegisterAtTable]</c> and
    /// <c>[UsesTable]</c> attributes — no manual registration needed.</para>
    /// </remarks>
    public static void RegisterStandardProtocols(this IStackBuilder builder)
    {
        // Eagerly initialize all display text lookup tables so the cost is paid
        // during setup, not during the first packet parse in the timed phase.
        DisplayTables.EnsureInitialized();

        // Frame — entry point, owns the frame.link_type dispatch table
        // Auto-discovered by name FrameProtocol.ProtocolName during Build()
        FrameProtocol frame = new();
        ProtocolId frameId = builder.RegisterProtocol(frame);
        frame.RegisterFields(builder, frameId);

        // Ethernet — auto-registered at frame.link_type = 1, owns the eth.type dispatch table
        EthernetProtocol ethernet = new();
        ProtocolId ethId = builder.RegisterProtocol(ethernet);
        ethernet.RegisterFields(builder, ethId);

        // VLAN (802.1Q) — auto-registered at eth.type = 0x8100, 0x88A8
        VlanProtocol vlan = new();
        ProtocolId vlanId = builder.RegisterProtocol(vlan);
        vlan.RegisterFields(builder, vlanId);

        // IPv4 — auto-registered at eth.type = 0x0800, owns ip.proto dispatch table
        IPv4Protocol ipv4 = new();
        ProtocolId ipv4Id = builder.RegisterProtocol(ipv4);
        ipv4.RegisterFields(builder, ipv4Id);

        // IPv6 — auto-registered at eth.type = 0x86DD, auto-resolves ip.proto table
        IPv6Protocol ipv6 = new();
        ProtocolId ipv6Id = builder.RegisterProtocol(ipv6);
        ipv6.RegisterFields(builder, ipv6Id);

        // UDP — auto-registered at ip.proto = 17
        UdpProtocol udp = new();
        ProtocolId udpId = builder.RegisterProtocol(udp);
        udp.RegisterFields(builder, udpId);

        // ARP — auto-registered at eth.type = 0x0806
        ArpProtocol arp = new();
        ProtocolId arpId = builder.RegisterProtocol(arp);
        arp.RegisterFields(builder, arpId);

        // ICMP — auto-registered at ip.proto = 1
        IcmpProtocol icmp = new();
        ProtocolId icmpId = builder.RegisterProtocol(icmp);
        icmp.RegisterFields(builder, icmpId);

        // ICMPv6 — auto-registered at ip.proto = 58
        Icmpv6Protocol icmpv6 = new();
        ProtocolId icmpv6Id = builder.RegisterProtocol(icmpv6);
        icmpv6.RegisterFields(builder, icmpv6Id);

        // TCP — auto-registered at ip.proto = 6
        TcpProtocol tcp = new();
        ProtocolId tcpId = builder.RegisterProtocol(tcp);
        tcp.RegisterFields(builder, tcpId);

        // SLL v1 — auto-registered at frame.link_type = 113
        SllProtocol sll = new();
        ProtocolId sllId = builder.RegisterProtocol(sll);
        sll.RegisterFields(builder, sllId);

        // SLL v2 — auto-registered at frame.link_type = 276
        Sll2Protocol sll2 = new();
        ProtocolId sll2Id = builder.RegisterProtocol(sll2);
        sll2.RegisterFields(builder, sll2Id);

        // LLC — auto-registered at eth.ieee8023 = 1 (catch-all for IEEE 802.3 frames)
        LlcProtocol llc = new();
        ProtocolId llcId = builder.RegisterProtocol(llc);
        llc.RegisterFields(builder, llcId);

        // DNS — auto-registered at udp.port = 53 and tcp.port = 53
        DnsProtocol dns = new();
        ProtocolId dnsId = builder.RegisterProtocol(dns);
        dns.RegisterFields(builder, dnsId);

        // DHCPv4 — auto-registered at udp.port = 67 (server) and udp.port = 68 (client)
        DhcpProtocol dhcp = new();
        ProtocolId dhcpId = builder.RegisterProtocol(dhcp);
        dhcp.RegisterFields(builder, dhcpId);

        // DHCPv6 — auto-registered at udp.port = 546 (client) and udp.port = 547 (server)
        Dhcpv6Protocol dhcpv6 = new();
        ProtocolId dhcpv6Id = builder.RegisterProtocol(dhcpv6);
        dhcpv6.RegisterFields(builder, dhcpv6Id);

        // TLS — auto-registered at tcp.port = 443
        TlsProtocol tls = new();
        ProtocolId tlsId = builder.RegisterProtocol(tls);
        tls.RegisterFields(builder, tlsId);

        // DTLS — auto-registered at udp.port = 443
        DtlsProtocol dtls = new();
        ProtocolId dtlsId = builder.RegisterProtocol(dtls);
        dtls.RegisterFields(builder, dtlsId);

        // CAN (classic, FD, and XL) — registered once at frame.link_type = 227 (SocketCAN).
        // CAN XL frames are identified at parse time by the XLF flag (0x80) at byte offset 4
        // and handled by the same CanProtocol instance, so no separate registration is needed.
        CanProtocol can = new();
        ProtocolId canId = builder.RegisterProtocol(can);
        can.RegisterFields(builder, canId);

        // SOME/IP — auto-registered at udp.port = 30490 and tcp.port = 30490
        SomeIpProtocol someip = new();
        ProtocolId someipId = builder.RegisterProtocol(someip);
        someip.RegisterFields(builder, someipId);

        // FlexRay — auto-registered at frame.link_type = 210 (DLT_FLEXRAY)
        FlexRayProtocol flexray = new();
        ProtocolId flexrayId = builder.RegisterProtocol(flexray);
        flexray.RegisterFields(builder, flexrayId);

        // LIN — auto-registered at frame.link_type = 212 (DLT_LIN)
        LinProtocol lin = new();
        ProtocolId linId = builder.RegisterProtocol(lin);
        lin.RegisterFields(builder, linId);

        // HTTP/1.x — auto-registered at tcp.port = 80, 8080
        // Owns http.upgrade dispatch table for Upgrade-based protocol switching (WebSocket)
        // Must be registered before WebSocket so the http.upgrade table exists.
        HttpProtocol http = new();
        ProtocolId httpId = builder.RegisterProtocol(http);
        http.RegisterFields(builder, httpId);

        // HTTP/2 — auto-registered at tcp.port = 8443
        Http2Protocol http2 = new();
        ProtocolId http2Id = builder.RegisterProtocol(http2);
        http2.RegisterFields(builder, http2Id);

        // WebSocket — auto-registered at http.upgrade = "websocket"
        WebSocketProtocol ws = new();
        ProtocolId wsId = builder.RegisterProtocol(ws);
        ws.RegisterFields(builder, wsId);

        // Data — trivial fallback dissector for raw binary payloads (no table registration)
        DataProtocol data = new();
        ProtocolId dataId = builder.RegisterProtocol(data);
        data.RegisterFields(builder, dataId);

        // Text — line-based text display protocol (no table registration)
        TextProtocol text = new();
        ProtocolId textId = builder.RegisterProtocol(text);
        text.RegisterFields(builder, textId);

        // JSON — recursive JSON tree parser (no table registration)
        JsonProtocol json = new();
        ProtocolId jsonId = builder.RegisterProtocol(json);
        json.RegisterFields(builder, jsonId);

        // PDU Transport — concatenated PDU framing with config-based names
        PduTransportProtocol pduTransport = new();
        ProtocolId pduTransportId = builder.RegisterProtocol(pduTransport);
        pduTransport.RegisterFields(builder, pduTransportId);

        // Signal PDU — bit-level signal extraction with config-driven registration
        SignalPduProtocol signalPdu = new();
        ProtocolId signalPduId = builder.RegisterProtocol(signalPdu);
        signalPdu.RegisterFields(builder, signalPduId);

        #region TCP Heuristic Protocol Detection
        // Register the heuristic table on TCP and wire up content-based parsers.
        // This must come after all target protocols (HTTP, TLS, HTTP/2) are registered
        // so their ProtocolIds are available for the heuristic parsers.
        HeuristicProtocolTableId tcpHeuristicTableId = builder.RegisterHeuristicProtocolTable(
            tcpId, TcpProtocol.HeuristicTableName, "TCP Heuristic",
            "Content-based application protocol detection for TCP payload");
        tcp.SetHeuristicTableId(tcpHeuristicTableId);

        builder.RegisterHeuristicParser(tcpHeuristicTableId, new HttpHeuristicParser(httpId));
        builder.RegisterHeuristicParser(tcpHeuristicTableId, new TlsHeuristicParser(tlsId));
        builder.RegisterHeuristicParser(tcpHeuristicTableId, new Http2HeuristicParser(http2Id));

        #endregion

        #region TCP Stream Reassembly Configurations
        // Register PDU boundary detectors for protocols that need TCP reassembly.
        // DNS/TCP uses a 2-byte big-endian length prefix before each DNS message (RFC 1035 §4.2.2).
        builder.RegisterStreamReassemblyConfig(dnsId, new StreamReassemblyConfig
        {
            BoundaryDetector = new LengthPrefixDetector(
                lengthOffset: 0,
                lengthSize: 2,
                bigEndian: true,
                lengthIncludesHeader: false,
                headerSize: 2),
        });
        #endregion
    }
}
