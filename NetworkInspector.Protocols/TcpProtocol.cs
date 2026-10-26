// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Transmission Control Protocol (RFC 793) parser with lazy field population.
/// <para>Field tree structure:</para>
/// <code>
/// tcp: Transmission Control Protocol, Src Port: 443, Dst Port: 52341
/// ├── tcp.srcport: 443
/// ├── tcp.dstport: 52341
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
/// <para><b>Thread safety:</b> The first parse of a packet id mutates the TCP connection tracker
/// and the stream reassembler, so it must stay ordered and single-threaded (the session guarantees
/// this under its parse lock). Every later parse of an already-parsed id is a replay: it only reads
/// the protocol-owned effect store and is safe to run on any number of threads concurrently, even
/// while further packets are being parsed for the first time. The watermark
/// <see cref="_IngestWatermark"/> separates both paths without any caller-supplied mode flag.</para>
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
    private const string _TcpIndexGroup = "tcp";

    /// <summary>
    /// Error text for a segment that completes more reassembled PDUs than the packet can bind. Limit: 255 additional buffers per
    /// packet. Threat: a hostile segment packed with tiny PDUs. Constant (the effect-layer keys of the packet use 8 bits for the
    /// buffer index, a format fact, not configurable); secure by default. Exceeding it is an expected failure that is named, not
    /// a silent drop.
    /// </summary>
    private const string _BoundBufferLimitMessage =
        "Too many reassembled PDUs in one packet: a packet can hold at most 255 additional buffers; the remaining PDUs were not parsed";

    #endregion

    #region Fields (always present)

    [BytesField("tcp", "TCP", IndexGroup = _TcpIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("tcp.srcport", "Source Port", IndexGroup = _TcpIndexGroup)]
    private FieldId _SrcPortFieldId;

    [U64Field("tcp.dstport", "Destination Port", IndexGroup = _TcpIndexGroup)]
    private FieldId _DstPortFieldId;

    [U64Field("tcp.seq", "Sequence Number", IndexGroup = _TcpIndexGroup)]
    private FieldId _SeqFieldId;

    [U64Field("tcp.ack", "Acknowledgment Number", IndexGroup = _TcpIndexGroup)]
    private FieldId _AckFieldId;

    [U64Field("tcp.hdr_len", "Header Length", IndexGroup = _TcpIndexGroup)]
    private FieldId _HdrLenFieldId;

    [U64Field("tcp.flags", "Flags", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsFieldId;

    // Flag sub-fields (always present, under tcp.flags container in populator)
    [BoolField("tcp.flags.cwr", "Congestion Window Reduced", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsCwrFieldId;

    [BoolField("tcp.flags.ece", "ECN-Echo", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsEceFieldId;

    [BoolField("tcp.flags.urg", "Urgent", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsUrgFieldId;

    [BoolField("tcp.flags.ack", "Acknowledgment", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsAckFieldId;

    [BoolField("tcp.flags.push", "Push", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsPushFieldId;

    [BoolField("tcp.flags.reset", "Reset", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsResetFieldId;

    [BoolField("tcp.flags.syn", "Syn", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsSynFieldId;

    [BoolField("tcp.flags.fin", "Fin", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsFinFieldId;

    [BoolField("tcp.flags.ns", "Nonce", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsNsFieldId;

    [U64Field("tcp.flags.res", "Reserved", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsResFieldId;

    [U64Field("tcp.window_size_value", "Window", IndexGroup = _TcpIndexGroup)]
    private FieldId _WindowFieldId;

    [U64Field("tcp.checksum", "Checksum", IndexGroup = _TcpIndexGroup)]
    private FieldId _ChecksumFieldId;

    [U64Field("tcp.urgent_pointer", "Urgent Pointer", IndexGroup = _TcpIndexGroup)]
    private FieldId _UrgentPointerFieldId;

    [U64Field("tcp.len", "TCP Segment Len", IndexGroup = _TcpIndexGroup)]
    private FieldId _LenFieldId;

    #endregion

    #region Combined port field (alias group, Wireshark-compatible)

    // Field alias group ID assigned in _RegisterFieldsCustom for "tcp.port" -> { tcp.srcport, tcp.dstport }.
    // Independent of the protocol table also named "tcp.port" (PortTableName) — alias / field /
    // table namespaces do not collide. Alias is metadata-only; GetFieldId("tcp.port") never
    // resolves and no tcp.port node is appended to the parse tree.
    private FieldAliasGroupId _PortAliasGroupId;

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

    private const string _OptionsIndexGroup = "tcp.options";

    [NoneField("tcp.options", "Options", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptionsFieldId;

    [NoneField("tcp.options.eol", "End of Option List", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEolFieldId;

    [NoneField("tcp.options.nop", "No-Operation", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptNopFieldId;

    #endregion

    #region MSS (kind 2)
    [NoneField("tcp.options.mss", "Maximum Segment Size", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMssFieldId;

    [U64Field("tcp.options.mss_val", "MSS Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMssValFieldId;

    #endregion

    #region Window Scale (kind 3)
    [NoneField("tcp.options.wscale", "Window Scale", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptWscaleFieldId;

    [U64Field("tcp.options.wscale.shift", "Shift count", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptWscaleShiftFieldId;

    [U64Field("tcp.options.wscale.multiplier", "Multiplier", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptWscaleMultiplierFieldId;

    #endregion

    #region SACK Permitted (kind 4)
    [NoneField("tcp.options.sack_perm", "SACK Permitted", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptSackPermFieldId;

    #endregion

    #region SACK (kind 5)
    [NoneField("tcp.options.sack", "SACK", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptSackFieldId;

    [U64Field("tcp.options.sack.count", "SACK Block Count", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptSackCountFieldId;

    [U64Field("tcp.options.sack_le", "SACK Left Edge", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptSackLeFieldId;

    [U64Field("tcp.options.sack_re", "SACK Right Edge", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptSackReFieldId;

    #endregion

    #region Timestamps (kind 8)
    [NoneField("tcp.options.timestamp", "Timestamps", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTimestampsFieldId;

    [U64Field("tcp.options.timestamp.tsval", "Timestamp Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTsValFieldId;

    [U64Field("tcp.options.timestamp.tsecr", "Timestamp Echo Reply", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTsEcrFieldId;

    #endregion

    #region User Timeout (kind 28)
    [NoneField("tcp.options.user_timeout", "User Timeout", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUserTimeoutFieldId;

    [StringField("tcp.options.user_timeout.granularity", "Granularity", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUserTimeoutGranularityFieldId;

    [U64Field("tcp.options.user_timeout.val", "User Timeout Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUserTimeoutValFieldId;

    #endregion

    #region TCP Fast Open (kind 34)
    [NoneField("tcp.options.tfo", "TCP Fast Open", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTfoFieldId;

    [BoolField("tcp.options.tfo.request", "Cookie Request", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTfoRequestFieldId;

    [BytesField("tcp.options.tfo.cookie", "Cookie", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptTfoCookieFieldId;

    #endregion

    #region MPTCP (kind 30)
    [NoneField("tcp.options.mptcp", "Multipath TCP", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMptcpFieldId;

    [U64Field("tcp.options.mptcp.subtype", "Subtype", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMptcpSubtypeFieldId;

    #endregion

    #region MD5 Signature (kind 19)
    [NoneField("tcp.options.md5", "MD5 Signature", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMd5FieldId;

    [BytesField("tcp.options.md5.digest", "Digest", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptMd5DigestFieldId;

    #endregion

    #region TCP-AO (kind 29)
    [NoneField("tcp.options.ao", "TCP Authentication Option", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAoFieldId;

    [U64Field("tcp.options.ao.keyid", "KeyID", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAoKeyIdFieldId;

    [U64Field("tcp.options.ao.rnextkeyid", "RNextKeyID", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAoRNextKeyIdFieldId;

    [BytesField("tcp.options.ao.mac", "MAC", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAoMacFieldId;

    #endregion

    #region Echo, CC, SCPS, Quick-Start, AccECN, experimental

    [NoneField("tcp.options.echo", "Echo", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoFieldId;

    [U64Field("tcp.options.echo.value", "Echo Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoValueFieldId;

    [NoneField("tcp.options.echo_reply", "Echo Reply", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoReplyFieldId;

    [U64Field("tcp.options.echo_reply.value", "Echo Reply Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoReplyValueFieldId;

    [NoneField("tcp.options.cc", "CC", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcFieldId;

    [U64Field("tcp.options.cc.value", "CC Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcValueFieldId;

    [NoneField("tcp.options.cc_new", "CC.NEW", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcNewFieldId;

    [U64Field("tcp.options.cc_new.value", "CC.NEW Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcNewValueFieldId;

    [NoneField("tcp.options.cc_echo", "CC.ECHO", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcEchoFieldId;

    [U64Field("tcp.options.cc_echo.value", "CC.ECHO Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcEchoValueFieldId;

    [NoneField("tcp.options.scps", "SCPS Capabilities", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsFieldId;

    [BoolField("tcp.options.scps.bets", "BETS", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsBetsFieldId;

    [BoolField("tcp.options.scps.snack1", "SNACK1", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsSnack1FieldId;

    [BoolField("tcp.options.scps.snack2", "SNACK2", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsSnack2FieldId;

    [BoolField("tcp.options.scps.compress", "COMP", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsCompressFieldId;

    [BoolField("tcp.options.scps.nlts", "NLTS", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsNltsFieldId;

    [NoneField("tcp.options.qs", "Quick-Start Response", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptQsFieldId;

    [U64Field("tcp.options.qs.rate", "Rate", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptQsRateFieldId;

    [U64Field("tcp.options.qs.ttl_diff", "TTL Diff", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptQsTtlDiffFieldId;

    [NoneField("tcp.options.acc_ecn", "Accurate ECN", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAccEcnFieldId;

    [U64Field("tcp.options.acc_ecn.ee0b", "EE0B", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAccEcnEe0bFieldId;

    [U64Field("tcp.options.acc_ecn.eceb", "ECEB", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAccEcnEcebFieldId;

    [U64Field("tcp.options.acc_ecn.ee1b", "EE1B", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptAccEcnEe1bFieldId;

    [NoneField("tcp.options.exp", "Experimental", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpFieldId;

    [U64Field("tcp.options.exp.exid", "ExID", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpExIdFieldId;

    [BytesField("tcp.options.exp.data", "Data", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpDataFieldId;

    #endregion

    #region Unknown options
    [NoneField("tcp.options.unknown", "Unknown Option", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUnknownFieldId;

    [BytesField("tcp.options.unknown.data", "Data", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUnknownDataFieldId;

    #endregion

    #region Optional fields

    [StringField("tcp.checksum.status", "Checksum Status", IndexGroup = "tcp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    [BytesField("tcp.payload", "TCP Payload", IndexGroup = "tcp.payload")]
    private FieldId _PayloadFieldId;

    #endregion

    #region Error fields

    [StringField("tcp.error.no_ip", "No enclosing IP layer", IndexGroup = "tcp.error")]
    private FieldId _NoIpLayerFieldId;

    [StringField("tcp.error.stream_limit", "Stream table full", IndexGroup = "tcp.error")]
    private FieldId _StreamLimitFieldId;

    #endregion

    #region Stream index (always present, eagerly appended)

    [U64Field("tcp.stream", "Stream index", IndexGroup = _TcpIndexGroup)]
    private FieldId _StreamFieldId;

    #endregion

    #region TCP Analysis fields (eagerly appended, optional)

    private const string _AnalysisIndexGroup = "tcp.analysis";

    [NoneField("tcp.analysis", "SEQ/ACK analysis", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisFieldId;

    [BoolField("tcp.analysis.retransmission", "Retransmission", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisRetransmissionFieldId;

    [BoolField("tcp.analysis.fast_retransmission", "Fast Retransmission", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisFastRetransmissionFieldId;

    [BoolField("tcp.analysis.spurious_retransmission", "Spurious Retransmission", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisSpuriousRetransmissionFieldId;

    [BoolField("tcp.analysis.out_of_order", "Out-Of-Order", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisOutOfOrderFieldId;

    [BoolField("tcp.analysis.duplicate_ack", "Duplicate ACK", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisDuplicateAckFieldId;

    [U64Field("tcp.analysis.duplicate_ack_num", "Duplicate ACK #", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisDupAckNumFieldId;

    [BoolField("tcp.analysis.lost_segment", "Previous segment not captured", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisLostSegmentFieldId;

    [BoolField("tcp.analysis.keep_alive", "Keep-Alive", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisKeepAliveFieldId;

    [BoolField("tcp.analysis.keep_alive_ack", "Keep-Alive ACK", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisKeepAliveAckFieldId;

    [BoolField("tcp.analysis.zero_window", "Zero Window", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowFieldId;

    [BoolField("tcp.analysis.zero_window_probe", "Zero Window Probe", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowProbeFieldId;

    [BoolField("tcp.analysis.zero_window_probe_ack", "Zero Window Probe Ack", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisZeroWindowProbeAckFieldId;

    [BoolField("tcp.analysis.window_update", "Window update", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisWindowUpdateFieldId;

    [BoolField("tcp.analysis.window_full", "Window is full", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisWindowFullFieldId;

    [U64Field("tcp.analysis.bytes_in_flight", "Bytes in flight", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisBytesInFlightFieldId;

    [F64Field("tcp.analysis.initial_rtt", "iRTT", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisInitialRttFieldId;

    [F64Field("tcp.analysis.ack_rtt", "ACK RTT", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisAckRttFieldId;

    [StringField("tcp.analysis.connection_state", "Connection State", IndexGroup = _AnalysisIndexGroup)]
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
    /// does not support heuristic table attributes. TCP finds it by <see cref="HeuristicTableName"/>
    /// in <c>OnStart</c>, so the registration needs no reference to the TCP instance; invalid when no
    /// table of that name exists.
    /// </summary>
    private HeuristicProtocolTableId _HeuristicTableId;

    #endregion

    #region Settings

    // Off by default: verification costs one pass over the whole segment per packet and needs the enclosing IP addresses for the
    // pseudo-header. When off, tcp.checksum.status is not emitted at all.
    [BoolSetting("tcp.verify_checksum", "Verify Checksum", "tcp", Default = false)]
    private bool _VerifyChecksum;

    // When on, the displayed tcp.seq / tcp.ack are ISN-relative; tcp.seq_raw / tcp.ack_raw always carry the wire values.
    [BoolSetting("tcp.relative_sequence_numbers", "Relative sequence numbers", "tcp", Default = true)]
    private bool _RelativeSequenceNumbers;

    // Stream-key extensions (off by default): with the plain address/port tuple, the same tuple seen on two VLANs or two capture
    // interfaces is one stream. Turning these on splits such tuples into separate streams (see _TryCreateConnectionKey).
    [BoolSetting("tcp.stream_key_last_vlan", "Include last VLAN id in the TCP stream key", "tcp", Default = false)]
    private bool _StreamKeyLastVlan;

    // Despite the display name, the "frame identity" in the key is the capture interface id (see _ReadFrameIdentity), not a frame id.
    [BoolSetting("tcp.stream_key_frame", "Include frame identity in the TCP stream key", "tcp", Default = false)]
    private bool _StreamKeyFrame;

    // Limit: number of TCP connections the tracker keeps. Value: default 10000; values at or above int.MaxValue are clamped to
    // int.MaxValue in _RunAnalysis. Threat: every distinct tuple allocates a TcpConnectionState that lives as long as this
    // protocol instance (the stack), so a scan or SYN flood would otherwise grow memory without bound. Configurable (setting,
    // changed by the operator/user), not a constant, because the right size depends on the host and the capture. Posture: secure
    // by default (a tight default; raising it is opt-in), because the input comes from untrusted networks. Exceeding it is an
    // expected failure, reported per packet as tcp.error.stream_limit, never a silent drop.
    [U64Setting("tcp.max_tracked_streams", "Maximum tracked TCP streams", "tcp", Default = 10000,
        Description = "Upper bound for the number of tracked TCP connections. A new connection beyond it gets no stream index "
            + "and is flagged; raising the value lets a flood of distinct address/port pairs use more memory.")]
    private ulong _MaxTrackedStreams;

    #endregion

    #region Cross-Protocol References
    // Resolved during OnStart via stack.GetFieldId() for reading IP addresses
    // from the field tree via sibling navigation (not flat-array scan). This is only the fallback: the normal path reads the
    // per-thread address caches of the IP protocols (see _TryCreateConnectionKey), which also work when no field tree is built.

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
    /// <see cref="IProtocol.Parse"/> is dispatched from an enclosing IPv4 layer.</summary>
    private ProtocolId _Ipv4ProtocolId;

    /// <summary>ProtocolId of the IPv6 protocol ("ipv6"), resolved at startup.
    /// Used to select the correct per-protocol thread-local address cache when
    /// <see cref="IProtocol.Parse"/> is dispatched from an enclosing IPv6 layer.</summary>
    private ProtocolId _Ipv6ProtocolId;

    // Populator delegate created once in OnStart: a method-group conversion per packet would allocate a delegate each time.
    private LazyPopulator _Populator = null!;

    // One entry per port that has exactly one registered protocol, built once in OnStart. _DispatchPort scans this short array
    // linearly (cheaper than hashing for a handful of entries) before falling back to the table lookup, which also handles ports
    // with several registered protocols.
    private (ulong Key, ProtocolId Id)[] _PortSparseCache = [];

    // TCP connection tracking for analysis. Mutable cross-packet state: only the ordered first-parse path may touch it
    // (see the class remarks); replays read the recorded effect instead.
    private readonly TcpConnectionTracker _ConnectionTracker = new();

    // TCP stream reassembly engine (buffers segments and extracts PDUs). Same single-writer rule as the connection tracker.
    // Created in OnStart because it needs the stack to look up per-protocol reassembly configurations.
    private TcpReassemblyEngine? _ReassemblyEngine;

    // Bundled field IDs for passing to the options parser
    private TcpOptionsFieldIds _OptionsFieldIds;

    partial void _OnStartCustom(Stack stack)
    {
        _IpContainerFieldId = stack.GetFieldId("ip") ?? FieldId.Invalid;
        _Ipv6ContainerFieldId = stack.GetFieldId("ipv6") ?? FieldId.Invalid;
        _IpSrcFieldId = stack.GetFieldId("ip.src") ?? FieldId.Invalid;
        _IpDstFieldId = stack.GetFieldId("ip.dst") ?? FieldId.Invalid;
        _Ipv6SrcFieldId = stack.GetFieldId("ipv6.src") ?? FieldId.Invalid;
        _Ipv6DstFieldId = stack.GetFieldId("ipv6.dst") ?? FieldId.Invalid;
        _Ipv4ProtocolId = stack.GetProtocolId("ip") ?? ProtocolId.Invalid;
        _Ipv6ProtocolId = stack.GetProtocolId("ipv6") ?? ProtocolId.Invalid;
        _HeuristicTableId = stack.GetHeuristicProtocolTableId(HeuristicTableName) ?? HeuristicProtocolTableId.Invalid;

        // Per-stack state, built once here and never per packet: the populator delegate, the sparse port-dispatch cache
        // (DispatchCacheHelper requires OnStart), and the reassembly engine that needs the stack for its config lookups.
        _Populator = _PopulateTcp;
        _PortSparseCache = stack.BuildU64SparseIdCache(_PortTableId);
        _ReassemblyEngine = new TcpReassemblyEngine(stack);

        // Bundle the option field IDs so TcpOptionsParser receives one readonly struct instead of dozens of parameters.
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
            Echo = _OptEchoFieldId,
            EchoValue = _OptEchoValueFieldId,
            EchoReply = _OptEchoReplyFieldId,
            EchoReplyValue = _OptEchoReplyValueFieldId,
            Cc = _OptCcFieldId,
            CcValue = _OptCcValueFieldId,
            CcNew = _OptCcNewFieldId,
            CcNewValue = _OptCcNewValueFieldId,
            CcEcho = _OptCcEchoFieldId,
            CcEchoValue = _OptCcEchoValueFieldId,
            Scps = _OptScpsFieldId,
            ScpsBets = _OptScpsBetsFieldId,
            ScpsSnack1 = _OptScpsSnack1FieldId,
            ScpsSnack2 = _OptScpsSnack2FieldId,
            ScpsCompress = _OptScpsCompressFieldId,
            ScpsNlts = _OptScpsNltsFieldId,
            Qs = _OptQsFieldId,
            QsRate = _OptQsRateFieldId,
            QsTtlDiff = _OptQsTtlDiffFieldId,
            AccEcn = _OptAccEcnFieldId,
            AccEcnEe0b = _OptAccEcnEe0bFieldId,
            AccEcnEceb = _OptAccEcnEcebFieldId,
            AccEcnEe1b = _OptAccEcnEe1bFieldId,
            Exp = _OptExpFieldId,
            ExpExId = _OptExpExIdFieldId,
            ExpData = _OptExpDataFieldId,
            Unknown = _OptUnknownFieldId,
            UnknownData = _OptUnknownDataFieldId,
        };
    }

    /// <summary>
    /// Registers protocol-owned alias groups. Adds "tcp.port" -> { tcp.srcport, tcp.dstport }
    /// as metadata; the alias is reachable only via the alias-group APIs on IStack and is
    /// independent of the dispatch table also named "tcp.port".
    /// </summary>
    partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        _PortAliasGroupId = builder.RegisterFieldAliasGroup(
            protocolId,
            "tcp.port",
            "Any-match alias for source/destination TCP ports.",
            [_SrcPortFieldId, _DstPortFieldId]);
    }

    /// <summary>
    /// Lazy populator: checksum, sequence/ack numbers, header length, flags, window,
    /// urgent pointer, options, segment length, and payload for the TCP segment.
    /// Source and destination ports are eagerly appended by <see cref="IProtocol.Parse"/>.
    /// Called on first access of the TCP container's children.
    /// </summary>
    private ParseResult _PopulateTcp(in MutField container)
    {
        // The container value is the whole TCP segment (header + payload) stored by _ParseBody, so the populator can re-read
        // the header itself and the lazy work costs nothing when nobody expands the node.
        if (!container.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> tcpData))
        {
            return ParseError.InvalidData(ProtocolName, "Container value is not of type Bytes");
        }

        if (!TcpHeader.TryParse(tcpData.Span, out TcpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)tcpData.Length);
        }

        // tcp.srcport and tcp.dstport are eagerly appended by Parse() so checksum
        // validation and stream tracking can read ports without materialising the lazy TCP group.
        // The any-match name "tcp.port" is exposed via the alias group registered in
        // _RegisterFieldsCustom; no duplicate tcp.port field node is appended.

        // Checksum: displayed as hex from the precomputed table (no per-packet string formatting).
        string csumText = DisplayTables.FormatHexU16(header.Checksum.Value);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(header.Checksum.Value), csumText);

        // Optional verification. Field order matters: tcp.checksum.status directly follows tcp.checksum (the index group is
        // recorded in _ParseBody under the same _VerifyChecksum condition, so the setting must not change between the two).
        if (_VerifyChecksum)
        {
            bool? valid = _ValidateChecksum(in container, tcpData.Span);
            string statusText = valid switch
            {
                true => "[Good]",
                false => "[Bad]",
                null => "[Unverified]",
            };
            container.Append(_ChecksumStatusFieldId, FieldValue.NewString(statusText));
        }

        // Header length and payload length. _ParseBody has already rejected data offsets below 5 and headers longer than the
        // data, so headerLen <= tcpData.Length holds here and the slices below cannot go out of range.
        int headerLen = header.HeaderLength;
        int payloadLen = Math.Max(0, tcpData.Length - headerLen);

        // tcp.seq and tcp.ack are appended eagerly in Parse so relative values survive replay.
        // tcp.seq_raw / tcp.ack_raw stay here as the wire values.
        container.Append(_SeqRawFieldId, FieldValue.NewU64(header.SeqNumber.Value));
        container.Append(_AckRawFieldId, FieldValue.NewU64(header.AckNumber.Value));

        container.Append(_HdrLenFieldId, FieldValue.NewU64((ulong)headerLen));

        // Flags: header byte 13. CWR/ECE are the ECN bits (RFC 3168), the other six are the classic RFC 793 control bits.
        // NS (RFC 3540) is the lowest bit of byte 12 (shared with the data offset and the 3 reserved bits), so it is read
        // from the parsed header and is not part of this byte.
        byte flags = header.Flags;
        string flagsText = TcpFlagsFormatter.Format(flags);
        container.AppendWithCustomText(_FlagsFieldId,
            FieldValue.NewU64(flags), Helpers.DisplayTables.FormatHexU8(flags), " [", flagsText, "]");

        container.Append(_FlagsCwrFieldId, FieldValue.NewBool((flags & 0x80) != 0));
        container.Append(_FlagsEceFieldId, FieldValue.NewBool((flags & 0x40) != 0));
        container.Append(_FlagsUrgFieldId, FieldValue.NewBool((flags & 0x20) != 0));
        container.Append(_FlagsAckFieldId, FieldValue.NewBool((flags & 0x10) != 0));
        container.Append(_FlagsPushFieldId, FieldValue.NewBool((flags & 0x08) != 0));
        container.Append(_FlagsResetFieldId, FieldValue.NewBool((flags & 0x04) != 0));
        container.Append(_FlagsSynFieldId, FieldValue.NewBool((flags & 0x02) != 0));
        container.Append(_FlagsFinFieldId, FieldValue.NewBool((flags & 0x01) != 0));
        container.Append(_FlagsNsFieldId, FieldValue.NewBool(header.NsFlag != 0));
        container.Append(_FlagsResFieldId, FieldValue.NewU64(header.Reserved));

        container.Append(_WindowFieldId, FieldValue.NewU64(header.WindowSize.Value));
        container.Append(_UrgentPointerFieldId, FieldValue.NewU64(header.UrgentPointer.Value));

        // TCP Options (between fixed 20-byte header and payload). The span handed to the parser ends at the header
        // length, so option parsing is confined to the header and cannot run into the payload.
        if (headerLen > TcpHeader.MinSize)
        {
            int optionsLen = headerLen - TcpHeader.MinSize;
            ReadOnlySpan<byte> optionsData = tcpData.Span.Slice(TcpHeader.MinSize, optionsLen);

            MutField optionsContainer = container.AppendWithCustomText(
                _OptionsFieldId, FieldValue.None,
                (string)ZA.String("Options: (", optionsLen, " bytes)"));

            TcpOptionsParser.Parse(optionsData, in optionsContainer, in _OptionsFieldIds);
        }

        // Payload: a slice of the original segment memory, not a copy.
        container.Append(_LenFieldId, FieldValue.NewU64((ulong)payloadLen));

        if (payloadLen > 0)
        {
            container.Append(_PayloadFieldId, FieldValue.NewBytes(tcpData[headerLen..]));
        }

        return 0;
    }

    /// <summary>
    /// Validates the TCP checksum (RFC 793: ones' complement sum over the pseudo-header and the whole segment, which is zero for
    /// a valid segment). The IP addresses come from the field tree first (the nearest enclosing IP layer), and only when there is
    /// no tree (skip mode) or it holds no IP layer from the per-thread address caches of the IP protocols.
    /// Returns <see langword="true"/> if valid, <see langword="false"/> if invalid,
    /// or <see langword="null"/> if no IP layer was found.
    /// </summary>
    /// <param name="container">The TCP container.</param>
    /// <param name="tcpData">The whole TCP segment (header and payload).</param>
    private bool? _ValidateChecksum(in MutField container, ReadOnlySpan<byte> tcpData)
    {
        const byte TcpProtocolNumber = 6;
        // The IPv6 pseudo-header carries a 32-bit upper-layer length (jumbograms, reassembled payloads); IPv4 payloads
        // never exceed 16 bits.
        int tcpLength = tcpData.Length;
        PacketId packetId = container.Packet.Id;

        // Find the enclosing IP addresses. In an IPv4/IPv6 tunnel the packet holds an outer and an inner IP layer, and the
        // pseudo-header must use the one that directly encloses TCP. The field tree answers that: walking back through the
        // previous siblings meets the innermost IP container first.
        (IPv4Address Src, IPv4Address Dst)? ipv4 = null;
        (IPv6Address Src, IPv6Address Dst)? ipv6 = null;
        bool found = container.HasFieldTree
            && IpAddressExtractor.TryFindPreviousIpAddresses(
                in container,
                _IpContainerFieldId, _Ipv6ContainerFieldId,
                _IpSrcFieldId, _IpDstFieldId, _Ipv6SrcFieldId, _Ipv6DstFieldId,
                out ipv4,
                out ipv6);

        if (!found)
        {
            // Without a tree (skip mode) the per-thread address caches of the IP protocols are the only source. They are keyed
            // by packet id, so a stale entry of an earlier packet is never used. Known limitation: each cache holds one pair per
            // IP version, and the lazy populator has no dispatch context to tell which IP layer called TCP (the IP protocols
            // dispatch to TCP through a direct call that carries none), so in an IPv4/IPv6 tunnel without a field tree the
            // IPv4 pair wins here.
            ipv4 = null;
            ipv6 = null;
            if (IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
            {
                ipv4 = (src4, dst4);
            }
            else if (IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address src6, out IPv6Address dst6))
            {
                ipv6 = (src6, dst6);
            }
            else
            {
                return null;
            }
        }

        // Pseudo-header sum (IPv4: src, dst, protocol, 16-bit length; IPv6: 128-bit addresses, 32-bit length, next header),
        // then fold in the segment. A correct segment sums to 0 (the transmitted checksum cancels the rest).
        ulong pseudoSum;
        if (ipv4.HasValue)
        {
            pseudoSum = InternetChecksum.ComputeIPv4PseudoHeaderSum(
                ipv4.Value.Src.RawValue, ipv4.Value.Dst.RawValue, TcpProtocolNumber, (ushort)tcpLength);
        }
        else
        {
            IPv6Address s6 = ipv6!.Value.Src;
            IPv6Address d6 = ipv6!.Value.Dst;
            pseudoSum = InternetChecksum.ComputeIPv6PseudoHeaderSum(
                s6.High, s6.Low, d6.High, d6.Low, TcpProtocolNumber, (uint)tcpLength);
        }

        ushort result = InternetChecksum.ComputeWithPseudoHeader(tcpData, pseudoSum);
        return result == 0;
    }

    #endregion

    #region Parse

    /// <summary>
    /// Parses a Tcp protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding stack.
    /// <para>
    /// The first parse of a packet id drives the connection tracker and the reassembly engine and
    /// records the resulting analysis and dispatch decisions; every later parse of that id replays
    /// those recordings and leaves both engines untouched. The watermark
    /// (<see cref="_IsReplay"/>) separates both paths. A first parse that ends in an exception
    /// records nothing, so its replay degrades to the stateless raw-port path (the watermark is
    /// still raised in the <see langword="finally"/>, so the packet can never re-enter the first-parse path).
    /// </para>
    /// <para>
    /// Thread safety: see the class remarks; the first-parse path must be serialized by the caller, replays may be concurrent.
    /// </para>
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Parse context carrying stack, index, and dispatch information.</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        Packet packet = parentField.Packet;

        // The effect store is keyed by (packet id, buffer location of this layer's data). A slice that is not inside the
        // packet's buffers (for example a heap copy) cannot be keyed, so ingest and replay would disagree: reject it.
        if (!parentField.TryGetEffectLayerKey(data, out int layerKey))
        {
            return ParseError.Custom(ProtocolName, "Parse data is not a slice of this packet's buffers.");
        }

        // First parse or replay is decided only by the watermark; there is deliberately no caller-supplied mode flag.
        // TCP can be entered again while the same packet is still being parsed (nested layers, for example tunnels).
        // The watermark is raised only by the outermost first-parse call, so an inner layer of the same packet still sees
        // its own packet id as "not yet parsed" and takes the first-parse path too. _ParseNesting is a plain int: it is only
        // touched on the serialized first-parse path, replays never read or write it.
        bool isReplay = _IsReplay(packet.Id);
        bool raiseWatermark = !isReplay && _ParseNesting == 0;
        if (!isReplay)
        {
            _ParseNesting++;
        }

        // The watermark must be raised in finally, also after errors and exceptions: otherwise a later parse of this id would
        // re-enter the first-parse path and mutate the tracker a second time.
        try
        {
            return _ParseBody(in parentField, data, in context, layerKey, isReplay);
        }
        finally
        {
            if (!isReplay)
            {
                _ParseNesting--;
                if (raiseWatermark)
                {
                    _RaiseWatermark(packet.Id);
                }
            }
        }
    }

    /// <summary>
    /// Parse body. <paramref name="layerKey"/> is <see cref="MutField.TryGetEffectLayerKey"/> at the parse call and
    /// <paramref name="isReplay"/> selects effect replay over stateful analysis. Everything that is shown in the packet list or
    /// the filter index is appended eagerly here; only the bulky header details are deferred to <see cref="_PopulateTcp"/>.
    /// </summary>
    private ParseResult _ParseBody(
        in MutField parentField,
        ReadOnlyMemory<byte> data,
        in ParseContext context,
        int layerKey,
        bool isReplay)
    {
        if (data.Length < TcpHeader.MinSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)data.Length);
        }

        // Index presence is recorded for the packet index (filters/lookups) regardless of whether the lazy tree is ever built.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TcpGroupId);

        // tcp.seq_raw / tcp.ack_raw are unconditionally emitted by the lazy populator,
        // so their index groups must be recorded eagerly here for the index to mirror
        // the materialized field tree without forcing materialization.
        context.RecordGroupPresence(_TcpSeqrawGroupId);
        context.RecordGroupPresence(_TcpAckrawGroupId);

        ReadOnlySpan<byte> span = data.Span;
        if (!TcpHeader.TryParse(span, out TcpHeader header, out _))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)data.Length);
        }

        // Validate data offset (minimum 5 words = 20 bytes header). Both checks protect _PopulateTcp, which slices the options and
        // the payload from the stored segment without re-validating (a data offset of 15 means up to 60 header bytes).
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
        if (_VerifyChecksum)
        {
            context.RecordGroupPresence(_TcpChecksumStatusGroupId);
        }
        if (payloadLen > 0)
        {
            context.RecordGroupPresence(_TcpPayloadGroupId);
        }
        if (headerLen > TcpHeader.MinSize)
        {
            context.RecordGroupPresence(_TcpOptionsGroupId);  // IndexGroup = "tcp.options"
        }

        // Summary text. flagsText comes from a precomputed 256-entry table (no allocation). The packet info is a LazyString that
        // captures only the value-typed ports, so the text is built only if somebody reads the info column.
        string flagsText = TcpFlagsFormatter.Format(flags);

        parentField.SetPacketInfo(ZA.Lazy(srcPort, " → ", dstPort));

        // Store full TCP segment (header + payload) for the lazy populator. The custom representation ("32 bytes") is only
        // built when the packet wants custom text (tree or cache needs it); on a skip-mode parse this saves the allocation.
        FieldValue containerValue = FieldValue.NewBytes(data);
        if (parentField.WantsCustomText(_ProtocolFieldId))
        {
            containerValue = containerValue.WithCustomRepresentation(ZA.Lazy(headerLen, " bytes"));
        }

        // The title line differs only in the ", Len: " part; two call sites keep the format arguments typed (no boxing, no
        // intermediate string) and the title is only formatted if the packet wants custom text.
        MutField tcpContainer = payloadLen > 0
            ? parentField.AppendLazyWithCustomText(
                _ProtocolFieldId, containerValue,
                "Transmission Control Protocol, Src Port: ", srcPort,
                ", Dst Port: ", dstPort, ", Len: ", payloadLen,
                " [", flagsText, "]", _Populator)
            : parentField.AppendLazyWithCustomText(
                _ProtocolFieldId, containerValue,
                "Transmission Control Protocol, Src Port: ", srcPort,
                ", Dst Port: ", dstPort,
                " [", flagsText, "]", _Populator);

        // Eagerly append ports and the displayed sequence numbers. Ports are needed by checksum validation and stream tracking
        // without materialising the lazy group. Relative seq/ack values come from the analysis, which only exists during
        // parse (live tracker or recorded effect), so they cannot be produced by the lazy populator, which may run later on
        // any thread; it keeps tcp.seq_raw / tcp.ack_raw as the wire values.
        tcpContainer.Append(_SrcPortFieldId, FieldValue.NewU64(srcPort));
        tcpContainer.Append(_DstPortFieldId, FieldValue.NewU64(dstPort));

        // ── TCP analysis: stateful on the first parse, effect replay afterwards ───────────────────
        TcpAnalysisResult analysis = _ResolveAnalysis(
            in parentField, srcPort, dstPort, header.SeqNumber.Value, header.AckNumber.Value,
            flags, header.WindowSize.Value, payloadLen, span, in context, layerKey, isReplay);

        // Relative numbers are shown only when the analysis knows the ISN; otherwise the wire value is displayed.
        uint shownSeq = analysis.SequenceIsRelative ? analysis.RelativeSeq : header.SeqNumber.Value;
        uint shownAck = analysis.AckIsRelative ? analysis.RelativeAck : header.AckNumber.Value;
        tcpContainer.Append(_SeqFieldId, FieldValue.NewU64(shownSeq));
        tcpContainer.Append(_AckFieldId, FieldValue.NewU64(shownAck));

        // Stream table full (see tcp.max_tracked_streams): the header stays visible, but there is no stream state, so no
        // tcp.stream, no analysis and no payload dispatch. The condition is reported as a field, never dropped silently.
        // The effect (carrying StreamLimitReached) is still recorded on the first parse, so a replay reproduces this outcome
        // even if the table has room later, instead of asking the tracker again.
        if (analysis.StreamLimitReached)
        {
            parentField.Append(_StreamLimitFieldId, FieldValue.NewString("TCP stream table is full"));
            context.RecordGroupPresence(_TcpErrorGroupId);
            if (!isReplay)
            {
                _RecordEffect(
                    parentField.Packet.Id, layerKey, in analysis, PayloadDispatchMode.None, null, ProtocolId.Invalid);
            }

            return data.Length;
        }

        // Eagerly append tcp.stream as sibling of the lazy TCP container
        parentField.Append(_StreamFieldId, FieldValue.NewU64(analysis.StreamIndex));

        // Report error when no enclosing IP layer was found (no stream tracking possible)
        if (analysis.NoIpLayer)
        {
            parentField.Append(_NoIpLayerFieldId,
                FieldValue.NewString("No enclosing IPv4/IPv6 layer found for stream tracking"));
            context.RecordGroupPresence(_TcpErrorGroupId);
        }

        // Eagerly append stream timing fields. NaN is the "not measured" sentinel of TcpAnalysisResult (also for replays).
        if (!double.IsNaN(analysis.TimeRelative))
        {
            parentField.Append(_TimeRelativeFieldId, FieldValue.NewF64(analysis.TimeRelative));
            context.RecordGroupPresence(_TcpTimeGroupId);
        }
        if (!double.IsNaN(analysis.TimeDelta))
        {
            parentField.Append(_TimeDeltaFieldId, FieldValue.NewF64(analysis.TimeDelta));
        }

        // Eagerly append scaled window size when window scale factor is known (-1 = no scaling: this segment is a SYN, or the sender's SYN carried no scale).
        if (analysis.WindowScaleFactor >= 0)
        {
            parentField.Append(_WindowSizeFieldId, FieldValue.NewU64(analysis.ScaledWindowSize));
            parentField.Append(_WindowScaleFactorFieldId, FieldValue.NewU64((ulong)analysis.WindowScaleFactor));
            context.RecordGroupPresence(_TcpWindowsizeGroupId);
        }

        // Eagerly append analysis fields. The container always carries at least tcp.analysis.connection_state, so the group is
        // recorded whenever the connection was tracked. A full stream table already returned above, so only the missing
        // IP layer is left as an untracked case here.
        if (!analysis.NoIpLayer)
        {
            context.RecordGroupPresence(_TcpAnalysisGroupId);
            _AppendAnalysisFields(in parentField, in analysis, in context);
        }

        // Dispatch by port on parentField (sibling of the TCP container, not a child), with reassembly and heuristic fallback.
        if (payloadLen > 0)
        {
            ReadOnlyMemory<byte> payload = data[headerLen..];
            return _DispatchTcpPayload(
                in parentField, srcPort, dstPort, header.SeqNumber.Value, payload, data.Length, in context, in analysis, layerKey, isReplay);
        }

        // No payload path: record analysis-only effects so a replay reproduces identical fields.
        if (!isReplay)
        {
            _RecordEffect(
                parentField.Packet.Id, layerKey, in analysis, PayloadDispatchMode.None, null, ProtocolId.Invalid);
        }

        return data.Length;
    }

    #endregion

    #region Analysis and payload dispatch

    /// <summary>
    /// Resolves TCP analysis from the live tracker (first parse) or from the recorded effect (replay).
    /// A replay without a recorded effect degrades to <see cref="TcpAnalysisResult.Empty"/> rather
    /// than touching the tracker from a reader thread.
    /// </summary>
    private TcpAnalysisResult _ResolveAnalysis(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags, ushort window,
        int payloadLen,
        ReadOnlySpan<byte> tcpSegment, in ParseContext context,
        int layerKey, bool isReplay)
    {
        if (isReplay)
        {
            TcpLayerEffect? recorded = _FindRecordedEffect(parentField.Packet.Id, layerKey);
            if (recorded is null)
            {
                return TcpAnalysisResult.Empty;
            }

            return _AnalysisFromEffect(recorded.Value.Analysis);
        }

        return _RunAnalysis(
            in parentField, srcPort, dstPort, seqNum, ackNum, flags, window, payloadLen, tcpSegment, in context);
    }

    /// <summary>Dispatches TCP payload on the first parse, or replays the recorded dispatch.</summary>
    private ParseResult _DispatchTcpPayload(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint sequence,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        int layerKey,
        bool isReplay)
    {
        if (isReplay)
        {
            return _DispatchTcpPayloadReplay(
                in parentField, srcPort, dstPort, payload, totalConsumed, in context, layerKey);
        }

        // Record effects after the dispatch attempt so error exits also capture the
        // analysis facts and the dispatch mode reached so far.
        ParseResult result = _DispatchTcpPayloadFirstParse(
            in parentField, srcPort, dstPort, sequence, payload, in context,
            in analysis, totalConsumed,
            out PayloadDispatchMode mode, out List<PduEffect>? pdus, out ProtocolId heuristicProtocolId);

        // The learned server port is copied into the effect now: the live connection state keeps changing with later
        // packets, so a replay could not read it from there and must not (it would race with the ingest thread).
        bool hasPreferred = analysis.ConnectionState is { ServerPortSet: true };
        ushort preferred = hasPreferred ? analysis.ConnectionState!.ServerPort : (ushort)0;
        _RecordEffect(parentField.Packet.Id, layerKey, in analysis, mode, pdus, heuristicProtocolId, preferred, hasPreferred);
        return result;
    }

    /// <summary>
    /// Dispatches TCP payload on the first parse of a packet and reports the replay metadata
    /// (<paramref name="mode"/>, <paramref name="pdus"/>, <paramref name="heuristicProtocolId"/>)
    /// via out parameters so the caller can record them as effects.
    /// </summary>
    private ParseResult _DispatchTcpPayloadFirstParse(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        uint sequence,
        ReadOnlyMemory<byte> payload,
        in ParseContext context,
        in TcpAnalysisResult analysis,
        int totalConsumed,
        out PayloadDispatchMode mode,
        out List<PduEffect>? pdus,
        out ProtocolId heuristicProtocolId)
    {
        // Out parameters describe what actually happened so the caller can record it as the replay effect; they are assigned
        // up front because every early return (including errors) must still leave a consistent record.
        ushort lowPort = Math.Min(srcPort, dstPort);
        ushort highPort = Math.Max(srcPort, dstPort);
        mode = PayloadDispatchMode.RawPort;
        pdus = null;
        heuristicProtocolId = ProtocolId.Invalid;

        // Step 1: pick the application protocol that owns this connection, for reassembly. Server port learned from the SYN
        // wins (a client port that happens to be well-known must not select a protocol); without it, the lower port is the
        // usual server side, and the higher port is only consulted when the lower port has no protocol at all. Reassembly
        // is configured per protocol, so a port with zero or several registered protocols is not reassembled (several owners
        // are ambiguous and fall through to the plain port dispatch below).
        bool serverPortKnown = analysis.ConnectionState is { ServerPortSet: true };
        ushort serverPort = serverPortKnown ? analysis.ConnectionState!.ServerPort : (ushort)0;
        ProtocolId targetProtocol = default;
        bool singleOwner = false;
        if (serverPortKnown)
        {
            ReadOnlySpan<ProtocolId> onServer = _PortProtocols(serverPort, in context);
            if (onServer.Length == 1)
            {
                singleOwner = true;
                targetProtocol = onServer[0];
            }
        }
        else
        {
            ReadOnlySpan<ProtocolId> onLow = _PortProtocols(lowPort, in context);
            if (onLow.Length == 1)
            {
                singleOwner = true;
                targetProtocol = onLow[0];
            }
            else if (onLow.IsEmpty && lowPort != highPort)
            {
                ReadOnlySpan<ProtocolId> onHigh = _PortProtocols(highPort, in context);
                if (onHigh.Length == 1)
                {
                    singleOwner = true;
                    targetProtocol = onHigh[0];
                }
            }
        }

        // Step 2: reassembly path. Segments are buffered per direction; every complete PDU is dispatched to the owning protocol
        // (zero, one or several per segment: a segment can complete a pending PDU, carry several, or only add to a partial one).
        if (singleOwner && targetProtocol.IsValid && _ReassemblyEngine is not null
            && analysis.ConnectionState is not null)
        {
            TcpConnectionKey connKey = analysis.ConnectionKey;
            TcpStreamState? streamState = _ReassemblyEngine.GetOrCreateStream(
                in connKey, targetProtocol,
                analysis.SrcAddr, srcPort, out bool reassemblyForward);

            // A null stream state means the protocol has no reassembly configuration: fall through to plain port dispatch.
            if (streamState is not null)
            {
                // Passed to context-aware PDU detectors via StreamDetectionContext.HandshakeObserved: set once the capture
                // contains the SYN or SYN-ACK, i.e. the stream is seen from its start rather than joined mid-flow.
                if ((analysis.ConnectionState.Completeness
                        & (TcpConnectionState.SynSeen | TcpConnectionState.SynAckSeen)) != 0)
                {
                    streamState.HandshakeObserved = true;
                }
            }

            // A segment that the stream buffer refuses (the per-direction buffer limit was hit, or there is no boundary
            // detector) also falls through: the buffer abandons the stream and would otherwise swallow every later segment
            // without any sign. The plain dispatch shows what the segment holds on its own.
            if (streamState is not null
                && TcpReassemblyEngine.FeedSegment(streamState, reassemblyForward, sequence, payload))
            {
                bool emitted = false;
                while (TcpReassemblyEngine.TryExtractPdu(streamState, reassemblyForward,
                    out ReadOnlyMemory<byte> pdu))
                {
                    emitted = true;
                    mode = PayloadDispatchMode.ReassemblyPdu;
                    if (pdus is null)
                    {
                        pdus = [];
                    }

                    // TryExtractPdu already returns a caller-owned byte[] (never a slice of Frame.Data), so do not copy it again.
                    // It is attached to the packet as an additional buffer before the nested parse so the inner layer's data
                    // slice gets an effect layer key. The recorded PduEffect keeps the same bytes: a replay binds them again in
                    // the same order and therefore gets the same buffer index and layer key as this first parse. A zero-copy
                    // slice of the frame here would key as the frame on ingest but as an extra buffer on replay, and the
                    // replay lookup would miss.
                    ReadOnlyMemory<byte> bound = parentField.BindParseBuffer(pdu);
                    if (bound.IsEmpty)
                    {
                        // The packet holds at most 255 bound buffers (a constant of the packet's effect keys), so a segment that
                        // completes more PDUs than that cannot nest-parse the rest. An empty slice would reach the inner
                        // protocol as "no data" or as an unrelated error, so the limit is named here. The PDU is not recorded
                        // as an effect: it was not dispatched.
                        return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
                    }

                    pdus.Add(new PduEffect(targetProtocol, pdu));

                    ParseResult pduResult = parentField.CallProtocol(targetProtocol, bound, in context);
                    if (pduResult.TryPropagateError(out ParseResult pduError))
                    {
                        return pduError;
                    }
                }

                if (!emitted)
                {
                    mode = PayloadDispatchMode.ReassemblyNoEmit;
                }

                return totalConsumed;
            }
        }

        // Step 3: plain port dispatch of the raw segment payload (no reassembly). With a learned server port only that port is
        // tried; otherwise the lower port first, then the higher one if the lower port consumed nothing.
        mode = PayloadDispatchMode.RawPort;
        ParseResult result;
        if (serverPortKnown)
        {
            result = _DispatchPort(in parentField, serverPort, payload, in context);
            if (result.TryPropagateError(out ParseResult serverError))
            {
                return serverError;
            }
        }
        else
        {
            result = _DispatchPort(in parentField, lowPort, payload, in context);
            if (result.TryPropagateError(out ParseResult error))
            {
                return error;
            }

            if (!result.TryGetConsumed(out _) && lowPort != highPort)
            {
                result = _DispatchPort(in parentField, highPort, payload, in context);
                if (result.TryPropagateError(out ParseResult highError))
                {
                    return highError;
                }
            }
        }

        // Step 4: content-based detection as the last resort when no port matched. A hit is recorded (mode Heuristic plus the
        // protocol id) so a replay calls that protocol directly; re-running the detectors would
        // read and write the per-connection cache (HeuristicProtocolId), and a replay must not touch connection state.
        if (!result.TryGetConsumed(out _) && _HeuristicTableId.IsValid)
        {
            result = _TryHeuristicDispatch(in parentField, payload, in context, analysis.ConnectionState);
            if (result.TryPropagateError(out ParseResult heuristicError))
            {
                return heuristicError;
            }

            if (result.TryGetConsumed(out _) && analysis.ConnectionState?.HeuristicProtocolId is ProtocolId heuristicId)
            {
                mode = PayloadDispatchMode.Heuristic;
                heuristicProtocolId = heuristicId;
            }
        }

        return totalConsumed;
    }

    /// <summary>
    /// Replays TCP payload dispatch from the recorded effect without mutating trackers.
    /// Without a recorded effect the payload falls back to the stateless raw-port dispatch.
    /// </summary>
    private ParseResult _DispatchTcpPayloadReplay(
        in MutField parentField,
        ushort srcPort, ushort dstPort,
        ReadOnlyMemory<byte> payload,
        int totalConsumed,
        in ParseContext context,
        int layerKey)
    {
        // No recorded effect (the first parse threw before recording): degrade to the stateless low-then-high port dispatch.
        // It never falls back into mutation (tracker/reassembly), so a reader thread stays race-free.
        TcpLayerEffect? recorded = _FindRecordedEffect(parentField.Packet.Id, layerKey);
        if (recorded is null)
        {
            ushort lowPort = Math.Min(srcPort, dstPort);
            ushort highPort = Math.Max(srcPort, dstPort);
            ParseResult result = _DispatchPort(in parentField, lowPort, payload, in context);
            if (result.TryPropagateError(out ParseResult error))
            {
                return error;
            }

            if (!result.TryGetConsumed(out _) && lowPort != highPort)
            {
                result = _DispatchPort(in parentField, highPort, payload, in context);
                if (result.TryPropagateError(out ParseResult highError))
                {
                    return highError;
                }
            }

            return totalConsumed;
        }

        // Replay the recorded decision exactly; the switch mirrors the modes written by _DispatchTcpPayloadFirstParse.
        PayloadDispatchEffect dispatch = recorded.Value.Dispatch;
        switch (dispatch.Mode)
        {
            case PayloadDispatchMode.ReassemblyPdu:
                if (dispatch.Pdus is not null)
                {
                    foreach (PduEffect pdu in dispatch.Pdus)
                    {
                        // Binding in recorded order reproduces the buffer indices (and thus effect layer keys) of the first
                        // parse, so nested stateful layers find their own recorded effects.
                        ReadOnlyMemory<byte> bound = parentField.BindParseBuffer(pdu.PduBytes);
                        if (bound.IsEmpty)
                        {
                            // Same limit as on the first parse (the replay binds the same PDUs in the same order).
                            return ParseError.Custom(ProtocolName, _BoundBufferLimitMessage);
                        }

                        ParseResult pduResult = parentField.CallProtocol(
                            pdu.ProtocolId, bound, in context);
                        if (pduResult.TryPropagateError(out ParseResult pduError))
                        {
                            return pduError;
                        }
                    }
                }

                return totalConsumed;

            case PayloadDispatchMode.ReassemblyNoEmit:
                // No complete PDU was available on the first parse, so nothing was dispatched then and nothing is now.
                return totalConsumed;

            case PayloadDispatchMode.Heuristic:
                if (dispatch.HeuristicProtocolId.IsValid)
                {
                    ParseResult heuristicResult = parentField.CallProtocol(
                        dispatch.HeuristicProtocolId, payload, in context);
                    if (heuristicResult.TryPropagateError(out ParseResult heuristicError))
                    {
                        return heuristicError;
                    }
                }

                return totalConsumed;

            case PayloadDispatchMode.RawPort:
            default:
                // RawPort also covers None and unknown modes. A learned server port was the only port tried on the first parse,
                // so only it is replayed; otherwise the low-then-high order of the first parse applies.
                if (dispatch.HasPreferredPort)
                {
                    ParseResult preferredResult = _DispatchPort(
                        in parentField, dispatch.PreferredPort, payload, in context);
                    if (preferredResult.TryPropagateError(out ParseResult preferredError))
                    {
                        return preferredError;
                    }

                    return totalConsumed;
                }

                ushort low = Math.Min(srcPort, dstPort);
                ushort high = Math.Max(srcPort, dstPort);
                ParseResult portResult = _DispatchPort(in parentField, low, payload, in context);
                if (portResult.TryPropagateError(out ParseResult portError))
                {
                    return portError;
                }

                if (!portResult.TryGetConsumed(out _) && low != high)
                {
                    portResult = _DispatchPort(in parentField, high, payload, in context);
                    if (portResult.TryPropagateError(out ParseResult highPortError))
                    {
                        return highPortError;
                    }
                }

                return totalConsumed;
        }
    }

    /// <summary>
    /// Copies live analysis into an immutable effect. The mutable <see cref="TcpConnectionState"/> reference is dropped on
    /// purpose: it is owned by the ingest thread, and a stored effect is read by any number of replay threads.
    /// </summary>
    private static AnalysisEffect _ToAnalysisEffect(in TcpAnalysisResult analysis) =>
        // The enum casts are value casts: AnalysisEffectFlags must mirror TcpAnalysisFlags bit for bit (the internal enum cannot
        // be part of the public effect type), and TcpConnectionPhase is a byte enum. Keep both in sync when adding a value.
        new(
            StreamIndex: analysis.StreamIndex,
            Flags: (AnalysisEffectFlags)analysis.Flags,
            DupAckNum: analysis.DupAckNum,
            BytesInFlight: analysis.BytesInFlight,
            InitialRtt: analysis.InitialRtt,
            AckRtt: analysis.AckRtt,
            TimeRelative: analysis.TimeRelative,
            TimeDelta: analysis.TimeDelta,
            ScaledWindowSize: analysis.ScaledWindowSize,
            WindowScaleFactor: analysis.WindowScaleFactor,
            Phase: (byte)analysis.Phase,
            NoIpLayer: analysis.NoIpLayer,
            RelativeSeq: analysis.RelativeSeq,
            RelativeAck: analysis.RelativeAck,
            SequenceIsRelative: analysis.SequenceIsRelative,
            AckIsRelative: analysis.AckIsRelative,
            StreamLimitReached: analysis.StreamLimitReached);

    /// <summary>Rebuilds a tracker-free analysis result from a recorded effect.</summary>
    private static TcpAnalysisResult _AnalysisFromEffect(in AnalysisEffect effect) =>
        new()
        {
            Flags = (TcpAnalysisFlags)effect.Flags,
            StreamIndex = effect.StreamIndex,
            DupAckNum = effect.DupAckNum,
            BytesInFlight = effect.BytesInFlight,
            InitialRtt = effect.InitialRtt,
            AckRtt = effect.AckRtt,
            TimeRelative = effect.TimeRelative,
            TimeDelta = effect.TimeDelta,
            ScaledWindowSize = effect.ScaledWindowSize,
            WindowScaleFactor = effect.WindowScaleFactor,
            Phase = (TcpConnectionPhase)effect.Phase,
            NoIpLayer = effect.NoIpLayer,
            RelativeSeq = effect.RelativeSeq,
            RelativeAck = effect.RelativeAck,
            SequenceIsRelative = effect.SequenceIsRelative,
            AckIsRelative = effect.AckIsRelative,
            StreamLimitReached = effect.StreamLimitReached,
        };

    /// <summary>
    /// Runs TCP connection tracking and segment analysis.
    /// Creates/finds the connection state, determines direction, and invokes the analyzer.
    /// For SYN packets with options, extracts the Window Scale value and passes it
    /// to the analyzer so it can be stored for future window calculations.
    /// </summary>
    private TcpAnalysisResult _RunAnalysis(
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
        if (!_TryCreateConnectionKey(in parentField, srcPort, dstPort, out TcpConnectionKey key, out UInt128 srcAddr, in context))
        {
            // No IP layer found — return empty result with NoIpLayer flag
            // so the caller can append a diagnostic error field.
            return TcpAnalysisResult.Empty with
            {
                NoIpLayer = true
            };
        }

        // Stream table limit (tcp.max_tracked_streams, see the setting). The setting is a ulong while the tracker compares
        // against a Dictionary count (int), so values from int.MaxValue up are clamped instead of overflowing the cast.
        // A full table is an expected failure: report it to the caller (it appends tcp.error.stream_limit) and keep going.
        int maxStreams = _MaxTrackedStreams >= int.MaxValue ? int.MaxValue : (int)_MaxTrackedStreams;
        if (!_ConnectionTracker.TryGetOrCreate(in key, maxStreams, out TcpConnectionState? conn))
        {
            return TcpAnalysisResult.Empty with
            {
                StreamLimitReached = true,
                ConnectionKey = key,
                SrcAddr = srcAddr,
            };
        }

        // Direction relative to the normalized key (the key orders the two endpoints, so both directions share one state).
        bool isForward = key.IsForward(srcAddr, srcPort);

        // Extract Window Scale from SYN/SYN-ACK options for connection state tracking.
        // This must be done eagerly (not lazily) because subsequent packets need the
        // scale factor to compute the correct scaled window size. The option is only valid in SYN segments (RFC 7323),
        // so other segments are not scanned. The lightweight scan avoids running the full options parser, which would
        // append fields, just to read one byte.
        byte? windowScale = null;
        bool isSyn = (flags & 0x02) != 0;
        if (isSyn && tcpSegment.Length > TcpHeader.MinSize)
        {
            // Data offset is the high nibble of header byte 12, in 32-bit words. The options span is bounds-checked against
            // the segment length before slicing, so the scan stays inside the segment.
            int headerLen = (tcpSegment[12] >> 4) * 4; // data offset from raw header
            if (headerLen > TcpHeader.MinSize && headerLen <= tcpSegment.Length)
            {
                ReadOnlySpan<byte> optionsData = tcpSegment[TcpHeader.MinSize..headerLen];
                windowScale = _ExtractWindowScale(optionsData);
            }
        }

        // The analyzer mutates the connection state, which is why this method is first-parse only. The connection key and
        // source address are attached to the result for the reassembly step.
        return TcpConnectionTracker.Analyze(
            conn, isForward, seqNum, ackNum, flags, window, payloadLen, timestamp,
            srcPort, dstPort, windowScale, _RelativeSequenceNumbers)
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
    private static byte? _ExtractWindowScale(ReadOnlySpan<byte> optionsData)
    {
        const byte OptEol = 0;
        const byte OptNop = 1;
        const byte OptWindowScale = 3;
        // RFC 7323 section 2.3: a shift count above 14 must be treated as 14 (the largest window stays below 1 GiB).
        const byte MaxWindowScale = 14;

        // Walk the option list (kind, length, data; EOL and NOP are single bytes). Every length is validated against the
        // remaining span, so a malformed option ends the scan instead of reading out of range or looping forever (length < 2).
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
    private bool _TryCreateConnectionKey(
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

        // Optional key extensions (settings tcp.stream_key_last_vlan / tcp.stream_key_frame). Both default to 0 so streams
        // are not split unless the setting is on; the discard is deliberate (no VLAN tag simply leaves the key part at 0).
        ushort lastVlan = 0;
        if (_StreamKeyLastVlan)
        {
            _ = VlanProtocol.TryGetLastVlan(packetId, out lastVlan);
        }

        int frameIdentity = 0;
        if (_StreamKeyFrame)
        {
            frameIdentity = _ReadFrameIdentity(parentField.Packet);
        }

        // Fast path: read from per-protocol thread-local caches (no field-tree walk, works in skip mode where no tree exists).
        // IPv4 addresses are stored as IPv4-mapped IPv6 (::ffff:a.b.c.d: upper 64 bits zero, bits 47..32 of the lower half
        // set) so one UInt128 address type serves both IP versions in the key and in the direction check.
        if (!callerIsIpv6 && IPv4Protocol.TryGetCachedAddresses(packetId, out IPv4Address src4, out IPv4Address dst4))
        {
            key = TcpConnectionKey.FromIPv4(src4.RawValue, dst4.RawValue, srcPort, dstPort, lastVlan, frameIdentity);
            srcAddr = new UInt128(0, 0x0000_FFFF_0000_0000UL | src4.RawValue);
            return true;
        }
        if (!callerIsIpv4 && IPv6Protocol.TryGetCachedAddresses(packetId, out IPv6Address cachedSrc6, out IPv6Address cachedDst6))
        {
            srcAddr = new UInt128(cachedSrc6.High, cachedSrc6.Low);
            key = new TcpConnectionKey(srcAddr, new UInt128(cachedDst6.High, cachedDst6.Low), srcPort, dstPort, lastVlan, frameIdentity);
            return true;
        }

        // Fallback (no cache entry matches this packet id or the caller guard excluded it): walk previous siblings to find
        // typed IP addresses. Needs a field tree, so it returns false in skip mode.
        // materialize: false — IP containers are already eager at parse time, so no lazy group is expanded for the lookup.
        if (!parentField.HasFieldTree
            || !parentField.TryGetLastChild(out MutField prev, materialize: false))
        {
            key = default;
            srcAddr = default;
            return false;
        }

        if (!IpAddressExtractor.TryFindPreviousIpAddresses(in prev,
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
            key = TcpConnectionKey.FromIPv4(srcIp, dstIp, srcPort, dstPort, lastVlan, frameIdentity);
            srcAddr = new UInt128(0, 0x0000_FFFF_0000_0000UL | srcIp);
            return true;
        }

        // ipv6 is guaranteed non-null when TryFindPreviousIpAddresses returns true
        IPv6Address src6 = ipv6!.Value.Src;
        IPv6Address dst6 = ipv6!.Value.Dst;
        srcAddr = new UInt128(src6.High, src6.Low);
        UInt128 dstAddr128 = new(dst6.High, dst6.Low);
        key = new TcpConnectionKey(srcAddr, dstAddr128, srcPort, dstPort, lastVlan, frameIdentity);
        return true;
    }

    /// <summary>
    /// Capture-interface identity for the stream key (<see cref="Frame.InterfaceId"/>).
    /// An invalid interface contributes 0 and does not split streams. This is not <see cref="Frame.Id"/>.
    /// </summary>
    private static int _ReadFrameIdentity(Packet packet)
    {
        if (packet.Frame.InterfaceId.IsValid)
        {
            return packet.Frame.InterfaceId.Value;
        }

        return 0;
    }

    #endregion

    /// <summary>
    /// Eagerly appends tcp.analysis container and its sub-fields to the parent field.
    /// Called for every tracked segment (the container always carries at least the connection state);
    /// the individual flag fields appear only for the conditions that were detected. Works on a recorded analysis
    /// as well as a live one, so a replay yields identical fields.
    /// </summary>
    private void _AppendAnalysisFields(in MutField parentField, in TcpAnalysisResult analysis, in ParseContext context)
    {
        MutField analysisContainer = parentField.Append(_AnalysisFieldId, FieldValue.None);

        TcpAnalysisFlags flags = analysis.Flags;

        if ((flags & TcpAnalysisFlags.Retransmission) != 0)
        {
            analysisContainer.Append(_AnalysisRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.FastRetransmission) != 0)
        {
            analysisContainer.Append(_AnalysisFastRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.SpuriousRetransmission) != 0)
        {
            analysisContainer.Append(_AnalysisSpuriousRetransmissionFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.OutOfOrder) != 0)
        {
            analysisContainer.Append(_AnalysisOutOfOrderFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.DuplicateAck) != 0)
        {
            analysisContainer.Append(_AnalysisDuplicateAckFieldId, FieldValue.NewBool(true));
            analysisContainer.Append(_AnalysisDupAckNumFieldId, FieldValue.NewU64(analysis.DupAckNum));
        }
        if ((flags & TcpAnalysisFlags.LostSegment) != 0)
        {
            analysisContainer.Append(_AnalysisLostSegmentFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.KeepAlive) != 0)
        {
            analysisContainer.Append(_AnalysisKeepAliveFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.KeepAliveAck) != 0)
        {
            analysisContainer.Append(_AnalysisKeepAliveAckFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindow) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbe) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowProbeFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.ZeroWindowProbeAck) != 0)
        {
            analysisContainer.Append(_AnalysisZeroWindowProbeAckFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.WindowUpdate) != 0)
        {
            analysisContainer.Append(_AnalysisWindowUpdateFieldId, FieldValue.NewBool(true));
        }
        if ((flags & TcpAnalysisFlags.WindowFull) != 0)
        {
            analysisContainer.Append(_AnalysisWindowFullFieldId, FieldValue.NewBool(true));
        }

        if (analysis.BytesInFlight > 0)
        {
            analysisContainer.Append(_AnalysisBytesInFlightFieldId, FieldValue.NewU64(analysis.BytesInFlight));
        }
        if (!double.IsNaN(analysis.InitialRtt))
        {
            analysisContainer.Append(_AnalysisInitialRttFieldId, FieldValue.NewF64(analysis.InitialRtt));
        }
        if (!double.IsNaN(analysis.AckRtt))
        {
            analysisContainer.Append(_AnalysisAckRttFieldId, FieldValue.NewF64(analysis.AckRtt));
        }

        // Connection state (always present when analysis container is shown)
        string phaseText = TcpConnectionTracker.GetPhaseDisplayText(analysis.Phase);
        analysisContainer.Append(_AnalysisConnectionStateFieldId, FieldValue.NewString(phaseText));
    }

    /// <summary>
    /// Dispatches to the next protocol by TCP port using the sparse cache (ports with exactly one registered protocol).
    /// Falls back to full table dispatch for uncached ports, which also covers ports with several registered protocols
    /// and ports without any. Returns <see cref="ParseResult.NotDispatched"/> when the table has no protocol for the port.
    /// </summary>
    private ParseResult _DispatchPort(
        in MutField parentField, ulong port, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        foreach ((ulong key, ProtocolId id) in _PortSparseCache)
        {
            if (key == port)
            {
                return parentField.CallProtocol(id, payload, in context);
            }
        }

        return parentField.TryCallNextProtocolU64(_PortTableId, port, payload, in context);
    }

    /// <summary>
    /// Attempts heuristic-based protocol dispatch with per-connection caching.
    /// On the first data packet of a connection, runs all registered heuristic parsers.
    /// If a match is found, caches the protocol ID on the connection state so subsequent
    /// packets bypass the heuristic tests. Writes the cache on the connection state, so it belongs to the
    /// first-parse path only; replays use the recorded <see cref="PayloadDispatchMode.Heuristic"/> effect instead.
    /// </summary>
    private ParseResult _TryHeuristicDispatch(
        in MutField parentField, ReadOnlyMemory<byte> payload, in ParseContext context,
        TcpConnectionState? connectionState)
    {
        // Level 1: Check per-connection cache for a previously detected protocol
        if (connectionState?.HeuristicProtocolId is not null)
        {
            return parentField.CallProtocol(connectionState.HeuristicProtocolId.Value, payload, in context);
        }

        // Level 2: Run the heuristic protocol table to detect the protocol
        ProtocolId? matchedId = context.Stack?.TryMatchHeuristic(_HeuristicTableId, payload);
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
    /// Returns the protocols registered for <paramref name="port"/> in the TCP port table, or an empty span when the parse
    /// context carries no stack. Used to decide whether a port has a single owner (the precondition for reassembly).
    /// </summary>
    private ReadOnlySpan<ProtocolId> _PortProtocols(ushort port, in ParseContext context)
    {
        Stack? stack = context.Stack;
        if (stack is null)
        {
            return default;
        }

        return stack.GetProtocolsFromU64ProtocolTable(_PortTableId, port);
    }
}
