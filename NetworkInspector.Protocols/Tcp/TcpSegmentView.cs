// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// The facts of one TCP segment that the analysis steps read, computed once and passed by reference to every step.
/// </summary>
/// <remarks>
/// Motivation: the analysis consists of many small steps (sequence classification, window checks, round trip sampling,
/// state machine) that all look at the same dozen header values. Passing them as separate parameters made one method with
/// twelve parameters and seven locals that every region read; a view struct lets each step be a short method named after
/// the question it answers. It is a <c>readonly struct</c> passed with <c>in</c>, so there is no copy and no allocation.
/// Thread safety: immutable value type.
/// </remarks>
/// <param name="Seq">Sequence number of the first payload byte (or of the SYN/FIN).</param>
/// <param name="AckNumber">Acknowledgement number.</param>
/// <param name="PayloadLength">Number of payload bytes.</param>
/// <param name="Flags">TCP flags byte (FIN 0x01, SYN 0x02, RST 0x04, PSH 0x08, ACK 0x10).</param>
/// <param name="Window">Advertised window, not scaled.</param>
/// <param name="Nanos">Capture time in nanoseconds.</param>
/// <param name="PacketId">Id of the packet that carries the segment.</param>
/// <param name="SrcPort">Source port.</param>
/// <param name="DstPort">Destination port.</param>
/// <param name="WindowScaleOption">Shift count of a Window Scale option in this SYN, or -1 when the segment has none.</param>
internal readonly record struct TcpSegmentView(
    uint Seq,
    uint AckNumber,
    uint PayloadLength,
    byte Flags,
    ushort Window,
    long Nanos,
    int PacketId,
    ushort SrcPort,
    ushort DstPort,
    int WindowScaleOption)
{
    /// <summary>The SYN flag.</summary>
    internal bool Syn => (Flags & 0x02) != 0;

    /// <summary>The ACK flag.</summary>
    internal bool HasAck => (Flags & 0x10) != 0;

    /// <summary>The FIN flag.</summary>
    internal bool Fin => (Flags & 0x01) != 0;

    /// <summary>The RST flag.</summary>
    internal bool Rst => (Flags & 0x04) != 0;

    /// <summary>SYN and FIN each occupy one sequence number.</summary>
    internal bool SynOrFin => (Flags & 0x03) != 0;

    /// <summary>
    /// Sequence number after this segment. Unsigned arithmetic on purpose: sequence numbers wrap at 2^32, and every
    /// ordering test uses serial-number comparison, never plain &lt; or &gt;.
    /// </summary>
    internal uint NextSeq => Seq + PayloadLength + (Syn ? 1u : 0u) + (Fin ? 1u : 0u);

    /// <summary>No SYN, FIN or RST: the segment is an ordinary data or ACK segment (the checks below skip control segments).</summary>
    internal bool IsPlain => (Flags & 0x07) == 0;
}
