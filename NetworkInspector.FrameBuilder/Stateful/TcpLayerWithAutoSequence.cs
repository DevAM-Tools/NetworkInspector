// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Stateful TCP layer that auto-advances its sequence number per frame by the
/// emitted payload length (with the conventional +1 increment for SYN and FIN
/// frames).  ACK number is sticky: it stays at whatever value
/// <see cref="Session{TStack,TTrailer,TInterceptor}.UpdateAck"/> last set on the owning session.
/// </summary>
/// <remarks>
/// <para>
/// State slot: <see cref="SessionState.TcpNextSeq"/> and
/// <see cref="SessionState.TcpAck"/>.  Sequence is initialised to the
/// caller-supplied <c>initialSequence</c>; ACK to <c>initialAck</c>.
/// </para>
/// <para>
/// Only usable inside a <see cref="Session{TStack,TTrailer,TInterceptor}"/>.  Direct
/// stateless emission is rejected at compile time.
/// </para>
/// </remarks>
public readonly struct TcpLayerWithAutoSequence :
    IStatefulLayer, IInteriorLayer, IProvidesProtocolType,
    IProvidesNextProtocolValue<IpNextProtocolKind>, IRequiresPseudoHeader, IStreamCarrier
{
    private const int ChecksumOffset = 16;

    private readonly ushort _SrcPort;
    private readonly ushort _DstPort;
    private readonly uint _InitialSequence;
    private readonly uint _InitialAck;
    private readonly byte _Flags;
    private readonly ushort _WindowSize;
    private readonly ushort _UrgentPointer;

    private readonly ushort _ExplicitChecksum;
    private readonly bool _ChecksumIsExplicit;

    /// <summary>Creates a stateful auto-sequence TCP layer.</summary>
    /// <param name="srcPort">Source port.</param>
    /// <param name="dstPort">Destination port.</param>
    /// <param name="initialSequence">Initial sequence number; advanced by payload length per frame.</param>
    /// <param name="initialAck">Initial ACK number; sticky until updated.</param>
    /// <param name="flags">TCP control flags (see <see cref="TcpFlags"/>); SYN / FIN add +1 to the seq advance.</param>
    /// <param name="windowSize">Window size; default 65535.</param>
    /// <param name="urgentPointer">Urgent pointer; default 0.</param>
    /// <param name="checksum">Auto-compute (default) or pinned via <see cref="Auto{T}.Explicit"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TcpLayerWithAutoSequence(
        ushort srcPort,
        ushort dstPort,
        uint initialSequence = 0,
        uint initialAck = 0,
        byte flags = TcpFlags.Ack,
        ushort windowSize = 65535,
        ushort urgentPointer = 0,
        Auto<ushort> checksum = default)
    {
        _SrcPort = srcPort;
        _DstPort = dstPort;
        _InitialSequence = initialSequence;
        _InitialAck = initialAck;
        _Flags = flags;
        _WindowSize = windowSize;
        _UrgentPointer = urgentPointer;
        _ChecksumIsExplicit = checksum.TryGetExplicit(out ushort v);
        _ExplicitChecksum = v;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TcpHeader.Size;
    }

    /// <inheritdoc />
    public ushort ProtocolType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IpProtocols.Tcp;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitializeState(ref SessionState state)
    {
        state.TcpNextSeq = _InitialSequence;
        state.TcpAck = _InitialAck;
        state.HasTcpAutoSeq = true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst, ref SessionState state)
    {
        // Snapshot of the seq/ack to write into THIS frame.
        uint seq = state.TcpNextSeq;
        uint ack = state.TcpAck;

        // Advance the counter for the NEXT frame.  SYN and FIN consume one
        // sequence number even though they carry no payload; payload bytes
        // each consume one sequence number too.
        bool isSynOrFin = (_Flags & (TcpFlags.Syn | TcpFlags.Fin)) != 0;
        uint advance = (uint)state.CurrentPayloadLength + (isSynOrFin ? 1u : 0u);
        unchecked
        {
            state.TcpNextSeq = seq + advance;
        }

        TcpHeader hdr = TcpHeader.Create(
            _SrcPort, _DstPort, seq, ack,
            _Flags, _WindowSize, dataOffsetWords: 5, _UrgentPointer);
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        if (phase != FixPhase.InnerChecksum)
        {
            return;
        }

        if (_ChecksumIsExplicit)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), _ExplicitChecksum);
            return;
        }

        ComputeChecksum(frame, myOffset, myLength, in ctx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeChecksum(Span<byte> frame, int myOffset, int myLength, in PostFixContext ctx)
    {
        frame[myOffset + ChecksumOffset] = 0;
        frame[myOffset + ChecksumOffset + 1] = 0;

        ReadOnlySpan<byte> segment = frame.Slice(myOffset, myLength);
        ReadOnlySpan<byte> srcIp = ctx.PseudoSrcIp[..ctx.PseudoIpLength];
        ReadOnlySpan<byte> dstIp = ctx.PseudoDstIp[..ctx.PseudoIpLength];

        ushort checksum = ctx.PseudoIsIPv6
            ? ChecksumUtils.PseudoHeaderIPv6(srcIp, dstIp, IpProtocols.Tcp, segment)
            : ChecksumUtils.PseudoHeaderIPv4(srcIp, dstIp, IpProtocols.Tcp, segment);

        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(myOffset + ChecksumOffset, 2), checksum);
    }
}

