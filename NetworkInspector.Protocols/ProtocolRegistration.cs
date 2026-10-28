// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Extension methods for registering all standard protocols with a <see cref="IStackBuilder"/>.
/// </summary>
public static class ProtocolRegistration
{
    /// <summary>
    /// Registers the lean protocol set for profiling: Frame, Ethernet, IPv4, IPv6, UDP, and TCP, in this order. No TCP
    /// heuristics, no stream reassembly configurations, no application protocols.
    /// </summary>
    /// <param name="builder">The stack builder to register protocols with.</param>
    /// <returns>An empty warning list: none of these protocols reads an external configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SettingsLoadWarning> RegisterFairProfilingProtocols(this IStackBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Eagerly initialize display tables so the timed phase never pays first-use cost.
        DisplayTables.EnsureInitialized();

        // Each protocol is registered and then has its fields registered right away. The dispatch tables are wired by
        // the [RegisterAtTable] / [UsesTable] attributes (see RegisterStandardProtocols). Protocols that are not
        // registered here (VLAN, ICMP, ARP, ...) are not part of this lean set and are therefore not dissected.
        FrameProtocol frame = new();
        ProtocolId frameId = builder.RegisterProtocol(frame);
        frame.RegisterFields(builder, frameId);

        EthernetProtocol ethernet = new();
        ProtocolId ethId = builder.RegisterProtocol(ethernet);
        ethernet.RegisterFields(builder, ethId);

        IPv4Protocol ipv4 = new();
        ProtocolId ipv4Id = builder.RegisterProtocol(ipv4);
        ipv4.RegisterFields(builder, ipv4Id);

        IPv6Protocol ipv6 = new();
        ProtocolId ipv6Id = builder.RegisterProtocol(ipv6);
        ipv6.RegisterFields(builder, ipv6Id);

        UdpProtocol udp = new();
        ProtocolId udpId = builder.RegisterProtocol(udp);
        udp.RegisterFields(builder, udpId);

        TcpProtocol tcp = new();
        ProtocolId tcpId = builder.RegisterProtocol(tcp);
        tcp.RegisterFields(builder, tcpId);

        return [];
    }

    /// <summary>
    /// Registers all standard protocols and wires up dispatch tables.
    /// </summary>
    /// <param name="builder">The stack builder to register protocols with.</param>
    /// <remarks>
    /// <para>Registered protocols (in registration order):</para>
    /// <list type="bullet">
    ///   <item><b>Meta / link:</b> Frame, Ethernet, VLAN (802.1Q), LLC/SNAP, Linux SLL, Linux SLL2.</item>
    ///   <item><b>Network:</b> IPv4 (options + fragmentation), IPv6 (extension headers + fragmentation), ARP, ICMPv4, ICMPv6.</item>
    ///   <item><b>Transport:</b> TCP (options, stateful analysis, heuristic detection, stream reassembly for DNS-over-TCP and HTTP/1), UDP.</item>
    ///   <item><b>Application:</b> DNS, DHCPv4, DHCPv6, TLS, DTLS, HTTP/1.x, HTTP/2 (HPACK), WebSocket, SOME/IP, JSON, Text.</item>
    ///   <item><b>Automotive:</b> CAN (classic / FD / XL), FlexRay, LIN, PDU Transport,
    ///   Signal messages (<see cref="SignalMessage.SignalMessageRegistration"/>).</item>
    ///   <item><b>Fallback:</b> Data (raw bytes when no specific dissector applies).</item>
    /// </list>
    /// <para>Two ordering constraints are explicit in the code: HTTP/1.x before WebSocket (HTTP owns the
    /// <c>http.upgrade</c> table WebSocket attaches to), and the TCP heuristic and reassembly setup at the end, which
    /// needs the protocol ids of its targets.</para>
    /// <para>The frame protocol is auto-discovered by name "frame" during
    /// <see cref="StackBuilder.Build"/> and stored on the stack's <c>FrameProtocolId</c> property.</para>
    /// <para>Dispatch graph (high level):
    /// PacketProtocol → Frame (<c>frame.link_type</c>) → {Ethernet | SLL | SLL2 | CAN | FlexRay | LIN}
    /// → Ethernet (<c>eth.type</c>) → {IPv4 | IPv6 | ARP | VLAN | LLC}
    /// → IPv4/IPv6 (<c>ip.proto</c>) → {TCP | UDP | ICMP | ICMPv6 | …}
    /// → UDP (<c>udp.port</c>) → {DNS | DHCPv4 | DHCPv6 | DTLS | SOME/IP | …}
    /// → TCP (<c>tcp.port</c> + heuristics) → {TLS | HTTP/1.x | HTTP/2 | WebSocket | DNS-over-TCP | …}.</para>
    /// <para>Dispatch table wiring is handled automatically via <c>[RegisterAtTable]</c> and
    /// <c>[UsesTable]</c> attributes — no manual registration needed.</para>
    /// <para>
    /// PDU Transport and Signal Message JSON issues are returned as
    /// <see cref="SettingsLoadWarning"/> entries. Inspect the list; this method does not
    /// discard them. Settings/profile files may be empty. Additional JSON can be loaded
    /// afterwards via stream/object overloads on
    /// <see cref="PduTransport.PduTransportRegistration"/> and
    /// <see cref="SignalMessage.SignalMessageRegistration"/> (merged, not replacing).
    /// </para>
    /// </remarks>
    /// <returns>
    /// Warnings from PDU Transport (config load, field-size clamps) and Signal Message
    /// (config load, compile skips, registration skips). Empty when both succeed with
    /// no external config.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SettingsLoadWarning> RegisterStandardProtocols(this IStackBuilder builder)
    {
        // A null builder would fail deep inside the first RegisterProtocol call with an unrelated stack trace;
        // fail at the entry with the parameter name instead (same as RegisterFairProfilingProtocols).
        ArgumentNullException.ThrowIfNull(builder);

        // Eagerly initialize all display text lookup tables so the cost is paid
        // during setup, not during the first packet parse in the timed phase.
        DisplayTables.EnsureInitialized();

        // Collects the warnings of the registrations that read external JSON (PDU Transport, Signal Message); they
        // are returned, never discarded, so the host decides how to surface a bad configuration file.
        List<SettingsLoadWarning> warnings = [];

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
        PduTransportRegistration.Register(builder, warnings);

        // Signal messages — JSON-driven per-message protocols (no meta loader protocol)
        warnings.AddRange(SignalMessageRegistration.Register(builder));

        #region TCP Heuristic Protocol Detection
        // Register the heuristic table on TCP and wire up content-based parsers.
        // This must come after all target protocols (HTTP, TLS, HTTP/2) are registered
        // so their ProtocolIds are available for the heuristic parsers. TCP finds the table by
        // its name when the stack starts.
        HeuristicProtocolTableId tcpHeuristicTableId = builder.RegisterHeuristicProtocolTable(
            tcpId, TcpProtocol.HeuristicTableName, "TCP Heuristic",
            "Content-based application protocol detection for TCP payload");

        builder.RegisterHeuristicParser(tcpHeuristicTableId, new HttpHeuristicParser(httpId));
        builder.RegisterHeuristicParser(tcpHeuristicTableId, new TlsHeuristicParser(tlsId));
        builder.RegisterHeuristicParser(tcpHeuristicTableId, new Http2HeuristicParser(http2Id));

        #endregion

        #region TCP Stream Reassembly Configurations
        // Register PDU boundary detectors for protocols that need TCP reassembly.
        // DNS/TCP uses a 2-byte big-endian length prefix before each DNS message (RFC 1035 §4.2.2). The prefix does not
        // count itself (lengthIncludesHeader false), so the detector adds headerSize 2 to get the total PDU length.
        // Limit: MaxPduSize is the largest legal PDU, 65535 bytes of DNS message plus the 2-byte prefix (RFC 1035 §4.2.2).
        // The stream buffer compares it with the total PDU length including the prefix, so the default of 65536 would
        // refuse (and put the stream into its error state for) a maximum-size message such as a large zone transfer
        // answer. Threat it still bounds: a detector fed hostile bytes announcing an oversized PDU; with a 16-bit length field
        // the format itself caps a PDU at this value, so it is a constant, not a tunable. Posture: secure by default,
        // nothing larger than the format allows is accepted. MaxBufferSize stays at its default of 1 MiB.
        builder.RegisterStreamReassemblyConfig(dnsId, new StreamReassemblyConfig
        {
            MaxPduSize = ushort.MaxValue + 2,
            BoundaryDetector = new LengthPrefixDetector(
                lengthOffset: 0,
                lengthSize: 2,
                bigEndian: true,
                lengthIncludesHeader: false,
                headerSize: 2),
        });

        // HTTP/1 messages are framed by headers plus Content-Length or chunked encoding, or by the end of the connection for a
        // response without a length; the detector also follows HEAD and CONNECT requests (see Http1MessageDetector).
        // Limits: both are raised from the defaults (64 KiB per PDU, 1 MiB buffer per direction) to 16 MiB so that HTTP
        // messages with a larger body can complete. Threat they still bound: a peer announcing a huge body or never
        // finishing a message, which would otherwise grow the per-stream buffer without end. Exceeding the buffer limit drops
        // the message in progress and reports tcp.error.reassembly_limit; the connection and its later messages keep working
        // (nothing is truncated silently, and one oversized transfer does not switch reassembly off). Constants chosen by
        // this registration code, not user-configurable here; open by default compared with the defaults. The exact number
        // 16 MiB has no recorded derivation.
        builder.RegisterStreamReassemblyConfig(httpId, new StreamReassemblyConfig
        {
            BoundaryDetector = new Http1MessageDetector(),
            MaxPduSize = 16 * 1024 * 1024,
            MaxBufferSize = 16 * 1024 * 1024,
        });

        // TLS records are framed per record and extended to the records that complete a spanning handshake message
        // (TlsRecordDetector), so the server name, ALPN and certificates of a split hello or chain are parsed.
        // Limits: MaxPduSize 2 MiB bounds one framed unit, a handshake message that spans records (the 24-bit handshake
        // length would allow 16 MiB, but no real certificate chain exceeds a few hundred KiB); the threat is a hostile length
        // that makes the stream buffer fill up to the cap and then drop. MaxBufferSize 4 MiB bounds the bytes held per
        // direction; the threat is a peer that never completes a record. Constants chosen by this registration code, not
        // user-configurable here; secure by default (TLS is exposed to untrusted peers and a unit never needs more).
        // Exceeding the buffer limit drops the unit in progress and reports tcp.error.reassembly_limit.
        builder.RegisterStreamReassemblyConfig(tlsId, new StreamReassemblyConfig
        {
            BoundaryDetector = new TlsRecordDetector(),
            MaxPduSize = 2 * 1024 * 1024,
            MaxBufferSize = 4 * 1024 * 1024,
        });

        // HTTP/2: the client preface is a unit of its own, then one frame per unit (Http2FrameDetector). A frame header is
        // accepted only when its type is defined and its payload fits the frame size limit of the direction (16384 until the
        // peer announces more), so encrypted bytes on a port that usually carries TLS are not buffered as a "frame".
        // Limits: MaxPduSize is header plus the largest payload the format allows (2^24 - 1, a format fact: the peer may
        // announce a larger frame size limit in SETTINGS); MaxBufferSize 16 MiB bounds the bytes held per direction (threat:
        // a peer that announces a huge limit and never completes a frame). Constants of this registration code, not
        // user-configurable; open by default compared with the defaults, like HTTP/1, because frames above 16 KiB are legal
        // once announced. Exceeding the buffer limit drops the frame in progress and reports tcp.error.reassembly_limit.
        builder.RegisterStreamReassemblyConfig(http2Id, new StreamReassemblyConfig
        {
            BoundaryDetector = new Http2FrameDetector(),
            MaxPduSize = Http2FrameHeader.Size + Http2FrameDetector.AbsoluteMaxFrameSize,
            MaxBufferSize = 16 * 1024 * 1024,
        });
        #endregion

        return warnings;
    }
}
