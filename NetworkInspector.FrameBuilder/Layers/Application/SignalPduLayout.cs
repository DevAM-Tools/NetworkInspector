// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Single Source of Truth describing one Signal-PDU as a structured bitfield
/// layout. The same in-memory definition feeds three consumers in a test:
/// <list type="bullet">
///   <item>the FrameBuilder <see cref="SignalPduLayer"/>'s bitfield encoder,</item>
///   <item>the parser-side JSON config (via the test bridge),</item>
///   <item>the tshark UAT profile generator.</item>
/// </list>
/// </summary>
/// <remarks>
/// The layout mirrors the parser-side
/// <c>SignalPduDefinition</c> JSON model verbatim but uses typed enums
/// (<see cref="SignalEndian"/>, <see cref="SignalType"/>) instead of string
/// fields. This lets the FrameBuilder pick a meaningful encoder at compile
/// time without a string-switch on every byte.
/// <para>Thread safety: instances are immutable after construction.</para>
/// </remarks>
public sealed class SignalPduLayout
{
    /// <summary>Unique PDU identifier used for parser dispatch.</summary>
    public uint PduId
    {
        get; init;
    }

    /// <summary>Human-readable PDU name (also written into the parser config).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Total wire-length of the PDU payload in bytes.</summary>
    public int ByteLength
    {
        get; init;
    }

    /// <summary>Static signals — always present in every emitted frame.</summary>
    public ImmutableArray<SignalSpec> Signals { get; init; } = [];

    /// <summary>
    /// Optional multiplexer selector: when set, exactly one of the
    /// <see cref="MuxGroups"/> is rendered in addition to the static
    /// <see cref="Signals"/>, picked by the selector value at runtime.
    /// </summary>
    public MuxSpec? Mux
    {
        get; init;
    }

    /// <summary>Multiplexer groups; only the group matching the active selector value is rendered.</summary>
    public ImmutableArray<MuxGroupSpec> MuxGroups { get; init; } = [];

    /// <summary>
    /// Dispatch-table bindings (e.g. <c>can.id</c>, <c>pdu_transport.id</c>,
    /// <c>udp.port</c>) under which the parser shall register this PDU. The
    /// FrameBuilder itself does not consume these; the test bridge writes
    /// them into the parser JSON and the UAT profile so all three sides
    /// agree on dispatch.
    /// </summary>
    public ImmutableArray<DispatchBinding> RegisterAt { get; init; } = [];
}

/// <summary>
/// Definition of a single signal within a <see cref="SignalPduLayout"/>:
/// bit position, encoding, scaling and (optionally) discrete value names.
/// </summary>
/// <remarks>
/// The bit-numbering follows the dissector convention reproduced in
/// <c>SignalDecoder</c>: for big-endian, <see cref="StartBit"/> is the
/// MSB position; for little-endian, it is the LSB position.
/// </remarks>
public readonly struct SignalSpec
{
    /// <summary>Signal name; serves as key in <see cref="SignalValueSet"/>.</summary>
    public string Name
    {
        get; init;
    }

    /// <summary>Bit position of the signal start (MSB for big-endian, LSB for little-endian).</summary>
    public int StartBit
    {
        get; init;
    }

    /// <summary>Number of bits to encode (1-64).</summary>
    public int BitLength
    {
        get; init;
    }

    /// <summary>Byte / bit order the signal uses on the wire.</summary>
    public SignalEndian Endian
    {
        get; init;
    }

    /// <summary>Numeric type of the signal (drives raw-bit interpretation and writeback).</summary>
    public SignalType Type
    {
        get; init;
    }

    /// <summary>Linear scaling factor: physical = raw * factor + offset.</summary>
    public double Factor
    {
        get; init;
    }

    /// <summary>Linear scaling offset: physical = raw * factor + offset.</summary>
    public double Offset
    {
        get; init;
    }

    /// <summary>Physical unit string (e.g. <c>"rpm"</c>, <c>"°C"</c>); pass-through to parser/UAT.</summary>
    public string Unit
    {
        get; init;
    }

    /// <summary>
    /// Optional map raw integer value → display name (e.g. <c>0 → "Off"</c>);
    /// passed through to the parser config and the tshark UAT.
    /// </summary>
    internal ImmutableDictionary<ulong, string>? ValueNames
    {
        get; init;
    }
}

/// <summary>
/// Multiplexer selector: a small bit-field within the same PDU that selects
/// which <see cref="MuxGroupSpec"/> is currently active.
/// </summary>
public readonly struct MuxSpec
{
    /// <summary>Multiplexer signal name.</summary>
    public string Name
    {
        get; init;
    }

    /// <summary>Bit position of the selector (same convention as <see cref="SignalSpec.StartBit"/>).</summary>
    public int StartBit
    {
        get; init;
    }

    /// <summary>Number of bits in the selector (1-64).</summary>
    public int BitLength
    {
        get; init;
    }

    /// <summary>Byte / bit order used by the selector.</summary>
    public SignalEndian Endian
    {
        get; init;
    }
}

/// <summary>
/// A group of mux-conditional signals: only rendered when the multiplexer
/// selector equals <see cref="MuxValue"/>.
/// </summary>
public readonly struct MuxGroupSpec
{
    /// <summary>Selector value that activates this group.</summary>
    public ulong MuxValue
    {
        get; init;
    }

    /// <summary>Signals contained in this group.</summary>
    internal ImmutableArray<SignalSpec> Signals
    {
        get; init;
    }
}

/// <summary>
/// Parser-table binding for dynamic dispatch registration.
/// </summary>
/// <remarks>
/// Mirrors the parser's <c>register_at</c> list and the tshark dissector's
/// dispatch-table preferences: the same binding is emitted in three places
/// to keep all consumers in lock-step.
/// </remarks>
public readonly struct DispatchBinding
{
    /// <summary>Dispatch-table name (e.g. <c>"can.id"</c>, <c>"pdu_transport.id"</c>, <c>"udp.port"</c>).</summary>
    public string Table
    {
        get; init;
    }

    /// <summary>Key value at which this PDU is registered.</summary>
    public ulong Key
    {
        get; init;
    }
}

/// <summary>
/// Byte / bit order convention used by a signal or multiplexer selector.
/// </summary>
public enum SignalEndian : byte
{
    /// <summary>Motorola order; <c>start_bit</c> is the MSB position.</summary>
    Big = 0,

    /// <summary>Intel order; <c>start_bit</c> is the LSB position.</summary>
    Little = 1,
}

/// <summary>
/// Numeric type of a signal; drives both raw-bit interpretation and the
/// FrameBuilder writeback.
/// </summary>
public enum SignalType : byte
{
    /// <summary>Plain unsigned integer (raw is taken verbatim).</summary>
    Unsigned = 0,

    /// <summary>Two's-complement signed integer.</summary>
    Signed = 1,

    /// <summary>32-bit IEEE 754 (only valid with <see cref="SignalSpec.BitLength"/> = 32).</summary>
    F32 = 2,

    /// <summary>64-bit IEEE 754 (only valid with <see cref="SignalSpec.BitLength"/> = 64).</summary>
    F64 = 3,
}
