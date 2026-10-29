// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Internet Control Message Protocol for IPv6 (RFC 4443) parser with Neighbor Discovery (RFC 4861), Multicast
/// Listener Discovery (RFC 2710, RFC 3810) and optional checksum validation.
/// <para>Field tree structure (the optional groups appear only for the message types named in brackets):</para>
/// <code>
/// icmpv6: Internet Control Message Protocol v6
/// ├── icmpv6.type: 128 (Echo Request)
/// ├── icmpv6.code: 0
/// ├── icmpv6.checksum: 0xabcd
/// ├── icmpv6.checksum.status: [Good]                [optional, when verification enabled]
/// ├── icmpv6.echo.identifier: 0x1234                [optional, echo only]
/// ├── icmpv6.echo.sequence_number: 1                [optional, echo only]
/// ├── icmpv6.mtu: 1280                              [optional, Packet Too Big]
/// ├── icmpv6.pointer: 40                            [optional, Parameter Problem]
/// ├── icmpv6.length: 5 (40 bytes)                   [optional, Destination Unreachable / Time Exceeded, RFC 4884]
/// ├── icmpv6.reserved: 00 00 00                     [optional, Destination Unreachable / Time Exceeded]
/// ├── icmpv6.orig_ipv6: Internet Protocol Version 6 [optional, error types quoting an IPv6 header]
/// │   ├── icmpv6.orig_ipv6.src / .dst / .nxt / .hlim
/// │   └── icmpv6.orig_ipv6.srcport / .dstport       [optional, quoted TCP or UDP]
/// ├── icmpv6.mld.maximum_response_delay             [MLDv1, types 130-132 with 24 bytes]
/// ├── icmpv6.mld.multicast_address                  [MLDv1 and MLDv2 query]
/// ├── icmpv6.mld.maximum_response_code / .flag{.reserved,.s,.qrv} / .qqi / .nb_sources / .source_address
/// │                                                 [MLDv2 query, type 130 with 28 or more bytes]
/// ├── icmpv6.mldr.nb_mcast_records, .mar{.record_type,.aux_data_len,.nb_sources,.multicast_address,
/// │   .source_address,.auxiliary_data}              [MLDv2 report, type 143]
/// ├── icmpv6.nd.ra.cur_hop_limit, .flag{.m,.o,.h,.prf,.p,.s,.rsv}, .router_lifetime, .reachable_time,
/// │   .retrans_timer                                [Router Advertisement, type 134]
/// ├── icmpv6.nd.ns.target_address                   [Neighbor Solicitation, type 135]
/// ├── icmpv6.nd.na.flag{.r,.s,.o,.rsv}, .target_address   [Neighbor Advertisement, type 136]
/// ├── icmpv6.nd.rd.target_address, .destination_address   [Redirect, type 137]
/// ├── icmpv6.opt (one per option): .type, .length, then one of .linkaddr | .linkaddr_eui64 | .linkaddr_raw,
/// │   .prefix.* (length, flag.l/a/r/p/reserved, lifetimes, prefix), .mtu, .rdnss.*, .redirected_packet,
/// │   .advertisement_interval, .home_agent_*, .nonce, .route_info.*, .efo, .dnssl.*, .captive_portal, .pref64.*
/// │                                                 [types 133-137]
/// ├── icmpv6.nd.error.option_length / .hop_limit    [optional, a zero-length or truncated option ended the walk; a
///                                                 Neighbor Discovery message arrived with a hop limit other than 255]
/// └── icmpv6.data: (32 bytes)                       [optional, echo payload and other undecoded bodies]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>_OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// <para><b>Why the quoted datagram is decoded by hand:</b> see <see cref="IcmpProtocol"/>; the same reasoning holds
/// here. Dispatching the quoted bytes into the IPv6, TCP and UDP parsers would feed stream tracking and reassembly
/// with packets that were never on the wire at that position, so the few fields that matter are read directly.</para>
/// <para><b>Not decoded:</b> the NDP options 6, 9 to 13, 15 to 23, 27 to 30, 32 to 36, 39 to 42 and 144 (secure
/// neighbour discovery, 6LoWPAN, handover and encrypted DNS) are named and sized but their content is not decoded;
/// they show type and length only.</para>
/// <para><b>Enclosing IPv6 header:</b> two checks read the header bytes that the IPv6 container stores as its value
/// (<see cref="EnclosingIpHeader"/>) when the field is emitted, and the per-thread IPv6 cache (which holds the same hop limit)
/// when the index group is recorded, because the mode without a field tree has no container to read. A
/// Neighbor Discovery message must arrive with hop limit 255 (RFC 4861 section 6.1.2: a router on the path would have
/// decremented it, so any other value shows an off-link sender); another value is reported as
/// <c>icmpv6.nd.error.hop_limit</c>. A message that the capture length cut (the header declares more payload than was
/// captured) cannot have its checksum verified and shows <c>[Unverified]</c> instead of <c>[Bad]</c>.</para>
/// </remarks>
[Protocol("icmpv6", "Internet Control Message Protocol v6", Description = "ICMPv6 (RFC 4443)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class Icmpv6Protocol : IProtocol
{
    #region Constants

    /// <summary>IP protocol number for ICMPv6 (58).</summary>
    public const ulong IpProtoKey = 58;

    /// <summary>ICMPv6 protocol number for pseudo-header computation.</summary>
    private const byte _Icmpv6ProtocolNumber = 58;

    /// <summary>
    /// Minimum ICMPv6 message size in bytes that this parser accepts: the 4-byte header (type, code, checksum) plus
    /// the 4 bytes of message-specific data that every message type handled here carries (identifier and sequence
    /// number for Echo, reserved bytes or other type-specific words for the rest). Shorter input is a parse error.
    /// </summary>
    private const int _HeaderSize = 8;

    /// <summary>
    /// Size of the fixed ICMPv6 message header (type + code + checksum) that precedes an NDP
    /// message body. NDP messages (types 133–137) place their body immediately after these 4 bytes,
    /// unlike Echo which carries an additional 4-byte identifier/sequence pair (<see cref="_HeaderSize"/>).
    /// The eager NDP detector must slice the body at this offset so it observes exactly the same
    /// bytes the lazy populator passes to <see cref="Icmpv6NdpParser"/> (<c>span[4..]</c>).
    /// </summary>
    private const int _NdpHeaderSize = 4;

    /// <summary>Size of the fixed IPv6 header in bytes; a quoted datagram needs at least this much.</summary>
    private const int _Ipv6HeaderSize = 40;

    /// <summary>The hop limit every Neighbor Discovery message must have on arrival (RFC 4861 section 6.1.2).</summary>
    private const byte _NdpRequiredHopLimit = 255;

    /// <summary>Bytes of a quoted TCP or UDP header that hold both ports.</summary>
    private const int _QuotedPortsLength = 4;

    /// <summary>IP protocol number of TCP.</summary>
    private const byte _ProtocolTcp = 6;

    /// <summary>IP protocol number of UDP.</summary>
    private const byte _ProtocolUdp = 17;

    /// <summary>Index group for always-present ICMPv6 fields.</summary>
    private const string _Icmpv6IndexGroup = "icmpv6";

    /// <summary>ICMPv6 type: Destination Unreachable (RFC 4443 section 3.1).</summary>
    private const byte _TypeDestUnreach = 1;

    /// <summary>ICMPv6 type: Packet Too Big (RFC 4443 section 3.2).</summary>
    private const byte _TypePacketTooBig = 2;

    /// <summary>ICMPv6 type: Time Exceeded (RFC 4443 section 3.3).</summary>
    private const byte _TypeTimeExceeded = 3;

    /// <summary>ICMPv6 type: Parameter Problem (RFC 4443 section 3.4).</summary>
    private const byte _TypeParamProblem = 4;

    /// <summary>ICMPv6 type: Echo Request.</summary>
    private const byte _TypeEchoRequest = 128;

    /// <summary>ICMPv6 type: Echo Reply.</summary>
    private const byte _TypeEchoReply = 129;

    #endregion

    #region Fields

    [BytesField("icmpv6", "ICMPv6", IndexGroup = _Icmpv6IndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("icmpv6.type", "Type", IndexGroup = _Icmpv6IndexGroup)]
    private FieldId _TypeFieldId;

    [U64Field("icmpv6.code", "Code", IndexGroup = _Icmpv6IndexGroup)]
    private FieldId _CodeFieldId;

    [U64Field("icmpv6.checksum", "Checksum", IndexGroup = _Icmpv6IndexGroup)]
    private FieldId _ChecksumFieldId;

    // Optional: checksum validation status
    [StringField("icmpv6.checksum.status", "Checksum Status", IndexGroup = "icmpv6.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Optional: echo request/reply fields
    [U64Field("icmpv6.echo.identifier", "Identifier", IndexGroup = "icmpv6.echo")]
    private FieldId _EchoIdentFieldId;

    [U64Field("icmpv6.echo.sequence_number", "Sequence Number", IndexGroup = "icmpv6.echo")]
    private FieldId _EchoSeqFieldId;

    // Optional: payload data
    [BytesField("icmpv6.data", "Data", IndexGroup = "icmpv6.data")]
    private FieldId _DataFieldId;

    #endregion

    #region Error message fields (types 1-4)

    [U64Field("icmpv6.mtu", "MTU", IndexGroup = "icmpv6.mtu")]
    private FieldId _MtuFieldId;

    [U64Field("icmpv6.pointer", "Pointer", IndexGroup = "icmpv6.pointer")]
    private FieldId _PointerFieldId;

    // RFC 4884: length of the quoted original datagram in 8-byte units; 0 means "not RFC 4884".
    [U64Field("icmpv6.length", "Length of original datagram", IndexGroup = "icmpv6.length")]
    private FieldId _OrigLengthFieldId;

    [BytesField("icmpv6.reserved", "Reserved", IndexGroup = "icmpv6.reserved")]
    private FieldId _ReservedFieldId;

    // The quoted IPv6 header of an error message. Ports are a group of their own so that they are indexed exactly
    // when the quote reaches them (quoted TCP or UDP with four transport bytes).
    [NoneField("icmpv6.orig_ipv6", "Internet Protocol Version 6 (original datagram)", IndexGroup = "icmpv6.orig_ipv6")]
    private FieldId _OrigIpv6FieldId;

    [IPv6Field("icmpv6.orig_ipv6.src", "Source Address", IndexGroup = "icmpv6.orig_ipv6")]
    private FieldId _OrigIpv6SrcFieldId;

    [IPv6Field("icmpv6.orig_ipv6.dst", "Destination Address", IndexGroup = "icmpv6.orig_ipv6")]
    private FieldId _OrigIpv6DstFieldId;

    [U64Field("icmpv6.orig_ipv6.nxt", "Next Header", IndexGroup = "icmpv6.orig_ipv6")]
    private FieldId _OrigIpv6NxtFieldId;

    [U64Field("icmpv6.orig_ipv6.hlim", "Hop Limit", IndexGroup = "icmpv6.orig_ipv6")]
    private FieldId _OrigIpv6HlimFieldId;

    [U64Field("icmpv6.orig_ipv6.srcport", "Source Port", IndexGroup = "icmpv6.orig_ipv6.ports")]
    private FieldId _OrigIpv6SrcPortFieldId;

    [U64Field("icmpv6.orig_ipv6.dstport", "Destination Port", IndexGroup = "icmpv6.orig_ipv6.ports")]
    private FieldId _OrigIpv6DstPortFieldId;

    #endregion

    #region MLD fields (Multicast Listener Discovery — types 130-132 and 143)

    [U64Field("icmpv6.mld.maximum_response_delay", "Maximum Response Delay", IndexGroup = "icmpv6.mld")]
    private FieldId _MldMaxResponseDelayFieldId;

    [IPv6Field("icmpv6.mld.multicast_address", "Multicast Address", IndexGroup = "icmpv6.mld")]
    private FieldId _MldMulticastAddressFieldId;

    [U64Field("icmpv6.mld.maximum_response_code", "Maximum Response Code", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldMaxResponseCodeFieldId;

    [NoneField("icmpv6.mld.flag", "Flags", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldFlagFieldId;

    [U64Field("icmpv6.mld.flag.reserved", "Reserved", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldFlagReservedFieldId;

    [BoolField("icmpv6.mld.flag.s", "Suppress router-side processing", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldFlagSFieldId;

    [U64Field("icmpv6.mld.flag.qrv", "Querier's robustness variable", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldFlagQrvFieldId;

    [U64Field("icmpv6.mld.qqi", "Querier's query interval", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldQqiFieldId;

    [U64Field("icmpv6.mld.nb_sources", "Number of sources", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldNbSourcesFieldId;

    [IPv6Field("icmpv6.mld.source_address", "Source Address", IndexGroup = "icmpv6.mld.v2")]
    private FieldId _MldSourceAddressFieldId;

    [U64Field("icmpv6.mldr.nb_mcast_records", "Number of multicast address records", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordCountFieldId;

    [NoneField("icmpv6.mldr.mar", "Multicast Address Record", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordFieldId;

    [U64Field("icmpv6.mldr.mar.record_type", "Record type", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordTypeFieldId;

    [U64Field("icmpv6.mldr.mar.aux_data_len", "Auxiliary data length", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordAuxLenFieldId;

    [U64Field("icmpv6.mldr.mar.nb_sources", "Number of sources", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordNbSourcesFieldId;

    [IPv6Field("icmpv6.mldr.mar.multicast_address", "Multicast Address", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordMulticastAddressFieldId;

    [IPv6Field("icmpv6.mldr.mar.source_address", "Source Address", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordSourceAddressFieldId;

    [BytesField("icmpv6.mldr.mar.auxiliary_data", "Auxiliary Data", IndexGroup = "icmpv6.mldr")]
    private FieldId _MldrRecordAuxDataFieldId;

    #endregion

    #region NDP fields (Neighbor Discovery Protocol — types 133-137)

    // Router Advertisement
    [U64Field("icmpv6.nd.ra.cur_hop_limit", "Current Hop Limit", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaCurHopLimitFieldId;

    [NoneField("icmpv6.nd.ra.flag", "Flags", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagsFieldId;

    [BoolField("icmpv6.nd.ra.flag.m", "Managed address configuration", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagMFieldId;

    [BoolField("icmpv6.nd.ra.flag.o", "Other configuration", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagOFieldId;

    [BoolField("icmpv6.nd.ra.flag.h", "Home Agent", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagHFieldId;

    [U64Field("icmpv6.nd.ra.flag.prf", "Default Router Preference", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagPrfFieldId;

    [BoolField("icmpv6.nd.ra.flag.p", "Neighbor Discovery Proxy", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagPFieldId;

    [BoolField("icmpv6.nd.ra.flag.s", "SNAC Router", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagSFieldId;

    [U64Field("icmpv6.nd.ra.flag.rsv", "Reserved", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaFlagRsvFieldId;

    [U64Field("icmpv6.nd.ra.router_lifetime", "Router Lifetime", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaRouterLifetimeFieldId;

    [U64Field("icmpv6.nd.ra.reachable_time", "Reachable Time", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaReachableTimeFieldId;

    [U64Field("icmpv6.nd.ra.retrans_timer", "Retransmission Timer", IndexGroup = "icmpv6.nd.ra")]
    private FieldId _NdRaRetransTimerFieldId;

    // Target addresses: one field per message type (the message type is the context a reader needs), one shared group
    [IPv6Field("icmpv6.nd.ns.target_address", "Target Address", IndexGroup = "icmpv6.nd.target")]
    private FieldId _NdNsTargetAddressFieldId;

    [IPv6Field("icmpv6.nd.na.target_address", "Target Address", IndexGroup = "icmpv6.nd.target")]
    private FieldId _NdNaTargetAddressFieldId;

    [IPv6Field("icmpv6.nd.rd.target_address", "Target Address", IndexGroup = "icmpv6.nd.target")]
    private FieldId _NdRdTargetAddressFieldId;

    // Neighbor Advertisement flags
    [NoneField("icmpv6.nd.na.flag", "Flags", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagsFieldId;

    [BoolField("icmpv6.nd.na.flag.r", "Router", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagRFieldId;

    [BoolField("icmpv6.nd.na.flag.s", "Solicited", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagSFieldId;

    [BoolField("icmpv6.nd.na.flag.o", "Override", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagOFieldId;

    [U64Field("icmpv6.nd.na.flag.rsv", "Reserved", IndexGroup = "icmpv6.nd.na")]
    private FieldId _NdNaFlagRsvFieldId;

    // Redirect destination
    [IPv6Field("icmpv6.nd.rd.destination_address", "Destination Address", IndexGroup = "icmpv6.nd.redirect")]
    private FieldId _NdRdDestinationAddressFieldId;

    // NDP options
    [NoneField("icmpv6.opt", "Option", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptFieldId;

    [U64Field("icmpv6.opt.type", "Type", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptTypeFieldId;

    [U64Field("icmpv6.opt.length", "Length", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLengthFieldId;

    [MacField("icmpv6.opt.linkaddr", "Link-Layer Address", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLinkAddrFieldId;

    [Eui64Field("icmpv6.opt.linkaddr_eui64", "Link-Layer Address (EUI-64)", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLinkAddrEui64FieldId;

    [BytesField("icmpv6.opt.linkaddr_raw", "Link-Layer Address (raw)", IndexGroup = "icmpv6.nd.opt")]
    private FieldId _NdOptLinkAddrRawFieldId;

    [U64Field("icmpv6.opt.prefix.length", "Prefix Length", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixLengthFieldId;

    [BoolField("icmpv6.opt.prefix.flag.l", "On-link flag", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagLFieldId;

    [BoolField("icmpv6.opt.prefix.flag.a", "Autonomous address-configuration flag", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagAFieldId;

    [BoolField("icmpv6.opt.prefix.flag.r", "Router address flag", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagRFieldId;

    [BoolField("icmpv6.opt.prefix.flag.p", "DHCPv6-PD prefix flag", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagPFieldId;

    [U64Field("icmpv6.opt.prefix.flag.reserved", "Reserved", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFlagReservedFieldId;

    [U64Field("icmpv6.opt.prefix.valid_lifetime", "Valid Lifetime", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixValidLifetimeFieldId;

    [U64Field("icmpv6.opt.prefix.preferred_lifetime", "Preferred Lifetime", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixPreferredLifetimeFieldId;

    [IPv6Field("icmpv6.opt.prefix", "Prefix", IndexGroup = "icmpv6.nd.opt.prefix")]
    private FieldId _NdOptPrefixFieldId;

    [U64Field("icmpv6.opt.mtu", "MTU", IndexGroup = "icmpv6.nd.opt.mtu")]
    private FieldId _NdOptMtuFieldId;

    [U64Field("icmpv6.opt.rdnss.lifetime", "Lifetime", IndexGroup = "icmpv6.nd.opt.rdnss")]
    private FieldId _NdOptRdnssLifetimeFieldId;

    [IPv6Field("icmpv6.opt.rdnss", "DNS Server", IndexGroup = "icmpv6.nd.opt.rdnss")]
    private FieldId _NdOptRdnssAddressFieldId;

    [BytesField("icmpv6.opt.redirected_packet", "Redirected packet", IndexGroup = "icmpv6.nd.opt.redirected")]
    private FieldId _NdOptRedirectedPacketFieldId;

    [U64Field("icmpv6.opt.advertisement_interval", "Advertisement Interval", IndexGroup = "icmpv6.nd.opt.adv_interval")]
    private FieldId _NdOptAdvertisementIntervalFieldId;

    [U64Field("icmpv6.opt.home_agent_preference", "Home Agent Preference", IndexGroup = "icmpv6.nd.opt.home_agent")]
    private FieldId _NdOptHomeAgentPreferenceFieldId;

    [U64Field("icmpv6.opt.home_agent_lifetime", "Home Agent Lifetime", IndexGroup = "icmpv6.nd.opt.home_agent")]
    private FieldId _NdOptHomeAgentLifetimeFieldId;

    [BytesField("icmpv6.opt.nonce", "Nonce", IndexGroup = "icmpv6.nd.opt.nonce")]
    private FieldId _NdOptNonceFieldId;

    [U64Field("icmpv6.opt.route_info.prefix_length", "Prefix Length", IndexGroup = "icmpv6.nd.opt.route_info")]
    private FieldId _NdOptRouteInfoPrefixLengthFieldId;

    [U64Field("icmpv6.opt.route_info.flag.route_preference", "Route Preference", IndexGroup = "icmpv6.nd.opt.route_info")]
    private FieldId _NdOptRouteInfoPreferenceFieldId;

    [U64Field("icmpv6.opt.route_lifetime", "Route Lifetime", IndexGroup = "icmpv6.nd.opt.route_info")]
    private FieldId _NdOptRouteLifetimeFieldId;

    [IPv6Field("icmpv6.opt.route_info.prefix", "Prefix", IndexGroup = "icmpv6.nd.opt.route_info")]
    private FieldId _NdOptRouteInfoPrefixFieldId;

    [BytesField("icmpv6.opt.efo", "RA Flags Extension", IndexGroup = "icmpv6.nd.opt.flags_ext")]
    private FieldId _NdOptFlagsExtensionFieldId;

    [U64Field("icmpv6.opt.dnssl.lifetime", "Lifetime", IndexGroup = "icmpv6.nd.opt.dnssl")]
    private FieldId _NdOptDnsslLifetimeFieldId;

    [StringField("icmpv6.opt.dnssl", "Domain Name Suffix", IndexGroup = "icmpv6.nd.opt.dnssl")]
    private FieldId _NdOptDnsslFieldId;

    [StringField("icmpv6.opt.captive_portal", "Captive Portal URI", IndexGroup = "icmpv6.nd.opt.captive_portal")]
    private FieldId _NdOptCaptivePortalFieldId;

    [U64Field("icmpv6.opt.pref64.scaled_lifetime", "Scaled Lifetime", IndexGroup = "icmpv6.nd.opt.pref64")]
    private FieldId _NdOptPref64ScaledLifetimeFieldId;

    [U64Field("icmpv6.opt.pref64.plc", "Prefix Length Code", IndexGroup = "icmpv6.nd.opt.pref64")]
    private FieldId _NdOptPref64PlcFieldId;

    [IPv6Field("icmpv6.opt.pref64.prefix", "Prefix", IndexGroup = "icmpv6.nd.opt.pref64")]
    private FieldId _NdOptPref64PrefixFieldId;

    // A zero-length or truncated option ends the option walk. The error is a field, not a silent stop, so a malformed
    // message is visible in the tree and findable through the index.
    [StringField("icmpv6.nd.error.option_length", "Invalid option length", IndexGroup = "icmpv6.nd.error")]
    private FieldId _NdErrorOptionLengthFieldId;

    // A Neighbor Discovery message whose enclosing IPv6 hop limit is not 255 did not come from the local link (RFC 4861
    // section 6.1.2); an off-link router advertisement is the textbook injection. The field shares the error group.
    [StringField("icmpv6.nd.error.hop_limit", "Invalid hop limit", IndexGroup = "icmpv6.nd.error")]
    private FieldId _NdErrorHopLimitFieldId;

    // Settings. Loaded once when the stack starts and constant while it runs; Parse and the lazy populator both read it.
    [BoolSetting("icmpv6.verify_checksum", "Verify Checksum", "icmpv6", Default = false,
        Description = "Verifies the ICMPv6 checksum over the whole message and the IPv6 pseudo header and shows [Good] or [Bad]. "
            + "The status is [Unverified] when no enclosing IPv6 layer is found or when the capture length cut the message short.")]
    private bool _VerifyChecksum;

    #endregion

    #region Cross-Protocol Field References
    // Resolved during OnStart for the checksum pseudo-header lookup. They are the fallback when the per-thread IPv6
    // address cache does not hold this packet (see _ValidateChecksum): the lazy populator can run long after the
    // parse, on another thread. The fallback walks siblings (same path as UDP/TCP) rather than scanning the flat
    // field array, because the first occurrence in the flat array would be the outermost tunneled IPv6 header,
    // which is the wrong address pair. FieldId.Invalid marks a protocol that is not part of the stack.

    /// <summary>FieldId of the IPv4 container field ("ip").</summary>
    private FieldId _IpContainerFieldId;

    /// <summary>FieldId of the IPv6 container field ("ipv6").</summary>
    private FieldId _Ipv6ContainerFieldId;

    /// <summary>FieldId of ip.src (resolved at startup).</summary>
    private FieldId _IpSrcFieldId;

    /// <summary>FieldId of ip.dst (resolved at startup).</summary>
    private FieldId _IpDstFieldId;

    /// <summary>FieldId of ipv6.src (resolved at startup).</summary>
    private FieldId _Ipv6SrcFieldId;

    /// <summary>FieldId of ipv6.dst (resolved at startup).</summary>
    private FieldId _Ipv6DstFieldId;

    // Pre-allocated populator delegate: one instance for all packets, so Parse does not allocate a closure per packet.
    private LazyPopulator _Populator = null!;

    /// <summary>NDP field IDs struct populated in _OnStartCustom.</summary>
    private Icmpv6NdpFieldIds _NdpFieldIds;

    /// <summary>MLD field IDs struct populated in _OnStartCustom.</summary>
    private Icmpv6MldFieldIds _MldFieldIds;

    /// <summary>
    /// Resolves the cross-protocol field ids for the checksum lookup, creates the shared populator delegate and
    /// bundles the NDP and MLD field ids. Runs once, single-threaded, before any packet is parsed; the results are
    /// only read afterwards.
    /// </summary>
    partial void _OnStartCustom(Stack stack)
    {
        _IpContainerFieldId = stack.GetFieldId("ip") ?? FieldId.Invalid;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? FieldId.Invalid;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? FieldId.Invalid;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? FieldId.Invalid;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? FieldId.Invalid;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? FieldId.Invalid;
        _Populator = _PopulateIcmpv6Fields;

        _MldFieldIds = new Icmpv6MldFieldIds
        {
            MaxResponseDelay = _MldMaxResponseDelayFieldId,
            MulticastAddress = _MldMulticastAddressFieldId,
            MaxResponseCode = _MldMaxResponseCodeFieldId,
            Flags = _MldFlagFieldId,
            FlagReserved = _MldFlagReservedFieldId,
            FlagS = _MldFlagSFieldId,
            FlagQrv = _MldFlagQrvFieldId,
            Qqi = _MldQqiFieldId,
            NbSources = _MldNbSourcesFieldId,
            SourceAddress = _MldSourceAddressFieldId,
            ReportRecordCount = _MldrRecordCountFieldId,
            Record = _MldrRecordFieldId,
            RecordType = _MldrRecordTypeFieldId,
            RecordAuxDataLength = _MldrRecordAuxLenFieldId,
            RecordNbSources = _MldrRecordNbSourcesFieldId,
            RecordMulticastAddress = _MldrRecordMulticastAddressFieldId,
            RecordSourceAddress = _MldrRecordSourceAddressFieldId,
            RecordAuxData = _MldrRecordAuxDataFieldId,
        };

        _NdpFieldIds = new Icmpv6NdpFieldIds
        {
            RaCurHopLimit = _NdRaCurHopLimitFieldId,
            RaFlags = _NdRaFlagsFieldId,
            RaFlagM = _NdRaFlagMFieldId,
            RaFlagO = _NdRaFlagOFieldId,
            RaFlagH = _NdRaFlagHFieldId,
            RaFlagPrf = _NdRaFlagPrfFieldId,
            RaFlagP = _NdRaFlagPFieldId,
            RaFlagS = _NdRaFlagSFieldId,
            RaFlagRsv = _NdRaFlagRsvFieldId,
            RaRouterLifetime = _NdRaRouterLifetimeFieldId,
            RaReachableTime = _NdRaReachableTimeFieldId,
            RaRetransTimer = _NdRaRetransTimerFieldId,
            NsTargetAddress = _NdNsTargetAddressFieldId,
            NaTargetAddress = _NdNaTargetAddressFieldId,
            RdTargetAddress = _NdRdTargetAddressFieldId,
            RdDestinationAddress = _NdRdDestinationAddressFieldId,
            NaFlags = _NdNaFlagsFieldId,
            NaFlagR = _NdNaFlagRFieldId,
            NaFlagS = _NdNaFlagSFieldId,
            NaFlagO = _NdNaFlagOFieldId,
            NaFlagRsv = _NdNaFlagRsvFieldId,
            Opt = _NdOptFieldId,
            OptType = _NdOptTypeFieldId,
            OptLength = _NdOptLengthFieldId,
            OptLinkAddr = _NdOptLinkAddrFieldId,
            OptLinkAddrEui64 = _NdOptLinkAddrEui64FieldId,
            OptLinkAddrRaw = _NdOptLinkAddrRawFieldId,
            PrefixLength = _NdOptPrefixLengthFieldId,
            PrefixFlagL = _NdOptPrefixFlagLFieldId,
            PrefixFlagA = _NdOptPrefixFlagAFieldId,
            PrefixFlagR = _NdOptPrefixFlagRFieldId,
            PrefixFlagP = _NdOptPrefixFlagPFieldId,
            PrefixFlagReserved = _NdOptPrefixFlagReservedFieldId,
            PrefixValidLifetime = _NdOptPrefixValidLifetimeFieldId,
            PrefixPreferredLifetime = _NdOptPrefixPreferredLifetimeFieldId,
            Prefix = _NdOptPrefixFieldId,
            OptMtu = _NdOptMtuFieldId,
            OptRdnssLifetime = _NdOptRdnssLifetimeFieldId,
            OptRdnss = _NdOptRdnssAddressFieldId,
            OptRedirectedPacket = _NdOptRedirectedPacketFieldId,
            OptAdvertisementInterval = _NdOptAdvertisementIntervalFieldId,
            OptHomeAgentPreference = _NdOptHomeAgentPreferenceFieldId,
            OptHomeAgentLifetime = _NdOptHomeAgentLifetimeFieldId,
            OptNonce = _NdOptNonceFieldId,
            OptRouteInfoPrefixLength = _NdOptRouteInfoPrefixLengthFieldId,
            OptRouteInfoPreference = _NdOptRouteInfoPreferenceFieldId,
            OptRouteLifetime = _NdOptRouteLifetimeFieldId,
            OptRouteInfoPrefix = _NdOptRouteInfoPrefixFieldId,
            OptFlagsExtension = _NdOptFlagsExtensionFieldId,
            OptDnsslLifetime = _NdOptDnsslLifetimeFieldId,
            OptDnssl = _NdOptDnsslFieldId,
            OptCaptivePortal = _NdOptCaptivePortalFieldId,
            OptPref64ScaledLifetime = _NdOptPref64ScaledLifetimeFieldId,
            OptPref64Plc = _NdOptPref64PlcFieldId,
            OptPref64Prefix = _NdOptPref64PrefixFieldId,
            ErrorOptionLength = _NdErrorOptionLengthFieldId,
        };
    }

    #endregion

    #region Shared decisions (eager index and lazy fields)

    /// <summary>
    /// Tells whether a type is one of the four error messages that quote the datagram which caused them.
    /// </summary>
    /// <param name="type">The ICMPv6 type byte.</param>
    private static bool _IsErrorType(byte type) =>
        type is _TypeDestUnreach or _TypePacketTooBig or _TypeTimeExceeded or _TypeParamProblem;

    /// <summary>
    /// Tells whether the bytes quoted in an error message start with a complete IPv6 header (version 6, at least 40
    /// bytes). The single place for the decision: the eager index group and the lazy populator both call it.
    /// </summary>
    /// <param name="quoted">Bytes after the 8-byte ICMPv6 error header.</param>
    private static bool _HasQuotedIpv6Header(ReadOnlySpan<byte> quoted) =>
        quoted.Length >= _Ipv6HeaderSize && (quoted[0] >> 4) == 6;

    /// <summary>
    /// Tells whether the quoted datagram reaches the transport ports: a TCP or UDP next header directly after the
    /// fixed header (extension headers are not walked) and at least the four port bytes. Shared by Parse and the
    /// populator.
    /// </summary>
    /// <param name="quoted">Bytes after the 8-byte ICMPv6 error header; must satisfy <see cref="_HasQuotedIpv6Header"/>.</param>
    private static bool _HasQuotedPorts(ReadOnlySpan<byte> quoted) =>
        quoted[6] is _ProtocolTcp or _ProtocolUdp && quoted.Length >= _Ipv6HeaderSize + _QuotedPortsLength;

    /// <summary>
    /// Tells whether a message carries an undecoded payload after the first eight bytes that is shown as
    /// <c>icmpv6.data</c>. Echo data is shown (a ping payload carries timestamps and patterns); Neighbor Discovery and
    /// MLD bodies are decoded field by field and never have a data field; an error message shows its quote as the
    /// original-datagram container when it starts with an IPv6 header and as data otherwise, so no quoted byte is
    /// lost. The eager index group and the populator both call this.
    /// </summary>
    /// <param name="type">The ICMPv6 type byte.</param>
    /// <param name="message">The whole ICMPv6 message.</param>
    private static bool _HasDataField(byte type, ReadOnlySpan<byte> message)
    {
        if (message.Length <= _HeaderSize || Icmpv6NdpParser.IsNdpType(type) || Icmpv6MldParser.IsMldType(type))
        {
            return false;
        }

        return !_IsErrorType(type) || !_HasQuotedIpv6Header(message[_HeaderSize..]);
    }

    #endregion

    #region Lazy populator

    /// <summary>
    /// Populates ICMPv6 child fields lazily from stored datagram bytes.
    /// <para>
    /// Thread safety: reads only the immutable container bytes and start-time state, so it may run concurrently for
    /// different packets. The emission conditions are mirrored by the index groups recorded in <c>Parse</c> through
    /// the shared decision functions; keep both in step by changing the functions, not the call sites.
    /// </para>
    /// </summary>
    private ParseResult _PopulateIcmpv6Fields(in MutField container)
    {
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> icmpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (icmpData.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)icmpData.Length);
        }

        // ICMPv6 header (RFC 4443 section 2.1): type(1), code(1), checksum(2); all multi-byte values are big endian.
        ReadOnlySpan<byte> span = icmpData.Span;
        byte type = span[0];
        byte code = span[1];
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);

        // The texts come from precomputed tables and are wrapped in LazyString on purpose: a bare string argument would
        // bind to the generic AppendWithCustomText<T0> overload, which boxes the string into a lazy-format object per
        // field and copies it again on first read. The LazyString struct only holds the reference to the shared string.
        container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(type), new LazyString(DisplayTables.GetIcmpv6TypeDisplayText(type)));
        container.AppendWithCustomText(_CodeFieldId, FieldValue.NewU64(code), new LazyString(DisplayTables.GetIcmpv6CodeDisplayText(type, code)));
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(checksum), new LazyString(DisplayTables.FormatHexU16(checksum)));

        // Checksum validation (uses IPv6 pseudo-header, RFC 4443 section 2.3). The status field is always emitted when
        // verification is on, because Parse records its index group from the setting alone (whether an IPv6 layer
        // exists is not known there). When the enclosing IPv6 addresses cannot be determined, the status is
        // "[Unverified]" (the same text UDP uses) instead of a missing field that the index would still announce.
        if (_VerifyChecksum)
        {
            bool? valid = _ValidateChecksum(in container, icmpData.Span);
            string statusText = valid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        // Echo Request/Reply: identifier and sequence number
        if (type == _TypeEchoRequest || type == _TypeEchoReply)
        {
            ushort ident = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
            container.AppendWithCustomText(_EchoIdentFieldId, FieldValue.NewU64(ident), new LazyString(DisplayTables.FormatHexU16(ident)));
            container.Append(_EchoSeqFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(span[6..8])));
        }

        // Error messages: the word at offset 4 depends on the type (RFC 4443 sections 3.1 to 3.4).
        _AppendErrorWord(in container, type, icmpData);

        // Multicast Listener Discovery (types 130-132 and 143), see Icmpv6MldParser for the layouts and length rules.
        if (Icmpv6MldParser.IsMldType(type))
        {
            Icmpv6MldParser.Parse(in container, icmpData, type, in _MldFieldIds);
        }

        // NDP messages (types 133-137): parse after the 4-byte ICMPv6 header
        if (Icmpv6NdpParser.IsNdpType(type))
        {
            Icmpv6NdpParser.Parse(in container, icmpData[_NdpHeaderSize..], type, in _NdpFieldIds);

            // The hop limit check reads the enclosing IPv6 header; it is the same test Parse used for the index group.
            if (container.HasFieldTree
                && EnclosingIpHeader.TryFindPrevious(in container, _IpContainerFieldId, _Ipv6ContainerFieldId, out EnclosingIpHeader ip)
                && _HasInvalidNdpHopLimit(in ip))
            {
                container.Append(
                    _NdErrorHopLimitFieldId,
                    FieldValue.NewString(string.Create(CultureInfo.InvariantCulture, $"IPv6 hop limit must be 255 for this message (found {ip.HopLimit})")));
            }
        }

        // Quoted datagram of an error message, or the payload of other messages.
        if (_IsErrorType(type) && _HasQuotedIpv6Header(span[_HeaderSize..]))
        {
            _AppendQuotedIpv6Header(in container, span[_HeaderSize..]);
        }
        else if (_HasDataField(type, span))
        {
            container.Append(_DataFieldId, FieldValue.NewBytes(icmpData[_HeaderSize..]));
        }

        return 0;
    }

    /// <summary>
    /// Appends the fields of the error word at offset 4. Packet Too Big carries the MTU, Parameter Problem the byte
    /// pointer; Destination Unreachable and Time Exceeded carry the RFC 4884 length of the quoted datagram in byte 4
    /// (0 when the sender does not implement RFC 4884) and reserved bytes.
    /// </summary>
    private void _AppendErrorWord(in MutField container, byte type, ReadOnlyMemory<byte> icmpData)
    {
        ReadOnlySpan<byte> span = icmpData.Span;
        switch (type)
        {
            case _TypePacketTooBig:
                container.Append(_MtuFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(span[4..8])));
                break;
            case _TypeParamProblem:
                container.Append(_PointerFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt32BigEndian(span[4..8])));
                break;
            case _TypeDestUnreach:
            case _TypeTimeExceeded:
                if (span[4] != 0)
                {
                    // The byte counts 8-byte units; the text shows bytes, which is what a reader compares with the quote.
                    container.AppendWithCustomText(_OrigLengthFieldId, FieldValue.NewU64(span[4]), span[4] * 8, " bytes");
                    container.Append(_ReservedFieldId, FieldValue.NewBytes(icmpData[5.._HeaderSize]));
                }
                else
                {
                    container.Append(_ReservedFieldId, FieldValue.NewBytes(icmpData[4.._HeaderSize]));
                }

                break;
        }
    }

    /// <summary>
    /// Appends the quoted IPv6 header of an error message and, when the quote reaches them, the transport ports. The
    /// caller has checked <see cref="_HasQuotedIpv6Header"/>.
    /// </summary>
    /// <param name="container">ICMPv6 container that receives the original-datagram container.</param>
    /// <param name="quoted">Bytes after the 8-byte ICMPv6 error header.</param>
    private void _AppendQuotedIpv6Header(in MutField container, ReadOnlySpan<byte> quoted)
    {
        // IPv6 header offsets: next header at 6, hop limit at 7, source at 8..23, destination at 24..39. The values
        // come from the remote sender and are informational only; every read is inside the 40 bytes checked by the
        // caller.
        byte nextHeader = quoted[6];
        IPv6Address src = new(BinaryPrimitives.ReadUInt64BigEndian(quoted[8..16]), BinaryPrimitives.ReadUInt64BigEndian(quoted[16..24]));
        IPv6Address dst = new(BinaryPrimitives.ReadUInt64BigEndian(quoted[24..32]), BinaryPrimitives.ReadUInt64BigEndian(quoted[32..40]));

        MutField ipContainer = container.AppendWithCustomText(
            _OrigIpv6FieldId, FieldValue.None,
            "Internet Protocol Version 6, Src: ", src, ", Dst: ", dst);
        ipContainer.Append(_OrigIpv6SrcFieldId, FieldValue.NewIPv6(src));
        ipContainer.Append(_OrigIpv6DstFieldId, FieldValue.NewIPv6(dst));
        ipContainer.AppendWithCustomText(
            _OrigIpv6NxtFieldId, FieldValue.NewU64(nextHeader), new LazyString(DisplayTables.GetIpProtocolDisplayText(nextHeader)));
        ipContainer.Append(_OrigIpv6HlimFieldId, FieldValue.NewU64(quoted[7]));

        if (_HasQuotedPorts(quoted))
        {
            // The ports are the first two 16-bit words of the quoted TCP or UDP header, directly behind the 40 bytes.
            ReadOnlySpan<byte> ports = quoted.Slice(_Ipv6HeaderSize, _QuotedPortsLength);
            ipContainer.Append(_OrigIpv6SrcPortFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(ports[..2])));
            ipContainer.Append(_OrigIpv6DstPortFieldId, FieldValue.NewU64(BinaryPrimitives.ReadUInt16BigEndian(ports[2..4])));
        }
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses an ICMPv6 message from the supplied <paramref name="data"/> buffer. Eagerly it reads only what the
    /// index groups and the packet info text need; all other fields are created by the lazy populator when the
    /// container's children are first accessed. ICMPv6 has no payload protocol, so nothing is dispatched.
    /// <para>Thread safety: stateless apart from start-time settings; may be called concurrently.</para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _HeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _HeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_Icmpv6GroupId);

        ReadOnlySpan<byte> span = data.Span;
        byte type = span[0];

        // Record optional index groups. Each group is gated on the exact same condition under which the lazy
        // populator emits a field in that group (the conditions live in shared functions), so the presence index
        // never contains a false positive (a recorded group with no emitted field). The checksum status group below
        // follows the setting only, because whether an IPv6 layer is found is not known at this point; the populator
        // therefore emits the status field whenever the setting is on.
        bool isEcho = type == _TypeEchoRequest || type == _TypeEchoReply;
        if (isEcho)
        {
            // The populator reads the 8-byte echo header (identifier + sequence), which the length check above covers.
            context.RecordGroupPresence(_Icmpv6EchoGroupId);
        }

        _RecordErrorGroups(in context, type, span);
        _RecordMldGroups(in context, type, data.Length);
        _RecordNdpGroups(in context, in parentField, type, span);

        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_Icmpv6ChecksumStatusGroupId);
        }

        if (_HasDataField(type, span))
        {
            context.RecordGroupPresence(_Icmpv6DataGroupId);
        }

        _SetPacketInfo(in parentField, type, code: span[1], isEcho, span);

        // Store entire ICMPv6 message for lazy populator; the checksum (which covers the whole message) is validated
        // from it later. The representation names the stored length from a shared table (no string per packet for
        // messages up to 2 KiB), and the title depends on the type byte only, so it is also a shared string.
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(new LazyString(DisplayTables.GetByteCountDisplayText(data.Length)));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, new LazyString(DisplayTables.GetIcmpv6ContainerTitle(type)), _Populator);

        return data.Length;
    }

    /// <summary>Records the index groups of the error-message fields; mirrors <c>_AppendErrorWord</c> and the quote handling.</summary>
    private void _RecordErrorGroups(in ParseContext context, byte type, ReadOnlySpan<byte> message)
    {
        if (!_IsErrorType(type))
        {
            return;
        }

        switch (type)
        {
            case _TypePacketTooBig:
                context.RecordGroupPresence(_Icmpv6MtuGroupId);
                break;
            case _TypeParamProblem:
                context.RecordGroupPresence(_Icmpv6PointerGroupId);
                break;
            default:
                // Destination Unreachable and Time Exceeded: the reserved bytes are always emitted, the length only
                // when the sender sets the RFC 4884 length byte.
                context.RecordGroupPresence(_Icmpv6ReservedGroupId);
                if (message[4] != 0)
                {
                    context.RecordGroupPresence(_Icmpv6LengthGroupId);
                }

                break;
        }

        // The quote is shown as the original-datagram container when it starts with an IPv6 header.
        if (message.Length > _HeaderSize && _HasQuotedIpv6Header(message[_HeaderSize..]))
        {
            context.RecordGroupPresence(_Icmpv6Orig_ipv6GroupId);
            if (_HasQuotedPorts(message[_HeaderSize..]))
            {
                context.RecordGroupPresence(_Icmpv6Orig_ipv6PortsGroupId);
            }
        }
    }

    /// <summary>Records the Multicast Listener Discovery groups from the shared length rules of <see cref="Icmpv6MldParser"/>.</summary>
    private void _RecordMldGroups(in ParseContext context, byte type, int length)
    {
        MldGroups groups = Icmpv6MldParser.DetectGroups(type, length);
        if ((groups & MldGroups.Mld) != 0)
        {
            context.RecordGroupPresence(_Icmpv6MldGroupId);
        }
        if ((groups & MldGroups.QueryV2) != 0)
        {
            context.RecordGroupPresence(_Icmpv6MldV2GroupId);
        }
        if ((groups & MldGroups.Report) != 0)
        {
            context.RecordGroupPresence(_Icmpv6MldrGroupId);
        }
    }

    /// <summary>
    /// Records the Neighbor Discovery groups. The fixed-header groups and the option groups come from one call of
    /// <see cref="Icmpv6NdpParser.DetectGroups"/>, the same function whose rules the lazy parse follows.
    /// </summary>
    private void _RecordNdpGroups(in ParseContext context, in MutField parentField, byte type, ReadOnlySpan<byte> message)
    {
        if (!Icmpv6NdpParser.IsNdpType(type))
        {
            return;
        }

        // The hop limit is judged for every Neighbor Discovery message, also for one whose body is too short to decode: the
        // sender is wrong either way. The group is the one the option errors use. The hop limit comes from the per-thread
        // IPv6 cache, not from the field tree: the mode that builds no tree has no IPv6 container to read, and the index
        // must be the same in both modes. The cache describes the innermost IPv6 layer being parsed, which is the layer this
        // message sits in; the populator reads the same byte from that layer's container.
        // Residual risk, accepted: an ICMPv6 message directly behind an IPv4 header (invalid, but the protocol table is shared)
        // inside an IPv6 tunnel would see the outer IPv6 hop limit here, while the populator finds the IPv4 container and
        // emits nothing; only malformed input can make the index announce a group without a field.
        if (IPv6Protocol.TryGetCachedHopLimit(parentField.Packet.Id, out byte hopLimit)
            && hopLimit != _NdpRequiredHopLimit)
        {
            context.RecordGroupPresence(_Icmpv6NdErrorGroupId);
        }

        NdpGroups groups = Icmpv6NdpParser.DetectGroups(message[_NdpHeaderSize..], type);
        if (groups == NdpGroups.None)
        {
            return;
        }

        _RecordIf(in context, groups, NdpGroups.RouterAdvertisement, _Icmpv6NdRaGroupId);
        _RecordIf(in context, groups, NdpGroups.Target, _Icmpv6NdTargetGroupId);
        _RecordIf(in context, groups, NdpGroups.NeighborAdvertisement, _Icmpv6NdNaGroupId);
        _RecordIf(in context, groups, NdpGroups.Redirect, _Icmpv6NdRedirectGroupId);
        _RecordIf(in context, groups, NdpGroups.Option, _Icmpv6NdOptGroupId);
        _RecordIf(in context, groups, NdpGroups.Prefix, _Icmpv6NdOptPrefixGroupId);
        _RecordIf(in context, groups, NdpGroups.Mtu, _Icmpv6NdOptMtuGroupId);
        _RecordIf(in context, groups, NdpGroups.Rdnss, _Icmpv6NdOptRdnssGroupId);
        _RecordIf(in context, groups, NdpGroups.RedirectedHeader, _Icmpv6NdOptRedirectedGroupId);
        _RecordIf(in context, groups, NdpGroups.AdvertisementInterval, _Icmpv6NdOptAdv_intervalGroupId);
        _RecordIf(in context, groups, NdpGroups.HomeAgent, _Icmpv6NdOptHome_agentGroupId);
        _RecordIf(in context, groups, NdpGroups.Nonce, _Icmpv6NdOptNonceGroupId);
        _RecordIf(in context, groups, NdpGroups.RouteInfo, _Icmpv6NdOptRoute_infoGroupId);
        _RecordIf(in context, groups, NdpGroups.FlagsExtension, _Icmpv6NdOptFlags_extGroupId);
        _RecordIf(in context, groups, NdpGroups.Dnssl, _Icmpv6NdOptDnsslGroupId);
        _RecordIf(in context, groups, NdpGroups.CaptivePortal, _Icmpv6NdOptCaptive_portalGroupId);
        _RecordIf(in context, groups, NdpGroups.Pref64, _Icmpv6NdOptPref64GroupId);
        _RecordIf(in context, groups, NdpGroups.Error, _Icmpv6NdErrorGroupId);
    }

    /// <summary>
    /// Tells whether the enclosing header is IPv6 with a hop limit other than 255. The populator's test for the field; the
    /// index group applies the same comparison to the cached hop limit of the same header.
    /// </summary>
    private static bool _HasInvalidNdpHopLimit(in EnclosingIpHeader ip) =>
        ip.IsIPv6
        && ip.HopLimit != _NdpRequiredHopLimit;

    /// <summary>Records <paramref name="groupId"/> when <paramref name="flag"/> is set in <paramref name="groups"/>.</summary>
    private static void _RecordIf(in ParseContext context, NdpGroups groups, NdpGroups flag, IndexGroupId groupId)
    {
        if ((groups & flag) != 0)
        {
            context.RecordGroupPresence(groupId);
        }
    }

    /// <summary>
    /// Sets the packet-info (summary column) text. Echo shows the identifier and sequence number (how ping
    /// request and reply are matched by eye); an error message shows the code name (the diagnosis); Neighbor
    /// Solicitation and Advertisement show the target address and, for the advertisement, the flags; a link-layer
    /// address option adds "from" (source) or "is at" (target) and the address.
    /// </summary>
    /// <remarks>
    /// The text is built lazily from a small value state, so the eager parse pays one box for the messages that
    /// have details and nothing for the rest. The hop limit of the enclosing IPv6 header is not part of the text: the
    /// IPv6 parser stores it in its lazy fields, and materialising them here would defeat lazy parsing.
    /// </remarks>
    private static void _SetPacketInfo(in MutField parentField, byte type, byte code, bool isEcho, ReadOnlySpan<byte> message)
    {
        if (isEcho)
        {
            ushort ident = BinaryPrimitives.ReadUInt16BigEndian(message[4..6]);
            ushort seq = BinaryPrimitives.ReadUInt16BigEndian(message[6..8]);
            parentField.SetPacketInfo(LazyString.FormatLazy(
                (type, ident, seq),
                static s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayTables.GetIcmpv6TypeDisplayText(s.type)} id=0x{s.ident:x4}, seq={s.seq}")));
            return;
        }

        if (_IsErrorType(type))
        {
            parentField.SetPacketInfo(LazyString.FormatLazy(
                (type, code),
                static s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DisplayTables.GetIcmpv6TypeDisplayText(s.type)}: {DisplayTables.GetIcmpv6CodeDisplayText(s.type, s.code)}")));
            return;
        }

        if (Icmpv6NdpParser.IsNdpType(type) && message.Length > _NdpHeaderSize)
        {
            _SetNdpPacketInfo(in parentField, type, message[_NdpHeaderSize..]);
            return;
        }

        parentField.SetPacketInfo(new LazyString(DisplayTables.GetIcmpv6TypeDisplayText(type)));
    }

    /// <summary>State captured for the Neighbor Discovery info text; small value types only.</summary>
    private readonly record struct NdpInfoState(byte Type, bool HasTarget, IPv6Address Target, byte NaFlags, byte LinkOptionType, MacAddress LinkAddress);

    /// <summary>Builds the packet info of a Neighbor Discovery message from the target address and the first link-layer option.</summary>
    private static void _SetNdpPacketInfo(in MutField parentField, byte type, ReadOnlySpan<byte> body)
    {
        // Only a Neighbor Solicitation and a Neighbor Advertisement name their target in the info text; the Redirect
        // target is a router address and stays in the field tree.
        IPv6Address target = default;
        bool hasTarget = false;
        if (type is 135 or 136)
        {
            hasTarget = Icmpv6NdpParser.TryReadTarget(body, type, out target);
        }

        bool hasLink = Icmpv6NdpParser.TryReadFirstEthernetLinkAddress(body, type, out byte linkOptionType, out MacAddress linkAddress);

        // A message without any detail (a Router Solicitation without options, a Redirect) shows the type name only,
        // which is a shared table string and costs no box.
        if (!hasTarget && !hasLink)
        {
            parentField.SetPacketInfo(new LazyString(DisplayTables.GetIcmpv6TypeDisplayText(type)));
            return;
        }

        byte naFlags = 0;
        if (Icmpv6NdpParser.TryReadNeighborAdvertisementFlags(body, type, out bool router, out bool solicited, out bool overrideFlag))
        {
            naFlags = (byte)Icmpv6NdpFlagsFormatter.NaKey(router, solicited, overrideFlag);
        }

        byte shownLinkOptionType = 0;
        if (hasLink)
        {
            shownLinkOptionType = linkOptionType;
        }

        NdpInfoState state = new(type, hasTarget, target, naFlags, shownLinkOptionType, linkAddress);
        parentField.SetPacketInfo(LazyString.FormatLazy(state, static s => _FormatNdpInfo(s)));
    }

    /// <summary>Formats the Neighbor Discovery info text when it is read.</summary>
    private static string _FormatNdpInfo(NdpInfoState s)
    {
        string typeText = DisplayTables.GetIcmpv6TypeDisplayText(s.Type);
        string text;
        if (s.HasTarget && s.Type == 135)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"{typeText} for {s.Target}");
        }
        else if (s.HasTarget)
        {
            string flags = Icmpv6NdpFlagsFormatter.FormatNaInfo((s.NaFlags & 1) != 0, (s.NaFlags & 2) != 0, (s.NaFlags & 4) != 0);
            text = string.Create(CultureInfo.InvariantCulture, $"{typeText} {s.Target} ({flags})");
        }
        else
        {
            text = typeText;
        }

        // Option 1 names the sender's own address ("from"), option 2 the address the target can be reached at ("is at").
        return s.LinkOptionType switch
        {
            1 => string.Create(CultureInfo.InvariantCulture, $"{text} from {s.LinkAddress}"),
            2 => string.Create(CultureInfo.InvariantCulture, $"{text} is at {s.LinkAddress}"),
            _ => text,
        };
    }

    #endregion

    #region Checksum

    /// <summary>
    /// Validates the ICMPv6 checksum using the IPv6 cache first, then the innermost
    /// enclosing IPv6 layer (sibling walk, same contract as UDP/TCP). A flat
    /// <c>TryGetFieldValue</c> scan would pick the first <c>ipv6.src</c>/<c>ipv6.dst</c>
    /// in storage order — the outermost header in a tunnel — and compute the wrong checksum.
    /// Returns <see langword="null"/> when no IPv6 layer is found.
    /// </summary>
    /// <param name="container">The ICMPv6 container; provides the packet id and the field tree for the fallback.</param>
    /// <param name="icmpSpan">The whole ICMPv6 message including the transmitted checksum.</param>
    /// <returns>Whether the checksum is valid, or <see langword="null"/> when it cannot be determined.</returns>
    private bool? _ValidateChecksum(in MutField container, ReadOnlySpan<byte> icmpSpan)
    {
        // A message that the capture length cut cannot be verified: the missing bytes are part of the sum, so the honest
        // answer is "unknown", not "bad". The check comes first because it needs the field tree and the cache path below
        // does not look at it.
        if (container.HasFieldTree
            && EnclosingIpHeader.TryFindPrevious(in container, _IpContainerFieldId, _Ipv6ContainerFieldId, out EnclosingIpHeader enclosing)
            && enclosing.IsIPv6
            && enclosing.IsTruncated(icmpSpan.Length))
        {
            return null;
        }

        // Fast path: the per-thread cache is written by IPv6Protocol.Parse and cleared again in its finally block, so
        // the entry exists only while the IPv6 parse is still on the call stack. A lazy populator therefore always
        // misses it and takes the sibling walk below; the fast path serves eager materialization only. The cache
        // matches only while it still holds this packet's id. The upper-layer length of the pseudo header (RFC 8200
        // section 8.1) is the ICMPv6 message length; the sum of message plus pseudo header is 0 when valid.
        if (IPv6Protocol.TryGetCachedAddresses(container.Packet.Id, out IPv6Address src, out IPv6Address dst))
        {
            // The pseudo header carries a 32-bit upper-layer length (RFC 8200 section 8.1); a 16-bit cast would wrap for a
            // message of 64 KiB or more. A span length is never negative, so the conversion to uint is exact.
            uint icmpLen = (uint)icmpSpan.Length;
            ulong pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                src.High, src.Low, dst.High, dst.Low, _Icmpv6ProtocolNumber, icmpLen);
            return InternetChecksum.ComputeWithPseudoHeader(icmpSpan, pseudoSum) == 0;
        }

        // Fallback: without a field tree (no sibling to read) the pseudo header cannot be built, so the result is
        // "unknown" rather than a guess. Otherwise take the nearest preceding IPv6 layer; an IPv4 layer found first
        // (ICMPv6 is never valid over IPv4) leaves 'ipv6' empty and also yields "unknown".
        if (!container.HasFieldTree)
        {
            return null;
        }

        if (!IpAddressExtractor.TryFindPreviousIpAddresses(
                in container,
                _IpContainerFieldId,
                _Ipv6ContainerFieldId,
                _IpSrcFieldId,
                _IpDstFieldId,
                _Ipv6SrcFieldId,
                _Ipv6DstFieldId,
                out _,
                out (IPv6Address Src, IPv6Address Dst)? ipv6)
            || !ipv6.HasValue)
        {
            return null;
        }

        IPv6Address srcAddr = ipv6.Value.Src;
        IPv6Address dstAddr = ipv6.Value.Dst;
        uint len = (uint)icmpSpan.Length; // 32-bit upper-layer length, see the cache branch above
        ulong sum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
            srcAddr.High, srcAddr.Low, dstAddr.High, dstAddr.Low, _Icmpv6ProtocolNumber, len);
        return InternetChecksum.ComputeWithPseudoHeader(icmpSpan, sum) == 0;
    }

    #endregion
}
