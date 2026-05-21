// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Transmission Control Protocol (RFC 793) parser with lazy field population.
/// <para>Field tree structure:</para>
/// <code>
/// tcp: Transmission Control Protocol, Src Port: 443, Dst Port: 52341
/// ├── tcp.srcport: 443
/// ├── tcp.dstport: 52341
/// ├── tcp.port: 443                   [any-match, appended twice]
/// ├── tcp.port: 52341                 [any-match, appended twice]
/// ├── tcp.seq: 0x12345678
/// ├── tcp.ack: 0xabcdef01
/// ├── tcp.seq_raw: 0x12345678         [absolute sequence number]
/// ├── tcp.ack_raw: 0xabcdef01         [absolute ack number]
/// ├── tcp.hdr_len: 32
/// ├── tcp.flags: 0x18 [PSH, ACK]
/// │   ├── tcp.flags.cwr: false
/// │   ├── tcp.flags.ece: false
/// │   ├── tcp.flags.urg: false
/// │   ├── tcp.flags.ack: true
/// │   ├── tcp.flags.push: true
/// │   ├── tcp.flags.reset: false
/// │   ├── tcp.flags.syn: false
/// │   └── tcp.flags.fin: false
/// ├── tcp.window_size_value: 502
/// ├── tcp.checksum: 0xabcd
/// ├── tcp.checksum.status: [Good]      [optional]
/// ├── tcp.urgent_pointer: 0
/// ├── tcp.options: Options (12 bytes)  [optional, when header &gt; 20 bytes]
/// │   ├── tcp.options.nop              [NOP padding]
/// │   ├── tcp.options.mss              [MSS container]
/// │   │   └── tcp.options.mss_val: 1460
/// │   ├── tcp.options.wscale           [Window Scale container]
/// │   │   ├── tcp.options.wscale.shift: 7
/// │   │   └── tcp.options.wscale.multiplier: 128
/// │   ├── tcp.options.sack_perm        [SACK Permitted]
/// │   ├── tcp.options.sack             [SACK container]
/// │   │   ├── tcp.options.sack.count: 1
/// │   │   ├── tcp.options.sack_le: 12345
/// │   │   └── tcp.options.sack_re: 67890
/// │   ├── tcp.options.timestamp        [Timestamps container]
/// │   │   ├── tcp.options.timestamp.tsval: 123456
/// │   │   └── tcp.options.timestamp.tsecr: 789012
/// │   ├── tcp.options.tfo              [TCP Fast Open]
/// │   ├── tcp.options.mptcp            [Multipath TCP]
/// │   ├── tcp.options.md5              [MD5 Signature]
/// │   ├── tcp.options.ao               [TCP-AO]
/// │   ├── tcp.options.user_timeout     [User Timeout]
/// │   ├── tcp.options.unknown          [Unknown option]
/// │   └── tcp.options.eol              [End of Options]
/// ├── tcp.len: 1460                    [payload length]
/// └── tcp.payload: (1460 bytes)        [optional]
/// tcp.stream: 0                            [eager, always present]
/// tcp.time_relative: 0.023                 [eager, optional]
/// tcp.time_delta: 0.001                    [eager, optional]
/// tcp.window_size: 64256                   [eager, optional, scaled]
/// tcp.window_size_scalefactor: 7           [eager, optional]
/// tcp.analysis: SEQ/ACK analysis           [eager, optional]
/// ├── tcp.analysis.retransmission          [optional]
/// ├── tcp.analysis.fast_retransmission     [optional]
/// ├── tcp.analysis.spurious_retransmission [optional]
/// ├── tcp.analysis.out_of_order            [optional]
/// ├── tcp.analysis.duplicate_ack           [optional]
/// ├── tcp.analysis.duplicate_ack_num       [optional]
/// ├── tcp.analysis.lost_segment            [optional]
/// ├── tcp.analysis.keep_alive              [optional]
/// ├── tcp.analysis.zero_window             [optional]
/// ├── tcp.analysis.zero_window_probe       [optional]
/// ├── tcp.analysis.zero_window_probe_ack   [optional]
/// ├── tcp.analysis.window_update           [optional]
/// ├── tcp.analysis.window_full             [optional]
/// ├── tcp.analysis.bytes_in_flight: 1460   [optional]
/// ├── tcp.analysis.initial_rtt: 0.023      [optional]
/// ├── tcp.analysis.ack_rtt: 0.001          [optional]
/// └── tcp.analysis.connection_state: ESTABLISHED
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.
/// The TCP connection tracker accumulates mutable state across packets and must not be
/// accessed concurrently.</para>
/// </remarks>
[Protocol("tcp", "Transmission Control Protocol", Description = "TCP (RFC 793)")]
[RegisterAtTable(IPv4Protocol.IpProtoTableName, IpProtoKey)]
public sealed partial class TcpProtocol : IProtocol
{
    #region Constants

    /// <summary>IP protocol number for TCP (6).</summary>
    public const ulong IpProtoKey = 6;

    /// <summary>Dispatch table name for TCP port-based protocol lookup.</summary>
    public const string PortTableName = "tcp.port";

    /// <summary>Index group for always-present TCP fields.</summary>
    private const string TcpIndexGroup = "tcp";

    #endregion

    #region Fields (always present)

    [BytesField("tcp", "TCP", IndexGroup = TcpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("tcp.srcport", "Source Port", IndexGroup = TcpIndexGroup)]
    private FieldId _SrcPortFieldId;

    [U64Field("tcp.dstport", "Destination Port", IndexGroup = TcpIndexGroup)]
    private FieldId _DstPortFieldId;

    [U64Field("tcp.seq", "Sequence Number", IndexGroup = TcpIndexGroup)]
    private FieldId _SeqFieldId;

    [U64Field("tcp.ack", "Acknowledgment Number", IndexGroup = TcpIndexGroup)]
    private FieldId _AckFieldId;

    [U64Field("tcp.hdr_len", "Header Length", IndexGroup = TcpIndexGroup)]
    private FieldId _HdrLenFieldId;

    [U64Field("tcp.flags", "Flags", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsFieldId;

    // Flag sub-fields (always present, under tcp.flags container in populator)
    [BoolField("tcp.flags.cwr", "Congestion Window Reduced", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsCwrFieldId;

    [BoolField("tcp.flags.ece", "ECN-Echo", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsEceFieldId;

    [BoolField("tcp.flags.urg", "Urgent", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsUrgFieldId;

    [BoolField("tcp.flags.ack", "Acknowledgment", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsAckFieldId;

    [BoolField("tcp.flags.push", "Push", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsPushFieldId;

    [BoolField("tcp.flags.reset", "Reset", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsResetFieldId;

    [BoolField("tcp.flags.syn", "Syn", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsSynFieldId;

    [BoolField("tcp.flags.fin", "Fin", IndexGroup = TcpIndexGroup)]
    private FieldId _FlagsFinFieldId;

    [U64Field("tcp.window_size_value", "Window", IndexGroup = TcpIndexGroup)]
    private FieldId _WindowFieldId;

    [U64Field("tcp.checksum", "Checksum", IndexGroup = TcpIndexGroup)]
    private FieldId _ChecksumFieldId;

    [U64Field("tcp.urgent_pointer", "Urgent Pointer", IndexGroup = TcpIndexGroup)]
    private FieldId _UrgentPointerFieldId;

    [U64Field("tcp.len", "TCP Segment Len", IndexGroup = TcpIndexGroup)]
    private FieldId _LenFieldId;

    #endregion

    #region Combined port field (any-match, Wireshark-compatible)

    /// <summary>
    /// Combined port field appended twice per segment (once for src, once for dst)
    /// so that filter expressions like <c>tcp.port == 443</c> match either endpoint.
    /// </summary>
    [U64Field("tcp.port", "Port", IndexGroup = TcpIndexGroup)]
    private FieldId _PortFieldId;

    #endregion

    #region Raw sequence/ack numbers (absolute, before ISN subtraction)

    [U64Field("tcp.seq_raw", "Sequence Number (raw)", IndexGroup = "tcp.seqraw")]
    private FieldId _SeqRawFieldId;

    [U64Field("tcp.ack_raw", "Acknowledgment Number (raw)", IndexGroup = "tcp.ackraw")]
    private FieldId _AckRawFieldId;

    #endregion

    #region Scaled window size

    [U64Field("tcp.window_size", "Calculated window size", IndexGroup = "tcp.windowsize")]
    private FieldId _WindowSizeFieldId;

    [U64Field("tcp.window_size_scalefactor", "Window size scaling factor", IndexGroup = "tcp.windowsize")]
    private FieldId _WindowScaleFactorFieldId;

    #endregion

    #region Stream timing

    [F64Field("tcp.time_relative", "Time since first frame in this TCP stream", IndexGroup = "tcp.time")]
    private FieldId _TimeRelativeFieldId;

    [F64Field("tcp.time_delta", "Time since previous frame in this TCP stream", IndexGroup = "tcp.time")]
    private FieldId _TimeDeltaFieldId;

    #endregion

    #region TCP Options fields (optional, when header length > 20)

    private const string OptionsIndexGroup = "tcp.options";

    [NoneField("tcp.options", "Options", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptionsFieldId;

    [NoneField("tcp.options.eol", "End of Option List", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptEolFieldId;

    [NoneField("tcp.options.nop", "No-Operation", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptNopFieldId;

    #endregion

    #region MSS (kind 2)
    [NoneField("tcp.options.mss", "Maximum Segment Size", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMssFieldId;

    [U64Field("tcp.options.mss_val", "MSS Value", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMssValFieldId;

    #endregion

    #region Window Scale (kind 3)
    [NoneField("tcp.options.wscale", "Window Scale", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptWscaleFieldId;

    [U64Field("tcp.options.wscale.shift", "Shift count", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptWscaleShiftFieldId;

    [U64Field("tcp.options.wscale.multiplier", "Multiplier", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptWscaleMultiplierFieldId;

    #endregion

    #region SACK Permitted (kind 4)
    [NoneField("tcp.options.sack_perm", "SACK Permitted", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptSackPermFieldId;

    #endregion

    #region SACK (kind 5)
    [NoneField("tcp.options.sack", "SACK", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptSackFieldId;

    [U64Field("tcp.options.sack.count", "SACK Block Count", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptSackCountFieldId;

    [U64Field("tcp.options.sack_le", "SACK Left Edge", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptSackLeFieldId;

    [U64Field("tcp.options.sack_re", "SACK Right Edge", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptSackReFieldId;

    #endregion

    #region Timestamps (kind 8)
    [NoneField("tcp.options.timestamp", "Timestamps", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTimestampsFieldId;

    [U64Field("tcp.options.timestamp.tsval", "Timestamp Value", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTsValFieldId;

    [U64Field("tcp.options.timestamp.tsecr", "Timestamp Echo Reply", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTsEcrFieldId;

    #endregion

    #region User Timeout (kind 28)
    [NoneField("tcp.options.user_timeout", "User Timeout", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptUserTimeoutFieldId;

    [StringField("tcp.options.user_timeout.granularity", "Granularity", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptUserTimeoutGranularityFieldId;

    [U64Field("tcp.options.user_timeout.val", "User Timeout Value", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptUserTimeoutValFieldId;

    #endregion

    #region TCP Fast Open (kind 34)
    [NoneField("tcp.options.tfo", "TCP Fast Open", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTfoFieldId;

    [BoolField("tcp.options.tfo.request", "Cookie Request", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTfoRequestFieldId;

    [BytesField("tcp.options.tfo.cookie", "Cookie", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptTfoCookieFieldId;

    #endregion

    #region MPTCP (kind 30)
    [NoneField("tcp.options.mptcp", "Multipath TCP", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMptcpFieldId;

    [U64Field("tcp.options.mptcp.subtype", "Subtype", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMptcpSubtypeFieldId;

    #endregion

    #region MD5 Signature (kind 19)
    [NoneField("tcp.options.md5", "MD5 Signature", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMd5FieldId;

    [BytesField("tcp.options.md5.digest", "Digest", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptMd5DigestFieldId;

    #endregion

    #region TCP-AO (kind 29)
    [NoneField("tcp.options.ao", "TCP Authentication Option", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptAoFieldId;

    [U64Field("tcp.options.ao.keyid", "KeyID", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptAoKeyIdFieldId;

    [U64Field("tcp.options.ao.rnextkeyid", "RNextKeyID", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptAoRNextKeyIdFieldId;

    [BytesField("tcp.options.ao.mac", "MAC", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptAoMacFieldId;

    #endregion

    #region Unknown options
    [NoneField("tcp.options.unknown", "Unknown Option", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptUnknownFieldId;

    [BytesField("tcp.options.unknown.data", "Data", IndexGroup = OptionsIndexGroup)]
    private FieldId _OptUnknownDataFieldId;

    #endregion

    #region Optional fields

    [StringField("tcp.checksum.status", "Checksum Status", IndexGroup = "tcp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    // Pre-computed pseudo-header sum, eagerly appended in Parse() as a preceding sibling of
    // the TCP container when checksum verification is enabled. The lazy populator reads this
    // field to validate the checksum without needing the CallerProtocolId from the original
    // dispatch context (which is not available inside a lazy populator).
    [U64Field("tcp.pseudo_sum", "Pseudo-Header Sum", IndexGroup = "tcp.checksum.status")]
    private FieldId _PseudoHeaderSumFieldId;

    [BytesField("tcp.payload", "TCP Payload", IndexGroup = "tcp.payload")]
    private FieldId _PayloadFieldId;

    // Second lazy group container: stores TCP bytes for the details populator.
    // Groups seq/ack/flags/window/options/payload separately from identifying port/checksum fields.
    [BytesField("tcp.hdr", "Header Details", IndexGroup = TcpIndexGroup)]
    private FieldId _HdrDetailsFieldId;

    #endregion

    #region Error fields

    [StringField("tcp.error.no_ip", "No enclosing IP layer", IndexGroup = "tcp.error")]
    private FieldId _NoIpLayerFieldId;

    #endregion

    #region Stream index (always present, eagerly appended)

    [U64Field("tcp.stream", "Stream index", IndexGroup = TcpIndexGroup)]
    private FieldId _StreamFieldId;

    #endregion

    #region TCP Analysis fields (eagerly appended, optional)

    private const string AnalysisIndexGroup = "tcp.analysis";

    [NoneField("tcp.analysis", "SEQ/ACK analysis", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisFieldId;

    [BoolField("tcp.analysis.retransmission", "Retransmission", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisRetransmissionFieldId;

    [BoolField("tcp.analysis.fast_retransmission", "Fast Retransmission", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisFastRetransmissionFieldId;

    [BoolField("tcp.analysis.spurious_retransmission", "Spurious Retransmission", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisSpuriousRetransmissionFieldId;

    [BoolField("tcp.analysis.out_of_order", "Out-Of-Order", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisOutOfOrderFieldId;

    [BoolField("tcp.analysis.duplicate_ack", "Duplicate ACK", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisDuplicateAckFieldId;

    [U64Field("tcp.analysis.duplicate_ack_num", "Duplicate ACK #", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisDupAckNumFieldId;

    [BoolField("tcp.analysis.lost_segment", "Previous segment not captured", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisLostSegmentFieldId;

    [BoolField("tcp.analysis.keep_alive", "Keep-Alive", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisKeepAliveFieldId;

    [BoolField("tcp.analysis.zero_window", "Zero Window", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowFieldId;

    [BoolField("tcp.analysis.zero_window_probe", "Zero Window Probe", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowProbeFieldId;

    [BoolField("tcp.analysis.zero_window_probe_ack", "Zero Window Probe Ack", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowProbeAckFieldId;

    [BoolField("tcp.analysis.window_update", "Window update", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisWindowUpdateFieldId;

    [BoolField("tcp.analysis.window_full", "Window is full", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisWindowFullFieldId;

    [U64Field("tcp.analysis.bytes_in_flight", "Bytes in flight", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisBytesInFlightFieldId;

    [F64Field("tcp.analysis.initial_rtt", "iRTT", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisInitialRttFieldId;

    [F64Field("tcp.analysis.ack_rtt", "ACK RTT", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisAckRttFieldId;

    [StringField("tcp.analysis.connection_state", "Connection State", IndexGroup = AnalysisIndexGroup)]
    private FieldId _AnalysisConnectionStateFieldId;

    #endregion

    #region Dispatch

    [ProtocolTableU64(PortTableName, "TCP Port")]
    private ProtocolTableId _PortTableId;

    /// <summary>Heuristic dispatch table name for content-based protocol detection.</summary>
    public const string HeuristicTableName = "tcp.heuristic";

    /// <summary>
    /// Heuristic dispatch table for payload-based protocol detection.
    /// Used as fallback when port-based dispatch fails to match.
    /// Registered manually in <see cref="ProtocolRegistration"/> because the source generator
    /// does not support heuristic table attributes.
    /// </summary>
    private HeuristicProtocolTableId _HeuristicTableId;

    /// <summary>
    /// Sets the heuristic protocol table ID. Called by <see cref="ProtocolRegistration"/>
    /// after the table is registered with the stack builder.
    /// </summary>
    internal void SetHeuristicTableId(HeuristicProtocolTableId tableId) => _HeuristicTableId = tableId;

    #endregion

    #region Settings

    [BoolSetting("tcp.verify_checksum", "Verify Checksum", "tcp", Default = false)]
    private bool _VerifyChecksum;

    #endregion

    #region Cross-Protocol References
    // Resolved during OnStart via stack.GetFieldId() for reading IP addresses
    // from the field tree via sibling navigation (not flat-array scan).

    /// <summary>FieldId of the IPv4 container field ("ip"), used to identify the IP
    /// container when walking backwards through siblings.</summary>
    private FieldId _IpContainerFieldId;

    /// <summary>FieldId of the IPv6 container field ("ipv6"), used to identify the IPv6
    /// container when walking backwards through siblings.</summary>
    private FieldId _Ipv6ContainerFieldId;

    private FieldId _IpSrcFieldId;
    private FieldId _IpDstFieldId;
    private FieldId _Ipv6SrcFieldId;
    private FieldId _Ipv6DstFieldId;

    /// <summary>ProtocolId of the IPv4 protocol ("ip"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="Parse"/> is dispatched from an enclosing IPv4 layer.</summary>
    private ProtocolId _Ipv4ProtocolId;

    /// <summary>ProtocolId of the IPv6 protocol ("ipv6"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="Parse"/> is dispatched from an enclosing IPv6 layer.</summary>
    private ProtocolId _Ipv6ProtocolId;

    // Pre-allocated populator and dispatch cache
    private LazyPopulator _Populator = null!;
    private LazyPopulator _DetailsPopulator = null!;
    private (ulong Key, ParseDelegate Parse)[] _PortSparseCache = [];

    // TCP connection tracking for analysis
    private readonly TcpConnectionTracker _ConnectionTracker = new();

    // TCP stream reassembly engine (buffers segments and extracts PDUs)
    private TcpReassemblyEngine? _ReassemblyEngine;

    // Bundled field IDs for passing to the options parser
    private TcpOptionsFieldIds _OptionsFieldIds;

    partial void OnStartCustom(Stack stack)
    {
        _IpContainerFieldId = stack.GetFieldId("ip") ?? default;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? default;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? default;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? default;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? default;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? default;
        _Ipv4ProtocolId = stack.GetProtocolId("ip") ?? default;
        _Ipv6ProtocolId = stack.GetProtocolId("ipv6") ?? default;
        _Populator = (in MutField container) => PopulateTcpPrimary(in container);
        _DetailsPopulator = (in MutField container) => PopulateTcpDetails(in container);
        _PortSparseCache = stack.BuildU64SparseDelegateCache(_PortTableId);
        _ReassemblyEngine = new TcpReassemblyEngine(stack);

        // Bundle option field IDs for the parser
        _OptionsFieldIds = new TcpOptionsFieldIds
        {
            Eol = _OptEolFieldId,
            Nop = _OptNopFieldId,
            Mss = _OptMssFieldId,
            MssVal = _OptMssValFieldId,
            WindowScale = _OptWscaleFieldId,
            WindowScaleVal = _OptWscaleShiftFieldId,
            WindowScaleMultiplier = _OptWscaleMultiplierFieldId,
            SackPermitted = _OptSackPermFieldId,
            Sack = _OptSackFieldId,
            SackCount = _OptSackCountFieldId,
            SackLeftEdge = _OptSackLeFieldId,
            SackRightEdge = _OptSackReFieldId,
            Timestamps = _OptTimestampsFieldId,
            TimestampTsVal = _OptTsValFieldId,
            TimestampTsEcr = _OptTsEcrFieldId,
            UserTimeout = _OptUserTimeoutFieldId,
            UserTimeoutGranularity = _OptUserTimeoutGranularityFieldId,
            UserTimeoutVal = _OptUserTimeoutValFieldId,
            FastOpen = _OptTfoFieldId,
            FastOpenRequest = _OptTfoRequestFieldId,
            FastOpenCookie = _OptTfoCookieFieldId,
            Mptcp = _OptMptcpFieldId,
            MptcpSubtype = _OptMptcpSubtypeFieldId,
            Md5 = _OptMd5FieldId,
            Md5Digest = _OptMd5DigestFieldId,
            TcpAo = _OptAoFieldId,
            TcpAoKeyId = _OptAoKeyIdFieldId,
            TcpAoRNextKeyId = _OptAoRNextKeyIdFieldId,
            TcpAoMac = _OptAoMacFieldId,
            Unknown = _OptUnknownFieldId,
            UnknownData = _OptUnknownDataFieldId,
        };
    }

    /// <summary>
    /// Primary lazy group: port and checksum fields for the TCP segment.
    /// Registers <c>tcp.hdr</c> as a second lazy group for all remaining fields.
    /// Called on first access of the TCP container's children.
    /// </summary>
    private ParseResult PopulateTcpPrimary(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> tcpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (!TcpHeader.TryParse(tcpData.Span, out TcpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)tcpData.Length);
        }

        ushort srcPort = header.SrcPort.Value;
        ushort dstPort = header.DstPort.Value;

        // Port fields first — filter expressions like `tcp.srcport == X` find them
        // without walking past the other header fields.
        container.Append(_SrcPortFieldId, FieldValue.NewU64(srcPort), in context);
        container.Append(_DstPortFieldId, FieldValue.NewU64(dstPort), in context);
        container.Append(_PortFieldId, FieldValue.NewU64(srcPort), in context);
        container.Append(_PortFieldId, FieldValue.NewU64(dstPort), in context);

        string csumText = DisplayTables.FormatHexU16(header.Checksum.Value);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(header.Checksum.Value), csumText, in context);

        if (_VerifyChecksum && header.Checksum.Value != 0)
        {
            bool? valid = ValidateChecksum(in container, tcpData.Span, in context);
            string statusText = valid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText), in context);
        }

        // Register the details group for seq/ack/flags/window/options/payload.
        container.AppendLazyWithCustomText(
            _HdrDetailsFieldId,
            FieldValue.NewBytes(tcpData),
            new LazyString("Header Details"),
            _DetailsPopulator);

        return 0;
    }

    /// <summary>
    /// Details lazy group: sequence/ack numbers, header length, flags, window, urgent pointer,
    /// options, segment length, and payload.
    /// Fires only when <c>tcp.hdr</c> children are accessed (e.g., during full materialisation).
    /// </summary>
    private ParseResult PopulateTcpDetails(in MutField container)
    {
        ParseContext context = new ParseContext(container.Packet.Stack);
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> tcpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (!TcpHeader.TryParse(tcpData.Span, out TcpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)tcpData.Length);
        }

        int headerLen = header.HeaderLength;
        int payloadLen = Math.Max(0, tcpData.Length - headerLen);

        container.Append(_SeqFieldId, FieldValue.NewU64(header.SeqNumber.Value), in context);
        container.Append(_AckFieldId, FieldValue.NewU64(header.AckNumber.Value), in context);

        // Raw (absolute) sequence/ack numbers — always appended.
        container.Append(_SeqRawFieldId, FieldValue.NewU64(header.SeqNumber.Value), in context);
        container.Append(_AckRawFieldId, FieldValue.NewU64(header.AckNumber.Value), in context);

        container.Append(_HdrLenFieldId, FieldValue.NewU64((ulong)headerLen), in context);

        byte flags = header.Flags;
        string flagsText = TcpFlagsFormatter.Format(flags);
        container.AppendWithCustomText(_FlagsFieldId,
            FieldValue.NewU64(flags), ZA.Lazy(Helpers.DisplayTables.FormatHexU8(flags), " [", flagsText, "]"), in context);

        container.Append(_FlagsCwrFieldId, FieldValue.NewBool((flags & 0x80) != 0), in context);
        container.Append(_FlagsEceFieldId, FieldValue.NewBool((flags & 0x40) != 0), in context);
        container.Append(_FlagsUrgFieldId, FieldValue.NewBool((flags & 0x20) != 0), in context);
        container.Append(_FlagsAckFieldId, FieldValue.NewBool((flags & 0x10) != 0), in context);
        container.Append(_FlagsPushFieldId, FieldValue.NewBool((flags & 0x08) != 0), in context);
        container.Append(_FlagsResetFieldId, FieldValue.NewBool((flags & 0x04) != 0), in context);
        container.Append(_FlagsSynFieldId, FieldValue.NewBool((flags & 0x02) != 0), in context);
        container.Append(_FlagsFinFieldId, FieldValue.NewBool((flags & 0x01) != 0), in context);

        container.Append(_WindowFieldId, FieldValue.NewU64(header.WindowSize.Value), in context);
        container.Append(_UrgentPointerFieldId, FieldValue.NewU64(header.UrgentPointer.Value), in context);

        // TCP Options (between fixed 20-byte header and payload)
        if (headerLen > TcpHeader.MinSize)
        {
            int optionsLen = headerLen - TcpHeader.MinSize;
            ReadOnlySpan<byte> optionsData = tcpData.Span.Slice(TcpHeader.MinSize, optionsLen);

            MutField optionsContainer = container.AppendWithCustomText(
                _OptionsFieldId, FieldValue.None,
                (string)ZA.String("Options: (", optionsLen, " bytes)"), in context);

            TcpOptionsParser.Parse(optionsData, in optionsContainer, in _OptionsFieldIds, in context);
        }

        container.Append(_LenFieldId, FieldValue.NewU64((ulong)payloadLen), in context);

        if (payloadLen > 0)
        {
            container.Append(_PayloadFieldId, FieldValue.NewBytes(tcpData[headerLen..]), in context);
        }

        return 0;
    }

    /// <summary>
    /// Validates the TCP checksum using the pre-computed pseudo-header sum stored as a
    /// preceding sibling field by <see cref="AppendPseudoHeaderSumIfAvailable"/> (fast
    /// path), falling back to per-protocol thread-local caches and previous-sibling
    /// navigation for edge cases. Returns <see langword="true"/> if the checksum is valid,
    /// <see langword="false"/> if invalid, or <see langword="null"/> if no IP layer was found.
    /// </summary>
    private bool? ValidateChecksum(in MutField container, ReadOnlySpan<byte> tcpData, in ParseContext context)
    {
        const byte TcpProtocolNumber = 6;
        ushort tcpLength = (ushort)tcpData.Length;

        // Fast path: use pre-computed pseudo-header sum stored as a preceding sibling field.
        // This value was computed in Parse() while CallerProtocolId was still available,
        // so it always refers to the correct IP layer even in tunnel scenarios (6in4, 4in6).
        if (TryReadPseudoHeaderSum(container.AsField(), out ulong precomputedSum))
        {
            ushort result = InternetChecksum.ComputeWithPseudoHeader(tcpData, precomputedSum);
            return result == 0;
        }

        // Fallback: walk previous siblings to find typed IP addresses. This handles
        // edge cases where no IP layer was found during Parse() (e.g., custom stacks
        // or non-standard encapsulations where the caches had no entry).
        Field containerField = container.AsField();
        if (!IpAddressExtractor.TryFindPreviousIpAddresses(containerField,
            _IpContainerFieldId, _Ipv6ContainerFieldId,
            _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
            out (IPv4Address Src, IPv4Address Dst)? ipv4,
            out (IPv6Address Src, IPv6Address Dst)? ipv6))
        {
            return null; // No IP layer found — cannot validate
        }

        ulong pseudoSum;
        if (ipv4.HasValue)
        {
            pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                ipv4.Value.Src.RawValue, ipv4.Value.Dst.RawValue, TcpProtocolNumber, tcpLength);
        }
        else
        {
            IPv6Address s6 = ipv6!.Value.Src;
            IPv6Address d6 = ipv6!.Value.Dst;
            pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                s6.High, s6.Low, d6.High, d6.Low, TcpProtocolNumber, tcpLength);
        }

        ushort fallbackResult = InternetChecksum.ComputeWithPseudoHeader(tcpData, pseudoSum);
        return fallbackResult == 0;
    }

    /// <summary>
    /// Attempts to read the pre-computed pseudo-header sum from a previous sibling of
    /// <paramref name="containerField"/>. The pseudo-header sum field is eagerly appended
    /// by <see cref="AppendPseudoHeaderSumIfAvailable"/> just before the TCP container in
    /// <see cref="Parse"/>, so it is typically 1 sibling back.
    /// </summary>
    private bool TryReadPseudoHeaderSum(Field containerField, out ulong pseudoSum)
    {
        // Walk at most 3 siblings back — the pseudo-header sum is always
        // immediately before the container in the sibling list.
        Field field = containerField;
        int maxWalk = 3;
        while (maxWalk-- > 0 && field.TryGetPrev(out field))
        {
            if (field.FieldId == _PseudoHeaderSumFieldId
                && field.Value.Data.TryGetAsU64(out pseudoSum))
            {
                return true;
            }
        }

        pseudoSum = 0;
        return false;
    }

    /// <summary>
    /// Pre-computes the TCP checksum pseudo-header sum and stores it as an eager sibling
    /// field immediately before the TCP protocol container. Called from <see cref="Parse"/>
    /// when checksum verification is enabled, so that <see cref="ValidateChecksum"/> (which
    /// runs inside a lazy populator without dispatch context) can read the correct value.
    /// <para>Uses <paramref name="context"/>'s <c>CallerProtocolId</c> to select the correct
    /// thread-local address cache, correctly handling tunnel scenarios where both IPv4 and
    /// IPv6 caches may hold valid entries for the same <see cref="PacketId"/>.</para>
    /// </summary>
    private void AppendPseudoHeaderSumIfAvailable(
        in MutField parentField, ushort tcpLength, in ParseContext context)
    {
        const byte TcpProtoNumber = 6;
        PacketId packetId = parentField.Packet.Id;

        bool callerIsIpv4 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv4ProtocolId;
        bool callerIsIpv6 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv6ProtocolId;

        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            ulong sum = InternetChecksum.ComputeIPv4PseudoHeaderSum(src4.RawValue, dst4.RawValue, TcpProtoNumber, tcpLength);
            parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(sum), in context);
            return;
        }
        if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
        {
            ulong sum = InternetChecksum.ComputeIPv6PseudoHeaderSum(src6.High, src6.Low, dst6.High, dst6.Low, TcpProtoNumber, tcpLength);
            parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(sum), in context);
            return;
        }

        // Fallback: sibling walk. Naturally finds the closest (innermost) IP layer in
        // the sibling list, which is correct even in tunnel scenarios.
        Field root = parentField.AsField();
        if (root.TryGetLastChild(out Field prev)
            && IpAddressExtractor.TryFindPreviousIpAddresses(prev,
                _IpContainerFieldId, _Ipv6ContainerFieldId,
                _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
                out (IPv4Address Src, IPv4Address Dst)? ipv4,
                out (IPv6Address Src, IPv6Address Dst)? ipv6))
        {
            ulong sum;
            if (ipv4.HasValue)
            {
                sum = InternetChecksum.ComputeIPv4PseudoHeaderSum(ipv4.Value.Src.RawValue, ipv4.Value.Dst.RawValue, TcpProtoNumber, tcpLength);
            }
            else
            {
                IPv6Address fbSrc6 = ipv6!.Value.Src;
                IPv6Address fbDst6 = ipv6!.Value.Dst;
                sum = InternetChecksum.ComputeIPv6PseudoHeaderSum(fbSrc6.High, fbSrc6.Low, fbDst6.High, fbDst6.Low, TcpProtoNumber, tcpLength);
            }
            parentField.Append(_PseudoHeaderSumFieldId, FieldValue.NewU64(sum), in context);
        }
        // If no IP layer found at all, don't append — ValidateChecksum will fall back
        // to sibling walk and return null (cannot validate).
    }

    /// <summary>
    /// Parses a Tcp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding stack.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Parse context carrying stack, index, and dispatch information.</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < TcpHeader.MinSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TcpGroupId);

        ReadOnlySpan<byte> span = data.Span;
        if (!TcpHeader.TryParse(span, out TcpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)data.Length);
        }

        // Validate data offset (minimum 5 = 20 bytes header)
        if (header.DataOffset < 5)
        {
            return ParseError.InvalidData(ProtocolName, "Data offset too small");
        }

        int headerLen = header.HeaderLength;
        if (headerLen > data.Length)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, (ulong)headerLen, (ulong)data.Length);
        }

        ushort srcPort = header.SrcPort.Value;
        ushort dstPort = header.DstPort.Value;
        byte flags = header.Flags;
        int payloadLen = Math.Max(0, data.Length - headerLen);

        // Record optional index groups
        if (_VerifyChecksum && header.Checksum.Value != 0)
        {
            context.RecordGroupPresence(_TcpChecksumStatusGroupId);
            // Pre-compute pseudo-header sum here, while CallerProtocolId is still available.
            // The lazy populator (PopulateTcpPrimary) creates a fresh ParseContext that lacks
            // dispatch information, so ValidateChecksum cannot use CallerProtocolId directly.
            // Storing the pre-computed value as an eager sibling field before the TCP container
            // mirrors the approach used by UdpProtocol.
            AppendPseudoHeaderSumIfAvailable(in parentField, (ushort)data.Length, in context);
        }
        if (payloadLen > 0)
        {
            context.RecordGroupPresence(_TcpPayloadGroupId);
        }
        if (headerLen > TcpHeader.MinSize)
        {
            context.RecordGroupPresence(_TcpOptionsGroupId);  // IndexGroup = "tcp.options"
        }

        // Summary closure captures srcPort, dstPort, flags, payloadLen
        string flagsText = TcpFlagsFormatter.Format(flags);
        LazyString summary = payloadLen > 0
            ? ZA.Lazy("Transmission Control Protocol, Src Port: ", srcPort,
                      ", Dst Port: ", dstPort, ", Len: ", payloadLen,
                      " [", flagsText, "]")
            : ZA.Lazy("Transmission Control Protocol, Src Port: ", srcPort,
                      ", Dst Port: ", dstPort,
                      " [", flagsText, "]");

        parentField.SetPacketInfo(ZA.Lazy(srcPort, " → ", dstPort));

        // Store full TCP segment (header + payload) for lazy populator
        FieldValue containerValue = FieldValue.NewBytes(data)
            .WithCustomRepresentation(ZA.Lazy(headerLen, " bytes"));
        parentField.AppendLazyWithCustomText(_ProtocolFieldId, containerValue, summary, _Populator);

    #endregion

        #region TCP Analysis (stateful, runs on every segment)
        TcpAnalysisResult analysis = RunAnalysis(
            in parentField, srcPort, dstPort, header.SeqNumber.Value, header.AckNumber.Value,
            flags, header.WindowSize.Value, payloadLen, span, in context);

        // Eagerly append tcp.stream as sibling of the lazy TCP container
        parentField.Append(_StreamFieldId, FieldValue.NewU64(analysis.StreamIndex), in context);

        // Report error when no enclosing IP layer was found (no stream tracking possible)
        if (analysis.NoIpLayer)
        {
            parentField.Append(_NoIpLayerFieldId,
                FieldValue.NewString("No enclosing IPv4/IPv6 layer found for stream tracking"), in context);
            context.RecordGroupPresence(_TcpErrorGroupId);
        }

        // Eagerly append stream timing fields
        if (!double.IsNaN(analysis.TimeRelative))
        {
            parentField.Append(_TimeRelativeFieldId, FieldValue.NewF64(analysis.TimeRelative), in context);
            context.RecordGroupPresence(_TcpTimeGroupId);
        }
        if (!double.IsNaN(analysis.TimeDelta))
        {
            parentField.Append(_TimeDeltaFieldId, FieldValue.NewF64(analysis.TimeDelta), in context);
        }

        // Eagerly append scaled window size when window scale factor is known
        if (analysis.WindowScaleFactor >= 0)
        {
            parentField.Append(_WindowSizeFieldId, FieldValue.NewU64(analysis.ScaledWindowSize), in context);
            parentField.Append(_WindowScaleFactorFieldId, FieldValue.NewU64((ulong)analysis.WindowScaleFactor), in context);
            context.RecordGroupPresence(_TcpWindowsizeGroupId);
        }

        // Eagerly append analysis fields (if any flags were detected)
        if (analysis.HasAnyFlag || analysis.BytesInFlight > 0
            || !double.IsNaN(analysis.InitialRtt) || !double.IsNaN(analysis.AckRtt))
        {
            context.RecordGroupPresence(_TcpAnalysisGroupId);
            AppendAnalysisFields(in parentField, in analysis, in context);
        }

        // Dispatch by port on parentField, with reassembly and heuristic fallback
        if (payloadLen > 0)
        {
            ReadOnlyMemory<byte> payload = data[headerLen..];
            ushort lowPort = Math.Min(srcPort, dstPort);
            ushort highPort = Math.Max(srcPort, dstPort);

            // Identify the target protocol and check for reassembly support.
            // If the protocol has a StreamReassemblyConfig, buffer segments and
            // dispatch extracted PDUs instead of raw segment payloads.
            ProtocolId targetProtocol = TryIdentifyPortProtocol(lowPort, highPort, in context);

            if (targetProtocol.IsValid && _ReassemblyEngine is not null
                && analysis.ConnectionState is not null)
            {
                // Check if this protocol has a reassembly config
                TcpConnectionKey connKey = analysis.ConnectionKey;
                TcpStreamState? streamState = _ReassemblyEngine.GetOrCreateStream(
                    in connKey, targetProtocol,
                    analysis.SrcAddr, srcPort, out bool reassemblyForward);

                if (streamState is not null)
                {
                    // Track handshake observation for resync heuristic context
                    if (analysis.ConnectionState.Phase == TcpConnectionPhase.SynSent
                        || analysis.ConnectionState.Phase == TcpConnectionPhase.SynReceived)
                    {
                        streamState.HandshakeObserved = true;
                    }

                    // Buffer segment and extract PDUs
                    TcpReassemblyEngine.FeedSegment(streamState, reassemblyForward, payload);

                    // Dispatch all available complete PDUs
                    while (TcpReassemblyEngine.TryExtractPdu(streamState, reassemblyForward,
                        out ReadOnlyMemory<byte> pdu))
                    {
                        ParseResult pduResult = parentField.CallProtocol(
                            targetProtocol, pdu, in context);
                        if (pduResult.IsError)
                        {
                            return pduResult;
                        }
                    }

                    return data.Length;
                }
            }

            // No reassembly — dispatch raw payload directly
            ParseResult result = DispatchPort(in parentField, lowPort, payload, in context);
            if (result.IsError)
            {
                return result;
            }

            // If lowPort didn't match, try highPort
            if (result.Value == 0 && lowPort != highPort)
            {
                result = DispatchPort(in parentField, highPort, payload, in context);
                if (result.IsError)
                {
                    return result;
                }
            }

            // If no port-based match, try heuristic detection as fallback
            if (result.Value == 0 && _HeuristicTableId.IsValid)
            {
                result = TryHeuristicDispatch(in parentField, payload, in context, analysis.ConnectionState);
                if (result.IsError)
                {
                    return result;
                }
            }
        }

        return data.Length;
    }

    /// <summary>
    /// Runs TCP connection tracking and segment analysis.
    /// Creates/finds the connection state, determines direction, and invokes the analyzer.
    /// For SYN packets with options, extracts the Window Scale value and passes it
    /// to the analyzer so it can be stored for future window calculations.
    /// </summary>
    private TcpAnalysisResult RunAnalysis(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags, ushort window,
        int payloadLen,
        ReadOnlySpan<byte> tcpSegment, in ParseContext context)
    {
        Packet packet = parentField.Packet;
        Timestamp timestamp = packet.Timestamp;

        // Try to build a connection key from IP addresses via sibling navigation.
        // Uses sibling walk to find the immediately enclosing IP layer,
        // which correctly handles tunnel scenarios (IP-in-IP, GRE).
        if (!TryCreateConnectionKey(in parentField, srcPort, dstPort, out TcpConnectionKey key, out UInt128 srcAddr, in context))
        {
            // No IP layer found — return empty result with NoIpLayer flag
            // so the caller can append a diagnostic error field.
            return TcpAnalysisResult.Empty with
            {
                NoIpLayer = true
            };
        }

        TcpConnectionState conn = _ConnectionTracker.GetOrCreate(in key, out _);
        bool isForward = key.IsForward(srcAddr, srcPort);

        // Extract Window Scale from SYN/SYN-ACK options for connection state tracking.
        // This must be done eagerly (not lazily) because subsequent packets need the
        // scale factor to compute the correct scaled window size.
        byte? windowScale = null;
        bool isSyn = (flags & 0x02) != 0;
        if (isSyn && tcpSegment.Length > TcpHeader.MinSize)
        {
            int headerLen = (tcpSegment[12] >> 4) * 4; // data offset from raw header
            if (headerLen > TcpHeader.MinSize && headerLen <= tcpSegment.Length)
            {
                ReadOnlySpan<byte> optionsData = tcpSegment[TcpHeader.MinSize..headerLen];
                windowScale = ExtractWindowScale(optionsData);
            }
        }

        return TcpConnectionTracker.Analyze(
            conn, isForward, seqNum, ackNum, flags, window, payloadLen, timestamp, windowScale)
            with
        {
            ConnectionKey = key,
            SrcAddr = srcAddr
        };
    }

    /// <summary>
    /// Lightweight scan of TCP options to extract only the Window Scale value.
    /// Used during SYN/SYN-ACK processing before full lazy option parsing.
    /// </summary>
    private static byte? ExtractWindowScale(ReadOnlySpan<byte> optionsData)
    {
        const byte OptEol = 0;
        const byte OptNop = 1;
        const byte OptWindowScale = 3;
        const byte MaxWindowScale = 14;

        int offset = 0;
        while (offset < optionsData.Length)
        {
            byte kind = optionsData[offset];
            if (kind == OptEol)
            {
                break;
            }
            if (kind == OptNop)
            {
                offset++;
                continue;
            }
            if (offset + 1 >= optionsData.Length)
            {
                break;
            }
            byte optLen = optionsData[offset + 1];
            if (optLen < 2 || offset + optLen > optionsData.Length)
            {
                break;
            }
            if (kind == OptWindowScale && optLen >= 3)
            {
                return Math.Min(optionsData[offset + 2], MaxWindowScale);
            }
            offset += optLen;
        }
        return null;
    }

    /// <summary>
    /// Builds a <see cref="TcpConnectionKey"/> from IP addresses. Reads from the
    /// per-protocol thread-local caches first (fast path), falling back to
    /// previous-sibling tree navigation for edge cases.
    /// </summary>
    private bool TryCreateConnectionKey(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        out TcpConnectionKey key,
        out UInt128 srcAddr, in ParseContext context)
    {
        PacketId packetId = parentField.Packet.Id;

        // CallerProtocolId selects which cache to check first. In tunnel scenarios
        // (e.g., IPv6-over-IPv4 / 6in4), both caches may hold valid entries for the same
        // PacketId — the outer IPv4 layer and inner IPv6 layer each cache their own addresses.
        // Without the guard, checking IPv4 first returns the outer tunnel endpoints,
        // producing a wrong connection key and corrupting the stream index.
        bool callerIsIpv4 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv4ProtocolId;
        bool callerIsIpv6 = context.Dispatch.HasDispatch && context.Dispatch.CallerProtocolId == _Ipv6ProtocolId;

        // Fast path: read from per-protocol thread-local caches
        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            key = TcpConnectionKey.FromIPv4(src4.RawValue, dst4.RawValue, srcPort, dstPort);
            srcAddr = new UInt128(0, 0x0000_FFFF_0000_0000UL | src4.RawValue);
            return true;
        }
        if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address cachedSrc6, out IPv6Address cachedDst6))
        {
            srcAddr = new UInt128(cachedSrc6.High, cachedSrc6.Low);
            key = new TcpConnectionKey(srcAddr, new UInt128(cachedDst6.High, cachedDst6.Low), srcPort, dstPort);
            return true;
        }

        // Fallback: walk previous siblings to find typed IP addresses
        Field root = parentField.AsField();
        if (!root.TryGetLastChild(out Field prev))
        {
            key = default;
            srcAddr = default;
            return false;
        }

        if (!IpAddressExtractor.TryFindPreviousIpAddresses(prev,
            _IpContainerFieldId, _Ipv6ContainerFieldId,
            _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
            out (IPv4Address Src, IPv4Address Dst)? ipv4,
            out (IPv6Address Src, IPv6Address Dst)? ipv6))
        {
            key = default;
            srcAddr = default;
            return false;
        }

        if (ipv4.HasValue)
        {
            uint srcIp = ipv4.Value.Src.RawValue;
            uint dstIp = ipv4.Value.Dst.RawValue;
            key = TcpConnectionKey.FromIPv4(srcIp, dstIp, srcPort, dstPort);
            srcAddr = new UInt128(0, 0x0000_FFFF_0000_0000UL | srcIp);
            return true;
        }

        // ipv6 is guaranteed non-null when TryFindPreviousIpAddresses returns true
        IPv6Address src6 = ipv6!.Value.Src;
        IPv6Address dst6 = ipv6!.Value.Dst;
        srcAddr = new UInt128(src6.High, src6.Low);
        UInt128 dstAddr128 = new(dst6.High, dst6.Low);
        key = new TcpConnectionKey(srcAddr, dstAddr128, srcPort, dstPort);
        return true;
    }

        #endregion

    /// <summary>
    /// Eagerly appends tcp.analysis container and its sub-fields to the parent field.
    /// Only called when there is analysis data worth displaying.
    /// </summary>
    private void AppendAnalysisFields(in MutField parentField, in TcpAnalysisResult analysis, in ParseContext context)
    {
        MutField analysisContainer = parentField.Append(_AnalysisFieldId, FieldValue.None, in context);

        TcpAnalysisFlags flags = analysis.Flags;

        if ((flags & TcpAnalysisFlags.Retransmission) != 0)
        {
            analysisContainer.Append(_AnalysisRetransmissionFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.FastRetransmission) != 0)
        {
            analysisContainer.Append(_AnalysisFastRetransmissionFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.SpuriousRetransmission) != 0)
        {
            analysisContainer.Append(_AnalysisSpuriousRetransmissionFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.OutOfOrder) != 0)
        {
            analysisContainer.Append(_AnalysisOutOfOrderFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.DuplicateAck) != 0)
        {
            analysisContainer.Append(_AnalysisDuplicateAckFieldId, FieldValue.NewBool(true), in context);
            analysisContainer.Append(_AnalysisDupAckNumFieldId, FieldValue.NewU64(analysis.DupAckNum), in context);
        }
        if ((flags & TcpAnalysisFlags.LostSegment) != 0)
        {
            analysisContainer.Append(_AnalysisLostSegmentFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.KeepAlive) != 0)
        {
            analysisContainer.Append(_AnalysisKeepAliveFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.ZeroWindow) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbe) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowProbeFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbeAck) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowProbeAckFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.WindowUpdate) != 0)
        {
            analysisContainer.Append(_AnalysisWindowUpdateFieldId, FieldValue.NewBool(true), in context);
        }
        if ((flags & TcpAnalysisFlags.WindowFull) != 0)
        {
            analysisContainer.Append(_AnalysisWindowFullFieldId, FieldValue.NewBool(true), in context);
        }

        if (analysis.BytesInFlight > 0)
        {
            analysisContainer.Append(_AnalysisBytesInFlightFieldId, FieldValue.NewU64(analysis.BytesInFlight), in context);
        }
        if (!double.IsNaN(analysis.InitialRtt))
        {
            analysisContainer.Append(_AnalysisInitialRttFieldId, FieldValue.NewF64(analysis.InitialRtt), in context);
        }
        if (!double.IsNaN(analysis.AckRtt))
        {
            analysisContainer.Append(_AnalysisAckRttFieldId, FieldValue.NewF64(analysis.AckRtt), in context);
        }

        // Connection state (always present when analysis container is shown)
        string phaseText = TcpConnectionTracker.GetPhaseDisplayText(analysis.Phase);
        analysisContainer.Append(_AnalysisConnectionStateFieldId, FieldValue.NewString(phaseText), in context);
    }

    /// <summary>
    /// Dispatches to the next protocol by TCP port using the sparse cache.
    /// Falls back to full table dispatch for uncached ports.
    /// </summary>
    private ParseResult DispatchPort(
        in MutField parentField, ulong port, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        foreach ((ulong key, ParseDelegate parse) in _PortSparseCache)
        {
            if (key == port)
            {
                return parse(in parentField, payload, in context);
            }
        }

        return parentField.TryCallNextProtocolU64(_PortTableId, port, payload, in context);
    }

    /// <summary>
    /// Attempts heuristic-based protocol dispatch with per-connection caching.
    /// On the first data packet of a connection, runs all registered heuristic parsers.
    /// If a match is found, caches the protocol ID on the connection state so subsequent
    /// packets bypass the heuristic tests.
    /// </summary>
    private ParseResult TryHeuristicDispatch(
        in MutField parentField, ReadOnlyMemory<byte> payload, in ParseContext context,
        TcpConnectionState? connectionState)
    {
        // Level 1: Check per-connection cache for a previously detected protocol
        if (connectionState?.HeuristicProtocolId is { } cachedId)
        {
            return parentField.CallProtocol(cachedId, payload, in context);
        }

        // Level 2: Run the heuristic protocol table to detect the protocol
        HeuristicProtocolTable? table = context.Stack?.GetHeuristicProtocolTable(_HeuristicTableId);
        if (table is null)
        {
            return 0;
        }

        ProtocolId? matchedId = table.TryMatch(payload);
        if (matchedId is null)
        {
            return 0;
        }

        // Cache the detected protocol on the connection for future packets
        if (connectionState is not null)
        {
            connectionState.HeuristicProtocolId = matchedId.Value;
        }

        return parentField.CallProtocol(matchedId.Value, payload, in context);
    }

    /// <summary>
    /// Identifies which protocol is registered for the given port pair without dispatching.
    /// Checks low port first, then high port. Returns invalid <see cref="ProtocolId"/>
    /// if no match is found.
    /// </summary>
    private ProtocolId TryIdentifyPortProtocol(ushort lowPort, ushort highPort, in ParseContext context)
    {
        ProtocolTable? table = context.Stack?.GetProtocolTable(_PortTableId);
        if (table is null)
        {
            return default;
        }

        ReadOnlySpan<ProtocolId> protocols = table.GetAllU64(lowPort);
        if (!protocols.IsEmpty)
        {
            return protocols[0];
        }

        if (lowPort != highPort)
        {
            protocols = table.GetAllU64(highPort);
            if (!protocols.IsEmpty)
            {
                return protocols[0];
            }
        }

        return default;
    }
}
