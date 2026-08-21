// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Single Source of Truth describing one Signal Message as a structured bitfield
/// layout. The same in-memory definition feeds three consumers in a test:
/// <list type="bullet">
///   <item>the FrameBuilder <see cref="SignalMessageLayer"/>'s bitfield encoder,</item>
///   <item>the parser-side JSON config (via the test bridge),</item>
///   <item>the tshark UAT profile generator.</item>
/// </list>
/// </summary>
/// <remarks>
/// The layout mirrors the parser-side
/// <c>SignalMessageConfig</c> JSON model verbatim but uses typed enums
/// (<see cref="SignalEndian"/>) instead of string fields. This lets the
/// FrameBuilder pick a meaningful encoder at compile time without a
/// string-switch on every byte.
/// <para>Thread safety: instances are immutable after construction.</para>
/// </remarks>
public sealed class SignalMessageLayout
{
    /// <summary>Machine-readable message protocol name (unique; also the container field name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Human-readable message UI name for the container field.</summary>
    public string UiName { get; init; } = string.Empty;

    /// <summary>Optional legacy tag; not serialized to parser JSON.</summary>
    public uint PduId
    {
        get; init;
    }

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
    public ImmutableArray<DispatchBinding> DispatchBindings { get; init; } = [];
}

/// <summary>
/// Definition of a single signal within a <see cref="SignalMessageLayout"/>:
/// bit position, encoding, scaling and (optionally) discrete value names.
/// </summary>
/// <remarks>
/// The bit-numbering follows the dissector convention in <c>SignalMessageProtocol</c>:
/// for big-endian, <see cref="StartBit"/> is the MSB position; for little-endian, it is the LSB position.
/// </remarks>
/// <param name="Name">Layout-local encoder key. The test JSON bridge writes the parser field name as <c>{message}.{Name}</c>.</param>
/// <param name="UiName">Human-readable signal UI name written into JSON <c>ui_name</c>.</param>
/// <param name="StartBit">Bit position of the signal start (MSB for big-endian, LSB for little-endian).</param>
/// <param name="BitLength">Number of bits to encode (1-64).</param>
/// <param name="Endian">Byte / bit order the signal uses on the wire.</param>
/// <param name="Factor">Linear scaling factor: physical = raw * factor + offset.</param>
/// <param name="Offset">Linear scaling offset: physical = raw * factor + offset.</param>
/// <param name="Unit">Physical unit string (e.g. <c>"rpm"</c>, <c>"°C"</c>); pass-through to parser/UAT.</param>
public readonly record struct SignalSpec(
    string Name,
    string UiName,
    int StartBit,
    int BitLength,
    SignalEndian Endian,
    double Factor,
    double Offset,
    string Unit)
{
    #region Properties

    /// <summary>
    /// Optional map raw integer value → display name (e.g. <c>0 → "Off"</c>);
    /// passed through to the parser config and the tshark UAT.
    /// </summary>
    internal ImmutableDictionary<ulong, string>? ValueNames { get; init; }

    #endregion
}

/// <summary>
/// Multiplexer selector: a small bit-field within the same PDU that selects
/// which <see cref="MuxGroupSpec"/> is currently active.
/// </summary>
/// <param name="Name">Layout-local mux encoder key. The test JSON bridge writes the parser field name as <c>{message}.{Name}</c>.</param>
/// <param name="UiName">Multiplexer UI name written into JSON <c>ui_name</c>.</param>
/// <param name="StartBit">Bit position of the selector (same convention as <see cref="SignalSpec.StartBit"/>).</param>
/// <param name="BitLength">Number of bits in the selector (1-64).</param>
/// <param name="Endian">Byte / bit order used by the selector.</param>
public readonly record struct MuxSpec(
    string Name,
    string UiName,
    int StartBit,
    int BitLength,
    SignalEndian Endian);

/// <summary>
/// A group of mux-conditional signals: only rendered when the multiplexer
/// selector equals <see cref="MuxValue"/>.
/// </summary>
/// <param name="MuxValue">Selector value that activates this group.</param>
public readonly record struct MuxGroupSpec(ulong MuxValue)
{
    #region Properties

    /// <summary>Signals contained in this group.</summary>
    internal ImmutableArray<SignalSpec> Signals { get; init; }

    #endregion
}

/// <summary>
/// Parser-table binding for dynamic dispatch registration.
/// </summary>
/// <remarks>
/// Mirrors the parser's <c>dispatch_bindings</c> list and the tshark dissector's
/// dispatch-table preferences: the same binding is emitted in three places
/// to keep all consumers in lock-step.
/// </remarks>
/// <param name="Table">Dispatch-table name (e.g. <c>"can.id"</c>, <c>"pdu_transport.id"</c>, <c>"udp.port"</c>).</param>
/// <param name="Key">Key value at which this PDU is registered.</param>
public readonly record struct DispatchBinding(string Table, ulong Key);

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
