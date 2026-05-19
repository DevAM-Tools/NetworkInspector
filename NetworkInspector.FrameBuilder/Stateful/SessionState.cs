// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Per-session, mutable state for stateful layers in a <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The struct holds one slot per supported stateful layer kind.  Each slot is
/// gated by a corresponding <c>Has*</c> flag set during
/// <see cref="IStatefulLayer.InitializeState"/>; stateful layers read and mutate
/// only their own slot.  Slots not in use by the current stack stay at their
/// <c>default</c> value and cost nothing at runtime.
/// </para>
/// <para>
/// This keeps the type system simple (no parallel state cons-list) while still
/// being fully allocation-free and type-safe at the boundary: only stateful
/// layer types whose slot is defined here can be added to a stack.  Support
/// for additional stateful layer kinds is added by extending this struct with
/// new slots and corresponding <c>Has*</c> flags.
/// </para>
/// <para>
/// Thread safety: not thread-safe.  Each <see cref="Session{TStack,TTrailer,TInterceptor}"/>
/// owns one instance; sessions are not safe to share across threads.
/// </para>
/// </remarks>
public struct SessionState
{
    /// <summary>Next IP Identification value to write for an auto-IPID IPv4 layer.</summary>
    public ushort IPv4NextId
    {
        get; set;
    }

    /// <summary>Whether an <see cref="IPv4LayerWithAutoIpId"/> exists in the stack.</summary>
    public bool HasIPv4AutoId
    {
        get; set;
    }

    /// <summary>
    /// Next TCP sequence number to write for an auto-sequence TCP layer.
    /// Advanced by <c>payload.Length</c> after each frame; for SYN / FIN frames
    /// the layer increments by 1 itself.
    /// </summary>
    public uint TcpNextSeq
    {
        get; set;
    }

    /// <summary>Sticky ACK number written into every frame from an auto-sequence TCP layer.</summary>
    public uint TcpAck
    {
        get; set;
    }

    /// <summary>Whether a <see cref="TcpLayerWithAutoSequence"/> exists in the stack.</summary>
    public bool HasTcpAutoSeq
    {
        get; set;
    }

    /// <summary>
    /// Next IPv6 fragment Identification value (32-bit) to write for an
    /// <see cref="IPv6FragmentExtensionLayerWithAutoId"/>.  Advanced by 1 per
    /// logical packet (not per fragment).
    /// </summary>
    public uint IPv6NextFragId
    {
        get; set;
    }

    /// <summary>Whether an <see cref="IPv6FragmentExtensionLayerWithAutoId"/> exists in the stack.</summary>
    public bool HasIPv6AutoFragId
    {
        get; set;
    }

    /// <summary>
    /// Next SOME/IP session identifier (16-bit) to write for a
    /// <see cref="SomeIpTpLayerWithAutoCounter"/>.
    /// AUTOSAR §4.1.2.5: SessionId 0 is reserved (no session) and must be
    /// skipped on wraparound.
    /// </summary>
    public ushort SomeIpNextSessionId
    {
        get; set;
    }

    /// <summary>Whether a <c>SomeIp*LayerWithAutoCounter</c> exists in the stack.</summary>
    public bool HasSomeIpAutoCounter
    {
        get; set;
    }

    /// <summary>
    /// Payload length of the frame currently being written.  Set by
    /// <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}.MoveNext"/>
    /// before walking the layers so that <see cref="TcpLayerWithAutoSequence"/>
    /// can advance its sequence by the right amount in <c>WriteHeader</c>.
    /// </summary>
    public int CurrentPayloadLength
    {
        get; set;
    }

    // ------------------------------------------------------------------
    // TcpStreamLayer slots — written by TcpConnection BEFORE every
    // NextPacket call, read by TcpStreamLayer.WriteHeader.  All values
    // (flags, window, urgent, ack) vary per emitted segment; only the
    // src/dst port pair is baked into the layer struct itself.  SEQ is
    // self-managed (initial value seeded by InitializeState, advanced
    // per frame by payload + (SYN|FIN ? 1 : 0)).
    // ------------------------------------------------------------------

    /// <summary>Next sequence number for the upcoming TcpStreamLayer frame.</summary>
    public uint TcpStreamNextSeq
    {
        get; set;
    }

    /// <summary>Acknowledgment number to write into the next TcpStreamLayer frame.</summary>
    public uint TcpStreamAck
    {
        get; set;
    }

    /// <summary>TCP control flags to write into the next TcpStreamLayer frame.</summary>
    public byte TcpStreamFlags
    {
        get; set;
    }

    /// <summary>Window-size value to write into the next TcpStreamLayer frame.</summary>
    public ushort TcpStreamWindow
    {
        get; set;
    }

    /// <summary>Urgent-pointer value to write into the next TcpStreamLayer frame.</summary>
    public ushort TcpStreamUrgent
    {
        get; set;
    }

    /// <summary>Whether a <see cref="TcpStreamLayer"/> exists in the stack.</summary>
    public bool HasTcpStream
    {
        get; set;
    }
}

