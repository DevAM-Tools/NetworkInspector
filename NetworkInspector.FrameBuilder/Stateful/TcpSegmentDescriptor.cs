// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Mutable, typed description of a TCP segment that
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/> is about to emit.
/// All fields are pre-populated with the connection's defaults for the
/// current <see cref="TcpSegmentContext.Phase"/>; an optional
/// <see cref="TcpSegmentMutator"/> may modify any of them before the
/// segment is serialised.
/// </summary>
/// <remarks>
/// <para>
/// Mutations affect the wire bytes 1:1 — Flag changes feed the SEQ/ACK
/// bookkeeping (SYN and FIN both consume one sequence number), Window /
/// UrgentPointer feed the corresponding header fields, and the resulting
/// header is fed into the pseudo-header / checksum pass after the
/// mutator returns.  The <see cref="Payload"/> span is read-only and
/// owned by the caller of WriteFromClient/Server (or by the
/// <see cref="IStreamProducer"/> buffer); it must not be modified.
/// </para>
/// <para>
/// Thread safety: not thread-safe.  Each mutator invocation receives a
/// fresh stack-allocated descriptor; concurrent emission across
/// connections is safe per-instance only.
/// </para>
/// </remarks>
public ref struct TcpSegmentDescriptor
{
    /// <summary>Sequence number to write into the segment. Default: connection's next-SEQ for this direction.</summary>
    public uint Sequence
    {
        get; set;
    }

    /// <summary>Acknowledgment number to write into the segment. Default: peer's next expected SEQ.</summary>
    public uint Acknowledgment
    {
        get; set;
    }

    /// <summary>TCP control flags to set. Default: lifecycle-appropriate (SYN / SYN+ACK / ACK / FIN+ACK / RST / PSH+ACK).</summary>
    public byte Flags
    {
        get; set;
    }

    /// <summary>Receive window to advertise. Default: <see cref="TcpConnectionOptions.WindowSize"/>.</summary>
    public ushort WindowSize
    {
        get; set;
    }

    /// <summary>Urgent pointer. Default: 0.</summary>
    public ushort UrgentPointer
    {
        get; set;
    }

    /// <summary>Read-only application payload slice for this segment (empty for control segments).</summary>
    public ReadOnlySpan<byte> Payload
    {
        get; set;
    }
}

/// <summary>
/// Read-only context handed to a <see cref="TcpSegmentMutator"/> alongside
/// the mutable <see cref="TcpSegmentDescriptor"/>.  Identifies which
/// segment within which lifecycle phase is being emitted, so a single
/// mutator can branch on direction, phase, or position within a Write.
/// </summary>
public readonly ref struct TcpSegmentContext
{
    /// <summary>Direction of the segment (client→server or server→client).</summary>
    public TcpDirection Direction
    {
        get; init;
    }

    /// <summary>Lifecycle phase that produced this segment.</summary>
    public TcpLifecycle Phase
    {
        get; init;
    }

    /// <summary>Zero-based index of this segment inside the current Emit/Write call.</summary>
    public int SegmentIndex
    {
        get; init;
    }

    /// <summary>Total number of segments the current Emit/Write call will produce.</summary>
    public int SegmentCount
    {
        get; init;
    }

    /// <summary>Effective Maximum Segment Size used to split this Write call.</summary>
    public ushort Mss
    {
        get; init;
    }
}

/// <summary>
/// Optional pre-serialise mutator hook for <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.
/// Invoked once per emitted segment, after defaults have been populated
/// but BEFORE any byte is written.  Mutations to <paramref name="segment"/>
/// flow through the SEQ/ACK accounting (SYN/FIN add their +1 increment),
/// the header writer, and the checksum computation transparently.
/// </summary>
/// <remarks>
/// <para>
/// Adding the FIN flag to a data-carrying segment (i.e. a segment produced
/// by a Write call that already has a non-empty payload) causes the
/// connection to automatically advance the peer's expected ACK by
/// <c>payload.Length + 1</c> — the full sequence space consumed by the
/// combined data and FIN.
/// </para>
/// </remarks>
/// <param name="segment">Mutable descriptor; payload is read-only.</param>
/// <param name="context">Direction / phase / index information.</param>
public delegate void TcpSegmentMutator(ref TcpSegmentDescriptor segment, in TcpSegmentContext context);
