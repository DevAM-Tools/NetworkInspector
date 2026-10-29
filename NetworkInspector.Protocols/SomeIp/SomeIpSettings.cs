// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The effective SOME/IP settings, read once from the settings manager when the stack is built and immutable afterwards.
/// <para>
/// The parser never asks the settings manager while it parses: a value is read at registration, validated by the manager against
/// the range of its definition (see <see cref="SomeIpRegistration"/>), narrowed to the type the parser needs, and kept here. That
/// makes every parse thread read the same plain fields, makes a parse independent of a setting that changes later (a stack is
/// built with the values of its time), and keeps the hot path free of dictionary lookups. Rejected alternative: reading the
/// settings per packet, which would let one packet see two values of the same setting and cost a lookup per message.
/// </para>
/// <para>
/// The ten limits carry the posture of their definition in <see cref="SomeIpLimits"/>: every default is secure and loosening is
/// opt-in. The switches default to the cheapest and least surprising state: decoding of configured payloads on; everything that
/// costs state, needs guessing or can mistake unrelated traffic for SOME/IP (timing, learning, content-based detection) off, and
/// framing resynchronization on because a TCP stream that lost its position would otherwise stay undecoded to its end.
/// </para>
/// <para><b>Thread-safety:</b> immutable value; safe to copy and read from any thread.</para>
/// </summary>
/// <param name="DecodePayload">Decode configured payloads into typed fields (<c>someip.decode_payload</c>).</param>
/// <param name="ShowTypeMetadata">Add type, length, padding and tag fields to a decoded payload (<c>someip.show_type_metadata</c>).</param>
/// <param name="VerifyE2eCrc">Compute and compare the end-to-end protection checksum (<c>someip.verify_e2e_crc</c>).</param>
/// <param name="MethodTiming">Pair requests with their responses and show the delay (<c>someip.method_timing</c>).</param>
/// <param name="MethodTimingIgnoreInterface">Pair across capture interfaces (<c>someip.method_timing_ignore_interface</c>).</param>
/// <param name="LearnSdPorts">Decode UDP ports that service discovery announced (<c>someip.learn_sd_ports</c>).</param>
/// <param name="LearnSdPortsIgnore">Ports that are never learned (<c>someip.learn_sd_ports_ignore</c>).</param>
/// <param name="Heuristic">Decode plausible SOME/IP on any port (<c>someip.heuristic</c>).</param>
/// <param name="TcpResync">Find the next message after a stream lost its position (<c>someip.tcp_resync</c>).</param>
/// <param name="DetectDtls">Hand datagrams that look like DTLS to the DTLS parser (<c>someip.detect_dtls</c>).</param>
/// <param name="MaxMessagesPerUnit">Messages decoded from one unit (<c>someip.max_messages_per_unit</c>).</param>
/// <param name="MaxTypeDepth">Nesting depth of a payload type (<c>someip.max_type_depth</c>).</param>
/// <param name="MaxPayloadNodes">Decoded nodes per payload (<c>someip.max_payload_nodes</c>).</param>
/// <param name="TpMaxSessions">Open segmented-message sessions (<c>someip.tp_max_sessions</c>).</param>
/// <param name="TpMaxMessageSize">Size of one segmented message in bytes (<c>someip.tp_max_message_size</c>).</param>
/// <param name="TpMaxBufferedBytes">Bytes held by all open sessions (<c>someip.tp_max_buffered_bytes</c>).</param>
/// <param name="TcpMaxMessageSize">Size of one message of a TCP stream in bytes (<c>someip.tcp_max_message_size</c>).</param>
/// <param name="ConfigMaxServices">Services accepted from a configuration file (<c>someip.config_max_services</c>).</param>
/// <param name="ConfigMaxTypes">Types accepted from a configuration file (<c>someip.config_max_types</c>).</param>
/// <param name="TimingMaxPending">Unanswered requests kept for timing (<c>someip.timing_max_pending</c>).</param>
public readonly record struct SomeIpSettings(
    bool DecodePayload,
    bool ShowTypeMetadata,
    bool VerifyE2eCrc,
    bool MethodTiming,
    bool MethodTimingIgnoreInterface,
    bool LearnSdPorts,
    ReadOnlyMemory<ushort> LearnSdPortsIgnore,
    bool Heuristic,
    bool TcpResync,
    bool DetectDtls,
    int MaxMessagesPerUnit,
    int MaxTypeDepth,
    int MaxPayloadNodes,
    int TpMaxSessions,
    int TpMaxMessageSize,
    long TpMaxBufferedBytes,
    int TcpMaxMessageSize,
    int ConfigMaxServices,
    int ConfigMaxTypes,
    int TimingMaxPending)
{
    /// <summary>
    /// Every setting at its registered default. This is what a stack gets without a preloaded value, and what a test that builds
    /// the protocol directly starts from. The numbers come from <see cref="SomeIpLimits"/>, so there is one place that defines a
    /// limit's default.
    /// </summary>
    public static SomeIpSettings Default { get; } = new(
        DecodePayload: true,
        ShowTypeMetadata: false,
        VerifyE2eCrc: false,
        MethodTiming: false,
        MethodTimingIgnoreInterface: false,
        LearnSdPorts: false,
        LearnSdPortsIgnore: ReadOnlyMemory<ushort>.Empty,
        Heuristic: false,
        TcpResync: true,
        DetectDtls: false,
        MaxMessagesPerUnit: (int)SomeIpLimits.MaxMessagesPerUnit.Default,
        MaxTypeDepth: (int)SomeIpLimits.MaxTypeDepth.Default,
        MaxPayloadNodes: (int)SomeIpLimits.MaxPayloadNodes.Default,
        TpMaxSessions: (int)SomeIpLimits.TpMaxSessions.Default,
        TpMaxMessageSize: (int)SomeIpLimits.TpMaxMessageSize.Default,
        TpMaxBufferedBytes: (long)SomeIpLimits.TpMaxBufferedBytes.Default,
        TcpMaxMessageSize: (int)SomeIpLimits.TcpMaxMessageSize.Default,
        ConfigMaxServices: (int)SomeIpLimits.ConfigMaxServices.Default,
        ConfigMaxTypes: (int)SomeIpLimits.ConfigMaxTypes.Default,
        TimingMaxPending: (int)SomeIpLimits.TimingMaxPending.Default);
}
