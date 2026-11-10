// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Transmission Control Protocol (RFC 9293) parser with lazy field population, sequence analysis and stream reassembly.
/// <para>Field tree structure:</para>
/// <code>
/// tcp: Transmission Control Protocol, Src Port: 443, Dst Port: 52341
/// ├── tcp.srcport: 443
/// ├── tcp.dstport: 52341
/// ├── tcp.seq: 1                       [relative to the sender's initial sequence number]
/// ├── tcp.ack: 1                       [relative to the peer's initial sequence number]
/// ├── tcp.nxtseq: 1461                 [sequence number after this segment, SYN and FIN count one]
/// ├── tcp.seq_raw: 0x12345678          [absolute sequence number]
/// ├── tcp.ack_raw: 0xabcdef01          [absolute ack number]
/// ├── tcp.hdr_len: 32
/// ├── tcp.flags: 0x018 [PSH, ACK]      [12 bits: the AE flag and three reserved bits above the classic eight]
/// │   ├── tcp.flags.cwr / ece / urg / ack / push / reset / syn / fin
/// │   ├── tcp.flags.ae: false
/// │   └── tcp.flags.res: 0
/// ├── tcp.window_size_value: 502
/// ├── tcp.checksum: 0xabcd
/// ├── tcp.checksum.status: 1           [optional: 0 bad, 1 good, 2 unverified]
/// ├── tcp.urgent_pointer: 0
/// ├── tcp.options: Options (12 bytes)  [optional, when the header is longer than 20 bytes]
/// │   └── tcp.options.mss / wscale / sack_perm / sack / timestamp / tfo / mptcp / md5 / ao / user_to / echo / cc / scps / qs
/// │       / acc_ecn / experimental / unknown ...
/// ├── tcp.len: 1460                    [payload length]
/// └── tcp.payload: (1460 bytes)        [optional]
/// tcp.stream: 0                            [eager; absent when the stream table is full]
/// tcp.time_relative: 0.023                 [eager, optional]
/// tcp.time_delta: 0.001                    [eager, optional]
/// tcp.window_size: 64256                   [eager, scaled by the sender's window scale when it is known]
/// tcp.window_size_scalefactor: 128         [eager, not on SYN: -1 unknown, -2 no scaling, otherwise the multiplier]
/// tcp.completeness: 7                      [eager, lazy children: .str, .syn, .syn_ack, .ack, .data, .fin, .rst]
/// tcp.analysis: SEQ/ACK analysis           [eager]
/// ├── tcp.analysis.flags                   [present when at least one anomaly applies]
/// │   ├── tcp.analysis.retransmission / fast_retransmission / spurious_retransmission / out_of_order
/// │   ├── tcp.analysis.duplicate_ack / lost_segment / ack_lost_segment / partial_ack / reused_ports
/// │   └── tcp.analysis.keep_alive / keep_alive_ack / zero_window / zero_window_probe / zero_window_probe_ack / window_update / window_full
/// ├── tcp.analysis.duplicate_ack_num / duplicate_ack_frame   [duplicate ACK: its number and the ACK it repeats]
/// ├── tcp.analysis.rto / rto_frame                           [retransmission: the timeout and the packet it counts from]
/// ├── tcp.analysis.acks_frame: 12          [the packet this ACK acknowledges]
/// ├── tcp.analysis.ambiguous_ack           [the acknowledged bytes were sent more than once]
/// ├── tcp.analysis.bytes_in_flight: 1460   [data segments]
/// ├── tcp.analysis.ack_rtt: 0.001
/// ├── tcp.analysis.initial_rtt: 0.023      [on packets that have an analysis result, once measured]
/// └── tcp.analysis.connection_state: ESTABLISHED
/// tcp.segments                             [on the segment that completes a PDU of several segments]
/// ├── tcp.segment: 7                       [one per contributing packet]
/// ├── tcp.segment.count: 3
/// └── tcp.reassembled.length: 4380
/// tcp.segment_data                         [a segment that only fills a reassembly buffer, or repeats data]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> The first parse of a packet id mutates the TCP connection tracker
/// and the stream reassembler, so it must stay ordered and single-threaded (the session guarantees
/// this under its parse lock). Every later parse of an already-parsed id is a replay: it only reads
/// the protocol-owned effect store and is safe to run on any number of threads concurrently, even
/// while further packets are being parsed for the first time. The watermark
/// <see cref="_Gate"/> separates both paths without any caller-supplied mode flag.</para>
/// <para><b>Reassembly:</b> DNS, HTTP/1, TLS and HTTP/2 are reassembled before their parser runs: the segments of a
/// direction are ordered by sequence number in a buffer, and every PDU the application protocol's boundary detector finds
/// is handed over whole. This also holds for a protocol that was found by content (the heuristic table) on a port without
/// an owner; a port with more than one registered protocol is not reassembled. A FIN delivers what is still buffered as the
/// last PDU of its direction (a response that ends with the connection), an RST drops it. Retransmitted or reordered data on
/// the plain path is shown as <c>tcp.segment_data</c> and not parsed a second time.</para>
/// <para><b>Analysis and known differences:</b> the sequence analysis follows the established rules of passive TCP analysis and
/// is cross-checked against an independent dissector in the test suite. It is a single pass over the packets, so values that
/// need a later packet are not reproduced: <c>tcp.reassembled_in</c> on the earlier segments of a PDU, and the final
/// (two-pass) conversation completeness. <c>tcp.completeness</c> shows the state before the packet itself.</para>
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

    // Values of tcp.checksum.status. Format facts of the field (a protocol-wide convention shared with other analyzers), so
    // constants and not settings: filters written elsewhere rely on exactly these numbers.
    private const ulong _ChecksumBad = 0;
    private const ulong _ChecksumGood = 1;
    private const ulong _ChecksumUnverified = 2;

    // Info suffixes for segments that are not dispatched on their own (see _AppendSegmentDataInfo).
    private const string _InfoSegmentOfPdu = " [TCP segment of a reassembled PDU]";
    private const string _InfoRetransmission = " [TCP Retransmission]";
    private const string _InfoFastRetransmission = " [TCP Fast Retransmission]";
    private const string _InfoSpuriousRetransmission = " [TCP Spurious Retransmission]";
    private const string _InfoOutOfOrder = " [TCP Out-Of-Order]";

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

    // Bit 8 of the 12-bit flags field. RFC 3540 named it NS (Nonce Sum); Accurate ECN reuses the bit as AE, which is how
    // current tools name it, so filters written for them match.
    [BoolField("tcp.flags.ae", "Accurate ECN", IndexGroup = _TcpIndexGroup)]
    private FieldId _FlagsAeFieldId;

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

    #region Combined port field (alias group)

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

    // Present on every segment: the advertised window, shifted by the sender's scale when that scale is known.
    [U64Field("tcp.window_size", "Calculated window size", IndexGroup = "tcp.windowsize")]
    private FieldId _WindowSizeFieldId;

    // Signed on purpose: -1 means "unknown" (no SYN captured), -2 means "the handshake negotiated no scaling", anything else
    // is the multiplier (1 << shift). Present on every segment except SYN and SYN-ACK, whose window is never scaled.
    [I64Field("tcp.window_size_scalefactor", "Window size scaling factor", IndexGroup = "tcp.windowsize")]
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
    [NoneField("tcp.options.user_to", "User Timeout", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUserTimeoutFieldId;

    [StringField("tcp.options.user_to_granularity", "Granularity", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUserTimeoutGranularityFieldId;

    [U64Field("tcp.options.user_to_val", "User Timeout Value", IndexGroup = _OptionsIndexGroup)]
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

    // One value field for Echo and Echo Reply (they carry the same 32-bit value), named as other analyzers name it.
    [U64Field("tcp.options.echo_value", "Echo Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoValueFieldId;

    [NoneField("tcp.options.echo_reply", "Echo Reply", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptEchoReplyFieldId;

    [NoneField("tcp.options.cc", "CC", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcFieldId;

    // One value field for CC, CC.NEW and CC.ECHO, named as other analyzers name it.
    [U64Field("tcp.options.cc_value", "CC Value", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcValueFieldId;

    [NoneField("tcp.options.cc_new", "CC.NEW", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcNewFieldId;

    [NoneField("tcp.options.cc_echo", "CC.ECHO", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptCcEchoFieldId;

    [NoneField("tcp.options.scps", "SCPS Capabilities", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsFieldId;

    [U64Field("tcp.options.scps.vector", "Capabilities Vector", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsVectorFieldId;

    [BoolField("tcp.options.scpsflags.bets", "BETS", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsBetsFieldId;

    [BoolField("tcp.options.scpsflags.snack1", "SNACK1", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsSnack1FieldId;

    [BoolField("tcp.options.scpsflags.snack2", "SNACK2", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsSnack2FieldId;

    [BoolField("tcp.options.scpsflags.compress", "COMP", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptScpsCompressFieldId;

    [BoolField("tcp.options.scpsflags.nlts", "NLTS", IndexGroup = _OptionsIndexGroup)]
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

    [NoneField("tcp.options.experimental", "Experimental", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpFieldId;

    [U64Field("tcp.options.experimental.exid", "ExID", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpExIdFieldId;

    [BytesField("tcp.options.experimental.data", "Data", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptExpDataFieldId;

    #endregion

    #region Unknown options
    [NoneField("tcp.options.unknown", "Unknown Option", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUnknownFieldId;

    [BytesField("tcp.options.unknown.payload", "Payload", IndexGroup = _OptionsIndexGroup)]
    private FieldId _OptUnknownDataFieldId;

    #endregion

    #region Optional fields

    // Numeric on purpose so a filter such as "tcp.checksum.status == 0" works as in other analyzers: 0 = bad, 1 = good,
    // 2 = unverified. The display text keeps the readable "[Good]".
    [U64Field("tcp.checksum.status", "Checksum Status", IndexGroup = "tcp.checksum.status")]
    private FieldId _ChecksumStatusFieldId;

    [BytesField("tcp.payload", "TCP Payload", IndexGroup = "tcp.payload")]
    private FieldId _PayloadFieldId;

    #endregion

    #region Error fields

    [StringField("tcp.error.no_ip", "No enclosing IP layer", IndexGroup = "tcp.error")]
    private FieldId _NoIpLayerFieldId;

    [StringField("tcp.error.stream_limit", "Stream table full", IndexGroup = "tcp.error")]
    private FieldId _StreamLimitFieldId;

    // The reassembly buffer of the application protocol hit a limit (MaxBufferSize, or MaxPduSize for the last bytes of a
    // closed stream) and dropped the message in progress. Reported on the segment that caused it, never silent.
    [StringField("tcp.error.reassembly_limit", "Reassembly limit reached", IndexGroup = "tcp.error")]
    private FieldId _ReassemblyLimitFieldId;

    // The stream lost its message boundary and the resynchronization heuristic skipped bytes to find the next one. Reported on
    // the segment whose bytes triggered it, so skipped bytes are never invisible.
    [StringField("tcp.error.resync", "Stream resynchronized", IndexGroup = "tcp.error")]
    private FieldId _ResyncFieldId;

    #endregion

    #region Stream index (always present, eagerly appended)

    [U64Field("tcp.stream", "Stream index", IndexGroup = _TcpIndexGroup)]
    private FieldId _StreamFieldId;

    #endregion

    #region TCP Analysis fields (eagerly appended, optional)

    private const string _AnalysisIndexGroup = "tcp.analysis";

    [NoneField("tcp.analysis", "SEQ/ACK analysis", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisFieldId;

    // Container for every anomaly flag below. A filter on the container name matches any segment that has at least one
    // anomaly, which is how the analysis is usually filtered; it is absent on segments without an anomaly.
    [NoneField("tcp.analysis.flags", "TCP Analysis Flags", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisFlagsFieldId;

    [U64Field("tcp.analysis.acks_frame", "This is an ACK to the segment in frame", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisAcksFrameFieldId;

    [NoneField("tcp.analysis.reused_ports", "A new tcp session is started with the same ports as an earlier session in this trace",
        IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisReusedPortsFieldId;

    [NoneField("tcp.analysis.ack_lost_segment", "ACKed segment that wasn't captured (common at capture start)",
        IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisAckLostSegmentFieldId;

    [NoneField("tcp.analysis.partial_ack", "Partial Acknowledgement of a segment", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisPartialAckFieldId;

    [NoneField("tcp.analysis.ambiguous_ack", "Ambiguous ACK following Karn's definition", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisAmbiguousAckFieldId;

    [F64Field("tcp.analysis.rto", "The RTO for this segment was", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisRtoFieldId;

    [U64Field("tcp.analysis.rto_frame", "RTO based on delta from frame", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisRtoFrameFieldId;

    [U64Field("tcp.analysis.duplicate_ack_frame", "Duplicate to the ACK in frame", IndexGroup = _AnalysisIndexGroup)]
    private FieldId _AnalysisDupAckFrameFieldId;

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

    #region Sequence end and conversation completeness (eagerly appended)

    [U64Field("tcp.nxtseq", "Next Sequence Number", IndexGroup = _TcpIndexGroup)]
    private FieldId _NxtSeqFieldId;

    private const string _CompletenessIndexGroup = "tcp.completeness";

    [U64Field("tcp.completeness", "Conversation completeness", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessFieldId;

    [StringField("tcp.completeness.str", "Completeness Flags", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessStrFieldId;

    [BoolField("tcp.completeness.syn", "SYN", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessSynFieldId;

    // Field names allow letters, digits, dots and underscores only (the filter language reads '-' as a minus), so the name of
    // this one uses an underscore where other analyzers use a hyphen.
    [BoolField("tcp.completeness.syn_ack", "SYN-ACK", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessSynAckFieldId;

    [BoolField("tcp.completeness.ack", "ACK", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessAckFieldId;

    [BoolField("tcp.completeness.data", "Data", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessDataFieldId;

    [BoolField("tcp.completeness.fin", "FIN", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessFinFieldId;

    [BoolField("tcp.completeness.rst", "RST", IndexGroup = _CompletenessIndexGroup)]
    private FieldId _CompletenessRstFieldId;

    #endregion

    #region Reassembly and segment data (eagerly appended)

    private const string _SegmentIndexGroup = "tcp.segment";

    // Payload of a segment that is not dispatched on its own: bytes buffered for a PDU that completes later, or a
    // retransmitted / reordered copy of data the application protocol already saw (or will see in order).
    [BytesField("tcp.segment_data", "TCP segment data", IndexGroup = _SegmentIndexGroup)]
    private FieldId _SegmentDataFieldId;

    // On the segment that completes a PDU: the packets that carried its bytes. tcp.segments is the container; its children
    // are one tcp.segment per contributing packet plus the count and the total length. The reverse reference (earlier
    // packets pointing at the completing one) is not available: a packet that was parsed is never amended.
    [NoneField("tcp.segments", "Reassembled TCP Segments", IndexGroup = _SegmentIndexGroup)]
    private FieldId _SegmentsFieldId;

    [U64Field("tcp.segment", "TCP Segment", IndexGroup = _SegmentIndexGroup)]
    private FieldId _SegmentFieldId;

    [U64Field("tcp.segment.count", "Segment count", IndexGroup = _SegmentIndexGroup)]
    private FieldId _SegmentCountFieldId;

    [U64Field("tcp.reassembled.length", "Reassembled TCP length", IndexGroup = _SegmentIndexGroup)]
    private FieldId _ReassembledLengthFieldId;

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

    // The key "tcp.stream_key_frame" keeps its historical name because saved settings refer to it; the value that enters the
    // stream key is the capture interface id (see _ReadFrameIdentity), not an id of a frame, so the texts say "interface".
    [BoolSetting("tcp.stream_key_frame", "Include capture interface id in the TCP stream key", "tcp", Default = false,
        Description = "Off: the same address/port tuple seen on two capture interfaces is one stream. "
            + "On: each interface id gets its own stream.")]
    private bool _StreamKeyFrame;

    // Limit: number of TCP connections the tracker keeps. Value: default 10000; values at or above int.MaxValue are clamped to
    // int.MaxValue in _RunAnalysis. Threat: every distinct tuple allocates a TcpConnectionState that lives as long as this
    // protocol instance (the stack), so a scan or SYN flood would otherwise grow memory without bound. A tracked connection
    // costs about 1 KiB idle (the unacked list is allocated on the first data segment), so the default holds roughly 10 MB of
    // idle state; reassembly buffers of connections that carry data come on top, each bounded by the protocol's own limits.
    // Configurable (setting, changed by the operator/user), not a constant, because the right size depends on the host and
    // the capture. Posture: secure by default (a tight default; raising it is opt-in), because the input comes from
    // untrusted networks. Rejected alternative: a default derived from the host's memory (it would make the same capture
    // behave differently on two machines). Exceeding it is an expected failure, reported per packet as
    // tcp.error.stream_limit, never a silent drop; the application layer of such a connection stays visible (see _ParseBody).
    [U64Setting("tcp.max_tracked_streams", "Maximum tracked TCP streams", "tcp", Default = 10000,
        Description = "Upper bound for the number of tracked TCP connections. A new connection beyond it keeps its header "
            + "and a stateless dispatch of its payload, but gets no stream index, no analysis and no reassembly, and is "
            + "flagged; raising the value lets a flood of distinct address/port pairs use more memory (about 1 KiB per "
            + "connection plus its reassembly buffers).")]
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

    // Same reason for the lazy tcp.completeness container (one delegate per stack, not per packet).
    private LazyPopulator _CompletenessPopulator = null!;

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
        _CompletenessPopulator = _PopulateCompleteness;
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
            Cc = _OptCcFieldId,
            CcValue = _OptCcValueFieldId,
            CcNew = _OptCcNewFieldId,
            CcEcho = _OptCcEchoFieldId,
            Scps = _OptScpsFieldId,
            ScpsVector = _OptScpsVectorFieldId,
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

        if (!TcpHeader.TryParse(tcpData.Span, out TcpHeader header))
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, TcpHeader.MinSize, (ulong)tcpData.Length);
        }

        // tcp.srcport and tcp.dstport are eagerly appended by Parse() so checksum
        // validation and stream tracking can read ports without materialising the lazy TCP group.
        // The any-match name "tcp.port" is exposed via the alias group registered in
        // _RegisterFieldsCustom; no duplicate tcp.port field node is appended.

        // Checksum: displayed as hex from the precomputed table (no per-packet string formatting).
        string csumText = DisplayTables.FormatHexU16(header.Checksum);
        container.AppendWithCustomText(_ChecksumFieldId, FieldValue.NewU64(header.Checksum), csumText);

        // Optional verification. Field order matters: tcp.checksum.status directly follows tcp.checksum (the index group is
        // recorded in _ParseBody under the same _VerifyChecksum condition, so the setting must not change between the two).
        if (_VerifyChecksum)
        {
            bool? valid = _ValidateChecksum(in container, tcpData.Span);
            ulong status;
            string statusText;
            switch (valid)
            {
                case true:
                    status = _ChecksumGood;
                    statusText = "[Good]";
                    break;
                case false:
                    status = _ChecksumBad;
                    statusText = "[Bad]";
                    break;
                default:
                    status = _ChecksumUnverified;
                    statusText = "[Unverified]";
                    break;
            }

            container.AppendWithCustomText(_ChecksumStatusFieldId, FieldValue.NewU64(status), statusText);
        }

        // Header length and payload length. _ParseBody has already rejected data offsets below 5 and headers longer than the
        // data, so headerLen <= tcpData.Length holds here and the slices below cannot go out of range.
        int headerLen = header.HeaderLength;
        int payloadLen = Math.Max(0, tcpData.Length - headerLen);

        // tcp.seq and tcp.ack are appended eagerly in Parse so relative values survive replay.
        // tcp.seq_raw / tcp.ack_raw stay here as the wire values.
        container.Append(_SeqRawFieldId, FieldValue.NewU64(header.SeqNumber));
        container.Append(_AckRawFieldId, FieldValue.NewU64(header.AckNumber));

        container.Append(_HdrLenFieldId, FieldValue.NewU64((ulong)headerLen));

        // Flags: header byte 13 plus the low four bits of byte 12. CWR/ECE are the ECN bits (RFC 3168), the other six are the
        // classic RFC 793 control bits. AE (the former NS bit, lowest bit of byte 12) and the three reserved bits above it
        // are part of the 12-bit flags field, so the field value carries them too and a filter on the whole field sees them.
        // The display text prints three hex digits ("0x018") because the field is 12 bits wide.
        byte flags = header.Flags;
        string flagsText = TcpFlagsFormatter.Format(flags);
        ushort flags12 = (ushort)(((header.Reserved << 1 | header.NsFlag) << 8) | flags);
        container.AppendWithCustomText(_FlagsFieldId,
            FieldValue.NewU64(flags12), Helpers.DisplayTables.FormatHexU12(flags12), " [", flagsText, "]");

        container.Append(_FlagsCwrFieldId, FieldValue.NewBool((flags & 0x80) != 0));
        container.Append(_FlagsEceFieldId, FieldValue.NewBool((flags & 0x40) != 0));
        container.Append(_FlagsUrgFieldId, FieldValue.NewBool((flags & 0x20) != 0));
        container.Append(_FlagsAckFieldId, FieldValue.NewBool((flags & 0x10) != 0));
        container.Append(_FlagsPushFieldId, FieldValue.NewBool((flags & 0x08) != 0));
        container.Append(_FlagsResetFieldId, FieldValue.NewBool((flags & 0x04) != 0));
        container.Append(_FlagsSynFieldId, FieldValue.NewBool((flags & 0x02) != 0));
        container.Append(_FlagsFinFieldId, FieldValue.NewBool((flags & 0x01) != 0));
        container.Append(_FlagsAeFieldId, FieldValue.NewBool(header.NsFlag != 0));
        container.Append(_FlagsResFieldId, FieldValue.NewU64(header.Reserved));

        container.Append(_WindowFieldId, FieldValue.NewU64(header.WindowSize));
        container.Append(_UrgentPointerFieldId, FieldValue.NewU64(header.UrgentPointer));

        // TCP Options (between fixed 20-byte header and payload). The span handed to the parser ends at the header
        // length, so option parsing is confined to the header and cannot run into the payload.
        if (headerLen > TcpHeader.MinSize)
        {
            int optionsLen = headerLen - TcpHeader.MinSize;
            ReadOnlySpan<byte> optionsData = tcpData.Span.Slice(TcpHeader.MinSize, optionsLen);

            MutField optionsContainer = container.AppendWithCustomText(
                _OptionsFieldId, FieldValue.None,
                // Eager text (one string): the field tree stores it right away; InvariantCulture per the repository rule.
                string.Create(CultureInfo.InvariantCulture, $"Options: ({optionsLen} bytes)"));

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
    /// (<see cref="_Gate"/>) separates both paths. A first parse that ends in an exception
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

        // First parse or replay is decided only by the ingest gate; there is deliberately no caller-supplied mode flag.
        // TCP can be entered again while the same packet is still being parsed (nested layers, for example tunnels).
        // The watermark is raised only by the outermost scope, so an inner layer of the same packet still sees its own
        // packet id as "not yet parsed" and takes the first-parse path too. A replay holds the inert default scope.
        bool isReplay = _Gate.IsReplay(in context, packet.Id);
        IngestScope scope = isReplay
            ? default
            : _Gate.Enter(in context, packet.Id);

        // The scope must end in finally, also after errors and exceptions: otherwise a later parse of this id would
        // re-enter the first-parse path and mutate the tracker a second time.
        try
        {
            return _ParseBody(in parentField, data, in context, layerKey, isReplay);
        }
        finally
        {
            scope.Dispose();
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
        if (!TcpHeader.TryParse(span, out TcpHeader header))
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

        ushort srcPort = header.SrcPort;
        ushort dstPort = header.DstPort;
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

        // Summary text. flagsText comes from a precomputed 256-entry table (no allocation). The packet info is set below, after
        // the analysis: it shows the displayed (relative) numbers, which only the analysis knows.
        string flagsText = TcpFlagsFormatter.Format(flags);

        // Store full TCP segment (header + payload) for the lazy populator. The custom representation ("32 bytes") is only
        // built when the packet wants custom text (tree or cache needs it); on a skip-mode parse this saves the allocation.
        FieldValue containerValue = FieldValue.NewBytes(data);
        if (parentField.WantsCustomText(_ProtocolFieldId))
        {
            containerValue = containerValue.WithCustomRepresentation(LazyString.FormatLazy(
                headerLen, static s => string.Create(CultureInfo.InvariantCulture, $"{s} bytes")));
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
            in parentField, srcPort, dstPort, header.SeqNumber, header.AckNumber,
            flags, header.WindowSize, payloadLen, span, in context, layerKey, isReplay);

        // Relative numbers are shown only when the analysis knows the ISN; otherwise the wire value is displayed.
        uint shownSeq = analysis.SequenceIsRelative ? analysis.RelativeSeq : header.SeqNumber;
        uint shownAck = analysis.AckIsRelative ? analysis.RelativeAck : header.AckNumber;
        tcpContainer.Append(_SeqFieldId, FieldValue.NewU64(shownSeq));
        tcpContainer.Append(_AckFieldId, FieldValue.NewU64(shownAck));

        // tcp.nxtseq: the sequence number the next segment of this direction should carry. SYN and FIN each occupy one number.
        // It follows the displayed numbering (relative when tcp.seq is relative), so seq and nxtseq of one packet are
        // comparable. The sum wraps modulo 2^32 like the sequence space itself.
        bool isSyn = (flags & 0x02) != 0;
        bool isFin = (flags & 0x01) != 0;
        uint nextSeq = unchecked(shownSeq + (uint)payloadLen + (isSyn ? 1u : 0u) + (isFin ? 1u : 0u));
        tcpContainer.Append(_NxtSeqFieldId, FieldValue.NewU64(nextSeq));

        // The advertised window is shown on every segment: scaled when the sender's scale is known, otherwise the raw value.
        // An analysis without a window (no IP layer, full stream table, replay without a recorded effect) carries 0, which
        // can only mean "not computed" for a non-zero header value, so the header value is shown then.
        ulong windowSize = analysis.ScaledWindowSize == 0 && header.WindowSize != 0
            ? header.WindowSize
            : analysis.ScaledWindowSize;

        // Packet info: ports, flags, displayed numbers, window and payload length. Built lazily from value-typed state, so the
        // text exists only if somebody reads the info column. The ACK number is left out when the ACK flag is clear.
        parentField.SetPacketInfo(LazyString.FormatLazy(
            (srcPort, dstPort, flags, shownSeq, shownAck, windowSize, payloadLen),
            static s => _FormatPacketInfo(s.srcPort, s.dstPort, s.flags, s.shownSeq, s.shownAck, s.windowSize, s.payloadLen)));

        // Stream table full (see tcp.max_tracked_streams): the header stays visible and the condition is reported as a field,
        // never dropped silently, but there is no stream state, so no tcp.stream, no analysis and no reassembly. The payload
        // is still dispatched (stateless, by port or by content): the limit protects memory, and an HTTP request line or a TLS
        // hello needs no connection state, so the application layer of an untracked connection stays visible
        // (a limit must not cripple the host). The effect carries StreamLimitReached, so a replay reproduces this outcome even
        // if the table has room later, instead of asking the tracker again.
        if (analysis.StreamLimitReached)
        {
            parentField.Append(_StreamLimitFieldId, FieldValue.NewString(
                "TCP stream table is full (tcp.max_tracked_streams): no stream index, analysis or reassembly for this connection"));
            context.RecordGroupPresence(_TcpErrorGroupId);
        }
        else
        {
            // Eagerly append tcp.stream as sibling of the lazy TCP container
            parentField.Append(_StreamFieldId, FieldValue.NewU64(analysis.StreamIndex));
        }

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

        // Window: tcp.window_size is on every segment; the scaling factor is on every segment except SYN and SYN-ACK, whose
        // window is never scaled (-1 unknown, -2 no scaling, otherwise the multiplier).
        parentField.Append(_WindowSizeFieldId, FieldValue.NewU64(windowSize));
        context.RecordGroupPresence(_TcpWindowsizeGroupId);
        if (!isSyn)
        {
            parentField.Append(_WindowScaleFactorFieldId, FieldValue.NewI64(analysis.WindowScaleFactor));
        }

        // Eagerly append analysis and completeness fields. The analysis container always carries at least
        // tcp.analysis.connection_state, so the group is recorded whenever the connection was tracked. A full stream table and
        // a missing IP layer are the untracked cases: they have no connection state to describe.
        if (!analysis.NoIpLayer && !analysis.StreamLimitReached)
        {
            context.RecordGroupPresence(_TcpAnalysisGroupId);
            _AppendAnalysisFields(in parentField, in analysis, in context);
            _AppendCompletenessFields(in parentField, analysis.Completeness, in context);
        }

        // Dispatch by port on parentField (sibling of the TCP container, not a child), with reassembly and heuristic fallback.
        // Every segment goes through the dispatch, also a pure ACK or FIN without payload: a FIN or RST must reach the
        // reassembly engine to flush or drop the stream, and the recorded effect must exist for every first parse.
        ReadOnlyMemory<byte> payload = data[headerLen..];
        return _DispatchTcpPayload(
            in parentField, srcPort, dstPort, header.SeqNumber, flags, payload, data.Length, in context, in analysis, layerKey, isReplay);
    }

    /// <summary>
    /// Builds the packet info text: <c>49152 → 80 [PSH, ACK] Seq=1 Ack=1 Win=65535 Len=100</c>. Runs only when the info
    /// column is read. The ACK number is left out when the ACK flag is clear, because the field is meaningless then.
    /// </summary>
    private static string _FormatPacketInfo(
        ushort srcPort, ushort dstPort, byte flags, uint seq, uint ack, ulong window, int length)
    {
        string flagsText = TcpFlagsFormatter.Format(flags);
        if ((flags & 0x10) == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"{srcPort} → {dstPort} [{flagsText}] Seq={seq} Win={window} Len={length}");
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"{srcPort} → {dstPort} [{flagsText}] Seq={seq} Ack={ack} Win={window} Len={length}");
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
